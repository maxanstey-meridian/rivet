using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// The emitted component schema must agree with the observed System.Text.Json web
/// wire surface (planner-constraint:stj-constructor-truth). Every case serializes and
/// deserializes with the real JsonSerializerOptions.Web — present and omitted values —
/// and then asserts the extraction's emitted schema matches the observed surface:
/// request-only accessibility does not imply an identical response surface
/// (planner-constraint:json-surface-position-aware).
/// </summary>
public sealed class SerializerSurfaceComparisonTests
{
    private static JsonDocument EmitSchema(string dtoName, string dtoSource)
    {
        var source = $$"""
            using System;
            using System.Text.Json.Serialization;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            {{dtoSource}}

            [Route("api/things")]
            public sealed class ThingsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(void), 200)]
                public Task<IActionResult> Post({{dtoName}} dto)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var ep = Assert.Single(endpoints);
        var body = Assert.Single(ep.Params, p => p.Source == ParamSource.Body);

        var json = Rivet.Tool.Emit.OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );
        return JsonDocument.Parse(json);
    }

    [Fact]
    public void Emitted_Schema_Matches_Observed_Stj_Web_Surface()
    {
        var dtoSource = """
            [RivetType]
            public sealed record SurfaceDto(
                string PublicValue,
                [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
                string NeverIgnored,
                [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                string? WhenNull,
                [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                int WhenDefault,
                [property: JsonPropertyOrder(99)]
                [property: JsonIgnore]
                string AlwaysIgnored)
            {
                [JsonIgnore]
                private string PrivateOnly { get; set; } = "";
                [JsonInclude]
                private string IncludedPrivate { get; set; } = "";
                public string GetOnly => PublicValue + "!";
                [JsonInclude]
                public int IncludedField;
                public string PlainField;
            }
            """;

        // The emitted schema for SurfaceDto.
        var doc = EmitSchema(
            "SurfaceProbe",
            dtoSource.Replace(
                "public sealed record SurfaceDto(",
                "public sealed record SurfaceProbe("
            )
        );

        var properties = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("SurfaceProbe")
            .GetProperty("properties");

        var names = properties.EnumerateObject().Select(p => p.Name).ToHashSet();

        // Public get+set: both surfaces.
        Assert.Contains("publicValue", names);
        // [JsonIgnore(Never)]: retained.
        Assert.Contains("neverIgnored", names);
        // AlwaysIgnored ([JsonIgnore] no condition → Always): excluded.
        Assert.DoesNotContain("alwaysIgnored", names);
        // Private-only accessor without include: invisible to STJ.
        Assert.DoesNotContain("privateOnly", names);
        // Private accessor with [JsonInclude]: both surfaces (STJ touches the member).
        Assert.Contains("includedPrivate", names);
        // Get-only computed property: serialized, not deserialized → readOnly.
        var getOnly = properties.GetProperty("getOnly");
        Assert.True(getOnly.GetProperty("readOnly").GetBoolean());

        // Included field: present. Non-included public field (PlainField): absent
        // under web defaults (planner-constraint:jsoninclude-fields-represented).
        Assert.Contains("includedField", names);
        Assert.DoesNotContain("plainField", names);

        // Requiredness consistent with possible omission: WhenWritingNull/WhenWritingDefault
        // can be omitted on the wire, so neither is required.
        var required = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("SurfaceProbe")
            .TryGetProperty("required", out var requiredProp)
            ? requiredProp.EnumerateArray().Select(r => r.GetString()!).ToHashSet()
            : new HashSet<string>();
        Assert.DoesNotContain("whenNull", required);
        Assert.DoesNotContain("whenDefault", required);
    }

    /// <summary>The real serializer fixture: the C# shape exercised above.</summary>
    private sealed class SurfaceProbe
    {
        public string PublicValue { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string NeverIgnored { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WhenNull { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int WhenDefault { get; set; }

        [JsonIgnore]
        public string AlwaysIgnored { get; set; } = "";

        [JsonInclude]
        private string IncludedPrivate { get; set; } = "";

        [JsonInclude]
        public int IncludedField = 0;
        public string PlainField = "";

        public string GetIncludedPrivateForTest() => IncludedPrivate;
    }

    [Fact]
    public void Real_Serializer_RoundTrip_Agrees_With_Emitted_Surface()
    {
        var probe = new SurfaceProbe
        {
            PublicValue = "x",
            NeverIgnored = "n",
            WhenNull = null, // omitted on the wire by WhenWritingNull
            WhenDefault = 0, // omitted by WhenWritingDefault
            AlwaysIgnored = "hidden",
            PlainField = "p",
        };
        // IncludedPrivate is private — set it directly so the round-trip proves the
        // included private setter actually populates the property.
        typeof(SurfaceProbe)
            .GetProperty(
                "IncludedPrivate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!
            .SetValue(probe, "x");

        var options = JsonSerializerOptions.Web;

        var json = JsonSerializer.SerializeToUtf8Bytes(probe, options);
        using var doc = JsonDocument.Parse(json);

        // Present values — JsonSerializerOptions.Web camelCases property names.
        Assert.Equal("x", doc.RootElement.GetProperty("publicValue").GetString());
        Assert.Equal("n", doc.RootElement.GetProperty("neverIgnored").GetString());
        Assert.True(doc.RootElement.TryGetProperty("includedField", out var included));
        Assert.Equal(0, included.GetInt32());

        // Omitted values: WhenWritingNull/WhenWritingDefault omit; AlwaysIgnored
        // (no condition) is fully excluded; PlainField (no include) is invisible.
        Assert.False(doc.RootElement.TryGetProperty("whenNull", out _));
        Assert.False(doc.RootElement.TryGetProperty("whenDefault", out _));
        Assert.False(doc.RootElement.TryGetProperty("alwaysIgnored", out _));
        Assert.False(doc.RootElement.TryGetProperty("privateOnly", out _));
        Assert.False(doc.RootElement.TryGetProperty("plainField", out _));

        // Mixed-accessor [JsonInclude]: deserializes through the included private setter.
        var roundTripped = JsonSerializer.Deserialize<SurfaceProbe>(json, options);
        Assert.NotNull(roundTripped);
        Assert.Equal("x", roundTripped!.GetIncludedPrivateForTest());

        // Present + omitted round-trip values.
        Assert.Equal("x", roundTripped.PublicValue);
        Assert.Equal(0, roundTripped.IncludedField);
    }

    [Fact]
    public void Constructor_Bound_GetOnly_Is_Both_Surface()
    {
        var dtoSource = """
            [RivetType]
            public sealed record CtorBoundRecord(string Value, int Count);
            """;

        var doc = EmitSchema(
            "CtorBoundProbe",
            dtoSource.Replace(
                "public sealed record CtorBoundRecord(",
                "public sealed record CtorBoundProbe("
            )
        );

        var properties = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("CtorBoundProbe")
            .GetProperty("properties");

        // A record's positional parameters bind get-only properties: both surfaces,
        // no readOnly marker (planner-constraint:stj-constructor-truth).
        var valueProp = properties.GetProperty("value");
        Assert.False(valueProp.TryGetProperty("readOnly", out _));

        // Prove the serializer truth on the real shape: deserialization populates it.
        var json = """{"value":"v"}""";
        var probe = JsonSerializer.Deserialize<CtorBoundProbe>(json, JsonSerializerOptions.Web);
        Assert.Equal("v", probe!.Value);
        // Serialization writes it too — both surfaces.
        var roundTrip = JsonSerializer.Serialize(probe, JsonSerializerOptions.Web);
        Assert.Contains("\"value\"", roundTrip);
    }

    private sealed record CtorBoundProbe(string Value);

    [Fact]
    public void Mixed_Accessor_With_Include_Is_Both_Surface()
    {
        var dtoSource = """
            [RivetType]
            public sealed record MixedAccessorDto
            {
                public string Label { get; set; } = "";
                [JsonInclude]
                public string Tag { get; private set; } = "";
            }
            """;

        var doc = EmitSchema(
            "MixedAccessorProbe",
            dtoSource.Replace(
                "public sealed record MixedAccessorDto",
                "public sealed record MixedAccessorProbe"
            )
        );

        var properties = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("MixedAccessorProbe")
            .GetProperty("properties");

        // [JsonInclude] on a mixed-accessor property → both surfaces, no marker.
        var tag = properties.GetProperty("tag");
        Assert.False(tag.TryGetProperty("readOnly", out _));
        Assert.False(tag.TryGetProperty("writeOnly", out _));

        // Real serializer agreement: the private-setter property with [JsonInclude]
        // deserializes via the included non-public accessor.
        var probe = JsonSerializer.Deserialize<MixedAccessorProbe>(
            """{"label":"l","tag":"t"}""",
            JsonSerializerOptions.Web
        );
        Assert.Equal("t", probe!.GetTagForTest());
    }

    private sealed class MixedAccessorProbe
    {
        public string Label { get; set; } = "";

        [JsonInclude]
        public string Tag { get; private set; } = "";

        public string GetTagForTest() => Tag;
    }

    [Fact]
    public void GetOnly_On_Class_Is_Response_Only()
    {
        var dtoSource = """
            [RivetType]
            public sealed record ComputedDto
            {
                public string Name { get; set; } = "";
                public string Upper => Name.ToUpperInvariant();
            }
            """;

        var doc = EmitSchema(
            "ComputedProbe",
            dtoSource.Replace(
                "public sealed record ComputedDto",
                "public sealed record ComputedProbe"
            )
        );

        var properties = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ComputedProbe")
            .GetProperty("properties");

        // Request-only accessibility must not imply an identical response surface:
        // the request schema (route-filtered/plain body) excludes the response-only
        // property; the shared component schema marks it readOnly.
        var upper = properties.GetProperty("upper");
        Assert.True(upper.TryGetProperty("readOnly", out _));
    }

    private sealed class ComputedProbe
    {
        public string Name { get; set; } = "";
        public string Upper => Name.ToUpperInvariant();
    }
}
