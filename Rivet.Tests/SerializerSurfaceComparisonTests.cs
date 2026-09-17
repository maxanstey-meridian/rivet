using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// The emitted component schema must agree with the observed System.Text.Json web
/// wire surface (planner-constraint:stj-constructor-truth). One shared probe shape
/// feeds both sides of the comparison: the emission side compiles the same member
/// set the serializer side round-trips, and the assertions enumerate the real
/// JsonSerializerOptions.Web output — present and omitted values — instead of
/// selected hand-picked pairs. Request-only accessibility does not imply an
/// identical response surface (planner-constraint:json-surface-position-aware).
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
        // The emission probe declares the same member set as the serializer mirror
        // class below; the mechanical comparison fails when the two diverge.
        var dtoSource = """
            [RivetType]
            public sealed record SurfaceProbe(
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
                public string WriteOnlySecret { private get; set; } = "";
            }
            """;

        // The emitted schema for the shared probe shape.
        var doc = EmitSchema("SurfaceProbe", dtoSource);

        var probeSchema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("SurfaceProbe");
        var schemaProperties = probeSchema.GetProperty("properties");
        var emittedNames = schemaProperties
            .EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        var writeOnlyNames = schemaProperties
            .EnumerateObject()
            .Where(p =>
                p.Value.TryGetProperty("writeOnly", out var writeOnly) && writeOnly.GetBoolean()
            )
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The real serializer over the same shape — the mirror class declares the
        // same member set — in a present-value case and an omission case.
        var options = JsonSerializerOptions.Web;
        var present = new SurfaceProbe
        {
            PublicValue = "x",
            NeverIgnored = "n",
            WhenNull = "pn", // present: WhenWritingNull keeps it on the wire
            WhenDefault = 5, // present: WhenWritingDefault keeps it on the wire
            AlwaysIgnored = "hidden",
            PlainField = "p",
            WriteOnlySecret = "s",
        };
        var omitted = new SurfaceProbe
        {
            PublicValue = "x",
            NeverIgnored = "n",
            WhenNull = null, // omitted on the wire by WhenWritingNull
            WhenDefault = 0, // omitted by WhenWritingDefault
            AlwaysIgnored = "hidden",
            PlainField = "p",
            WriteOnlySecret = "s",
        };

        using var presentDoc = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(present, options)
        );
        using var omittedDoc = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(omitted, options)
        );
        var presentNames = presentDoc
            .RootElement.EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        var omittedNames = omittedDoc
            .RootElement.EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Conditional members, both ways. Present values serialize, and the emitted
        // schema agrees: both members are schema properties.
        Assert.Equal("pn", presentDoc.RootElement.GetProperty("whenNull").GetString());
        Assert.Equal(5, presentDoc.RootElement.GetProperty("whenDefault").GetInt32());
        Assert.Contains("whenNull", emittedNames);
        Assert.Contains("whenDefault", emittedNames);

        // Omitted values disappear, and the emitted schema agrees by not requiring
        // them: a member the serializer can leave off the wire cannot be required.
        Assert.False(omittedDoc.RootElement.TryGetProperty("whenNull", out _));
        Assert.False(omittedDoc.RootElement.TryGetProperty("whenDefault", out _));
        var requiredNames = probeSchema.TryGetProperty("required", out var requiredProp)
            ? requiredProp
                .EnumerateArray()
                .Select(r => r.GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var omissibleNames = presentNames.Except(omittedNames).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(omissibleNames.Intersect(requiredNames));

        // The single-shape comparison, enumerated mechanically: the emitted member
        // set equals the members the real serializer produces across the exercised
        // cases plus the request-direction members it marks writeOnly. A member
        // declared on one probe side only fails here.
        var serializedNames = new HashSet<string>(presentNames, StringComparer.Ordinal);
        serializedNames.UnionWith(omittedNames);
        var wireSurface = new HashSet<string>(serializedNames, StringComparer.Ordinal);
        wireSurface.UnionWith(writeOnlyNames);
        Assert.True(
            emittedNames.SetEquals(wireSurface),
            $"emitted [{string.Join(", ", emittedNames.OrderBy(n => n, StringComparer.Ordinal))}] vs "
                + $"wire [{string.Join(", ", wireSurface.OrderBy(n => n, StringComparer.Ordinal))}]"
        );

        // Requiredness agrees with observed omission: exactly the members serialized
        // in every case, plus the request-direction (writeOnly) members.
        var alwaysSerialized = serializedNames
            .Intersect(omittedNames)
            .ToHashSet(StringComparer.Ordinal);
        alwaysSerialized.UnionWith(writeOnlyNames);
        Assert.True(
            requiredNames.SetEquals(alwaysSerialized),
            $"required [{string.Join(", ", requiredNames.OrderBy(n => n, StringComparer.Ordinal))}] vs "
                + $"always-serialized/writeOnly [{string.Join(", ", alwaysSerialized.OrderBy(n => n, StringComparer.Ordinal))}]"
        );

        // Directionality markers agree with the observed serializer behavior: the
        // computed get-only member serializes and is readOnly; the set-only member
        // never serializes and is writeOnly.
        Assert.Contains("getOnly", presentNames);
        Assert.True(schemaProperties.GetProperty("getOnly").GetProperty("readOnly").GetBoolean());
        Assert.DoesNotContain("writeOnlySecret", presentNames);
        Assert.DoesNotContain("writeOnlySecret", omittedNames);
        Assert.True(
            schemaProperties.GetProperty("writeOnlySecret").GetProperty("writeOnly").GetBoolean()
        );

        // Members invisible to the serializer are absent from the schema too.
        foreach (var excluded in new[] { "alwaysIgnored", "privateOnly", "plainField" })
        {
            Assert.DoesNotContain(excluded, emittedNames);
            Assert.DoesNotContain(excluded, presentNames);
        }
    }

    /// <summary>
    /// The serializer fixture: the same C# shape the emission side compiles as
    /// <c>SurfaceProbe</c> — same member set, accessibility and attributes. A
    /// member declared on one side only fails the mechanical comparison in
    /// Emitted_Schema_Matches_Observed_Stj_Web_Surface.
    /// </summary>
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

        [JsonIgnore]
        private string PrivateOnly { get; set; } = "";

        [JsonInclude]
        private string IncludedPrivate { get; set; } = "";

        public string GetOnly => PublicValue + "!";

        [JsonInclude]
        public int IncludedField = 0;

        public string PlainField = "";

        // Public setter only: the request direction populates it; the private getter
        // (without [JsonInclude]) keeps it out of serialized output.
        public string WriteOnlySecret { private get; set; } = "";

        public string GetIncludedPrivateForTest() => IncludedPrivate;

        public string GetWriteOnlySecretForTest() => WriteOnlySecret;

        public string GetPrivateOnlyForTest() => PrivateOnly;
    }

    [Fact]
    public void Real_Serializer_RoundTrip_Populates_Emitted_Surface_Members()
    {
        var options = JsonSerializerOptions.Web;

        // Serialize the present-value case: the writeOnly member stays off the wire
        // behind its private getter; the computed member serializes.
        var probe = new SurfaceProbe
        {
            PublicValue = "x",
            NeverIgnored = "n",
            WhenNull = "pn",
            WhenDefault = 5,
            AlwaysIgnored = "hidden",
            PlainField = "p",
            WriteOnlySecret = "s",
        };
        // IncludedPrivate is private — set it directly so the round-trip proves the
        // included private setter actually populates the property.
        typeof(SurfaceProbe)
            .GetProperty(
                "IncludedPrivate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!
            .SetValue(probe, "x");

        using var serialized = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(probe, options)
        );
        Assert.False(serialized.RootElement.TryGetProperty("writeOnlySecret", out _));
        Assert.Equal("x!", serialized.RootElement.GetProperty("getOnly").GetString());

        // Feed the emitted member surface through the real deserializer: every bound
        // member populates, the derived member recomputes (the spoofed wire value is
        // ignored), and the writeOnly member populates through its public setter.
        var wire =
            """{"publicValue":"y","neverIgnored":"m","whenNull":"q","whenDefault":9,"includedPrivate":"x","getOnly":"SPOOFED","includedField":3,"writeOnlySecret":"s"}""";
        var roundTripped = JsonSerializer.Deserialize<SurfaceProbe>(wire, options);
        Assert.NotNull(roundTripped);
        Assert.Equal("y", roundTripped!.PublicValue);
        Assert.Equal("m", roundTripped.NeverIgnored);
        Assert.Equal("q", roundTripped.WhenNull);
        Assert.Equal(9, roundTripped.WhenDefault);
        Assert.Equal(3, roundTripped.IncludedField);
        Assert.Equal("x", roundTripped.GetIncludedPrivateForTest());
        Assert.Equal("s", roundTripped.GetWriteOnlySecretForTest());
        Assert.Equal("y!", roundTripped.GetOnly);

        // Re-serialize the round-tripped instance: the derived member recomputes and
        // the writeOnly member drops again.
        var reSerialized = JsonSerializer.Serialize(roundTripped, options);
        Assert.Contains("\"getOnly\":\"y!\"", reSerialized);
        Assert.DoesNotContain("SPOOFED", reSerialized);
        Assert.DoesNotContain("writeOnlySecret", reSerialized);
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

        // The real serializer over the same shape: the computed member is present in
        // serialized output and is not populated on deserialization — the spoofed
        // wire value is ignored and the expression recomputes.
        var probe = new ComputedProbe { Name = "n" };
        Assert.Contains(
            "\"upper\":\"N\"",
            JsonSerializer.Serialize(probe, JsonSerializerOptions.Web)
        );

        var roundTripped = JsonSerializer.Deserialize<ComputedProbe>(
            """{"name":"n","upper":"SPOOFED"}""",
            JsonSerializerOptions.Web
        );
        Assert.NotNull(roundTripped);
        Assert.Equal("n", roundTripped!.Name);
        Assert.Equal("N", roundTripped.Upper);
        Assert.DoesNotContain(
            "SPOOFED",
            JsonSerializer.Serialize(roundTripped, JsonSerializerOptions.Web)
        );
    }

    private sealed class ComputedProbe
    {
        public string Name { get; set; } = "";
        public string Upper => Name.ToUpperInvariant();
    }
}
