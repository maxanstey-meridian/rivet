using System.Text.Json;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class ResponseSetFidelityTests
{
    [Fact]
    public void Imported_Void_Method_Defaults_Survive_The_Public_Pipeline()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/get": { "get": { "operationId": "Defaults_Get", "responses": { "200": { "description": "OK" } } } },
            "/put": { "put": { "operationId": "Defaults_Put", "responses": { "200": { "description": "OK" } } } },
            "/patch": { "patch": { "operationId": "Defaults_Patch", "responses": { "200": { "description": "OK" } } } },
            "/head": { "head": { "operationId": "Defaults_Head", "responses": { "200": { "description": "OK" } } } },
            "/options": { "options": { "operationId": "Defaults_Options", "responses": { "200": { "description": "OK" } } } },
            "/post": { "post": { "operationId": "Defaults_Post", "responses": { "201": { "description": "Created" } } } },
            "/delete": { "delete": { "operationId": "Defaults_Delete", "responses": { "204": { "description": "No Content" } } } },
            "/typed-delete": {
                "delete": {
                    "operationId": "Defaults_TypedDelete",
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": { "schema": { "type": "string" } }
                            }
                        }
                    }
                }
            }
            """
        );

        var (_, openApiJson, _) = TraceImport(spec);
        using var openApi = JsonDocument.Parse(openApiJson);

        AssertResponse(openApi, "/get", "get", "200");
        AssertResponse(openApi, "/put", "put", "200");
        AssertResponse(openApi, "/patch", "patch", "200");
        AssertResponse(openApi, "/head", "head", "200");
        AssertResponse(openApi, "/options", "options", "200");
        AssertResponse(openApi, "/post", "post", "201");
        AssertResponse(openApi, "/delete", "delete", "204");
        AssertResponse(openApi, "/typed-delete", "delete", "200");
    }

    [Fact]
    public void Imported_Wildcard_Response_Keys_Survive_Exactly()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/wildcards": {
                "get": {
                    "operationId": "Responses_Wildcards",
                    "responses": {
                        "2XX": {
                            "description": "Any success",
                            "content": {
                                "application/json": { "schema": { "type": "string" } }
                            }
                        },
                        "4XX": { "description": "Any client failure" },
                        "5XX": { "description": "Any server failure" }
                    }
                }
            }
            """
        );

        var (contractJson, openApiJson, _) = TraceImport(spec);
        using var contract = JsonDocument.Parse(contractJson);
        using var openApi = JsonDocument.Parse(openApiJson);
        var contractKeys = contract
            .RootElement.GetProperty("endpoints")[0]
            .GetProperty("responses")
            .EnumerateArray()
            .Select(response => response.GetProperty("statusKey").GetString());
        var responses = Responses(openApi, "/wildcards", "get");

        Assert.Equal(new[] { "2XX", "4XX", "5XX" }, contractKeys);
        Assert.Equal(
            new[] { "2XX", "4XX", "5XX" },
            responses.EnumerateObject().Select(p => p.Name)
        );
        Assert.Equal(
            "string",
            responses
                .GetProperty("2XX")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("type")
                .GetString()
        );
    }

    [Fact]
    public void Imported_Default_And_Concrete_500_Do_Not_Collide()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/failure": {
                "get": {
                    "operationId": "Responses_Failure",
                    "responses": {
                        "default": { "description": "Any other failure" },
                        "500": { "description": "Concrete server failure" }
                    }
                }
            }
            """
        );

        var (contractJson, openApiJson, _) = TraceImport(spec);
        using var contract = JsonDocument.Parse(contractJson);
        using var openApi = JsonDocument.Parse(openApiJson);
        var contractResponses = contract
            .RootElement.GetProperty("endpoints")[0]
            .GetProperty("responses");
        var responses = Responses(openApi, "/failure", "get");

        Assert.Contains(
            contractResponses.EnumerateArray(),
            response =>
                response.TryGetProperty("statusKey", out var key) && key.GetString() == "default"
        );
        Assert.Contains(
            contractResponses.EnumerateArray(),
            response =>
                response.TryGetProperty("statusCode", out var code) && code.GetInt32() == 500
        );
        Assert.Equal(2, responses.EnumerateObject().Count());
        Assert.Equal(
            "Any other failure",
            responses.GetProperty("default").GetProperty("description").GetString()
        );
        Assert.Equal(
            "Concrete server failure",
            responses.GetProperty("500").GetProperty("description").GetString()
        );
    }

    [Fact]
    public void Imported_Concrete_Response_Set_And_Descriptions_Survive()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/many": {
                "post": {
                    "operationId": "Responses_Many",
                    "responses": {
                        "102": { "description": "Processing" },
                        "201": { "description": "Created primary" },
                        "202": { "description": "Accepted secondary" },
                        "304": { "description": "Not modified" }
                    }
                }
            }
            """
        );

        var (_, openApiJson, _) = TraceImport(spec);
        using var openApi = JsonDocument.Parse(openApiJson);
        var responses = Responses(openApi, "/many", "post");

        Assert.Equal(
            new[] { "102", "201", "202", "304" },
            responses.EnumerateObject().Select(p => p.Name)
        );
        Assert.Equal(
            "Processing",
            responses.GetProperty("102").GetProperty("description").GetString()
        );
        Assert.Equal(
            "Created primary",
            responses.GetProperty("201").GetProperty("description").GetString()
        );
        Assert.Equal(
            "Accepted secondary",
            responses.GetProperty("202").GetProperty("description").GetString()
        );
        Assert.Equal(
            "Not modified",
            responses.GetProperty("304").GetProperty("description").GetString()
        );
    }

    [Fact]
    public void Imported_Error_Only_Operation_Suppresses_Implicit_Success()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/error-only": {
                "get": {
                    "operationId": "Errors_GetOnly",
                    "responses": {
                        "400": { "description": "Bad request only" }
                    }
                }
            }
            """
        );

        var (contractJson, openApiJson, generatedContract) = TraceImport(spec);
        using var contract = JsonDocument.Parse(contractJson);
        using var openApi = JsonDocument.Parse(openApiJson);

        Assert.Contains(".SuppressImplicitResponse()", generatedContract);
        var contractResponses = contract
            .RootElement.GetProperty("endpoints")[0]
            .GetProperty("responses");
        Assert.Single(contractResponses.EnumerateArray());
        Assert.Equal(400, contractResponses[0].GetProperty("statusCode").GetInt32());
        AssertResponse(openApi, "/error-only", "get", "400");
    }

    [Fact]
    public void Authored_Void_Method_Defaults_Are_Materialized_Through_Contract_Json()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class DefaultsContract
            {
                public static readonly Define Get = Define.Get("/get");
                public static readonly Define Put = Define.Put("/put");
                public static readonly Define Patch = Define.Patch("/patch");
                public static readonly Define Head = Define.Head("/head");
                public static readonly Define Options = Define.Options("/options");
                public static readonly Define Post = Define.Post("/post");
                public static readonly Define Delete = Define.Delete("/delete");
                public static readonly Define TypedDelete = Define.Delete<string>("/typed-delete");
            }
            """;

        var (endpoints, walker) = CompilationHelper.WalkContract(source);
        var contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            endpoints
        );
        var openApiJson = CompilationHelper.EmitOpenApiFromJson(contractJson);
        using var openApi = JsonDocument.Parse(openApiJson);

        AssertResponse(openApi, "/get", "get", "200");
        AssertResponse(openApi, "/put", "put", "200");
        AssertResponse(openApi, "/patch", "patch", "200");
        AssertResponse(openApi, "/head", "head", "200");
        AssertResponse(openApi, "/options", "options", "200");
        AssertResponse(openApi, "/post", "post", "201");
        AssertResponse(openApi, "/delete", "delete", "204");
        AssertResponse(openApi, "/typed-delete", "delete", "200");
    }

    [Theory]
    [InlineData("GET", false, "200")]
    [InlineData("POST", false, "201")]
    [InlineData("DELETE", false, "204")]
    [InlineData("DELETE", true, "200")]
    public void Empty_Response_Sets_Emit_A_Concrete_Method_Default(
        string method,
        bool hasReturnType,
        string expectedStatus
    )
    {
        var endpoint = new Rivet.Tool.Model.TsEndpointDefinition(
            "empty",
            method,
            "/empty",
            [],
            hasReturnType ? new Rivet.Tool.Model.TsType.Primitive("string") : null,
            "empty",
            []
        );

        var contractJson = ContractEmitter.Emit([], [], [endpoint]);
        using var contract = JsonDocument.Parse(contractJson);
        var contractResponses = contract
            .RootElement.GetProperty("endpoints")[0]
            .GetProperty("responses");
        Assert.Single(contractResponses.EnumerateArray());
        Assert.Equal(
            expectedStatus,
            contractResponses[0].GetProperty("statusCode").GetInt32().ToString()
        );

        using var openApi = JsonDocument.Parse(CompilationHelper.EmitOpenApiFromJson(contractJson));
        AssertResponse(openApi, "/empty", method.ToLowerInvariant(), expectedStatus);
    }

    private static void AssertResponse(
        JsonDocument document,
        string path,
        string method,
        string statusKey
    )
    {
        var responses = Responses(document, path, method);
        Assert.Single(responses.EnumerateObject());
        Assert.True(responses.TryGetProperty(statusKey, out _));
    }

    private static JsonElement Responses(JsonDocument document, string path, string method) =>
        document
            .RootElement.GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method)
            .GetProperty("responses");

    private static (string ContractJson, string OpenApiJson, string GeneratedContract) TraceImport(
        string spec
    )
    {
        var import = CompilationHelper.Import(spec);
        var generatedContract = Assert
            .Single(
                import.Files,
                file => file.FileName.StartsWith("Contracts/", StringComparison.Ordinal)
            )
            .Content;
        var compilation = CompilationHelper.CompileImportResult(import);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            endpoints
        );
        return (
            contractJson,
            CompilationHelper.EmitOpenApiFromJson(contractJson),
            generatedContract
        );
    }
}
