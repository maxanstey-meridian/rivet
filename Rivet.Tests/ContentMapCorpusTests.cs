using System.Text.Json.Nodes;

namespace Rivet.Tests;

public sealed class ContentMapCorpusTests
{
    [Fact]
    public void Import_Compile_Emit_Preserves_Every_Media_Type_And_Its_Schema()
    {
        var spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Content", "version": "1.0.0" },
              "paths": {
                "/convert": {
                  "post": {
                    "operationId": "convert",
                    "requestBody": {
                      "required": false,
                      "content": {
                        "application/json": { "schema": { "$ref": "#/components/schemas/Input" } },
                        "text/plain": { "schema": { "type": "string" } }
                      }
                    },
                    "responses": {
                      "200": {
                        "description": "converted",
                        "content": {
                          "application/json": { "schema": { "$ref": "#/components/schemas/Output" } },
                          "text/plain": { "schema": { "type": "string" } }
                        }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Input": {
                    "type": "object",
                    "required": ["value"],
                    "properties": { "value": { "type": "string" } }
                  },
                  "Output": {
                    "type": "object",
                    "required": ["length"],
                    "properties": { "length": { "type": "integer" } }
                  }
                }
              }
            }
            """;

        var workDir = Directory.CreateTempSubdirectory("rivet-content-map-");
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

            var original = JsonNode.Parse(spec)!["paths"]!["/convert"]!["post"]!;
            var reemitted = JsonNode.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "openapi.json"))
            )!["paths"]!["/convert"]!["post"]!;
            Assert.True(
                JsonNode.DeepEquals(
                    original["requestBody"]!["content"],
                    reemitted["requestBody"]!["content"]
                ),
                "Request content map changed."
            );
            Assert.True(
                JsonNode.DeepEquals(
                    original["responses"]!["200"]!["content"],
                    reemitted["responses"]!["200"]!["content"]
                ),
                "Response content map changed."
            );
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }
}
