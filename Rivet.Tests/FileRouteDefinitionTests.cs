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
        var route = Define.File("/api/stream").ContentType("video/mp4");

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
        var route = Define.File<FileDownloadInput>("/api/stream").ContentType("video/mp4");

        Assert.Equal("video/mp4", route.FileContentType);
    }

    [Fact]
    public void File_BaseBuilderMethods_Work()
    {
        var route = Define
            .File("/api/stream")
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
        var route = Define
            .File<FileDownloadInput>("/api/stream")
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

    // --- QueryAuth tests ---

    [Fact]
    public void QueryAuth_DefaultParameterName_IsToken()
    {
        var route = Define.File("/api/stream").QueryAuth();

        Assert.True(route.IsQueryAuth);
        Assert.Equal("token", route.QueryAuthParameterName);
    }

    [Fact]
    public void QueryAuth_CustomParameterName()
    {
        var route = Define.File("/api/stream").QueryAuth("key");

        Assert.True(route.IsQueryAuth);
        Assert.Equal("key", route.QueryAuthParameterName);
    }

    [Fact]
    public void QueryAuth_NotSet_IsFalse()
    {
        var route = Define.File("/api/stream");

        Assert.False(route.IsQueryAuth);
        Assert.Null(route.QueryAuthParameterName);
    }

    [Fact]
    public void QueryAuth_ChainableWithContentType()
    {
        var route = Define.File("/api/stream").ContentType("video/mp4").QueryAuth();

        Assert.Equal("video/mp4", route.FileContentType);
        Assert.True(route.IsQueryAuth);
        Assert.Equal("token", route.QueryAuthParameterName);
    }

    [Fact]
    public void QueryAuth_AvailableOnRouteDefinition()
    {
        var route = Define.Get("/api/data").QueryAuth("api_key");

        Assert.True(route.IsQueryAuth);
        Assert.Equal("api_key", route.QueryAuthParameterName);
    }

    [Fact]
    public void QueryAuth_AvailableOnGenericRouteDefinition()
    {
        var route = Define.Get<FileDownloadInput, string>("/api/data").QueryAuth();

        Assert.True(route.IsQueryAuth);
        Assert.Equal("token", route.QueryAuthParameterName);
    }

    [Fact]
    public void QueryAuth_AvailableOnFileRouteDefinitionGeneric()
    {
        var route = Define
            .File<FileDownloadInput>("/api/stream")
            .ContentType("audio/mpeg")
            .QueryAuth("session");

        Assert.True(route.IsQueryAuth);
        Assert.Equal("session", route.QueryAuthParameterName);
        Assert.Equal("audio/mpeg", route.FileContentType);
    }

    [Fact]
    public void QueryAuth_SurvivesCopyStateTo_ViaAccepts()
    {
        var route = Define.Get("/api/data").QueryAuth("tk").Accepts<FileDownloadInput>();

        Assert.True(route.IsQueryAuth);
        Assert.Equal("tk", route.QueryAuthParameterName);
    }

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
    public void R3_BuilderChain_BeforePublication_StillMutable()
    {
        // The generation-time fluent chain is unaffected
        var route = Define.File("/api/stream").ContentType("video/mp4").Summary("ok").QueryAuth();

        Assert.Equal("video/mp4", route.FileContentType);
    }

    // Dummy input type for generic variant tests
    private sealed record FileDownloadInput(string Id);
}
