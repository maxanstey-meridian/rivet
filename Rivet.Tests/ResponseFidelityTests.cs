using System.Text.Json;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Response-fidelity regressions: explicit response status/media-type/body declarations
/// agree with the emitted contract. Covers the text/plain media type of plain string
/// actions, [Produces] precedence, the RIV1102 body-forbidden-status guard through C#
/// contract AND contract-JSON inputs, the emitter's defense-in-depth re-check, ordinary
/// byte[] staying JSON base64 without an explicit file declaration, and refusal of
/// ambiguous result containers (no invented success responses).
/// </summary>
public sealed class ResponseFidelityTests
{
    [Fact]
    public void Annotation_Ambiguous_Result_Container_Refuses_With_RIV1006()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpPost("items")]
                public Task<IActionResult> Post(CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        // Bare IActionResult/void/Task actions acquire no invented success response.
        Assert.Contains("RIV1006", exception.Message);
        Assert.Contains("ItemsController.Post", exception.Message);
    }

    [Fact]
    public void Annotation_Void_Action_Without_Response_Declaration_Refuses_With_RIV1006()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class TasksController : ControllerBase
            {
                [HttpDelete("tasks/{id}")]
                public Task Delete(int id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        // void/non-generic Task stays unresolved — no synthetic 204.
        Assert.Contains("RIV1006", exception.Message);
        Assert.Contains("TasksController.Delete", exception.Message);
    }

    [Fact]
    public void Annotation_Variable_Status_Typed_Result_Refuses_With_RIV1006()
    {
        var source = """
            using System;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ProblemsController : ControllerBase
            {
                [HttpPost("problems")]
                public ProblemHttpResult Post() => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        // ProblemHttpResult carries its status at runtime — no invented 200/500.
        // The Results<ProblemHttpResult, ...> branch form is refused by the same
        // unmapped-branch refusal in CollectTypedResultMappings.
        Assert.Contains("RIV1006", exception.Message);
        Assert.Contains("ProblemsController.Post", exception.Message);
    }

    [Fact]
    public void BuiltIn_NonFixed_IResult_Refuses_With_RIV1006()
    {
        // acceptance:direct-ambiguous-result-refuses — a built-in result whose
        // concrete type does not statically carry its status (ContentHttpResult
        // writes whatever status the runtime set) refuses rather than becoming a
        // 200 payload contract. Detection is the IResult interface boundary, not a
        // concrete-class list (acceptance:all-aspnet-result-containers-are-ambiguous).
        var source = """
            using System;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ContentController : ControllerBase
            {
                [HttpGet("content")]
                public ContentHttpResult Get() => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        Assert.Contains("RIV1006", exception.Message);
        Assert.Contains("ContentController.Get", exception.Message);
    }

    [Fact]
    public void Custom_IResult_Implementation_Refuses_With_RIV1006()
    {
        // acceptance:direct-ambiguous-result-refuses — a user-defined IResult
        // implementation is status/content selecting through the declared interface
        // relationship and refuses without sufficient explicit response declarations.
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed class TeapotResult : IResult
            {
                public Task ExecuteAsync(HttpContext httpContext)
                    => throw new NotImplementedException();
            }

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class CustomController : ControllerBase
            {
                [HttpGet("teapot")]
                public TeapotResult Get() => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        Assert.Contains("RIV1006", exception.Message);
        Assert.Contains("CustomController.Get", exception.Message);
    }

    [Fact]
    public void Results_Branch_Cannot_Hide_Behind_ProducesResponseType()
    {
        // acceptance:results-branches-cannot-hide-behind-attributes — one explicit
        // response attribute must not suppress validation of the declared Results<>
        // branches: ProblemHttpResult is an unmapped variable-status branch and
        // refuses with the existing unmapped-result diagnostic.
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FooDto(string Id);

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ResultsController : ControllerBase
            {
                [HttpGet("items/{id}")]
                [ProducesResponseType(typeof(FooDto), 200)]
                public Results<Ok<FooDto>, ProblemHttpResult> Get(string id)
                    => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        Assert.Contains("RIV1006", exception.Message);
        // The validate-and-merge path passes the method name as the warn context, so
        // the diagnostic names the unmapped branch itself rather than Controller.Method.
        Assert.Contains("ProblemHttpResult", exception.Message);
    }

    [Fact]
    public void Results_AllFixed_Branches_Behind_Attributes_Keep_Mapping()
    {
        // acceptance:fixed-typed-results-still-work — an all-fixed Results<> return
        // behind response attributes stays valid: the already-declared 200 from the
        // attribute is not duplicated and the fixed NotFound branch merges into 404
        // (planner-constraint:results-validation-merges-not-appends).
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FooDto(string Id);

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class FixedResultsController : ControllerBase
            {
                [HttpGet("items/{id}")]
                [ProducesResponseType(typeof(FooDto), 200)]
                public Results<Ok<FooDto>, NotFound> Get(string id)
                    => throw new NotImplementedException();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        Assert.Equal([200, 404], ep.Responses.Select(r => r.StatusCode).ToList());
        var ok = Assert.Single(ep.Responses, r => r.StatusCode == 200);
        Assert.True(ok.DataType is TsType.TypeRef { Name: "FooDto" });
        Assert.Null(Assert.Single(ep.Responses, r => r.StatusCode == 404).DataType);
    }

    [Fact]
    public void Conflicting_Payload_Types_Behind_Attributes_Refuse_With_RIV1107()
    {
        // acceptance:declared-response-conflicts-refuse — a mapped Results<> branch
        // landing on an already-declared status must agree with the attribute on the
        // payload; a different payload type is two authorities answering the same
        // question differently and refuses instead of silently attribute-wins.
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FooDto(string Id);

            [RivetType]
            public sealed record BarDto(string Id);

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ClashResultsController : ControllerBase
            {
                [HttpGet("items/{id}")]
                [ProducesResponseType(typeof(FooDto), 200)]
                public Results<Ok<FooDto>, Ok<BarDto>> Get(string id)
                    => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        Assert.Contains("RIV1107", exception.Message);
        Assert.Contains("ClashResultsController.Get", exception.Message);
        Assert.Contains("FooDto", exception.Message);
        Assert.Contains("BarDto", exception.Message);
    }

    [Fact]
    public void Body_Attribute_Vs_Bodyless_Branch_Refuses_With_RIV1107()
    {
        // acceptance:declared-response-conflicts-refuse — a typed attribute on a
        // status where the mapped branch carries no body (NotFound) is a
        // body-vs-bodyless disagreement: the spec would lie about body presence.
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FooDto(string Id);

            [RivetType]
            public sealed record ErrorDto(string Message);

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class PresenceResultsController : ControllerBase
            {
                [HttpGet("items/{id}")]
                [ProducesResponseType(typeof(ErrorDto), 404)]
                public Results<Ok<FooDto>, NotFound> Get(string id)
                    => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        Assert.Contains("RIV1107", exception.Message);
        Assert.Contains("PresenceResultsController.Get", exception.Message);
    }

    [Fact]
    public void Bodyless_Attribute_Vs_Body_Bearing_Branch_Refuses_With_RIV1107()
    {
        // acceptance:declared-response-conflicts-refuse — the mirror case: a
        // bodyless attribute declaration against a mapped branch that carries a
        // payload is the same lie from the other side.
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FooDto(string Id);

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class MirrorResultsController : ControllerBase
            {
                [HttpGet("items/{id}")]
                [ProducesResponseType(200)]
                public Results<Ok<FooDto>, NotFound> Get(string id)
                    => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        Assert.Contains("RIV1107", exception.Message);
        Assert.Contains("MirrorResultsController.Get", exception.Message);
    }

    [Fact]
    public void Annotation_Explicit_ProducesResponseType_Wins_Over_Defaults()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpPost("items")]
                [ProducesResponseType(typeof(ItemDto), StatusCodes.Status201Created)]
                public Task<IActionResult> Post(CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        // Explicit metadata wins — the synthesized default never fires.
        var response = Assert.Single(ep.Responses);
        Assert.Equal(201, response.StatusCode);
    }

    [Fact]
    public void Annotation_Plain_String_Action_Carries_Text_Plain_Media_Type()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class TextController : ControllerBase
            {
                [HttpPost("echo")]
                public Task<string> Echo([FromBody] string message, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        // MVC's string formatter writes plain string actions as text/plain.
        Assert.Equal("text/plain", ep.ResponseContentTypeOverride);
        var response = Assert.Single(ep.Responses);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("string", Assert.IsType<TsType.Primitive>(response.DataType).Name);
    }

    [Fact]
    public void Annotation_Explicit_Produces_Wins_Over_Formatter_Defaults()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetClient]
            [ApiController]
            [Produces("application/xml")]
            [Route("api")]
            public sealed class TextController : ControllerBase
            {
                [HttpPost("echo")]
                [Produces("text/markdown")]
                public Task<string> Echo([FromBody] string message, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        // The action-level [Produces] wins over both the controller-level one and
        // the text/plain formatter default — no invented media type beneath it.
        Assert.Equal("text/markdown", ep.ResponseContentTypeOverride);
    }

    [Fact]
    public void Contract_First_Post_Keeps_201_Default()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define CreateItem =
                    Define.Post<ItemDto>("/api/items");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        // Contract-first semantics authored at source: Define.Post → 201.
        var response = Assert.Single(ep.Responses);
        Assert.Equal(201, response.StatusCode);
    }

    // ════════════════ RIV1102 body-forbidden-status guard ════════════════

    [Theory]
    [InlineData(101)]
    [InlineData(204)]
    [InlineData(205)]
    [InlineData(304)]
    public void Csharp_Contract_Body_Bearing_Example_On_Forbidden_Status_Fails(int status)
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition Route =
                    Define.Get("/api/thing")
                        .Status({{status}})
                        .ResponseExampleJson({{status}}, "{\"message\":\"body\"}");
            }
            """;

        var exception = Assert.Throws<ContractAnalysisException>(() =>
            CompilationHelper.EmitOpenApi(source)
        );

        Assert.Contains("RIV1102", exception.Message);
        // The parse guard names the endpoint by its contract-first camelCase field name.
        Assert.Contains("'route'", exception.Message);
        Assert.Contains(status.ToString(), exception.Message);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(204)]
    [InlineData(205)]
    [InlineData(304)]
    public void Contract_Json_Body_Bearing_Example_On_Forbidden_Status_Fails(int status)
    {
        var contractJson = $$"""
            {
              "types": [],
              "enums": [],
              "endpoints": [
                {
                  "name": "deleteSession",
                  "httpMethod": "DELETE",
                  "routeTemplate": "/api/auth/session",
                  "controllerName": "auth",
                  "responses": [
                    {
                      "statusCode": {{status}},
                      "examples": [
                        { "mediaType": "application/json", "name": "deleted", "json": "{\"message\":\"deleted\"}" }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        // The parse-side guard lives on the shared normalization path every
        // frontend passes through — contract-JSON inputs included.
        var exception = Assert.Throws<ContractAnalysisException>(() =>
            CompilationHelper.EmitOpenApiFromJson(contractJson)
        );

        Assert.Contains("RIV1102", exception.Message);
        Assert.Contains("deleteSession", exception.Message);
        Assert.Contains(status.ToString(), exception.Message);
    }

    [Fact]
    public void Emission_Pipeline_Guard_Rejects_Forbidden_Content_Before_Output()
    {
        // Direct IR feed through the public Emit path: the parse-side guard inside
        // EmitWithSecurityMetadata's NormalizeIrAndEnsureResponse runs on every
        // frontend, so the reachable abort is ContractAnalysisException — the
        // BuildResponses re-check stays as disclosed defense in depth (it cannot be
        // reached through any public emission path without first passing the parse
        // guard, which sees the same authored content).
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "deleteSession",
                "DELETE",
                "/api/auth/session",
                [],
                null,
                "auth",
                [
                    new TsResponseType(
                        204,
                        null,
                        Examples:
                        [
                            new TsEndpointExample(
                                "application/json",
                                "deleted",
                                Json: "{\"message\":\"deleted\"}"
                            ),
                        ]
                    ),
                ]
            ),
        };

        var exception = Assert.Throws<ContractAnalysisException>(() =>
            OpenApiEmitter.Emit(
                endpoints,
                new Dictionary<string, TsTypeDefinition>(),
                new Dictionary<string, TsType.Brand>(),
                new Dictionary<string, TsType>(),
                null
            )
        );

        Assert.Contains("RIV1102", exception.Message);
        // The parse guard receives bare endpoint.Name and names the endpoint
        // 'deleteSession' (no controller prefix) — the emitter's defense-in-depth
        // re-check names 'auth.deleteSession' but is dominated by this guard.
        Assert.Contains("'deleteSession'", exception.Message);
        Assert.Contains("204", exception.Message);
    }

    [Fact]
    public void Ordinary_204_Remains_Bodyless_And_Emits_Successfully()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition DeleteSession =
                    Define.Delete("/api/auth/session");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var response = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/auth/session")
            .GetProperty("delete")
            .GetProperty("responses")
            .GetProperty("204");

        Assert.Equal("No Content", response.GetProperty("description").GetString());
        Assert.False(response.TryGetProperty("content", out _));
    }

    [Fact]
    public void Void_Documented_Annotation_204_Emits_Bodyless()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class TasksController : ControllerBase
            {
                [HttpDelete("tasks/{id}")]
                [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
                public Task<IActionResult> Delete(int id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var response = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/tasks/{id}")
            .GetProperty("delete")
            .GetProperty("responses")
            .GetProperty("204");

        // typeof(void) as a declared response type means no body — not untyped JSON.
        Assert.False(response.TryGetProperty("content", out _));
    }

    [Fact]
    public void Valid_Untyped_200_And_Typed_201_Examples_Keep_Working()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateOrderRequest(string CustomerId);

            [RivetType]
            public sealed record OrderDto(string Id);

            [RivetContract]
            public static class OrdersContract
            {
                public static readonly Define CreateOrder =
                    Define.Post<CreateOrderRequest, OrderDto>("/api/orders")
                        .ResponseExampleJson(201, "{\"id\":\"ord_123\"}", name: "created");

                public static readonly RouteDefinition Ping =
                    Define.Get("/api/ping")
                        .ResponseExampleJson(200, "\"pong\"", name: "pong");
            }
            """;

        var (endpoints, walker) = CompilationHelper.WalkContract(source);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        var created = paths
            .GetProperty("/api/orders")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples")
            .GetProperty("created");
        Assert.Equal("ord_123", created.GetProperty("value").GetProperty("id").GetString());

        var pong = paths
            .GetProperty("/api/ping")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("examples")
            .GetProperty("pong");
        Assert.Equal("\"pong\"", pong.GetProperty("value").GetRawText());
    }

    [Fact]
    public void Valid_No_Body_101_And_304_Keep_Working()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class StreamContract
            {
                public static readonly Define OpenStream =
                    Define.Get("/api/stream").Status(101);

                public static readonly RouteDefinition NotModified =
                    Define.Get("/api/resource").Status(304);
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var paths = doc.RootElement.GetProperty("paths");

        // Valid bodyless statuses survive with no fabricated content.
        Assert.False(
            paths
                .GetProperty("/api/stream")
                .GetProperty("get")
                .GetProperty("responses")
                .GetProperty("101")
                .TryGetProperty("content", out _)
        );
        Assert.False(
            paths
                .GetProperty("/api/resource")
                .GetProperty("get")
                .GetProperty("responses")
                .GetProperty("304")
                .TryGetProperty("content", out _)
        );
    }

    // ════════════════ Ordinary byte[] stays JSON base64 ════════════════

    [Fact]
    public void Byte_Array_Response_Schema_Agrees_With_Runtime_Base64_Wire()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class BlobContract
            {
                public static readonly Define GetBlob =
                    Define.Get<byte[]>("/api/blobs/{id}");
            }
            """;

        var (endpoints, walker) = CompilationHelper.WalkContract(source);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        using var doc = JsonDocument.Parse(json);
        var content = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/blobs/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content");

        // JSON media with the base64 string schema — matching the runtime's actual
        // Success(byte[]) wire shape (STJ serializes byte[] as a base64 JSON string).
        var mediaType = content.GetProperty("application/json");
        var schema = mediaType.GetProperty("schema");
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal("base64", schema.GetProperty("contentEncoding").GetString());
        Assert.False(content.TryGetProperty("application/octet-stream", out _));
    }

    [Fact]
    public void Explicit_File_Declarations_Keep_Raw_Binary_Emission()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition Download =
                    Define.Get("/api/files/{id}")
                        .ProducesFile("image/png");

                public static readonly FileRouteDefinition Raw =
                    Define.File("/api/raw/{id}");
            }
            """;

        var (endpoints, walker) = CompilationHelper.WalkContract(source);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        var png = paths
            .GetProperty("/api/files/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content");
        Assert.True(png.TryGetProperty("image/png", out var pngMedia));
        Assert.Equal("binary", pngMedia.GetProperty("schema").GetProperty("format").GetString());

        var raw = paths
            .GetProperty("/api/raw/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content");
        Assert.True(raw.TryGetProperty("application/octet-stream", out var rawMedia));
        Assert.Equal("binary", rawMedia.GetProperty("schema").GetProperty("format").GetString());
    }
}
