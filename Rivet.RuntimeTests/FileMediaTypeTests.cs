using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Rivet.RuntimeTests;

public sealed class FileMediaTypeTests
{
    [Theory]
    [InlineData(false, "image/png")]
    [InlineData(true, "image/png")]
    [InlineData(false, "image/jpeg")]
    [InlineData(true, "image/jpeg")]
    public async Task Selected_declared_representation_reaches_the_wire(bool mvc, string mediaType)
    {
        var route = Define
            .File("/image")
            .ProducesFile("image/png")
            .ResponseBinaryContent(200, "image/jpeg");
        var result = route.File([1, 2, 3], contentType: mediaType);
        var context = await ExecuteAsync(result, mvc);
        Assert.Equal(mediaType, context.Response.ContentType);
        Assert.Equal([1, 2, 3], ((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_works_for_streams_and_physical_files(bool mvc)
    {
        var route = Define
            .File("/image")
            .ProducesFile("image/png")
            .ResponseBinaryContent(200, "image/jpeg");
        var stream = new MemoryStream([4, 5, 6]);
        var streamed = await ExecuteAsync(
            route.File(stream, "image.jpg", contentType: "image/jpeg"),
            mvc
        );
        Assert.Equal("image/jpeg", streamed.Response.ContentType);
        Assert.Equal([4, 5, 6], ((MemoryStream)streamed.Response.Body).ToArray());
        Assert.False(stream.CanRead);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [4, 5, 6]);
            var physical = await ExecuteAsync(route.File(path, contentType: "image/png"), mvc);
            Assert.Equal("image/png", physical.Response.ContentType);
            Assert.Equal([4, 5, 6], ((MemoryStream)physical.Response.Body).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("image/png\r\nX-Test: injected")]
    public void Undeclared_or_malformed_selection_is_rejected(string mediaType)
    {
        var route = Define
            .File("/image")
            .ProducesFile("image/png")
            .ResponseBinaryContent(200, "image/jpeg");
        Assert.Throws<RivetContractViolationException>(() =>
            route.File([1], contentType: mediaType)
        );
    }

    [Fact]
    public void Omitted_selection_remains_ambiguous_and_json_is_not_a_file_representation()
    {
        var route = Define
            .File("/image")
            .ProducesFile("image/png")
            .ResponseBinaryContent(200, "image/jpeg");
        Assert.Throws<RivetContractViolationException>(() => route.File([1]));
        Assert.Throws<RivetContractViolationException>(() =>
            Define.Get<string>("/json").File([1], contentType: "application/json")
        );
    }

    [Fact]
    public void Every_public_definition_and_bound_variant_accepts_selection()
    {
        Assert.NotNull(
            Define.Get("/file").ProducesFile("image/png").File([1], contentType: "image/png")
        );
        Assert.NotNull(
            Define
                .Get<string>("/file")
                .ProducesFile("image/png")
                .File([1], contentType: "image/png")
        );
        Assert.NotNull(
            Define
                .Get<Input, string>("/file/{id}")
                .ProducesFile("image/png")
                .Bind(new Input("1"))
                .File([1], contentType: "image/png")
        );
        Assert.NotNull(
            Define
                .Get("/file/{id}")
                .Accepts<Input>()
                .ProducesFile("image/png")
                .Bind(new Input("1"))
                .File([1], contentType: "image/png")
        );
        Assert.NotNull(
            Define
                .File<Input>("/file/{id}")
                .ProducesFile("image/png")
                .Bind(new Input("1"))
                .File([1], contentType: "image/png")
        );
    }

    [Theory]
    [InlineData(false, "*/*", "application/pdf")]
    [InlineData(true, "*/*", "application/pdf")]
    [InlineData(false, "image/*", "image/webp")]
    [InlineData(true, "image/*", "image/webp")]
    [InlineData(true, "*/*", "text/plain; charset=utf-8")]
    public async Task A_declared_range_serves_the_concrete_runtime_type(
        bool mvc,
        string declared,
        string runtime
    )
    {
        var route = Define.File("/stored").ProducesFile(declared);
        var context = await ExecuteAsync(route.File([1, 2], contentType: runtime), mvc);
        Assert.Equal(runtime, context.Response.ContentType);
        Assert.Equal([1, 2], ((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData("image/*", "application/pdf")]
    [InlineData("image/*", "image/*")]
    [InlineData("*/*", "*/*")]
    [InlineData("*/*", "invalid")]
    [InlineData("*/*", "image/png\r\nX-Test: injected")]
    public void A_range_rejects_types_outside_it_and_wildcard_or_malformed_runtime_types(
        string declared,
        string runtime
    )
    {
        var route = Define.File("/stored").ProducesFile(declared);
        Assert.Throws<RivetContractViolationException>(() => route.File([1], contentType: runtime));
    }

    [Fact]
    public void A_range_needs_the_runtime_type()
    {
        var route = Define.File("/stored").ProducesFile("*/*");
        Assert.Throws<RivetContractViolationException>(() => route.File([1]));
    }

    [Fact]
    public void An_exact_declaration_wins_over_a_range()
    {
        var route = Define
            .File("/stored")
            .ProducesFile("*/*")
            .ResponseBinaryContent(200, "image/png");
        Assert.NotNull(route.File([1], contentType: "image/png"));
        Assert.NotNull(route.File([1], contentType: "image/gif"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inline_keeps_the_file_name_and_every_file_is_nosniff(bool mvc)
    {
        var route = Define.File("/stored").ProducesFile("*/*");

        var inline = await ExecuteAsync(
            route.File([1], "photo one.png", contentType: "image/png", inline: true),
            mvc
        );
        var inlineDisposition = inline.Response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("inline", inlineDisposition);
        Assert.Contains("photo one.png", inlineDisposition);
        Assert.Equal("nosniff", inline.Response.Headers.XContentTypeOptions.ToString());

        var attachment = await ExecuteAsync(
            route.File([1], "report.pdf", contentType: "application/pdf"),
            mvc
        );
        Assert.StartsWith("attachment", attachment.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("nosniff", attachment.Response.Headers.XContentTypeOptions.ToString());

        var bare = await ExecuteAsync(route.File([1], contentType: "image/png", inline: true), mvc);
        Assert.Equal("inline", bare.Response.Headers.ContentDisposition.ToString());
    }

    // A writer is a file whose body is produced while it is sent, such as a live transcode.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_writer_streams_its_bytes_with_the_declared_type_and_nosniff(bool mvc)
    {
        var route = Define.File("/live").ProducesFile("video/mp4");
        var context = await ExecuteAsync(
            route.File(async output => await output.WriteAsync(new byte[] { 1, 2, 3 })),
            mvc
        );
        Assert.Equal("video/mp4", context.Response.ContentType);
        Assert.Equal([1, 2, 3], ((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal(string.Empty, context.Response.Headers.AcceptRanges.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_writer_serves_the_concrete_type_inside_a_declared_range(bool mvc)
    {
        var route = Define.File("/live").ProducesFile("video/*");
        var context = await ExecuteAsync(
            route.File(
                output => output.WriteAsync(new byte[] { 1 }).AsTask(),
                contentType: "video/mp4"
            ),
            mvc
        );
        Assert.Equal("video/mp4", context.Response.ContentType);
        Assert.Throws<RivetContractViolationException>(() =>
            route.File(output => Task.CompletedTask)
        );
    }

    [Fact]
    public void A_writer_outside_the_declaration_is_rejected_before_it_runs()
    {
        var called = false;
        Task Write(Stream output)
        {
            called = true;
            return Task.CompletedTask;
        }

        Assert.Throws<RivetContractViolationException>(() =>
            Define.File("/live").ProducesFile("video/*").File(Write, contentType: "audio/mpeg")
        );
        Assert.Throws<RivetContractViolationException>(() =>
            Define.Get<string>("/json").File(Write)
        );
        Assert.False(called);
    }

    [Fact]
    public void A_null_writer_is_rejected()
    {
        var exception = Assert.Throws<RivetContractViolationException>(() =>
            Define.File("/live").File((Func<Stream, Task>)null!)
        );
        Assert.Contains("null file writer", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_writer_keeps_an_inline_name(bool mvc)
    {
        var route = Define.File("/live").ProducesFile("video/mp4");
        var context = await ExecuteAsync(
            route.File(output => Task.CompletedTask, "clip one.mp4", inline: true),
            mvc
        );
        var disposition = context.Response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("inline", disposition);
        Assert.Contains("clip one.mp4", disposition);
    }

    private static async Task<DefaultHttpContext> ExecuteAsync(RivetResult result, bool mvc)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        if (mvc)
        {
            await result
                .ToActionResult()
                .ExecuteResultAsync(
                    new ActionContext(context, new RouteData(), new ActionDescriptor())
                );
        }
        else
        {
            await result.ToResult().ExecuteAsync(context);
        }
        return context;
    }

    public sealed record Input(string Id);
}
