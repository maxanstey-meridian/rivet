namespace Rivet.Tests;

public sealed class GeneratedSourceRegressionTests
{
    [Fact]
    public void Secondary_IFormFile_Response_Gets_Http_Namespace()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/logs": {
                "get": {
                    "operationId": "logs_Get",
                    "responses": {
                        "200": { "description": "Text logs" },
                        "101": {
                            "description": "Stream logs",
                            "content": {
                                "application/json": {
                                    "schema": { "type": "string", "format": "binary" }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var result = CompilationHelper.Import(spec);
        var generated = Assert.Single(
            result.Files,
            file => file.Content.Contains("Returns<IFormFile>", StringComparison.Ordinal)
        );

        Assert.Contains("using Microsoft.AspNetCore.Http;", generated.Content);
        Assert.Empty(RealWorldImportTests.GetCompilationErrors(result));
    }

    [Fact]
    public void Exact_Status_Response_Header_Generates_Unambiguous_CSharp()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/limited": {
                "get": {
                    "operationId": "limits_Get",
                    "responses": {
                        "default": {
                            "description": "Fallback",
                            "headers": {
                                "RateLimit-Limit": {
                                    "description": "Request quota",
                                    "schema": { "type": "string" }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        var result = CompilationHelper.Import(spec);
        var contract = Assert
            .Single(
                result.Files,
                file => file.FileName.StartsWith("Contracts/", StringComparison.Ordinal)
            )
            .Content;

        Assert.Contains(
            ".WithResponseHeaderKey<string>(\"default\", \"RateLimit-Limit\", \"Request quota\"",
            contract
        );
        Assert.Empty(RealWorldImportTests.GetCompilationErrors(result));
    }
}
