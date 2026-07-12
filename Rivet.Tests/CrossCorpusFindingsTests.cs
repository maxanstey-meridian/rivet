using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// FABLE_ROUNDTRIP cross-corpus findings (cloudflare/notion, 2026-06-12) — the
/// three classes the github corpus never exercised. #1: a dictionary/collection/
/// scalar input on a bodyless method enumerated its CLR members (Count, Keys,
/// Comparer, Capacity, …) into the emitted spec as invented query params (118
/// cloudflare DELETE ops + 2 notion GET ops); the importer also preferred that
/// unexpressable body over the op's real, expressable params. #2: OpenAPI status
/// ranges (4XX/5XX) silently projected to literal statuses. #3: 1xx-only ops
/// (websocket upgrades) lost their 101 and gained a fabricated 200.
/// </summary>
public sealed class CrossCorpusFindingsTests
{
    // ---- #1, emit side: no property surface → no params, loudly ----

    [Fact]
    public void Dictionary_Input_On_Get_Warns_RIV1020_And_Lowers_No_Query_Params()
    {
        var source = """
            using Rivet;
            using System.Collections.Generic;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define GetThing =
                    Define.Get<Dictionary<string, string>, ThingDto>("/dict-input-things/{thing_id}");
            }
            """;

        IReadOnlyList<TsEndpointDefinition> endpoints = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            endpoints = CompilationHelper.WalkContract(source).Endpoints
        );

        Assert.Contains("RIV1020", stderr);
        Assert.Contains("/dict-input-things/", stderr);

        var ep = Assert.Single(endpoints);
        // the route token survives untyped; no CLR member becomes a query param
        var param = Assert.Single(ep.Params);
        Assert.Equal("thing_id", param.Name);
        Assert.Equal(ParamSource.Route, param.Source);
        Assert.DoesNotContain(ep.Params, p => p.Source == ParamSource.Query);
    }

    [Fact]
    public void Collection_Input_On_Delete_Warns_RIV1020_And_Lowers_No_Query_Params()
    {
        var source = """
            using Rivet;
            using System.Collections.Generic;

            namespace Test;

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define PurgeThings =
                    Define.Delete("/list-input-things")
                        .Status(204)
                        .Accepts<List<string>>();
            }
            """;

        IReadOnlyList<TsEndpointDefinition> endpoints = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            endpoints = CompilationHelper.WalkContract(source).Endpoints
        );

        Assert.Contains("RIV1020", stderr);
        Assert.Contains("/list-input-things", stderr);
        Assert.Empty(Assert.Single(endpoints).Params);
    }

    [Fact]
    public void Record_Input_On_Get_Does_Not_Warn_RIV1020()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ListThingsInput(int Page);

            [RivetType]
            public sealed record ThingDto(string Name);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define ListThings =
                    Define.Get<ListThingsInput, ThingDto>("/record-input-things");
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() => CompilationHelper.WalkContract(source));

        // endpoint-unique needle: stderr capture is process-global under
        // parallel test classes (see ParamWireNamePinningTests)
        Assert.DoesNotContain("/record-input-things", stderr);
    }

    // ---- #1, import side: params and opaque body content remain independent ----

    [Fact]
    public void Bodyless_Op_With_Opaque_Body_Preserves_Params_And_Content()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "ItemDto": { "type": "object", "properties": { "id": { "type": "string" } } }
            """,
            paths: """
            "/api/items": {
                "get": {
                    "operationId": "listItems",
                    "parameters": [
                        {"name": "page_size", "in": "query", "required": false, "schema": {"type": "integer"}}
                    ],
                    "requestBody": {
                        "content": {"application/x-www-form-urlencoded": {"schema": {"type": "object", "additionalProperties": true}}}
                    },
                    "responses": {
                        "200": {
                            "description": "ok",
                            "content": {"application/json": {"schema": {"$ref": "#/components/schemas/ItemDto"}}}
                        }
                    }
                }
            }
            """,
            title: "API"
        );

        var result = CompilationHelper.Import(spec);
        var contract = CompilationHelper.FindFile(result, "DefaultContract.cs");

        Assert.Contains(".RequestContent<", contract);
        Assert.Contains("\"application/x-www-form-urlencoded\"", contract);
        Assert.Contains(".Parameter<long>(\"page_size\", \"query\", false", contract);
        Assert.DoesNotContain("reason=", contract);
    }

    [Fact]
    public void Delete_With_Opaque_Body_Preserves_Body_And_Path_Param()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Unused": { "type": "object", "properties": { "x": { "type": "string" } } }
            """,
            paths: """
            "/api/tunnels/{tunnel_id}": {
                "delete": {
                    "operationId": "deleteTunnel",
                    "parameters": [
                        {"name": "tunnel_id", "in": "path", "required": true, "schema": {"type": "string"}}
                    ],
                    "requestBody": {
                        "content": {"application/json": {"schema": {"type": "object", "additionalProperties": true}}}
                    },
                    "responses": { "204": { "description": "deleted" } }
                }
            }
            """,
            title: "API"
        );

        var contract = CompilationHelper.FindFile(
            CompilationHelper.Import(spec),
            "DefaultContract.cs"
        );

        Assert.Contains(".RequestContent<", contract);
        Assert.Contains("(\"application/json\"", contract);
        Assert.Contains(".Parameter<string>(\"tunnel_id\", \"path\", true", contract);
        Assert.DoesNotContain("reason=", contract);
    }

    [Fact]
    public void Post_With_Opaque_Body_Preserves_Body_And_Query_Param()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Unused": { "type": "object", "properties": { "x": { "type": "string" } } }
            """,
            paths: """
            "/api/ingest": {
                "post": {
                    "operationId": "ingestBlob",
                    "parameters": [
                        {"name": "tag", "in": "query", "required": false, "schema": {"type": "string"}}
                    ],
                    "requestBody": {
                        "required": true,
                        "content": {"application/json": {"schema": {"type": "object", "additionalProperties": true}}}
                    },
                    "responses": { "202": { "description": "accepted" } }
                }
            }
            """,
            title: "API"
        );

        var contract = CompilationHelper.FindFile(
            CompilationHelper.Import(spec),
            "DefaultContract.cs"
        );

        Assert.Contains(".RequestContent<", contract);
        Assert.Contains("(\"application/json\"", contract);
        Assert.Contains(".Parameter<string>(\"tag\", \"query\", false", contract);
        Assert.DoesNotContain("reason=", contract);
    }

    // ---- #2: status ranges remain exact ----

    [Fact]
    public void Error_Status_Range_Remains_Exact()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "ErrorDto": { "type": "object", "properties": { "message": { "type": "string" } } },
            "ItemDto": { "type": "object", "properties": { "id": { "type": "string" } } }
            """,
            paths: """
            "/api/items/{id}": {
                "get": {
                    "operationId": "getItem",
                    "parameters": [{"name": "id", "in": "path", "required": true, "schema": {"type": "string"}}],
                    "responses": {
                        "200": {
                            "description": "ok",
                            "content": {"application/json": {"schema": {"$ref": "#/components/schemas/ItemDto"}}}
                        },
                        "4xx": {
                            "description": "client error",
                            "content": {"application/json": {"schema": {"$ref": "#/components/schemas/ErrorDto"}}}
                        }
                    }
                }
            }
            """,
            title: "API"
        );

        var contract = CompilationHelper.FindFile(
            CompilationHelper.Import(spec),
            "DefaultContract.cs"
        );

        Assert.Contains(".Returns<ErrorDto>(\"4xx\"", contract);
        Assert.DoesNotContain("projected=400", contract);
    }

    // ---- #3: 1xx-only ops keep their informational status ----

    [Fact]
    public void Websocket_101_Only_Op_Declares_101_And_No_Fabricated_200()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Unused": { "type": "object", "properties": { "x": { "type": "string" } } }
            """,
            paths: """
            "/api/stream": {
                "get": {
                    "operationId": "openStream",
                    "responses": { "101": { "description": "switching protocols" } }
                }
            }
            """,
            title: "API"
        );

        var contract = CompilationHelper.FindFile(
            CompilationHelper.Import(spec),
            "DefaultContract.cs"
        );

        Assert.Contains(".Status(101)", contract);
        Assert.DoesNotContain(".Status(200)", contract);
        Assert.DoesNotContain(".Returns(101", contract); // promoted, not double-declared
    }

    [Fact]
    public void Informational_Status_Beside_A_2xx_Remains_Declared()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "ItemDto": { "type": "object", "properties": { "id": { "type": "string" } } }
            """,
            paths: """
            "/api/maybe-stream": {
                "get": {
                    "operationId": "maybeStream",
                    "responses": {
                        "101": { "description": "switching protocols" },
                        "200": {
                            "description": "ok",
                            "content": {"application/json": {"schema": {"$ref": "#/components/schemas/ItemDto"}}}
                        }
                    }
                }
            }
            """,
            title: "API"
        );

        var contract = CompilationHelper.FindFile(
            CompilationHelper.Import(spec),
            "DefaultContract.cs"
        );

        // 200 remains the runtime success; 101 remains an independent exact response.
        Assert.DoesNotContain(".Status(101)", contract);
        Assert.Contains(".Returns(101, \"switching protocols\")", contract);
        Assert.DoesNotContain("informational-status-dropped", contract);
    }
}
