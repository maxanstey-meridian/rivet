using System.Text.Json;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;

namespace Rivet.Tests;

public sealed class SchemaFidelitySurfaceTests
{
    [Fact]
    public void Okta_nested_password_objects_survive_public_pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "PasswordChange": {
                "type": "object",
                "properties": {
                    "newPassword": {
                        "type": "object",
                        "properties": { "value": { "type": "string" } }
                    },
                    "oldPassword": {
                        "type": "object",
                        "properties": { "value": { "type": "string" } }
                    }
                }
            }
            """,
            paths: ResponsePath("PasswordChange")
        );

        using var emitted = CompileWalkContractJsonAndEmit(
            CompilationHelper.Import(spec, "OktaSchemaTrace")
        );

        foreach (var name in new[] { "newPassword", "oldPassword" })
        {
            var password = TraceSchema(emitted.RootElement, "PasswordChange", "properties", name);
            Assert.Equal("object", password.GetProperty("type").GetString());
            Assert.True(password.GetProperty("properties").TryGetProperty("value", out _));
        }
    }

    [Fact]
    public void Twilio_nested_format_and_nullable_values_survive_public_pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Token": {
                "type": "object",
                "properties": {
                    "ice_servers": {
                        "type": "array",
                        "nullable": true,
                        "items": {
                            "type": "object",
                            "format": "ice-server",
                            "properties": { "url": { "type": "string" } }
                        }
                    },
                    "request": {
                        "nullable": true,
                        "description": "Arbitrary request value"
                    },
                    "configuration": {
                        "type": "object",
                        "nullable": true,
                        "properties": { "enabled": { "type": "boolean" } }
                    }
                }
            }
            """,
            paths: ResponsePath("Token")
        );

        using var emitted = CompileWalkContractJsonAndEmit(
            CompilationHelper.Import(spec, "TwilioSchemaTrace")
        );
        var iceServer = TraceSchema(
            emitted.RootElement,
            "Token",
            "properties",
            "ice_servers",
            "items"
        );
        Assert.Equal("ice-server", iceServer.GetProperty("format").GetString());
        Assert.True(AdmitsNull(TraceSchema(emitted.RootElement, "Token", "properties", "request")));
        Assert.True(
            AdmitsNull(TraceSchema(emitted.RootElement, "Token", "properties", "configuration"))
        );
    }

    [Fact]
    public void Square_integer_format_and_unresolved_required_names_survive_public_pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "TipSettings": {
                "type": "object",
                "properties": {
                    "tip_percentages": {
                        "type": "array",
                        "items": { "type": "integer" }
                    }
                }
            },
            "RiskEvaluation": {
                "type": "object",
                "properties": {
                    "created_at": { "type": "string" },
                    "risk_level": { "type": "string" }
                },
                "required": ["status"]
            },
            "SearchSubscriptionsRequest": {
                "type": "object",
                "properties": { "cursor": { "type": "string" } },
                "required": ["location_ids"]
            },
            "SquareSurface": {
                "type": "object",
                "properties": {
                    "tips": { "$ref": "#/components/schemas/TipSettings" },
                    "risk": { "$ref": "#/components/schemas/RiskEvaluation" },
                    "search": { "$ref": "#/components/schemas/SearchSubscriptionsRequest" }
                }
            }
            """,
            paths: ResponsePath("SquareSurface")
        );

        using var emitted = CompileWalkContractJsonAndEmit(
            CompilationHelper.Import(spec, "SquareSchemaTrace")
        );
        var item = TraceSchema(
            emitted.RootElement,
            "SquareSurface",
            "properties",
            "tips",
            "properties",
            "tip_percentages",
            "items"
        );
        Assert.Equal("integer", item.GetProperty("type").GetString());
        Assert.False(item.TryGetProperty("format", out _));
        Assert.Equal(
            ["status"],
            TraceSchema(emitted.RootElement, "RiskEvaluation")
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString())
        );
        Assert.Equal(
            ["location_ids"],
            TraceSchema(emitted.RootElement, "SearchSubscriptionsRequest")
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString())
        );
    }

    [Fact]
    public void Public_pipeline_schema_tracer_stops_on_reference_cycles()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Node": {
                "type": "object",
                "properties": { "next": { "$ref": "#/components/schemas/Node" } }
            }
            """,
            paths: ResponsePath("Node")
        );
        using var emitted = CompileWalkContractJsonAndEmit(
            CompilationHelper.Import(spec, "CycleSchemaTrace")
        );

        var exception = Assert.Throws<InvalidOperationException>(() =>
            TraceSchema(emitted.RootElement, "Node", "properties", "next", "properties", "next")
        );
        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nested_schema_metadata_and_dictionary_refs_survive_generated_disk_surface()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Leaf": {
                "type": "object",
                "properties": { "value": { "type": "string" } },
                "required": ["value"]
            },
            "Node": {
                "type": "object",
                "properties": {
                    "children": {
                        "type": "array",
                        "items": { "$ref": "#/components/schemas/Node" }
                    }
                },
                "required": ["children"]
            },
            "Surface": {
                "type": "object",
                "title": "Schema surface",
                "examples": [{ "rows": [["ab"]] }, { "rows": [["cd"]] }],
                "xml": {
                    "name": "surface",
                    "namespace": "https://example.test/schema",
                    "prefix": "s",
                    "attribute": true,
                    "wrapped": true
                },
                "properties": {
                    "rows": {
                        "type": "array",
                        "items": {
                            "type": "array",
                            "title": "Row",
                            "minItems": 1,
                            "maxItems": 4,
                            "items": {
                                "type": "string",
                                "default": "ab",
                                "examples": ["ab", "cd"],
                                "minLength": 2,
                                "maxLength": 8,
                                "xml": { "name": "cell", "attribute": true }
                            }
                        }
                    },
                    "labels": {
                        "type": "object",
                        "additionalProperties": { "$ref": "#/components/schemas/Leaf" },
                        "xml": { "name": "labels", "wrapped": true }
                    },
                    "scores": {
                        "type": "object",
                        "additionalProperties": {
                            "type": "string",
                            "default": "ok",
                            "examples": ["ok", "held"],
                            "maxLength": 20,
                            "xml": { "name": "score" }
                        }
                    }
                },
                "required": ["rows", "labels", "scores"]
            }
            """
        );

        var imported = CompilationHelper.Import(spec, "SchemaSurface");
        Assert.Empty(imported.Warnings);
        var source = CompilationHelper.FindFile(imported, "Surface.cs");
        Assert.Contains("RivetGeneratedSchemaMetadata", source);
        Assert.Contains("\"/items/items\"", source);
        Assert.Contains("\"/additionalProperties\"", source);

        using var emitted = CompileWalkContractJsonAndEmit(imported);
        var schemas = emitted.RootElement.GetProperty("components").GetProperty("schemas");
        var surface = schemas.GetProperty("Surface");
        Assert.Equal("Schema surface", surface.GetProperty("title").GetString());
        Assert.Equal(2, surface.GetProperty("examples").GetArrayLength());
        var surfaceXml = surface.GetProperty("xml");
        Assert.Equal("surface", surfaceXml.GetProperty("name").GetString());
        Assert.Equal(
            "https://example.test/schema",
            surfaceXml.GetProperty("namespace").GetString()
        );
        Assert.Equal("s", surfaceXml.GetProperty("prefix").GetString());
        Assert.True(surfaceXml.GetProperty("attribute").GetBoolean());
        Assert.True(surfaceXml.GetProperty("wrapped").GetBoolean());

        var properties = surface.GetProperty("properties");
        var row = properties.GetProperty("rows").GetProperty("items");
        Assert.Equal("Row", row.GetProperty("title").GetString());
        Assert.Equal(1, row.GetProperty("minItems").GetInt32());
        Assert.Equal(4, row.GetProperty("maxItems").GetInt32());
        var cell = row.GetProperty("items");
        Assert.Equal("ab", cell.GetProperty("default").GetString());
        Assert.Equal(
            ["ab", "cd"],
            cell.GetProperty("examples").EnumerateArray().Select(x => x.GetString())
        );
        Assert.Equal(2, cell.GetProperty("minLength").GetInt32());
        Assert.Equal(8, cell.GetProperty("maxLength").GetInt32());
        Assert.True(cell.GetProperty("xml").GetProperty("attribute").GetBoolean());

        Assert.Equal(
            "#/components/schemas/Leaf",
            properties
                .GetProperty("labels")
                .GetProperty("additionalProperties")
                .GetProperty("$ref")
                .GetString()
        );
        var score = properties.GetProperty("scores").GetProperty("additionalProperties");
        Assert.Equal("ok", score.GetProperty("default").GetString());
        Assert.Equal(2, score.GetProperty("examples").GetArrayLength());
        Assert.Equal(20, score.GetProperty("maxLength").GetInt32());
        Assert.Equal("score", score.GetProperty("xml").GetProperty("name").GetString());
        Assert.Equal(
            "#/components/schemas/Node",
            schemas
                .GetProperty("Node")
                .GetProperty("properties")
                .GetProperty("children")
                .GetProperty("items")
                .GetProperty("$ref")
                .GetString()
        );
    }

    [Fact]
    public void Non_object_additional_properties_is_a_silent_no_op()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "connectOAuthConfig": {
                "type": "object",
                "properties": {
                    "customParameters": {
                        "type": "string",
                        "additionalProperties": { "type": "string" }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec, "InvalidAdditionalProperties");

        Assert.Empty(imported.Warnings);
    }

    [Fact]
    public void Schema_metadata_survives_public_cli_disk_pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "DiskSurface": {
                "type": "object",
                "title": "Disk surface",
                "examples": [{ "values": ["aa"] }],
                "properties": {
                    "values": {
                        "type": "array",
                        "items": {
                            "type": "string",
                            "default": "aa",
                            "examples": ["aa", "bb"],
                            "minLength": 2,
                            "xml": { "name": "value" }
                        }
                    }
                },
                "required": ["values"]
            }
            """
        );
        var workDirectory = Directory.CreateTempSubdirectory("rivet-schema-surface-");
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
            Assert.DoesNotContain("warning RIV", import.StdErr);

            var compile = CliRunner.RunCli(
                workDirectory.FullName,
                [generatedDirectory, "--routes"]
            );
            Assert.True(compile.ExitCode == 0, compile.StdErr);

            var outputDirectory = Path.Combine(workDirectory.FullName, "output");
            var emit = CliRunner.RunCli(
                workDirectory.FullName,
                [generatedDirectory, "--openapi", "--output", outputDirectory]
            );
            Assert.True(emit.ExitCode == 0, emit.StdErr);
            Assert.DoesNotContain("warning RIV", emit.StdErr);

            using var emitted = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(outputDirectory, "openapi.json"))
            );
            var schema = emitted
                .RootElement.GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("DiskSurface");
            Assert.Equal("Disk surface", schema.GetProperty("title").GetString());
            Assert.Single(schema.GetProperty("examples").EnumerateArray());
            var item = schema.GetProperty("properties").GetProperty("values").GetProperty("items");
            Assert.Equal("aa", item.GetProperty("default").GetString());
            Assert.Equal(2, item.GetProperty("examples").GetArrayLength());
            Assert.Equal(2, item.GetProperty("minLength").GetInt32());
            Assert.Equal("value", item.GetProperty("xml").GetProperty("name").GetString());
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    private static JsonDocument CompileWalkContractJsonAndEmit(ImportResult imported)
    {
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            endpoints
        );
        var read = JsonContractReader.Read(contractJson);
        return JsonDocument.Parse(
            OpenApiEmitter.Emit(
                read.Endpoints,
                read.Types.ToDictionary(type => type.Name),
                read.Brands,
                read.Enums,
                security: null
            )
        );
    }

    private static string ResponsePath(string schema) =>
        $$"""
            "/surface": {
                "get": {
                    "operationId": "getSurface",
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/{{schema}}" }
                                }
                            }
                        }
                    }
                }
            }
            """;

    private static JsonElement TraceSchema(
        JsonElement document,
        string component,
        params string[] path
    )
    {
        var current = document
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(component);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var segment in path)
        {
            current = Dereference(document, current, visited);
            current = current.GetProperty(segment);
        }

        return Dereference(document, current, visited);
    }

    private static JsonElement Dereference(
        JsonElement document,
        JsonElement schema,
        HashSet<string> visited
    )
    {
        for (var depth = 0; schema.TryGetProperty("$ref", out var reference); depth++)
        {
            if (depth >= 64)
            {
                throw new InvalidOperationException("Schema trace exceeded 64 references.");
            }

            var pointer = reference.GetString()!;
            if (!visited.Add(pointer))
            {
                throw new InvalidOperationException($"Schema reference cycle at '{pointer}'.");
            }
            if (!pointer.StartsWith("#/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Schema trace cannot resolve '{pointer}'.");
            }

            schema = document;
            foreach (var token in pointer[2..].Split('/'))
            {
                schema = schema.GetProperty(
                    token
                        .Replace("~1", "/", StringComparison.Ordinal)
                        .Replace("~0", "~", StringComparison.Ordinal)
                );
            }
        }

        return schema;
    }

    private static bool AdmitsNull(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            return type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(value => value.GetString() == "null")
                : type.GetString() == "null";
        }

        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (
                schema.TryGetProperty(keyword, out var branches)
                && branches.EnumerateArray().Any(AdmitsNull)
            )
            {
                return true;
            }
        }

        return false;
    }
}
