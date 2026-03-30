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
        Assert.Equal("Buyers.find.return", result[0].Context);
        Assert.Same(inner, result[1].Type);
        Assert.Equal("Buyers.find.return.field.address", result[1].Context);
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

    // --- Singularize tests ---

    [Theory]
    [InlineData("Buyers", "Buyer")]
    [InlineData("Categories", "Category")]
    [InlineData("Orders", "Order")]
    public void Singularize_Basic(string input, string expected)
    {
        Assert.Equal(expected, InlineTypeExtractor.Singularize(input));
    }

    [Theory]
    [InlineData("As", "As")]
    [InlineData("Is", "Is")]
    [InlineData("Go", "Go")]
    public void Singularize_ShortWord(string input, string expected)
    {
        Assert.Equal(expected, InlineTypeExtractor.Singularize(input));
    }

    // --- GenerateName tests ---

    [Fact]
    public void GenerateName_FromControllerName()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var occurrences = new List<(TsType.InlineObject Type, string Context)>
        {
            (inline, "Buyers.find.return"),
        };

        var result = InlineTypeExtractor.GenerateName("Buyers", occurrences, new HashSet<string>());

        Assert.Equal("BuyerDto", result);
    }

    [Fact]
    public void GenerateName_AlreadySingular()
    {
        var inline = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);
        var occurrences = new List<(TsType.InlineObject Type, string Context)>
        {
            (inline, "Order.list.return"),
        };

        var result = InlineTypeExtractor.GenerateName("Order", occurrences, new HashSet<string>());

        Assert.Equal("OrderDto", result);
    }

    [Fact]
    public void GenerateName_NestedField()
    {
        var inner = new TsType.InlineObject([("street", new TsType.Primitive("string"))]);
        var occurrences = new List<(TsType.InlineObject Type, string Context)>
        {
            (inner, "Buyers.find.return.field.lines"),
        };

        var result = InlineTypeExtractor.GenerateName("Buyers", occurrences, new HashSet<string>());

        Assert.Equal("LineDto", result);
    }

    [Fact]
    public void GenerateName_Collision_AppendsSuffix()
    {
        var inline = new TsType.InlineObject([("id", new TsType.Primitive("number"))]);
        var occurrences = new List<(TsType.InlineObject Type, string Context)>
        {
            (inline, "Buyers.find.return"),
        };
        var usedNames = new HashSet<string> { "BuyerDto" };

        var result = InlineTypeExtractor.GenerateName("Buyers", occurrences, usedNames);

        Assert.Equal("BuyerDto2", result);
        Assert.Contains("BuyerDto2", usedNames);
    }

    // --- Extract tests ---

    [Fact]
    public void DuplicateInlineObjects_ExtractedToNamedType()
    {
        var inline1 = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var inline2 = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: inline1),
            MakeEndpoint("Buyers", "list", returnType: inline2),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        // One extracted type
        Assert.Single(result.ExtractedTypes);
        var extracted = result.ExtractedTypes[0];
        Assert.Equal("BuyerDto", extracted.Name);
        Assert.Equal(2, extracted.Properties.Count);

        // Both endpoints replaced with TypeRef
        Assert.IsType<TsType.TypeRef>(result.Endpoints[0].ReturnType);
        Assert.Equal("BuyerDto", ((TsType.TypeRef)result.Endpoints[0].ReturnType!).Name);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[1].ReturnType);
        Assert.Equal("BuyerDto", ((TsType.TypeRef)result.Endpoints[1].ReturnType!).Name);

        // Namespace is null (common group)
        Assert.Null(result.TypeNamespaces["BuyerDto"]);
    }

    [Fact]
    public void LargeInlineObject_ExtractedEvenIfSingle()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
            ("email", new TsType.Primitive("string")),
            ("phone", new TsType.Primitive("string")),
            ("address", new TsType.Primitive("string")),
        ]);
        var endpoints = new[] { MakeEndpoint("Buyers", "find", returnType: inline) };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Single(result.ExtractedTypes);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[0].ReturnType);
    }

    [Fact]
    public void SmallSingleOccurrence_NotExtracted()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
            ("email", new TsType.Primitive("string")),
        ]);
        var endpoints = new[] { MakeEndpoint("Buyers", "find", returnType: inline) };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Empty(result.ExtractedTypes);
        Assert.IsType<TsType.InlineObject>(result.Endpoints[0].ReturnType);
    }

    [Fact]
    public void ReplacementIsRecursive()
    {
        var inner = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: new TsType.Array(inner)),
            MakeEndpoint("Buyers", "list", returnType: new TsType.Array(
                new TsType.InlineObject([
                    ("id", new TsType.Primitive("number")),
                    ("name", new TsType.Primitive("string")),
                ]))),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Single(result.ExtractedTypes);

        // Both endpoints should have Array(TypeRef(...))
        var ret0 = Assert.IsType<TsType.Array>(result.Endpoints[0].ReturnType);
        Assert.IsType<TsType.TypeRef>(ret0.Element);

        var ret1 = Assert.IsType<TsType.Array>(result.Endpoints[1].ReturnType);
        Assert.IsType<TsType.TypeRef>(ret1.Element);
    }

    [Fact]
    public void ResponseDataTypes_AlsoReplaced()
    {
        var inline = new TsType.InlineObject([
            ("total", new TsType.Primitive("number")),
            ("items", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Orders", "list",
                responses: [new TsResponseType(200, inline)]),
            MakeEndpoint("Orders", "search",
                responses: [new TsResponseType(200,
                    new TsType.InlineObject([
                        ("total", new TsType.Primitive("number")),
                        ("items", new TsType.Primitive("string")),
                    ]))]),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Single(result.ExtractedTypes);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[0].Responses[0].DataType);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[1].Responses[0].DataType);
    }

    [Fact]
    public void ExistingDefinitions_NoNameCollision()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: inline),
            MakeEndpoint("Buyers", "list", returnType: new TsType.InlineObject([
                ("id", new TsType.Primitive("number")),
                ("name", new TsType.Primitive("string")),
            ])),
        };
        var existingDefs = new[] { new TsTypeDefinition("BuyerDto", [], []) };

        var result = InlineTypeExtractor.Extract(endpoints, existingDefs);

        Assert.Single(result.ExtractedTypes);
        Assert.Equal("BuyerDto2", result.ExtractedTypes[0].Name);
    }

    [Fact]
    public void ExtractedTypeProperties_CorrectOptional()
    {
        var inline = new TsType.InlineObject([
            ("name", new TsType.Primitive("string")),
            ("nickname", new TsType.Nullable(new TsType.Primitive("string"))),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: inline),
            MakeEndpoint("Buyers", "list", returnType: new TsType.InlineObject([
                ("name", new TsType.Primitive("string")),
                ("nickname", new TsType.Nullable(new TsType.Primitive("string"))),
            ])),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        var extracted = result.ExtractedTypes[0];
        Assert.Equal(2, extracted.Properties.Count);

        var nameProp = extracted.Properties.First(p => p.Name == "name");
        Assert.False(nameProp.IsOptional);
        Assert.IsType<TsType.Primitive>(nameProp.Type);

        var nicknameProp = extracted.Properties.First(p => p.Name == "nickname");
        Assert.True(nicknameProp.IsOptional);
        Assert.IsType<TsType.Primitive>(nicknameProp.Type);
        Assert.Equal("string", ((TsType.Primitive)nicknameProp.Type).Name);
    }

    [Fact]
    public void NestedInlineObjects_BothExtracted()
    {
        var inner = new TsType.InlineObject([
            ("street", new TsType.Primitive("string")),
            ("city", new TsType.Primitive("string")),
        ]);
        var outer = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
            ("address", inner),
        ]);
        // Duplicate both outer and inner across two endpoints
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: outer),
            MakeEndpoint("Buyers", "list", returnType: new TsType.InlineObject([
                ("id", new TsType.Primitive("number")),
                ("name", new TsType.Primitive("string")),
                ("address", new TsType.InlineObject([
                    ("street", new TsType.Primitive("string")),
                    ("city", new TsType.Primitive("string")),
                ])),
            ])),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        // Both inner and outer extracted
        Assert.Equal(2, result.ExtractedTypes.Count);

        // Both endpoints replaced with TypeRef at top level
        Assert.IsType<TsType.TypeRef>(result.Endpoints[0].ReturnType);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[1].ReturnType);

        // The outer type's 'address' property should reference the inner type via TypeRef
        var outerType = result.ExtractedTypes.First(t =>
            t.Properties.Any(p => p.Name == "address"));
        var addressProp = outerType.Properties.First(p => p.Name == "address");
        Assert.IsType<TsType.TypeRef>(addressProp.Type);
    }

    // --- Regression guard tests for Extract gaps ---

    [Fact]
    public void ParamInlineObjects_Replaced()
    {
        var inline = new TsType.InlineObject([
            ("title", new TsType.Primitive("string")),
            ("body", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Posts", "create",
                parameters: [new TsEndpointParam("data", inline, ParamSource.Body)]),
            MakeEndpoint("Posts", "update",
                parameters: [new TsEndpointParam("data",
                    new TsType.InlineObject([
                        ("title", new TsType.Primitive("string")),
                        ("body", new TsType.Primitive("string")),
                    ]), ParamSource.Body)]),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Single(result.ExtractedTypes);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[0].Params[0].Type);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[1].Params[0].Type);
        Assert.Equal("PostDto", result.ExtractedTypes[0].Name);
    }

    [Fact]
    public void ReplaceInType_Dictionary()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("label", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Items", "map",
                returnType: new TsType.Dictionary(inline)),
            MakeEndpoint("Items", "index",
                returnType: new TsType.Dictionary(
                    new TsType.InlineObject([
                        ("id", new TsType.Primitive("number")),
                        ("label", new TsType.Primitive("string")),
                    ]))),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Single(result.ExtractedTypes);
        var dict0 = Assert.IsType<TsType.Dictionary>(result.Endpoints[0].ReturnType);
        Assert.IsType<TsType.TypeRef>(dict0.Value);
        var dict1 = Assert.IsType<TsType.Dictionary>(result.Endpoints[1].ReturnType);
        Assert.IsType<TsType.TypeRef>(dict1.Value);
    }

    [Fact]
    public void ReplaceInType_Generic()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("value", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Items", "paged",
                returnType: new TsType.Generic("PagedResult", [inline])),
            MakeEndpoint("Items", "search",
                returnType: new TsType.Generic("PagedResult", [
                    new TsType.InlineObject([
                        ("id", new TsType.Primitive("number")),
                        ("value", new TsType.Primitive("string")),
                    ])])),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Single(result.ExtractedTypes);
        var gen0 = Assert.IsType<TsType.Generic>(result.Endpoints[0].ReturnType);
        Assert.IsType<TsType.TypeRef>(gen0.TypeArguments[0]);
        var gen1 = Assert.IsType<TsType.Generic>(result.Endpoints[1].ReturnType);
        Assert.IsType<TsType.TypeRef>(gen1.TypeArguments[0]);
    }

    [Fact]
    public void ReplaceInType_Brand()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("tag", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Items", "get",
                returnType: new TsType.Brand("Tagged", inline)),
            MakeEndpoint("Items", "find",
                returnType: new TsType.Brand("Tagged",
                    new TsType.InlineObject([
                        ("id", new TsType.Primitive("number")),
                        ("tag", new TsType.Primitive("string")),
                    ]))),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Single(result.ExtractedTypes);
        var brand0 = Assert.IsType<TsType.Brand>(result.Endpoints[0].ReturnType);
        Assert.IsType<TsType.TypeRef>(brand0.Inner);
        var brand1 = Assert.IsType<TsType.Brand>(result.Endpoints[1].ReturnType);
        Assert.IsType<TsType.TypeRef>(brand1.Inner);
    }

    [Fact]
    public void NonExtractedParent_ChildFieldReplaced()
    {
        // Inner appears in two endpoints (extracted), outer is unique with 2 fields (not extracted)
        var inner = new TsType.InlineObject([
            ("street", new TsType.Primitive("string")),
            ("city", new TsType.Primitive("string")),
        ]);
        var outer = new TsType.InlineObject([
            ("name", new TsType.Primitive("string")),
            ("address", inner),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: outer),
            // Second endpoint has same inner but different outer
            MakeEndpoint("Sellers", "find", returnType: new TsType.InlineObject([
                ("company", new TsType.Primitive("string")),
                ("address", new TsType.InlineObject([
                    ("street", new TsType.Primitive("string")),
                    ("city", new TsType.Primitive("string")),
                ])),
            ])),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        // Inner extracted (appears twice), outers not extracted (2 fields each, unique)
        Assert.Single(result.ExtractedTypes);

        // Outers remain InlineObject but their address field is now a TypeRef
        var ret0 = Assert.IsType<TsType.InlineObject>(result.Endpoints[0].ReturnType);
        var addressField0 = ret0.Fields.First(f => f.Name == "address");
        Assert.IsType<TsType.TypeRef>(addressField0.Type);

        var ret1 = Assert.IsType<TsType.InlineObject>(result.Endpoints[1].ReturnType);
        var addressField1 = ret1.Fields.First(f => f.Name == "address");
        Assert.IsType<TsType.TypeRef>(addressField1.Type);
    }

    [Fact]
    public void MultipleDistinctTypes_AllExtracted()
    {
        var buyerShape = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var orderShape = new TsType.InlineObject([
            ("orderId", new TsType.Primitive("number")),
            ("total", new TsType.Primitive("number")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: buyerShape),
            MakeEndpoint("Buyers", "list", returnType: new TsType.InlineObject([
                ("id", new TsType.Primitive("number")),
                ("name", new TsType.Primitive("string")),
            ])),
            MakeEndpoint("Orders", "get", returnType: orderShape),
            MakeEndpoint("Orders", "list", returnType: new TsType.InlineObject([
                ("orderId", new TsType.Primitive("number")),
                ("total", new TsType.Primitive("number")),
            ])),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Equal(2, result.ExtractedTypes.Count);
        var names = result.ExtractedTypes.Select(t => t.Name).OrderBy(n => n).ToList();
        Assert.Contains("BuyerDto", names);
        Assert.Contains("OrderDto", names);
    }

    [Fact]
    public void CustomFieldThreshold_ExtractsSmaller()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
            ("email", new TsType.Primitive("string")),
        ]);
        var endpoints = new[] { MakeEndpoint("Buyers", "find", returnType: inline) };

        // Default threshold (5) — should NOT extract
        var resultDefault = InlineTypeExtractor.Extract(endpoints, []);
        Assert.Empty(resultDefault.ExtractedTypes);

        // Custom threshold (3) — should extract
        var resultCustom = InlineTypeExtractor.Extract(endpoints, [], fieldThreshold: 3);
        Assert.Single(resultCustom.ExtractedTypes);
        Assert.IsType<TsType.TypeRef>(resultCustom.Endpoints[0].ReturnType);
    }

    [Fact]
    public void SameEndpointDuplication_ReturnAndResponse()
    {
        // Same InlineObject appears in both ReturnType and a Response on the same endpoint
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find",
                returnType: inline,
                responses: [new TsResponseType(200,
                    new TsType.InlineObject([
                        ("id", new TsType.Primitive("number")),
                        ("name", new TsType.Primitive("string")),
                    ]))]),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        Assert.Single(result.ExtractedTypes);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[0].ReturnType);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[0].Responses[0].DataType);
    }

    [Fact]
    public void EmptyEndpoints_ReturnsEmptyResult()
    {
        var result = InlineTypeExtractor.Extract([], []);

        Assert.Empty(result.ExtractedTypes);
        Assert.Empty(result.Endpoints);
        Assert.Empty(result.TypeNamespaces);
    }

    [Fact]
    public void Extract_RequestTypeNull_NoEffect()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "find", returnType: inline),
            MakeEndpoint("Buyers", "list", returnType: inline),
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        // Extraction works normally
        Assert.Single(result.ExtractedTypes);
        // RequestType stays null
        Assert.Null(result.Endpoints[0].RequestType);
        Assert.Null(result.Endpoints[1].RequestType);
    }

    [Fact]
    public void Extract_DeduplicatesRequestTypeInlineObjects()
    {
        var inline1 = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var inline2 = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var endpoints = new[]
        {
            MakeEndpoint("Buyers", "create") with { RequestType = inline1 },
            MakeEndpoint("Buyers", "update") with { RequestType = inline2 },
        };

        var result = InlineTypeExtractor.Extract(endpoints, []);

        // One extracted type (deduplicated)
        Assert.Single(result.ExtractedTypes);
        Assert.Equal("BuyerDto", result.ExtractedTypes[0].Name);

        // Both endpoints' RequestType replaced with TypeRef
        Assert.IsType<TsType.TypeRef>(result.Endpoints[0].RequestType);
        Assert.Equal("BuyerDto", ((TsType.TypeRef)result.Endpoints[0].RequestType!).Name);
        Assert.IsType<TsType.TypeRef>(result.Endpoints[1].RequestType);
        Assert.Equal("BuyerDto", ((TsType.TypeRef)result.Endpoints[1].RequestType!).Name);
    }

    [Fact]
    public void CollectInlineObjects_WalksRequestType()
    {
        var inline = new TsType.InlineObject([
            ("id", new TsType.Primitive("number")),
            ("name", new TsType.Primitive("string")),
        ]);
        var endpoint = MakeEndpoint("Buyers", "create") with { RequestType = inline };

        var result = InlineTypeExtractor.CollectInlineObjects([endpoint]);

        Assert.Single(result);
        Assert.Same(inline, result[0].Type);
        Assert.Equal("Buyers.create.requestType", result[0].Context);
    }
}
