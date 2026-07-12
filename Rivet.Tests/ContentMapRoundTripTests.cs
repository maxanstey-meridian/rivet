using System.Text.Json;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ContentMapRoundTripTests
{
    [Fact]
    public void Swagger_Produces_Does_Not_Invent_Content_For_SchemaLess_Responses()
    {
        const string spec = """
            {
              "swagger": "2.0",
              "info": { "title": "Legacy API", "version": "1.0.0" },
              "produces": ["application/json", "application/xml"],
              "paths": {
                "/pets": {
                  "get": {
                    "operationId": "pets_get",
                    "tags": ["Pets"],
                    "responses": {
                      "200": {
                        "description": "Pet found",
                        "schema": { "type": "string" }
                      },
                      "400": { "description": "Invalid pet identifier" },
                      "404": { "description": "Pet not found" }
                    }
                  }
                }
              }
            }
            """;

        var imported = CompilationHelper.Import(spec);
        using var document = RoundTrip(imported, out _);
        var responses = document
            .RootElement.GetProperty("paths")
            .GetProperty("/pets")
            .GetProperty("get")
            .GetProperty("responses");

        Assert.Equal(
            "Invalid pet identifier",
            responses.GetProperty("400").GetProperty("description").GetString()
        );
        Assert.Equal(
            "Pet not found",
            responses.GetProperty("404").GetProperty("description").GetString()
        );
        Assert.False(responses.GetProperty("400").TryGetProperty("content", out _));
        Assert.False(responses.GetProperty("404").TryGetProperty("content", out _));
    }

    [Fact]
    public void SchemaLess_Response_Descriptions_And_Examples_Survive_The_Public_Pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/jobs": {
                "post": {
                    "operationId": "jobs_create",
                    "tags": ["Jobs"],
                    "responses": {
                        "400": {
                            "description": "Invalid job request",
                            "content": {
                                "application/json": {
                                    "examples": {
                                        "validation": {
                                            "value": { "message": "Name is required" }
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

        var imported = CompilationHelper.Import(spec);
        using var document = RoundTrip(imported, out _);
        var response = document
            .RootElement.GetProperty("paths")
            .GetProperty("/jobs")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("400");

        Assert.Equal("Invalid job request", response.GetProperty("description").GetString());
        Assert.Equal(
            "Name is required",
            response
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("examples")
                .GetProperty("validation")
                .GetProperty("value")
                .GetProperty("message")
                .GetString()
        );
    }

    [Fact]
    public void File_Content_Type_Is_Not_Applied_To_Secondary_SchemaLess_Successes()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/reports": {
                "get": {
                    "operationId": "reports_download",
                    "tags": ["Reports"],
                    "responses": {
                        "200": {
                            "description": "Report",
                            "content": {
                                "application/pdf": {
                                    "schema": { "type": "string", "format": "binary" }
                                }
                            }
                        },
                        "202": {
                            "description": "Report is still being generated"
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        using var document = RoundTrip(imported, out _);
        var responses = document
            .RootElement.GetProperty("paths")
            .GetProperty("/reports")
            .GetProperty("get")
            .GetProperty("responses");

        Assert.True(
            responses
                .GetProperty("200")
                .GetProperty("content")
                .TryGetProperty("application/pdf", out _)
        );
        Assert.False(responses.GetProperty("202").TryGetProperty("content", out _));
        Assert.Equal(
            "Report is still being generated",
            responses.GetProperty("202").GetProperty("description").GetString()
        );
    }

    [Fact]
    public void Same_Wire_Parameter_Name_In_Different_Locations_Does_Not_Leak_Clr_Collision_Name()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/disclosures/{langCode}": {
                "get": {
                    "operationId": "disclosures_get",
                    "tags": ["Disclosures"],
                    "parameters": [
                        { "name": "langCode", "in": "path", "required": true, "schema": { "type": "string" } },
                        { "name": "langCode", "in": "query", "required": false, "schema": { "type": "string" } }
                    ],
                    "responses": { "204": { "description": "No content" } }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        using var document = RoundTrip(imported, out _);
        var parameters = document
            .RootElement.GetProperty("paths")
            .GetProperty("/disclosures/{langCode}")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .Select(parameter =>
                (parameter.GetProperty("name").GetString(), parameter.GetProperty("in").GetString())
            )
            .ToList();

        Assert.Equal(2, parameters.Count);
        Assert.Contains(("langCode", "path"), parameters);
        Assert.Contains(("langCode", "query"), parameters);
    }

    [Fact]
    public void Optional_SchemaLess_Request_And_Response_Survive_The_Public_Pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/hooks": {
                "post": {
                    "operationId": "hooks_receive",
                    "tags": ["Hooks"],
                    "requestBody": {
                        "required": false,
                        "content": {
                            "application/webhook+json": {}
                        }
                    },
                    "responses": {
                        "200": {
                            "description": "Accepted",
                            "content": {
                                "text/event-stream": {}
                            }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generatedContract = CompilationHelper.FindFile(imported, "HooksContract.cs");
        Assert.Contains(".RequestContent(\"application/webhook+json\")", generatedContract);
        Assert.Contains(".RequestBodyRequired(false)", generatedContract);
        Assert.Contains(".ResponseContent(200, \"text/event-stream\")", generatedContract);

        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var walked = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            walked
        );
        var (types, enums, endpoints, brands) = JsonContractReader.Read(contractJson);
        var openApi = OpenApiEmitter.Emit(
            endpoints,
            types.ToDictionary(type => type.Name),
            brands,
            enums,
            security: null
        );

        using var document = JsonDocument.Parse(openApi);
        var operation = document
            .RootElement.GetProperty("paths")
            .GetProperty("/hooks")
            .GetProperty("post");
        var requestBody = operation.GetProperty("requestBody");
        Assert.False(requestBody.GetProperty("required").GetBoolean());
        Assert.False(
            requestBody
                .GetProperty("content")
                .GetProperty("application/webhook+json")
                .TryGetProperty("schema", out _)
        );
        Assert.False(
            operation
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("text/event-stream")
                .TryGetProperty("schema", out _)
        );
    }

    [Fact]
    public void SchemaLess_Request_Does_Not_Emit_Unsupported_Marker()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/hooks": {
                "post": {
                    "operationId": "hooks_receive",
                    "tags": ["Hooks"],
                    "requestBody": {
                        "required": false,
                        "content": {
                            "text/plain": {},
                            "application/octet-stream": {}
                        }
                    },
                    "responses": { "204": { "description": "Accepted" } }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generatedContract = CompilationHelper.FindFile(imported, "HooksContract.cs");

        Assert.DoesNotContain("[rivet:unsupported", generatedContract);
        Assert.Contains(".RequestContent(\"text/plain\")", generatedContract);
        Assert.Contains(".RequestContent(\"application/octet-stream\")", generatedContract);
    }

    [Fact]
    public void Binary_Success_And_Object_Error_Sharing_Media_Type_Do_Not_Emit_Unsupported_Marker()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/documents/{id}": {
                "get": {
                    "operationId": "documents_download",
                    "tags": ["Documents"],
                    "parameters": [
                        { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                    ],
                    "responses": {
                        "200": {
                            "description": "Document",
                            "content": {
                                "application/pdf": { "schema": { "type": "string", "format": "binary" } }
                            }
                        },
                        "400": {
                            "description": "Invalid request",
                            "content": {
                                "application/pdf": {
                                    "schema": {
                                        "type": "object",
                                        "properties": { "message": { "type": "string" } },
                                        "required": ["message"]
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generatedContract = CompilationHelper.FindFile(imported, "DocumentsContract.cs");

        Assert.DoesNotContain("[rivet:unsupported", generatedContract);
        Assert.Contains(".ResponseBinaryContent(200, \"application/pdf\")", generatedContract);
        Assert.Contains(".ResponseContent<", generatedContract);
        Assert.Contains("400, \"application/pdf\"", generatedContract);

        using var document = RoundTrip(imported, out _);
        var responses = document
            .RootElement.GetProperty("paths")
            .GetProperty("/documents/{id}")
            .GetProperty("get")
            .GetProperty("responses");
        Assert.Equal(
            "binary",
            responses
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/pdf")
                .GetProperty("schema")
                .GetProperty("format")
                .GetString()
        );
        Assert.Equal(
            "object",
            responses
                .GetProperty("400")
                .GetProperty("content")
                .GetProperty("application/pdf")
                .GetProperty("schema")
                .GetProperty("type")
                .GetString()
        );
    }

    [Fact]
    public void Mixed_Json_And_Binary_Maps_Survive_The_Public_Pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/documents": {
                "post": {
                    "operationId": "documents_convert",
                    "tags": ["Documents"],
                    "requestBody": {
                        "required": true,
                        "content": {
                            "application/json": {
                                "schema": {
                                    "type": "object",
                                    "properties": { "name": { "type": "string" } },
                                    "required": ["name"]
                                }
                            },
                            "application/pdf": {
                                "schema": { "type": "string", "format": "binary" }
                            }
                        }
                    },
                    "responses": {
                        "200": {
                            "description": "Converted",
                            "content": {
                                "application/json": {
                                    "schema": {
                                        "type": "object",
                                        "properties": { "id": { "type": "string" } },
                                        "required": ["id"]
                                    }
                                },
                                "application/pdf": {
                                    "schema": { "type": "string", "format": "binary" }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generatedContract = CompilationHelper.FindFile(imported, "DocumentsContract.cs");
        Assert.Contains(".RequestContent<", generatedContract);
        Assert.Contains(".RequestBinaryContent(\"application/pdf\")", generatedContract);
        Assert.Contains(".ResponseContent<", generatedContract);
        Assert.Contains(".ResponseBinaryContent(200, \"application/pdf\")", generatedContract);

        using var document = RoundTrip(imported, out var contractJson);
        Assert.Contains("\"isBinary\": true", contractJson);

        var operation = document
            .RootElement.GetProperty("paths")
            .GetProperty("/documents")
            .GetProperty("post");
        var requestContent = operation.GetProperty("requestBody").GetProperty("content");
        Assert.Equal(2, requestContent.EnumerateObject().Count());
        Assert.True(requestContent.TryGetProperty("application/json", out _));
        Assert.Equal(
            "binary",
            requestContent
                .GetProperty("application/pdf")
                .GetProperty("schema")
                .GetProperty("format")
                .GetString()
        );

        var responseContent = operation
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content");
        Assert.Equal(2, responseContent.EnumerateObject().Count());
        Assert.True(responseContent.TryGetProperty("application/json", out _));
        Assert.Equal(
            "binary",
            responseContent
                .GetProperty("application/pdf")
                .GetProperty("schema")
                .GetProperty("format")
                .GetString()
        );
    }

    [Theory]
    [InlineData("get")]
    [InlineData("delete")]
    public void Get_And_Delete_Bodies_Coexist_With_Parameters(string method)
    {
        var paths = $$"""
            "/search/{scope}": {
                "{{method}}": {
                    "operationId": "search_{{method}}",
                    "tags": ["Search"],
                    "parameters": [
                        { "name": "scope", "in": "path", "required": true, "schema": { "type": "string" } },
                        { "name": "limit", "in": "query", "required": false, "schema": { "type": "integer", "format": "int32" } }
                    ],
                    "requestBody": {
                        "required": true,
                        "content": {
                            "application/json": {
                                "schema": {
                                    "type": "object",
                                    "properties": { "query": { "type": "string" } },
                                    "required": ["query"]
                                }
                            }
                        }
                    },
                    "responses": { "204": { "description": "No Content" } }
                }
            }
            """;

        var imported = CompilationHelper.Import(CompilationHelper.BuildSpec(paths: paths));
        using var document = RoundTrip(imported, out _);
        var operation = document
            .RootElement.GetProperty("paths")
            .GetProperty("/search/{scope}")
            .GetProperty(method);

        Assert.True(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.True(
            operation
                .GetProperty("requestBody")
                .GetProperty("content")
                .TryGetProperty("application/json", out _)
        );
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToList();
        Assert.Contains(
            parameters,
            parameter =>
                parameter.GetProperty("name").GetString() == "scope"
                && parameter.GetProperty("in").GetString() == "path"
        );
        Assert.Contains(
            parameters,
            parameter =>
                parameter.GetProperty("name").GetString() == "limit"
                && parameter.GetProperty("in").GetString() == "query"
        );
    }

    [Fact]
    public void Optional_Binary_Request_With_Parameters_Survives_The_Public_Pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/blobs/{id}": {
                "put": {
                    "operationId": "blobs_upload",
                    "tags": ["Blobs"],
                    "parameters": [
                        { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
                        { "name": "checksum", "in": "query", "required": false, "schema": { "type": "string" } }
                    ],
                    "requestBody": {
                        "required": false,
                        "content": {
                            "application/octet-stream": {
                                "schema": { "type": "string", "format": "binary" }
                            }
                        }
                    },
                    "responses": { "204": { "description": "No Content" } }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generatedContract = CompilationHelper.FindFile(imported, "BlobsContract.cs");
        Assert.Contains(".RequestBinaryContent(\"application/octet-stream\")", generatedContract);
        Assert.Contains(".AcceptsBinary()", generatedContract);

        using var document = RoundTrip(imported, out _);
        var operation = document
            .RootElement.GetProperty("paths")
            .GetProperty("/blobs/{id}")
            .GetProperty("put");
        Assert.False(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.Equal(
            "binary",
            operation
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/octet-stream")
                .GetProperty("schema")
                .GetProperty("format")
                .GetString()
        );
        Assert.Equal(2, operation.GetProperty("parameters").GetArrayLength());
    }

    [Fact]
    public void Optional_Multipart_Request_Survives_The_Public_Pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/uploads": {
                "post": {
                    "operationId": "uploads_create",
                    "tags": ["Uploads"],
                    "requestBody": {
                        "required": false,
                        "content": {
                            "multipart/form-data": {
                                "schema": {
                                    "type": "object",
                                    "properties": {
                                        "file": { "type": "string", "format": "binary" },
                                        "caption": { "type": "string" }
                                    }
                                }
                            }
                        }
                    },
                    "responses": { "204": { "description": "No Content" } }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        using var document = RoundTrip(imported, out _);
        var requestBody = document
            .RootElement.GetProperty("paths")
            .GetProperty("/uploads")
            .GetProperty("post")
            .GetProperty("requestBody");
        Assert.False(requestBody.GetProperty("required").GetBoolean());
        Assert.True(
            requestBody.GetProperty("content").TryGetProperty("multipart/form-data", out _)
        );
    }

    [Fact]
    public void Explicit_Optionality_Applies_To_Legacy_Request_Body_Branches()
    {
        var endpoints = new[]
        {
            new TsEndpointDefinition(
                "binary",
                "PUT",
                "/binary",
                [],
                null,
                "requests",
                [],
                BinaryRequestContentType: "application/octet-stream",
                RequestBodyRequired: false,
                RequestBodyPresent: true
            ),
            new TsEndpointDefinition(
                "multipart",
                "POST",
                "/multipart",
                [new TsEndpointParam("file", new TsType.Primitive("File"), ParamSource.File)],
                null,
                "requests",
                [],
                RequestBodyRequired: false,
                RequestBodyPresent: true
            ),
            new TsEndpointDefinition(
                "bodyParam",
                "POST",
                "/body-param",
                [new TsEndpointParam("body", new TsType.Primitive("string"), ParamSource.Body)],
                null,
                "requests",
                [],
                RequestBodyRequired: false,
                RequestBodyPresent: true
            ),
            new TsEndpointDefinition(
                "requestType",
                "POST",
                "/request-type",
                [],
                null,
                "requests",
                [],
                RequestType: new TsType.Primitive("string"),
                RequestBodyRequired: false,
                RequestBodyPresent: true
            ),
        };

        var openApi = OpenApiEmitter.Emit(
            endpoints,
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>(),
            security: null
        );
        using var document = JsonDocument.Parse(openApi);
        var paths = document.RootElement.GetProperty("paths");

        Assert.All(
            new[] { "/binary", "/multipart", "/body-param", "/request-type" },
            path =>
                Assert.False(
                    paths
                        .GetProperty(path)
                        .EnumerateObject()
                        .Single()
                        .Value.GetProperty("requestBody")
                        .GetProperty("required")
                        .GetBoolean()
                )
        );
    }

    [Fact]
    public void Empty_Request_Content_Map_Preserves_Body_Presence_And_Requiredness()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/signals": {
                "post": {
                    "operationId": "signals_send",
                    "tags": ["Signals"],
                    "requestBody": {
                        "required": false,
                        "content": {}
                    },
                    "responses": { "204": { "description": "No Content" } }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generatedContract = CompilationHelper.FindFile(imported, "SignalsContract.cs");
        Assert.Contains(".RequestBody()", generatedContract);
        Assert.Contains(".RequestBodyRequired(false)", generatedContract);

        using var document = RoundTrip(imported, out var contractJson);
        using var contractDocument = JsonDocument.Parse(contractJson);
        var contractEndpoint = contractDocument.RootElement.GetProperty("endpoints")[0];
        Assert.True(contractEndpoint.GetProperty("requestBodyPresent").GetBoolean());
        Assert.Equal(0, contractEndpoint.GetProperty("requestContents").GetArrayLength());

        var requestBody = document
            .RootElement.GetProperty("paths")
            .GetProperty("/signals")
            .GetProperty("post")
            .GetProperty("requestBody");
        Assert.False(requestBody.GetProperty("required").GetBoolean());
        Assert.Empty(requestBody.GetProperty("content").EnumerateObject());
    }

    [Fact]
    public void Post_Parameters_Do_Not_Invent_A_Request_Body()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/jobs/{id}": {
                "post": {
                    "operationId": "jobs_start",
                    "tags": ["Jobs"],
                    "parameters": [
                        { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
                        { "name": "dryRun", "in": "query", "required": false, "schema": { "type": "boolean" } }
                    ],
                    "responses": { "202": { "description": "Accepted" } }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        using var document = RoundTrip(imported, out _);
        var operation = document
            .RootElement.GetProperty("paths")
            .GetProperty("/jobs/{id}")
            .GetProperty("post");

        Assert.False(operation.TryGetProperty("requestBody", out _));
        Assert.Equal(2, operation.GetProperty("parameters").GetArrayLength());
    }

    [Fact]
    public void Inline_Primitive_Request_Content_Preserves_Exact_Base64_Format()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/artwork": {
                "post": {
                    "operationId": "artwork_upload",
                    "requestBody": {
                        "required": true,
                        "content": {
                            "image/jpeg": {
                                "schema": { "type": "string", "format": "base64" }
                            }
                        }
                    },
                    "responses": { "204": { "description": "Uploaded" } }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generated = CompilationHelper.FindFile(imported, "DefaultContract.cs");
        Assert.Contains("schemaType: \"string\", format: \"base64\"", generated);
        using var document = RoundTrip(imported, out var contractJson);
        using var contract = JsonDocument.Parse(contractJson);
        var contractContent = contract
            .RootElement.GetProperty("endpoints")[0]
            .GetProperty("requestContents")[0];
        Assert.Equal("string", contractContent.GetProperty("schemaType").GetString());
        Assert.Equal("base64", contractContent.GetProperty("format").GetString());
        Assert.True(contractContent.GetProperty("isFormatSpecified").GetBoolean());
        var schema = document
            .RootElement.GetProperty("paths")
            .GetProperty("/artwork")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("image/jpeg")
            .GetProperty("schema");

        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal("base64", schema.GetProperty("format").GetString());
        Assert.False(schema.TryGetProperty("contentEncoding", out _));
    }

    [Fact]
    public void Inline_Primitive_Response_Content_Preserves_Custom_String_Format()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/page": {
                "get": {
                    "operationId": "page_get",
                    "responses": {
                        "200": {
                            "description": "Page",
                            "content": {
                                "text/html": {
                                    "schema": { "type": "string", "format": "html" }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generated = CompilationHelper.FindFile(imported, "DefaultContract.cs");
        Assert.Contains("schemaType: \"string\", format: \"html\"", generated);
        using var document = RoundTrip(imported, out _);
        var schema = document
            .RootElement.GetProperty("paths")
            .GetProperty("/page")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("text/html")
            .GetProperty("schema");

        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal("html", schema.GetProperty("format").GetString());
    }

    [Fact]
    public void Inline_Primitive_Response_Content_Preserves_Nullable_Number_Format_Absence()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/measurement": {
                "get": {
                    "operationId": "measurement_get",
                    "responses": {
                        "200": {
                            "description": "Measurement",
                            "content": {
                                "application/json": {
                                    "schema": { "type": ["number", "null"] }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generated = CompilationHelper.FindFile(imported, "DefaultContract.cs");
        Assert.Contains("schemaType: \"number\", format: \"\"", generated);
        using var document = RoundTrip(imported, out _);
        var schema = document
            .RootElement.GetProperty("paths")
            .GetProperty("/measurement")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");

        Assert.Equal(
            ["number", "null"],
            schema.GetProperty("type").EnumerateArray().Select(type => type.GetString())
        );
        Assert.False(schema.TryGetProperty("format", out _));
    }

    [Fact]
    public void Composed_Response_Content_Does_Not_Guess_Scalar_Leaf_Provenance()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/composed": {
                "get": {
                    "operationId": "composed_get",
                    "responses": {
                        "200": {
                            "description": "Composed",
                            "content": {
                                "application/json": {
                                    "schema": {
                                        "oneOf": [
                                            { "type": "number" },
                                            { "type": "string" }
                                        ]
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var generated = CompilationHelper.FindFile(imported, "DefaultContract.cs");
        var contentCall = Assert.Single(
            generated.Split('\n'),
            line => line.Contains(".ResponseContent<", StringComparison.Ordinal)
        );
        Assert.DoesNotContain("schemaType:", contentCall);
        Assert.DoesNotContain("format:", contentCall);
    }

    private static JsonDocument RoundTrip(
        Rivet.Tool.Import.ImportResult imported,
        out string contractJson
    )
    {
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var walked = CompilationHelper.WalkContracts(compilation, discovered, walker);
        contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            walked
        );
        var (types, enums, endpoints, brands) = JsonContractReader.Read(contractJson);
        var openApi = OpenApiEmitter.Emit(
            endpoints,
            types.ToDictionary(type => type.Name),
            brands,
            enums,
            security: null
        );
        return JsonDocument.Parse(openApi);
    }
}
