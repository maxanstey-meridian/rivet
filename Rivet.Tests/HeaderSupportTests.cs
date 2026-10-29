using System.Text.Json;
using Rivet.Tool;
using Rivet.Tool.Analysis;
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
                public static readonly RouteDefinition<ListInput, string> ListPages =
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
                public static readonly RouteDefinition<ListInput, string> ListItems =
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
                [ProducesResponseType(typeof(void), 200)]
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
                public static readonly RouteDefinition<CreateItemRequest, string> CreateItem =
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
                public static readonly RouteDefinition<TaskDto, TaskDto> CreateTask =
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

    [Theory]
    [InlineData(".WithResponseHeader(\"ETag\").WithResponseHeader(\"etag\")", "200")]
    [InlineData(
        ".Returns(429).WithResponseHeader(429, \"Retry-After\").WithResponseHeader(429, \"retry-after\")",
        "429"
    )]
    public void WithResponseHeader_Duplicate_Name_On_One_Status_Is_Refused(
        string chain,
        string status
    )
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}"){{chain}};
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("RIV1109", exception.Message);
        Assert.Contains($"status {status}", exception.Message);
    }

    [Fact]
    public void WithResponseHeader_Same_Name_On_Different_Statuses_Is_Allowed()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .WithResponseHeader("ETag")
                        .Returns(429)
                        .WithResponseHeader(429, "etag");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        Assert.All(
            Assert.Single(endpoints).Responses,
            response => Assert.Single(response.Headers!)
        );
    }

    [Fact]
    public void WithResponseHeader_OnUndeclaredStatus_IsRefusedWithRIV1017()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .WithResponseHeader(404, "X-Lost-And-Found");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.StartsWith("error RIV1017:", exception.Message);
        Assert.Contains("'X-Lost-And-Found' for status 404", exception.Message);
        Assert.Contains(".Returns(...)", exception.Message);
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
                public static readonly RouteDefinition<ListInput, string> ListPages =
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

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Accept")]
    [InlineData("content-type")]
    public void Emitter_Refuses_Reserved_Header_Names_With_RIV2009(string header)
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            public sealed record ListInput(
                [property: RivetHeader("{{header}}")] string Reserved);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<ListInput, string> ListItems =
                    Define.Get<ListInput, string>("/api/items");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApi(source).Dispose()
        );

        Assert.Contains("error RIV2009:", exception.Message);
        Assert.Contains($"'{header}'", exception.Message);
    }

    // ---------------------------------------------------------------
    // OpenApiEmitter — responses[status].headers
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(".WithResponseHeader(\"Content-Type\", required: true)", "200")]
    [InlineData(".Returns(404).WithResponseHeader(404, \"content-type\")", "404")]
    [InlineData(
        ".StatusKey(\"2XX\").WithResponseHeaderKey<string>(\"2XX\", \"CONTENT-TYPE\")",
        "2XX"
    )]
    public void Emitter_Refuses_A_Response_Header_Named_Content_Type_With_RIV2009(
        string declaration,
        string status
    )
    {
        // G.11: OpenAPI says such a header "SHALL be ignored", so the spec would drop it
        // while the adapters demand it.
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<string> ListItems =
                    Define.Get<string>("/api/items"){{declaration}};
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApi(source).Dispose()
        );

        Assert.StartsWith("error RIV2009:", exception.Message);
        Assert.Contains($"on status {status} of endpoint 'items.listItems'", exception.Message);
        Assert.Contains(".ProducesContentType(...)", exception.Message);
    }

    [Fact]
    public void Contract_Json_Response_Header_Named_Content_Type_Fails_With_RIV2009()
    {
        var json = """
            { "types": [], "enums": [], "endpoints": [{
              "name": "get", "httpMethod": "GET", "routeTemplate": "/api/things", "params": [],
              "controllerName": "things",
              "responses": [{ "statusCode": 200, "headers": [
                { "name": "Content-Type", "type": { "kind": "primitive", "type": "string" } }
              ] }] }] }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApiFromJson(json)
        );

        Assert.StartsWith("error RIV2009:", exception.Message);
        Assert.Contains("response header 'Content-Type' on status 200", exception.Message);
    }

    [Theory]
    [InlineData("Accept")]
    [InlineData("Authorization")]
    public void Only_Content_Type_Is_Reserved_On_A_Response(string header)
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<string> ListItems =
                    Define.Get<string>("/api/items").WithResponseHeader("{{header}}");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);

        Assert.True(
            doc.RootElement.GetProperty("paths")
                .GetProperty("/api/items")
                .GetProperty("get")
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("headers")
                .TryGetProperty(header, out _)
        );
    }

    [Theory]
    [InlineData("ItemDto", "the schema 'ItemDto'")]
    [InlineData("string[]", "an array")]
    public void A_Response_Header_SchemaType_On_A_Non_Primitive_Fails_With_RIV1117(
        string type,
        string mapsTo
    )
    {
        // It only re-types a primitive leaf, so it used to vanish from the schema.
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<string> ListItems =
                    Define.Get<string>("/api/items")
                        .WithResponseHeader<{{type}}>("X-Item", schemaType: "string");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApi(source).Dispose()
        );

        Assert.StartsWith($"error {Diagnostics.SchemaTypeOnNonPrimitive}:", exception.Message);
        Assert.Contains("schemaType: \"string\" on response header 'X-Item'", exception.Message);
        Assert.Contains("endpoint 'listItems'", exception.Message);
        Assert.Contains(mapsTo, exception.Message);
        Assert.Contains("drop schemaType:", exception.Message);
    }

    [Fact]
    public void Imported_Content_Type_Response_Headers_Stay_Out_Of_The_Contract_And_Come_Back_Verbatim()
    {
        // G.11's import side: OpenAPI ignores them, so the C# never declares them (emit would
        // refuse it), and preserved-field provenance writes them back as the source had them.
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/markdown": {
                "post": {
                    "operationId": "render",
                    "responses": {
                        "200": {
                            "description": "OK",
                            "headers": {
                                "Content-Type": { "schema": { "type": "string" }, "example": "text/html" },
                                "X-Version": { "schema": { "type": "string" } }
                            }
                        }
                    }
                }
            },
            "/raw": {
                "get": {
                    "operationId": "raw",
                    "responses": {
                        "200": {
                            "description": "OK",
                            "headers": { "content-type": { "schema": { "type": "string" } } }
                        }
                    }
                }
            }
            """
        );

        var imported = CompilationHelper.Import(spec);
        var contractSource = CompilationHelper.FindFile(imported, "Contract.cs");
        Assert.DoesNotContain(
            "\"Content-Type\"",
            contractSource,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains("\"X-Version\"", contractSource);

        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        using var emitted = JsonDocument.Parse(
            OpenApiEmitter.Emit(
                endpoints,
                walker.Definitions,
                walker.Brands,
                walker.Enums,
                security: null,
                new OpenApiDocumentInfo(
                    Provenance: OpenApiProvenanceWalker.Walk(compilation, walker)
                )
            )
        );
        var paths = emitted.RootElement.GetProperty("paths");
        var markdown = paths
            .GetProperty("/markdown")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("headers");
        Assert.Equal(
            "text/html",
            markdown.GetProperty("Content-Type").GetProperty("example").GetString()
        );
        Assert.True(markdown.TryGetProperty("X-Version", out _));
        var raw = paths
            .GetProperty("/raw")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("headers");
        Assert.Equal(
            "string",
            raw.GetProperty("content-type").GetProperty("schema").GetProperty("type").GetString()
        );
    }

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
                public static readonly RouteDefinition<TaskDto, TaskDto> CreateTask =
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

    [Theory]
    // `examples` on a schema is a JSON array; anything else is no schema at all.
    [InlineData("schemaExamplesJson: \"{}\"", "must be a JSON array")]
    [InlineData("schemaExamplesJson: \"[1\"", "must be a JSON literal")]
    [InlineData("exampleJson: \"{\"", "must be a JSON literal")]
    [InlineData("examplesJson: \"nope\"", "must be a JSON literal")]
    // A header's `examples` is a map of Example objects by name.
    [InlineData("examplesJson: \"[1]\"", "must be a JSON object mapping names to Example objects")]
    public void Emitter_Refuses_Response_Header_Json_It_Cannot_Emit(
        string argument,
        string expected
    )
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .WithResponseHeader<int>("X-Rate-Limit", {{argument}});
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApi(source)
        );

        Assert.StartsWith("error RIV1123: ", exception.Message);
        Assert.Contains(expected, exception.Message);
        Assert.Contains("response header 'X-Rate-Limit' on endpoint '", exception.Message);
        Assert.Contains("getTask'", exception.Message);
    }

    [Theory]
    [InlineData("{", "must be a JSON object of parameter metadata")]
    [InlineData("{\\\"deprecated\\\":\\\"yes\\\"}", "must be a JSON object of parameter metadata")]
    [InlineData("{\\\"examples\\\":[1]}", "must be a JSON object mapping names to Example objects")]
    public void Walker_Refuses_Parameter_Metadata_It_Cannot_Read(
        string metadataJson,
        string expected
    )
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> ListTasks =
                    Define.Get<TaskDto>("/api/tasks")
                        .Parameter<string>("X-Trace", "header", false, metadataJson: "{{metadataJson}}");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApi(source)
        );

        Assert.StartsWith("error RIV1123: ", exception.Message);
        Assert.Contains(expected, exception.Message);
        Assert.Contains("'X-Trace' on endpoint '", exception.Message);
        Assert.Contains("listTasks'", exception.Message);
    }

    // --from reaches the emitter without the walker, so the shape rules live where both meet.
    [Theory]
    [InlineData("\"examples\": [1]", false)]
    [InlineData("\"examples\": { \"first\": 1 }", false)]
    [InlineData("\"examples\": [1]", true)]
    [InlineData("\"examples\": { \"first\": 1 }", true)]
    public void Contract_Json_Header_Param_Examples_Must_Be_A_Map_Of_Example_Objects(
        string examples,
        bool onResponseHeader
    )
    {
        var header =
            $$"""{ "name": "X-Trace", "type": { "kind": "primitive", "type": "string" }, {{examples}} }""";
        var param =
            $$"""{ "name": "X-Trace", "type": { "kind": "primitive", "type": "string" }, "source": "header", {{examples}} }""";
        var json = $$"""
            {
              "types": [],
              "enums": [],
              "endpoints": [{
                "name": "list",
                "httpMethod": "GET",
                "routeTemplate": "/api/items",
                "params": [{{(onResponseHeader ? "" : param)}}],
                "controllerName": "items",
                "responses": [{ "statusCode": 204{{(
                onResponseHeader ? $", \"headers\": [{header}]" : ""
            )}} }]
              }]
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApiFromJson(json)
        );

        Assert.Contains(
            "must be a JSON object mapping names to Example objects",
            exception.Message
        );
        Assert.Contains("'X-Trace' on endpoint 'items.list'", exception.Message);
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
