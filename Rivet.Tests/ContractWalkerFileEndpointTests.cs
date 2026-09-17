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

    [Fact]
    public void StandardJsonReturnType_IsNotFileEndpoint()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemInput(string Id);

            [RivetType]
            public sealed record ItemDto(string Id, string Name);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItem =
                    Define.Get<ItemInput, ItemDto>("/api/items/{id}");
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.False(ep.IsFileEndpoint);
        Assert.NotNull(ep.ReturnType);
        Assert.Null(ep.FileContentType);
    }

    [Fact]
    public void ExplicitDefineFile_TakesPrecedenceOverReturnTypeHeuristic()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record StreamInput(string Id);

            [RivetContract]
            public static class MediaContract
            {
                public static readonly Define GetVideo =
                    Define.File<StreamInput>("/api/media/{id}/video")
                        .ContentType("video/mp4");
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.IsFileEndpoint);
        Assert.Equal("video/mp4", ep.FileContentType); // Not overridden to octet-stream
    }

    [Fact]
    public void FileEndpointWithInputType_ExtractsQueryParams()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FileRequest(string Path);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly Define Stream =
                    Define.File<FileRequest>("/api/files/stream")
                        .QueryAuth();
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.IsFileEndpoint);
        // FileRequest.Path should become a query param — it's not in the route template
        var pathParam = Assert.Single(ep.Params, p => p.Name == "path");
        Assert.Equal(ParamSource.Query, pathParam.Source);
    }

    [Fact]
    public void FileEndpointWithInputType_ExtractsRouteAndQueryParams()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record StreamInput(string Id, string Quality);

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
        Assert.True(ep.IsFileEndpoint);
        Assert.Equal(2, ep.Params.Count);
        var idParam = Assert.Single(ep.Params, p => p.Name == "id");
        Assert.Equal(ParamSource.Route, idParam.Source);
        var qualityParam = Assert.Single(ep.Params, p => p.Name == "quality");
        Assert.Equal(ParamSource.Query, qualityParam.Source);
    }
}
