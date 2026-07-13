using System.Text.Json.Nodes;
using Rivet.Tool.Analysis;

namespace Rivet.Tests;

public sealed class ReusableComponentIdentityRoundTripTests
{
    [Fact]
    public void Used_Shared_Unused_And_Escaped_Parameters_And_Responses_Reach_A_Fixed_Point()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reusable components", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "Pet": {
                    "type": "object",
                    "properties": { "name": { "type": "string" } },
                    "required": ["name"]
                  }
                },
                "parameters": {
                  "Used/Parameter": {
                    "name": "petId",
                    "in": "path",
                    "required": true,
                    "description": "Escaped parameter",
                    "schema": { "type": "string", "format": "uuid" }
                  },
                  "Shared.Parameter": {
                    "name": "cursor",
                    "in": "query",
                    "required": false,
                    "schema": { "type": "integer", "format": "int32", "minimum": 0 }
                  },
                  "Unused~Parameter": {
                    "name": "unused",
                    "in": "header",
                    "schema": { "type": "string" }
                  }
                },
                "responses": {
                  "Used/Response": {
                    "description": "Escaped response",
                    "content": {
                      "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } }
                    }
                  },
                  "Shared.Response": {
                    "description": "Shared response",
                    "headers": {
                      "X-Next": { "schema": { "type": "string" } }
                    }
                  },
                  "Unused~Response": {
                    "description": "Unused response",
                    "content": {
                      "text/plain": { "schema": { "type": "string" } }
                    }
                  }
                }
              },
              "paths": {
                "/pets/{petId}": {
                  "get": {
                    "parameters": [
                      { "$ref": "#/components/parameters/Used~1Parameter" },
                      { "$ref": "#/components/parameters/Shared.Parameter" }
                    ],
                    "responses": {
                      "200": { "$ref": "#/components/responses/Used~1Response" },
                      "default": { "$ref": "#/components/responses/Shared.Response" }
                    }
                  }
                },
                "/shared": {
                  "get": {
                    "parameters": [
                      { "$ref": "#/components/parameters/Shared.Parameter" }
                    ],
                    "responses": {
                      "200": { "$ref": "#/components/responses/Shared.Response" }
                    }
                  }
                },
                "/override": {
                  "parameters": [
                    { "$ref": "#/components/parameters/Shared.Parameter" }
                  ],
                  "get": {
                    "parameters": [
                      {
                        "name": "cursor",
                        "in": "query",
                        "description": "Inline override",
                        "schema": { "type": "integer", "format": "int32" }
                      }
                    ],
                    "responses": { "204": { "description": "No content" } }
                  }
                }
              }
            }
            """;

        var workDirectory = Directory.CreateTempSubdirectory("rivet-reusable-components-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);

            var first = RunPass(workDirectory.FullName, sourcePath, "first");
            AssertReusableComponents(first);

            var secondPath = Path.Combine(workDirectory.FullName, "first.json");
            File.WriteAllText(secondPath, first.ToJsonString());
            var second = RunPass(workDirectory.FullName, secondPath, "second");
            AssertReusableComponents(second);

            Assert.True(
                JsonNode.DeepEquals(
                    first["components"]!["parameters"],
                    second["components"]!["parameters"]
                )
            );
            Assert.True(
                JsonNode.DeepEquals(
                    first["components"]!["responses"],
                    second["components"]!["responses"]
                )
            );
            Assert.True(JsonNode.DeepEquals(first["paths"], second["paths"]));
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Missing_Component_Definition_Provenance_Falls_Back_To_Inline_Use_Sites()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Fallback", "version": "1.0.0" },
              "components": {
                "parameters": {
                  "Limit": {
                    "name": "limit",
                    "in": "query",
                    "description": "Page limit",
                    "schema": { "type": "integer", "format": "int32" }
                  }
                },
                "responses": {
                  "Accepted": { "description": "Accepted for processing" }
                }
              },
              "paths": {
                "/jobs": {
                  "get": {
                    "parameters": [{ "$ref": "#/components/parameters/Limit" }],
                    "responses": { "202": { "$ref": "#/components/responses/Accepted" } }
                  }
                }
              }
            }
            """;

        var workDirectory = Directory.CreateTempSubdirectory("rivet-component-fallback-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);
            var generatedDirectory = Path.Combine(workDirectory.FullName, "generated");
            var import = CliRunner.RunCli(
                workDirectory.FullName,
                [
                    "--from-openapi",
                    sourcePath,
                    "--output",
                    generatedDirectory,
                    "--namespace",
                    "Generated",
                ]
            );
            Assert.True(import.ExitCode == 0, import.StdErr);

            var documentPath = Path.Combine(generatedDirectory, "RivetDocument.cs");
            File.WriteAllLines(
                documentPath,
                File.ReadAllLines(documentPath)
                    .Where(line =>
                        !line.Contains("RivetDocumentParameter", StringComparison.Ordinal)
                        && !line.Contains("RivetDocumentResponse", StringComparison.Ordinal)
                    )
            );

            var outputDirectory = Path.Combine(workDirectory.FullName, "output");
            var emit = CliRunner.RunCli(
                workDirectory.FullName,
                [generatedDirectory, "--openapi", "--output", outputDirectory]
            );
            Assert.True(emit.ExitCode == 0, emit.StdErr);
            var document = JsonNode
                .Parse(File.ReadAllText(Path.Combine(outputDirectory, "openapi.json")))!
                .AsObject();

            var operation = document["paths"]!["/jobs"]!["get"]!;
            Assert.Equal("limit", operation["parameters"]![0]!["name"]!.GetValue<string>());
            Assert.Equal(
                "integer",
                operation["parameters"]![0]!["schema"]!["type"]!.GetValue<string>()
            );
            Assert.Equal(
                "Accepted for processing",
                operation["responses"]!["202"]!["description"]!.GetValue<string>()
            );
            Assert.Null(document["components"]?["parameters"]);
            Assert.Null(document["components"]?["responses"]);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Named_Array_Of_Refs_Survives_Generated_CSharp_And_Reaches_A_Fixed_Point()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Named arrays", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "Pet": {
                    "type": "object",
                    "properties": { "name": { "type": "string" } },
                    "required": ["name"]
                  },
                  "Pet/List": {
                    "type": "array",
                    "description": "Named pet collection",
                    "items": { "$ref": "#/components/schemas/Pet" }
                  }
                }
              },
              "paths": {
                "/pets": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "Pets",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/Pet~1List" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var workDirectory = Directory.CreateTempSubdirectory("rivet-named-array-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);

            var first = RunPass(
                workDirectory.FullName,
                sourcePath,
                "array-first",
                out var generated
            );
            Assert.Contains("RivetGeneratedSchema(\"PetList\", \"Pet/List\"", generated);
            Assert.Contains("ResponseContent<List<Pet>>", generated);
            Assert.Contains("schemaRef: \"PetList\"", generated);
            AssertNamedArray(first);

            var secondPath = Path.Combine(workDirectory.FullName, "array-first.json");
            File.WriteAllText(secondPath, first.ToJsonString());
            var second = RunPass(workDirectory.FullName, secondPath, "array-second");
            AssertNamedArray(second);
            Assert.True(
                JsonNode.DeepEquals(
                    first["components"]!["schemas"]!["Pet/List"],
                    second["components"]!["schemas"]!["Pet/List"]
                )
            );
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Recovered_Component_Provenance_Rejects_Non_Object_Json()
    {
        var compilation = CompilationHelper.CreateCompilation(
            """
            using Rivet;

            [assembly: RivetDocumentInfo("Invalid provenance", "1.0.0")]
            [assembly: RivetDocumentParameter(0, "Broken", "[]")]
            """
        );

        var exception = Assert.Throws<ContractAnalysisException>(() =>
            OpenApiProvenanceWalker.Walk(compilation)
        );
        Assert.Contains("component parameter JSON", exception.Message);
        Assert.Contains("not an object", exception.Message);
    }

    [Fact]
    public void Referenced_Unsupported_Component_Gets_An_Untyped_Identity_Fallback()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Fallback identities", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "BaseId": { "type": "string", "format": "uuid" },
                  "Constrained/Id": {
                    "allOf": [
                      { "minLength": 36, "type": "string" },
                      { "$ref": "#/components/schemas/BaseId" }
                    ]
                  },
                  "IdList": {
                    "type": "array",
                    "items": { "$ref": "#/components/schemas/Constrained~1Id" }
                  }
                },
                "parameters": {
                  "Id": {
                    "name": "id",
                    "in": "query",
                    "schema": { "$ref": "#/components/schemas/Constrained~1Id" }
                  }
                }
              },
              "paths": {
                "/ids": {
                  "get": {
                    "parameters": [{ "$ref": "#/components/parameters/Id" }],
                    "responses": {
                      "200": {
                        "description": "IDs",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/IdList" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var imported = CompilationHelper.Import(spec, "FallbackIdentities");
        Assert.Contains(
            imported.Warnings,
            warning =>
                warning.Contains("RIV3010", StringComparison.Ordinal)
                && warning.Contains("Constrained/Id", StringComparison.Ordinal)
        );

        var workDirectory = Directory.CreateTempSubdirectory("rivet-fallback-identities-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);
            var emitted = RunPass(workDirectory.FullName, sourcePath, "fallback-identities");

            Assert.NotNull(emitted["components"]!["schemas"]!["Constrained/Id"]);
            Assert.Equal(
                "#/components/schemas/Constrained~1Id",
                emitted["components"]!["schemas"]!["IdList"]!["items"]!["$ref"]!.GetValue<string>()
            );
            Assert.Equal(
                "#/components/schemas/Constrained~1Id",
                emitted["components"]!["parameters"]!["Id"]!["schema"]!["$ref"]!.GetValue<string>()
            );
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Preserved_Component_With_Missing_Schema_Gets_An_Untyped_Emission_Fallback()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Missing schema", "version": "1.0.0" },
              "components": {
                "parameters": {
                  "Filter": {
                    "name": "filter",
                    "in": "query",
                    "schema": { "$ref": "#/components/schemas/MissingFilter" }
                  }
                }
              },
              "paths": {
                "/items": {
                  "get": {
                    "parameters": [{ "$ref": "#/components/parameters/Filter" }],
                    "responses": { "204": { "description": "No content" } }
                  }
                }
              }
            }
            """;

        var workDirectory = Directory.CreateTempSubdirectory("rivet-missing-schema-fallback-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);
            var emitted = RunPass(workDirectory.FullName, sourcePath, "missing-schema");

            Assert.NotNull(emitted["components"]!["schemas"]!["MissingFilter"]);
            Assert.Equal(
                "#/components/schemas/MissingFilter",
                emitted["components"]!["parameters"]!["Filter"]!["schema"]![
                    "$ref"
                ]!.GetValue<string>()
            );
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    private static JsonObject RunPass(string workingDirectory, string sourcePath, string pass)
    {
        return RunPass(workingDirectory, sourcePath, pass, out _);
    }

    private static JsonObject RunPass(
        string workingDirectory,
        string sourcePath,
        string pass,
        out string generatedSource
    )
    {
        var generatedDirectory = Path.Combine(workingDirectory, $"generated-{pass}");
        var import = CliRunner.RunCli(
            workingDirectory,
            [
                "--from-openapi",
                sourcePath,
                "--output",
                generatedDirectory,
                "--namespace",
                "Generated",
            ]
        );
        Assert.True(import.ExitCode == 0, import.StdErr);
        generatedSource = string.Join(
            "\n",
            Directory
                .GetFiles(generatedDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
        );

        var outputDirectory = Path.Combine(workingDirectory, $"output-{pass}");
        var emit = CliRunner.RunCli(
            workingDirectory,
            [generatedDirectory, "--openapi", "--output", outputDirectory]
        );
        Assert.True(emit.ExitCode == 0, emit.StdErr);
        return JsonNode
            .Parse(File.ReadAllText(Path.Combine(outputDirectory, "openapi.json")))!
            .AsObject();
    }

    private static void AssertNamedArray(JsonObject document)
    {
        var schema = document["components"]!["schemas"]!["Pet/List"]!;
        Assert.Equal("array", schema["type"]!.GetValue<string>());
        Assert.Equal("#/components/schemas/Pet", schema["items"]!["$ref"]!.GetValue<string>());
        Assert.Equal(
            "#/components/schemas/Pet~1List",
            document["paths"]!["/pets"]!["get"]!["responses"]!["200"]!["content"]![
                "application/json"
            ]!["schema"]!["$ref"]!.GetValue<string>()
        );
    }

    private static void AssertReusableComponents(JsonObject document)
    {
        var parameters = document["components"]!["parameters"]!.AsObject();
        Assert.Equal(3, parameters.Count);
        Assert.Equal(
            "uuid",
            parameters["Used/Parameter"]!["schema"]!["format"]!.GetValue<string>()
        );
        Assert.Equal(0, parameters["Shared.Parameter"]!["schema"]!["minimum"]!.GetValue<int>());
        Assert.Equal("header", parameters["Unused~Parameter"]!["in"]!.GetValue<string>());

        var responses = document["components"]!["responses"]!.AsObject();
        Assert.Equal(3, responses.Count);
        Assert.Equal(
            "#/components/schemas/Pet",
            responses["Used/Response"]!["content"]!["application/json"]!["schema"]![
                "$ref"
            ]!.GetValue<string>()
        );
        Assert.Equal(
            "string",
            responses["Shared.Response"]!["headers"]!["X-Next"]!["schema"]![
                "type"
            ]!.GetValue<string>()
        );
        Assert.Equal(
            "Unused response",
            responses["Unused~Response"]!["description"]!.GetValue<string>()
        );

        Assert.Equal(
            "#/components/parameters/Used~1Parameter",
            document["paths"]!["/pets/{petId}"]!["get"]!["parameters"]![0]![
                "$ref"
            ]!.GetValue<string>()
        );
        Assert.Equal(
            "#/components/parameters/Shared.Parameter",
            document["paths"]!["/shared"]!["get"]!["parameters"]![0]!["$ref"]!.GetValue<string>()
        );
        Assert.Equal(
            "#/components/responses/Used~1Response",
            document["paths"]!["/pets/{petId}"]!["get"]!["responses"]!["200"]![
                "$ref"
            ]!.GetValue<string>()
        );
        Assert.Equal(
            "#/components/responses/Shared.Response",
            document["paths"]!["/shared"]!["get"]!["responses"]!["200"]!["$ref"]!.GetValue<string>()
        );
        Assert.Equal(
            "Inline override",
            document["paths"]!["/override"]!["get"]!["parameters"]![0]![
                "description"
            ]!.GetValue<string>()
        );
        Assert.Null(document["paths"]!["/override"]!["get"]!["parameters"]![0]!["$ref"]);
    }
}
