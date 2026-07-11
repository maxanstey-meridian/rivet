using System.Text.Json;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// FABLE_ROUNDTRIP #8/#9/#10 — the three small inventions. Redirect-only
/// operations keep their 3xx instead of gaining a fabricated 200; JSON null
/// example values survive instead of leaking Microsoft.OpenApi's sentinel
/// string; text/* media types survive as .AcceptsContentType() /
/// .ProducesContentType() overrides instead of re-emitting as
/// application/json (the octet-stream bug's sibling).
/// </summary>
public sealed class ContentTypeOverrideAndRedirectTests
{
    // ---- walker + emitter: the new builder calls are contract concepts ----

    [Fact]
    public void ContentType_Overrides_Flow_From_Contract_To_Walker()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class RenderContract
            {
                public static readonly Define RenderRaw =
                    Define.Post<string, string>("/render/raw")
                        .Status(200)
                        .AcceptsContentType("text/plain")
                        .ProducesContentType("text/html");
            }
            """;

        var ep = Assert.Single(CompilationHelper.WalkContract(source).Endpoints);

        Assert.Equal("text/plain", ep.RequestContentTypeOverride);
        Assert.Equal("text/html", ep.ResponseContentTypeOverride);
    }

    [Fact]
    public void ContentType_Overrides_Survive_Contract_And_OpenApi_RoundTrip()
    {
        var endpoint = new TsEndpointDefinition(
            "renderRaw", "POST", "/render/raw", [], new TsType.Primitive("string"),
            "RenderController", [new TsResponseType(200, new TsType.Primitive("string"))],
            RequestType: new TsType.Primitive("string"),
            RequestContentTypeOverride: "text/plain",
            ResponseContentTypeOverride: "text/html");

        var contractJson = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        var readEndpoint = Assert.Single(JsonContractReader.Read(contractJson).Endpoints);
        Assert.Equal("text/plain", readEndpoint.RequestContentTypeOverride);
        Assert.Equal("text/html", readEndpoint.ResponseContentTypeOverride);

        var openApiJson = OpenApiEmitter.Emit(
            [readEndpoint], new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(), new Dictionary<string, TsType>(), null);
        using var document = JsonDocument.Parse(openApiJson);
        var operation = document.RootElement.GetProperty("paths").GetProperty("/render/raw").GetProperty("post");
        Assert.True(operation.GetProperty("requestBody").GetProperty("content").TryGetProperty("text/plain", out _));
        Assert.True(operation.GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("text/html", out _));
    }

    [Fact]
    public void Redirect_Only_Import_Declares_The_3xx_And_No_Fabricated_200()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "Unused": { "type": "object", "properties": { "x": { "type": "string" } } }
                """,
            paths: """
                "/download/{artifact_id}": {
                    "get": {
                        "operationId": "downloadArtifact",
                        "parameters": [{"name": "artifact_id", "in": "path", "required": true, "schema": {"type": "integer"}}],
                        "responses": { "302": { "description": "redirect to blob storage" } }
                    }
                }
                """,
            title: "API");

        var contract = CompilationHelper.FindFile(CompilationHelper.Import(spec), "DefaultContract.cs");

        Assert.Contains(".Status(302)", contract);
        Assert.DoesNotContain(".Returns(302", contract); // promoted, not double-declared
    }

    [Fact]
    public void Text_Body_And_Response_Import_As_ContentType_Overrides()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "Unused": { "type": "object", "properties": { "x": { "type": "string" } } }
                """,
            paths: """
                "/render/raw": {
                    "post": {
                        "operationId": "renderRaw",
                        "requestBody": {"required": true, "content": {"text/plain": {"schema": {"type": "string"}}}},
                        "responses": {
                            "200": { "description": "rendered", "content": { "text/html": { "schema": { "type": "string" } } } }
                        }
                    }
                }
                """,
            title: "API");

        var contract = CompilationHelper.FindFile(CompilationHelper.Import(spec), "DefaultContract.cs");

        Assert.Contains(".AcceptsContentType(\"text/plain\")", contract);
        Assert.Contains(".ProducesContentType(\"text/html\")", contract);
    }

    [Fact]
    public void Null_Example_Values_Do_Not_Leak_The_Library_Sentinel()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "Unused": { "type": "object", "properties": { "x": { "type": "string" } } }
                """,
            paths: """
                "/echo": {
                    "post": {
                        "operationId": "echo",
                        "requestBody": {"required": true, "content": {"application/json": {
                            "schema": {"type": "object", "properties": {"x": {"type": "string"}}},
                            "examples": {"default": {"value": null}}
                        }}},
                        "responses": { "200": { "description": "ok" } }
                    }
                }
                """,
            title: "API");

        var contract = CompilationHelper.FindFile(CompilationHelper.Import(spec), "DefaultContract.cs");

        Assert.DoesNotContain("openapi-json-null-sentinel", contract);
        Assert.Contains(".RequestExampleJson(\"null\"", contract);
    }

    // ---- runtime builder guards ----

    [Fact]
    public void AcceptsContentType_Rejects_Combination_With_FormEncoded()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Rivet.Define.Post("/x").FormEncoded().AcceptsContentType("text/plain"));

        Assert.Contains("AcceptsContentType", ex.Message);
    }

    [Fact]
    public void ProducesContentType_Rejects_Combination_With_ProducesFile()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Rivet.Define.Get("/x").ProducesFile("application/pdf").ProducesContentType("text/html"));

        Assert.Contains("ProducesContentType", ex.Message);
    }
}
