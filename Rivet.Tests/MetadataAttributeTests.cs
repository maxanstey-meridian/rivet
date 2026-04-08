using System.Text.Json;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Forward-pipeline tests for metadata attributes.
/// These test C# source with attributes → TypeWalker → emitters,
/// NOT the import round-trip (which is tested elsewhere).
/// </summary>
public sealed class MetadataAttributeTests
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

    private static (TypeWalker Walker, IReadOnlyList<TsEndpointDefinition> Endpoints) WalkSource(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        return (walker, endpoints);
    }

    private static string EmitSchemas(string source) => CompilationHelper.EmitSchemas(source);

    private static string EmitClient(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        return string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
    }

    // ========== [RivetOptional] ==========

    [Fact]
    public void RivetOptional_Excludes_Property_From_Required()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record ItemDto(
                string Name,
                [property: RivetOptional] string? Nickname);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
            }
            """;

        using var doc = EmitOpenApi(source);
        var schema = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("ItemDto");
        var required = schema.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("name", required);
        Assert.DoesNotContain("nickname", required);
    }

    // ========== [RivetDescription] ==========

    [Fact]
    public void RivetDescription_On_Type_Emits_Schema_Description()
    {
        var source = """
            using Rivet;

            [RivetDescription("A user in the system")]
            [RivetType]
            public sealed record UserDto(string Name);

            [RivetContract]
            public static class UserContract
            {
                public static readonly RouteDefinition<UserDto> Get = Define.Get<UserDto>("/api/users");
            }
            """;

        using var doc = EmitOpenApi(source);
        var schema = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("UserDto");

        Assert.Equal("A user in the system", schema.GetProperty("description").GetString());
    }

    [Fact]
    public void RivetDescription_On_Property_Emits_Property_Description()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record UserDto(
                [property: RivetDescription("The user's full name")] string Name);

            [RivetContract]
            public static class UserContract
            {
                public static readonly RouteDefinition<UserDto> Get = Define.Get<UserDto>("/api/users");
            }
            """;

        using var doc = EmitOpenApi(source);
        var prop = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("UserDto")
            .GetProperty("properties").GetProperty("name");

        Assert.Equal("The user's full name", prop.GetProperty("description").GetString());
    }

    // ========== [RivetConstraints] ==========

    [Fact]
    public void RivetConstraints_Emit_In_OpenApi()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            [RivetType]
            public sealed record ItemDto(
                [property: MinLength(1), MaxLength(100), RegularExpression("^[a-z]+$")] string Name,
                [property: Range(0, 999.5), RivetConstraints(MultipleOf = 0.5)] double Score);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
            }
            """;

        using var doc = EmitOpenApi(source);
        var props = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("ItemDto")
            .GetProperty("properties");

        var name = props.GetProperty("name");
        Assert.Equal(1, name.GetProperty("minLength").GetInt32());
        Assert.Equal(100, name.GetProperty("maxLength").GetInt32());
        Assert.Equal("^[a-z]+$", name.GetProperty("pattern").GetString());

        var score = props.GetProperty("score");
        Assert.Equal(0, score.GetProperty("minimum").GetDouble());
        Assert.Equal(999.5, score.GetProperty("maximum").GetDouble());
        Assert.Equal(0.5, score.GetProperty("multipleOf").GetDouble());
    }

    [Fact]
    public void RivetConstraints_Emit_In_JsonSchema()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            [RivetType]
            public sealed record ItemDto(
                [property: StringLength(50, MinimumLength = 3)] string Name);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
            }
            """;

        var schemas = EmitSchemas(source);
        var defsJson = ExtractDefsJson(schemas);
        var defs = JsonDocument.Parse(defsJson).RootElement;

        var name = defs.GetProperty("ItemDto").GetProperty("properties").GetProperty("name");
        Assert.Equal(3, name.GetProperty("minLength").GetInt32());
        Assert.Equal(50, name.GetProperty("maxLength").GetInt32());
    }

    // ========== [RivetDefault] ==========

    [Fact]
    public void RivetDefault_Emits_Default_Value()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record ConfigDto(
                [property: RivetDefault("\"en\"")] string Locale,
                [property: RivetDefault("42")] int Limit);

            [RivetContract]
            public static class ConfigContract
            {
                public static readonly RouteDefinition<ConfigDto> Get = Define.Get<ConfigDto>("/api/config");
            }
            """;

        using var doc = EmitOpenApi(source);
        var props = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("ConfigDto")
            .GetProperty("properties");

        Assert.Equal("en", props.GetProperty("locale").GetProperty("default").GetString());
        Assert.Equal(42, props.GetProperty("limit").GetProperty("default").GetInt32());
    }

    // ========== [RivetExample] ==========

    [Fact]
    public void RivetExample_Emits_Example_Value()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record UserDto(
                [property: RivetExample("\"jane@example.com\"")] string Email);

            [RivetContract]
            public static class UserContract
            {
                public static readonly RouteDefinition<UserDto> Get = Define.Get<UserDto>("/api/users");
            }
            """;

        using var doc = EmitOpenApi(source);
        var prop = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("UserDto")
            .GetProperty("properties").GetProperty("email");

        Assert.Equal("jane@example.com", prop.GetProperty("example").GetString());
    }

    // ========== [RivetReadOnly] / [RivetWriteOnly] ==========

    [Fact]
    public void RivetReadOnly_And_WriteOnly_Emit_Flags()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record UserDto(
                [property: RivetReadOnly] string Id,
                [property: RivetWriteOnly] string Password,
                string Name);

            [RivetContract]
            public static class UserContract
            {
                public static readonly RouteDefinition<UserDto> Get = Define.Get<UserDto>("/api/users");
            }
            """;

        using var doc = EmitOpenApi(source);
        var props = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("UserDto")
            .GetProperty("properties");

        Assert.True(props.GetProperty("id").GetProperty("readOnly").GetBoolean());
        Assert.True(props.GetProperty("password").GetProperty("writeOnly").GetBoolean());
        Assert.False(props.GetProperty("name").TryGetProperty("readOnly", out _));
        Assert.False(props.GetProperty("name").TryGetProperty("writeOnly", out _));
    }

    // ========== [RivetFormat] ==========

    [Fact]
    public void RivetFormat_Applied_To_String_Property()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record LinkDto(
                [property: RivetFormat("uri-template")] string Href);

            [RivetContract]
            public static class LinkContract
            {
                public static readonly RouteDefinition<LinkDto> Get = Define.Get<LinkDto>("/api/links");
            }
            """;

        using var doc = EmitOpenApi(source);
        var prop = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("LinkDto")
            .GetProperty("properties").GetProperty("href");

        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("uri-template", prop.GetProperty("format").GetString());
    }

    // ========== [JsonStringEnumMemberName] in TypeWalker ==========

    [Fact]
    public void JsonStringEnumMemberName_Preserves_Original_Values()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            [JsonConverter(typeof(JsonStringEnumConverter))]
            public enum Priority
            {
                [JsonStringEnumMemberName("low")]
                Low,
                [JsonStringEnumMemberName("medium")]
                Medium,
                [JsonStringEnumMemberName("high")]
                High,
            }

            [RivetType]
            public sealed record TaskDto(string Title, Priority Priority);

            [RivetContract]
            public static class TaskContract
            {
                public static readonly RouteDefinition<TaskDto> Get = Define.Get<TaskDto>("/api/tasks");
            }
            """;

        var (walker, _) = WalkSource(source);

        Assert.True(walker.Enums.ContainsKey("Priority"));
        var members = ((TsType.StringUnion)walker.Enums["Priority"]).Members;
        Assert.Contains("low", members);
        Assert.Contains("medium", members);
        Assert.Contains("high", members);
        Assert.DoesNotContain("Low", members);
        Assert.DoesNotContain("Medium", members);
        Assert.DoesNotContain("High", members);
    }

    [Fact]
    public void JsonStringEnumMemberName_Values_In_OpenApi_Schema()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            [JsonConverter(typeof(JsonStringEnumConverter))]
            public enum Status
            {
                [JsonStringEnumMemberName("in-progress")]
                InProgress,
                Active,
                [JsonStringEnumMemberName("on_hold")]
                OnHold,
            }

            [RivetType]
            public sealed record ItemDto(Status Status);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
            }
            """;

        using var doc = EmitOpenApi(source);
        var enumSchema = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("Status");
        var values = enumSchema.GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()!).ToList();

        Assert.Contains("in-progress", values);
        Assert.Contains("active", values);
        Assert.Contains("on_hold", values);
        Assert.DoesNotContain("InProgress", values);
        Assert.DoesNotContain("OnHold", values);
        Assert.DoesNotContain("Active", values);
    }

    // ========== void .Returns(statusCode) ==========

    [Fact]
    public void Void_Returns_Emits_Response_Without_Content()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record ItemDto(string Name);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto, ItemDto> Update =
                    Define.Put<ItemDto, ItemDto>("/api/items/{id}")
                        .Returns(404, "Not found")
                        .Returns(409);
            }
            """;

        using var doc = EmitOpenApi(source);
        var responses = doc.RootElement
            .GetProperty("paths").GetProperty("/api/items/{id}")
            .GetProperty("put").GetProperty("responses");

        // 404 — void with description
        var resp404 = responses.GetProperty("404");
        Assert.Equal("Not found", resp404.GetProperty("description").GetString());
        Assert.False(resp404.TryGetProperty("content", out _), "Void response should have no content");

        // 409 — void without description
        var resp409 = responses.GetProperty("409");
        Assert.False(resp409.TryGetProperty("content", out _), "Void response should have no content");
    }

    // ========== FormEncoded ==========

    [Fact]
    public void FormEncoded_Emits_UrlEncoded_ContentType_In_OpenApi()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record LoginInput(string Username, string Password);

            [RivetType]
            public sealed record TokenDto(string Token);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition<LoginInput, TokenDto> Login =
                    Define.Post<LoginInput, TokenDto>("/api/auth/login")
                        .FormEncoded();
            }
            """;

        using var doc = EmitOpenApi(source);
        var requestBody = doc.RootElement
            .GetProperty("paths").GetProperty("/api/auth/login")
            .GetProperty("post").GetProperty("requestBody")
            .GetProperty("content");

        Assert.True(requestBody.TryGetProperty("application/x-www-form-urlencoded", out _),
            "Form-encoded endpoint should use application/x-www-form-urlencoded");
        Assert.False(requestBody.TryGetProperty("application/json", out _),
            "Form-encoded endpoint should not use application/json");
    }

    [Fact]
    public void FormEncoded_Client_Emits_URLSearchParams()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record LoginInput(string Username, string Password);

            [RivetType]
            public sealed record TokenDto(string Token);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition<LoginInput, TokenDto> Login =
                    Define.Post<LoginInput, TokenDto>("/api/auth/login")
                        .FormEncoded();
            }
            """;

        var client = EmitClient(source);

        Assert.Contains("URLSearchParams", client);
        Assert.Contains("formEncoded: true", client);
    }

    // ========== IFormFile → File primitive ==========

    [Fact]
    public void IFormFile_Maps_To_Binary_In_JsonSchema()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            [RivetType]
            public sealed record UploadInput(IFormFile Document, string Title);

            [RivetContract]
            public static class UploadContract
            {
                public static readonly RouteDefinition<UploadInput> Upload =
                    Define.Post<UploadInput>("/api/upload")
                        .AcceptsFile();
            }
            """;

        var schemas = EmitSchemas(source);
        var defsJson = ExtractDefsJson(schemas);
        var defs = JsonDocument.Parse(defsJson).RootElement;

        var docProp = defs.GetProperty("UploadInput").GetProperty("properties").GetProperty("document");
        Assert.Equal("string", docProp.GetProperty("type").GetString());
        Assert.Equal("binary", docProp.GetProperty("format").GetString());
    }

    // ========== Combined metadata round-trip ==========

    [Fact]
    public void All_Metadata_Attributes_Survive_Forward_Pipeline()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            [RivetDescription("A product listing")]
            [RivetType]
            public sealed record ProductDto(
                [property: RivetReadOnly]
                [property: RivetDescription("Unique identifier")]
                string Id,

                [property: MinLength(1), MaxLength(200)]
                [property: RivetDescription("Product name")]
                [property: RivetExample("\"Widget Pro\"")] string Name,

                [property: RivetDefault("9.99")]
                [property: Range(0, double.MaxValue)]
                double Price,

                [property: RivetOptional]
                [property: RivetWriteOnly]
                string? InternalNotes);

            [RivetContract]
            public static class ProductContract
            {
                public static readonly RouteDefinition<ProductDto> Get =
                    Define.Get<ProductDto>("/api/products");
            }
            """;

        using var doc = EmitOpenApi(source);
        var schema = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("ProductDto");

        // Type-level description
        Assert.Equal("A product listing", schema.GetProperty("description").GetString());

        // Required: id, name, price are required; internalNotes is optional
        var required = schema.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("id", required);
        Assert.Contains("name", required);
        Assert.Contains("price", required);
        Assert.DoesNotContain("internalNotes", required);

        var props = schema.GetProperty("properties");

        // id: readOnly + description
        var id = props.GetProperty("id");
        Assert.True(id.GetProperty("readOnly").GetBoolean());
        Assert.Equal("Unique identifier", id.GetProperty("description").GetString());

        // name: constraints + description + example
        var name = props.GetProperty("name");
        Assert.Equal(1, name.GetProperty("minLength").GetInt32());
        Assert.Equal(200, name.GetProperty("maxLength").GetInt32());
        Assert.Equal("Product name", name.GetProperty("description").GetString());
        Assert.Equal("Widget Pro", name.GetProperty("example").GetString());

        // price: default + constraint
        var price = props.GetProperty("price");
        Assert.Equal(9.99, price.GetProperty("default").GetDouble());
        Assert.Equal(0, price.GetProperty("minimum").GetDouble());

        // internalNotes: writeOnly
        var notes = props.GetProperty("internalNotes");
        Assert.True(notes.GetProperty("writeOnly").GetBoolean());
    }

    // ========== [RivetFormat] on nullable string ==========

    [Fact]
    public void RivetFormat_On_Nullable_String_Emits_Format()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record LinkDto(
                [property: RivetFormat("uri")] string? Href);

            [RivetContract]
            public static class LinkContract
            {
                public static readonly RouteDefinition<LinkDto> Get = Define.Get<LinkDto>("/api/links");
            }
            """;

        using var doc = EmitOpenApi(source);
        var prop = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("LinkDto")
            .GetProperty("properties").GetProperty("href");

        Assert.Equal("uri", prop.GetProperty("format").GetString());
        Assert.True(prop.GetProperty("nullable").GetBoolean());
    }

    // ========== Monomorphised generic metadata ==========

    [Fact]
    public void Monomorphised_Generic_Preserves_Property_Metadata()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            [RivetType]
            public sealed record PagedResult<T>(
                [property: RivetDescription("Result items")]
                [property: RivetConstraints(MinItems = 0, MaxItems = 100)]
                List<T> Items,
                [property: RivetDefault("1")]
                int Page);

            [RivetType]
            public sealed record TaskDto(string Title);

            [RivetContract]
            public static class TaskContract
            {
                public static readonly RouteDefinition<PagedResult<TaskDto>> List =
                    Define.Get<PagedResult<TaskDto>>("/api/tasks");
            }
            """;

        using var doc = EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Find the monomorphised schema
        var monoName = schemas.EnumerateObject()
            .First(p => p.Name.Contains("PagedResult")).Name;
        var mono = schemas.GetProperty(monoName);
        var items = mono.GetProperty("properties").GetProperty("items");

        Assert.Equal("Result items", items.GetProperty("description").GetString());
        Assert.Equal(0, items.GetProperty("minItems").GetInt32());
        Assert.Equal(100, items.GetProperty("maxItems").GetInt32());

        var page = mono.GetProperty("properties").GetProperty("page");
        Assert.Equal(1, page.GetProperty("default").GetInt32());
    }

    // ========== SchemaEnricher invalid JSON default fallback ==========

    [Fact]
    public void RivetDefault_Invalid_Json_Falls_Back_To_Raw_String()
    {
        var source = """
            using Rivet;

            [RivetType]
            public sealed record ConfigDto(
                [property: RivetDefault("not-valid-json")] string Mode);

            [RivetContract]
            public static class ConfigContract
            {
                public static readonly RouteDefinition<ConfigDto> Get =
                    Define.Get<ConfigDto>("/api/config");
            }
            """;

        using var doc = EmitOpenApi(source);
        var prop = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("ConfigDto")
            .GetProperty("properties").GetProperty("mode");

        Assert.True(prop.TryGetProperty("default", out var def),
            "default should be present even for invalid JSON");
        Assert.Equal("not-valid-json", def.GetString());
    }

    // ========== Constraints override integer range ==========

    [Fact]
    public void RivetConstraints_Overrides_Integer_Range_In_OpenApi()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            [RivetType]
            public sealed record ItemDto(
                [property: Range(0, 100)] int Score);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> Get =
                    Define.Get<ItemDto>("/api/items");
            }
            """;

        using var doc = EmitOpenApi(source);
        var prop = doc.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("ItemDto")
            .GetProperty("properties").GetProperty("score");

        // User constraints should override int32 range (-2147483648 to 2147483647)
        Assert.Equal(0, prop.GetProperty("minimum").GetDouble());
        Assert.Equal(100, prop.GetProperty("maximum").GetDouble());
    }

    // ========== Helpers ==========

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
}
