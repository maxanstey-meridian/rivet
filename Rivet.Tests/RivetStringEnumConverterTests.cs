using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// The Rivet*EnumConverter family — the string-wire enum converter whose class
/// name declares the casing convention. Rules: plain enums are numeric; a
/// built-in [JsonConverter(typeof(JsonStringEnumConverter<...>))] emits exact
/// C# member names; a family converter cases those names; a per-member
/// [JsonStringEnumMemberName] wins over the policy; a casing collision degrades
/// loudly to numeric. The emitted string union must equal the runtime wire the
/// same converter produces — asserted side-by-side here, since the test host
/// references both the tool and the runtime converters.
/// </summary>
public sealed class RivetStringEnumConverterTests
{
    // ---------------------------------------------------------------
    // Wire parity: emitted contract values == runtime serializer values
    // ---------------------------------------------------------------

    [JsonConverter(typeof(RivetCamelCaseEnumConverter<StreetClassification>))]
    public enum StreetClassification
    {
        Unknown,
        RestrictedByway,
        Route2,
        RestrictedByway2A,
    }

    [JsonConverter(typeof(RivetSnakeCaseEnumConverter<StreetClassificationSnake>))]
    public enum StreetClassificationSnake
    {
        Unknown,
        RestrictedByway,
        Route2,
        RestrictedByway2A,
    }

    [JsonConverter(typeof(RivetKebabCaseEnumConverter<StreetClassificationKebab>))]
    public enum StreetClassificationKebab
    {
        Unknown,
        RestrictedByway,
        Route2,
        RestrictedByway2A,
    }

    [JsonConverter(typeof(RivetLowerCaseEnumConverter<StreetClassificationLower>))]
    public enum StreetClassificationLower
    {
        Unknown,
        RestrictedByway,
        Route2,
        RestrictedByway2A,
    }

    private const string SourceTemplate = """
        using System.Text.Json.Serialization;
        using Rivet;

        [JsonConverter(typeof({0}<StreetClassification>))]
        public enum StreetClassification
        {{
            Unknown,
            RestrictedByway,
            Route2,
            RestrictedByway2A,
        }}

        [RivetType]
        public sealed record PathDto(StreetClassification Classification);

        [RivetContract]
        public static class PathContract
        {{
            public static readonly RouteDefinition<PathDto> Get = Define.Get<PathDto>("/api/paths");
        }}
        """;

    /// <summary>
    /// The runtime wire values of the matching test enum, in declaration order.
    /// The compiled contract source and the test-host enum are separate
    /// declarations on purpose: parity means two independent declarations of the
    /// same shape produce the same wire, not that one object feeds both sides.
    /// </summary>
    private static IReadOnlyList<string> RuntimeWireValues(Type enumType) =>
        enumType
            .GetEnumValues()
            .Cast<object>()
            .Select(value => JsonSerializer.Serialize(value).Trim('"'))
            .ToList();

    [Theory]
    [InlineData("RivetCamelCaseEnumConverter", typeof(StreetClassification))]
    [InlineData("RivetSnakeCaseEnumConverter", typeof(StreetClassificationSnake))]
    [InlineData("RivetKebabCaseEnumConverter", typeof(StreetClassificationKebab))]
    [InlineData("RivetLowerCaseEnumConverter", typeof(StreetClassificationLower))]
    public void Emitted_String_Union_Equals_The_Runtime_Wire(
        string converterName,
        Type runtimeEnumType
    )
    {
        var source = string.Format(SourceTemplate, converterName);

        var (_, walker) = CompilationHelper.WalkContract(source);

        var emitted = ((TsType.StringUnion)walker.Enums["StreetClassification"]).Members;
        var runtime = RuntimeWireValues(runtimeEnumType);
        Assert.Equal(runtime, emitted);
    }

    // ---------------------------------------------------------------
    // Rule matrix
    // ---------------------------------------------------------------

    [Fact]
    public void Built_In_String_Converter_Emits_Exact_Member_Names()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            [JsonConverter(typeof(JsonStringEnumConverter<Exact>))]
            public enum Exact
            {
                DarkGreen,
                Level3,
            }

            [RivetType]
            public sealed record Dto(Exact Value);

            [RivetContract]
            public static class DtoContract
            {
                public static readonly RouteDefinition<Dto> Get = Define.Get<Dto>("/api/dtos");
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var members = ((TsType.StringUnion)walker.Enums["Exact"]).Members;
        Assert.Equal(["DarkGreen", "Level3"], members);
    }

    [Fact]
    public void Member_Pin_Overrides_The_Converter_Policy()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            [JsonConverter(typeof(RivetCamelCaseEnumConverter<Priority>))]
            public enum Priority
            {
                [JsonStringEnumMemberName("special")]
                SpecialCase,
                RegularCase,
            }

            [RivetType]
            public sealed record TaskDto(Priority Priority);

            [RivetContract]
            public static class TaskContract
            {
                public static readonly RouteDefinition<TaskDto> Get = Define.Get<TaskDto>("/api/tasks");
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var members = ((TsType.StringUnion)walker.Enums["Priority"]).Members;
        Assert.Equal(["special", "regularCase"], members);
    }

    [Fact]
    public void Family_Converter_Emits_String_Union_In_OpenApi_Schema()
    {
        var source = string.Format(SourceTemplate, "RivetCamelCaseEnumConverter");

        using var doc = CompilationHelper.EmitOpenApi(source);

        var schema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("StreetClassification");
        Assert.Equal("string", schema.GetProperty("type").GetString());
        var values = schema
            .GetProperty("enum")
            .EnumerateArray()
            .Select(v => v.GetString())
            .ToList();
        Assert.Equal(["unknown", "restrictedByway", "route2", "restrictedByway2A"], values);
    }

    // ---------------------------------------------------------------
    // Loud degradation
    // ---------------------------------------------------------------

    [Fact]
    public void Converter_Collision_Warns_And_Emits_Numeric()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            [JsonConverter(typeof(RivetCamelCaseEnumConverter<Clash>))]
            public enum Clash
            {
                FooBar,
                fooBar,
            }

            [RivetType]
            public sealed record Dto(Clash Value);

            [RivetContract]
            public static class DtoContract
            {
                public static readonly RouteDefinition<Dto> Get = Define.Get<Dto>("/api/dtos");
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            var (_, walker) = CompilationHelper.WalkContract(source);
            Assert.IsType<TsType.IntUnion>(walker.Enums["Clash"]);
        });

        Assert.Contains("warning RIV1106:", stderr);
        Assert.Contains("Clash", stderr);
        Assert.Contains("FooBar", stderr);
        Assert.Contains("fooBar", stderr);
    }

    [Fact]
    public void Duplicate_Member_Pins_Warn_And_Emit_Numeric_Without_A_Policy()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            [JsonConverter(typeof(JsonStringEnumConverter<Clash>))]
            public enum Clash
            {
                [JsonStringEnumMemberName("same")]
                First,
                [JsonStringEnumMemberName("same")]
                Second,
            }

            [RivetType]
            public sealed record Dto(Clash Value);

            [RivetContract]
            public static class DtoContract
            {
                public static readonly RouteDefinition<Dto> Get = Define.Get<Dto>("/api/dtos");
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            var (_, walker) = CompilationHelper.WalkContract(source);
            Assert.IsType<TsType.IntUnion>(walker.Enums["Clash"]);
        });

        Assert.Contains("warning RIV1106:", stderr);
        Assert.Contains("(First, Second)", stderr);
    }

    // ---------------------------------------------------------------
    // Import round-trip
    // ---------------------------------------------------------------

    [Fact]
    public void Emitted_Spec_Carries_The_Naming_Policy_Extension()
    {
        var source = string.Format(SourceTemplate, "RivetCamelCaseEnumConverter");

        using var doc = CompilationHelper.EmitOpenApi(source);

        var schema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("StreetClassification");
        Assert.Equal("camelCase", schema.GetProperty("x-rivet-enum-naming-policy").GetString());
    }

    [Fact]
    public void Imported_Policy_Enum_Uses_The_Family_Converter_And_No_Redundant_Pins()
    {
        var source = string.Format(SourceTemplate, "RivetCamelCaseEnumConverter");

        var (endpoints, walker) = CompilationHelper.WalkMerged(source);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        var imported = CompilationHelper.Import(json);
        var streetFile = CompilationHelper.FindFile(imported, "StreetClassification.cs");

        // The policy re-derives every wire value, so the imported enum keeps the
        // family converter and carries no member pins at all — the round-trip
        // preserves the declaration, not a member-by-member pin echo.
        Assert.Contains(
            "[JsonConverter(typeof(RivetCamelCaseEnumConverter<StreetClassification>))]",
            streetFile
        );
        Assert.DoesNotContain("JsonStringEnumMemberName", streetFile);
    }

    [Fact]
    public void Imported_Policy_Enum_Pins_Only_Where_The_Wire_Differs_From_The_Policy()
    {
        // A spec authored with a non-derivable wire value ("special-case" cannot
        // come from camelCasing "SpecialCase") keeps a pin for that member while
        // policy-derivable members stay pin-free — the declared fact carries, the
        // derivation fills in the rest.
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            [JsonConverter(typeof(RivetCamelCaseEnumConverter<Status>))]
            public enum Status
            {
                Unknown,
                SpecialCase,
            }

            [RivetType]
            public sealed record Dto(Status Value);

            [RivetContract]
            public static class DtoContract
            {
                public static readonly RouteDefinition<Dto> Get = Define.Get<Dto>("/api/dtos");
            }
            """;

        // Rewire SpecialCase's value to one the policy cannot derive, then import.
        var (endpoints, walker) = CompilationHelper.WalkMerged(source);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );
        var spec = JsonNode.Parse(json)!;
        spec["components"]!["schemas"]!["Status"]!["enum"]![1] = "special-case";
        json = spec.ToJsonString();

        var imported = CompilationHelper.Import(json);
        var statusFile = CompilationHelper.FindFile(imported, "Status.cs");

        Assert.Contains("[JsonConverter(typeof(RivetCamelCaseEnumConverter<Status>))]", statusFile);
        Assert.Contains("[JsonStringEnumMemberName(\"special-case\")]", statusFile);
        Assert.DoesNotContain("[JsonStringEnumMemberName(\"unknown\")]", statusFile);
    }
}
