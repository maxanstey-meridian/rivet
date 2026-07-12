using Rivet.Tool.Model;

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
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        // Types should be discovered transitively via endpoint params/return types
        Assert.True(
            walker.Definitions.ContainsKey("CreateItemRequest"),
            $"CreateItemRequest not discovered. Definitions: [{string.Join(", ", walker.Definitions.Keys)}]"
        );
        Assert.True(
            walker.Definitions.ContainsKey("ItemDto"),
            $"ItemDto not discovered. Definitions: [{string.Join(", ", walker.Definitions.Keys)}]"
        );

        var request = walker.Definitions["CreateItemRequest"];
        var nameProp = Assert.Single(request.Properties, p => p.Name == "name");
        Assert.Equal("string", Assert.IsType<TsType.Primitive>(nameProp.Type).Name);
        var quantityProp = Assert.Single(request.Properties, p => p.Name == "quantity");
        Assert.Equal("number", Assert.IsType<TsType.Primitive>(quantityProp.Type).Name);

        // Endpoints should reference the discovered types
        Assert.Equal(2, endpoints.Count);
        var create = Assert.Single(endpoints, e => e.Name == "create");
        var bodyParam = Assert.Single(create.Params, p => p.Source == ParamSource.Body);
        Assert.Equal("CreateItemRequest", Assert.IsType<TsType.TypeRef>(bodyParam.Type).Name);
        Assert.Equal("ItemDto", Assert.IsType<TsType.TypeRef>(create.ReturnType).Name);

        var get = Assert.Single(endpoints, e => e.Name == "get");
        Assert.Equal("ItemDto", Assert.IsType<TsType.TypeRef>(get.ReturnType).Name);
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
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        // PostDto discovered via endpoint
        var ep = Assert.Single(endpoints);
        Assert.Equal("PostDto", Assert.IsType<TsType.TypeRef>(ep.ReturnType).Name);
        Assert.True(
            walker.Definitions.ContainsKey("PostDto"),
            $"PostDto not discovered. Definitions: [{string.Join(", ", walker.Definitions.Keys)}]"
        );

        // AuthorInfo discovered transitively via PostDto
        Assert.True(
            walker.Definitions.ContainsKey("AuthorInfo"),
            $"AuthorInfo not discovered. Definitions: [{string.Join(", ", walker.Definitions.Keys)}]"
        );
        var postDto = walker.Definitions["PostDto"];
        var authorProp = Assert.Single(postDto.Properties, p => p.Name == "author");
        Assert.Equal("AuthorInfo", Assert.IsType<TsType.TypeRef>(authorProp.Type).Name);

        // Email discovered as branded VO via AuthorInfo
        var emailBrand = Assert.Contains("Email", walker.Brands);
        Assert.Equal("string", Assert.IsType<TsType.Primitive>(emailBrand.Inner).Name);
        var authorInfo = walker.Definitions["AuthorInfo"];
        var emailProp = Assert.Single(authorInfo.Properties, p => p.Name == "email");
        Assert.Equal("Email", Assert.IsType<TsType.Brand>(emailProp.Type).Name);

        // Priority discovered as named enum type via PostDto
        var priority = Assert.Contains("Priority", walker.Enums);
        var union = Assert.IsType<TsType.StringUnion>(priority);
        Assert.Equal(["low", "medium", "high"], union.Members);
    }

    [Fact]
    public void ContractTypes_DiscoveredFromProjectReference()
    {
        // Domain types in a separate "project" (CompilationReference) — no [RivetType]
        var domainSource = """
            using System.Collections.Generic;

            namespace Domain;

            public sealed record CaseDocumentMeta(
                int DocumentId,
                string DocumentName);

            public sealed record CaseSearchResult(
                string CaseRef,
                string? Title,
                IReadOnlyList<CaseDocumentMeta> Documents);
            """;

        var mainSource = """
            using Domain;
            using Rivet;

            namespace Host;

            [RivetContract]
            public static class CaseSearchContract
            {
                public static readonly RouteDefinition<CaseSearchResult> Search =
                    Define.Get<CaseSearchResult>("/api/cases/{caseRef}")
                        .Description("Search for a case");
            }
            """;

        var compilation = CompilationHelper.CreateCompilationWithProjectReference(
            mainSource,
            domainSource
        );
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        // Endpoint should be discovered
        Assert.Single(endpoints);
        Assert.Equal("search", endpoints[0].Name);
        Assert.NotNull(endpoints[0].ReturnType);

        // Types from the referenced project should be walked transitively
        Assert.True(
            walker.Definitions.ContainsKey("CaseSearchResult"),
            $"CaseSearchResult not discovered. Definitions: [{string.Join(", ", walker.Definitions.Keys)}]"
        );
        Assert.True(
            walker.Definitions.ContainsKey("CaseDocumentMeta"),
            $"CaseDocumentMeta not discovered. Definitions: [{string.Join(", ", walker.Definitions.Keys)}]"
        );

        // CaseSearchResult should have the Documents property
        var csr = walker.Definitions["CaseSearchResult"];
        Assert.Contains(csr.Properties, p => p.Name == "documents");
    }
}
