using System.Text.Json.Nodes;

namespace Rivet.Tests;

public sealed class DocumentOperationProvenanceTests
{
    [Fact]
    public void OpenApi3_Document_And_Operation_Provenance_Survives_The_Public_Pipeline()
    {
        var spec = """
            {
              "openapi": "3.1.0",
              "info": {
                "title": "Provenance API",
                "version": "2026.7",
                "description": "Document description",
                "termsOfService": "https://example.test/terms",
                "contact": {
                  "name": "API Team",
                  "url": "https://example.test/contact",
                  "email": "api@example.test"
                },
                "license": {
                  "name": "Apache-2.0",
                  "identifier": "Apache-2.0"
                }
              },
              "tags": [
                {
                  "name": "pets",
                  "description": "Pet operations",
                  "externalDocs": {
                    "url": "https://example.test/pets",
                    "description": "Pet guide"
                  }
                },
                { "name": "PET" }
              ],
              "externalDocs": {
                "url": "https://example.test/docs",
                "description": "Main guide"
              },
              "servers": [
                {
                  "url": "https://{region}.example.test/{version}",
                  "description": "Production",
                  "variables": {
                    "region": {
                      "default": "eu",
                      "enum": ["eu", "us"],
                      "description": "Deployment region"
                    },
                    "version": { "default": "v2" }
                  }
                },
                { "url": "/relative" }
              ],
              "paths": {
                "/pets": {
                  "post": {
                    "operationId": "create.PET_exact",
                    "tags": ["pets", "PET"],
                    "deprecated": true,
                    "requestBody": {
                      "description": "Exact body description",
                      "required": true,
                      "content": {
                        "application/json": { "schema": { "type": "string" } }
                      }
                    },
                    "responses": { "204": { "description": "Created" } }
                  }
                },
                "/path-scope": {
                  "servers": [
                    {
                      "url": "https://{tenant}.example.test/path",
                      "description": "Tenant path",
                      "variables": {
                        "tenant": {
                          "default": "main",
                          "enum": ["main", "backup"],
                          "description": "Tenant"
                        }
                      }
                    }
                  ],
                  "get": {
                    "responses": { "204": { "description": "No content" } }
                  }
                },
                "/operation-scope": {
                  "servers": [{ "url": "https://path.example.test" }],
                  "get": {
                    "servers": [],
                    "tags": [],
                    "responses": { "204": { "description": "No content" } }
                  }
                },
                "/referenced-body": {
                  "post": {
                    "requestBody": { "$ref": "#/components/requestBodies/ReferencedBody" },
                    "responses": { "204": { "description": "No content" } }
                  }
                }
              },
              "components": {
                "requestBodies": {
                  "ReferencedBody": {
                    "description": "Referenced body description",
                    "required": true,
                    "content": {
                      "application/json": { "schema": { "type": "string" } }
                    }
                  }
                }
              }
            }
            """;

        WithImportedSource(
            spec,
            (workingDirectory, generatedDirectory) =>
            {
                var emitted = Emit(workingDirectory, generatedDirectory);
                var source = JsonNode.Parse(spec)!.AsObject();

                Assert.True(JsonNode.DeepEquals(source["info"], emitted["info"]));
                Assert.True(JsonNode.DeepEquals(source["tags"], emitted["tags"]));
                Assert.True(JsonNode.DeepEquals(source["externalDocs"], emitted["externalDocs"]));
                Assert.True(JsonNode.DeepEquals(source["servers"], emitted["servers"]));

                var pets = emitted["paths"]!["/pets"]!["post"]!.AsObject();
                Assert.Equal("create.PET_exact", pets["operationId"]!.GetValue<string>());
                Assert.True(
                    JsonNode.DeepEquals(source["paths"]!["/pets"]!["post"]!["tags"], pets["tags"])
                );
                Assert.True(pets["deprecated"]!.GetValue<bool>());
                Assert.Equal(
                    "Exact body description",
                    pets["requestBody"]!["description"]!.GetValue<string>()
                );
                Assert.False(pets.ContainsKey("servers"));

                var pathScope = emitted["paths"]!["/path-scope"]!["get"]!.AsObject();
                Assert.False(pathScope.ContainsKey("operationId"));
                Assert.False(pathScope.ContainsKey("tags"));
                Assert.True(
                    JsonNode.DeepEquals(
                        source["paths"]!["/path-scope"]!["servers"],
                        pathScope["servers"]
                    )
                );

                var operationScope = emitted["paths"]!["/operation-scope"]!["get"]!.AsObject();
                Assert.False(operationScope.ContainsKey("operationId"));
                Assert.Empty(operationScope["servers"]!.AsArray());

                Assert.Equal(
                    "Referenced body description",
                    emitted["paths"]!["/referenced-body"]!["post"]!["requestBody"]![
                        "description"
                    ]!.GetValue<string>()
                );

                var overridden = Emit(
                    workingDirectory,
                    generatedDirectory,
                    "--title",
                    "CLI title",
                    "--version",
                    "9.9.9",
                    "--server",
                    "https://cli.example.test"
                );
                Assert.Equal("CLI title", overridden["info"]!["title"]!.GetValue<string>());
                Assert.Equal("9.9.9", overridden["info"]!["version"]!.GetValue<string>());
                Assert.Equal(
                    "Document description",
                    overridden["info"]!["description"]!.GetValue<string>()
                );
                Assert.Equal(
                    "https://cli.example.test",
                    overridden["servers"]![0]!["url"]!.GetValue<string>()
                );
                Assert.Single(overridden["servers"]!.AsArray());
            }
        );
    }

    [Theory]
    [InlineData(
        "\"host\": \"pet.example.test\", \"basePath\": \"/v2\", \"schemes\": [\"https\", \"http\"]",
        "https://pet.example.test/v2",
        "http://pet.example.test/v2"
    )]
    [InlineData("\"basePath\": \"/v2\"", "/v2", null)]
    public void Swagger2_Address_And_Operation_Metadata_Are_Projected_Without_Invention(
        string address,
        string firstServer,
        string? secondServer
    )
    {
        var spec = $$"""
            {
              "swagger": "2.0",
              "info": { "title": "Swagger source", "version": "2.0", "contact": {} },
              {{address}},
              "paths": {
                "/pets": {
                  "post": {
                    "operationId": "addPet",
                    "tags": ["PetStore"],
                    "deprecated": true,
                    "parameters": [
                      {
                        "name": "body",
                        "in": "body",
                        "description": "Swagger body description",
                        "required": true,
                        "schema": { "type": "string" }
                      }
                    ],
                    "responses": { "204": { "description": "Done" } }
                  }
                }
              }
            }
            """;

        WithImportedSource(
            spec,
            (workingDirectory, generatedDirectory) =>
            {
                var emitted = Emit(workingDirectory, generatedDirectory);
                Assert.Empty(emitted["info"]!["contact"]!.AsObject());
                var servers = emitted["servers"]!.AsArray();
                Assert.Equal(firstServer, servers[0]!["url"]!.GetValue<string>());
                if (secondServer is null)
                {
                    Assert.Single(servers);
                }
                else
                {
                    Assert.Equal(secondServer, servers[1]!["url"]!.GetValue<string>());
                    Assert.Equal(2, servers.Count);
                }

                var operation = emitted["paths"]!["/pets"]!["post"]!.AsObject();
                Assert.Equal("addPet", operation["operationId"]!.GetValue<string>());
                Assert.Equal("PetStore", operation["tags"]![0]!.GetValue<string>());
                Assert.True(operation["deprecated"]!.GetValue<bool>());
                Assert.Equal(
                    "Swagger body description",
                    operation["requestBody"]!["description"]!.GetValue<string>()
                );
            }
        );
    }

    private static void WithImportedSource(string spec, Action<string, string> assertion)
    {
        var workDirectory = Directory.CreateTempSubdirectory("rivet-provenance-");
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
            assertion(workDirectory.FullName, generatedDirectory);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    private static JsonObject Emit(
        string workingDirectory,
        string generatedDirectory,
        params string[] additionalArguments
    )
    {
        var outputDirectory = Path.Combine(workingDirectory, $"output-{Guid.NewGuid():N}");
        var arguments = new List<string>
        {
            generatedDirectory,
            "--openapi",
            "--output",
            outputDirectory,
        };
        arguments.AddRange(additionalArguments);
        var emit = CliRunner.RunCli(workingDirectory, arguments);
        Assert.True(emit.ExitCode == 0, emit.StdErr);
        return JsonNode
            .Parse(File.ReadAllText(Path.Combine(outputDirectory, "openapi.json")))!
            .AsObject();
    }
}
