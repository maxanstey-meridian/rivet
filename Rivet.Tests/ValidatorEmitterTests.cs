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
        var validators = ValidatorEmitter.Emit(endpoints);
        var client = ClientEmitter.Emit(endpoints, definitions);
        var validatedClient = ClientEmitter.Emit(endpoints, definitions, validated: true);
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
        Assert.Contains("""import type { MessageDto } from "./types.js";""", validators);
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

        // Unvalidated client: arrow function, no assert
        Assert.Contains("export const getMessage = (id: string): Promise<MessageDto> =>", client);
        Assert.DoesNotContain("assertMessageDto", client);

        // Validated client: async + assert wrapper
        Assert.Contains("""import { assertMessageDto } from "./build/validators.js";""", validatedClient);
        Assert.Contains("export const getMessage = async (id: string): Promise<MessageDto> => {", validatedClient);
        Assert.Contains("const raw = await rivetFetch<MessageDto>", validatedClient);
        Assert.Contains("return assertMessageDto(raw);", validatedClient);
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

        // Void returns don't get assert wrappers
        Assert.Contains("export const delete = (id: string): Promise<void> =>", validatedClient);
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
        Assert.Contains("export const getItem = async", validatedClient);
        Assert.Contains("export const createItem = async", validatedClient);
        Assert.Contains("export const deleteItem = (id: string): Promise<void> =>", validatedClient);
    }
}
