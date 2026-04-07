using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ContractWalkerFileEndpointTests
{
    private static IReadOnlyList<TsEndpointDefinition> Walk(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        return CompilationHelper.WalkContracts(compilation, discovered, walker);
    }

    [Fact]
    public void File_WithContentTypeAndQueryAuth_SetsAllMetadata()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record StreamInput(string Id);

            [RivetContract]
            public static class MediaContract
            {
                public static readonly Define Stream =
                    Define.File<StreamInput>("/api/media/{id}/stream")
                        .ContentType("video/mp4")
                        .QueryAuth();
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("GET", ep.HttpMethod);
        Assert.True(ep.IsFileEndpoint);
        Assert.Equal("video/mp4", ep.FileContentType);
        Assert.NotNull(ep.QueryAuth);
        Assert.Equal("token", ep.QueryAuth.ParameterName);
    }

    [Fact]
    public void File_QueryAuth_CustomParameterName()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class MediaContract
            {
                public static readonly Define Stream =
                    Define.File("/api/media/stream")
                        .QueryAuth("key");
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.IsFileEndpoint);
        Assert.NotNull(ep.QueryAuth);
        Assert.Equal("key", ep.QueryAuth.ParameterName);
    }

    [Fact]
    public void StandardGet_IsNotFileEndpoint_NoQueryAuth()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItem =
                    Define.Get<ItemDto>("/api/items/{id}");
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("GET", ep.HttpMethod);
        Assert.False(ep.IsFileEndpoint);
        Assert.Null(ep.QueryAuth);
    }

    [Fact]
    public void QueryAuth_OnNonFileEndpoint_SetsQueryAuthOnly()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record DataDto(string Value);

            [RivetContract]
            public static class DataContract
            {
                public static readonly Define GetData =
                    Define.Get<DataDto>("/api/data")
                        .QueryAuth("api_key");
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.False(ep.IsFileEndpoint);
        Assert.NotNull(ep.QueryAuth);
        Assert.Equal("api_key", ep.QueryAuth.ParameterName);
    }

    [Fact]
    public void File_WithoutQueryAuth_DefaultsCorrectly()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class FilesContract
            {
                public static readonly Define Download =
                    Define.File("/api/files/{id}/download");
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("GET", ep.HttpMethod);
        Assert.True(ep.IsFileEndpoint);
        Assert.Equal("application/octet-stream", ep.FileContentType);
        Assert.Null(ep.QueryAuth);
    }
}
