using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Tool;
using Rivet.Tool.Analysis;
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

    private static (TypeWalker Walker, IReadOnlyList<TsEndpointDefinition> Endpoints) WalkSource(
        string source
    )
    {
        var (endpoints, walker) = CompilationHelper.WalkContract(source);
        return (walker, endpoints);
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ItemDto");
        var required = schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        Assert.Contains("name", required);
        Assert.DoesNotContain("nickname", required);
    }

    [Fact]
    public void Attributes_Are_Identified_By_Symbol_Not_By_Name()
    {
        var source = """
            using Rivet;

            namespace Other
            {
                [System.AttributeUsage(System.AttributeTargets.All)]
                public sealed class RivetOptionalAttribute : System.Attribute;

                [System.AttributeUsage(System.AttributeTargets.All)]
                public sealed class RivetDescriptionAttribute(string text) : System.Attribute
                {
                    public string Text { get; } = text;
                }
            }

            namespace App
            {
                [RivetType]
                public sealed record ItemDto(
                    [property: Other.RivetOptional] string? Nickname,
                    [property: Rivet.RivetOptional] string? Alias,
                    [property: Other.RivetDescription("not a Rivet description")] string Name);

                [RivetContract]
                public static class ItemContract
                {
                    public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
                }
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ItemDto");
        var required = schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        Assert.Contains("nickname", required);
        Assert.DoesNotContain("alias", required);
        Assert.False(
            schema
                .GetProperty("properties")
                .GetProperty("name")
                .TryGetProperty("description", out _)
        );
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("UserDto");

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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var prop = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("UserDto")
            .GetProperty("properties")
            .GetProperty("name");

        Assert.Equal("The user's full name", prop.GetProperty("description").GetString());
    }

    // ========== [RivetConstraints] ==========

    // A DataAnnotations attribute (or [RivetConstraints]) on a positional record parameter
    // without [property:] lands on the constructor parameter, where MVC validation reads
    // it. The spec reads it there too, so both placements emit the same constraints.
    [Theory]
    [InlineData("property: ")]
    [InlineData("")]
    public void RivetConstraints_Emit_In_OpenApi(string target)
    {
        var source = $$"""
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            [RivetType]
            public sealed record ItemDto(
                [{{target}}MinLength(1), MaxLength(100), RegularExpression("^[a-z]+$")] string Name,
                [{{target}}Range(0, 999.5)][{{target}}RivetConstraints(MultipleOf = 0.5)] double Score,
                [{{target}}EmailAddress] string Contact);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var props = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ItemDto")
            .GetProperty("properties");

        var name = props.GetProperty("name");
        Assert.Equal(1, name.GetProperty("minLength").GetInt32());
        Assert.Equal(100, name.GetProperty("maxLength").GetInt32());
        Assert.Equal("^[a-z]+$", name.GetProperty("pattern").GetString());

        var score = props.GetProperty("score");
        Assert.Equal(0, score.GetProperty("minimum").GetDouble());
        Assert.Equal(999.5, score.GetProperty("maximum").GetDouble());
        Assert.Equal(0.5, score.GetProperty("multipleOf").GetDouble());

        Assert.Equal("email", props.GetProperty("contact").GetProperty("format").GetString());
    }

    // MVC validates a record through the bound type's own constructor, so the parameter
    // attributes that count are the derived record's, never the base record's.
    [Fact]
    public void Parameter_Constraints_Come_From_The_Walked_Record_Not_Its_Base()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            public abstract record NamedDto([MaxLength(5)] string Name);

            [RivetType]
            public sealed record ItemDto([MinLength(1)] string Name) : NamedDto(Name);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var name = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ItemDto")
            .GetProperty("properties")
            .GetProperty("name");

        Assert.Equal(1, name.GetProperty("minLength").GetInt32());
        Assert.False(name.TryGetProperty("maxLength", out _));
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var props = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ConfigDto")
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var prop = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("UserDto")
            .GetProperty("properties")
            .GetProperty("email");

        // 3.1: schema-level examples is a JSON Schema 2020-12 array
        var examples = prop.GetProperty("examples");
        Assert.Equal(1, examples.GetArrayLength());
        Assert.Equal("jane@example.com", examples[0].GetString());
    }

    // ========== [RivetGeneratedSchemaMetadata] enumJson ==========

    [Fact]
    public void Generated_Schema_Metadata_EnumJson_Emits_The_Values_At_Its_Pointer()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            [RivetType]
            public sealed record ResultDto(
                [property: RivetGeneratedSchemaMetadata("", null, null, null, null, null, -1, -1, null, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, -1, -1, false, null, null, null, false, false, null, false, false, false, false, false, null, enumJson: "[false]")]
                bool Success,
                [property: RivetGeneratedSchemaMetadata("/items", null, null, null, null, null, -1, -1, null, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, -1, -1, false, null, null, null, false, false, null, false, false, false, false, false, null, enumJson: "[0.5,1.5]")]
                List<double> Ratios);

            [RivetContract]
            public static class ResultContract
            {
                public static readonly RouteDefinition<ResultDto> Get = Define.Get<ResultDto>("/api/result");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var props = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ResultDto")
            .GetProperty("properties");

        Assert.False(
            Assert
                .Single(props.GetProperty("success").GetProperty("enum").EnumerateArray())
                .GetBoolean()
        );
        Assert.Equal(
            [0.5, 1.5],
            props
                .GetProperty("ratios")
                .GetProperty("items")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetDouble())
        );
    }

    [Theory]
    [InlineData("null", "\"true\"", "enum", "true")]
    [InlineData("null", "\"{}\"", "enum", "{}")]
    [InlineData("\"\\\"one\\\"\"", "null", "examples", "\"one\"")]
    public void Generated_Schema_Metadata_Array_Keyword_That_Is_Not_An_Array_Is_A_User_Error(
        string examples,
        string enumJson,
        string keyword,
        string value
    )
    {
        // `enum: true` or `examples: "one"` is not a schema; refuse rather than emit it.
        var source = $$"""
            using Rivet;

            [RivetType]
            public sealed record ResultDto(
                [property: RivetGeneratedSchemaMetadata("", null, null, null, null, {{examples}}, -1, -1, null, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, -1, -1, false, null, null, null, false, false, null, false, false, false, false, false, null, enumJson: {{enumJson}})]
                bool Success);

            [RivetContract]
            public static class ResultContract
            {
                public static readonly RouteDefinition<ResultDto> Get = Define.Get<ResultDto>("/api/result");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApi(source)
        );
        Assert.Equal(
            $"error RIV1123: schema metadata {keyword} must be a JSON array, got '{value}'",
            exception.Message
        );
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var props = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("UserDto")
            .GetProperty("properties");

        Assert.True(props.GetProperty("id").GetProperty("readOnly").GetBoolean());
        Assert.True(props.GetProperty("password").GetProperty("writeOnly").GetBoolean());
        Assert.False(props.GetProperty("name").TryGetProperty("readOnly", out _));
        Assert.False(props.GetProperty("name").TryGetProperty("writeOnly", out _));
    }

    // System.Text.Json binds a get-only property only through the constructor it
    // deserializes with: [JsonConstructor], else a public parameterless one, else the only
    // public one. A bound property is on both surfaces; an unbound one is readOnly.
    [Theory]
    [InlineData(
        "public sealed record ItemDto(string Name) { public string Name { get; } = Name; }",
        false
    )]
    [InlineData(
        "public sealed class ItemDto(string name) { public string Name { get; } = name; }",
        false
    )]
    [InlineData(
        "public sealed class ItemDto { public ItemDto(string name) => Name = name; public string Name { get; } }",
        false
    )]
    [InlineData(
        "public sealed class ItemDto { public ItemDto() { } [JsonConstructor] public ItemDto(string name) => Name = name; public string Name { get; } = \"\"; }",
        false
    )]
    [InlineData(
        "public sealed record ItemDto(string Name) { public string Name { get; } = Name; public ItemDto() : this(\"x\") { } }",
        true
    )]
    [InlineData(
        "public sealed class ItemDto { public ItemDto(string name) => Name = name; public ItemDto(string name, int n) => Name = name; public string Name { get; } }",
        true
    )]
    // [RivetType] can't sit on a struct: it marks a stand-in and the contract reaches the struct.
    [InlineData(
        "public sealed class Marker; public readonly struct ItemDto { public ItemDto(string name) => Name = name; public string Name { get; } }",
        true
    )]
    [InlineData(
        "public abstract class BaseDto { protected BaseDto(string name) => Name = name; public string Name { get; } } public sealed class ItemDto : BaseDto { public ItemDto(string name) : base(name) { } }",
        false
    )]
    public void GetOnly_Property_Is_ReadOnly_Unless_The_Deserialization_Constructor_Binds_It(
        string declaration,
        bool readOnly
    )
    {
        var source = $$"""
            using System.Text.Json.Serialization;
            using Rivet;

            [RivetType]
            {{declaration}}

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var name = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ItemDto")
            .GetProperty("properties")
            .GetProperty("name");

        Assert.Equal(readOnly, name.TryGetProperty("readOnly", out _));
    }

    [Fact]
    public void System_Text_Json_Picks_The_Constructors_The_Walker_Assumes()
    {
        var lone = JsonSerializer.Deserialize<LoneConstructorProbe>(
            """{"name":"wire"}""",
            JsonSerializerOptions.Web
        );
        Assert.Equal("wire", lone!.Name);

        var parameterless = JsonSerializer.Deserialize<ParameterlessConstructorProbe>(
            """{"name":"wire"}""",
            JsonSerializerOptions.Web
        );
        Assert.Equal("default", parameterless!.Name);

        var structured = JsonSerializer.Deserialize<StructConstructorProbe>(
            """{"name":"wire"}""",
            JsonSerializerOptions.Web
        );
        Assert.Null(structured.Name);

        var inherited = JsonSerializer.Deserialize<DerivedConstructorProbe>(
            """{"name":"wire"}""",
            JsonSerializerOptions.Web
        );
        Assert.Equal("wire", inherited!.Name);

        Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Deserialize<TwoJsonConstructorsProbe>(
                """{"name":"wire"}""",
                JsonSerializerOptions.Web
            )
        );
    }

    private readonly struct StructConstructorProbe
    {
        public StructConstructorProbe(string name) => Name = name;

        public string Name { get; }
    }

    private abstract class BaseConstructorProbe
    {
        protected BaseConstructorProbe(string name) => Name = name;

        public string Name { get; }
    }

    private sealed class DerivedConstructorProbe(string name) : BaseConstructorProbe(name);

    private sealed class TwoJsonConstructorsProbe
    {
        [JsonConstructor]
        public TwoJsonConstructorsProbe(string name) => Name = name;

        [JsonConstructor]
        public TwoJsonConstructorsProbe(string name, int count) => Name = name + count;

        public string Name { get; }
    }

    private sealed class LoneConstructorProbe(string name)
    {
        public string Name { get; } = name;
    }

    private sealed record ParameterlessConstructorProbe(string Name)
    {
        public string Name { get; } = Name;

        public ParameterlessConstructorProbe()
            : this("default") { }
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var prop = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("LinkDto")
            .GetProperty("properties")
            .GetProperty("href");

        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("uri-template", prop.GetProperty("format").GetString());
    }

    // A [RivetScalar] brand may be a class or a struct, and RIV1117 tells either to take
    // its format from a [RivetFormat] on the brand, so the attribute must reach both.
    [Theory]
    [InlineData("sealed record")]
    [InlineData("readonly record struct")]
    public void RivetFormat_On_A_Scalar_Brand_Sets_The_Brand_Format(string declaration)
    {
        var source = $$"""
            using Rivet;

            [RivetScalar, RivetFormat("email")]
            public {{declaration}} Email(string Value);

            [RivetType]
            public sealed record ContactDto(Email Address);

            [RivetContract]
            public static class ContactContract
            {
                public static readonly RouteDefinition<ContactDto> Get = Define.Get<ContactDto>("/api/contact");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var brand = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Email");

        Assert.Equal("string", brand.GetProperty("type").GetString());
        Assert.Equal("email", brand.GetProperty("format").GetString());
        Assert.Equal("Email", brand.GetProperty("x-rivet-brand").GetString());
    }

    // An older Rivet.Attributes writes a shorter [RivetGeneratedSchemaMetadata]: the arguments
    // it lacks read as absent, as IntAt, StringAt, and BoolAt read them, never out of range.
    [Fact]
    public void Generated_Schema_Metadata_From_An_Older_Version_Reads_Missing_Arguments_As_Absent()
    {
        var source = """
            using System;
            using Rivet;

            [RivetType]
            public sealed record ResultDto(
                [property: RivetGeneratedSchemaMetadata("", null, "Older metadata.", null, null, null, -1, -1, null)]
                double Ratio);

            [RivetContract]
            public static class ResultContract
            {
                public static readonly RouteDefinition<ResultDto> Get = Define.Get<ResultDto>("/api/result");
            }

            namespace Rivet
            {
                [AttributeUsage(AttributeTargets.Property)]
                public sealed class RivetGeneratedSchemaMetadataAttribute(
                    string pointer, string? title, string? description, string? defaultValue,
                    string? example, string? examples, int minLength, int maxLength,
                    string? pattern) : Attribute;
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var ratio = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ResultDto")
            .GetProperty("properties")
            .GetProperty("ratio");

        Assert.Equal("Older metadata.", ratio.GetProperty("description").GetString());
        Assert.False(ratio.TryGetProperty("minimum", out _));
    }

    // ========== Enum wire representation ==========

    [Fact]
    public void Ordinary_Enum_Is_Numeric_By_Default()
    {
        var source = """
            using Rivet;

            public enum Priority
            {
                Low,
                Medium,
                High,
            }

            [RivetType]
            public sealed record TaskDto(string Title, Priority Priority);
            """;

        var (walker, _) = WalkSource(source);

        // No explicit string converter — ordinary System.Text.Json numeric serialization.
        var union = Assert.IsType<TsType.IntUnion>(walker.Enums["Priority"]);
        Assert.Equal(["0", "1", "2"], union.Members);
    }

    [Fact]
    public void Wide_Enum_Underlying_Types_Preserve_Values_Outside_Int32()
    {
        var source = """
            using Rivet;

            public enum Large : long
            {
                Huge = 5000000000,
            }

            public enum Unbound : ulong
            {
                Huge = 5000000000,
                Max = 18446744073709551615,
            }

            [RivetType]
            public sealed record WideDto(Large Large, Unbound Unbound);

            [RivetContract]
            public static class WideContract
            {
                public static readonly RouteDefinition<WideDto> Get = Define.Get<WideDto>("/api/wide");
            }
            """;

        var (walker, _) = WalkSource(source);

        // Signed and unsigned underlying types carry every legal constant as exact
        // decimal digits — no Int32 truncation and no negative reinterpretation for
        // values above Int64
        // (acceptance:numeric-enums-cover-all-legal-underlying-values).
        var large = Assert.IsType<TsType.IntUnion>(walker.Enums["Large"]);
        Assert.Equal(["5000000000"], large.Members);

        var unbound = Assert.IsType<TsType.IntUnion>(walker.Enums["Unbound"]);
        Assert.Equal(["5000000000", "18446744073709551615"], unbound.Members);

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var largeValues = schemas
            .GetProperty("Large")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(v => v.GetRawText())
            .ToList();
        Assert.Equal(["5000000000"], largeValues);
        var unboundValues = schemas
            .GetProperty("Unbound")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(v => v.GetRawText())
            .ToList();
        Assert.Equal(["5000000000", "18446744073709551615"], unboundValues);
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var enumSchema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Status");
        var values = enumSchema
            .GetProperty("enum")
            .EnumerateArray()
            .Select(v => v.GetString()!)
            .ToList();

        Assert.Contains("in-progress", values);
        Assert.Contains("Active", values);
        Assert.Contains("on_hold", values);
        Assert.DoesNotContain("InProgress", values);
        Assert.DoesNotContain("OnHold", values);
        Assert.DoesNotContain("active", values);
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
                    Define.Put<ItemDto, ItemDto>("/api/items/{id:guid}")
                        .Returns(404, "Not found")
                        .Returns(409);
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var responses = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/items/{id}")
            .GetProperty("put")
            .GetProperty("responses");

        // 404 — void with description
        var resp404 = responses.GetProperty("404");
        Assert.Equal("Not found", resp404.GetProperty("description").GetString());
        Assert.False(
            resp404.TryGetProperty("content", out _),
            "Void response should have no content"
        );

        // 409 — void without description
        var resp409 = responses.GetProperty("409");
        Assert.False(
            resp409.TryGetProperty("content", out _),
            "Void response should have no content"
        );
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var requestBody = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/auth/login")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content");

        Assert.True(
            requestBody.TryGetProperty("application/x-www-form-urlencoded", out _),
            "Form-encoded endpoint should use application/x-www-form-urlencoded"
        );
        Assert.False(
            requestBody.TryGetProperty("application/json", out _),
            "Form-encoded endpoint should not use application/json"
        );
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ProductDto");

        // Type-level description
        Assert.Equal("A product listing", schema.GetProperty("description").GetString());

        // Required: id, name, price are required; internalNotes is optional
        var required = schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
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
        Assert.Equal("Widget Pro", name.GetProperty("examples")[0].GetString());

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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var prop = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("LinkDto")
            .GetProperty("properties")
            .GetProperty("href");

        Assert.Equal("uri", prop.GetProperty("format").GetString());
        Assert.Equal(
            ["string", "null"],
            prop.GetProperty("type").EnumerateArray().Select(t => t.GetString()!).ToArray()
        );
    }

    // ========== Monomorphised generic metadata ==========

    [Fact]
    public void Monomorphised_Generic_Preserves_Property_Metadata()
    {
        var source = """
            using System.Collections.Generic;
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            [RivetType]
            public sealed record PagedResult<T>(
                [property: RivetDescription("Result items")]
                [property: Length(0, 100)]
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Find the monomorphised schema
        var monoName = schemas.EnumerateObject().First(p => p.Name.Contains("PagedResult")).Name;
        var mono = schemas.GetProperty(monoName);
        var items = mono.GetProperty("properties").GetProperty("items");

        Assert.Equal("Result items", items.GetProperty("description").GetString());
        Assert.Equal(0, items.GetProperty("minItems").GetInt32());
        Assert.Equal(100, items.GetProperty("maxItems").GetInt32());

        var page = mono.GetProperty("properties").GetProperty("page");
        Assert.Equal(1, page.GetProperty("default").GetInt32());
    }

    // ========== Invalid JSON default is a user error ==========

    [Fact]
    public void RivetDefault_Invalid_Json_Is_Refused()
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

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApi(source)
        );
        Assert.Contains(
            "default of property 'mode' must be a JSON literal, got 'not-valid-json'",
            exception.Message
        );
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

        using var doc = CompilationHelper.EmitOpenApi(source);
        var prop = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ItemDto")
            .GetProperty("properties")
            .GetProperty("score");

        // User constraints should override int32 range (-2147483648 to 2147483647)
        Assert.Equal(0, prop.GetProperty("minimum").GetDouble());
        Assert.Equal(100, prop.GetProperty("maximum").GetDouble());
    }

    // ========== [Range] bounds ==========

    [Fact]
    public void RangeTypeStringOverload_ParsesInvariant_NoCrash()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PriceDto
            {
                [Range(typeof(decimal), "0.1", "100")]
                public decimal Amount { get; init; }
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var amount = Assert.Single(walker.Definitions["PriceDto"].Properties);
        Assert.NotNull(amount.Constraints);
        Assert.Equal(0.1, amount.Constraints!.Minimum);
        Assert.Equal(100, amount.Constraints.Maximum);
    }

    [Fact]
    public void RangeUnparseableBound_SkipsConstraintWithWarning()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PriceDto
            {
                [Range(typeof(decimal), "not-a-number", "100")]
                public decimal Amount { get; init; }
            }
            """;

        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (_, walker) = CompilationHelper.WalkContract(source);
        });

        var amount = Assert.Single(walker.Definitions["PriceDto"].Properties);
        Assert.True(
            amount.Constraints is null
                || (amount.Constraints.Minimum is null && amount.Constraints.Maximum is null)
        );
        Assert.Contains("Range", stderr);
    }

    // ========== Attribute identity ==========

    [Fact]
    public void User_Attribute_Named_JsonExtensionData_Does_Not_Hide_A_Property()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class JsonExtensionDataAttribute : Attribute;

            [RivetType]
            public sealed record Input(
                [property: JsonExtensionData] string Value,
                string Other);

            [RivetType]
            public sealed record Result(string Id);

            [RivetContract]
            public static class TestContract
            {
                public static readonly RouteDefinition<Input, Result> Create = Define.Post<Input, Result>("/api/items");
            }
            """;

        using var document = CompilationHelper.EmitOpenApi(source);
        var input = document
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Input");

        Assert.True(input.GetProperty("properties").TryGetProperty("value", out _));
    }

    // ========== [RivetSchemaType] only re-types a primitive (G.1) ==========

    private static RivetUserException WalkRefusal(string source) =>
        Assert.Throws<RivetUserException>(() => CompilationHelper.WalkContract(source));

    private static JsonElement Schema(string source, string name)
    {
        using var document = CompilationHelper.EmitOpenApi(source);
        return document
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(name)
            .Clone();
    }

    // A brand is a scalar already, so its advice is to change its Value, not to make it one.
    [Theory]
    [InlineData("ItemDto", "the schema 'ItemDto'", "mark it [RivetScalar]")]
    [InlineData("List<string>", "an array", "mark it [RivetScalar]")]
    [InlineData("Dictionary<string, int>?", "a dictionary (nullable)", "mark it [RivetScalar]")]
    [InlineData("Email", "the [RivetScalar] brand 'Email'", "the Value property on 'Email'")]
    [InlineData(
        "Email?",
        "the [RivetScalar] brand 'Email' (nullable)",
        "the Value property on 'Email'"
    )]
    public void RivetSchemaType_On_A_NonPrimitive_Member_Is_Refused(
        string memberType,
        string mapsTo,
        string advice
    )
    {
        var source = $$"""
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetScalar]
            public sealed record Email(string Value);

            public sealed record ItemDto(string Id);

            [RivetType]
            public sealed record Holder([property: RivetSchemaType("string")] {{memberType}} Value);
            """;

        var refusal = WalkRefusal(source);

        Assert.StartsWith($"error {Diagnostics.SchemaTypeOnNonPrimitive}:", refusal.Message);
        Assert.Contains("'Holder.Value'", refusal.Message);
        Assert.Contains($"maps to {mapsTo}", refusal.Message);
        Assert.Contains(advice, refusal.Message);
        Assert.Contains("remove [RivetSchemaType]", refusal.Message);
    }

    // The same reader serves a polymorphic variant's members and a route, query or header
    // param lowered from an input record, so neither drops the attribute either.
    [Theory]
    [InlineData("variant")]
    [InlineData("lowered")]
    public void RivetSchemaType_On_A_NonPrimitive_Is_Refused_On_Every_Path(string path)
    {
        var source =
            path == "variant"
                ? """
                    using System.Collections.Generic;
                    using System.Text.Json.Serialization;
                    using Rivet;

                    namespace Test;

                    [RivetType]
                    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
                    [JsonDerivedType(typeof(Circle), "circle")]
                    public abstract record Shape;

                    public sealed record Circle([property: RivetSchemaType("string")] List<int> Radii) : Shape;
                    """
                : """
                    using System.Collections.Generic;
                    using Rivet;

                    namespace Test;

                    public sealed record Search([property: RivetSchemaType("string")] List<int> Ids);

                    public sealed record Result(string Id);

                    [RivetContract]
                    public static class SearchContract
                    {
                        public static readonly RouteDefinition<Search, Result> Find =
                            Define.Get<Search, Result>("/api/search");
                    }
                    """;

        var refusal = WalkRefusal(source);

        Assert.StartsWith($"error {Diagnostics.SchemaTypeOnNonPrimitive}:", refusal.Message);
        Assert.Contains(path == "variant" ? "'Circle.Radii'" : "'Search.Ids'", refusal.Message);
    }

    // ========== Open and closed objects (G.3, G.9) ==========

    [Theory]
    [InlineData("Dictionary<string, JsonElement>")]
    [InlineData("Dictionary<string, object>")]
    [InlineData("IDictionary<string, JsonElement>")]
    [InlineData("SortedDictionary<string, object?>")]
    [InlineData("JsonObject")]
    public void A_JsonExtensionData_Bag_Opens_The_Schema(string bagType)
    {
        var source = $$"""
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Nodes;
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Holder(string Id)
            {
                [JsonExtensionData]
                public {{bagType}} Extra { get; init; } = default!;
            }
            """;

        var holder = Schema(source, "Holder");

        Assert.Equal(
            ["id"],
            holder.GetProperty("properties").EnumerateObject().Select(p => p.Name)
        );
        var open = holder.GetProperty("additionalProperties");
        Assert.Equal(JsonValueKind.Object, open.ValueKind);
        Assert.Empty(open.EnumerateObject());
    }

    // A base's bag opens every type that inherits it, a variant included; a private bag
    // (System.Text.Json never sees it) opens nothing.
    [Fact]
    public void An_Inherited_Bag_Opens_The_Derived_Schema_And_A_Private_One_Does_Not()
    {
        var source = """
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            public abstract record Extensible
            {
                [JsonExtensionData]
                public Dictionary<string, JsonElement> Extra { get; init; } = [];
            }

            [RivetType]
            public sealed record Child(string Id) : Extensible;

            [RivetType]
            [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
            [JsonDerivedType(typeof(Circle), "circle")]
            public abstract record Shape;

            public sealed record Circle(double Radius) : Shape
            {
                [JsonExtensionData]
                public Dictionary<string, object> Extra { get; init; } = [];
            }

            [RivetType]
            public sealed record Hidden(string Id)
            {
                [JsonExtensionData]
                private Dictionary<string, object> Extra { get; init; } = [];
            }
            """;

        using var document = CompilationHelper.EmitOpenApi(source);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.Equal(
            JsonValueKind.Object,
            schemas.GetProperty("Child").GetProperty("additionalProperties").ValueKind
        );
        Assert.Equal(
            JsonValueKind.Object,
            schemas.GetProperty("Shape_Circle").GetProperty("additionalProperties").ValueKind
        );
        Assert.False(schemas.GetProperty("Hidden").TryGetProperty("additionalProperties", out _));
    }

    [Theory]
    [InlineData("Dictionary<string, string>")]
    [InlineData("IReadOnlyDictionary<string, JsonElement>")]
    [InlineData("Dictionary<string, JsonNode>")]
    public void A_Bag_System_Text_Json_Refuses_Is_Refused(string bagType)
    {
        var source = $$"""
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Nodes;
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Holder(string Id)
            {
                [JsonExtensionData]
                public {{bagType}} Extra { get; init; } = default!;
            }
            """;

        var refusal = WalkRefusal(source);

        Assert.StartsWith($"error {Diagnostics.InvalidJsonExtensionData}:", refusal.Message);
        Assert.Contains("'Holder.Extra'", refusal.Message);
        Assert.Contains("JsonObject", refusal.Message);
    }

    [Fact]
    public void A_Second_Bag_Down_The_Chain_Is_Refused()
    {
        var source = """
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            public abstract record Extensible
            {
                [JsonExtensionData]
                public Dictionary<string, JsonElement> Extra { get; init; } = [];
            }

            [RivetType]
            public sealed record Child(string Id) : Extensible
            {
                [JsonExtensionData]
                public Dictionary<string, object> More { get; init; } = [];
            }
            """;

        var refusal = WalkRefusal(source);

        Assert.StartsWith($"error {Diagnostics.InvalidJsonExtensionData}:", refusal.Message);
        Assert.Contains("'Extensible.Extra', 'Child.More'", refusal.Message);
    }

    // Decision 10: Disallow is modelled, on the type it sits on (System.Text.Json doesn't
    // inherit it), a polymorphic variant included.
    [Fact]
    public void JsonUnmappedMemberHandling_Disallow_Closes_Only_Its_Own_Schema()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
            public record Strict(string Id);

            [RivetType]
            public sealed record Lenient(string Id, string Note) : Strict(Id);

            [RivetType]
            [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
            public sealed record Skipping(string Id);

            [RivetType]
            [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
            [JsonDerivedType(typeof(Circle), "circle")]
            public abstract record Shape;

            [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
            public sealed record Circle(double Radius) : Shape;
            """;

        using var document = CompilationHelper.EmitOpenApi(source);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.False(
            schemas.GetProperty("Strict").GetProperty("additionalProperties").GetBoolean()
        );
        Assert.False(schemas.GetProperty("Lenient").TryGetProperty("additionalProperties", out _));
        Assert.False(schemas.GetProperty("Skipping").TryGetProperty("additionalProperties", out _));
        var circle = schemas.GetProperty("Shape_Circle");
        Assert.False(circle.GetProperty("additionalProperties").GetBoolean());
        Assert.True(circle.GetProperty("properties").TryGetProperty("kind", out _));
    }

    [Fact]
    public void Disallow_Beside_A_Bag_Is_Refused()
    {
        var source = """
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
            public sealed record Holder(string Id)
            {
                [JsonExtensionData]
                public Dictionary<string, JsonElement> Extra { get; init; } = [];
            }
            """;

        var refusal = WalkRefusal(source);

        Assert.StartsWith($"error {Diagnostics.InvalidJsonExtensionData}:", refusal.Message);
        Assert.Contains("'Holder.Extra'", refusal.Message);
        Assert.Contains("Disallow", refusal.Message);
    }

    // ========== Required members that may be omitted (G.9) ==========

    [Theory]
    [InlineData("[JsonRequired, RivetOptional]", "", "[JsonRequired]", "[RivetOptional]")]
    [InlineData("[RivetOptional]", "required ", "required", "[RivetOptional]")]
    [InlineData(
        "[JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]",
        "",
        "[JsonRequired]",
        "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]"
    )]
    [InlineData(
        "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]",
        "required ",
        "required",
        "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]"
    )]
    public void A_Required_Member_That_May_Be_Omitted_Is_Refused(
        string attributes,
        string modifier,
        string demanded,
        string omittable
    )
    {
        var source = $$"""
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed class Holder
            {
                {{attributes}}
                public {{modifier}}string? Note { get; init; }
            }
            """;

        var refusal = WalkRefusal(source);

        Assert.StartsWith($"error {Diagnostics.RequiredMemberMayBeOmitted}:", refusal.Message);
        Assert.Contains($"'Holder.Note' is {demanded} but also {omittable}", refusal.Message);
        Assert.Contains($"Drop {demanded}", refusal.Message);
        Assert.Contains($"or drop {omittable}", refusal.Message);
    }

    [Fact]
    public void A_Required_Member_That_Is_Always_Written_Stays_Required()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed class Holder
            {
                [JsonRequired]
                public string? Note { get; init; }

                public required string Name { get; init; }
            }
            """;

        var holder = Schema(source, "Holder");

        Assert.Equal(
            ["note", "name"],
            holder.GetProperty("required").EnumerateArray().Select(name => name.GetString())
        );
    }

    // ========== Generated schema names (G.2) ==========

    [Fact]
    public void A_Walked_Type_Shadowing_A_Generated_Schema_Is_Refused()
    {
        var source = """
            using Rivet;

            [assembly: RivetGeneratedSchema("Code", "code", "string", null, false, "{}")]

            namespace Test;

            [RivetType]
            public sealed record Code(string Value);
            """;

        var refusal = WalkRefusal(source);

        Assert.StartsWith($"error {Diagnostics.TypeNameCollision}:", refusal.Message);
        Assert.Contains("the generated schema 'Code' (component 'code')", refusal.Message);
        Assert.Contains("'Test.Code'", refusal.Message);
    }

    // An isEnum entry is the walked enum's own metadata, so it must name one.
    [Theory]
    [InlineData("", "the compilation has no type of that name")]
    [InlineData(
        "[RivetType] public sealed record State(string Value);",
        "'Test.State' isn't an enum"
    )]
    public void Enum_Metadata_Naming_No_Enum_Is_Refused(string declaration, string found)
    {
        var source = $$"""
            using Rivet;

            [assembly: RivetGeneratedSchema("State", "state", "string", null, false, "{}", true)]

            namespace Test;

            {{declaration}}
            """;

        var refusal = WalkRefusal(source);

        Assert.StartsWith($"error {Diagnostics.MalformedGeneratedMetadata}:", refusal.Message);
        Assert.Contains(found, refusal.Message);
    }

    [Fact]
    public void Enum_Metadata_Joins_The_Enum_Of_Its_Name()
    {
        var source = """
            using Rivet;

            [assembly: RivetGeneratedSchema("State", "state", "string", null, false, "{\"description\":\"Lifecycle\"}", true)]

            namespace Test;

            [RivetType]
            public enum State { Ready, Held }
            """;

        Assert.Equal("Lifecycle", Schema(source, "State").GetProperty("description").GetString());
    }
}
