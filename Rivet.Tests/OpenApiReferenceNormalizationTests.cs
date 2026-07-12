using System.Text.Json;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class OpenApiReferenceNormalizationTests
{
    [Fact]
    public void Operation_Parameter_Ref_To_Arbitrary_Local_Json_Pointer_Survives_RoundTrip()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "paths": {
                "/v2/account~keys": {
                  "get": {
                    "operationId": "keys_source",
                    "tags": ["Keys"],
                    "parameters": [
                      {
                        "name": "region",
                        "in": "query",
                        "required": true,
                        "schema": { "type": "integer", "format": "int32" }
                      }
                    ],
                    "responses": { "204": { "description": "No Content" } }
                  }
                },
                "/account/keys": {
                  "get": {
                    "operationId": "keys_list",
                    "tags": ["Keys"],
                    "parameters": [
                      {
                        "$ref": "#/paths/%7E1v2%7E1account%7E0keys/get/parameters/0",
                        "description": "Region override"
                      }
                    ],
                    "responses": { "204": { "description": "No Content" } }
                  }
                }
              }
            }
            """;

        var emitted = ImportCompileWalkAndEmit(spec);
        var parameter = Assert.Single(
            emitted
                .RootElement.GetProperty("paths")
                .GetProperty("/account/keys")
                .GetProperty("get")
                .GetProperty("parameters")
                .EnumerateArray()
        );

        Assert.Equal("region", parameter.GetProperty("name").GetString());
        Assert.Equal("query", parameter.GetProperty("in").GetString());
        Assert.Equal("integer", parameter.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void Referenced_Path_Item_Merges_Local_Siblings_And_Overrides_Path_Parameters_By_Identity()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "x-path-items": {
                "base": {
                  "parameters": [
                    {
                      "name": "id",
                      "in": "path",
                      "required": true,
                      "schema": { "type": "string" }
                    },
                    {
                      "name": "source",
                      "in": "query",
                      "schema": { "type": "boolean" }
                    }
                  ],
                  "get": {
                    "operationId": "accounts_get",
                    "tags": ["Accounts"],
                    "responses": { "204": { "description": "No Content" } }
                  }
                }
              },
              "paths": {
                "/accounts/{id}": {
                  "$ref": "#/x-path-items/base",
                  "parameters": [
                    {
                      "name": "id",
                      "in": "path",
                      "required": true,
                      "schema": { "type": "integer", "format": "int32" }
                    },
                    {
                      "name": "locale",
                      "in": "query",
                      "schema": { "type": "string" }
                    }
                  ],
                  "post": {
                    "operationId": "accounts_create",
                    "tags": ["Accounts"],
                    "responses": { "204": { "description": "No Content" } }
                  }
                }
              }
            }
            """;

        var emitted = ImportCompileWalkAndEmit(spec);
        var path = emitted.RootElement.GetProperty("paths").GetProperty("/accounts/{id}");
        var getParameters = path.GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .ToDictionary(parameter =>
                (parameter.GetProperty("name").GetString(), parameter.GetProperty("in").GetString())
            );

        Assert.True(path.TryGetProperty("post", out _));
        Assert.Equal(3, getParameters.Count);
        Assert.Equal(
            "integer",
            getParameters[("id", "path")].GetProperty("schema").GetProperty("type").GetString()
        );
        Assert.Contains(("source", "query"), getParameters.Keys);
        Assert.Contains(("locale", "query"), getParameters.Keys);
    }

    [Fact]
    public void Missing_Local_Parameter_Target_Fails_Loudly()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "paths": {
                "/accounts": {
                  "get": {
                    "parameters": [
                      { "$ref": "#/components/parameters/missing" }
                    ],
                    "responses": { "204": { "description": "No Content" } }
                  }
                }
              }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompilationHelper.Import(spec, "ReferenceNormalization")
        );

        Assert.Contains("#/components/parameters/missing", exception.Message);
        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cyclic_Local_Parameter_Refs_Fail_Loudly()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "components": {
                "parameters": {
                  "first": { "$ref": "#/components/parameters/second" },
                  "second": { "$ref": "#/components/parameters/first" }
                }
              },
              "paths": {
                "/accounts": {
                  "get": {
                    "parameters": [
                      { "$ref": "#/components/parameters/first" }
                    ],
                    "responses": { "204": { "description": "No Content" } }
                  }
                }
              }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompilationHelper.Import(spec, "ReferenceNormalization")
        );

        Assert.Contains("Cyclic local parameter reference", exception.Message);
    }

    [Fact]
    public void Cyclic_Local_Path_Item_Refs_Fail_Loudly()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "x-path-items": {
                "first": { "$ref": "#/x-path-items/second" },
                "second": { "$ref": "#/x-path-items/first" }
              },
              "paths": {
                "/accounts": { "$ref": "#/x-path-items/first" }
              }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompilationHelper.Import(spec, "ReferenceNormalization")
        );

        Assert.Contains("Cyclic local path-item reference", exception.Message);
    }

    [Fact]
    public void Parameter_Normalization_Does_Not_Inline_Schema_Refs()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "AccountFilter": {
                    "type": "object",
                    "properties": { "active": { "type": "boolean" } }
                  }
                },
                "parameters": {
                  "filter": {
                    "name": "filter",
                    "in": "query",
                    "schema": { "$ref": "#/components/schemas/AccountFilter" }
                  }
                }
              },
              "paths": {
                "/accounts": {
                  "get": {
                    "operationId": "accounts_list",
                    "tags": ["Accounts"],
                    "parameters": [
                      { "$ref": "#/components/parameters/filter" }
                    ],
                    "responses": { "204": { "description": "No Content" } }
                  }
                }
              }
            }
            """;

        var emitted = ImportCompileWalkAndEmit(spec);
        var schema = Assert
            .Single(
                emitted
                    .RootElement.GetProperty("paths")
                    .GetProperty("/accounts")
                    .GetProperty("get")
                    .GetProperty("parameters")
                    .EnumerateArray()
            )
            .GetProperty("schema");

        Assert.Equal("#/components/schemas/AccountFilter", schema.GetProperty("$ref").GetString());
    }

    [Fact]
    public void Response_Ref_To_Arbitrary_Path_Pointer_Survives_RoundTrip()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "paths": {
                "/source~responses": {
                  "get": {
                    "operationId": "source_get",
                    "tags": ["Source"],
                    "responses": {
                      "200": {
                        "description": "Base response",
                        "content": {
                          "application/json": {
                            "schema": { "type": "string" }
                          }
                        }
                      }
                    }
                  }
                },
                "/target": {
                  "get": {
                    "operationId": "target_get",
                    "tags": ["Target"],
                    "responses": {
                      "200": {
                        "$ref": "#/paths/%7E1source%7E0responses/get/responses/200",
                        "description": "Local response"
                      }
                    }
                  }
                }
              }
            }
            """;

        var paths = ImportCompileWalkAndEmit(spec).RootElement.GetProperty("paths");
        var response = paths
            .GetProperty("/target")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200");

        Assert.Equal("Local response", response.GetProperty("description").GetString());
        Assert.Equal(
            "Base response",
            paths
                .GetProperty("/source~responses")
                .GetProperty("get")
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("description")
                .GetString()
        );
        Assert.Equal(
            "string",
            response
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("type")
                .GetString()
        );
    }

    [Fact]
    public void RequestBody_Ref_To_Arbitrary_Path_Pointer_Survives_RoundTrip()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "components": {
                "schemas": {
                  "CreateAccount": {
                    "type": "object",
                    "properties": { "name": { "type": "string" } },
                    "required": ["name"]
                  }
                }
              },
              "paths": {
                "/source~body": {
                  "post": {
                    "operationId": "source_create",
                    "tags": ["Source"],
                    "requestBody": {
                      "required": true,
                      "content": {
                        "application/json": {
                          "schema": { "$ref": "#/components/schemas/CreateAccount" }
                        }
                      }
                    },
                    "responses": { "204": { "description": "Created" } }
                  }
                },
                "/target": {
                  "post": {
                    "operationId": "target_create",
                    "tags": ["Target"],
                    "requestBody": {
                      "$ref": "#/paths/%7E1source%7E0body/post/requestBody",
                      "description": "Local body"
                    },
                    "responses": { "204": { "description": "Created" } }
                  }
                }
              }
            }
            """;

        var requestBody = ImportCompileWalkAndEmit(spec)
            .RootElement.GetProperty("paths")
            .GetProperty("/target")
            .GetProperty("post")
            .GetProperty("requestBody");

        Assert.True(requestBody.GetProperty("required").GetBoolean());
        Assert.Equal(
            "#/components/schemas/CreateAccount",
            requestBody
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
    }

    [Fact]
    public void Response_Header_Ref_To_Arbitrary_Path_Pointer_Survives_RoundTrip()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "paths": {
                "/source~headers": {
                  "get": {
                    "operationId": "source_get",
                    "tags": ["Source"],
                    "responses": {
                      "200": {
                        "description": "OK",
                        "headers": {
                          "X-Rate-Limit": {
                            "description": "Base limit",
                            "schema": { "type": "integer", "format": "int32" }
                          }
                        }
                      }
                    }
                  }
                },
                "/target": {
                  "get": {
                    "operationId": "target_get",
                    "tags": ["Target"],
                    "responses": {
                      "200": {
                        "description": "OK",
                        "headers": {
                          "X-Rate-Limit": {
                            "$ref": "#/paths/%7E1source%7E0headers/get/responses/200/headers/X-Rate-Limit",
                            "description": "Local limit"
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var header = ImportCompileWalkAndEmit(spec)
            .RootElement.GetProperty("paths")
            .GetProperty("/target")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("headers")
            .GetProperty("X-Rate-Limit");

        Assert.Equal("Local limit", header.GetProperty("description").GetString());
        Assert.Equal("integer", header.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void Example_Ref_To_Arbitrary_Path_Pointer_Survives_RoundTrip()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "paths": {
                "/source~examples": {
                  "get": {
                    "operationId": "source_get",
                    "tags": ["Source"],
                    "responses": {
                      "200": {
                        "description": "OK",
                        "content": {
                          "application/json": {
                            "schema": { "type": "object" },
                            "examples": {
                              "canonical": {
                                "summary": "Base example",
                                "value": { "status": "ready" }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                },
                "/target": {
                  "get": {
                    "operationId": "target_get",
                    "tags": ["Target"],
                    "responses": {
                      "200": {
                        "description": "OK",
                        "content": {
                          "application/json": {
                            "schema": { "type": "object" },
                            "examples": {
                              "copied": {
                                "$ref": "#/paths/%7E1source%7E0examples/get/responses/200/content/application%7E1json/examples/canonical",
                                "summary": "Local example"
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var example = ImportCompileWalkAndEmit(spec)
            .RootElement.GetProperty("paths")
            .GetProperty("/target")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples")
            .GetProperty("copied");

        Assert.Equal("ready", example.GetProperty("value").GetProperty("status").GetString());
    }

    [Fact]
    public void Embedded_Component_Example_Refs_And_Their_Values_Survive_RoundTrip()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "components": {
                "examples": {
                  "account": { "value": { "id": "acct_123", "active": true } }
                }
              },
              "paths": {
                "/accounts": {
                  "get": {
                    "operationId": "accounts_get",
                    "tags": ["Accounts"],
                    "responses": {
                      "200": {
                        "description": "OK",
                        "content": {
                          "application/json": {
                            "examples": {
                              "wrapped": {
                                "value": {
                                  "account": { "$ref": "#/components/examples/account" },
                                  "role": "owner"
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        using var emitted = ImportCompileWalkAndEmit(spec);
        var root = emitted.RootElement;
        Assert.Equal(
            "#/components/examples/account",
            root.GetProperty("paths")
                .GetProperty("/accounts")
                .GetProperty("get")
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("examples")
                .GetProperty("wrapped")
                .GetProperty("value")
                .GetProperty("account")
                .GetProperty("$ref")
                .GetString()
        );
        Assert.Equal(
            "acct_123",
            root.GetProperty("components")
                .GetProperty("examples")
                .GetProperty("account")
                .GetProperty("value")
                .GetProperty("id")
                .GetString()
        );
    }

    [Fact]
    public void ExternalValue_Example_Is_Reported_As_Unsupported()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/reports": {
                "get": {
                    "operationId": "reports_get",
                    "tags": ["Reports"],
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": {
                                    "examples": {
                                        "download": {
                                            "externalValue": "https://example.test/report.json"
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

        var generated = CompilationHelper.FindFile(
            CompilationHelper.Import(spec),
            "ReportsContract.cs"
        );

        Assert.Contains(
            "[rivet:unsupported response-example status=200 media-type=application/json name=download reason=external-value]",
            generated
        );
    }

    [Fact]
    public void Cyclic_Local_Response_Refs_Fail_Loudly()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "x-responses": {
                "first": { "$ref": "#/x-responses/second" },
                "second": { "$ref": "#/x-responses/first" }
              },
              "paths": {
                "/accounts": {
                  "get": {
                    "responses": {
                      "200": { "$ref": "#/x-responses/first" }
                    }
                  }
                }
              }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompilationHelper.Import(spec, "ReferenceNormalization")
        );

        Assert.Contains("Cyclic local response reference", exception.Message);
    }

    [Fact]
    public void Missing_Local_Example_Target_Fails_Loudly()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Reference API", "version": "1.0.0" },
              "paths": {
                "/accounts": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "OK",
                        "content": {
                          "application/json": {
                            "examples": {
                              "missing": { "$ref": "#/paths/~1missing/get/responses/200/content/application~1json/examples/missing" }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompilationHelper.Import(spec, "ReferenceNormalization")
        );

        Assert.Contains("#/paths/~1missing", exception.Message);
        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument ImportCompileWalkAndEmit(string spec)
    {
        var result = CompilationHelper.Import(spec, "ReferenceNormalization");
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            security: null
        );
        return JsonDocument.Parse(json);
    }
}
