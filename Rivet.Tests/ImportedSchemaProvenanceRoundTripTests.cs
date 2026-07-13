using System.Text.Json.Nodes;
using Rivet.Tool;

namespace Rivet.Tests;

public sealed class ImportedSchemaProvenanceRoundTripTests
{
    [Fact]
    public void Typeless_Items_Schema_Uses_An_Array_Runtime_Type_Without_Degradation()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Typeless items", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "Item": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } }
                  }
                }
              },
              "paths": {
                "/items": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "Items",
                        "content": {
                          "application/json": {
                            "schema": {
                              "type": "object",
                              "properties": {
                                "data": {
                                  "items": { "$ref": "#/components/schemas/Item" }
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var imported = CompilationHelper.Import(spec, "TypelessItems");

        Assert.Empty(imported.Warnings);
        Assert.Contains(
            imported.Files,
            file => file.Content.Contains("public List<Item> Data", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Reversible_Imported_Schema_Algebra_Preserves_Exact_Transport_Shape()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Imported schema provenance", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "Base": {
                    "type": "object",
                    "properties": { "id": { "type": "string" } },
                    "required": ["id"]
                  },
                  "Derived": {
                    "allOf": [
                      { "$ref": "#/components/schemas/Base" },
                      {
                        "type": "object",
                        "properties": { "label": { "type": "string" } },
                        "required": ["label"]
                      }
                    ]
                  },
                  "Alias": { "$ref": "#/components/schemas/Derived" },
                  "InlineList": {
                    "type": "array",
                    "description": "Inline item collection",
                    "items": {
                      "type": "object",
                      "properties": { "value": { "type": "integer" } },
                      "required": ["value"]
                    }
                  },
                  "PortMap": {
                    "type": "object",
                    "additionalProperties": {
                      "type": "array",
                      "items": { "$ref": "#/components/schemas/Base" }
                    }
                  },
                  "Payload": {
                    "type": "object",
                    "properties": {
                      "choice": {
                        "items": { "type": "string" },
                        "oneOf": [
                          { "type": "array", "items": {} },
                          { "type": "string" }
                        ]
                      },
                      "mode": {
                        "type": "number",
                        "enum": [-1, 0, 1]
                      }
                    }
                  }
                }
              },
              "paths": {
                "/items": {
                  "post": {
                    "parameters": [
                      {
                        "name": "q",
                        "in": "query",
                        "schema": {
                          "type": "string",
                          "title": "Query value",
                          "description": "Schema-level description",
                          "examples": ["one"]
                        }
                      }
                    ],
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/json": {
                          "schema": {
                            "allOf": [
                              { "$ref": "#/components/schemas/Derived" },
                              {
                                "type": "object",
                                "properties": { "extra": { "type": "boolean" } }
                              }
                            ],
                            "examples": [{ "id": "1", "label": "item", "extra": true }]
                          }
                        }
                      }
                    },
                    "responses": {
                      "200": {
                        "description": "Alias response",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/Alias" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var source = JsonNode.Parse(spec)!.AsObject();
        var workDirectory = Directory.CreateTempSubdirectory("rivet-imported-schema-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);

            var first = RunPass(workDirectory.FullName, sourcePath, "first");
            AssertSchemaSurface(source, first);

            var firstPath = Path.Combine(workDirectory.FullName, "first", "openapi.json");
            var second = RunPass(workDirectory.FullName, firstPath, "second");
            AssertSchemaSurface(source, second);
            AssertSchemaSurface(first, second);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Edited_Imported_Source_Fails_Instead_Of_Emitting_Stale_Schema_Provenance()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Conflict", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "Payload": {
                    "type": "object",
                    "properties": {
                      "value": {
                        "type": "string",
                        "oneOf": [{ "type": "string" }, { "type": "integer" }]
                      }
                    }
                  }
                }
              },
              "paths": {
                "/payload": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "Payload",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/Payload" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;
        var workDirectory = Directory.CreateTempSubdirectory("rivet-imported-conflict-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            var generatedDirectory = Path.Combine(workDirectory.FullName, "generated");
            File.WriteAllText(sourcePath, spec);
            var import = CliRunner.RunCli(
                workDirectory.FullName,
                ["--from-openapi", sourcePath, "--output", generatedDirectory]
            );
            Assert.True(import.ExitCode == 0, import.StdErr);

            var payloadPath = Path.Combine(generatedDirectory, "Types", "Payload.cs");
            var payload = File.ReadAllText(payloadPath);
            var edited = payload.Replace("public string Value", "public long Value");
            Assert.NotEqual(payload, edited);
            File.WriteAllText(payloadPath, edited);

            var emit = CliRunner.RunCli(
                workDirectory.FullName,
                [
                    generatedDirectory,
                    "--openapi",
                    "--output",
                    Path.Combine(workDirectory.FullName, "out"),
                ]
            );

            Assert.Equal(1, emit.ExitCode);
            Assert.Contains(Diagnostics.ImportedSchemaProvenanceConflict, emit.StdErr);
            Assert.Contains("Types/Payload.cs", emit.StdErr);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Formatting_Imported_Source_Does_Not_Invalidate_Schema_Provenance()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Formatting", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "Payload": {
                    "type": "object",
                    "properties": {
                      "value": {
                        "type": "string",
                        "oneOf": [{ "type": "string" }, { "type": "integer" }]
                      }
                    }
                  }
                }
              },
              "paths": {}
            }
            """;
        var workDirectory = Directory.CreateTempSubdirectory("rivet-imported-formatting-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            var generatedDirectory = Path.Combine(workDirectory.FullName, "generated");
            File.WriteAllText(sourcePath, spec);
            var import = CliRunner.RunCli(
                workDirectory.FullName,
                ["--from-openapi", sourcePath, "--output", generatedDirectory]
            );
            Assert.True(import.ExitCode == 0, import.StdErr);

            var payloadPath = Path.Combine(generatedDirectory, "Types", "Payload.cs");
            File.AppendAllText(payloadPath, "\n\n");
            var emit = CliRunner.RunCli(
                workDirectory.FullName,
                [
                    generatedDirectory,
                    "--openapi",
                    "--output",
                    Path.Combine(workDirectory.FullName, "out"),
                ]
            );

            Assert.Equal(0, emit.ExitCode);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    private static JsonObject RunPass(string workingDirectory, string sourcePath, string name)
    {
        var sourceDirectory = Path.Combine(workingDirectory, $"{name}-source");
        var import = CliRunner.RunCli(
            workingDirectory,
            ["--from-openapi", sourcePath, "--output", sourceDirectory, "--namespace", "Generated"]
        );
        Assert.True(import.ExitCode == 0, import.StdErr);

        var outputDirectory = Path.Combine(workingDirectory, name);
        var emit = CliRunner.RunCli(
            workingDirectory,
            [sourceDirectory, "--openapi", "--output", outputDirectory]
        );
        Assert.True(emit.ExitCode == 0, emit.StdErr);
        return JsonNode
            .Parse(File.ReadAllText(Path.Combine(outputDirectory, "openapi.json")))!
            .AsObject();
    }

    private static void AssertSchemaSurface(JsonObject expected, JsonObject actual)
    {
        foreach (var name in new[] { "Derived", "Alias", "InlineList", "PortMap", "Payload" })
        {
            Assert.True(
                JsonNode.DeepEquals(
                    expected["components"]!["schemas"]![name],
                    actual["components"]!["schemas"]![name]
                ),
                $"Component schema '{name}' changed."
            );
        }

        var expectedOperation = expected["paths"]!["/items"]!["post"]!;
        var actualOperation = actual["paths"]!["/items"]!["post"]!;
        Assert.True(
            JsonNode.DeepEquals(
                expectedOperation["parameters"]![0]!["schema"],
                actualOperation["parameters"]![0]!["schema"]
            )
        );
        Assert.True(
            JsonNode.DeepEquals(
                expectedOperation["requestBody"]!["content"]!["application/json"]!["schema"],
                actualOperation["requestBody"]!["content"]!["application/json"]!["schema"]
            )
        );
        Assert.True(
            JsonNode.DeepEquals(
                expectedOperation["responses"]!["200"]!["content"]!["application/json"]!["schema"],
                actualOperation["responses"]!["200"]!["content"]!["application/json"]!["schema"]
            )
        );
    }
}
