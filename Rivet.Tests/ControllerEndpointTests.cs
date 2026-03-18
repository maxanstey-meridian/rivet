using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class ControllerEndpointTests
{
    private static string GenerateClient(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var endpoints = EndpointWalker.Walk(compilation, walker);
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
        Assert.Contains("\"GET\", `/api/items/${id}`", client);
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
        Assert.Contains("""`/api/case-statuses/${id}`""", client);
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

        // {id:guid} should become ${id}, not ${id:guid}
        Assert.Contains("${id}", client);
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
