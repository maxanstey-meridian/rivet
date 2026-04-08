using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ZodValidatorEmitterTests
{
    private static string GenerateValidators(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();

        var extraction = InlineTypeExtractor.Extract(endpoints, definitions);
        var allDefinitions = definitions.Concat(extraction.ExtractedTypes).ToList();
        var allNamespaces = new Dictionary<string, string?>(walker.TypeNamespaces);
        foreach (var (name, ns) in extraction.TypeNamespaces)
            allNamespaces[name] = ns;

        var typeGrouping = TypeGrouper.Group(allDefinitions, walker.Brands.Values.ToList(), walker.Enums, allNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        return ZodValidatorEmitter.Emit(extraction.Endpoints, typeFileMap);
    }

    [Fact]
    public void RequestType_EmitsAssertFunction()
    {
        var source = """
            using System;
            using System.ComponentModel.DataAnnotations;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateUserRequest(
                [property: MinLength(2)] string Name,
                [property: MinLength(5)] string Email);

            [RivetType]
            public sealed record UserDto(Guid Id, string Name, string Email);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpPost("/api/users")]
                public static Task<UserDto> CreateUser(
                    [FromBody] CreateUserRequest body)
                    => throw new NotImplementedException();
            }
            """;

        var validators = GenerateValidators(source);

        // Request type gets an assert function
        Assert.Contains("assertCreateUserRequest", validators);
        // Response type also gets an assert function
        Assert.Contains("assertUserDto", validators);
        // Schema imports include request type
        Assert.Contains("CreateUserRequestSchema", validators);
        // Type imports include request type (not just substring match on assert/schema lines)
        Assert.Contains("import type { CreateUserRequest", validators);
    }

    [Fact]
    public void SharedType_RequestAndResponse_SingleAssert()
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
                [HttpPost("/api/items")]
                public static Task<ItemDto> CreateItem(
                    [FromBody] ItemDto body)
                    => throw new NotImplementedException();
            }
            """;

        var validators = GenerateValidators(source);

        // Only one assert function despite being both request and response
        var count = validators.Split("export const assertItemDto").Length - 1;
        Assert.Equal(1, count);

        // Schema and type imports present
        Assert.Contains("ItemDtoSchema", validators);
        Assert.Contains("assertItemDto", validators);
    }

    [Fact]
    public void RequestTypeField_EmitsAssertFunction()
    {
        // Exercises the RequestType field path (contract/import pipeline),
        // not the body Params path used by [RivetEndpoint].
        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                Name: "createOrder",
                HttpMethod: "POST",
                RouteTemplate: "/api/orders",
                Params: [],
                ReturnType: new TsType.TypeRef("OrderDto"),
                ControllerName: "Orders",
                Responses: [],
                RequestType: new TsType.TypeRef("CreateOrderRequest"))
        };

        var typeFileMap = new Dictionary<string, string>
        {
            ["OrderDto"] = "orders",
            ["CreateOrderRequest"] = "orders",
        };

        var validators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);

        // Request type assert function from RequestType field
        Assert.Contains("assertCreateOrderRequest", validators);
        Assert.Contains("CreateOrderRequestSchema", validators);
        // Response type still present
        Assert.Contains("assertOrderDto", validators);
        Assert.Contains("OrderDtoSchema", validators);
        // Type imports include request type (not just substring match on assert/schema lines)
        Assert.Contains("import type { CreateOrderRequest", validators);
    }
}
