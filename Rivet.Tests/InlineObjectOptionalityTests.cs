using System.Text.Json;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class InlineObjectOptionalityTests
{
    private static readonly JsonSerializerOptions _options = JsonContractReader.Options;

    [Fact]
    public void OpenApi_Preserves_Optional_And_Nullable_As_Independent_Axes()
    {
        var type = new TsType.InlineObject([
            new("required", new TsType.Primitive("string")),
            new("optional", new TsType.Primitive("string"), Optional: true),
            new("requiredNullable", new TsType.Nullable(new TsType.Primitive("string"))),
            new(
                "optionalNullable",
                new TsType.Nullable(new TsType.Primitive("string")),
                Optional: true
            ),
        ]);

        var schema = OpenApiEmitterTests.ResponseSchemaOf(type);

        Assert.Equal(
            ["required", "requiredNullable"],
            schema.GetProperty("required").EnumerateArray().Select(value => value.GetString())
        );
        Assert.Equal(
            ["string", "null"],
            schema
                .GetProperty("properties")
                .GetProperty("requiredNullable")
                .GetProperty("type")
                .EnumerateArray()
                .Select(value => value.GetString())
        );
    }

    [Fact]
    public void Json_RoundTrip_Writes_Explicit_Optionality()
    {
        TsType type = new TsType.InlineObject([
            new("requiredNullable", new TsType.Nullable(new TsType.Primitive("string"))),
            new("optional", new TsType.Primitive("string"), Optional: true),
        ]);

        var json = JsonSerializer.Serialize(type, _options);
        var roundTripped = Assert.IsType<TsType.InlineObject>(
            JsonSerializer.Deserialize<TsType>(json, _options)
        );

        Assert.False(roundTripped.Fields[0].Optional);
        Assert.True(roundTripped.Fields[1].Optional);
        Assert.Contains("\"optional\":false", json);
        Assert.Contains("\"optional\":true", json);
    }

    [Fact]
    public void Tuple_Syntax_Does_Not_Infer_Optionality_From_Nullability()
    {
        TsType.InlineObjectField field = (
            "value",
            new TsType.Nullable(new TsType.Primitive("number"))
        );

        Assert.False(field.Optional);
    }

    [Fact]
    public void CanonicalHash_Distinguishes_Optionality()
    {
        var required = new TsType.InlineObject([new("value", new TsType.Primitive("string"))]);
        var optional = new TsType.InlineObject([
            new("value", new TsType.Primitive("string"), Optional: true),
        ]);

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(required),
            InlineTypeExtractor.CanonicalHash(optional)
        );
    }

    [Fact]
    public void CanonicalHash_Distinguishes_Question_Mark_In_Name_From_Optionality()
    {
        var questionMarkInName = new TsType.InlineObject([
            new("value?", new TsType.Primitive("string")),
        ]);
        var optional = new TsType.InlineObject([
            new("value", new TsType.Primitive("string"), Optional: true),
        ]);

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(questionMarkInName),
            InlineTypeExtractor.CanonicalHash(optional)
        );
    }
}
