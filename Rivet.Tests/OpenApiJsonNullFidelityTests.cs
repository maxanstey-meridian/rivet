using System.Text.Json.Nodes;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class OpenApiJsonNullFidelityTests
{
    private const string LiteralSentinel =
        "openapi-json-null-sentinel-value-2BF93600-0FE4-4250-987A-E5DDB203E464";

    [Fact]
    public void Property_schema_explicit_null_example_survives_import_and_emit()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Item": {
                "type": "object",
                "properties": {
                    "value": {
                        "type": ["string", "null"],
                        "example": null
                    }
                },
                "required": ["value"]
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        Assert.Empty(
            imported
                .Files.Where(file =>
                    file.Content.Contains("rivet-openapi-json-null-sentinel-literal-")
                )
                .Select(file => file.FileName)
        );
        var emitted = CompileAndEmit(imported);
        var property = emitted["components"]!["schemas"]!["Item"]!["properties"]![
            "value"
        ]!.AsObject();

        Assert.True(property.ContainsKey("example"));
        Assert.Null(property["example"]);
    }

    [Fact]
    public void Explicit_null_examples_survive_all_imported_example_surfaces()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: $$"""
            "Scalar": {
                "type": ["string", "null"],
                "example": null
            },
            "ExampleSet": {
                "type": "string",
                "examples": [{ "nested": null }, "{{LiteralSentinel}}"]
            }
            """,
            paths: $$"""
            "/items": {
                "get": {
                    "operationId": "getItems",
                    "parameters": [
                        {
                            "name": "q",
                            "in": "query",
                            "schema": {
                                "type": ["string", "null"],
                                "examples": [{ "nested": null }, "{{LiteralSentinel}}"]
                            },
                            "example": null
                        },
                        {
                            "name": "absent",
                            "in": "query",
                            "schema": { "type": "string" }
                        },
                        {
                            "name": "named",
                            "in": "query",
                            "schema": { "type": "string" },
                            "examples": {
                                "nullValue": { "value": null },
                                "control": {
                                    "value": { "nested": null, "literal": "{{LiteralSentinel}}" }
                                }
                            }
                        }
                    ],
                    "responses": {
                        "200": {
                            "description": "OK",
                            "headers": {
                                "X-Null": {
                                    "schema": {
                                        "type": ["string", "null"],
                                        "examples": [{ "nested": null }, "{{LiteralSentinel}}"]
                                    },
                                    "example": null
                                },
                                "X-Named": {
                                    "schema": { "type": "string" },
                                    "examples": {
                                        "nullValue": { "value": null },
                                        "control": {
                                            "value": {
                                                "nested": null,
                                                "literal": "{{LiteralSentinel}}"
                                            }
                                        }
                                    }
                                }
                            },
                            "content": {
                                "application/json": { "example": null },
                                "application/problem+json": {},
                                "application/vnd.named+json": {
                                    "examples": {
                                        "nullValue": { "value": null },
                                        "control": {
                                            "value": {
                                                "nested": null,
                                                "literal": "{{LiteralSentinel}}"
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        Assert.Empty(
            imported
                .Files.Where(file =>
                    file.Content.Contains("rivet-openapi-json-null-sentinel-literal-")
                )
                .Select(file => file.FileName)
        );
        var emitted = CompileAndEmit(imported);
        var schemas = emitted["components"]!["schemas"]!;
        AssertExplicitNull(schemas["Scalar"]!, "example");
        AssertExampleControls(schemas["ExampleSet"]!["examples"]!);

        var operation = emitted["paths"]!["/items"]!["get"]!;
        var parameters = operation["parameters"]!.AsArray();
        var q = parameters.Single(node => node!["name"]!.GetValue<string>() == "q")!;
        AssertExplicitNull(q, "example");
        AssertExampleControls(q["schema"]!["examples"]!);
        var absent = parameters.Single(node => node!["name"]!.GetValue<string>() == "absent")!;
        Assert.False(absent.AsObject().ContainsKey("example"));
        var named = parameters.Single(node => node!["name"]!.GetValue<string>() == "named")!;
        AssertNamedExampleControls(named["examples"]!);

        var response = operation["responses"]!["200"]!;
        var header = response["headers"]!["X-Null"]!;
        AssertExplicitNull(header, "example");
        AssertExampleControls(header["schema"]!["examples"]!);
        AssertNamedExampleControls(response["headers"]!["X-Named"]!["examples"]!);
        AssertExplicitNull(response["content"]!["application/json"]!, "example");
        Assert.False(
            response["content"]!["application/problem+json"]!.AsObject().ContainsKey("example")
        );
        AssertNamedExampleControls(
            response["content"]!["application/vnd.named+json"]!["examples"]!
        );
    }

    private static void AssertExplicitNull(JsonNode owner, string propertyName)
    {
        Assert.True(owner.AsObject().ContainsKey(propertyName));
        Assert.Null(owner[propertyName]);
    }

    private static void AssertExampleControls(JsonNode examples)
    {
        Assert.Null(examples[0]!["nested"]);
        Assert.Equal(LiteralSentinel, examples[1]!.GetValue<string>());
    }

    private static void AssertNamedExampleControls(JsonNode examples)
    {
        AssertExplicitNull(examples["nullValue"]!, "value");
        var control = examples["control"]!["value"]!;
        Assert.Null(control["nested"]);
        Assert.True(
            control["literal"] is { } literal && literal.GetValue<string>() == LiteralSentinel,
            examples.ToJsonString()
        );
    }

    private static JsonNode CompileAndEmit(Rivet.Tool.Import.ImportResult imported)
    {
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        return JsonNode.Parse(
            OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null)
        )!;
    }
}
