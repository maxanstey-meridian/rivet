using Rivet.Tool;

namespace Rivet.Tests;

/// <summary>
/// FABLE_ROUNDTRIP cross-corpus findings (cloudflare/notion, 2026-06-12) — the
/// classes the github corpus never exercised. Response-set fidelity now lives in
/// <see cref="ResponseSetFidelityTests"/>. #1: a dictionary/collection/
/// scalar input on a bodyless method enumerated its CLR members (Count, Keys,
/// Comparer, Capacity, …) into the emitted spec as invented query params (118
/// cloudflare DELETE ops + 2 notion GET ops); the importer also preferred that
/// unexpressable body over the op's real, expressable params. #3: 1xx-only ops
/// (websocket upgrades) lost their 101 and gained a fabricated 200.
/// </summary>
public sealed class CrossCorpusFindingsTests
{
    // ---- #1, emit side: no property surface → refused, never dropped ----

    [Theory]
    [InlineData(
        "RouteDefinition<Dictionary<string, string>, ThingDto>",
        "Define.Get<Dictionary<string, string>, ThingDto>(\"/dict-input-things/{thing_id}\")",
        "GET /dict-input-things/{thing_id}"
    )]
    [InlineData(
        "InputRouteDefinition<List<string>>",
        "Define.Delete(\"/list-input-things\").Status(204).Accepts<List<string>>()",
        "DELETE /list-input-things"
    )]
    [InlineData(
        "RouteDefinition<int, ThingDto>",
        "Define.Get<int, ThingDto>(\"/scalar-input-things\")",
        "GET /scalar-input-things"
    )]
    [InlineData(
        "RouteDefinition<string, ThingDto>",
        "Define.Post<string, ThingDto>(\"/binary-scalar-things\").AcceptsBinary()",
        "POST /binary-scalar-things"
    )]
    public void Unlowerable_Input_Without_Json_Body_Is_Refused_With_RIV1020(
        string fieldType,
        string definition,
        string operation
    )
    {
        var source = $$"""
            using Rivet;
            using System.Collections.Generic;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly {{fieldType}} Thing = {{definition}};
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.StartsWith("error RIV1020:", exception.Message);
        Assert.Contains(operation, exception.Message);
        Assert.Contains("record with one property per route or query parameter", exception.Message);
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
                public static readonly RouteDefinition<ListThingsInput, ThingDto> ListThings =
                    Define.Get<ListThingsInput, ThingDto>("/record-input-things");
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() => CompilationHelper.WalkContract(source));

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

    // ---- #3: 1xx-only ops keep their informational status ----

    [Fact]
    public void Websocket_101_Only_Op_Declares_101_And_No_Fabricated_200()
    {
        var spec = CompilationHelper.BuildSpec(
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
}
