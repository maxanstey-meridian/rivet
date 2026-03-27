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
}
