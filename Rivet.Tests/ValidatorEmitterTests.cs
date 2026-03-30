using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ValidatorEmitterTests
{
    private static (string Validators, string Client, string ValidatedClient) Generate(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();

        // Extraction pass — matches EmitPipeline.RunAsync wiring
        var extraction = InlineTypeExtractor.Extract(endpoints, definitions);
        var allDefinitions = definitions.Concat(extraction.ExtractedTypes).ToList();
        var allNamespaces = new Dictionary<string, string?>(walker.TypeNamespaces);
        foreach (var (name, ns) in extraction.TypeNamespaces)
            allNamespaces[name] = ns;

        var typeGrouping = TypeGrouper.Group(allDefinitions, walker.Brands.Values.ToList(), walker.Enums, allNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var validators = ZodValidatorEmitter.Emit(extraction.Endpoints, typeFileMap);
        var client = ClientEmitter.EmitControllerClient("endpoints", extraction.Endpoints, typeFileMap);
        var validatedClient = ClientEmitter.EmitControllerClient("endpoints", extraction.Endpoints, typeFileMap, ValidateMode.Zod);
        return (validators, client, validatedClient);
    }

    private static (string ZodValidators, string ZodClient) GenerateZod(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();

        // Extraction pass — matches EmitPipeline.RunAsync wiring
        var extraction = InlineTypeExtractor.Extract(endpoints, definitions);
        var allDefinitions = definitions.Concat(extraction.ExtractedTypes).ToList();
        var allNamespaces = new Dictionary<string, string?>(walker.TypeNamespaces);
        foreach (var (name, ns) in extraction.TypeNamespaces)
            allNamespaces[name] = ns;

        var typeGrouping = TypeGrouper.Group(allDefinitions, walker.Brands.Values.ToList(), walker.Enums, allNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var zodValidators = ZodValidatorEmitter.Emit(extraction.Endpoints, typeFileMap);
        var zodClient = ClientEmitter.EmitControllerClient("endpoints", extraction.Endpoints, typeFileMap, ValidateMode.Zod);
        return (zodValidators, zodClient);
    }

    [Fact]
    public void ValidatorEmitter_EmitsZodAssert()
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

        Assert.Contains("import { fromJSONSchema, z } from \"zod\";", validators);
        Assert.Contains("MessageDto", validators);
        Assert.Contains("from \"./schemas.js\";", validators);
        Assert.Contains("export const assertMessageDto = (data: unknown): MessageDto =>", validators);
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

        Assert.Contains("assertMessageDto", validators);
        Assert.Contains("assertUserDto", validators);
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
        Assert.Contains("""import { assertMessageDto } from "../validators.js";""", validatedClient);
        Assert.Contains("export function getMessage(id: string): Promise<MessageDto>;", validatedClient);
        Assert.Contains("export async function getMessage(id: string, opts?: { unwrap?: boolean })", validatedClient);
        // unwrap: false validates the result too
        Assert.Contains("if (opts?.unwrap === false) {", validatedClient);
        Assert.Contains("assertMessageDto(result.data)", validatedClient);
        // default path validates directly
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
        Assert.Contains("assertItemDto", validators);
        Assert.DoesNotContain("assertCreateItemCommand", validators);

        // GET and POST get assert wrappers, DELETE doesn't
        Assert.Contains("export async function getItem", validatedClient);
        Assert.Contains("export async function createItem", validatedClient);
        Assert.Contains("export function deleteItem(id: string): Promise<void>;", validatedClient);
    }

    [Fact]
    public void ValidatedClient_MultiResponse_ValidatesEachBranch()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDetailDto(Guid Id, string Title);
            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetClient]
            [Route("api/tasks")]
            public sealed class TasksController
            {
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(TaskDetailDto), 200)]
                [ProducesResponseType(typeof(NotFoundDto), 404)]
                public Task<IActionResult> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var (validators, _, validatedClient) = Generate(source);

        // Validators emitted for both response types
        Assert.Contains("assertTaskDetailDto", validators);
        Assert.Contains("assertNotFoundDto", validators);

        // Validated client imports both asserters
        Assert.Contains("""import { assertNotFoundDto, assertTaskDetailDto } from "../validators.js";""", validatedClient);

        // Validated client validates each branch by status code
        Assert.Contains("if (result.status === 200) return { ...result, data: assertTaskDetailDto(result.data) };", validatedClient);
        Assert.Contains("else if (result.status === 404) return { ...result, data: assertNotFoundDto(result.data) };", validatedClient);
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

        // Distinct assert names for different generic specializations (underscore separator)
        Assert.Contains("assertPagedResult_TaskListItemDto", validators);
        Assert.Contains("assertPagedResult_MemberDto", validators);

        // All nested type refs are imported
        Assert.Contains("PagedResult", validators);
        Assert.Contains("TaskListItemDto", validators);
        Assert.Contains("MemberDto", validators);
    }

    [Fact]
    public void ZodValidatorEmitter_EmitsFromJSONSchema()
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

        var (zodValidators, _) = GenerateZod(source);

        Assert.Contains("import { fromJSONSchema, z } from \"zod\";", zodValidators);
        Assert.Contains("import { MessageDtoSchema } from \"./schemas.js\";", zodValidators);
        Assert.Contains("const _assertMessageDto = fromJSONSchema(MessageDtoSchema);", zodValidators);
        Assert.Contains("export const assertMessageDto = (data: unknown): MessageDto => _assertMessageDto.parse(data) as MessageDto;", zodValidators);
    }

    [Fact]
    public void ZodClient_ImportsFromValidatorsJs()
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

        var (_, zodClient) = GenerateZod(source);

        // Zod client imports from validators.js (not build/validators.js)
        Assert.Contains("""import { assertMessageDto } from "../validators.js";""", zodClient);
        Assert.DoesNotContain("build/validators.js", zodClient);

        // Same assertion wiring as typia
        Assert.Contains("return assertMessageDto(data);", zodClient);
    }

    [Fact]
    public void ZodValidatorEmitter_VoidReturn_Empty()
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

        var (zodValidators, _) = GenerateZod(source);

        Assert.Empty(zodValidators);
    }

    [Fact]
    public void NullableReturnType_GetsDistinctAssertName()
    {
        // assertTaskDtoNullable should be distinct from assertTaskDto
        var type = new TsType.TypeRef("TaskDto");
        var nullableType = new TsType.Nullable(type);

        var assertName = ZodValidatorEmitter.GetAssertName(type);
        var nullableAssertName = ZodValidatorEmitter.GetAssertName(nullableType);

        Assert.Equal("assertTaskDto", assertName);
        Assert.Equal("assertTaskDtoNullable", nullableAssertName);
        Assert.NotEqual(assertName, nullableAssertName);
    }

    [Fact]
    public void GetAssertName_InlineObject_UsesFieldNames()
    {
        var inlineObj = new TsType.InlineObject([
            ("key", new TsType.Primitive("string")),
            ("value", new TsType.Primitive("number")),
        ]);

        var assertName = ZodValidatorEmitter.GetAssertName(inlineObj);
        Assert.Equal("assertKeyValue", assertName);
    }

    [Fact]
    public void GetAssertName_InlineObject_ManyFields_UsesObject()
    {
        var inlineObj = new TsType.InlineObject([
            ("a", new TsType.Primitive("string")),
            ("b", new TsType.Primitive("string")),
            ("c", new TsType.Primitive("string")),
            ("d", new TsType.Primitive("string")),
        ]);

        var assertName = ZodValidatorEmitter.GetAssertName(inlineObj);
        Assert.Equal("assertObject", assertName);
    }

    [Fact]
    public void GetAssertName_StringUnion_FewMembers_UsesNames()
    {
        var union = new TsType.StringUnion(["Active", "Inactive"]);

        var assertName = ZodValidatorEmitter.GetAssertName(union);
        Assert.Equal("assertActiveInactive", assertName);
    }

    [Fact]
    public void GetAssertName_StringUnion_ManyMembers_UsesUnion()
    {
        var union = new TsType.StringUnion(["A", "B", "C", "D"]);

        var assertName = ZodValidatorEmitter.GetAssertName(union);
        Assert.Equal("assertEnum", assertName);
    }

    [Fact]
    public void GetAssertName_Generic_UsesUnderscoreSeparator()
    {
        var generic = new TsType.Generic("PagedResult", [new TsType.TypeRef("TaskDto")]);

        var assertName = ZodValidatorEmitter.GetAssertName(generic);
        Assert.Equal("assertPagedResult_TaskDto", assertName);
    }

    [Fact]
    public void ZodValidatorEmitter_Dictionary_StringUnion_InlineObject()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record MetadataDto(Dictionary<string, string> Tags);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/metadata")]
                public static Task<MetadataDto> GetMetadata()
                    => throw new NotImplementedException();
            }
            """;

        var (zodValidators, _) = GenerateZod(source);

        // Should emit without error — Dictionary gets z.record()
        Assert.Contains("fromJSONSchema(MetadataDtoSchema)", zodValidators);
    }

    [Fact]
    public void ExtractedInlineType_ZodValidator_ReferencesSchema()
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
                [HttpGet("/api/orders/{id}")]
                public static Task<(Guid Id, string Name, decimal Price)> GetOrder(
                    [FromRoute] Guid id)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpGet("/api/orders")]
                public static Task<(Guid Id, string Name, decimal Price)[]> ListOrders()
                    => throw new NotImplementedException();
            }
            """;

        var (zodValidators, _) = GenerateZod(source);

        // The extracted type's schema should be imported and used
        Assert.Contains("EndpointGetOrderDtoSchema", zodValidators);
        Assert.Contains("assertEndpointGetOrderDto", zodValidators);
        Assert.Contains("EndpointGetOrderDto", zodValidators);
    }
}
