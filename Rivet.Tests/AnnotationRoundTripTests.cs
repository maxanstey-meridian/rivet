using System.Text.Json;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Tests that standard System.ComponentModel.DataAnnotations attributes
/// flow through all four pipeline stages: TypeWalker → JSON Schema → OpenAPI → Import round-trip.
/// </summary>
public sealed class AnnotationRoundTripTests
{
    private const string FixtureSource = """
        using System.ComponentModel.DataAnnotations;
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record ConstrainedRecord(
            [property: Required, MinLength(1), MaxLength(200)]
            string Title,

            [property: RegularExpression(@"^REF-\d+$")]
            string Reference,

            [property: Range(1, 100)]
            int Priority,

            [property: StringLength(500, MinimumLength = 10)]
            string Description,

            [property: RivetConstraints(ExclusiveMinimum = 0, MultipleOf = 0.5)]
            double Score,

            [property: EmailAddress]
            string Email,

            [property: Url]
            string Website);

        [RivetContract]
        public static class ConstrainedContract
        {
            public static readonly Define GetConstrained =
                Define.Get<ConstrainedRecord>("/api/constrained");
        }
        """;

    private static JsonElement ParseDefs(string output)
    {
        const string prefix = "= ";
        var lineStart = output.IndexOf("const $defs", StringComparison.Ordinal);
        var start = output.IndexOf(prefix, lineStart, StringComparison.Ordinal) + prefix.Length;
        var end = output.IndexOf(";\n", start, StringComparison.Ordinal);
        var json = output[start..end];
        return JsonDocument.Parse(json).RootElement;
    }

    private const string NullabilityFixture = """
        #nullable enable
        using System.ComponentModel.DataAnnotations;
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record NullabilityRecord(
            [property: Required]
            string? NullableRequired,

            string? NullableOptional,

            string NonNullableNoAttr);
        """;

    [Fact]
    public void Required_Attribute_Overrides_Nullability()
    {
        var output = CompilationHelper.EmitSchemas(NullabilityFixture);
        var defs = ParseDefs(output);
        var schema = defs.GetProperty("NullabilityRecord");

        var requiredNames = new List<string>();
        foreach (var item in schema.GetProperty("required").EnumerateArray())
            requiredNames.Add(item.GetString()!);

        // string? with [Required] → required
        Assert.Contains("nullableRequired", requiredNames);

        // string? without [Required] → NOT required
        Assert.DoesNotContain("nullableOptional", requiredNames);

        // string without [Required] → required (existing behaviour)
        Assert.Contains("nonNullableNoAttr", requiredNames);
    }

    [Fact]
    public void TypeWalker_Reads_DataAnnotations()
    {
        var compilation = CompilationHelper.CreateCompilation(FixtureSource);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var typeDef = walker.Definitions["ConstrainedRecord"];

        // Title: [MinLength(1), MaxLength(200)]
        var title = typeDef.Properties.First(p => p.Name == "title");
        Assert.NotNull(title.Constraints);
        Assert.Equal(1, title.Constraints!.MinLength);
        Assert.Equal(200, title.Constraints.MaxLength);

        // Reference: [RegularExpression(@"^REF-\d+$")]
        var reference = typeDef.Properties.First(p => p.Name == "reference");
        Assert.NotNull(reference.Constraints);
        Assert.Equal(@"^REF-\d+$", reference.Constraints!.Pattern);

        // Priority: [Range(1, 100)]
        var priority = typeDef.Properties.First(p => p.Name == "priority");
        Assert.NotNull(priority.Constraints);
        Assert.Equal(1.0, priority.Constraints!.Minimum);
        Assert.Equal(100.0, priority.Constraints.Maximum);

        // Description: [StringLength(500, MinimumLength = 10)]
        var description = typeDef.Properties.First(p => p.Name == "description");
        Assert.NotNull(description.Constraints);
        Assert.Equal(10, description.Constraints!.MinLength);
        Assert.Equal(500, description.Constraints.MaxLength);

        // Score: [RivetConstraints(ExclusiveMinimum = 0, MultipleOf = 0.5)]
        var score = typeDef.Properties.First(p => p.Name == "score");
        Assert.NotNull(score.Constraints);
        Assert.Equal(0.0, score.Constraints!.ExclusiveMinimum);
        Assert.Equal(0.5, score.Constraints.MultipleOf);

        // Email: [EmailAddress] → Format = "email"
        var email = typeDef.Properties.First(p => p.Name == "email");
        Assert.Equal("email", email.Format);
        Assert.Null(email.Constraints);

        // Website: [Url] → Format = "uri"
        var website = typeDef.Properties.First(p => p.Name == "website");
        Assert.Equal("uri", website.Format);
        Assert.Null(website.Constraints);
    }

    [Fact]
    public void JsonSchema_Emits_Constraints()
    {
        var output = CompilationHelper.EmitSchemas(FixtureSource);
        var defs = ParseDefs(output);
        var schema = defs.GetProperty("ConstrainedRecord");
        var props = schema.GetProperty("properties");

        // Title constraints
        var title = props.GetProperty("title");
        Assert.Equal(1, title.GetProperty("minLength").GetInt32());
        Assert.Equal(200, title.GetProperty("maxLength").GetInt32());

        // Title is in required array
        var required = schema.GetProperty("required");
        var requiredNames = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredNames.Add(item.GetString()!);
        Assert.Contains("title", requiredNames);

        // Reference pattern
        var reference = props.GetProperty("reference");
        Assert.Equal(@"^REF-\d+$", reference.GetProperty("pattern").GetString());

        // Priority range
        var priority = props.GetProperty("priority");
        Assert.Equal(1, priority.GetProperty("minimum").GetDouble());
        Assert.Equal(100, priority.GetProperty("maximum").GetDouble());

        // Description string length
        var description = props.GetProperty("description");
        Assert.Equal(10, description.GetProperty("minLength").GetInt32());
        Assert.Equal(500, description.GetProperty("maxLength").GetInt32());

        // Score exotic constraints
        var score = props.GetProperty("score");
        Assert.Equal(0.0, score.GetProperty("exclusiveMinimum").GetDouble());
        Assert.Equal(0.5, score.GetProperty("multipleOf").GetDouble());

        // Email: format = "email"
        var email = props.GetProperty("email");
        Assert.Equal("email", email.GetProperty("format").GetString());

        // Website: format = "uri"
        var website = props.GetProperty("website");
        Assert.Equal("uri", website.GetProperty("format").GetString());
    }

    [Fact]
    public void OpenApi_Emits_Constraints()
    {
        var compilation = CompilationHelper.CreateCompilation(FixtureSource);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var openApiJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var doc = JsonSerializer.Deserialize<JsonElement>(openApiJson);

        var props = doc.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ConstrainedRecord")
            .GetProperty("properties");

        // Title
        var title = props.GetProperty("title");
        Assert.Equal(1, title.GetProperty("minLength").GetInt32());
        Assert.Equal(200, title.GetProperty("maxLength").GetInt32());

        // Reference
        var reference = props.GetProperty("reference");
        Assert.Equal(@"^REF-\d+$", reference.GetProperty("pattern").GetString());

        // Priority
        var priority = props.GetProperty("priority");
        Assert.Equal(1, priority.GetProperty("minimum").GetDouble());
        Assert.Equal(100, priority.GetProperty("maximum").GetDouble());

        // Description
        var description = props.GetProperty("description");
        Assert.Equal(10, description.GetProperty("minLength").GetInt32());
        Assert.Equal(500, description.GetProperty("maxLength").GetInt32());

        // Score — OpenAPI 3.0: exclusiveMinimum is boolean, value goes in minimum
        var score = props.GetProperty("score");
        Assert.Equal(0.0, score.GetProperty("minimum").GetDouble());
        Assert.True(score.GetProperty("exclusiveMinimum").GetBoolean());
        Assert.Equal(0.5, score.GetProperty("multipleOf").GetDouble());

        // Email: format = "email"
        var email = props.GetProperty("email");
        Assert.Equal("email", email.GetProperty("format").GetString());

        // Website: format = "uri"
        var website = props.GetProperty("website");
        Assert.Equal("uri", website.GetProperty("format").GetString());
    }

    [Fact]
    public void Import_RoundTrip_Preserves_Constraints()
    {
        // Forward: C# → OpenAPI
        var compilation = CompilationHelper.CreateCompilation(FixtureSource);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var openApiJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        // Reverse: OpenAPI → import → compile → walk
        var importResult = OpenApiImporter.Import(openApiJson, new ImportOptions("RoundTrip"));
        var recompilation = CompilationHelper.CompileImportResult(importResult);
        var (_, rewalker) = CompilationHelper.DiscoverAndWalk(recompilation);

        var typeDef = rewalker.Definitions["ConstrainedRecord"];

        // Title: minLength=1, maxLength=200
        var title = typeDef.Properties.First(p => p.Name == "title");
        Assert.NotNull(title.Constraints);
        Assert.Equal(1, title.Constraints!.MinLength);
        Assert.Equal(200, title.Constraints.MaxLength);

        // Reference: pattern
        var reference = typeDef.Properties.First(p => p.Name == "reference");
        Assert.NotNull(reference.Constraints);
        Assert.Equal(@"^REF-\d+$", reference.Constraints!.Pattern);

        // Priority: minimum=1, maximum=100
        var priority = typeDef.Properties.First(p => p.Name == "priority");
        Assert.NotNull(priority.Constraints);
        Assert.Equal(1.0, priority.Constraints!.Minimum);
        Assert.Equal(100.0, priority.Constraints.Maximum);

        // Description: minLength=10, maxLength=500
        var description = typeDef.Properties.First(p => p.Name == "description");
        Assert.NotNull(description.Constraints);
        Assert.Equal(10, description.Constraints!.MinLength);
        Assert.Equal(500, description.Constraints.MaxLength);

        // Score: exclusiveMinimum=0, multipleOf=0.5
        var score = typeDef.Properties.First(p => p.Name == "score");
        Assert.NotNull(score.Constraints);
        Assert.Equal(0.0, score.Constraints!.ExclusiveMinimum);
        Assert.Equal(0.5, score.Constraints.MultipleOf);

        // Email: [EmailAddress] → format preserved via DA attribute round-trip
        var email = typeDef.Properties.First(p => p.Name == "email");
        Assert.Equal("email", email.Format);

        // Website: [Url] → format absorbed into C# Uri type on import.
        // Property-level Format is null, but the TsType.Primitive carries format = "uri".
        var website = typeDef.Properties.First(p => p.Name == "website");
        var websitePrimitive = Assert.IsType<TsType.Primitive>(website.Type);
        Assert.Equal("uri", websitePrimitive.Format);
    }

    [Fact]
    public void SingleSided_Minimum_Does_Not_Leak_Maximum_Into_OpenApi()
    {
        // CSharpWriter emits [RangeAttribute(0, double.MaxValue)] for single-sided minimum.
        // TypeWalker must filter the sentinel so OpenAPI only gets "minimum": 0, no "maximum".
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SingleMinDto(
                [property: RangeAttribute(0, double.MaxValue)]
                double Score);

            [RivetContract]
            public static class SingleMinContract
            {
                public static readonly Define GetSingleMin =
                    Define.Get<SingleMinDto>("/api/single-min");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        // TypeWalker should read minimum=0, maximum=null
        var typeDef = walker.Definitions["SingleMinDto"];
        var score = typeDef.Properties.First(p => p.Name == "score");
        Assert.NotNull(score.Constraints);
        Assert.Equal(0.0, score.Constraints!.Minimum);
        Assert.Null(score.Constraints.Maximum);

        // OpenAPI should have "minimum": 0 but no "maximum"
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var openApiJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(openApiJson);

        var prop = doc.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("SingleMinDto")
            .GetProperty("properties")
            .GetProperty("score");

        Assert.Equal(0.0, prop.GetProperty("minimum").GetDouble());
        Assert.False(prop.TryGetProperty("maximum", out _),
            "Single-sided minimum must not leak a sentinel maximum into OpenAPI");
    }

    [Fact]
    public void SingleSided_Maximum_Does_Not_Leak_Minimum_Into_OpenApi()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SingleMaxDto(
                [property: RangeAttribute(double.MinValue, 100)]
                double Score);

            [RivetContract]
            public static class SingleMaxContract
            {
                public static readonly Define GetSingleMax =
                    Define.Get<SingleMaxDto>("/api/single-max");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        // TypeWalker should read maximum=100, minimum=null
        var typeDef = walker.Definitions["SingleMaxDto"];
        var score = typeDef.Properties.First(p => p.Name == "score");
        Assert.NotNull(score.Constraints);
        Assert.Null(score.Constraints!.Minimum);
        Assert.Equal(100.0, score.Constraints.Maximum);

        // OpenAPI should have "maximum": 100 but no "minimum"
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var openApiJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(openApiJson);

        var prop = doc.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("SingleMaxDto")
            .GetProperty("properties")
            .GetProperty("score");

        Assert.Equal(100.0, prop.GetProperty("maximum").GetDouble());
        Assert.False(prop.TryGetProperty("minimum", out _),
            "Single-sided maximum must not leak a sentinel minimum into OpenAPI");
    }
}
