using System.Text.Json.Nodes;
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

        var imported = OpenApiImporter.Import(spec, new ImportOptions("Generated"));
        Assert.Empty(imported.Warnings);
        using var emitted = CompilationHelper.EmitImported(imported);
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
                      { "oauth": ["widgets:write", "widgets:read"], "apiKey": [] },
                      { "apiKey": [] }
                    ],
                    "responses": { "204": { "description": "ok" } }
                  }
                },
                "/widgets/{id}": {
                  "get": {
                    "operationId": "getWidget",
                    "parameters": [
                      { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                    ],
                    "security": [{}],
                    "responses": { "204": { "description": "ok" } }
                  },
                  "put": {
                    "operationId": "putWidget",
                    "parameters": [
                      { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                    ],
                    "security": [{ "oauth": ["widgets:write"] }, {}],
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

        using var workDir = new TempDir();
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
        // OR of AND-groups with scopes, optional auth ({}), anonymous ([]) and inheritance
        // (no security key) all survive.
        foreach (var (path, item) in original["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                Assert.True(
                    JsonNode.DeepEquals(
                        operation!["security"],
                        reemitted["paths"]![path]![method]!["security"]
                    ),
                    $"{method} {path} security requirements changed."
                );
            }
        }
    }

    // An alternative naming a scheme the document never defines can't be satisfied, so
    // dropping it changes nothing; an operation left with none is dropped rather than
    // imported as open.
    [Theory]
    [InlineData("[{ \"missing\": [] }, { \"auth\": [] }]", "[{\"auth\":[]}]")]
    [InlineData(
        "[{ \"missing\": [], \"auth\": [] }, { \"auth\": [\"read\"] }]",
        "[{\"auth\":[\"read\"]}]"
    )]
    [InlineData("[{ \"missing\": [] }]", null)]
    [InlineData("[{ \"missing\": [], \"auth\": [] }]", null)]
    public void Import_Drops_Security_Alternatives_Naming_Undefined_Schemes(
        string operationSecurity,
        string? expected
    )
    {
        var spec = $$"""
            {
              "openapi": "3.1.0",
              "info": { "title": "Security", "version": "1.0.0" },
              "paths": {
                "/widgets": {
                  "get": {
                    "security": {{operationSecurity}},
                    "responses": { "204": { "description": "ok" } }
                  }
                },
                "/health": {
                  "get": { "security": [], "responses": { "204": { "description": "ok" } } }
                }
              },
              "components": {
                "securitySchemes": { "auth": { "type": "oauth2", "flows": { "clientCredentials": { "tokenUrl": "https://example.com/token", "scopes": { "read": "Read" } } } } }
              }
            }
            """;

        var imported = OpenApiImporter.Import(spec, new ImportOptions("Generated"));

        Assert.Contains(
            imported.Warnings,
            warning =>
                warning.Contains("RIV3030")
                && warning.Contains("Security alternative dropped on GET /widgets")
                && warning.Contains("'missing'")
        );
        using var emitted = CompilationHelper.EmitImported(imported);
        var paths = JsonNode.Parse(emitted.RootElement.GetRawText())!["paths"]!;
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse("[]"), paths["/health"]!["get"]!["security"])
        );
        if (expected is null)
        {
            Assert.Contains(
                imported.Warnings,
                warning => warning.Contains("Operation dropped: GET /widgets")
            );
            Assert.Null(paths["/widgets"]);
        }
        else
        {
            Assert.True(
                JsonNode.DeepEquals(
                    JsonNode.Parse(expected),
                    paths["/widgets"]!["get"]!["security"]
                )
            );
        }
    }

    [Fact]
    public void Import_Drops_Operations_Inheriting_Unsatisfiable_Document_Security()
    {
        var spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Security", "version": "1.0.0" },
              "security": [{ "missing": [] }],
              "paths": {
                "/widgets": { "get": { "responses": { "204": { "description": "ok" } } } },
                "/gadgets": {
                  "get": { "security": [{ "auth": [] }], "responses": { "204": { "description": "ok" } } }
                }
              },
              "components": { "securitySchemes": { "auth": { "type": "http", "scheme": "bearer" } } }
            }
            """;

        var imported = OpenApiImporter.Import(spec, new ImportOptions("Generated"));

        Assert.Contains(
            imported.Warnings,
            warning => warning.Contains("RIV3030") && warning.Contains("on the document")
        );
        Assert.Contains(
            imported.Warnings,
            warning =>
                warning.Contains("RIV3030")
                && warning.Contains("Operation dropped: GET /widgets")
                && warning.Contains("inherits from the document")
        );
        using var emitted = CompilationHelper.EmitImported(imported);
        var root = JsonNode.Parse(emitted.RootElement.GetRawText())!;
        Assert.Null(root["paths"]!["/widgets"]);
        Assert.Null(root["security"]);
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("[{\"auth\":[]}]"),
                root["paths"]!["/gadgets"]!["get"]!["security"]
            )
        );
    }

    // The drop happens before anything reads the document, so a dropped operation's
    // preserved fields (externalDocs, a multipart encoding, a vendor extension) leave nothing
    // behind for emission to attach.
    [Fact]
    public void Dropped_Operation_Leaves_No_Preserved_Fields_Behind()
    {
        var spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Security", "version": "1.0.0" },
              "paths": {
                "/uploads": {
                  "summary": "Uploads",
                  "post": {
                    "security": [{ "missing": [], "other": [] }],
                    "externalDocs": { "url": "https://example.com/docs" },
                    "x-vendor": { "kept": false },
                    "requestBody": {
                      "content": {
                        "multipart/form-data": {
                          "schema": { "type": "object", "properties": { "file": { "type": "string", "format": "binary" } } },
                          "encoding": { "file": { "contentType": "application/octet-stream" } }
                        }
                      }
                    },
                    "responses": { "204": { "description": "ok" } }
                  }
                },
                "/health": { "get": { "security": [], "responses": { "204": { "description": "ok" } } } }
              }
            }
            """;

        var imported = OpenApiImporter.Import(spec, new ImportOptions("Generated"));

        Assert.Contains(
            imported.Warnings,
            warning => warning.Contains("RIV3030") && warning.Contains("'missing', 'other'")
        );
        Assert.Contains(
            imported.Warnings,
            warning =>
                warning.Contains("Operation dropped: POST /uploads")
                && warning.Contains("it declares")
        );
        using var emitted = CompilationHelper.EmitImported(imported);
        var paths = JsonNode.Parse(emitted.RootElement.GetRawText())!["paths"]!;
        Assert.Null(paths["/uploads"]);
        Assert.NotNull(paths["/health"]);
    }

    // --security names a scheme for operations that declare none, so they don't inherit the
    // document's unsatisfiable security.
    [Fact]
    public void Import_Security_Option_Keeps_Operations_Whose_Document_Security_Is_Unsatisfiable()
    {
        var spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Security", "version": "1.0.0" },
              "security": [{ "missing": [] }],
              "paths": {
                "/widgets": { "get": { "tags": ["Widgets"], "responses": { "204": { "description": "ok" } } } }
              }
            }
            """;

        var imported = OpenApiImporter.Import(spec, new ImportOptions("Generated", "auth"));

        Assert.DoesNotContain(imported.Warnings, warning => warning.Contains("Operation dropped"));
        Assert.Contains(
            ".Secure(\"auth\")",
            CompilationHelper.FindFile(imported, "WidgetsContract.cs")
        );
    }

    [Fact]
    public void Partly_Satisfiable_Document_Security_Keeps_Its_Defined_Alternatives()
    {
        var spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Security", "version": "1.0.0" },
              "security": [{ "missing": [] }, { "auth": [] }],
              "paths": {
                "/widgets": { "get": { "responses": { "204": { "description": "ok" } } } }
              },
              "components": { "securitySchemes": { "auth": { "type": "http", "scheme": "bearer" } } }
            }
            """;

        var imported = OpenApiImporter.Import(spec, new ImportOptions("Generated"));

        Assert.Contains(
            imported.Warnings,
            warning =>
                warning.Contains("Security alternative dropped on the document")
                && warning.Contains("'missing'")
        );
        using var emitted = CompilationHelper.EmitImported(imported);
        var root = JsonNode.Parse(emitted.RootElement.GetRawText())!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[{\"auth\":[]}]"), root["security"]));
        Assert.NotNull(root["paths"]!["/widgets"]);
    }

    [Fact]
    public void Swagger2_Security_Reads_Defined_Schemes_From_SecurityDefinitions()
    {
        var spec = """
            {
              "swagger": "2.0",
              "info": { "title": "Security", "version": "1.0.0" },
              "securityDefinitions": { "auth": { "type": "apiKey", "name": "X-Key", "in": "header" } },
              "paths": {
                "/widgets": {
                  "get": { "security": [{ "missing": [] }, { "auth": [] }], "responses": { "204": { "description": "ok" } } }
                },
                "/gadgets": {
                  "get": { "security": [{ "missing": [] }], "responses": { "204": { "description": "ok" } } }
                }
              }
            }
            """;

        var imported = OpenApiImporter.Import(spec, new ImportOptions("Generated"));

        Assert.Contains(
            imported.Warnings,
            warning => warning.Contains("Security alternative dropped on GET /widgets")
        );
        Assert.Contains(
            imported.Warnings,
            warning => warning.Contains("Operation dropped: GET /gadgets")
        );
        using var emitted = CompilationHelper.EmitImported(imported);
        var paths = JsonNode.Parse(emitted.RootElement.GetRawText())!["paths"]!;
        Assert.Null(paths["/gadgets"]);
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("[{\"auth\":[]}]"),
                paths["/widgets"]!["get"]!["security"]
            )
        );
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
        using var workDir = new TempDir();
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
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[{\"auth\":[]}]")!, output["security"]));
    }
}
