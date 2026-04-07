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

    // --- Return-type heuristic tests ---

    [Fact]
    public void StreamReturnType_SetsIsFileEndpoint()
    {
        var source = """
            using Rivet;
            using System.IO;

            namespace Test;

            [RivetType]
            public sealed record StreamInput(string Id);

            [RivetContract]
            public static class MediaContract
            {
                public static readonly Define GetStream =
                    Define.Get<StreamInput, Stream>("/api/media/{id}/stream");
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.IsFileEndpoint);
        Assert.Equal("application/octet-stream", ep.FileContentType);
        Assert.Null(ep.ReturnType); // Stream should not map to a TS type
    }

    [Fact]
    public void FileStreamResultReturnType_SetsIsFileEndpoint()
    {
        var source = """
            using Rivet;

            namespace Microsoft.AspNetCore.Mvc { public class FileStreamResult { } }

            namespace Test
            {
                [RivetType]
                public sealed record DownloadInput(string Id);

                [RivetContract]
                public static class FilesContract
                {
                    public static readonly Define Download =
                        Define.Get<DownloadInput, Microsoft.AspNetCore.Mvc.FileStreamResult>("/api/files/{id}");
                }
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.IsFileEndpoint);
        Assert.Equal("application/octet-stream", ep.FileContentType);
        Assert.Null(ep.ReturnType);
    }

    [Fact]
    public void FileContentResultReturnType_SetsIsFileEndpoint()
    {
        var source = """
            using Rivet;

            namespace Microsoft.AspNetCore.Mvc { public class FileContentResult { } }

            namespace Test
            {
                [RivetContract]
                public static class FilesContract
                {
                    public static readonly Define Download =
                        Define.Get<Microsoft.AspNetCore.Mvc.FileContentResult>("/api/files/export");
                }
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.IsFileEndpoint);
        Assert.Null(ep.ReturnType);
    }

    [Fact]
    public void PhysicalFileResultReturnType_SetsIsFileEndpoint()
    {
        var source = """
            using Rivet;

            namespace Microsoft.AspNetCore.Mvc { public class PhysicalFileResult { } }

            namespace Test
            {
                [RivetContract]
                public static class FilesContract
                {
                    public static readonly Define Download =
                        Define.Get<Microsoft.AspNetCore.Mvc.PhysicalFileResult>("/api/files/physical");
                }
            }
            """;

        var endpoints = Walk(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.IsFileEndpoint);
        Assert.Null(ep.ReturnType);
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
}
