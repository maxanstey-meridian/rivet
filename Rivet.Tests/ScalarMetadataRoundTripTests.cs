using System.Text.Json.Nodes;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class ScalarMetadataRoundTripTests
{
    [Fact]
    public void DateTime_Parameter_Leaf_Provenance_Survives_Contract_Json()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/events": {
                "get": {
                    "operationId": "events_list",
                    "parameters": [
                        {
                            "name": "from",
                            "in": "query",
                            "required": false,
                            "schema": { "type": "string", "format": "date-time" }
                        }
                    ],
                    "responses": { "204": { "description": "No Content" } }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var walked = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            walked
        );
        var contract = JsonNode.Parse(contractJson)!;
        var contractParameter = contract["endpoints"]![0]!["params"]![0]!;
        Assert.Equal("string", contractParameter["schemaType"]!.GetValue<string>());
        Assert.Equal("date-time", contractParameter["format"]!.GetValue<string>());
        Assert.True(contractParameter["isFormatSpecified"]!.GetValue<bool>());

        var (types, enums, endpoints, brands) = JsonContractReader.Read(contractJson);
        var emitted = JsonNode.Parse(
            OpenApiEmitter.Emit(
                endpoints,
                types.ToDictionary(type => type.Name),
                brands,
                enums,
                security: null
            )
        )!;
        var schema = emitted["paths"]!["/events"]!["get"]!["parameters"]![0]!["schema"]!;
        Assert.Equal("string", schema["type"]!.GetValue<string>());
        Assert.Equal("date-time", schema["format"]!.GetValue<string>());
    }

    [Fact]
    public void Parameter_Metadata_Public_Builder_Surface_Emits_Leaf_Metadata()
    {
        const string source = """
            using Rivet;

            [RivetContract]
            public static class ScalarContract
            {
                public static readonly RouteDefinition Get = Define.Get("/scalar")
                    .Parameter<long?>(
                        "page_size",
                        "query",
                        false,
                        "integer",
                        "",
                        metadataJson: "{\"description\":\"Maximum results per page\",\"deprecated\":true,\"default\":25,\"constraints\":{\"Minimum\":1,\"Maximum\":100},\"schemaExamples\":[25,50],\"style\":\"spaceDelimited\",\"explode\":true}"
                    );
            }
            """;

        using var document = CompilationHelper.EmitOpenApi(source);
        var parameter = document
            .RootElement.GetProperty("paths")
            .GetProperty("/scalar")
            .GetProperty("get")
            .GetProperty("parameters")[0];
        Assert.Equal("Maximum results per page", parameter.GetProperty("description").GetString());
        Assert.True(parameter.GetProperty("deprecated").GetBoolean());
        Assert.Equal("spaceDelimited", parameter.GetProperty("style").GetString());
        Assert.True(parameter.GetProperty("explode").GetBoolean());

        var schema = parameter.GetProperty("schema");
        Assert.Equal(
            ["integer", "null"],
            schema.GetProperty("type").EnumerateArray().Select(x => x.GetString())
        );
        Assert.False(schema.TryGetProperty("format", out _));
        Assert.Equal(25, schema.GetProperty("default").GetInt32());
        Assert.Equal(1, schema.GetProperty("minimum").GetDouble());
        Assert.Equal(100, schema.GetProperty("maximum").GetDouble());
        Assert.Equal(
            [25, 50],
            schema.GetProperty("examples").EnumerateArray().Select(x => x.GetInt32())
        );
    }

    [Fact]
    public void Record_Property_Leaf_Metadata_Survives_Generated_CSharp_And_Walker()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "ScalarDto": {
                "type": "object",
                "properties": {
                    "count": {
                        "type": "integer",
                        "description": "Current count",
                        "deprecated": true,
                        "default": 3,
                        "examples": [3],
                        "minimum": 1,
                        "maximum": 100,
                        "multipleOf": 1
                    },
                    "ratio": { "type": "number" },
                    "token": {
                        "type": "string",
                        "format": "opaque-token",
                        "readOnly": true,
                        "minLength": 2,
                        "maxLength": 20,
                        "pattern": "^[a-z]+$"
                    },
                    "secret": { "type": "string", "writeOnly": true }
                },
                "required": ["count", "ratio", "token", "secret"]
            }
            """,
            paths: """
            "/scalar": {
                "get": {
                    "operationId": "getScalar",
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/ScalarDto" }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generated = CompilationHelper.FindFile(imported, "ScalarDto.cs");
        Assert.Contains("[RivetSchemaType(\"integer\")]", generated);
        Assert.Contains("[RivetFormat]", generated);
        Assert.Contains("[RivetDescription(\"Current count\")]", generated);
        Assert.Contains("[RivetDefault(\"3\")]", generated);
        Assert.Contains("[RivetExample(\"3\")]", generated);
        Assert.Contains("[Obsolete]", generated);
        Assert.Contains("[RivetReadOnly]", generated);
        Assert.Contains("[RivetWriteOnly]", generated);

        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var emitted = JsonNode.Parse(
            OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null)
        )!;

        var expected = JsonNode.Parse(spec)!["components"]!["schemas"]!["ScalarDto"]!;
        var actual = emitted["components"]!["schemas"]!["ScalarDto"]!;
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"ScalarDto changed.\nExpected: {expected}\nActual: {actual}"
        );
    }

    [Fact]
    public void Named_Enum_And_Brand_Metadata_Survives_Generated_CSharp_And_Walker()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "State": {
                "type": "string",
                "format": "state-code",
                "description": "Lifecycle state",
                "enum": ["ready", "held"]
            },
            "AccountId": {
                "type": "string",
                "format": "account-id",
                "description": "Stable account identifier",
                "x-rivet-brand": "AccountId"
            },
            "MetadataHolder": {
                "type": "object",
                "properties": {
                    "state": { "$ref": "#/components/schemas/State" },
                    "accountId": { "$ref": "#/components/schemas/AccountId" }
                },
                "required": ["state", "accountId"]
            }
            """,
            paths: """
            "/metadata": {
                "get": {
                    "operationId": "getMetadata",
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/MetadataHolder" }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var enumSource = CompilationHelper.FindFile(imported, "State.cs");
        Assert.Contains("[Rivet.RivetDescription(\"Lifecycle state\")]", enumSource);
        Assert.Contains("[Rivet.RivetFormat(\"state-code\")]", enumSource);
        var brandSource = CompilationHelper.FindFile(imported, "AccountId.cs");
        Assert.Contains("[Rivet.RivetDescription(\"Stable account identifier\")]", brandSource);
        Assert.Contains("[Rivet.RivetFormat(\"account-id\")]", brandSource);

        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            endpoints
        );
        Assert.Contains("Lifecycle state", contractJson);
        Assert.Contains("Stable account identifier", contractJson);
        Assert.Contains("state-code", contractJson);
        Assert.Contains("account-id", contractJson);

        var emitted = JsonNode.Parse(
            OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null)
        )!;
        var schemas = emitted["components"]!["schemas"]!;
        Assert.Equal("Lifecycle state", schemas["State"]!["description"]!.GetValue<string>());
        Assert.Equal("state-code", schemas["State"]!["format"]!.GetValue<string>());
        Assert.Equal(
            "Stable account identifier",
            schemas["AccountId"]!["description"]!.GetValue<string>()
        );
        Assert.Equal("account-id", schemas["AccountId"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void Integer_Format_Does_Not_Invent_Constraints_But_Explicit_Range_Survives()
    {
        const string source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            [RivetType]
            public sealed record IntegerMetadata(
                int Count,
                [property: Range(1, 100)] int Limited);
            """;

        using var document = CompilationHelper.EmitOpenApi(source);
        var properties = document
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("IntegerMetadata")
            .GetProperty("properties");

        var count = properties.GetProperty("count");
        Assert.Equal("integer", count.GetProperty("type").GetString());
        Assert.Equal("int32", count.GetProperty("format").GetString());
        Assert.False(count.TryGetProperty("minimum", out _));
        Assert.False(count.TryGetProperty("maximum", out _));

        var limited = properties.GetProperty("limited");
        Assert.Equal(1, limited.GetProperty("minimum").GetDouble());
        Assert.Equal(100, limited.GetProperty("maximum").GetDouble());
    }
}
