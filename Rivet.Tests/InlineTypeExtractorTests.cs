using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class InlineTypeExtractorTests
{
    [Fact]
    public void TypeRef_HashesByName()
    {
        var a = new TsType.TypeRef("Foo");
        var b = new TsType.TypeRef("Foo");
        var c = new TsType.TypeRef("Bar");

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(c));
    }

    [Fact]
    public void IdenticalInlineObjects_SameHash()
    {
        var a = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var b = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
    }

    [Fact]
    public void DifferentFieldOrder_SameHash()
    {
        var a = new TsType.InlineObject([
            ("name", new TsType.Primitive("string")),
            ("id", new TsType.Primitive("number")),
        ]);
        var b = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
    }

    [Fact]
    public void DifferentFieldNames_DifferentHash()
    {
        var a = new TsType.InlineObject([("name", new TsType.Primitive("string"))]);
        var b = new TsType.InlineObject([("label", new TsType.Primitive("string"))]);

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
    }

    [Fact]
    public void DifferentFieldTypes_DifferentHash()
    {
        var a = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);
        var b = new TsType.InlineObject([("id", new TsType.Primitive("string"))]);

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
    }

    [Fact]
    public void NestedInlineObjects_HashRecursively()
    {
        var inner = new TsType.InlineObject([
            ("street", new TsType.Primitive("string")),
            ("city", new TsType.Primitive("string")),
        ]);
        var a = new TsType.InlineObject([("address", inner)]);

        var inner2 = new TsType.InlineObject([
            ("city", new TsType.Primitive("string")),
            ("street", new TsType.Primitive("string")),
        ]);
        var b = new TsType.InlineObject([("address", inner2)]);

        // Same structure, different field order in inner — should match
        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));

        // Different inner field name — should differ
        var inner3 = new TsType.InlineObject([
            ("road", new TsType.Primitive("string")),
            ("city", new TsType.Primitive("string")),
        ]);
        var c = new TsType.InlineObject([("address", inner3)]);

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(c));
    }

    [Fact]
    public void ArrayOfInlineObject_HashesCorrectly()
    {
        var inner = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);
        var arr = new TsType.Array(inner);

        // Deterministic
        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(arr),
            InlineTypeExtractor.CanonicalHash(new TsType.Array(
                new TsType.InlineObject([("id", new TsType.Primitive("number"))]))));

        // Array wrapping changes the hash
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(arr),
            InlineTypeExtractor.CanonicalHash(inner));
    }

    [Fact]
    public void NullableInlineObject_DiffersFromNonNullable()
    {
        var inner = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);
        var nullable = new TsType.Nullable(inner);

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(nullable),
            InlineTypeExtractor.CanonicalHash(inner));
    }
}
