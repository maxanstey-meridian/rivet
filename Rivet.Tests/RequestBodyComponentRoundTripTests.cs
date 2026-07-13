using System.Text.Json.Nodes;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class RequestBodyComponentRoundTripTests
{
    [Fact]
    public void Request_Body_Reference_Identity_Survives_Contract_Json()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Contract JSON", "version": "1.0.0" },
              "components": {
                "requestBodies": {
                  "Exact/Body": {
                    "required": true,
                    "content": { "application/json": { "schema": { "type": "string" } } }
                  }
                }
              },
              "paths": {
                "/exact": {
                  "post": {
                    "requestBody": { "$ref": "#/components/requestBodies/Exact~1Body" },
                    "responses": { "204": { "description": "Accepted" } }
                  }
                }
              }
            }
            """;

        var imported = CompilationHelper.Import(spec, "Generated");
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var provenance = OpenApiProvenanceWalker.Walk(compilation, walker);
        var contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            endpoints
        );
        Assert.Contains("\"requestBodyComponentId\": \"Exact/Body\"", contractJson);

        var read = JsonContractReader.Read(contractJson);
        var emitted = JsonNode.Parse(
            OpenApiEmitter.Emit(
                read.Endpoints,
                read.Types.ToDictionary(type => type.Name),
                read.Brands,
                read.Enums,
                security: null,
                new OpenApiDocumentInfo(Provenance: provenance)
            )
        )!;
        Assert.Equal(
            "#/components/requestBodies/Exact~1Body",
            emitted["paths"]!["/exact"]!["post"]!["requestBody"]!["$ref"]!.GetValue<string>()
        );
    }

    [Fact]
    public void Used_Shared_And_Unused_Request_Bodies_Survive_Public_Pipeline_And_Second_Pass()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Request body components", "version": "1.0.0" },
              "components": {
                "examples": {
                  "PetExample": { "summary": "Pet sample", "value": { "name": "Ada" } }
                },
                "schemas": {
                  "Pet": {
                    "type": "object",
                    "properties": { "name": { "type": "string" } },
                    "required": ["name"]
                  },
                  "SharedPayload": {
                    "type": "object",
                    "properties": { "value": { "type": "integer", "format": "int32" } },
                    "required": ["value"]
                  }
                },
                "requestBodies": {
                  "Used/Body": {
                    "description": "Used component",
                    "required": true,
                    "content": {
                      "application/json": {
                        "schema": { "$ref": "#/components/schemas/Pet" },
                        "examples": { "pet": { "$ref": "#/components/examples/PetExample" } }
                      },
                      "text/plain": { "schema": { "type": "string" } }
                    }
                  },
                  "Shared.Body": {
                    "description": "Shared component",
                    "required": false,
                    "content": {
                      "application/json": { "schema": { "$ref": "#/components/schemas/SharedPayload" } }
                    }
                  },
                  "Unused~Body": {
                    "description": "Unused component",
                    "required": false,
                    "content": {
                      "application/octet-stream": { "schema": { "type": "string", "format": "binary" } },
                      "application/vnd.empty": {
                        "example": { "accepted": true }
                      }
                    }
                  }
                }
              },
              "paths": {
                "/pets": {
                  "post": {
                    "operationId": "createPet",
                    "tags": ["Pets"],
                    "requestBody": { "$ref": "#/components/requestBodies/Used~1Body" },
                    "responses": { "204": { "description": "Created" } }
                  }
                },
                "/shared/one": {
                  "post": {
                    "operationId": "shareOne",
                    "tags": ["Shared"],
                    "requestBody": { "$ref": "#/components/requestBodies/Shared.Body" },
                    "responses": { "204": { "description": "Accepted" } }
                  }
                },
                "/shared/two": {
                  "put": {
                    "operationId": "shareTwo",
                    "tags": ["Shared"],
                    "requestBody": { "$ref": "#/components/requestBodies/Shared.Body" },
                    "responses": { "204": { "description": "Accepted" } }
                  }
                }
              }
            }
            """;

        var workDirectory = Directory.CreateTempSubdirectory("rivet-request-bodies-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);

            var first = RunPass(workDirectory.FullName, sourcePath, "first", out var generated);
            Assert.Contains("RivetDocumentRequestBody(0, \"Used/Body\"", generated);
            Assert.Contains("RivetDocumentRequestBody(2, \"Unused~Body\"", generated);
            AssertRequestBodyComponents(first);

            var secondPath = Path.Combine(workDirectory.FullName, "first.json");
            File.WriteAllText(secondPath, first.ToJsonString());
            var second = RunPass(workDirectory.FullName, secondPath, "second", out _);
            AssertRequestBodyComponents(second);

            Assert.True(
                JsonNode.DeepEquals(
                    first["components"]!["requestBodies"],
                    second["components"]!["requestBodies"]
                )
            );
            foreach (
                var (path, method) in new[]
                {
                    ("/pets", "post"),
                    ("/shared/one", "post"),
                    ("/shared/two", "put"),
                }
            )
            {
                Assert.True(
                    JsonNode.DeepEquals(
                        first["paths"]![path]![method]!["requestBody"],
                        second["paths"]![path]![method]!["requestBody"]
                    )
                );
            }
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Empty_Object_Request_Body_Reaches_A_Public_Pipeline_Fixed_Point()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Empty object request", "version": "1.0.0" },
              "paths": {
                "/empty": {
                  "post": {
                    "operationId": "postEmpty",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": { "type": "object", "properties": {} }
                        }
                      }
                    },
                    "responses": { "204": { "description": "Accepted" } }
                  }
                }
              }
            }
            """;

        var workDirectory = Directory.CreateTempSubdirectory("rivet-empty-object-request-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);

            var first = RunPass(workDirectory.FullName, sourcePath, "first", out _);
            var firstSchema = first["paths"]!["/empty"]!["post"]!["requestBody"]!["content"]![
                "application/json"
            ]!["schema"]!;
            Assert.Equal("object", firstSchema["type"]!.GetValue<string>());
            Assert.Empty(firstSchema["properties"]!.AsObject());
            Assert.Null(firstSchema["additionalProperties"]);
            Assert.Null(firstSchema["x-rivet-csharp-type"]);

            var secondPath = Path.Combine(workDirectory.FullName, "first.json");
            File.WriteAllText(secondPath, first.ToJsonString());
            var second = RunPass(workDirectory.FullName, secondPath, "second", out _);
            var secondSchema = second["paths"]!["/empty"]!["post"]!["requestBody"]!["content"]![
                "application/json"
            ]!["schema"]!;

            Assert.True(
                JsonNode.DeepEquals(firstSchema, secondSchema),
                $"Request schema did not reach a fixed point.\nFirst: {firstSchema}\nSecond: {secondSchema}"
            );
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Unconstrained_Array_Request_Body_Reaches_A_Public_Pipeline_Fixed_Point()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Unconstrained array request", "version": "1.0.0" },
              "paths": {
                "/array": {
                  "post": {
                    "operationId": "postArray",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": { "type": "array", "items": {} }
                        }
                      }
                    },
                    "responses": { "204": { "description": "Accepted" } }
                  }
                }
              }
            }
            """;

        var workDirectory = Directory.CreateTempSubdirectory("rivet-unconstrained-array-request-");
        try
        {
            var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);

            var first = RunPass(workDirectory.FullName, sourcePath, "first", out _);
            var firstSchema = first["paths"]!["/array"]!["post"]!["requestBody"]!["content"]![
                "application/json"
            ]!["schema"]!;
            Assert.Equal("array", firstSchema["type"]!.GetValue<string>());
            Assert.Empty(firstSchema["items"]!.AsObject());
            Assert.Null(firstSchema["x-rivet-csharp-type"]);

            var secondPath = Path.Combine(workDirectory.FullName, "first.json");
            File.WriteAllText(secondPath, first.ToJsonString());
            var second = RunPass(workDirectory.FullName, secondPath, "second", out _);
            var secondSchema = second["paths"]!["/array"]!["post"]!["requestBody"]!["content"]![
                "application/json"
            ]!["schema"]!;

            Assert.True(
                JsonNode.DeepEquals(firstSchema, secondSchema),
                $"Request schema did not reach a fixed point.\nFirst: {firstSchema}\nSecond: {secondSchema}"
            );
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
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

    private static void AssertRequestBodyComponents(JsonObject document)
    {
        var requestBodies = document["components"]!["requestBodies"]!.AsObject();
        Assert.Equal(3, requestBodies.Count);
        Assert.Equal(
            "Used component",
            requestBodies["Used/Body"]!["description"]!.GetValue<string>()
        );
        Assert.True(requestBodies["Used/Body"]!["required"]!.GetValue<bool>());
        Assert.Equal(
            "#/components/schemas/Pet",
            requestBodies["Used/Body"]!["content"]!["application/json"]!["schema"]![
                "$ref"
            ]!.GetValue<string>()
        );
        Assert.Equal(
            "string",
            requestBodies["Used/Body"]!["content"]!["text/plain"]!["schema"]![
                "type"
            ]!.GetValue<string>()
        );
        Assert.False(requestBodies["Shared.Body"]!["required"]!.GetValue<bool>());
        Assert.Equal(
            "binary",
            requestBodies["Unused~Body"]!["content"]!["application/octet-stream"]!["schema"]![
                "format"
            ]!.GetValue<string>()
        );
        Assert.True(
            requestBodies["Unused~Body"]!["content"]!["application/vnd.empty"]!["example"]![
                "accepted"
            ]!.GetValue<bool>()
        );
        Assert.Equal(
            "#/components/examples/PetExample",
            requestBodies["Used/Body"]!["content"]!["application/json"]!["examples"]!["pet"]![
                "$ref"
            ]!.GetValue<string>()
        );

        Assert.Equal(
            "#/components/requestBodies/Used~1Body",
            document["paths"]!["/pets"]!["post"]!["requestBody"]!["$ref"]!.GetValue<string>()
        );
        Assert.Equal(
            "#/components/requestBodies/Shared.Body",
            document["paths"]!["/shared/one"]!["post"]!["requestBody"]!["$ref"]!.GetValue<string>()
        );
        Assert.Equal(
            "#/components/requestBodies/Shared.Body",
            document["paths"]!["/shared/two"]!["put"]!["requestBody"]!["$ref"]!.GetValue<string>()
        );
    }
}
