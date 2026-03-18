using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class TransitiveEndpointTests
{
    [Fact]
    public void EndpointTypes_DiscoveredWithoutRivetType()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            // NO [RivetType] on these — should be discovered via endpoint
            public sealed record CreateItemRequest(string Name, int Quantity);
            public sealed record ItemDto(Guid Id, string Name, int Quantity, DateTime CreatedAt);

            [Route("api/items")]
            public sealed class ItemsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(ItemDto), 201)]
                public Task<IActionResult> Create(
                    [FromBody] CreateItemRequest request,
                    CancellationToken ct)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(ItemDto), 200)]
                public Task<IActionResult> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var endpoints = EndpointWalker.Walk(compilation, walker);
        var definitions = walker.Definitions.Values.ToList();
        var brands = walker.Brands.Values.ToList();
        var types = TypeEmitter.Emit(definitions, brands);
        var client = ClientEmitter.EmitControllerClient("items", endpoints);

        // Types should be discovered transitively via endpoint params/return types
        Assert.Contains("export type CreateItemRequest = {", types);
        Assert.Contains("export type ItemDto = {", types);
        Assert.Contains("name: string;", types);
        Assert.Contains("quantity: number;", types);

        // Client should reference them
        Assert.Contains("request: CreateItemRequest", client);
        Assert.Contains("Promise<RivetResponse<ItemDto>>", client);
    }

    [Fact]
    public void EndpointTypes_NestedTransitiveDiscovery()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public enum Priority { Low, Medium, High }
            public sealed record Email(string Value);
            public sealed record AuthorInfo(string Name, Email Email);

            // NO [RivetType] — discovered via endpoint, which discovers AuthorInfo, Email, Priority
            public sealed record PostDto(Guid Id, string Title, Priority Priority, AuthorInfo Author);

            [Route("api/posts")]
            public sealed class PostsController
            {
                [RivetEndpoint]
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(PostDto), 200)]
                public Task<IActionResult> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var endpoints = EndpointWalker.Walk(compilation, walker);
        var definitions = walker.Definitions.Values.ToList();
        var brands = walker.Brands.Values.ToList();
        var types = TypeEmitter.Emit(definitions, brands);

        // PostDto discovered via endpoint
        Assert.Contains("export type PostDto = {", types);
        // AuthorInfo discovered transitively via PostDto
        Assert.Contains("export type AuthorInfo = {", types);
        // Email discovered as branded VO via AuthorInfo
        Assert.Contains("""export type Email = string & { readonly __brand: "Email" };""", types);
        // Priority discovered as string union via PostDto
        Assert.Contains(""""Low" | "Medium" | "High"""", types);
    }
}
