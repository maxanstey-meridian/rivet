using System.Text.Json.Nodes;

namespace Rivet.Tests;

public sealed class OperationParameterCorpusTests
{
    [Fact]
    public void Import_Compile_Emit_Preserves_Parameters_Alongside_Optional_Body()
    {
        var spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Parameters", "version": "1.0.0" },
              "paths": {
                "/things/{thing_id}": {
                  "post": {
                    "operationId": "updateThing",
                    "parameters": [
                      {
                        "name": "thing_id",
                        "in": "path",
                        "required": true,
                        "schema": { "type": "integer", "format": "snowflake" }
                      },
                      {
                        "name": "with_details",
                        "in": "query",
                        "required": false,
                        "schema": { "type": "boolean" }
                      },
                      {
                        "name": "integer_without_format",
                        "in": "query",
                        "required": true,
                        "schema": { "type": "integer" }
                      },
                      {
                        "name": "number_without_format",
                        "in": "query",
                        "required": true,
                        "schema": { "type": "number" }
                      },
                      {
                        "name": "page_size",
                        "in": "query",
                        "required": false,
                        "description": "Maximum results per page",
                        "deprecated": true,
                        "style": "spaceDelimited",
                        "explode": true,
                        "examples": {
                          "small": { "summary": "Small page", "value": 10 }
                        },
                        "schema": {
                          "type": "integer",
                          "default": 25,
                          "minimum": 1,
                          "maximum": 100,
                          "multipleOf": 1,
                          "examples": [25, 50]
                        }
                      },
                      {
                        "name": "formatted_string",
                        "in": "query",
                        "required": true,
                        "schema": { "type": "string", "format": "hostname" }
                      },
                      {
                        "name": "from",
                        "in": "query",
                        "required": false,
                        "schema": { "type": "string", "format": "date-time" }
                      },
                      {
                        "name": "to",
                        "in": "query",
                        "required": false,
                        "schema": { "type": "string", "format": "date-time" }
                      },
                      {
                        "name": "nullable_integer",
                        "in": "query",
                        "required": false,
                        "schema": { "type": ["integer", "null"] }
                      },
                      {
                        "name": "X-Trace",
                        "in": "header",
                        "required": true,
                        "schema": { "type": "string" }
                      }
                    ],
                    "requestBody": {
                      "required": false,
                      "content": {
                        "application/json": { "schema": { "$ref": "#/components/schemas/Input" } }
                      }
                    },
                    "responses": { "200": { "description": "OK" } }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Input": {
                    "type": "object",
                    "required": ["name"],
                    "properties": { "name": { "type": "string" } }
                  }
                }
              }
            }
            """;

        var workDir = Directory.CreateTempSubdirectory("rivet-operation-parameters-");
        try
        {
            var sourcePath = Path.Combine(workDir.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);
            var generatedDirectory = Path.Combine(workDir.FullName, "generated");
            var import = CliRunner.RunCli(
                workDir.FullName,
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

            var outputDirectory = Path.Combine(workDir.FullName, "output");
            var emit = CliRunner.RunCli(
                workDir.FullName,
                [generatedDirectory, "--openapi", "--output", outputDirectory]
            );
            Assert.True(emit.ExitCode == 0, emit.StdErr);
            Assert.Equal("", emit.StdErr);

            var operation = JsonNode.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "openapi.json"))
            )!["paths"]!["/things/{thing_id}"]!["post"]!;
            Assert.True(
                JsonNode.DeepEquals(
                    JsonNode.Parse(spec)!["paths"]!["/things/{thing_id}"]!["post"]!["parameters"],
                    operation["parameters"]
                ),
                $"Parameters changed.\nExpected: {JsonNode.Parse(spec)!["paths"]!["/things/{thing_id}"]!["post"]!["parameters"]}\nActual: {operation["parameters"]}"
            );
            Assert.True(
                JsonNode.DeepEquals(
                    JsonNode.Parse(spec)!["paths"]!["/things/{thing_id}"]!["post"]!["requestBody"],
                    operation["requestBody"]
                ),
                "Request body changed."
            );
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }
}
