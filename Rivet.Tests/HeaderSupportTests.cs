using System.Text.Json;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// P2 wave 5 — headers as contract concepts. Request headers ([RivetHeader] on contract
/// input records, [FromHeader] in annotation mode) become ParamSource.Header params and
/// emit as in: header parameters; response headers (.WithResponseHeader) emit under
/// responses[status].headers. Everything is SPEC-ONLY at runtime: Rivet never binds,
/// sets or validates headers.
/// </summary>
public sealed class HeaderSupportTests
{
    // ---------------------------------------------------------------
    // ContractWalker — [RivetHeader] classification
    // ---------------------------------------------------------------

    [Fact]
    public void RivetHeader_Named_ClassifiesAsHeaderParam_WithOriginalCasing()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record ListInput(
                [property: RivetHeader("Notion-Version")] string Version,
                string? Filter);

            [RivetContract]
            public static class PagesContract
            {
                public static readonly Define ListPages =
                    Define.Get<ListInput, string>("/api/pages");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var ep = Assert.Single(endpoints);
        var header = Assert.Single(ep.Params, p => p.Source == ParamSource.Header);
        // Original casing survives — the attribute carries it
        Assert.Equal("Notion-Version", header.Name);
        Assert.False(header.IsOptional);
        // The remaining property is still a query param
        Assert.Single(ep.Params, p => p.Name == "filter" && p.Source == ParamSource.Query);
    }

    [Fact]
    public void RivetHeader_Defaulted_UsesThePropertyName()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record ListInput([property: RivetHeader] string XTraceId);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define ListItems =
                    Define.Get<ListInput, string>("/api/items");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var ep = Assert.Single(endpoints);
        var header = Assert.Single(ep.Params, p => p.Source == ParamSource.Header);
        // No name argument → the PascalCase property name IS the header name
        Assert.Equal("XTraceId", header.Name);
    }

    [Fact]
    public void Header_Route_Query_And_Body_Coexist()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record ItemDto(string Name);

            [ApiController]
            [Route("api/items")]
            public class ItemsController : ControllerBase
            {
                [RivetEndpoint]
                [HttpPut("{id}")]
                public IActionResult Update(
                    string id,
                    [FromQuery] bool dryRun,
                    [FromHeader(Name = "If-Match")] string etag,
                    [FromBody] ItemDto body)
                    => Ok();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var ep = Assert.Single(endpoints);
        Assert.Equal(4, ep.Params.Count);
        Assert.Single(ep.Params, p => p.Name == "id" && p.Source == ParamSource.Route);
        Assert.Single(ep.Params, p => p.Name == "dryRun" && p.Source == ParamSource.Query);
        Assert.Single(ep.Params, p => p.Name == "If-Match" && p.Source == ParamSource.Header);
        Assert.Single(ep.Params, p => p.Name == "body" && p.Source == ParamSource.Body);
    }

    [Fact]
    public void Header_Property_On_Body_Record_Leaves_The_Json_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(
                [property: RivetHeader("X-Request-Id")] string RequestId,
                string Name);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define CreateItem =
                    Define.Post<CreateItemRequest, string>("/api/items");
            }
            """;

        var (endpoints, walker) = CompilationHelper.WalkContract(source);

        var ep = Assert.Single(endpoints);
        Assert.Single(ep.Params, p => p.Name == "X-Request-Id" && p.Source == ParamSource.Header);
        Assert.Single(ep.Params, p => p.Name == "body" && p.Source == ParamSource.Body);

        // The header property never enters the record's JSON schema
        var definition = walker.Definitions["CreateItemRequest"];
        var prop = Assert.Single(definition.Properties);
        Assert.Equal("name", prop.Name);
    }

    // ---------------------------------------------------------------
    // ContractWalker — .WithResponseHeader(...)
    // ---------------------------------------------------------------

    [Fact]
    public void WithResponseHeader_AttachesToExplicitAndSuccessStatuses()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define CreateTask =
                    Define.Post<TaskDto, TaskDto>("/api/tasks")
                        .WithResponseHeader("Location", "Where it lives", required: true)
                        .Returns(429)
                        .WithResponseHeader(429, "Retry-After");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var ep = Assert.Single(endpoints);

        // Convenience overload targets the success status (POST default 201)
        var created = Assert.Single(ep.Responses, r => r.StatusCode == 201);
        var location = Assert.Single(created.Headers!);
        Assert.Equal("Location", location.Name);
        Assert.Equal("Where it lives", location.Description);
        Assert.True(location.Required);

        var rateLimited = Assert.Single(ep.Responses, r => r.StatusCode == 429);
        var retryAfter = Assert.Single(rateLimited.Headers!);
        Assert.Equal("Retry-After", retryAfter.Name);
        Assert.Null(retryAfter.Description);
        Assert.False(retryAfter.Required, "required must be opt-in only");
    }

    [Fact]
    public void WithResponseHeader_OnUndeclaredStatus_IsIgnoredWithRIV1017()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .WithResponseHeader(404, "X-Lost-And-Found");
            }
            """;

        IReadOnlyList<TsEndpointDefinition> endpoints = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (endpoints, _) = CompilationHelper.WalkContract(source);
        });

        var ep = Assert.Single(endpoints);
        Assert.DoesNotContain(ep.Responses, r => r.Headers is not null);
        Assert.Contains("warning RIV1017:", stderr);
        Assert.Contains("X-Lost-And-Found", stderr);
    }

    // ---------------------------------------------------------------
    // Runtime builder — declaration-time guards only (spec-only at runtime)
    // ---------------------------------------------------------------

    [Fact]
    public void Builder_Rejects_DuplicateHeaderPerStatus()
    {
        var definition = Define.Get<string>("/api/things").WithResponseHeader("ETag");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            definition.WithResponseHeader("etag")
        );
        Assert.Contains("already declared", ex.Message);
    }

    [Fact]
    public void Builder_Exposes_DeclaredResponseHeaders()
    {
        var definition = Define
            .Get<string>("/api/things")
            .WithResponseHeader("ETag")
            .WithResponseHeader(304, "Cache-Control", "Caching policy", required: true);

        Assert.NotNull(definition.ResponseHeaders);
        Assert.Equal(2, definition.ResponseHeaders!.Count);
        Assert.Equal(
            new RouteResponseHeader(null, "ETag", null, false, HeaderType: typeof(string)),
            definition.ResponseHeaders[0]
        );
        Assert.Equal(
            new RouteResponseHeader(
                304,
                "Cache-Control",
                "Caching policy",
                true,
                HeaderType: typeof(string)
            ),
            definition.ResponseHeaders[1]
        );
    }

    // ---------------------------------------------------------------
    // OpenApiEmitter — in: header parameters
    // ---------------------------------------------------------------

    [Fact]
    public void Emitter_Writes_InHeader_Parameter()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record ListInput(
                [property: RivetHeader("Notion-Version")] string Version,
                string? Cursor);

            [RivetContract]
            public static class PagesContract
            {
                public static readonly Define ListPages =
                    Define.Get<ListInput, string>("/api/pages");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var parameters = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/pages")
            .GetProperty("get")
            .GetProperty("parameters");

        var headerParam = parameters
            .EnumerateArray()
            .Single(p => p.GetProperty("in").GetString() == "header");
        Assert.Equal("Notion-Version", headerParam.GetProperty("name").GetString());
        Assert.True(headerParam.GetProperty("required").GetBoolean());
        Assert.Equal("string", headerParam.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void Emitter_Skips_Reserved_Header_Names_With_RIV2009()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record ListInput(
                [property: RivetHeader("Authorization")] string Auth,
                [property: RivetHeader("Accept")] string Accept,
                [property: RivetHeader("Content-Type")] string ContentType,
                [property: RivetHeader("X-Fine")] string Fine);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define ListItems =
                    Define.Get<ListInput, string>("/api/items");
            }
            """;

        JsonDocument doc = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            doc = CompilationHelper.EmitOpenApi(source);
        });

        using (doc)
        {
            var parameters = doc
                .RootElement.GetProperty("paths")
                .GetProperty("/api/items")
                .GetProperty("get")
                .GetProperty("parameters");

            var headerNames = parameters
                .EnumerateArray()
                .Where(p => p.GetProperty("in").GetString() == "header")
                .Select(p => p.GetProperty("name").GetString())
                .ToList();

            // Per OpenAPI rules the three reserved names are not legal header params
            Assert.Equal(["X-Fine"], headerNames);
        }

        Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Matches(stderr, "warning RIV2009:").Count
        );
        Assert.Contains("'Authorization'", stderr);
        Assert.Contains("'Accept'", stderr);
        Assert.Contains("'Content-Type'", stderr);
    }

    // ---------------------------------------------------------------
    // OpenApiEmitter — responses[status].headers
    // ---------------------------------------------------------------

    [Fact]
    public void Emitter_Writes_Response_Headers_Shape()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define CreateTask =
                    Define.Post<TaskDto, TaskDto>("/api/tasks")
                        .WithResponseHeader("Location", "URL of the created task", required: true)
                        .WithResponseHeader(201, "ETag");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var headers = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/tasks")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201")
            .GetProperty("headers");

        var location = headers.GetProperty("Location");
        Assert.Equal("URL of the created task", location.GetProperty("description").GetString());
        Assert.True(location.GetProperty("required").GetBoolean());
        Assert.Equal("string", location.GetProperty("schema").GetProperty("type").GetString());

        // required only on explicit opt-in; description only when present
        var etag = headers.GetProperty("ETag");
        Assert.False(etag.TryGetProperty("required", out _));
        Assert.False(etag.TryGetProperty("description", out _));
        Assert.Equal("string", etag.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void Imported_Response_Header_Leaf_Metadata_Survives_Generated_CSharp_And_Emitters()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/items": {
                "get": {
                    "operationId": "listItems",
                    "responses": {
                        "200": {
                            "description": "OK",
                            "headers": {
                                "X-Rate-Limit": {
                                    "description": "Requests remaining",
                                    "required": true,
                                    "schema": {
                                        "type": ["integer", "null"],
                                        "format": "int32"
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
        var contractSource = CompilationHelper.FindFile(imported, "Contract.cs");
        Assert.Contains(".WithResponseHeader<int?>", contractSource);

        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var endpoint = Assert.Single(endpoints);
        var response = Assert.Single(endpoint.Responses);
        var header = Assert.Single(response.Headers!);
        Assert.Equal("X-Rate-Limit", header.Name);
        Assert.Equal("Requests remaining", header.Description);
        Assert.True(header.Required);
        var nullable = Assert.IsType<TsType.Nullable>(header.Type);
        var primitive = Assert.IsType<TsType.Primitive>(nullable.Inner);
        Assert.Equal("integer", primitive.Name);
        Assert.Equal("int32", primitive.Format);

        using var contract = JsonDocument.Parse(
            ContractEmitter.Emit(
                walker.Definitions.ToDictionary(),
                walker.Enums.ToDictionary(),
                endpoints
            )
        );
        var contractHeader = contract
            .RootElement.GetProperty("endpoints")[0]
            .GetProperty("responses")[0]
            .GetProperty("headers")[0];
        Assert.Equal(
            "nullable",
            contractHeader.GetProperty("type").GetProperty("kind").GetString()
        );

        using var emitted = JsonDocument.Parse(
            OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null)
        );
        var emittedHeader = emitted
            .RootElement.GetProperty("paths")
            .GetProperty("/items")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("headers")
            .GetProperty("X-Rate-Limit");
        Assert.Equal("Requests remaining", emittedHeader.GetProperty("description").GetString());
        Assert.True(emittedHeader.GetProperty("required").GetBoolean());
        var schema = emittedHeader.GetProperty("schema");
        Assert.Equal(
            ["integer", "null"],
            schema.GetProperty("type").EnumerateArray().Select(value => value.GetString())
        );
        Assert.Equal("int32", schema.GetProperty("format").GetString());
    }
}
