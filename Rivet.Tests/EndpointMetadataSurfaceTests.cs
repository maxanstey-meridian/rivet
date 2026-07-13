using System.Text.Json;

namespace Rivet.Tests;

public sealed class EndpointMetadataSurfaceTests
{
    [Fact]
    public void Parameter_metadata_survives_public_cli_disk_pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/items": {
                "get": {
                    "operationId": "listItems",
                    "parameters": [
                        {
                            "name": "q",
                            "in": "query",
                            "allowEmptyValue": true,
                            "schema": {
                                "type": "string",
                                "examples": ["alpha", "beta"]
                            }
                        },
                        {
                            "name": "trace",
                            "in": "header",
                            "schema": { "type": "string" },
                            "example": "trace-123"
                        },
                        {
                            "name": "mode",
                            "in": "query",
                            "schema": { "type": "string" },
                            "examples": {
                                "fast": { "summary": "Fast mode", "value": "fast" }
                            }
                        }
                    ],
                    "responses": { "204": { "description": "No Content" } }
                }
            }
            """
        );

        using var emitted = RunDiskPipeline(
            spec,
            "rivet-parameter-metadata-",
            generatedSource => Assert.Contains("\\\"allowEmptyValue\\\":true", generatedSource)
        );
        var parameters = emitted
            .RootElement.GetProperty("paths")
            .GetProperty("/items")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!);

        Assert.Equal(
            ["alpha", "beta"],
            parameters["q"]
                .GetProperty("schema")
                .GetProperty("examples")
                .EnumerateArray()
                .Select(value => value.GetString())
        );
        Assert.True(parameters["q"].GetProperty("allowEmptyValue").GetBoolean());
        Assert.Equal("trace-123", parameters["trace"].GetProperty("example").GetString());
        Assert.Equal(
            "fast",
            parameters["mode"]
                .GetProperty("examples")
                .GetProperty("fast")
                .GetProperty("value")
                .GetString()
        );
    }

    [Fact]
    public void Framework_scalar_names_survive_component_name_collisions()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "DateTime": {
                "type": "object",
                "properties": {
                    "from": { "type": "string", "format": "date" },
                    "to": { "type": "string", "format": "date" }
              }
            },
            "DateTimeOffset": {
              "type": "object",
              "properties": { "offset": { "type": "string" } }
            },
            "System": {
              "type": "object",
              "properties": { "value": { "type": "string" } }
            },
            "Event": {
                "type": "object",
                "properties": {
                    "occurredAt": { "type": "string", "format": "date-time" },
                    "receivedAt": {
                      "type": "string",
                      "format": "date-time",
                      "x-rivet-csharp-type": "DateTimeOffset"
                    },
                    "untypedOffset": { "x-rivet-csharp-type": "DateTimeOffset" },
                    "nullableOffset": {
                      "type": "null",
                      "x-rivet-csharp-type": "DateTimeOffset"
                    },
                    "node": { "x-rivet-csharp-type": "JsonNode" },
                    "raw": {},
                    "systemValue": { "$ref": "#/components/schemas/System" },
                    "window": { "$ref": "#/components/schemas/DateTime" },
                    "offset": { "$ref": "#/components/schemas/DateTimeOffset" }
                  },
                  "required": ["occurredAt", "receivedAt", "untypedOffset", "nullableOffset", "node", "raw", "systemValue", "window", "offset"]
            }
            """,
            paths: """
            "/events": {
                "get": {
                    "operationId": "events_get",
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/Event" }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        using var emitted = RunDiskPipeline(
            spec,
            "rivet-framework-scalar-collision-",
            generatedSource =>
            {
                Assert.Contains("global::System.DateTime OccurredAt", generatedSource);
                Assert.Contains("global::System.DateTimeOffset ReceivedAt", generatedSource);
                Assert.Contains("global::System.DateTimeOffset UntypedOffset", generatedSource);
                Assert.Contains("global::System.DateTimeOffset? NullableOffset", generatedSource);
                Assert.Contains("global::System.Text.Json.Nodes.JsonNode Node", generatedSource);
                Assert.Contains("global::System.Text.Json.JsonElement Raw", generatedSource);
                Assert.Contains("System SystemValue", generatedSource);
                Assert.Contains("DateTime Window", generatedSource);
                Assert.Contains("DateTimeOffset Offset", generatedSource);
            }
        );
        var properties = emitted
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Event")
            .GetProperty("properties");
        var occurredAt = properties.GetProperty("occurredAt");
        Assert.Equal("string", occurredAt.GetProperty("type").GetString());
        Assert.Equal("date-time", occurredAt.GetProperty("format").GetString());
        Assert.Equal(
            "DateTimeOffset",
            properties.GetProperty("receivedAt").GetProperty("x-rivet-csharp-type").GetString()
        );
        Assert.Equal(
            "#/components/schemas/DateTime",
            properties.GetProperty("window").GetProperty("$ref").GetString()
        );
        Assert.Equal(
            "#/components/schemas/DateTimeOffset",
            properties.GetProperty("offset").GetProperty("$ref").GetString()
        );
    }

    [Fact]
    public void Response_header_metadata_survives_public_cli_disk_pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/items": {
                "get": {
                    "operationId": "listItems",
                    "responses": {
                        "200": {
                            "description": "OK",
                            "headers": {
                                "X-Meta": {
                                    "description": "Metadata header",
                                    "required": true,
                                    "deprecated": true,
                                    "allowEmptyValue": true,
                                    "style": "matrix",
                                    "explode": true,
                                    "allowReserved": true,
                                    "schema": {
                                        "type": "string",
                                        "examples": ["schema-a", "schema-b"]
                                    },
                                    "example": "header-a"
                                },
                                "X-Named": {
                                    "schema": { "type": "string" },
                                    "examples": {
                                        "named": { "summary": "Named", "value": "header-b" }
                                    }
                                },
                                "X-Content": {
                                    "content": {
                                        "text/plain": {
                                            "schema": {
                                                "type": "string",
                                                "examples": ["content-a"]
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

        using var emitted = RunDiskPipeline(
            spec,
            "rivet-response-header-metadata-",
            generatedSource =>
            {
                Assert.Contains("schemaExamplesJson:", generatedSource);
                Assert.Contains("exampleJson:", generatedSource);
                Assert.Contains("examplesJson:", generatedSource);
                Assert.Contains("deprecated: true", generatedSource);
                Assert.Contains("allowReserved: true", generatedSource);
                Assert.Contains("allowEmptyValue: true", generatedSource);
                Assert.Contains("contentType: \"text/plain\"", generatedSource);
            }
        );
        var headers = emitted
            .RootElement.GetProperty("paths")
            .GetProperty("/items")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("headers");

        var metadata = headers.GetProperty("X-Meta");
        Assert.Equal("Metadata header", metadata.GetProperty("description").GetString());
        Assert.True(metadata.GetProperty("required").GetBoolean());
        Assert.True(metadata.GetProperty("deprecated").GetBoolean());
        Assert.True(metadata.GetProperty("allowEmptyValue").GetBoolean());
        Assert.Equal("matrix", metadata.GetProperty("style").GetString());
        Assert.True(metadata.GetProperty("explode").GetBoolean());
        Assert.True(metadata.GetProperty("allowReserved").GetBoolean());
        Assert.Equal("header-a", metadata.GetProperty("example").GetString());
        Assert.Equal(2, metadata.GetProperty("schema").GetProperty("examples").GetArrayLength());

        Assert.Equal(
            "header-b",
            headers
                .GetProperty("X-Named")
                .GetProperty("examples")
                .GetProperty("named")
                .GetProperty("value")
                .GetString()
        );
        Assert.Equal(
            "content-a",
            headers
                .GetProperty("X-Content")
                .GetProperty("content")
                .GetProperty("text/plain")
                .GetProperty("schema")
                .GetProperty("examples")[0]
                .GetString()
        );
    }

    private static JsonDocument RunDiskPipeline(
        string spec,
        string temporaryDirectoryPrefix,
        Action<string>? inspectGeneratedSource = null
    )
    {
        var workDirectory = Directory.CreateTempSubdirectory(temporaryDirectoryPrefix);
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

            inspectGeneratedSource?.Invoke(
                string.Join(
                    "\n",
                    Directory
                        .GetFiles(generatedDirectory, "*.cs", SearchOption.AllDirectories)
                        .Select(File.ReadAllText)
                )
            );

            var outputDirectory = Path.Combine(workDirectory.FullName, "output");
            var emit = CliRunner.RunCli(
                workDirectory.FullName,
                [generatedDirectory, "--openapi", "--output", outputDirectory]
            );
            Assert.True(emit.ExitCode == 0, emit.StdErr);
            return JsonDocument.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "openapi.json"))
            );
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }
}
