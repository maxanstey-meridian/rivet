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

    private static TsEndpointDefinition MakeEndpoint(
        string controller, string name, TsType? returnType = null,
        IReadOnlyList<TsResponseType>? responses = null,
        IReadOnlyList<TsEndpointParam>? parameters = null) =>
        new(name, "GET", $"/{controller}/{name}",
            parameters ?? [],
            returnType, controller,
            responses ?? []);

    [Fact]
    public void CollectsFromReturnType()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var endpoint = MakeEndpoint("Buyers", "find", returnType: inline);

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Single(result);
        Assert.Same(inline, result[0].Type);
        Assert.Equal("Buyers.find.return", result[0].Context);
    }

    [Fact]
    public void CollectsFromNestedArray()
    {
        var inline = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);
        var endpoint = MakeEndpoint("Items", "list",
            returnType: new TsType.Array(inline));

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Single(result);
        Assert.Same(inline, result[0].Type);
        Assert.Equal("Items.list.return", result[0].Context);
    }

    [Fact]
    public void CollectsFromNullable()
    {
        var inline = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);
        var endpoint = MakeEndpoint("Items", "find",
            returnType: new TsType.Nullable(inline));

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Single(result);
        Assert.Same(inline, result[0].Type);
    }

    [Fact]
    public void MultipleEndpoints_CollectsAll()
    {
        var inline1 = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);
        var inline2 = new TsType.InlineObject([("name", new TsType.Primitive("string"))]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: inline1),
            MakeEndpoint("Orders", "list", returnType: inline2),
        };

        var result = InlineTypeExtractor.CollectInlineObjects(endpoints);

        Assert.Equal(2, result.Count);
        Assert.Equal("Buyers.find.return", result[0].Context);
        Assert.Equal("Orders.list.return", result[1].Context);
    }

    [Fact]
    public void ContextString_IncludesControllerAndEndpoint()
    {
        var inline = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);

        // Return type context
        var ep1 = MakeEndpoint("Products", "getById", returnType: inline);
        var r1 = InlineTypeExtractor.CollectInlineObjects([ep1]);
        Assert.Equal("Products.getById.return", r1[0].Context);

        // Param context
        var paramInline = new TsType.InlineObject([("title", new TsType.Primitive("string"))]);
        var ep2 = MakeEndpoint("Products", "create",
            parameters: [new TsEndpointParam("data", paramInline, ParamSource.Body)]);
        var r2 = InlineTypeExtractor.CollectInlineObjects([ep2]);
        Assert.Single(r2);
        Assert.Equal("Products.create.param.data", r2[0].Context);
    }

    [Fact]
    public void IgnoresTypeRefs()
    {
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: new TsType.TypeRef("Foo")),
            MakeEndpoint("Orders", "list", returnType: new TsType.TypeRef("Bar")),
        };

        var result = InlineTypeExtractor.CollectInlineObjects(endpoints);

        Assert.Empty(result);
    }

    [Fact]
    public void CollectsFromResponseDataType()
    {
        var inline = new TsType.InlineObject([
            ("total", new TsType.Primitive("number")),
        ]);
        var endpoint = MakeEndpoint("Orders", "list",
            responses: [new TsResponseType(200, inline)]);

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Single(result);
        Assert.Same(inline, result[0].Type);
        Assert.Equal("Orders.list.response.200", result[0].Context);
    }

    [Fact]
    public void PrimitiveWithFormat_DiffersFromWithout()
    {
        var dateTime = new TsType.Primitive("string", "date-time");
        var plainString = new TsType.Primitive("string");

        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(dateTime),
            InlineTypeExtractor.CanonicalHash(plainString));

        // Same format should match
        var dateTime2 = new TsType.Primitive("string", "date-time");
        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(dateTime),
            InlineTypeExtractor.CanonicalHash(dateTime2));
    }

    // --- Hash coverage for remaining TsType variants ---

    [Fact]
    public void Dictionary_HashesCorrectly()
    {
        var dict = new TsType.Dictionary(new TsType.Primitive("string"));
        var dict2 = new TsType.Dictionary(new TsType.Primitive("string"));
        var dictNum = new TsType.Dictionary(new TsType.Primitive("number"));

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(dict),
            InlineTypeExtractor.CanonicalHash(dict2));
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(dict),
            InlineTypeExtractor.CanonicalHash(dictNum));
    }

    [Fact]
    public void StringUnion_OrderIndependent()
    {
        var a = new TsType.StringUnion(["beta", "alpha", "gamma"]);
        var b = new TsType.StringUnion(["gamma", "alpha", "beta"]);
        var c = new TsType.StringUnion(["alpha", "delta"]);

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(c));
    }

    [Fact]
    public void IntUnion_OrderIndependent()
    {
        var a = new TsType.IntUnion([3, 1, 2]);
        var b = new TsType.IntUnion([1, 2, 3]);
        var c = new TsType.IntUnion([1, 2, 4]);

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(c));
    }

    [Fact]
    public void Generic_HashesWithTypeArguments()
    {
        var a = new TsType.Generic("PagedResult", [new TsType.TypeRef("Foo")]);
        var b = new TsType.Generic("PagedResult", [new TsType.TypeRef("Foo")]);
        var c = new TsType.Generic("PagedResult", [new TsType.TypeRef("Bar")]);
        var d = new TsType.Generic("OtherGeneric", [new TsType.TypeRef("Foo")]);

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(c));
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(d));
    }

    [Fact]
    public void TypeParam_HashesByName()
    {
        var a = new TsType.TypeParam("T");
        var b = new TsType.TypeParam("T");
        var c = new TsType.TypeParam("U");

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(c));
    }

    [Fact]
    public void Brand_HashesNameAndInner()
    {
        var a = new TsType.Brand("Email", new TsType.Primitive("string"));
        var b = new TsType.Brand("Email", new TsType.Primitive("string"));
        var c = new TsType.Brand("UserId", new TsType.Primitive("string"));
        var d = new TsType.Brand("Email", new TsType.Primitive("number"));

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(c));
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(d));
    }

    [Fact]
    public void EmptyInlineObject_HashesConsistently()
    {
        var a = new TsType.InlineObject([]);
        var b = new TsType.InlineObject([]);
        var c = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);

        Assert.Equal(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(b));
        Assert.NotEqual(
            InlineTypeExtractor.CanonicalHash(a),
            InlineTypeExtractor.CanonicalHash(c));
    }

    // --- Collection coverage for remaining wrapper types ---

    [Fact]
    public void CollectsFromDictionary()
    {
        var inline = new TsType.InlineObject([("key", new TsType.Primitive("string"))]);
        var endpoint = MakeEndpoint("Items", "map",
            returnType: new TsType.Dictionary(inline));

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Single(result);
        Assert.Same(inline, result[0].Type);
    }

    [Fact]
    public void CollectsFromGenericTypeArguments()
    {
        var inline = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);
        var endpoint = MakeEndpoint("Items", "paged",
            returnType: new TsType.Generic("PagedResult", [inline]));

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Single(result);
        Assert.Same(inline, result[0].Type);
    }

    [Fact]
    public void CollectsFromBrandInner()
    {
        var inline = new TsType.InlineObject([("value", new TsType.Primitive("string"))]);
        var endpoint = MakeEndpoint("Items", "get",
            returnType: new TsType.Brand("Tagged", inline));

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Single(result);
        Assert.Same(inline, result[0].Type);
    }

    [Fact]
    public void CollectsNestedInlineObjectsFromFields()
    {
        var inner = new TsType.InlineObject([("street", new TsType.Primitive("string"))]);
        var outer = new TsType.InlineObject([
            ("name", new TsType.Primitive("string")),
            ("address", inner),
        ]);
        var endpoint = MakeEndpoint("Buyers", "find", returnType: outer);

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        // Both outer and inner should be collected
        Assert.Equal(2, result.Count);
        Assert.Same(outer, result[0].Type);
        Assert.Same(inner, result[1].Type);
    }

    [Fact]
    public void NullReturnType_DoesNotCrash()
    {
        var endpoint = MakeEndpoint("Buyers", "delete");

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Empty(result);
    }

    [Fact]
    public void NullResponseDataType_DoesNotCrash()
    {
        var endpoint = MakeEndpoint("Buyers", "delete",
            responses: [new TsResponseType(204, null)]);

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Empty(result);
    }

    [Fact]
    public void EmptyEndpointsList_ReturnsEmpty()
    {
        var result = InlineTypeExtractor.CollectInlineObjects([]);

        Assert.Empty(result);
    }
}
