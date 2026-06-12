using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// FABLE_ROUNDTRIP #3 — enum wire-value identity. The emitter camelCases
/// unpinned member names, so the import-side pin trigger must compare the
/// original value against that EMITTED form, not against the C# member name:
/// 'Ready' (Pascal == original) still emitted as 'ready', silently, in both
/// directions. Pin whenever emitted ≠ original; skip the pin when camelCasing
/// already reproduces the original exactly.
/// </summary>
public sealed class EnumWireValuePinningTests
{
    private static string ImportStateEnum()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "StateDto": {
                    "type": "string",
                    "enum": ["Ready", "FLAT_RATE", "EastUs", "ALLCAPS", "open"]
                },
                "ItemDto": {
                    "type": "object",
                    "required": ["state"],
                    "properties": { "state": { "$ref": "#/components/schemas/StateDto" } }
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "ListItems",
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ItemDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        return CompilationHelper.FindFile(CompilationHelper.Import(spec), "StateDto.cs");
    }

    [Fact]
    public void CaseVariant_Values_Are_Pinned_CamelCase_Equivalents_Are_Not()
    {
        var enumContent = ImportStateEnum();

        // camelCase(member) would mangle these — every one needs a pin
        Assert.Contains("[JsonStringEnumMemberName(\"Ready\")]", enumContent);
        Assert.Contains("[JsonStringEnumMemberName(\"FLAT_RATE\")]", enumContent);
        Assert.Contains("[JsonStringEnumMemberName(\"EastUs\")]", enumContent);
        Assert.Contains("[JsonStringEnumMemberName(\"ALLCAPS\")]", enumContent);

        // 'open' -> member 'Open' -> emitted 'open': already wire-true, no pin
        Assert.DoesNotContain("[JsonStringEnumMemberName(\"open\")]", enumContent);
    }

    [Fact]
    public void Emitted_Wire_Values_Equal_Originals_Exactly()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "StateDto": {
                    "type": "string",
                    "enum": ["Ready", "FLAT_RATE", "EastUs", "ALLCAPS", "open"]
                },
                "ItemDto": {
                    "type": "object",
                    "required": ["state"],
                    "properties": { "state": { "$ref": "#/components/schemas/StateDto" } }
                }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "ListItems",
                        "responses": {
                            "200": {
                                "description": "Success",
                                "content": {
                                    "application/json": {
                                        "schema": { "$ref": "#/components/schemas/ItemDto" }
                                    }
                                }
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var compilation = CompilationHelper.CompileImportResult(result);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var state = (TsType.StringUnion)walker.Enums["StateDto"];
        Assert.Equal(["Ready", "FLAT_RATE", "EastUs", "ALLCAPS", "open"], state.Members);
    }
}
