namespace Rivet.Tests;

public sealed class FileRouteDefinitionTests
{
    [Fact]
    public void File_DefaultsToGet()
    {
        var route = Define.File("/api/files/{id}/download");

        Assert.Equal("GET", route.Method);
        Assert.Equal("/api/files/{id}/download", route.Route);
    }

    [Fact]
    public void File_DefaultContentType_IsOctetStream()
    {
        var route = Define.File("/api/files/{id}");

        Assert.Equal("application/octet-stream", route.FileContentType);
    }

    [Fact]
    public void File_ContentType_OverridesDefault()
    {
        var route = Define.File("/api/stream")
            .ContentType("video/mp4");

        Assert.Equal("video/mp4", route.FileContentType);
    }

    [Fact]
    public void File_ContentType_IsFluent()
    {
        var route = Define.File("/api/stream");
        var returned = route.ContentType("audio/mpeg");

        Assert.Same(route, returned);
    }

    [Fact]
    public void File_Generic_DefaultsToGet()
    {
        var route = Define.File<FileDownloadInput>("/api/files/{id}/download");

        Assert.Equal("GET", route.Method);
        Assert.Equal("/api/files/{id}/download", route.Route);
    }

    [Fact]
    public void File_Generic_DefaultContentType_IsOctetStream()
    {
        var route = Define.File<FileDownloadInput>("/api/files/{id}");

        Assert.Equal("application/octet-stream", route.FileContentType);
    }

    [Fact]
    public void File_Generic_ContentType_OverridesDefault()
    {
        var route = Define.File<FileDownloadInput>("/api/stream")
            .ContentType("video/mp4");

        Assert.Equal("video/mp4", route.FileContentType);
    }

    [Fact]
    public void File_BaseBuilderMethods_Work()
    {
        var route = Define.File("/api/stream")
            .ContentType("video/mp4")
            .Summary("Download a video")
            .Anonymous();

        Assert.Equal("video/mp4", route.FileContentType);
        Assert.Equal("Download a video", route.EndpointSummary);
        Assert.True(route.IsAnonymous);
    }

    [Fact]
    public void File_Generic_BaseBuilderMethods_Work()
    {
        var route = Define.File<FileDownloadInput>("/api/stream")
            .ContentType("audio/mpeg")
            .Description("Stream audio content")
            .Secure("Bearer");

        Assert.Equal("audio/mpeg", route.FileContentType);
        Assert.Equal("Stream audio content", route.EndpointDescription);
        Assert.Equal("Bearer", route.SecurityScheme);
    }

    [Fact]
    public void File_ImplicitConversionToDefine_Compiles()
    {
        // This test verifies the implicit operator exists and compiles.
        // The Define type is used by Roslyn analysis to discover contract fields.
        Define _ = Define.File("/api/files/{id}");
        Define __ = Define.File<FileDownloadInput>("/api/files/{id}");
    }

    // Dummy input type for generic variant tests
    private sealed record FileDownloadInput(string Id);
}
