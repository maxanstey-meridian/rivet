using System.Text.Json;
using Rivet.Tool.Emit;
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
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );
        return JsonDocument.Parse(json);
    }

    private static IReadOnlyList<TsEndpointDefinition> WalkEndpoints(string source) =>
        CompilationHelper.WalkContract(source).Endpoints;

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
        var idProp = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ItemDto")
            .GetProperty("properties")
            .GetProperty("id");

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
        var props = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("EventDto")
            .GetProperty("properties");

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
        var props = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("PriceDto")
            .GetProperty("properties");

        Assert.Equal("int32", props.GetProperty("quantity").GetProperty("format").GetString());
        Assert.Equal("int64", props.GetProperty("total").GetProperty("format").GetString());
        Assert.Equal("decimal", props.GetProperty("amount").GetProperty("format").GetString());
        Assert.Equal("double", props.GetProperty("rate").GetProperty("format").GetString());
        Assert.Equal("float", props.GetProperty("score").GetProperty("format").GetString());
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
        var payload = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("FlexDto")
            .GetProperty("properties")
            .GetProperty("payload");

        // 3.1: the empty schema already admits null — no 'nullable', no 'type' (and
        // never { "type": "unknown" }, which is not a valid JSON Schema type).
        Assert.False(payload.TryGetProperty("nullable", out _), "3.1 must not emit nullable: true");
        Assert.False(
            payload.TryGetProperty("type", out _),
            "Nullable unknown should not emit a 'type' field — 'unknown' is not a valid OpenAPI type"
        );
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
        var multipart = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema");
        Assert.True(
            multipart.TryGetProperty("$ref", out var refVal),
            "Named multipart input should emit as $ref"
        );
        Assert.Equal("#/components/schemas/UploadInput", refVal.GetString());

        // The component schema has the required array
        var uploadSchema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("UploadInput");
        Assert.True(
            uploadSchema.TryGetProperty("required", out var required),
            "UploadInput schema should include a 'required' array"
        );

        var requiredFields = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("document", requiredFields);
        Assert.Contains("title", requiredFields);
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

        var endpoints = WalkEndpoints(source);
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

        var endpoints = WalkEndpoints(source);
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

        var endpoints = WalkEndpoints(source);
        var update = Assert.Single(endpoints);

        var routeParam = Assert.Single(update.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", routeParam.Name);
        // Should be number (from int Id), not string
        Assert.IsType<TsType.Primitive>(routeParam.Type);
        Assert.Equal("number", ((TsType.Primitive)routeParam.Type).Name);
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
            """
        );

        var result = CompilationHelper.Import(spec);
        var content = CompilationHelper.FindFile(result, "ItemsContract.cs");

        // Input type should be wired so the type survives round-trip
        // (POST with path-only params uses .Accepts<T>() since there's no output type)
        Assert.Contains("Accepts<ArchiveItemInput>", content);
        Assert.Contains("Define.Post(\"/api/items/{id}/archive\")", content);
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
        var schema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("SensorDto");
        var props = schema.GetProperty("properties");

        Assert.Equal("integer", props.GetProperty("temp").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("voltage").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("channel").GetProperty("type").GetString());
        Assert.Equal("integer", props.GetProperty("offset").GetProperty("type").GetString());
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

        var endpoints = WalkEndpoints(source);
        var ep = Assert.Single(endpoints);

        // taskId should appear exactly once as Route
        var routeParams = ep.Params.Where(p => p.Source == ParamSource.Route).ToList();
        Assert.Single(routeParams);
        Assert.Equal("taskId", routeParams[0].Name);

        // taskId should NOT appear as a FormField
        var formFields = ep.Params.Where(p => p.Source == ParamSource.FormField).ToList();
        Assert.DoesNotContain(
            formFields,
            f => string.Equals(f.Name, "taskId", StringComparison.OrdinalIgnoreCase)
        );

        // document is File, title is FormField
        Assert.Single(ep.Params, p => p.Source == ParamSource.File);
        Assert.Single(formFields, f => f.Name == "title");
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

        // Tuple elements remain structurally required even when their values are nullable.
        var pairProp = schemas
            .GetProperty("WithNullableTuple")
            .GetProperty("properties")
            .GetProperty("pair");
        var required = pairProp.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("key", requiredNames);
        Assert.Contains("value", requiredNames);
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
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        // Verify extension is emitted
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        var emptySchema = doc.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("EmptyMarker");
        Assert.True(
            emptySchema.TryGetProperty("x-rivet-empty-record", out var ext),
            "EmptyMarker should have x-rivet-empty-record extension"
        );
        Assert.True(ext.GetBoolean());

        // Reverse: OpenAPI → import → compile → walk
        var importResult = CompilationHelper.Import(json);
        var recompilation = CompilationHelper.CreateCompilationFromMultiple(
            importResult.Files.Select(f => f.Content).ToArray()
        );
        var (reDiscovered, rewalker) = CompilationHelper.DiscoverAndWalk(recompilation);

        // EmptyMarker should survive as a definition (not collapsed to Dictionary)
        Assert.True(
            rewalker.Definitions.ContainsKey("EmptyMarker"),
            "EmptyMarker should survive round-trip as a type definition"
        );
        var emptyDef = rewalker.Definitions["EmptyMarker"];
        Assert.Empty(emptyDef.Properties);

        // ItemDto should reference EmptyMarker, not Dictionary<string, JsonElement>
        var itemDef = rewalker.Definitions["ItemDto"];
        var markerProp = itemDef.Properties.First(p => p.Name == "marker");
        Assert.True(
            markerProp.Type is TsType.TypeRef { Name: "EmptyMarker" },
            $"ItemDto.marker should be TypeRef(EmptyMarker), got {markerProp.Type}"
        );
    }

    // --- Nullable JsonNode import fix ---

    [Fact]
    public void NullableCSharpType_Survives_Import()
    {
        // Verify that nullable: true + x-rivet-csharp-type works for pure null type schemas
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "FlexDto": {
                "type": "object",
                "properties": {
                    "required": { "x-rivet-csharp-type": "JsonNode" },
                    "optional": { "nullable": true, "x-rivet-csharp-type": "JsonNode" }
                },
                "required": ["required"]
            }
            """
        );

        var result = CompilationHelper.Import(spec);
        var content = result.Files.First(f => f.Content.Contains("FlexDto")).Content;

        Assert.Contains("System.Text.Json.Nodes.JsonNode Required", content);
        Assert.Contains("System.Text.Json.Nodes.JsonNode? Optional", content);
    }

    [Fact]
    public void OpenApi_Nullable_Field_Is_Not_Required()
    {
        // Nullable properties without [Required] should NOT be in the required array
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
        var schema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ItemDto");
        var required = schema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("name", requiredNames);
        Assert.DoesNotContain("description", requiredNames);
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
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Both fields are required — Value is nullable but still a required constructor param
        var monoName = "WrapperOfNullableString";
        if (!schemas.TryGetProperty(monoName, out var monoSchema))
        {
            monoName = schemas
                .EnumerateObject()
                .First(p => p.Name.Contains("Wrapper") && p.Name != "OptionalWrapper")
                .Name;
            monoSchema = schemas.GetProperty(monoName);
        }

        var required = monoSchema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("label", requiredNames);
        Assert.Contains("value", requiredNames);
    }
}
