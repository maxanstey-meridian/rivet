using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class ValidatorEmitterTests
{
    private static (string Validators, string Client, string ValidatedClient) Generate(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var endpoints = EndpointWalker.Walk(compilation, walker);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var validators = ValidatorEmitter.Emit(endpoints, typeFileMap);
        var client = ClientEmitter.EmitControllerClient("endpoints", endpoints, typeFileMap);
        var validatedClient = ClientEmitter.EmitControllerClient("endpoints", endpoints, typeFileMap, validated: true);
        return (validators, client, validatedClient);
    }

    [Fact]
    public void ValidatorEmitter_EmitsCreateAssert()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record MessageDto(Guid Id, string Body);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpPost("/api/messages")]
                public static Task<MessageDto> CreateMessage(
                    [FromBody] MessageDto body)
                    => throw new NotImplementedException();
            }
            """;

        var (validators, _, _) = Generate(source);

        Assert.Contains("import typia from \"typia\";", validators);
        Assert.Contains("MessageDto", validators);
        Assert.Contains("from \"./types/", validators);
        Assert.DoesNotContain("from \"./types/index.js\"", validators);
        Assert.Contains("export const assertMessageDto = typia.createAssert<MessageDto>();", validators);
    }

    [Fact]
    public void ValidatorEmitter_MultipleReturnTypes()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record MessageDto(Guid Id, string Body);
            [RivetType]
            public sealed record UserDto(Guid Id, string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/messages/{id}")]
                public static Task<MessageDto> GetMessage([FromRoute] Guid id)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpGet("/api/users/{id}")]
                public static Task<UserDto> GetUser([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var (validators, _, _) = Generate(source);

        Assert.Contains("export const assertMessageDto = typia.createAssert<MessageDto>();", validators);
        Assert.Contains("export const assertUserDto = typia.createAssert<UserDto>();", validators);
    }

    [Fact]
    public void ValidatorEmitter_VoidReturn_NoAssert()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpDelete("/api/things/{id}")]
                public static Task Delete([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var (validators, _, _) = Generate(source);

        // No return type → no validators to emit
        Assert.Equal("", validators);
    }

    [Fact]
    public void ValidatedClient_WrapsWithAssert()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record MessageDto(Guid Id, string Body);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/messages/{id}")]
                public static Task<MessageDto> GetMessage([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client, validatedClient) = Generate(source);

        // Unvalidated client: overloaded function, no assert
        Assert.Contains("export function getMessage(id: string): Promise<MessageDto>;", client);
        Assert.DoesNotContain("assertMessageDto", client);

        // Validated client: async + assert wrapper with unwrap branching
        Assert.Contains("""import { assertMessageDto } from "../build/validators.js";""", validatedClient);
        Assert.Contains("export function getMessage(id: string): Promise<MessageDto>;", validatedClient);
        Assert.Contains("export async function getMessage(id: string, opts?: { unwrap?: boolean })", validatedClient);
        Assert.Contains("if (opts?.unwrap === false) return rivetFetch(", validatedClient);
        Assert.Contains("const data = await rivetFetch<MessageDto>", validatedClient);
        Assert.Contains("return assertMessageDto(data);", validatedClient);
    }

    [Fact]
    public void ValidatedClient_VoidReturn_NoAssert()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpDelete("/api/things/{id}")]
                public static Task Delete([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var (_, _, validatedClient) = Generate(source);

        // Void returns don't get assert wrappers — plain function with overloads
        Assert.Contains("export function remove(id: string): Promise<void>;", validatedClient);
        Assert.DoesNotContain("assert", validatedClient);
    }

    [Fact]
    public void ValidatedClient_MixedEndpoints()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);
            [RivetType]
            public sealed record CreateItemCommand(string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<ItemDto> GetItem([FromRoute] Guid id)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpPost("/api/items")]
                public static Task<ItemDto> CreateItem([FromBody] CreateItemCommand body)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpDelete("/api/items/{id}")]
                public static Task DeleteItem([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var (validators, _, validatedClient) = Generate(source);

        // Only one assert for ItemDto (shared return type)
        Assert.Contains("export const assertItemDto = typia.createAssert<ItemDto>();", validators);
        Assert.DoesNotContain("assertCreateItemCommand", validators);

        // GET and POST get assert wrappers, DELETE doesn't
        Assert.Contains("export async function getItem", validatedClient);
        Assert.Contains("export async function createItem", validatedClient);
        Assert.Contains("export function deleteItem(id: string): Promise<void>;", validatedClient);
    }

    [Fact]
    public void ValidatorEmitter_GenericReturnTypes_DistinctAssertNames()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskListItemDto(Guid Id, string Title);
            [RivetType]
            public sealed record MemberDto(Guid Id, string Name);
            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/tasks")]
                public static Task<PagedResult<TaskListItemDto>> ListTasks()
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpGet("/api/members")]
                public static Task<PagedResult<MemberDto>> ListMembers()
                    => throw new NotImplementedException();
            }
            """;

        var (validators, _, _) = Generate(source);

        // Distinct assert names for different generic specializations
        Assert.Contains("export const assertPagedResultTaskListItemDto = typia.createAssert<PagedResult<TaskListItemDto>>();", validators);
        Assert.Contains("export const assertPagedResultMemberDto = typia.createAssert<PagedResult<MemberDto>>();", validators);

        // All nested type refs are imported
        Assert.Contains("PagedResult", validators);
        Assert.Contains("TaskListItemDto", validators);
        Assert.Contains("MemberDto", validators);
    }
}
