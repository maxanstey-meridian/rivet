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
            new("optional", new TsType.Primitive("string"), IsOptional: true),
            new("requiredNullable", new TsType.Nullable(new TsType.Primitive("string"))),
            new(
                "optionalNullable",
                new TsType.Nullable(new TsType.Primitive("string")),
                IsOptional: true
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
            new("optional", new TsType.Primitive("string"), IsOptional: true),
        ]);

        var json = JsonSerializer.Serialize(type, _options);
        var roundTripped = Assert.IsType<TsType.InlineObject>(
            JsonSerializer.Deserialize<TsType>(json, _options)
        );

        Assert.False(roundTripped.Properties[0].IsOptional);
        Assert.True(roundTripped.Properties[1].IsOptional);
        Assert.Contains("\"optional\":false", json);
        Assert.Contains("\"optional\":true", json);
    }

    // rivet-ts leaves "optional" off an inline field; that reads as required, and a nullable
    // type doesn't change it.
    [Fact]
    public void Absent_Optional_Reads_As_Required_Even_When_Nullable()
    {
        var type = Assert.IsType<TsType.InlineObject>(
            JsonSerializer.Deserialize<TsType>(
                """
                {"kind":"inlineObject","properties":[{"name":"value","type":{"kind":"nullable","inner":{"kind":"primitive","type":"number"}}}]}
                """,
                _options
            )
        );

        Assert.False(Assert.Single(type.Properties).IsOptional);
    }

    [Fact]
    public void CanonicalHash_Distinguishes_Optionality()
    {
        var required = new TsType.InlineObject([new("value", new TsType.Primitive("string"))]);
        var optional = new TsType.InlineObject([
            new("value", new TsType.Primitive("string"), IsOptional: true),
        ]);

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(required),
            InlineTypeExtractor.CanonicalHash(optional)
        );
    }

    // Two shapes that differ only in an annotation are two schemas: sharing a hash would give
    // them one component name and fail the second with a false RIV2001.
    [Fact]
    public void CanonicalHash_Distinguishes_Annotations()
    {
        var text = new TsType.Primitive("string");
        TsType.TaggedUnion Union(TsType.TaggedUnionVariant variant) => new("kind", [variant]);
        var variant = new TsType.TaggedUnionVariant(
            "a",
            new TsType.InlineObject([new("kind", new TsType.StringUnion(["a"]))])
        );
        var stringEnum = new TsType.StringUnion(["a", "b"]);
        var intEnum = new TsType.IntUnion(["1", "2"]);
        var component = new TsTypeMetadata("Letters", TsTypeProvenance.Component);
        var nullable = new TsScalarMetadata(IsNullable: true);

        (TsType Plain, TsType Annotated)[] pairs =
        [
            (
                new TsType.InlineObject([new("value", text)]),
                new TsType.InlineObject([new("value", text, Description: "d")])
            ),
            (Union(variant), Union(variant with { Description = "d" })),
            (Union(variant), Union(variant with { DiscriminatorSchema = "{\"type\":\"string\"}" })),
            (stringEnum, stringEnum with { Description = "d" }),
            (stringEnum, stringEnum with { ScalarMetadata = nullable }),
            (stringEnum, stringEnum with { Metadata = component }),
            (stringEnum, stringEnum with { Format = "f" }),
            (stringEnum, stringEnum with { NamingPolicy = "camelCase" }),
            (intEnum, intEnum with { Description = "d" }),
            (intEnum, intEnum with { ScalarMetadata = nullable }),
            (intEnum, intEnum with { Metadata = component }),
        ];

        for (var index = 0; index < pairs.Length; index++)
        {
            Assert.True(
                InlineTypeExtractor.CanonicalHash(pairs[index].Plain)
                    != InlineTypeExtractor.CanonicalHash(pairs[index].Annotated),
                $"pair {index} hashes alike"
            );
        }
    }

    [Fact]
    public void CanonicalHash_Distinguishes_Question_Mark_In_Name_From_Optionality()
    {
        var questionMarkInName = new TsType.InlineObject([
            new("value?", new TsType.Primitive("string")),
        ]);
        var optional = new TsType.InlineObject([
            new("value", new TsType.Primitive("string"), IsOptional: true),
        ]);

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(questionMarkInName),
            InlineTypeExtractor.CanonicalHash(optional)
        );
    }
}
