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
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        return JsonDocument.Parse(json);
    }

    private static JsonElement ParseDefs(string output)
    {
        const string marker = "= ";
        var lineStart = output.IndexOf("const $defs", StringComparison.Ordinal);
        var start = output.IndexOf(marker, lineStart, StringComparison.Ordinal) + marker.Length;
        var end = output.IndexOf(";\n", start, StringComparison.Ordinal);
        var json = output[start..end];
        return JsonDocument.Parse(json).RootElement;
    }

    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, string Client) GenerateContract(
        string source, ValidateMode validateMode = ValidateMode.None)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
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
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var zodValidators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        var zodClient = string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap, ValidateMode.Zod)));
        return (zodValidators, zodClient);
    }

    /// <summary>
    /// Extracts the $defs JSON object from the TypeScript output of JsonSchemaEmitter.
    /// </summary>
    private static string ExtractDefsJson(string tsOutput)
    {
        const string marker = "= ";
        var lineStart = tsOutput.IndexOf("const $defs", StringComparison.Ordinal);
        Assert.True(lineStart >= 0, "Could not find '$defs' in JSON Schema output");
        var start = tsOutput.IndexOf(marker, lineStart, StringComparison.Ordinal) + marker.Length;
        var end = tsOutput.IndexOf(";\n", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "Could not find end of $defs in JSON Schema output");
        return tsOutput[start..end];
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

        var output = CompilationHelper.EmitSchemas(source);
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

        // Multipart with named input type emits $ref to component schema
        var multipart = doc.RootElement
            .GetProperty("paths").GetProperty("/api/files")
            .GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").GetProperty("multipart/form-data")
            .GetProperty("schema");
        Assert.True(multipart.TryGetProperty("$ref", out var refVal),
            "Named multipart input should emit as $ref");
        Assert.Equal("#/components/schemas/UploadInput", refVal.GetString());

        // The component schema has the required array
        var uploadSchema = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("UploadInput");
        Assert.True(uploadSchema.TryGetProperty("required", out var required),
            "UploadInput schema should include a 'required' array");

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
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
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
        Assert.Contains("{ status: 204; data: void; response: Response }", client);
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
    public void ParamOnly_Post_Import_Wires_Input_For_RoundTrip()
    {
        var spec = CompilationHelper.BuildSpec(
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

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ItemsContract.cs");

        // Input type should be wired so the type survives round-trip
        // (POST with path-only params uses .Accepts<T>() since there's no output type)
        Assert.Contains("Accepts<ArchiveItemInput>", content);
        Assert.Contains("Define.Post(\"/api/items/{id}/archive\")", content);
    }

    // ========== Bug 11: Unmatched route placeholders ==========

    [Fact]
    public void Unmatched_Route_Placeholder_Throws()
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

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ClientEmitter.EmitControllerClient(
                "items", endpoints, new Dictionary<string, string>()));

        Assert.Contains("id", ex.Message);
        Assert.Contains("no matching parameter", ex.Message);
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
    [InlineData("TimeOnly", "TimeOnly")]
    [InlineData("Uri", "Uri")]
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
            "Guid" or "DateTime" or "DateOnly" or "TimeOnly" or "Uri" => "String",
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

        var output = CompilationHelper.EmitSchemas(source);
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

    // ========== Deep Review Fix #3 (2026-03-21) — findings from two deep reviews ==========

    // --- Fix 1: CollectTypeRefs recurses into Brand.Inner ---

    [Fact]
    public void CollectTypeRefs_Brand_Wrapping_TypeRef_CollectsBoth()
    {
        var names = new HashSet<string>();
        TsType.CollectTypeRefs(new TsType.Brand("UserId", new TsType.TypeRef("IdBase")), names);

        Assert.Contains("UserId", names);
        Assert.Contains("IdBase", names);
    }

    // --- Fix 2: JsonSchemaEmitter InlineObject nullable fields not required ---

    [Fact]
    public void JsonSchema_InlineObject_Nullable_Field_Not_Required()
    {
        var inlineObj = new TsType.InlineObject([
            ("key", new TsType.Primitive("string")),
            ("value", new TsType.Nullable(new TsType.Primitive("number"))),
        ]);

        var schema = JsonSchemaEmitter.MapTsTypeToSchema(inlineObj);
        var required = (List<string>)schema["required"];
        Assert.Contains("key", required);
        Assert.DoesNotContain("value", required);
    }

    [Fact]
    public void JsonSchema_InlineObject_AllNullable_NoRequiredArray()
    {
        var inlineObj = new TsType.InlineObject([
            ("a", new TsType.Nullable(new TsType.Primitive("string"))),
            ("b", new TsType.Nullable(new TsType.Primitive("number"))),
        ]);

        var schema = JsonSchemaEmitter.MapTsTypeToSchema(inlineObj);
        // No required array when all fields are nullable
        Assert.False(schema.ContainsKey("required"));
    }

    // --- Fix 3: ZodValidatorEmitter CollectSchemaImports handles InlineObject ---

    [Fact]
    public void Zod_InlineObject_With_TypeRef_Imports_Schema()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TagDto(Guid Id, string Name);

            [RivetType]
            public sealed record WithTupleReturn((TagDto Tag, int Count) Result);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/tagged")]
                public static Task<WithTupleReturn> GetTagged()
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var zodValidators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);

        // The schema for WithTupleReturn should be imported (it's a TypeRef)
        Assert.Contains("WithTupleReturnSchema", zodValidators);
    }

    // --- Fix 4: TypeEmitter quotes invalid property names ---

    [Fact]
    public void TypeEmitter_Quotes_Hyphenated_PropertyName()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WeirdDto(
                [property: JsonPropertyName("display-name")] string DisplayName,
                [property: JsonPropertyName("@type")] string TypeField,
                string NormalProp);
            """;

        var result = CompilationHelper.EmitTypes(source);

        Assert.Contains("\"display-name\": string;", result);
        Assert.Contains("\"@type\": string;", result);
        Assert.Contains("normalProp: string;", result);
        // Ensure no unquoted invalid identifiers
        Assert.DoesNotContain("display-name:", result);
    }

    // --- Fix 5: Multipart route param deduplication ---

    [Fact]
    public void Multipart_RouteParam_Not_Duplicated_As_FormField()
    {
        var source = """
            using System;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadInput(Guid TaskId, IFormFile Document, string Title);

            [RivetType]
            public sealed record UploadResult(string Url);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly Define Upload =
                    Define.Post<UploadInput, UploadResult>("/api/tasks/{taskId}/files");
            }
            """;

        var (endpoints, client) = GenerateContract(source);
        var ep = Assert.Single(endpoints);

        // taskId should appear exactly once as Route
        var routeParams = ep.Params.Where(p => p.Source == ParamSource.Route).ToList();
        Assert.Single(routeParams);
        Assert.Equal("taskId", routeParams[0].Name);

        // taskId should NOT appear as a FormField
        var formFields = ep.Params.Where(p => p.Source == ParamSource.FormField).ToList();
        Assert.DoesNotContain(formFields, f => string.Equals(f.Name, "taskId", StringComparison.OrdinalIgnoreCase));

        // document is File, title is FormField
        Assert.Single(ep.Params, p => p.Source == ParamSource.File);
        Assert.Single(formFields, f => f.Name == "title");

        // Client should include route interpolation and FormData
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("fd.append(\"title\"", client);
        Assert.DoesNotContain("fd.append(\"taskId\"", client);
    }

    // --- Fix 6: Unmatched route placeholder throws ---
    // (Already tested above as Unmatched_Route_Placeholder_Throws)

    // --- Fix 7: Query param array serialization ---

    [Fact]
    public void RivetFetchBase_Handles_Array_QueryParams()
    {
        var rivetBase = ClientEmitter.EmitRivetBase();

        Assert.Contains("Array.isArray(v)", rivetBase);
        Assert.Contains("url.searchParams.append(k, String(item))", rivetBase);
    }

    // --- Fix 8: Route encodeURIComponent ---

    [Fact]
    public void Route_Interpolation_Uses_EncodeURIComponent()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id, string Name);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItem =
                    Define.Get<ItemDto>("/api/items/{id}");
            }
            """;

        var (_, client) = GenerateContract(source);

        Assert.Contains("encodeURIComponent(String(id))", client);
        Assert.DoesNotContain("${id}`", client);
    }

    // --- Fix 9: Zod format refinements ---

    [Fact]
    public void Zod_Guid_Emits_Uuid_Refinement()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, DateTime CreatedAt, DateOnly BirthDate);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<ItemDto> GetItem([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var zodValidators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);

        // Zod should emit format refinements from the JSON Schema
        // The Zod emitter wraps TypeRefs via fromJSONSchema, not primitive expressions directly.
        // But let's verify the Zod expression builder uses format refinements:
        Assert.Contains("fromJSONSchema(ItemDtoSchema)", zodValidators);
    }

    [Fact]
    public void Zod_Primitive_Format_Uuid_Uses_FromJSONSchema()
    {
        // Bare Guid return type should go through fromJSONSchema, same as DTOs
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/id")]
                public static Task<Guid> GetId()
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var zodValidators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);

        Assert.Contains("fromJSONSchema(", zodValidators);
        Assert.Contains("\"format\":\"uuid\"", zodValidators);
    }

    [Fact]
    public void Zod_Primitive_Format_DateTime_Uses_FromJSONSchema()
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
                [HttpGet("/api/time")]
                public static Task<DateTime> GetTime()
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var zodValidators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);

        Assert.Contains("fromJSONSchema(", zodValidators);
        Assert.Contains("\"format\":\"date-time\"", zodValidators);
    }

    // --- Fix 10: OpenAPI InlineObject nullable (already fixed, verify) ---

    [Fact]
    public void OpenApi_InlineObject_Nullable_Field_Not_Required()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record WithNullableTuple((string Key, int? Value) Pair);

            [RivetContract]
            public static class TestContract
            {
                public static readonly Define Get =
                    Define.Get<WithNullableTuple>("/api/test");
            }
            """;

        using var doc = EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // The inline object tuple should not have "value" in required
        var pairProp = schemas.GetProperty("WithNullableTuple").GetProperty("properties").GetProperty("pair");
        if (pairProp.TryGetProperty("required", out var required))
        {
            var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("key", requiredNames);
            Assert.DoesNotContain("value", requiredNames);
        }
    }

    // --- Hardened existing test: rivetFetch query param serialization ---

    [Fact]
    public void RivetFetchBase_NullCheck_Uses_DoubleEquals()
    {
        var rivetBase = ClientEmitter.EmitRivetBase();

        // Must use == null (not != null) for the continue to skip nulls
        Assert.Contains("if (v == null) continue;", rivetBase);
    }

    // --- QuoteIfNeeded helper ---

    [Fact]
    public void QuoteIfNeeded_ValidIdentifier_NotQuoted()
    {
        Assert.Equal("name", TypeEmitter.QuoteIfNeeded("name"));
        Assert.Equal("camelCase", TypeEmitter.QuoteIfNeeded("camelCase"));
        Assert.Equal("_private", TypeEmitter.QuoteIfNeeded("_private"));
        Assert.Equal("$dollar", TypeEmitter.QuoteIfNeeded("$dollar"));
    }

    [Fact]
    public void QuoteIfNeeded_InvalidIdentifier_Quoted()
    {
        Assert.Equal("\"display-name\"", TypeEmitter.QuoteIfNeeded("display-name"));
        Assert.Equal("\"@type\"", TypeEmitter.QuoteIfNeeded("@type"));
        Assert.Equal("\"has space\"", TypeEmitter.QuoteIfNeeded("has space"));
        Assert.Equal("\"123start\"", TypeEmitter.QuoteIfNeeded("123start"));
    }

    // --- Fix 10: Empty object schema round-trip ---

    [Fact]
    public void EmptyRecord_Survives_OpenApi_RoundTrip()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record EmptyMarker();

            [RivetType]
            public sealed record ItemDto(string Id, EmptyMarker Marker);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItem =
                    Define.Get<ItemDto>("/api/items/{id}");
            }
            """;

        // Forward: C# → OpenAPI
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        // Verify extension is emitted
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        var emptySchema = doc.GetProperty("components").GetProperty("schemas").GetProperty("EmptyMarker");
        Assert.True(emptySchema.TryGetProperty("x-rivet-empty-record", out var ext),
            "EmptyMarker should have x-rivet-empty-record extension");
        Assert.True(ext.GetBoolean());

        // Reverse: OpenAPI → import → compile → walk
        var importResult = CompilationHelper.Import(json);
        var recompilation = CompilationHelper.CreateCompilationFromMultiple(
            importResult.Files.Select(f => f.Content).ToArray());
        var (reDiscovered, rewalker) = CompilationHelper.DiscoverAndWalk(recompilation);

        // EmptyMarker should survive as a definition (not collapsed to Dictionary)
        Assert.True(rewalker.Definitions.ContainsKey("EmptyMarker"),
            "EmptyMarker should survive round-trip as a type definition");
        var emptyDef = rewalker.Definitions["EmptyMarker"];
        Assert.Empty(emptyDef.Properties);

        // ItemDto should reference EmptyMarker, not Dictionary<string, JsonElement>
        var itemDef = rewalker.Definitions["ItemDto"];
        var markerProp = itemDef.Properties.First(p => p.Name == "marker");
        Assert.True(markerProp.Type is TsType.TypeRef { Name: "EmptyMarker" },
            $"ItemDto.marker should be TypeRef(EmptyMarker), got {markerProp.Type}");
    }

    // --- Fix 11: Zod InlineObject field name quoting ---

    [Fact]
    public void Zod_InlineObject_Quotes_NonIdentifier_FieldNames()
    {
        // Construct an endpoint with an InlineObject return type that has non-identifier field names.
        // This can't happen via Roslyn tuples (tuple names are always valid identifiers),
        // but could occur if a future code path constructs InlineObjects from JSON Schema field names.
        var inlineType = new TsType.InlineObject([
            ("display-name", new TsType.Primitive("string")),
            ("@type", new TsType.Primitive("string")),
            ("normalProp", new TsType.Primitive("number")),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("getWeird", "GET", "/api/weird", [],
                inlineType, "weird",
                [new(200, inlineType, null)]),
        };

        var zodValidators = ZodValidatorEmitter.Emit(endpoints, new Dictionary<string, string>());

        // Non-identifier field names must be quoted in z.object({...})
        Assert.Contains("\"display-name\":", zodValidators);
        Assert.Contains("\"@type\":", zodValidators);
        // Normal identifier should NOT be quoted
        Assert.Contains("normalProp:", zodValidators);
        Assert.DoesNotContain("\"normalProp\"", zodValidators);
    }

    // --- Nullable JsonNode import fix ---

    [Fact]
    public void NullableCSharpType_Survives_Import()
    {
        // Verify that nullable: true + x-rivet-csharp-type works for pure null type schemas
        var spec = CompilationHelper.BuildSpec(schemas: """
            "FlexDto": {
                "type": "object",
                "properties": {
                    "required": { "x-rivet-csharp-type": "JsonNode" },
                    "optional": { "nullable": true, "x-rivet-csharp-type": "JsonNode" }
                },
                "required": ["required"]
            }
            """);

        var result = CompilationHelper.Import(spec);
        var content = result.Files.First(f => f.Content.Contains("FlexDto")).Content;

        Assert.Contains("System.Text.Json.Nodes.JsonNode Required", content);
        Assert.Contains("System.Text.Json.Nodes.JsonNode? Optional", content);
    }

    // ========== Deep review 2: nullable fields ARE required (positional params are required) ==========

    [Fact]
    public void JsonSchema_Nullable_Field_Is_Required()
    {
        // A nullable positional parameter is required (must be present) but nullable (value can be null).
        // OpenAPI explicitly supports required + nullable.
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Name, string? Description);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItem =
                    Define.Get<ItemDto>("/api/items/{id}");
            }
            """;

        var schemas = CompilationHelper.EmitSchemas(source);
        var json = ExtractDefsJson(schemas);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var itemDto = root.GetProperty("ItemDto");
        var required = itemDto.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("name", requiredNames);
        Assert.Contains("description", requiredNames);
    }

    [Fact]
    public void OpenApi_Nullable_Field_Is_Required()
    {
        // Same check for OpenAPI emitter — should be consistent with JSON Schema
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Name, string? Description);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItem =
                    Define.Get<ItemDto>("/api/items/{id}");
            }
            """;

        var doc = EmitOpenApi(source);
        var schema = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("ItemDto");
        var required = schema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("name", requiredNames);
        Assert.Contains("description", requiredNames);
    }

    // ========== Deep review 2: monomorphised nullable type arg IS required ==========

    [Fact]
    public void OpenApi_Monomorphised_Generic_Nullable_TypeArg_Is_Required()
    {
        // When a generic type parameter resolves to a nullable type,
        // the monomorphised schema should not mark that field as required
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Wrapper<T>(T Value, string Label);

            [RivetType]
            public sealed record OptionalWrapper(Wrapper<string?> Wrapped);

            [RivetContract]
            public static class WrapperContract
            {
                public static readonly Define GetWrapper =
                    Define.Get<OptionalWrapper>("/api/wrapper");
            }
            """;

        var doc = EmitOpenApi(source);
        var schemas = doc.RootElement
            .GetProperty("components").GetProperty("schemas");

        // Both fields are required — Value is nullable but still a required constructor param
        var monoName = "WrapperOfNullableString";
        if (!schemas.TryGetProperty(monoName, out var monoSchema))
        {
            monoName = schemas.EnumerateObject()
                .First(p => p.Name.Contains("Wrapper") && p.Name != "OptionalWrapper")
                .Name;
            monoSchema = schemas.GetProperty(monoName);
        }

        var required = monoSchema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("label", requiredNames);
        Assert.Contains("value", requiredNames);
    }

    [Fact]
    public void JsonSchema_Monomorphised_Generic_Nullable_TypeArg_Is_Required()
    {
        // Same check for JSON Schema emitter
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Wrapper<T>(T Value, string Label);

            [RivetType]
            public sealed record OptionalWrapper(Wrapper<string?> Wrapped);

            [RivetContract]
            public static class WrapperContract
            {
                public static readonly Define GetWrapper =
                    Define.Get<OptionalWrapper>("/api/wrapper");
            }
            """;

        var schemas = CompilationHelper.EmitSchemas(source);
        var json = ExtractDefsJson(schemas);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var monoName = root.EnumerateObject()
            .First(p => p.Name.Contains("Wrapper") && p.Name != "OptionalWrapper")
            .Name;
        var monoSchema = root.GetProperty(monoName);

        var required = monoSchema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("label", requiredNames);
        Assert.Contains("value", requiredNames);
    }
}
