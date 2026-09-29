using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task File_DefaultContentType_IsOctetStream()
    {
        var route = Define.File("/api/files/{id}");

        Assert.Equal("application/octet-stream", await WireContentType(route.File([1])));
    }

    [Fact]
    public async Task File_ContentType_OverridesDefault()
    {
        var route = Define.File("/api/stream").ContentType("video/mp4");

        Assert.Equal("video/mp4", await WireContentType(route.File([1])));
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
    public async Task File_Generic_DefaultContentType_IsOctetStream()
    {
        var route = Define.File<FileDownloadInput>("/api/files/{id}");

        Assert.Equal(
            "application/octet-stream",
            await WireContentType(route.Bind(new FileDownloadInput("1")).File([1]))
        );
    }

    [Fact]
    public async Task File_Generic_ContentType_OverridesDefault()
    {
        var route = Define.File<FileDownloadInput>("/api/stream").ContentType("video/mp4");

        Assert.Equal(
            "video/mp4",
            await WireContentType(route.Bind(new FileDownloadInput("1")).File([1]))
        );
    }

    [Fact]
    public async Task File_BaseBuilderMethods_Work()
    {
        var route = Define
            .File("/api/stream")
            .ContentType("video/mp4")
            .Summary("Download a video")
            .Anonymous()
            .QueryAuth();

        Assert.Equal("video/mp4", await WireContentType(route.File([1])));
    }

    [Fact]
    public async Task File_Generic_BaseBuilderMethods_Work()
    {
        var route = Define
            .File<FileDownloadInput>("/api/stream")
            .ContentType("audio/mpeg")
            .Description("Stream audio content")
            .Secure("Bearer")
            .QueryAuth("session");

        Assert.Equal(
            "audio/mpeg",
            await WireContentType(route.Bind(new FileDownloadInput("1")).File([1]))
        );
    }

    // --- QueryAuth tests ---

    [Fact]
    public void QueryAuth_IsFluent_ReturnsSelf()
    {
        var route = Define.File("/api/stream");
        var returned = route.QueryAuth();

        Assert.Same(route, returned);
    }

    // --- R3: builder freeze — mutating a published definition throws ---

    [Fact]
    public void R3_RouteDefinition_MutationAfterSuccess_Throws()
    {
        // R3: contract definitions live in static readonly fields shared by all requests;
        // a runtime builder call would silently mutate global state. After the first
        // terminal result is created, the definition is published and must be immutable.
        var route = Define.Get<string>("/api/data");

        route.Success("ok");

        var ex = Assert.Throws<InvalidOperationException>(() => route.Summary("too late"));
        Assert.Contains("immutable once published", ex.Message);
    }

    [Fact]
    public void R3_AllMutators_ThrowAfterSuccess()
    {
        var route = Define.Get<string>("/api/data");
        route.Success("ok");

        Assert.Throws<InvalidOperationException>(() => route.Summary("x"));
        Assert.Throws<InvalidOperationException>(() => route.Description("x"));
        Assert.Throws<InvalidOperationException>(() => route.Status(202));
        Assert.Throws<InvalidOperationException>(() => route.FormEncoded());
        Assert.Throws<InvalidOperationException>(() => route.Returns<string>(404));
        Assert.Throws<InvalidOperationException>(() => route.Returns(404));
        Assert.Throws<InvalidOperationException>(() => route.Anonymous());
        Assert.Throws<InvalidOperationException>(() => route.Secure("Bearer"));
        Assert.Throws<InvalidOperationException>(() => route.QueryAuth());
        Assert.Throws<InvalidOperationException>(() => route.ProducesFile());
        Assert.Throws<InvalidOperationException>(() => route.AcceptsFile());
    }

    [Fact]
    public void R3_VoidRouteDefinition_MutationAfterSuccess_Throws()
    {
        var route = Define.Delete("/api/items/{id}");

        route.Success();

        var ex = Assert.Throws<InvalidOperationException>(() => route.Returns(404));
        Assert.Contains("immutable once published", ex.Message);
    }

    [Fact]
    public void R3_InputRouteDefinition_MutationAfterBind_Throws()
    {
        var route = Define.Put("/api/items/{id}").Accepts<FileDownloadInput>();

        route.Bind(new FileDownloadInput("1"));

        Assert.Throws<InvalidOperationException>(() => route.Anonymous());
    }

    [Fact]
    public void R3_RepeatedSuccess_StillAllowed()
    {
        // Freezing affects builder mutation only — terminal results can be created per request.
        var route = Define.Get<string>("/api/data");

        var first = route.Success("a");
        var second = route.Success("b");

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task R3_BuilderChain_BeforePublication_StillMutable()
    {
        // The generation-time fluent chain is unaffected
        var route = Define.File("/api/stream").ContentType("video/mp4").Summary("ok").QueryAuth();

        Assert.Equal("video/mp4", await WireContentType(route.File([1])));
    }

    private static async Task<string?> WireContentType(RivetResult result)
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        await result.ToResult().ExecuteAsync(context);
        return context.Response.ContentType;
    }

    // Dummy input type for generic variant tests
    private sealed record FileDownloadInput(string Id);
}
