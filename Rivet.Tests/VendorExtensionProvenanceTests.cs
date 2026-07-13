using System.Text.Json;
using System.Text.Json.Nodes;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class VendorExtensionProvenanceTests
{
    [Fact]
    public void Preserved_extensions_survive_the_public_disk_pipeline_and_fixed_point()
    {
        var source = JsonNode.Parse(PreservationSpec)!.AsObject();
        var workDirectory = Directory.CreateTempSubdirectory("rivet-vendor-extension-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, PreservationSpec);

            var firstSource = Path.Combine(workDirectory.FullName, "first-source");
            Import(workDirectory.FullName, sourcePath, firstSource);
            Assert.Contains(
                "RivetVendorExtension",
                File.ReadAllText(Path.Combine(firstSource, "RivetDocument.cs"))
            );
            var first = Emit(workDirectory.FullName, firstSource, "first-output");
            AssertPreservedExtensions(source, first);

            var firstPath = Path.Combine(workDirectory.FullName, "first-output", "openapi.json");
            var secondSource = Path.Combine(workDirectory.FullName, "second-source");
            Import(workDirectory.FullName, firstPath, secondSource);
            var second = Emit(workDirectory.FullName, secondSource, "second-output");
            AssertPreservedExtensions(source, second);
            AssertPreservedExtensions(first, second);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Opaque_component_schema_owns_its_preserved_extensions()
    {
        var workDirectory = Directory.CreateTempSubdirectory("rivet-opaque-extension-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, OpaqueSchemaPreservationSpec);
            var generated = Path.Combine(workDirectory.FullName, "generated");
            Import(workDirectory.FullName, sourcePath, generated);

            var documentSource = File.ReadAllText(Path.Combine(generated, "RivetDocument.cs"));
            Assert.Contains("RivetDocumentSchema", documentSource);
            Assert.DoesNotContain("RivetVendorExtension", documentSource);

            var emitted = Emit(workDirectory.FullName, generated, "output");
            Assert.True(
                emitted["components"]!["schemas"]!["Payload"]!["x-is-beta"]!.GetValue<bool>()
            );
            Assert.Equal(
                "standard",
                emitted["paths"]!["/payload"]!["get"]!["responses"]!["200"]!["content"]![
                    "application/json"
                ]!["schema"]!["items"]!["x-twilio"]!["pii"]!["handling"]!.GetValue<string>()
            );
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Reviewed_map_extensions_are_projected_to_standard_semantics()
    {
        var workDirectory = Directory.CreateTempSubdirectory("rivet-vendor-map-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, MapProjectionSpec);
            var generated = Path.Combine(workDirectory.FullName, "generated");
            Import(workDirectory.FullName, sourcePath, generated);
            var emitted = Emit(workDirectory.FullName, generated, "output");

            var operation = emitted["paths"]!["/mapped"]!["get"]!;
            Assert.True(operation["deprecated"]!.GetValue<bool>());
            Assert.True(operation["parameters"]![0]!["deprecated"]!.GetValue<bool>());
            var schema = emitted["components"]!["schemas"]!["Mapped"]!;
            Assert.True(schema["deprecated"]!.GetValue<bool>());
            Assert.True(schema["properties"]!["value"]!["readOnly"]!.GetValue<bool>());
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Extension_names_inside_payload_values_are_opaque_data()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Opaque payload", "version": "1" },
              "paths": {
                "/payload": {
                  "post": {
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "example": {
                            "x-desc": "payload value",
                            "x-twilio": { "pii": "payload metadata" }
                          },
                          "schema": { "type": "object" }
                        }
                      }
                    },
                    "responses": { "204": { "description": "Done" } }
                  }
                }
              }
            }
            """;
        var workDirectory = Directory.CreateTempSubdirectory("rivet-vendor-payload-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);
            var generated = Path.Combine(workDirectory.FullName, "generated");
            Import(workDirectory.FullName, sourcePath, generated);
            var emitted = Emit(workDirectory.FullName, generated, "output");
            var example = emitted["paths"]!["/payload"]!["post"]!["requestBody"]!["content"]![
                "application/json"
            ]!["example"]!;

            Assert.Equal("payload value", example["x-desc"]!.GetValue<string>());
            Assert.Equal("payload metadata", example["x-twilio"]!["pii"]!.GetValue<string>());
            Assert.Null(example["description"]);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Named_Example_Object_Extensions_Are_Handled_Without_Inspecting_Their_Value()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Named example", "version": "1" },
              "paths": {},
              "components": {
                "examples": {
                  "Named": {
                    "x-desc": "Example description",
                    "x-twilio": { "owner": true },
                    "value": {
                      "x-desc": "payload value",
                      "x-twilio": { "owner": false }
                    }
                  }
                }
              }
            }
            """;
        var workDirectory = Directory.CreateTempSubdirectory("rivet-vendor-named-example-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);
            var generated = Path.Combine(workDirectory.FullName, "generated");
            Import(workDirectory.FullName, sourcePath, generated);
            var emitted = Emit(workDirectory.FullName, generated, "output");
            var example = emitted["components"]!["examples"]!["Named"]!;

            Assert.Equal("Example description", example["description"]!.GetValue<string>());
            Assert.True(example["x-twilio"]!["owner"]!.GetValue<bool>());
            Assert.Equal("payload value", example["value"]!["x-desc"]!.GetValue<string>());
            Assert.False(example["value"]!["x-twilio"]!["owner"]!.GetValue<bool>());
            Assert.Null(example["value"]!["description"]);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Swagger_Response_Example_Maps_Are_Opaque_Payloads()
    {
        const string spec = """
            {
              "swagger": "2.0",
              "info": { "title": "Swagger payload", "version": "1" },
              "produces": ["application/json"],
              "paths": {
                "/payload": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "Payload",
                        "schema": { "type": "object" },
                        "examples": {
                          "application/json": {
                            "x-desc": "payload value",
                            "x-twilio": { "pii": "payload metadata" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;
        var workDirectory = Directory.CreateTempSubdirectory("rivet-vendor-swagger-example-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);
            var generated = Path.Combine(workDirectory.FullName, "generated");
            Import(workDirectory.FullName, sourcePath, generated);
            var emitted = Emit(workDirectory.FullName, generated, "output");
            var example = emitted["paths"]!["/payload"]!["get"]!["responses"]!["200"]!["content"]![
                "application/json"
            ]!["example"]!;

            Assert.Equal("payload value", example["x-desc"]!.GetValue<string>());
            Assert.Equal("payload metadata", example["x-twilio"]!["pii"]!.GetValue<string>());
            Assert.Null(example["description"]);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Swagger_component_extension_owner_is_normalized_to_the_emitted_Oas_pointer()
    {
        var workDirectory = Directory.CreateTempSubdirectory("rivet-vendor-swagger-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, SwaggerPreservationSpec);
            var generated = Path.Combine(workDirectory.FullName, "generated");
            Import(workDirectory.FullName, sourcePath, generated);
            var emitted = Emit(workDirectory.FullName, generated, "output");

            Assert.True(
                emitted["components"]!["schemas"]!["Payload"]!["x-is-beta"]!.GetValue<bool>()
            );
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Emission_fails_when_a_preserved_extension_owner_cannot_be_attached()
    {
        var provenance = new OpenApiDocumentProvenance(
            new OpenApiInfoProvenance("Broken", "1"),
            [],
            null,
            [],
            [],
            VendorExtensions:
            [
                new OpenApiVendorExtensionProvenance(
                    "#/components/schemas/Missing",
                    "x-is-beta",
                    "true"
                ),
            ]
        );

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            OpenApiEmitter.Emit(
                [],
                new Dictionary<string, TsTypeDefinition>(),
                new Dictionary<string, TsType.Brand>(),
                new Dictionary<string, TsType>(),
                null,
                new OpenApiDocumentInfo(Provenance: provenance)
            )
        );

        Assert.Contains("emitted owner '#/components/schemas/Missing'", exception.Message);
    }

    [Fact]
    public void Comparator_accepts_all_reviewed_map_values_when_standard_semantics_match()
    {
        var original = CreateComparatorDocument();
        var reemitted = original.DeepClone().AsObject();
        RemoveReviewedExtensions(reemitted);

        var result = RunDiff(original, reemitted);

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("loss")]
    [InlineData("change")]
    public void Comparator_detects_preserved_extension_mutations(string mutation)
    {
        var original = CreateComparatorDocument();
        var reemitted = original.DeepClone().AsObject();
        var extensionOwner = reemitted["components"]!["schemas"]!["Mapped"]!.AsObject();
        if (mutation == "loss")
        {
            extensionOwner.Remove("x-is-beta");
        }
        else
        {
            extensionOwner["x-is-beta"] = false;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.VendorPreserveFindings > 0);
    }

    [Theory]
    [InlineData("x-is-deprecated")]
    [InlineData("x-read-only")]
    [InlineData("x-ms-summary")]
    [InlineData("x-oauthpermissions")]
    public void Comparator_detects_reviewed_map_mismatches(string extension)
    {
        var original = CreateComparatorDocument();
        var reemitted = original.DeepClone().AsObject();
        RemoveReviewedExtensions(reemitted);
        switch (extension)
        {
            case "x-is-deprecated":
                reemitted["paths"]!["/mapped"]!["get"]!["deprecated"] = false;
                break;
            case "x-read-only":
                reemitted["components"]!["schemas"]!["Mapped"]!["properties"]!["value"]![
                    "readOnly"
                ] = false;
                break;
            case "x-ms-summary":
                reemitted["components"]!["schemas"]!["Mapped"]!["description"] = "Changed";
                break;
            case "x-oauthpermissions":
                reemitted["paths"]!["/mapped"]!["get"]!["security"]![0]!["oauth2"] = new JsonArray(
                    "write"
                );
                break;
        }

        var result = RunDiff(original, reemitted);

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.VendorMapFindings > 0);
    }

    private static void AssertPreservedExtensions(JsonObject expected, JsonObject actual)
    {
        AssertExtension(expected, actual, "x-twilio");
        var expectedOperation = expected["paths"]!["/things/{id}"]!["post"]!;
        var actualOperation = actual["paths"]!["/things/{id}"]!["post"]!;
        AssertExtension(expectedOperation, actualOperation, "x-ds-examples");
        AssertExtension(
            expectedOperation["parameters"]![0]!,
            actualOperation["parameters"]![0]!,
            "x-is-beta"
        );
        AssertExtension(
            expectedOperation["requestBody"]!,
            actualOperation["requestBody"]!,
            "x-twilio"
        );
        AssertExtension(
            expected["components"]!["schemas"]!["Payload"]!,
            actual["components"]!["schemas"]!["Payload"]!,
            "x-enum-elements"
        );
    }

    private static void AssertExtension(JsonNode expected, JsonNode actual, string name) =>
        Assert.True(
            JsonNode.DeepEquals(expected[name], actual[name]),
            $"Extension '{name}' changed. Expected {expected[name]}, actual {actual[name]}."
        );

    private static void Import(string workingDirectory, string sourcePath, string outputDirectory)
    {
        var result = CliRunner.RunCli(
            workingDirectory,
            ["--from-openapi", sourcePath, "--output", outputDirectory, "--namespace", "Generated"]
        );
        Assert.True(result.ExitCode == 0, result.StdErr);
    }

    private static JsonObject Emit(
        string workingDirectory,
        string sourceDirectory,
        string outputName
    )
    {
        var outputDirectory = Path.Combine(workingDirectory, outputName);
        var result = CliRunner.RunCli(
            workingDirectory,
            [sourceDirectory, "--openapi", "--output", outputDirectory]
        );
        Assert.True(result.ExitCode == 0, result.StdErr);
        return JsonNode
            .Parse(File.ReadAllText(Path.Combine(outputDirectory, "openapi.json")))!
            .AsObject();
    }

    private static JsonObject CreateComparatorDocument() =>
        JsonNode
            .Parse(
                """
                {
                  "openapi": "3.1.0",
                  "info": { "title": "Mapped", "version": "1" },
                  "paths": {
                    "/mapped": {
                      "get": {
                        "operationId": "mapped",
                        "deprecated": true,
                        "x-is-deprecated": true,
                        "x-oauthpermissions": ["read"],
                        "x-ds-in-sdk": true,
                        "security": [{ "oauth2": ["read"] }],
                        "responses": { "204": { "description": "Done" } }
                      }
                    }
                  },
                  "components": {
                    "schemas": {
                      "Mapped": {
                        "type": "object",
                        "description": "Mapped summary",
                        "x-ms-summary": "Mapped summary",
                        "x-is-beta": true,
                        "properties": {
                          "value": {
                            "type": "string",
                            "readOnly": true,
                            "x-read-only": true
                          },
                          "alias": {
                            "type": "string",
                            "x-desc": "Mapped alias"
                          }
                        }
                      }
                    },
                    "securitySchemes": {
                      "oauth2": {
                        "type": "oauth2",
                        "flows": {
                          "clientCredentials": {
                            "tokenUrl": "https://example.test/token",
                            "scopes": { "read": "Read" }
                          }
                        }
                      }
                    }
                  }
                }
                """
            )!
            .AsObject();

    private static void RemoveReviewedExtensions(JsonObject document)
    {
        document["paths"]!["/mapped"]!["get"]!.AsObject().Remove("x-is-deprecated");
        document["paths"]!["/mapped"]!["get"]!.AsObject().Remove("x-oauthpermissions");
        document["components"]!["schemas"]!["Mapped"]!.AsObject().Remove("x-ms-summary");
        document["components"]!["schemas"]!["Mapped"]!["properties"]!["value"]!
            .AsObject()
            .Remove("x-read-only");
        var alias = document["components"]!["schemas"]!["Mapped"]!["properties"]![
            "alias"
        ]!.AsObject();
        alias["description"] = alias["x-desc"]!.DeepClone();
        alias.Remove("x-desc");
    }

    private static DiffResult RunDiff(JsonObject original, JsonObject reemitted)
    {
        var workDirectory = Directory.CreateTempSubdirectory("rivet-vendor-diff-");
        try
        {
            var originalPath = Path.Combine(workDirectory.FullName, "original.json");
            var reemittedPath = Path.Combine(workDirectory.FullName, "reemitted.json");
            var summaryPath = Path.Combine(workDirectory.FullName, "summary.json");
            File.WriteAllText(originalPath, original.ToJsonString());
            File.WriteAllText(reemittedPath, reemitted.ToJsonString());
            var process = CliRunner.Run(
                workDirectory.FullName,
                "python3",
                [
                    CliRunner.RepoPath("tools", "roundtrip-diff.py"),
                    originalPath,
                    reemittedPath,
                    "--summary-json",
                    summaryPath,
                ]
            );
            using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
            return new DiffResult(
                process.ExitCode,
                FindingCount(summary.RootElement, "vendor-extension-preserve"),
                FindingCount(summary.RootElement, "vendor-extension-map")
            );
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    private static int FindingCount(JsonElement summary, string category) =>
        new[] { "documentFindings", "opFindings", "schemaFindings" }
            .Select(scope => summary.GetProperty(scope))
            .Sum(findings =>
                findings.TryGetProperty(category, out var count) ? count.GetInt32() : 0
            );

    private sealed record DiffResult(
        int ExitCode,
        int VendorPreserveFindings,
        int VendorMapFindings
    );

    private const string PreservationSpec = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Extensions", "version": "1" },
          "x-twilio": { "region": "eu", "flags": [true, null, 3.5] },
          "paths": {
            "/things/{id}": {
              "post": {
                "operationId": "createThing",
                "x-ds-examples": [{ "name": "one", "request": { "value": "a" } }],
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "x-is-beta": true, "schema": { "type": "string" } }
                ],
                "requestBody": {
                  "required": true,
                  "x-twilio": { "conditional": ["value"], "pii": false },
                  "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Payload" } } }
                },
                "responses": { "204": { "description": "Created" } }
              }
            }
          },
          "components": {
            "schemas": {
              "Payload": {
                "type": "object",
                "x-enum-elements": [{ "name": "Active", "value": "active", "description": "Active value" }],
                "properties": { "value": { "type": "string" } },
                "required": ["value"]
              }
            }
          }
        }
        """;

    private const string MapProjectionSpec = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Mapped", "version": "1" },
          "paths": {
            "/mapped": {
              "get": {
                "operationId": "mapped",
                "x-is-deprecated": true,
                "parameters": [
                  { "name": "filter", "in": "query", "x-is-deprecated": true, "schema": { "type": "string" } }
                ],
                "responses": { "204": { "description": "Done" } }
              }
            }
          },
          "components": {
            "schemas": {
              "Mapped": {
                "type": "object",
                "x-is-deprecated": true,
                "properties": {
                  "value": { "type": "string", "x-read-only": true }
                }
              }
            }
          }
        }
        """;

    private const string OpaqueSchemaPreservationSpec = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Opaque extension", "version": "1" },
          "paths": {
            "/payload": {
              "get": {
                "operationId": "getPayload",
                "responses": {
                  "200": {
                    "description": "Payload",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "array",
                          "items": {
                            "$ref": "#/components/schemas/Payload",
                            "x-twilio": { "pii": { "handling": "standard" } }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
          },
          "components": {
            "schemas": {
              "Payload": {
                "type": "object",
                "x-is-beta": true,
                "properties": {
                  "child": { "$ref": "#/components/schemas/Child" }
                }
              },
              "Child": {
                "type": "object",
                "properties": { "value": { "type": "string" } }
              }
            }
          }
        }
        """;

    private const string SwaggerPreservationSpec = """
        {
          "swagger": "2.0",
          "info": { "title": "Swagger extensions", "version": "1" },
          "paths": {
            "/payload": {
              "get": {
                "operationId": "getPayload",
                "produces": ["application/json"],
                "responses": {
                  "200": {
                    "description": "Payload",
                    "schema": { "$ref": "#/definitions/Payload" }
                  }
                }
              }
            }
          },
          "definitions": {
            "Payload": {
              "type": "object",
              "x-is-beta": true,
              "properties": { "value": { "type": "string" } }
            }
          }
        }
        """;
}
