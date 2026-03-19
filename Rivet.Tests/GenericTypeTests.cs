using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class GenericTypeTests
{
    private static string Generate(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var definitions = walker.Definitions.Values.ToList();
        var brands = walker.Brands.Values.ToList();
        var grouping = TypeGrouper.Group(definitions, brands, walker.Enums, walker.TypeNamespaces);
        return string.Concat(grouping.Groups.Select(TypeEmitter.EmitGroupFile));
    }

    [Fact]
    public void GenericRecord_EmitsTypeParameter()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PagedResult<T>(
                List<T> Items,
                int TotalCount,
                int Page,
                int PageSize);
            """;

        var result = Generate(source);

        Assert.Contains("export type PagedResult<T> = {", result);
        Assert.Contains("items: T[];", result);
        Assert.Contains("totalCount: number;", result);
    }

    [Fact]
    public void GenericRecord_MultipleTypeParams()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Pair<TFirst, TSecond>(TFirst First, TSecond Second);
            """;

        var result = Generate(source);

        Assert.Contains("export type Pair<TFirst, TSecond> = {", result);
        Assert.Contains("first: TFirst;", result);
        Assert.Contains("second: TSecond;", result);
    }

    [Fact]
    public void ClosedGeneric_EmitsGenericApplication()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            public sealed record PagedResult<T>(
                List<T> Items,
                int TotalCount);

            [RivetType]
            public sealed record MessageDto(Guid Id, string Body);

            [RivetType]
            public sealed record MessageListResult(PagedResult<MessageDto> Messages);
            """;

        var result = Generate(source);

        // PagedResult should be emitted as a generic definition
        Assert.Contains("export type PagedResult<T> = {", result);
        // MessageListResult should reference the closed generic
        Assert.Contains("messages: PagedResult<MessageDto>;", result);
    }

    [Fact]
    public void ClosedGeneric_WithPrimitiveArg()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            public sealed record Wrapper<T>(T Value, string Label);

            [RivetType]
            public sealed record StringWrapper(Wrapper<string> Wrapped);
            """;

        var result = Generate(source);

        Assert.Contains("export type Wrapper<T> = {", result);
        Assert.Contains("wrapped: Wrapper<string>;", result);
    }

    [Fact]
    public void JsonElement_MapsToUnknown()
    {
        var source = """
            using System.Text.Json;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FlexibleDto(string Name, JsonElement Payload);
            """;

        var result = Generate(source);

        Assert.Contains("payload: unknown;", result);
    }

    [Fact]
    public void JsonNode_MapsToUnknown()
    {
        var source = """
            using System.Text.Json.Nodes;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record DynamicDto(string Name, JsonNode? Data);
            """;

        var result = Generate(source);

        Assert.Contains("data: unknown | null;", result);
    }

    [Fact]
    public void JsonObject_MapsToRecord()
    {
        var source = """
            using System.Text.Json.Nodes;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record BranchCase(string Label, JsonObject Condition);
            """;

        var result = Generate(source);

        Assert.Contains("condition: Record<string, unknown>;", result);
    }

    [Fact]
    public void JsonArray_MapsToUnknownArray()
    {
        var source = """
            using System.Text.Json.Nodes;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record BatchRequest(string Name, JsonArray Items);
            """;

        var result = Generate(source);

        Assert.Contains("items: unknown[];", result);
    }
}
