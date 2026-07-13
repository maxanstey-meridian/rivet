using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// FABLE_ROUNDTRIP #3 — enum wire-value identity. Imported string enums pin every
/// member explicitly so runtime serialization and forward emission use the exact
/// authored value rather than inferring it from the generated C# member name.
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
            title: "API"
        );

        return CompilationHelper.FindFile(CompilationHelper.Import(spec), "StateDto.cs");
    }

    [Fact]
    public void Every_String_Enum_Value_Is_Pinned_Explicitly()
    {
        var enumContent = ImportStateEnum();

        // camelCase(member) would mangle these — every one needs a pin
        Assert.Contains("[JsonStringEnumMemberName(\"Ready\")]", enumContent);
        Assert.Contains("[JsonStringEnumMemberName(\"FLAT_RATE\")]", enumContent);
        Assert.Contains("[JsonStringEnumMemberName(\"EastUs\")]", enumContent);
        Assert.Contains("[JsonStringEnumMemberName(\"ALLCAPS\")]", enumContent);

        // Runtime enum conversion does not apply the emitter's camel-case convention,
        // so wire-true values are explicit too.
        Assert.Contains("[JsonStringEnumMemberName(\"open\")]", enumContent);
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
            title: "API"
        );

        var result = CompilationHelper.Import(spec);
        var compilation = CompilationHelper.CompileImportResult(result);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var state = (TsType.StringUnion)walker.Enums["StateDto"];
        Assert.Equal(["Ready", "FLAT_RATE", "EastUs", "ALLCAPS", "open"], state.Members);
    }
}
