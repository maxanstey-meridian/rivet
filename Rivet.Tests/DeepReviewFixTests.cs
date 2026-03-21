using System.Text.Json;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Regression tests for bugs identified in the deep review (2026-03-20).
/// Each test targets a specific fix and would have caught the original bug.
/// </summary>
public sealed class DeepReviewFixTests
{
    // ========== Helpers ==========

    private static JsonDocument EmitOpenApi(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        return JsonDocument.Parse(json);
    }

    private static string EmitSchemas(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        return JsonSchemaEmitter.Emit(walker.Definitions, walker.Brands, walker.Enums);
    }

    private static JsonElement ParseDefs(string output)
    {
        var start = output.IndexOf("const $defs = ", StringComparison.Ordinal) + "const $defs = ".Length;
        var end = output.IndexOf(" as const;", start, StringComparison.Ordinal);
        var json = output[start..end];
        return JsonDocument.Parse(json).RootElement;
    }

    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, string Client) GenerateContract(
        string source, ValidateMode validateMode = ValidateMode.None)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        var client = string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap, validateMode)));
        return (endpoints, client);
    }

    private static (string ZodValidators, string ZodClient) GenerateZodContract(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var zodValidators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        var zodClient = string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap, ValidateMode.Zod)));
        return (zodValidators, zodClient);
    }

    private static ImportResult Import(string json, string ns = "Test")
    {
        return OpenApiImporter.Import(json, new ImportOptions(ns));
    }

    private static string FindFile(ImportResult result, string fileName)
    {
        var file = result.Files.FirstOrDefault(f => f.FileName.EndsWith(fileName));
        Assert.NotNull(file);
        return file.Content;
    }

    private static string BuildSpec(string? schemas = null, string? paths = null)
    {
        var schemasBlock = schemas is not null
            ? $"\"components\": {{ \"schemas\": {{ {schemas} }} }},"
            : "";

        var pathsBlock = paths is not null
            ? $"\"paths\": {{ {paths} }}"
            : "\"paths\": {}";

        return $$"""
            {
                "openapi": "3.1.0",
                "info": { "title": "Test", "version": "1.0.0" },
                {{schemasBlock}}
                {{pathsBlock}}
            }
            """;
    }

    // ========== Bug 1: Scalar format metadata ==========

    [Fact]
    public void OpenApi_Guid_Emits_Format_Uuid()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItem =
                    Define.Get<ItemDto>("/api/items/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var idProp = doc.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("ItemDto").GetProperty("properties").GetProperty("id");

        Assert.Equal("string", idProp.GetProperty("type").GetString());
        Assert.Equal("uuid", idProp.GetProperty("format").GetString());
    }

    [Fact]
    public void OpenApi_DateTime_Emits_Format_DateTime()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record EventDto(string Name, DateTimeOffset CreatedAt, DateOnly EventDate);

            [RivetContract]
            public static class EventsContract
            {
                public static readonly Define GetEvent =
                    Define.Get<EventDto>("/api/events/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var props = doc.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("EventDto").GetProperty("properties");

        Assert.Equal("date-time", props.GetProperty("createdAt").GetProperty("format").GetString());
        Assert.Equal("date", props.GetProperty("eventDate").GetProperty("format").GetString());
    }

    [Fact]
    public void OpenApi_Numeric_Types_Emit_Correct_Formats()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PriceDto(int Quantity, long Total, decimal Amount, double Rate, float Score);

            [RivetContract]
            public static class PricesContract
            {
                public static readonly Define GetPrice =
                    Define.Get<PriceDto>("/api/prices/{id}");
            }
            """;

        using var doc = EmitOpenApi(source);
        var props = doc.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("PriceDto").GetProperty("properties");

        Assert.Equal("int32", props.GetProperty("quantity").GetProperty("format").GetString());
        Assert.Equal("int64", props.GetProperty("total").GetProperty("format").GetString());
        Assert.Equal("decimal", props.GetProperty("amount").GetProperty("format").GetString());
        Assert.Equal("double", props.GetProperty("rate").GetProperty("format").GetString());
        Assert.Equal("float", props.GetProperty("score").GetProperty("format").GetString());
    }

    [Fact]
    public void JsonSchema_Guid_Emits_Format_Uuid()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);
            """;

        var output = EmitSchemas(source);
        var defs = ParseDefs(output);
        var idProp = defs.GetProperty("ItemDto").GetProperty("properties").GetProperty("id");

        Assert.Equal("string", idProp.GetProperty("type").GetString());
        Assert.Equal("uuid", idProp.GetProperty("format").GetString());
    }

    // ========== Bug 2: Nullable unknown ==========

    [Fact]
    public void OpenApi_Nullable_Unknown_Does_Not_Emit_Type_Unknown()
    {
        var source = """
            using System.Text.Json;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FlexDto(JsonElement? Payload);

            [RivetContract]
            public static class FlexContract
            {
                public static readonly Define Get =
                    Define.Get<FlexDto>("/api/flex");
            }
            """;

        using var doc = EmitOpenApi(source);
        var payload = doc.RootElement
            .GetProperty("components").GetProperty("schemas")
            .GetProperty("FlexDto").GetProperty("properties").GetProperty("payload");

        // Should be { "nullable": true } (empty schema + nullable), NOT { "type": "unknown", "nullable": true }
        Assert.True(payload.GetProperty("nullable").GetBoolean());
        Assert.False(payload.TryGetProperty("type", out _),
            "Nullable unknown should not emit a 'type' field — 'unknown' is not a valid OpenAPI type");
    }

    // ========== Bug 3: Multipart required array ==========

    [Fact]
    public void OpenApi_Multipart_Includes_Required_Array()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadInput(IFormFile Document, string Title);

            [RivetType]
            public sealed record UploadResult(string Url);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly Define Upload =
                    Define.Post<UploadInput, UploadResult>("/api/files");
            }
            """;

        using var doc = EmitOpenApi(source);
        var multipart = doc.RootElement
            .GetProperty("paths").GetProperty("/api/files")
            .GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").GetProperty("multipart/form-data")
            .GetProperty("schema");

        Assert.True(multipart.TryGetProperty("required", out var required),
            "Multipart schema should include a 'required' array");

        var requiredFields = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("document", requiredFields);
        Assert.Contains("title", requiredFields);
    }

    // ========== Bug 4: Duplicate endpoint warning ==========
    // (Warning is emitted to stderr — hard to assert directly, but we can verify last-writer-wins)

    // ========== Bug 5: OpenAPI version docs ==========
    // (Text change only — verified by inspection)

    // ========== Bug 6: ZodValidatorEmitter Dictionary ==========

    [Fact]
    public void Zod_Dictionary_Value_TypeRef_Imports_Schema()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items")]
                public static Task<Dictionary<string, ItemDto>> ListItems()
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = EndpointWalker.Walk(walker, discovered.EndpointMethods, discovered.ClientTypes);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var zodValidators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);

        // ItemDtoSchema should be imported because it's the value type of the Dictionary
        Assert.Contains("ItemDtoSchema", zodValidators);
    }

    // ========== Bug 7: ContractWalker [JsonPropertyName] / [JsonIgnore] ==========

    [Fact]
    public void ContractWalker_Respects_JsonPropertyName_On_QueryParam()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SearchInput(
                [property: JsonPropertyName("q")] string Query,
                int Limit);

            [RivetType]
            public sealed record ResultDto(string Id);

            [RivetContract]
            public static class SearchContract
            {
                public static readonly Define Search =
                    Define.Get<SearchInput, ResultDto>("/api/search");
            }
            """;

        var (endpoints, _) = GenerateContract(source);
        var search = Assert.Single(endpoints);

        var queryParam = Assert.Single(search.Params, p => p.Name == "q");
        Assert.Equal(ParamSource.Query, queryParam.Source);
    }

    [Fact]
    public void ContractWalker_Skips_JsonIgnore_On_InputProperty()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SearchInput(
                string Query,
                [property: JsonIgnore] string InternalToken);

            [RivetType]
            public sealed record ResultDto(string Id);

            [RivetContract]
            public static class SearchContract
            {
                public static readonly Define Search =
                    Define.Get<SearchInput, ResultDto>("/api/search");
            }
            """;

        var (endpoints, _) = GenerateContract(source);
        var search = Assert.Single(endpoints);

        // Only 'query' should be emitted; 'internalToken' is ignored
        Assert.Single(search.Params);
        Assert.Equal("query", search.Params[0].Name);
    }

    // ========== Bug 8: POST route params typed from TInput ==========

    [Fact]
    public void Post_Route_Param_Uses_TInput_Property_Type()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UpdateInput(int Id, string Title);

            [RivetType]
            public sealed record ItemDto(int Id, string Title);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define UpdateItem =
                    Define.Post<UpdateInput, ItemDto>("/api/items/{id}");
            }
            """;

        var (endpoints, _) = GenerateContract(source);
        var update = Assert.Single(endpoints);

        var routeParam = Assert.Single(update.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", routeParam.Name);
        // Should be number (from int Id), not string
        Assert.IsType<TsType.Primitive>(routeParam.Type);
        Assert.Equal("number", ((TsType.Primitive)routeParam.Type).Name);
    }

    // ========== Bug 9: Validated clients for void endpoints with error responses ==========

    [Fact]
    public void Void_Endpoint_With_Error_Response_Gets_Validation()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define DeleteItem =
                    Define.Delete("/api/items/{id}")
                        .Returns<NotFoundDto>(404, "Not found");
            }
            """;

        var (_, client) = GenerateContract(source, ValidateMode.Zod);

        // Should emit a result DU type
        Assert.Contains("export type DeleteItemResult =", client);
        Assert.Contains("{ status: 200; data: void; response: Response }", client);
        Assert.Contains("{ status: 404; data: NotFoundDto; response: Response }", client);

        // Should be async (needs await for validation)
        Assert.Contains("export async function deleteItem", client);

        // Should validate error response in unwrap: false branch
        Assert.Contains("assertNotFoundDto", client);
        Assert.Contains("if (result.status === 404)", client);
    }

    [Fact]
    public void Void_Endpoint_Without_Errors_Has_No_Result_DU()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define DeleteItem =
                    Define.Delete("/api/items/{id}");
            }
            """;

        var (endpoints, client) = GenerateContract(source);
        var ep = Assert.Single(endpoints);

        // No typed responses, no result DU
        Assert.DoesNotContain("DeleteItemResult", client);
        Assert.Contains("RivetResult<void>", client);
    }

    // ========== Bug 10: Param-only POST import ==========

    [Fact]
    public void ParamOnly_Post_Import_Does_Not_Wire_Body()
    {
        var spec = BuildSpec(
            paths: """
                "/api/items/{id}/archive": {
                    "post": {
                        "operationId": "items_archiveItem",
                        "tags": ["Items"],
                        "parameters": [
                            {
                                "name": "id",
                                "in": "path",
                                "required": true,
                                "schema": { "type": "string" }
                            }
                        ],
                        "responses": {
                            "200": { "description": "Success" }
                        }
                    }
                }
                """);

        var result = Import(spec);
        var content = FindFile(result, "ItemsContract.cs");

        // Should NOT wire the input as .Accepts<T>() or as a type arg
        Assert.DoesNotContain("Accepts<", content);
        Assert.DoesNotContain("RouteDefinition<ArchiveItemInput", content);
        // Should just be a plain RouteDefinition (no type args for body)
        Assert.Contains("Define.Post(\"/api/items/{id}/archive\")", content);
    }

    // ========== Bug 11: Unmatched route placeholders ==========

    [Fact]
    public void Unmatched_Route_Placeholder_Emits_Comment_Marker()
    {
        // Build endpoints directly to create a truly unmatched placeholder
        // (a route with {id} but no Route-sourced param named "id")
        var endpoints = new List<TsEndpointDefinition>
        {
            new("getItem", "GET", "/api/items/{id}",
                new List<TsEndpointParam>
                {
                    // Query param, not Route — so {id} in the template is unmatched
                    new("filter", new TsType.Primitive("string"), ParamSource.Query),
                },
                new TsType.Primitive("string"),
                "items",
                new List<TsResponseType> { new(200, new TsType.Primitive("string")) }),
        };

        var client = ClientEmitter.EmitControllerClient(
            "items", endpoints, new Dictionary<string, string>());

        // The unmatched {id} should be replaced with a valid JS expression + comment
        Assert.Contains("unmatched: id", client);
        Assert.DoesNotContain("/{id}`", client);
    }

    // ========== Deep Review Fix #2 (2026-03-21) ==========

    [Theory]
    [InlineData("long", "long")]
    [InlineData("double", "double")]
    [InlineData("float", "float")]
    [InlineData("decimal", "decimal")]
    [InlineData("Guid", "Guid")]
    [InlineData("DateTime", "DateTime")]
    [InlineData("DateOnly", "DateOnly")]
    public void GetCSharpTypeName_EmitsCorrectGenericArg(string csharpType, string expectedArg)
    {
        // BUG-1: GetCSharpTypeName mapped all numeric primitives to "int"
        // regardless of Format. PagedResult<long> emitted args: {"T": "int"}.
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Wrapper<T>(List<T> Items, int Total);

            [RivetContract]
            public static class WrapperContract
            {
                public static readonly Define Get =
                    Define.Get<Wrapper<{{csharpType}}>>("/api/wrapped");
            }
            """;

        var doc = EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // GetNameSuffix uses p.Name capitalised (e.g. "Number", "String")
        // All numeric types share "Number", all string types share "String"
        var suffix = csharpType switch
        {
            "long" or "double" or "float" or "decimal" => "Number",
            "Guid" or "DateTime" or "DateOnly" => "String",
            _ => throw new ArgumentException(csharpType),
        };

        var schema = schemas.GetProperty($"Wrapper_{suffix}");
        var args = schema.GetProperty("x-rivet-generic").GetProperty("args");
        var tArg = args.GetProperty("T").GetString();
        Assert.Equal(expectedArg, tArg);
    }

    [Fact]
    public void SmallIntegers_EmitAsInteger_NotNumber()
    {
        // BUG-2: short/byte/sbyte/ushort emitted type: "number" instead of "integer"
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SensorDto(short Temp, ushort Voltage, byte Channel, sbyte Offset);

            [RivetContract]
            public static class SensorContract
            {
                public static readonly Define Get =
                    Define.Get<SensorDto>("/api/sensor");
            }
            """;

        var doc = EmitOpenApi(source);
        var schema = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("SensorDto");
        var props = schema.GetProperty("properties");

        Assert.Equal("integer", props.GetProperty("temp").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("voltage").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("channel").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("offset").GetProperty("type").GetString());
    }

    [Fact]
    public void SmallIntegers_JsonSchema_EmitAsInteger()
    {
        // Same check for JsonSchemaEmitter
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SensorDto(short Temp, byte Channel);
            """;

        var output = EmitSchemas(source);
        var defs = ParseDefs(output);
        var props = defs.GetProperty("SensorDto").GetProperty("properties");

        Assert.Equal("integer", props.GetProperty("temp").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("channel").GetProperty("type").GetString());
    }

    [Fact]
    public void NonDU_Endpoint_Validation_NoStatusBranching()
    {
        // TYPE-1: hasTypedErrorResponses included success responses, causing
        // unnecessary status-code branching for simple validated endpoints.
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        var (_, client) = GenerateContract(source, ValidateMode.Zod);

        // Non-DU endpoint should use simple validation (no status branching)
        Assert.Contains("result.data = assertTaskDto(result.data)", client);
        // Should NOT have status-code branching for a simple endpoint
        Assert.DoesNotContain("result.status ===", client);
    }

    [Fact]
    public void DU_Endpoint_Validation_HasStatusBranching()
    {
        // Confirm DU endpoints still get proper status-code branching
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<NotFoundDto>(404);
            }
            """;

        var (_, client) = GenerateContract(source, ValidateMode.Zod);

        // DU endpoint should have status-code branching
        Assert.Contains("result.status === 404", client);
        Assert.Contains("assertNotFoundDto", client);
        Assert.Contains("assertTaskDto", client);
    }
}
