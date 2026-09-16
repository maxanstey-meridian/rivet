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
                        "206": {
                            "description": "Partial log stream",
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
    public void Body_Bearing_Informational_Response_Content_Is_Dropped_With_Warning()
    {
        // Negative oracle: HTTP forbids a message body on 1xx/204/205/304, so
        // authored content on 101 must be dropped at import (RIV3024) and the
        // generated C# must declare the status bodyless instead of .Returns<IFormFile>.
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

        Assert.Contains(
            result.Warnings,
            warning =>
                warning.StartsWith("RIV3024:", StringComparison.Ordinal)
                && warning.Contains("GET /logs (LogsGet)", StringComparison.Ordinal)
                && warning.Contains("body-forbidden status 101", StringComparison.Ordinal)
        );
        var contract = CompilationHelper.FindFile(result, "DefaultContract.cs");
        Assert.DoesNotContain("Returns<IFormFile>", contract, StringComparison.Ordinal);
        Assert.Contains(".Returns(101, \"Stream logs\")", contract, StringComparison.Ordinal);
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
