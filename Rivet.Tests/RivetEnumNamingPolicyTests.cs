using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// [RivetEnumNamingPolicy] — the emission-only marker naming the casing
/// convention for enum wire values on string-wire enums. Rules: plain enums are
/// numeric; a string converter alone emits exact C# member names; the marker
/// cases those names; a per-member [JsonStringEnumMemberName] wins over the
/// policy; a dangling marker or a policy collision degrades loudly to numeric.
/// </summary>
public sealed class RivetEnumNamingPolicyTests
{
    private const string StreetSourceTemplate = """
        using System.Text.Json.Serialization;
        using Rivet;

        [JsonConverter(typeof(JsonStringEnumConverter<StreetClassification>))]
        [RivetEnumNamingPolicy(RivetNamingPolicy.{0})]
        public enum StreetClassification
        {{
            Unknown,
            RestrictedByway,
            Route2,
        }}

        [RivetType]
        public sealed record PathDto(StreetClassification Classification);

        [RivetContract]
        public static class PathContract
        {{
            public static readonly RouteDefinition<PathDto> Get = Define.Get<PathDto>("/api/paths");
        }}
        """;

    [Theory]
    [InlineData(RivetNamingPolicy.LowerCase, "restrictedbyway", "route2")]
    [InlineData(RivetNamingPolicy.CamelCase, "restrictedByway", "route2")]
    [InlineData(RivetNamingPolicy.SnakeCase, "restricted_byway", "route_2")]
    [InlineData(RivetNamingPolicy.KebabCase, "restricted-byway", "route-2")]
    public void Policy_Cases_Member_Names_Per_Declared_Policy(
        RivetNamingPolicy policy,
        string byway,
        string route2
    )
    {
        var source = string.Format(StreetSourceTemplate, policy);

        var (_, walker) = CompilationHelper.WalkContract(source);

        var members = ((TsType.StringUnion)walker.Enums["StreetClassification"]).Members;
        Assert.Equal(["unknown", byway, route2], members);
    }

    [Fact]
    public void Member_Attribute_Overrides_The_Policy()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            [JsonConverter(typeof(JsonStringEnumConverter<Priority>))]
            [RivetEnumNamingPolicy(RivetNamingPolicy.CamelCase)]
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
    public void Policy_Emits_String_Union_In_OpenApi_Schema()
    {
        var source = string.Format(StreetSourceTemplate, RivetNamingPolicy.CamelCase);

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
        Assert.Equal(["unknown", "restrictedByway", "route2"], values);
    }

    [Fact]
    public void Marker_Without_String_Converter_Warns_And_Emits_Numeric()
    {
        var source = """
            using Rivet;

            [RivetEnumNamingPolicy(RivetNamingPolicy.CamelCase)]
            public enum Bare
            {
                First,
                Second,
            }

            [RivetType]
            public sealed record Dto(Bare Value);

            [RivetContract]
            public static class DtoContract
            {
                public static readonly RouteDefinition<Dto> Get = Define.Get<Dto>("/api/dtos");
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            var (_, walker) = CompilationHelper.WalkContract(source);
            Assert.IsType<TsType.IntUnion>(walker.Enums["Bare"]);
        });

        Assert.Contains("warning RIV1105:", stderr);
        Assert.Contains("Bare", stderr);
    }

    [Fact]
    public void Policy_Collision_Warns_And_Emits_Numeric()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            [JsonConverter(typeof(JsonStringEnumConverter<Clash>))]
            [RivetEnumNamingPolicy(RivetNamingPolicy.CamelCase)]
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
    public void Duplicate_Member_Pins_Warn_And_Emit_Numeric_Without_The_Marker()
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
        Assert.DoesNotContain("RivetEnumNamingPolicy", stderr);
    }

    [Fact]
    public void Policy_Cased_Values_Survive_Import_As_Explicit_Member_Pins()
    {
        var source = string.Format(StreetSourceTemplate, RivetNamingPolicy.CamelCase);

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

        Assert.Contains(
            "[JsonConverter(typeof(JsonStringEnumConverter<StreetClassification>))]",
            streetFile
        );
        Assert.Contains("[JsonStringEnumMemberName(\"unknown\")]", streetFile);
        Assert.Contains("[JsonStringEnumMemberName(\"restrictedByway\")]", streetFile);
        Assert.Contains("[JsonStringEnumMemberName(\"route2\")]", streetFile);
    }
}
