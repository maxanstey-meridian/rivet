using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class GenericTypeTests
{
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

        var result = CompilationHelper.EmitTypes(source);

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

        var compilation = CompilationHelper.CreateCompilation(source);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        // Validate type parameters on the definition
        var pair = walker.Definitions["Pair"];
        Assert.Equal(2, pair.TypeParameters.Count);
        Assert.Equal("TFirst", pair.TypeParameters[0]);
        Assert.Equal("TSecond", pair.TypeParameters[1]);

        // Validate property types reference type params
        var firstProp = Assert.Single(pair.Properties, p => p.Name == "first");
        var firstType = Assert.IsType<TsType.TypeParam>(firstProp.Type);
        Assert.Equal("TFirst", firstType.Name);
        var secondProp = Assert.Single(pair.Properties, p => p.Name == "second");
        var secondType = Assert.IsType<TsType.TypeParam>(secondProp.Type);
        Assert.Equal("TSecond", secondType.Name);

        var result = CompilationHelper.EmitTypes(source);

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

        var compilation = CompilationHelper.CreateCompilation(source);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        // Validate the walker model for closed generic type arguments
        var msgList = walker.Definitions["MessageListResult"];
        var messagesProp = Assert.Single(msgList.Properties, p => p.Name == "messages");
        var genericType = Assert.IsType<TsType.Generic>(messagesProp.Type);
        Assert.Equal("PagedResult", genericType.Name);
        Assert.Single(genericType.TypeArguments);
        var typeArg = Assert.IsType<TsType.TypeRef>(genericType.TypeArguments[0]);
        Assert.Equal("MessageDto", typeArg.Name);

        // Validate the generic definition has its type parameter
        var pagedResult = walker.Definitions["PagedResult"];
        Assert.Single(pagedResult.TypeParameters);
        Assert.Equal("T", pagedResult.TypeParameters[0]);

        var result = CompilationHelper.EmitTypes(source);

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

        var compilation = CompilationHelper.CreateCompilation(source);
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        // Validate the walker model for primitive type argument
        var stringWrapper = walker.Definitions["StringWrapper"];
        var wrappedProp = Assert.Single(stringWrapper.Properties, p => p.Name == "wrapped");
        var genericType = Assert.IsType<TsType.Generic>(wrappedProp.Type);
        Assert.Equal("Wrapper", genericType.Name);
        Assert.Single(genericType.TypeArguments);
        var typeArg = Assert.IsType<TsType.Primitive>(genericType.TypeArguments[0]);
        Assert.Equal("string", typeArg.Name);

        var result = CompilationHelper.EmitTypes(source);

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

        var result = CompilationHelper.EmitTypes(source);

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

        var result = CompilationHelper.EmitTypes(source);

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

        var result = CompilationHelper.EmitTypes(source);

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

        var result = CompilationHelper.EmitTypes(source);

        Assert.Contains("items: unknown[];", result);
    }
}
