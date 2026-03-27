namespace Rivet.Tests;

public sealed class PhpRoundTripTests
{
    [Fact]
    public void Scalars_Produce_Correct_TypeScript()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("export type ScalarDto = {", ts);
        Assert.Contains("  name: string;", ts);
        Assert.Contains("  count: number;", ts);
        Assert.Contains("  rate: number;", ts);
        Assert.Contains("  isActive: boolean;", ts);
    }

    [Fact]
    public void Nullables_Wrap_Inner_Type()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("  maybeName: string | null;", ts);
        Assert.Contains("  maybeCount: number | null;", ts);
    }

    [Fact]
    public void List_Produces_Array_Type()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("  tags: string[];", ts);
        Assert.Contains("  scores: number[];", ts);
    }

    [Fact]
    public void Dictionary_Produces_Record_Type()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("  scores: Record<string, number>;", ts);
    }

    [Fact]
    public void ArrayShape_Produces_InlineObject()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("dimensions: { width: number; height: number; };", ts);
    }

    [Fact]
    public void BackedEnum_Produces_StringUnion_And_Ref()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("export type Status = \"active\" | \"inactive\" | \"pending\";", ts);
        Assert.Contains("  status: Status;", ts);
    }

    [Fact]
    public void Optional_Property_Emits_QuestionMark()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("  required: string;", ts);
        Assert.Contains("  nickname?: string;", ts);
    }

    [Fact]
    public void IntBackedEnum_Produces_IntUnion_And_Ref()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("export type Priority = 1 | 2 | 3;", ts);
        Assert.Contains("  priority: Priority;", ts);
    }

    [Fact]
    public void DocblockStringUnion_Produces_Inline_Union()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("  priority: \"low\" | \"medium\" | \"high\";", ts);
    }

    [Fact]
    public void DocblockIntUnion_Produces_Inline_Union()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("  priority: 1 | 2 | 3;", ts);
    }

    [Fact]
    public void NestedDto_Emits_Both_Types_With_Ref()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        Assert.Contains("export type PersonDto", ts);
        Assert.Contains("  address: AddressDto;", ts);
        Assert.Contains("export type AddressDto", ts);
        Assert.Contains("  street: string;", ts);
        Assert.Contains("  city: string;", ts);
    }

    [Fact]
    public void FullContract_AllVariations_Produce_Correct_TypeScript()
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

        var ts = CompilationHelper.EmitTypesFromJson(json);

        // Enums (alphabetical)
        Assert.Contains("export type Priority = 1 | 2 | 3;", ts);
        Assert.Contains("export type Status = \"active\" | \"pending\";", ts);

        // Scalar types
        Assert.Contains("  name: string;", ts);
        Assert.Contains("  count: number;", ts);
        Assert.Contains("  rate: number;", ts);
        Assert.Contains("  active: boolean;", ts);

        // Nullable
        Assert.Contains("  nickname: string | null;", ts);

        // List
        Assert.Contains("  tags: string[];", ts);

        // Dictionary
        Assert.Contains("  scores: Record<string, number>;", ts);

        // Inline object
        Assert.Contains("dimensions: { width: number; height: number; };", ts);

        // Enum refs
        Assert.Contains("  status: Status;", ts);
        Assert.Contains("  priority: Priority;", ts);

        // Docblock unions inline
        Assert.Contains("  level: \"low\" | \"high\";", ts);
        Assert.Contains("  code: 1 | 2 | 3;", ts);

        // Nested ref
        Assert.Contains("  child: ChildDto;", ts);
        Assert.Contains("export type ChildDto", ts);
        Assert.Contains("export type ParentDto", ts);
    }
}
