using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ControllerEndpointTests
{
    private static IReadOnlyList<TsEndpointDefinition> WalkEndpoints(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        return CompilationHelper.WalkEndpoints(compilation, discovered, walker);
    }

    private static string GenerateClient(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        // Emit all controller groups and concatenate for assertion convenience
        return string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
    }

    [Fact]
    public void Controller_ProducesResponseType_ExtractsReturnType()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(ItemDto), 200)]
                public Task<IActionResult> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        Assert.Contains("export function get(", client);
        Assert.Contains("Promise<ItemDto>", client);
        Assert.Contains("\"GET\", `/api/items/${encodeURIComponent(String(id))}`", client);
    }

    [Fact]
    public void Controller_CombinesRoutes()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record StatusDto(Guid Id, string Label);

            [Route("api/case-statuses")]
            public sealed class CaseStatusesController
            {
                [RivetEndpoint]
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(StatusDto), 200)]
                public Task<IActionResult> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(StatusDto), 201)]
                public Task<IActionResult> Create(
                    [FromBody] StatusDto body,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        // GET combines route + method segment, strips :guid constraint
        Assert.Contains("""`/api/case-statuses/${encodeURIComponent(String(id))}`""", client);
        // POST uses controller route only
        Assert.Contains("""`/api/case-statuses`""", client);
    }

    [Fact]
    public void Controller_StripsRouteConstraints()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Value);

            [Route("api/things")]
            public sealed class ThingsController
            {
                [RivetEndpoint]
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(ThingDto), 200)]
                public Task<IActionResult> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        // {id:guid} should become ${encodeURIComponent(String(id))}, not ${id:guid}
        Assert.Contains("${encodeURIComponent(String(id))}", client);
        Assert.DoesNotContain(":guid", client);
    }

    [Fact]
    public void Controller_AbsoluteMethodRoute_IgnoresControllerRoute()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record HealthDto(string Status);

            [Route("api/things")]
            public sealed class ThingsController
            {
                [RivetEndpoint]
                [HttpGet("/api/health")]
                [ProducesResponseType(typeof(HealthDto), 200)]
                public Task<IActionResult> Health(CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        Assert.Contains("""`/api/health`""", client);
        Assert.DoesNotContain("things", client);
    }

    [Fact]
    public void Controller_NoProducesResponseType_FallsBackToMethodReturn()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<ItemDto> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        // Falls back to Task<ItemDto> return type
        Assert.Contains("Promise<ItemDto>", client);
    }

    [Fact]
    public void Controller_VoidEndpoint_NoReturn()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpDelete("{id:guid}")]
                [ProducesResponseType(200)]
                public Task<IActionResult> Delete(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        Assert.Contains("Promise<void>", client);
    }

    [Fact]
    public void Controller_WithBody_ExtractsParams()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(string Name);
            [RivetType]
            public sealed record CreateItemResponse(Guid Id);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(CreateItemResponse), 201)]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        Assert.Contains("request: CreateItemRequest", client);
        Assert.Contains("Promise<CreateItemResponse>", client);
        Assert.Contains("body: request", client);
        // CancellationToken should be skipped
        Assert.DoesNotContain("ct:", client);
    }

    [Fact]
    public void Controller_ExampleAttributes_Populate_Request_And_Response_Examples()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(string Name);

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetType]
            public sealed record ProblemDto(string Title);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                [ProducesResponseType(typeof(ProblemDto), 422)]
                [RivetRequestExample("{\"name\":\"Ada\"}")]
                [RivetResponseExample(422, "{\"title\":\"Validation failed\"}", name: "validationProblem")]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var endpoint = Assert.Single(endpoints);
        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Equal("""{"name":"Ada"}""", requestExample.Json);
        Assert.Null(requestExample.Name);
        Assert.Null(requestExample.ComponentExampleId);
        Assert.Null(requestExample.ResolvedJson);

        var response = endpoint.Responses.Single(r => r.StatusCode == 422);
        var responseExample = Assert.Single(response.Examples!);
        Assert.Equal("validationProblem", responseExample.Name);
        Assert.Equal("application/json", responseExample.MediaType);
        Assert.Equal("""{"title":"Validation failed"}""", responseExample.Json);
        Assert.Null(responseExample.ComponentExampleId);
        Assert.Null(responseExample.ResolvedJson);
    }

    [Fact]
    public void Controller_RequestExampleAttribute_Preserves_Ref_Metadata_Name_And_MediaType_Override()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(string Name);

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                [RivetRequestExample(
                    "{\"name\":\"Ada\"}",
                    componentExampleId: "create-item",
                    name: "starter",
                    mediaType: "application/merge-patch+json")]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var endpoint = Assert.Single(endpoints);
        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("starter", requestExample.Name);
        Assert.Equal("application/merge-patch+json", requestExample.MediaType);
        Assert.Null(requestExample.Json);
        Assert.Equal("create-item", requestExample.ComponentExampleId);
        Assert.Equal("""{"name":"Ada"}""", requestExample.ResolvedJson);
    }

    [Fact]
    public void Controller_ResponseExampleAttribute_Preserves_Ref_Metadata_And_MediaType_Override()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetType]
            public sealed record ProblemDto(string Title);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                [ProducesResponseType(typeof(ItemDto), 200)]
                [ProducesResponseType(typeof(ProblemDto), 422)]
                [RivetResponseExample(
                    422,
                    "{\"title\":\"Bad request\"}",
                    componentExampleId: "problem-example",
                    name: "problem",
                    mediaType: "application/problem+json")]
                public static Task<IActionResult> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var endpoint = Assert.Single(endpoints);
        var response = endpoint.Responses.Single(r => r.StatusCode == 422);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("problem", example.Name);
        Assert.Equal("application/problem+json", example.MediaType);
        Assert.Null(example.Json);
        Assert.Equal("problem-example", example.ComponentExampleId);
        Assert.Equal("""{"title":"Bad request"}""", example.ResolvedJson);
    }

    [Fact]
    public void Controller_ResponseExampleAttribute_Attaches_To_Synthesized_ActionResult_Success_Response()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetType]
            public sealed record ErrorDto(string Message);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                [ProducesResponseType(typeof(ErrorDto), 404)]
                [RivetResponseExample(200, "{\"id\":\"550e8400-e29b-41d4-a716-446655440000\",\"name\":\"Ada\"}", name: "ok")]
                public static Task<ActionResult<ItemDto>> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var endpoint = Assert.Single(endpoints);
        var response = endpoint.Responses.Single(r => r.StatusCode == 200);
        Assert.True(response.DataType is TsType.TypeRef { Name: "ItemDto" });
        var example = Assert.Single(response.Examples!);
        Assert.Equal("ok", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Equal("""{"id":"550e8400-e29b-41d4-a716-446655440000","name":"Ada"}""", example.Json);
        Assert.Null(example.ComponentExampleId);
        Assert.Null(example.ResolvedJson);
    }

    [Fact]
    public void Controller_ResponseExampleAttribute_Without_Declared_Response_Is_Ignored()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                [ProducesResponseType(typeof(ItemDto), 200)]
                [RivetResponseExample(422, "{\"title\":\"Validation failed\"}", name: "validationProblem")]
                public static Task<IActionResult> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var endpoint = Assert.Single(endpoints);
        Assert.Single(endpoint.Responses);
        Assert.Equal(200, endpoint.Responses[0].StatusCode);
        Assert.Null(endpoint.Responses[0].Examples);
        Assert.DoesNotContain(endpoint.Responses, response => response.StatusCode == 422);
    }

    [Fact]
    public void Controller_RequestExampleAttribute_Defaults_To_Multipart_For_File_Upload_Endpoints()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadResultDto(string Id);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpPost("/api/uploads")]
                [ProducesResponseType(typeof(UploadResultDto), 201)]
                [RivetRequestExample("{\"title\":\"Quarterly report\"}")]
                public static Task<IActionResult> Upload(IFormFile file, [FromQuery] string title)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var endpoint = Assert.Single(endpoints);
        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("multipart/form-data", requestExample.MediaType);
        Assert.Equal("""{"title":"Quarterly report"}""", requestExample.Json);
        Assert.Null(requestExample.ComponentExampleId);
        Assert.Null(requestExample.ResolvedJson);
    }

    [Fact]
    public void Controller_QueryOnly_RequestExample_Is_Preserved_In_Model_Without_RequestBody()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SearchResultDto(string Id);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/search")]
                [ProducesResponseType(typeof(SearchResultDto[]), 200)]
                [RivetRequestExample("{\"status\":\"open\"}", name: "filter")]
                public static Task<IActionResult> Search([FromQuery] string status)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var endpoint = Assert.Single(endpoints);
        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("filter", requestExample.Name);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Equal("""{"status":"open"}""", requestExample.Json);
        Assert.Null(requestExample.ComponentExampleId);
        Assert.Null(requestExample.ResolvedJson);
    }

    [Fact]
    public void RivetClient_AutoDiscovered_Method_Preserves_Request_And_Response_Examples()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(string Name);

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetType]
            public sealed record ProblemDto(string Title);

            [RivetClient]
            [Route("api/items")]
            public sealed class ItemsController
            {
                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                [ProducesResponseType(typeof(ProblemDto), 422)]
                [RivetRequestExample("{\"name\":\"Ada\"}", name: "starter")]
                [RivetResponseExample(422, "{\"title\":\"Validation failed\"}", name: "validationProblem")]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var endpoint = Assert.Single(endpoints);
        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("starter", requestExample.Name);
        Assert.Equal("""{"name":"Ada"}""", requestExample.Json);

        var response = endpoint.Responses.Single(r => r.StatusCode == 422);
        var responseExample = Assert.Single(response.Examples!);
        Assert.Equal("validationProblem", responseExample.Name);
        Assert.Equal("""{"title":"Validation failed"}""", responseExample.Json);
    }

    [Fact]
    public void Controller_Multiple_ExampleAttributes_Preserve_Declaration_Order()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateItemRequest(string Name);

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetType]
            public sealed record ProblemDto(string Title);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                [ProducesResponseType(typeof(ProblemDto), 422)]
                [RivetRequestExample("{\"name\":\"Ada\"}", name: "starter")]
                [RivetRequestExample("{\"name\":\"Bea\"}", name: "followUp")]
                [RivetResponseExample(422, "{\"title\":\"Validation failed\"}", name: "validationProblem")]
                [RivetResponseExample(422, "{\"title\":\"Already archived\"}", name: "archivedProblem")]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = WalkEndpoints(source);

        var endpoint = Assert.Single(endpoints);
        var requestExamples = Assert.IsAssignableFrom<IReadOnlyList<TsEndpointExample>>(endpoint.RequestExamples);
        Assert.Equal(2, requestExamples.Count);
        Assert.Equal("starter", requestExamples[0].Name);
        Assert.Equal("""{"name":"Ada"}""", requestExamples[0].Json);
        Assert.Equal("followUp", requestExamples[1].Name);
        Assert.Equal("""{"name":"Bea"}""", requestExamples[1].Json);

        var response = endpoint.Responses.Single(r => r.StatusCode == 422);
        var responseExamples = Assert.IsAssignableFrom<IReadOnlyList<TsEndpointExample>>(response.Examples);
        Assert.Equal(2, responseExamples.Count);
        Assert.Equal("validationProblem", responseExamples[0].Name);
        Assert.Equal("""{"title":"Validation failed"}""", responseExamples[0].Json);
        Assert.Equal("archivedProblem", responseExamples[1].Name);
        Assert.Equal("""{"title":"Already archived"}""", responseExamples[1].Json);
    }

    [Fact]
    public void RivetClient_AutoDiscoversPublicHttpMethods()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);
            [RivetType]
            public sealed record CreateItemRequest(string Name);

            [RivetClient]
            [Route("api/items")]
            public sealed class ItemsController
            {
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(ItemDto), 200)]
                public Task<IActionResult> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();

                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();

                [HttpDelete("{id:guid}")]
                [ProducesResponseType(200)]
                public Task<IActionResult> Delete(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();

                // Should NOT be discovered — no HTTP attribute
                public void HelperMethod() { }
            }
            """;

        var client = GenerateClient(source);

        // All three HTTP methods discovered without [RivetEndpoint]
        Assert.Contains("export function get(", client);
        Assert.Contains("export function create(", client);
        Assert.Contains("export function remove(", client);
        // Helper method not included
        Assert.DoesNotContain("helperMethod", client);
    }

    [Fact]
    public void RivetClient_AndRivetEndpoint_NoDuplicates()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            [RivetClient]
            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(ItemDto), 200)]
                public Task<IActionResult> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        // Should appear exactly once even though it matches both [RivetClient] and [RivetEndpoint]
        // Implementation signature appears once; overload declarations also contain "export function get"
        var implCount = client.Split("opts?: { unwrap?: boolean }").Length - 1;
        Assert.Equal(1, implCount);
    }

    [Fact]
    public void Results_Ok_NotFound_ExtractsSuccessType()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<Results<Ok<ItemDto>, NotFound>> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        Assert.Contains("Promise<ItemDto>", client);
    }

    [Fact]
    public void Results_Ok_NotFound_ExtractsAllResponses()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<Results<Ok<ItemDto>, NotFound>> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal(2, endpoint.Responses.Count);
        Assert.Equal(200, endpoint.Responses[0].StatusCode);
        Assert.True(endpoint.Responses[0].DataType is Rivet.Tool.Model.TsType.TypeRef { Name: "ItemDto" },
            $"200 response DataType should be TypeRef(ItemDto), got {endpoint.Responses[0].DataType}");
        Assert.Equal(404, endpoint.Responses[1].StatusCode);
        Assert.Null(endpoint.Responses[1].DataType);

        // ReturnType should be the unwrapped success type
        Assert.True(endpoint.ReturnType is Rivet.Tool.Model.TsType.TypeRef { Name: "ItemDto" },
            $"ReturnType should be TypeRef(ItemDto), got {endpoint.ReturnType}");
    }

    [Fact]
    public void Results_Created_Conflict_ExtractsResponses()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);
            [RivetType]
            public sealed record ErrorDto(string Message);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpPost("/api/items")]
                public static Task<Results<Created<ItemDto>, Conflict<ErrorDto>>> Create([FromBody] ItemDto body)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal(2, endpoint.Responses.Count);
        Assert.Equal(201, endpoint.Responses[0].StatusCode);
        Assert.True(endpoint.Responses[0].DataType is Rivet.Tool.Model.TsType.TypeRef { Name: "ItemDto" },
            $"201 response DataType should be TypeRef(ItemDto), got {endpoint.Responses[0].DataType}");
        Assert.Equal(409, endpoint.Responses[1].StatusCode);
        Assert.True(endpoint.Responses[1].DataType is Rivet.Tool.Model.TsType.TypeRef { Name: "ErrorDto" },
            $"409 response DataType should be TypeRef(ErrorDto), got {endpoint.Responses[1].DataType}");
    }

    [Fact]
    public void SingleTypedResult_Ok_ExtractsType()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<Ok<ItemDto>> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        Assert.Contains("Promise<ItemDto>", client);
    }

    [Fact]
    public void Results_VoidSuccessBeforeTypedSuccess_PrefersTypedBody()
    {
        // Results<> is a union — declaration order shouldn't affect which type is extracted
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<Results<NoContent, Ok<ItemDto>>> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        var endpoint = Assert.Single(endpoints);
        // Ok<ItemDto> should be the return type even though NoContent appears first
        Assert.NotNull(endpoint.ReturnType);
        Assert.Equal(2, endpoint.Responses.Count);
    }

    [Fact]
    public void Results_NoContent_ExtractsVoidSuccess()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpDelete("/api/items/{id}")]
                public static Task<Results<NoContent, NotFound>> Delete([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        var endpoint = Assert.Single(endpoints);
        Assert.Null(endpoint.ReturnType);
        Assert.Equal(2, endpoint.Responses.Count);
        Assert.Equal(204, endpoint.Responses[0].StatusCode);
        Assert.Null(endpoint.Responses[0].DataType);
        Assert.Equal(404, endpoint.Responses[1].StatusCode);
        Assert.Null(endpoint.Responses[1].DataType);
    }

    [Fact]
    public void Controller_ActionResultT_WithOnly404_Synthesizes200()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);
            [RivetType]
            public sealed record ErrorDto(string Message);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(ErrorDto), 404)]
                public Task<ActionResult<ItemDto>> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal(2, endpoint.Responses.Count);
        Assert.Equal(200, endpoint.Responses[0].StatusCode);
        Assert.NotNull(endpoint.Responses[0].DataType);
        Assert.Equal(404, endpoint.Responses[1].StatusCode);
        Assert.NotNull(endpoint.Responses[1].DataType);
    }

    [Fact]
    public void Controller_ActionResultT_UnwrapsReturnType()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<ActionResult<ItemDto>> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var client = GenerateClient(source);

        Assert.Contains("Promise<ItemDto>", client);
    }
}
