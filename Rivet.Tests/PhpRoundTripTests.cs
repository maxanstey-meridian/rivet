using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// rivet-php emits Rivet contract JSON (`rivet:reflect` → `rivet --from`). These tests
/// pin the .NET side of that pipeline: contract JSON shapes a PHP reflector produces →
/// OpenAPI 3.1 component schemas (post-Phase-3 the tool's only output).
/// </summary>
public sealed class PhpRoundTripTests
{
    private static JsonElement SchemasFor(string contractJson)
    {
        var spec = CompilationHelper.EmitOpenApiFromJson(contractJson);
        return JsonDocument.Parse(spec).RootElement
            .GetProperty("components").GetProperty("schemas");
    }

    private static JsonElement Prop(JsonElement schemas, string type, string prop)
        => schemas.GetProperty(type).GetProperty("properties").GetProperty(prop);

    private static List<string?> Required(JsonElement schemas, string type)
        => schemas.GetProperty(type).GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();

    private static void AssertNullableType(JsonElement prop, string expectedType)
    {
        // OpenAPI 3.1: nullable is a type array ["T", "null"]
        var types = prop.GetProperty("type").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(expectedType, types);
        Assert.Contains("null", types);
    }

    [Fact]
    public void Scalars_Produce_Correct_Schema_Types()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "ScalarDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "name", "type": { "kind": "primitive", "type": "string" }, "optional": false },
                            { "name": "count", "type": { "kind": "primitive", "type": "number", "format": "int32" }, "optional": false },
                            { "name": "rate", "type": { "kind": "primitive", "type": "number", "format": "double" }, "optional": false },
                            { "name": "isActive", "type": { "kind": "primitive", "type": "boolean" }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        Assert.Equal("string", Prop(schemas, "ScalarDto", "name").GetProperty("type").GetString());
        Assert.Equal("integer", Prop(schemas, "ScalarDto", "count").GetProperty("type").GetString());
        Assert.Equal("int32", Prop(schemas, "ScalarDto", "count").GetProperty("format").GetString());
        Assert.Equal("number", Prop(schemas, "ScalarDto", "rate").GetProperty("type").GetString());
        Assert.Equal("double", Prop(schemas, "ScalarDto", "rate").GetProperty("format").GetString());
        Assert.Equal("boolean", Prop(schemas, "ScalarDto", "isActive").GetProperty("type").GetString());
    }

    [Fact]
    public void Nullables_Emit_Type_Arrays()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "NullableDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "maybeName", "type": { "kind": "nullable", "inner": { "kind": "primitive", "type": "string" } }, "optional": false },
                            { "name": "maybeCount", "type": { "kind": "nullable", "inner": { "kind": "primitive", "type": "number", "format": "int32" } }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        AssertNullableType(Prop(schemas, "NullableDto", "maybeName"), "string");
        AssertNullableType(Prop(schemas, "NullableDto", "maybeCount"), "integer");
    }

    [Fact]
    public void List_Produces_Array_Schema()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "ListDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "tags", "type": { "kind": "array", "element": { "kind": "primitive", "type": "string" } }, "optional": false },
                            { "name": "scores", "type": { "kind": "array", "element": { "kind": "primitive", "type": "number", "format": "int32" } }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        var tags = Prop(schemas, "ListDto", "tags");
        Assert.Equal("array", tags.GetProperty("type").GetString());
        Assert.Equal("string", tags.GetProperty("items").GetProperty("type").GetString());

        var scores = Prop(schemas, "ListDto", "scores");
        Assert.Equal("array", scores.GetProperty("type").GetString());
        Assert.Equal("integer", scores.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void Dictionary_Produces_AdditionalProperties_Schema()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "DictDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "scores", "type": { "kind": "dictionary", "value": { "kind": "primitive", "type": "number", "format": "int32" } }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        var scores = Prop(schemas, "DictDto", "scores");
        Assert.Equal("object", scores.GetProperty("type").GetString());
        Assert.Equal("integer", scores.GetProperty("additionalProperties").GetProperty("type").GetString());
    }

    [Fact]
    public void ArrayShape_Produces_Inline_Object_Schema()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "ShapeDto",
                        "typeParameters": [],
                        "properties": [
                            {
                                "name": "dimensions",
                                "type": {
                                    "kind": "inlineObject",
                                    "properties": [
                                        { "name": "width", "type": { "kind": "primitive", "type": "number", "format": "double" } },
                                        { "name": "height", "type": { "kind": "primitive", "type": "number", "format": "double" } }
                                    ]
                                },
                                "optional": false
                            }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        var dimensions = Prop(schemas, "ShapeDto", "dimensions");
        Assert.Equal("object", dimensions.GetProperty("type").GetString());
        var props = dimensions.GetProperty("properties");
        Assert.Equal("number", props.GetProperty("width").GetProperty("type").GetString());
        Assert.Equal("number", props.GetProperty("height").GetProperty("type").GetString());
    }

    [Fact]
    public void BackedEnum_Produces_Enum_Component_And_Ref()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "WithEnumDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "status", "type": { "kind": "ref", "name": "Status" }, "optional": false }
                        ]
                    }
                ],
                "enums": [
                    { "name": "Status", "values": ["active", "inactive", "pending"] }
                ],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        Assert.Equal("#/components/schemas/Status",
            Prop(schemas, "WithEnumDto", "status").GetProperty("$ref").GetString());
        Assert.Equal(new[] { "active", "inactive", "pending" },
            schemas.GetProperty("Status").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Optional_Property_Excluded_From_Required()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "OptionalDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "required", "type": { "kind": "primitive", "type": "string" }, "optional": false },
                            { "name": "nickname", "type": { "kind": "primitive", "type": "string" }, "optional": true }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        var required = Required(schemas, "OptionalDto");
        Assert.Contains("required", required);
        Assert.DoesNotContain("nickname", required);
    }

    [Fact]
    public void IntBackedEnum_Produces_Int_Enum_Component_And_Ref()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "TaskDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "title", "type": { "kind": "primitive", "type": "string" }, "optional": false },
                            { "name": "priority", "type": { "kind": "ref", "name": "Priority" }, "optional": false }
                        ]
                    }
                ],
                "enums": [
                    { "name": "Priority", "intValues": [1, 2, 3] }
                ],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        Assert.Equal("#/components/schemas/Priority",
            Prop(schemas, "TaskDto", "priority").GetProperty("$ref").GetString());
        Assert.Equal(new[] { 1, 2, 3 },
            schemas.GetProperty("Priority").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public void DocblockStringUnion_Produces_Inline_Enum()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "PriorityDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "priority", "type": { "kind": "stringUnion", "values": ["low", "medium", "high"] }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        var priority = Prop(schemas, "PriorityDto", "priority");
        Assert.Equal(new[] { "low", "medium", "high" },
            priority.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void DocblockIntUnion_Produces_Inline_Enum()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "IntDocblockDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "priority", "type": { "kind": "intUnion", "values": [1, 2, 3] }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        var priority = Prop(schemas, "IntDocblockDto", "priority");
        Assert.Equal(new[] { 1, 2, 3 },
            priority.GetProperty("enum").EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public void NestedDto_Emits_Both_Schemas_With_Ref()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "PersonDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "name", "type": { "kind": "primitive", "type": "string" }, "optional": false },
                            { "name": "address", "type": { "kind": "ref", "name": "AddressDto" }, "optional": false }
                        ]
                    },
                    {
                        "name": "AddressDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "street", "type": { "kind": "primitive", "type": "string" }, "optional": false },
                            { "name": "city", "type": { "kind": "primitive", "type": "string" }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        Assert.Equal("#/components/schemas/AddressDto",
            Prop(schemas, "PersonDto", "address").GetProperty("$ref").GetString());
        Assert.Equal("string", Prop(schemas, "AddressDto", "street").GetProperty("type").GetString());
        Assert.Equal("string", Prop(schemas, "AddressDto", "city").GetProperty("type").GetString());
    }

    [Fact]
    public void FullContract_AllVariations_Produce_Correct_Schemas()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "ScalarDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "name", "type": { "kind": "primitive", "type": "string" }, "optional": false },
                            { "name": "count", "type": { "kind": "primitive", "type": "number", "format": "int32" }, "optional": false },
                            { "name": "rate", "type": { "kind": "primitive", "type": "number", "format": "double" }, "optional": false },
                            { "name": "active", "type": { "kind": "primitive", "type": "boolean" }, "optional": false }
                        ]
                    },
                    {
                        "name": "NullableDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "nickname", "type": { "kind": "nullable", "inner": { "kind": "primitive", "type": "string" } }, "optional": false },
                            { "name": "count", "type": { "kind": "nullable", "inner": { "kind": "primitive", "type": "number", "format": "int32" } }, "optional": false }
                        ]
                    },
                    {
                        "name": "ListDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "tags", "type": { "kind": "array", "element": { "kind": "primitive", "type": "string" } }, "optional": false }
                        ]
                    },
                    {
                        "name": "DictDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "scores", "type": { "kind": "dictionary", "value": { "kind": "primitive", "type": "number", "format": "int32" } }, "optional": false }
                        ]
                    },
                    {
                        "name": "ShapeDto",
                        "typeParameters": [],
                        "properties": [
                            {
                                "name": "dimensions",
                                "type": {
                                    "kind": "inlineObject",
                                    "properties": [
                                        { "name": "width", "type": { "kind": "primitive", "type": "number" } },
                                        { "name": "height", "type": { "kind": "primitive", "type": "number" } }
                                    ]
                                },
                                "optional": false
                            }
                        ]
                    },
                    {
                        "name": "EnumDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "status", "type": { "kind": "ref", "name": "Status" }, "optional": false }
                        ]
                    },
                    {
                        "name": "IntEnumDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "priority", "type": { "kind": "ref", "name": "Priority" }, "optional": false }
                        ]
                    },
                    {
                        "name": "UnionDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "level", "type": { "kind": "stringUnion", "values": ["low", "high"] }, "optional": false },
                            { "name": "code", "type": { "kind": "intUnion", "values": [1, 2, 3] }, "optional": false }
                        ]
                    },
                    {
                        "name": "ParentDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "child", "type": { "kind": "ref", "name": "ChildDto" }, "optional": false }
                        ]
                    },
                    {
                        "name": "ChildDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "value", "type": { "kind": "primitive", "type": "string" }, "optional": false }
                        ]
                    }
                ],
                "enums": [
                    { "name": "Status", "values": ["active", "pending"] },
                    { "name": "Priority", "intValues": [1, 2, 3] }
                ],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        // Enum components
        Assert.Equal(new[] { "active", "pending" },
            schemas.GetProperty("Status").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { 1, 2, 3 },
            schemas.GetProperty("Priority").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetInt32()).ToArray());

        // Scalar types
        Assert.Equal("string", Prop(schemas, "ScalarDto", "name").GetProperty("type").GetString());
        Assert.Equal("integer", Prop(schemas, "ScalarDto", "count").GetProperty("type").GetString());
        Assert.Equal("number", Prop(schemas, "ScalarDto", "rate").GetProperty("type").GetString());
        Assert.Equal("boolean", Prop(schemas, "ScalarDto", "active").GetProperty("type").GetString());

        // Nullable
        AssertNullableType(Prop(schemas, "NullableDto", "nickname"), "string");

        // List
        Assert.Equal("array", Prop(schemas, "ListDto", "tags").GetProperty("type").GetString());

        // Dictionary
        Assert.Equal("integer",
            Prop(schemas, "DictDto", "scores").GetProperty("additionalProperties").GetProperty("type").GetString());

        // Inline object
        Assert.Equal("number",
            Prop(schemas, "ShapeDto", "dimensions").GetProperty("properties").GetProperty("width").GetProperty("type").GetString());

        // Enum refs
        Assert.Equal("#/components/schemas/Status",
            Prop(schemas, "EnumDto", "status").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/Priority",
            Prop(schemas, "IntEnumDto", "priority").GetProperty("$ref").GetString());

        // Docblock unions inline
        Assert.Equal(new[] { "low", "high" },
            Prop(schemas, "UnionDto", "level").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { 1, 2, 3 },
            Prop(schemas, "UnionDto", "code").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetInt32()).ToArray());

        // Nested ref
        Assert.Equal("#/components/schemas/ChildDto",
            Prop(schemas, "ParentDto", "child").GetProperty("$ref").GetString());
        Assert.Equal("string", Prop(schemas, "ChildDto", "value").GetProperty("type").GetString());
    }

    [Fact]
    public void NullableRef_And_NullableArray_Emit_Correctly()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "ProfileDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "address", "type": { "kind": "nullable", "inner": { "kind": "ref", "name": "AddressDto" } }, "optional": false },
                            { "name": "tags", "type": { "kind": "nullable", "inner": { "kind": "array", "element": { "kind": "primitive", "type": "string" } } }, "optional": false }
                        ]
                    },
                    {
                        "name": "AddressDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "street", "type": { "kind": "primitive", "type": "string" }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": []
            }
            """;

        var schemas = SchemasFor(json);

        // Nullable $ref: 3.1 — anyOf [$ref, null] or oneOf; assert the ref is reachable
        var address = Prop(schemas, "ProfileDto", "address");
        var addressJson = address.GetRawText();
        Assert.Contains("#/components/schemas/AddressDto", addressJson);
        Assert.Contains("null", addressJson);

        // Nullable array: type ["array","null"] with string items
        var tags = Prop(schemas, "ProfileDto", "tags");
        var tagTypes = tags.GetProperty("type").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("array", tagTypes);
        Assert.Contains("null", tagTypes);
        Assert.Equal("string", tags.GetProperty("items").GetProperty("type").GetString());
    }
}
