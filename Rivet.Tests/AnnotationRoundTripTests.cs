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
            double Score);

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
    }
}
