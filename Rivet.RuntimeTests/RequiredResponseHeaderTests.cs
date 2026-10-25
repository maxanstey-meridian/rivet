using System.Text;
using Microsoft.AspNetCore.Http;
using static Rivet.RuntimeTests.TerminalAndAdapterTests;

namespace Rivet.RuntimeTests;

/// <summary>
/// A header declared <c>required: true</c> is a promise the adapters hold the handler to:
/// the response at that status has to carry it when the result runs.
/// </summary>
public sealed class RequiredResponseHeaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_required_header_is_refused(bool mvc)
    {
        var result = Define
            .Get("/download")
            .Status(302)
            .WithResponseHeader("Location", "Where to go", required: true)
            .Success();

        var refusal = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(result, mvc)
        );

        Assert.Contains("'GET /download'", refusal.Message);
        Assert.Contains("required response header 'Location' for status 302", refusal.Message);
        Assert.Contains("Set it on HttpContext.Response.Headers", refusal.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_header_the_handler_sets_before_returning_counts(bool mvc)
    {
        var result = Define
            .Get("/download")
            .Status(302)
            .WithResponseHeader("location", required: true)
            .Success();

        var response = await ExecuteAsync(
            result,
            mvc,
            context =>
            {
                context.Response.Headers.Location = "https://example.test/app.msi";
            }
        );

        Assert.Equal(302, response.StatusCode);
        Assert.Equal("https://example.test/app.msi", response.Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_optional_header_is_not_checked(bool mvc)
    {
        var result = Define
            .Get<Response>("/items")
            .WithResponseHeader("X-Trace")
            .WithResponseHeader<int>(200, "X-Count", required: false)
            .Success(new Response("item_1"));

        var response = await ExecuteAsync(result, mvc);

        Assert.Equal(200, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_success_overload_follows_a_later_status(bool mvc)
    {
        var route = Define
            .Post<Response>("/jobs")
            .WithResponseHeader<Uri>("Location", required: true)
            .Status(202)
            .Returns<ExactProblem>(409);

        var refusal = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(route.Success(new Response("job_1")), mvc)
        );
        var conflict = await ExecuteAsync(route.Error(409, new ExactProblem("busy")), mvc);

        Assert.Contains("'Location' for status 202", refusal.Message);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_range_status_holds_every_status_inside_it(bool mvc)
    {
        var route = Define
            .Get<Response>("/items")
            .Returns<ExactProblem>("4XX")
            .WithResponseHeaderKey("4XX", "Retry-After", required: true)
            .WithResponseHeader(503, "Retry-After", required: true)
            .Returns(503);

        var refusal = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(route.Error(429, new ExactProblem("slow down")), mvc)
        );
        var bodyless = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(route.Error(503), mvc)
        );
        var kept = await ExecuteAsync(
            route.Error(429, new ExactProblem("slow down")),
            mvc,
            context =>
            {
                context.Response.Headers.RetryAfter = "30";
            }
        );

        Assert.Contains("'Retry-After' for status 429", refusal.Message);
        Assert.Contains("'Retry-After' for status 503", bodyless.Message);
        Assert.Equal(429, kept.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_missing_header_is_named(bool mvc)
    {
        var result = Define
            .Get<Response>("/items")
            .WithResponseHeader("ETag", required: true)
            .WithResponseHeader("X-Trace", required: true)
            .Success(new Response("item_1"));

        var refusal = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(result, mvc)
        );

        Assert.Contains("required response headers 'ETag', 'X-Trace'", refusal.Message);
        Assert.Contains("doesn't carry them", refusal.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_already_started_response_without_the_header_is_refused(bool mvc)
    {
        var result = Define
            .Get("/login")
            .Status(302)
            .WithResponseHeader("Location", required: true)
            .Success();

        await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(
                result,
                mvc,
                async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    await context.Response.StartAsync();
                }
            )
        );
    }

    // Each header a file result writes from its own arguments counts, and the response
    // really carries it, so the check can't pass a header that never goes out.
    public static TheoryData<string, string, bool> FileHeaders()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var mvc in new[] { false, true })
        {
            data.Add("ETag", "bytes", mvc);
            data.Add("Last-Modified", "bytes", mvc);
            data.Add("Content-Disposition", "bytes", mvc);
            data.Add("Content-Disposition", "inline", mvc);
            data.Add("Accept-Ranges", "bytes", mvc);
            data.Add("Content-Length", "bytes", mvc);
            data.Add("Content-Length", "stream", mvc);
            data.Add("Content-Length", "physical", mvc);
            data.Add("Content-Type", "writer", mvc);
            data.Add("X-Content-Type-Options", "writer", mvc);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FileHeaders))]
    public async Task A_file_carries_the_headers_its_arguments_ask_for(
        string header,
        string source,
        bool mvc
    )
    {
        var route = Define
            .File("/report")
            .ProducesFile("text/plain")
            .WithResponseHeader(header, required: true);
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "report");
        try
        {
            var result = source switch
            {
                "bytes" => route.File(
                    Encoding.UTF8.GetBytes("report"),
                    downloadName: "report.txt",
                    enableRangeProcessing: true,
                    lastModified: DateTimeOffset.UnixEpoch,
                    entityTag: "\"v1\""
                ),
                "inline" => route.File(Encoding.UTF8.GetBytes("report"), inline: true),
                "stream" => route.File(new MemoryStream(Encoding.UTF8.GetBytes("report"))),
                "physical" => route.File(path),
                "writer" => route.File(output => output.WriteAsync("report"u8.ToArray()).AsTask()),
                _ => throw new ArgumentOutOfRangeException(nameof(source)),
            };

            var response = await ExecuteAsync(result, mvc);

            Assert.False(string.IsNullOrEmpty(response.Headers.GetValueOrDefault(header)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("ETag", false)]
    [InlineData("ETag", true)]
    [InlineData("Content-Length", false)]
    [InlineData("Content-Length", true)]
    public async Task A_file_header_its_arguments_dont_ask_for_is_refused(string header, bool mvc)
    {
        var route = Define.File("/report").WithResponseHeader(header, required: true);

        var refusal = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(
                route.File(output => output.WriteAsync("report"u8.ToArray()).AsTask()),
                mvc
            )
        );
        await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(route.File(new NonSeekableStream("report"u8.ToArray())), mvc)
        );

        Assert.Contains($"'{header}' for status 200", refusal.Message);
        Assert.Contains("File(...)'s entityTag:", refusal.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stream_refused_for_a_missing_header_is_disposed(bool mvc)
    {
        var stream = new TrackingStream("report"u8.ToArray());
        var result = Define.File("/report").WithResponseHeader("ETag", required: true).File(stream);

        await Assert.ThrowsAsync<RivetContractViolationException>(() => ExecuteAsync(result, mvc));

        Assert.True(stream.IsDisposed);
    }
}
