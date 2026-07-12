using System.Text.Json.Nodes;

namespace Rivet.Tests;

public sealed class SecurityCorpusTests
{
    [Fact]
    public void Import_Compile_Emit_Preserves_Full_Security_Without_External_Configuration()
    {
        var spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Security", "version": "1.0.0" },
              "security": [
                { "oauth": ["widgets:read"] },
                { "apiKey": [] }
              ],
              "paths": {
                "/widgets": {
                  "get": {
                    "operationId": "listWidgets",
                    "responses": { "204": { "description": "ok" } }
                  },
                  "post": {
                    "operationId": "createWidget",
                    "security": [
                      { "oauth": ["widgets:write"], "apiKey": [] }
                    ],
                    "responses": { "204": { "description": "ok" } }
                  }
                },
                "/health": {
                  "get": {
                    "operationId": "health",
                    "security": [],
                    "responses": { "204": { "description": "ok" } }
                  }
                }
              },
              "components": {
                "securitySchemes": {
                  "oauth": {
                    "type": "oauth2",
                    "description": "Full OAuth flow",
                    "flows": {
                      "authorizationCode": {
                        "authorizationUrl": "https://example.test/authorize",
                        "tokenUrl": "https://example.test/token",
                        "refreshUrl": "https://example.test/refresh",
                        "scopes": {
                          "widgets:read": "Read widgets",
                          "widgets:write": "Write widgets"
                        }
                      }
                    }
                  },
                  "apiKey": {
                    "type": "apiKey",
                    "in": "header",
                    "name": "X-API-Key"
                  }
                }
              }
            }
            """;

        var workDir = Directory.CreateTempSubdirectory("rivet-security-corpus-");
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

            var original = JsonNode.Parse(spec)!.AsObject();
            var reemitted = JsonNode.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "openapi.json"))
            )!.AsObject();
            Assert.True(
                JsonNode.DeepEquals(original["security"], reemitted["security"]),
                "Global security requirements changed."
            );
            Assert.True(
                JsonNode.DeepEquals(
                    original["components"]!["securitySchemes"],
                    reemitted["components"]!["securitySchemes"]
                ),
                "Security scheme definitions changed."
            );
            Assert.True(
                JsonNode.DeepEquals(
                    original["paths"]!["/widgets"]!["post"]!["security"],
                    reemitted["paths"]!["/widgets"]!["post"]!["security"]
                ),
                "Operation security requirements changed."
            );
            Assert.True(
                JsonNode.DeepEquals(
                    original["paths"]!["/health"]!["get"]!["security"],
                    reemitted["paths"]!["/health"]!["get"]!["security"]
                ),
                "Anonymous operation override changed."
            );
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }
}
