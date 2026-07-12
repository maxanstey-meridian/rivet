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

        var (_, walker) = CompilationHelper.WalkContract(source);

        // The open generic definition carries its type parameter
        var pagedResult = walker.Definitions["PagedResult"];
        Assert.Equal(["T"], pagedResult.TypeParameters);

        // items: T[] — array of the unresolved type parameter
        var itemsProp = Assert.Single(pagedResult.Properties, p => p.Name == "items");
        var array = Assert.IsType<TsType.Array>(itemsProp.Type);
        Assert.Equal("T", Assert.IsType<TsType.TypeParam>(array.Element).Name);

        // totalCount: number
        var totalCountProp = Assert.Single(pagedResult.Properties, p => p.Name == "totalCount");
        Assert.Equal("number", Assert.IsType<TsType.Primitive>(totalCountProp.Type).Name);
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

        var (_, walker) = CompilationHelper.WalkContract(source);

        // JsonElement → unknown
        var dto = walker.Definitions["FlexibleDto"];
        var payloadProp = Assert.Single(dto.Properties, p => p.Name == "payload");
        Assert.Equal("unknown", Assert.IsType<TsType.Primitive>(payloadProp.Type).Name);
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

        var (_, walker) = CompilationHelper.WalkContract(source);

        // JsonNode? → (unknown | null), optional; CSharpType preserved for round-tripping
        var dto = walker.Definitions["DynamicDto"];
        var dataProp = Assert.Single(dto.Properties, p => p.Name == "data");
        var nullable = Assert.IsType<TsType.Nullable>(dataProp.Type);
        var inner = Assert.IsType<TsType.Primitive>(nullable.Inner);
        Assert.Equal("unknown", inner.Name);
        Assert.Equal("JsonNode", inner.CSharpType);
        Assert.True(dataProp.IsOptional);
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

        var (_, walker) = CompilationHelper.WalkContract(source);

        // JsonObject → Record<string, unknown>
        var dto = walker.Definitions["BranchCase"];
        var conditionProp = Assert.Single(dto.Properties, p => p.Name == "condition");
        var dict = Assert.IsType<TsType.Dictionary>(conditionProp.Type);
        var value = Assert.IsType<TsType.Primitive>(dict.Value);
        Assert.Equal("unknown", value.Name);
        Assert.Equal("JsonObject", value.CSharpType);
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

        var (_, walker) = CompilationHelper.WalkContract(source);

        // JsonArray → unknown[]
        var dto = walker.Definitions["BatchRequest"];
        var itemsProp = Assert.Single(dto.Properties, p => p.Name == "items");
        var array = Assert.IsType<TsType.Array>(itemsProp.Type);
        var element = Assert.IsType<TsType.Primitive>(array.Element);
        Assert.Equal("unknown", element.Name);
        Assert.Equal("JsonArray", element.CSharpType);
    }
}
