using System.Text.Json.Nodes;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;

namespace Rivet.Tests;

public sealed class SecurityCorpusTests
{
    [Fact]
    public void Swagger2_Import_Generated_CSharp_Roslyn_Emission_Projects_Security_Exactly()
    {
        var spec = """
            {
              "swagger": "2.0",
              "info": { "title": "Security", "version": "1.0.0" },
              "securityDefinitions": {
                "api_key": {
                  "type": "apiKey",
                  "description": "Header key",
                  "name": "X-API-Key",
                  "in": "header"
                },
                "petstore_auth": {
                  "type": "oauth2",
                  "description": "Petstore OAuth",
                  "flow": "implicit",
                  "authorizationUrl": "https://example.test/oauth/authorize",
                  "scopes": {
                    "read:pets": "read pets",
                    "write:pets": "write pets"
                  }
                }
              },
              "security": [
                { "petstore_auth": ["read:pets"], "api_key": [] },
                { "petstore_auth": ["write:pets", "read:pets"] }
              ],
              "paths": {
                "/pets": {
                  "get": {
                    "operationId": "listPets",
                    "security": [
                      { "api_key": [] },
                      { "petstore_auth": ["read:pets"] }
                    ],
                    "responses": { "204": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        using var emitted = ImportCompileWalkAndEmit(spec);
        var root = emitted.RootElement;
        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        var apiKey = schemes.GetProperty("api_key");
        Assert.Equal("apiKey", apiKey.GetProperty("type").GetString());
        Assert.Equal("Header key", apiKey.GetProperty("description").GetString());
        Assert.Equal("X-API-Key", apiKey.GetProperty("name").GetString());
        Assert.Equal("header", apiKey.GetProperty("in").GetString());

        var oauth = schemes.GetProperty("petstore_auth");
        Assert.Equal("oauth2", oauth.GetProperty("type").GetString());
        Assert.Equal("Petstore OAuth", oauth.GetProperty("description").GetString());
        var implicitFlow = oauth.GetProperty("flows").GetProperty("implicit");
        Assert.Equal(
            "https://example.test/oauth/authorize",
            implicitFlow.GetProperty("authorizationUrl").GetString()
        );
        Assert.Equal(
            "read pets",
            implicitFlow.GetProperty("scopes").GetProperty("read:pets").GetString()
        );
        Assert.Equal(
            "write pets",
            implicitFlow.GetProperty("scopes").GetProperty("write:pets").GetString()
        );

        using var source = System.Text.Json.JsonDocument.Parse(spec);
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(source.RootElement.GetProperty("security").GetRawText()),
                JsonNode.Parse(root.GetProperty("security").GetRawText())
            ),
            "Root OR/AND requirements or scopes changed."
        );
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(
                    source
                        .RootElement.GetProperty("paths")
                        .GetProperty("/pets")
                        .GetProperty("get")
                        .GetProperty("security")
                        .GetRawText()
                ),
                JsonNode.Parse(
                    root.GetProperty("paths")
                        .GetProperty("/pets")
                        .GetProperty("get")
                        .GetProperty("security")
                        .GetRawText()
                )
            ),
            "Operation OR requirements or scopes changed."
        );
    }

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
            var reemitted = JsonNode
                .Parse(File.ReadAllText(Path.Combine(outputDirectory, "openapi.json")))!
                .AsObject();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Emission_Rejects_Unresolved_Root_And_Operation_Requirement_Names(bool atRoot)
    {
        var rootSecurity = atRoot ? "\"security\": [{ \"missing\": [] }]," : "";
        var operationSecurity = atRoot ? "" : "\"security\": [{ \"missing\": [] }],";
        var spec = $$"""
            {
              "openapi": "3.1.0",
              "info": { "title": "Security", "version": "1.0.0" },
              {{rootSecurity}}
              "paths": {
                "/widgets": {
                  "get": {
                    {{operationSecurity}}
                    "responses": { "204": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var imported = OpenApiImporter.Import(spec, new ImportOptions("Generated"));
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var security = SecurityMetadataWalker.Walk(compilation);

        var exception = Assert.Throws<OpenApiEmissionException>(() =>
            OpenApiEmitter.EmitWithSecurityMetadata(
                endpoints,
                walker.Definitions,
                walker.Brands,
                walker.Enums,
                security
            )
        );
        Assert.Contains("RIV2002", exception.Message);
        Assert.Contains("security scheme 'missing'", exception.Message);
    }

    [Fact]
    public void Explicit_Cli_Security_Overrides_Imported_Security_Provenance()
    {
        var spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Security", "version": "1.0.0" },
              "paths": {},
              "components": {
                "securitySchemes": {
                  "auth": { "type": "http", "scheme": "bearer" }
                }
              },
              "security": [{ "auth": [] }]
            }
            """;
        var workDir = Directory.CreateTempSubdirectory("rivet-security-override-");
        try
        {
            var sourcePath = Path.Combine(workDir.FullName, "source.json");
            File.WriteAllText(sourcePath, spec);
            var generatedDirectory = Path.Combine(workDir.FullName, "generated");
            var import = CliRunner.RunCli(
                workDir.FullName,
                ["--from-openapi", sourcePath, "--output", generatedDirectory]
            );
            Assert.Equal(0, import.ExitCode);

            var outputDirectory = Path.Combine(workDir.FullName, "output");
            var emit = CliRunner.RunCli(
                workDir.FullName,
                [
                    generatedDirectory,
                    "--openapi",
                    "--output",
                    outputDirectory,
                    "--security",
                    "auth=apikey:header:X-Override",
                ]
            );
            Assert.True(emit.ExitCode == 0, emit.StdErr);

            var output = JsonNode.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "openapi.json"))
            )!;
            var auth = output["components"]!["securitySchemes"]!["auth"]!;
            Assert.Equal("apiKey", auth["type"]!.GetValue<string>());
            Assert.Equal("X-Override", auth["name"]!.GetValue<string>());
            Assert.Equal("header", auth["in"]!.GetValue<string>());
            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse("[{\"auth\":[]}]")!, output["security"])
            );
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    private static System.Text.Json.JsonDocument ImportCompileWalkAndEmit(string spec)
    {
        var imported = OpenApiImporter.Import(spec, new ImportOptions("Generated"));
        Assert.Empty(imported.Warnings);
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var security = SecurityMetadataWalker.Walk(compilation);
        var emitted = OpenApiEmitter.EmitWithSecurityMetadata(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            security
        );
        return System.Text.Json.JsonDocument.Parse(emitted);
    }
}
