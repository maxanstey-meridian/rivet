using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Rivet.RuntimeTests;

public sealed class TerminalAndAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_result_has_the_same_wire_shape_in_both_adapters(bool mvc)
    {
        var result = Define.Post<Response>("/items").Success(new Response("item_1"));

        var response = await ExecuteAsync(result, mvc);

        Assert.Equal(StatusCodes.Status201Created, response.StatusCode);
        Assert.StartsWith("application/json", response.ContentType);
        Assert.Equal(
            "item_1",
            JsonDocument.Parse(response.Body).RootElement.GetProperty("id").GetString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_text_result_has_the_same_wire_shape_in_both_adapters(bool mvc)
    {
        var result = Define.Get<string>("/text").ProducesContentType("text/plain").Success("hello");

        var response = await ExecuteAsync(result, mvc);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.ContentType);
        Assert.Equal("hello", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task Explicit_primary_content_type_wins_over_a_multi_content_map()
    {
        var result = Define
            .Get<string>("/content")
            .ResponseContent<string>(200, "text/plain")
            .ResponseContent<string>(200, "text/html")
            .ProducesContentType("text/html")
            .Success("<strong>hello</strong>");

        var mvc = await ExecuteAsync(result, mvc: true);
        var minimal = await ExecuteAsync(result, mvc: false);

        Assert.Equal("text/html", mvc.ContentType);
        Assert.Equal("text/html", minimal.ContentType);
        Assert.Equal("<strong>hello</strong>", Encoding.UTF8.GetString(mvc.Body));
        Assert.Equal(mvc.Body, minimal.Body);
    }

    [Fact]
    public void Ambiguous_non_json_content_map_is_rejected()
    {
        var route = Define
            .Get<string>("/content")
            .ResponseContent<string>(200, "text/plain")
            .ResponseContent<string>(200, "text/html");

        var exception = Assert.Throws<RivetContractViolationException>(() =>
            route.Success("hello")
        );

        Assert.Contains("multiple non-JSON representations", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_json_suffix_is_serialized_as_json(bool mvc)
    {
        var result = Define
            .Get<Response>("/problem")
            .ProducesContentType("application/problem+json")
            .Success(new Response("problem"));

        var response = await ExecuteAsync(result, mvc);

        Assert.Equal(
            "problem",
            JsonDocument.Parse(response.Body).RootElement.GetProperty("id").GetString()
        );
        Assert.Equal("application/problem+json", response.ContentType);
    }

    [Fact]
    public void Exact_error_beats_range_and_default()
    {
        var route = Define
            .Get<Response>("/items")
            .Returns<ExactProblem>(404)
            .Returns<RangeProblem>("4XX")
            .Returns<DefaultProblem>("default");

        Assert.NotNull(route.Error(404, new ExactProblem("exact")));
        Assert.NotNull(route.Error(429, new RangeProblem("range")));
        Assert.NotNull(route.Error(503, new DefaultProblem("default")));
        Assert.Throws<RivetContractViolationException>(() =>
            route.Error(404, new RangeProblem("wrong precedence"))
        );
    }

    [Fact]
    public void Numeric_string_status_collides_with_success_in_both_orderings()
    {
        var statusFirst = Assert.Throws<InvalidOperationException>(() =>
            Define.Get<Response>("/status-first").Status(202).Returns<ExactProblem>("202")
        );
        var returnsFirst = Assert.Throws<InvalidOperationException>(() =>
            Define.Get<Response>("/returns-first").Returns<ExactProblem>("202").Status(202)
        );

        Assert.Contains("success and error responses cannot share", statusFirst.Message);
        Assert.Contains("success and error responses cannot share", returnsFirst.Message);
    }

    [Fact]
    public void Numeric_string_and_integer_returns_are_canonical_duplicates()
    {
        var stringFirst = Assert.Throws<InvalidOperationException>(() =>
            Define.Get<Response>("/string-first").Returns<ExactProblem>("202").Returns(202)
        );
        var integerFirst = Assert.Throws<InvalidOperationException>(() =>
            Define.Get<Response>("/integer-first").Returns(202).Returns<ExactProblem>("202")
        );

        Assert.Contains("already declared", stringFirst.Message);
        Assert.Contains("already declared", integerFirst.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(600)]
    [InlineData(999)]
    public void Integer_and_numeric_string_returns_reject_invalid_exact_statuses(int statusCode)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Define.Get<Response>($"/integer-{statusCode}").Returns(statusCode)
        );
        Assert.Throws<InvalidOperationException>(() =>
            Define.Get<Response>($"/string-{statusCode}").Returns(statusCode.ToString())
        );
    }

    [Theory]
    [InlineData("6XX")]
    [InlineData("invalid")]
    [InlineData("0202")]
    [InlineData("+202")]
    [InlineData(" 202")]
    [InlineData("202 ")]
    public void Invalid_non_exact_status_keys_remain_loud(string statusKey)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Define.Get<Response>("/invalid-key").Returns(statusKey)
        );

        Assert.Contains($"'{statusKey}'", exception.Message);
    }

    [Fact]
    public void Range_and_default_keys_remain_case_insensitive()
    {
        var route = Define
            .Get<Response>("/case-insensitive")
            .Returns<RangeProblem>("4xx")
            .Returns<DefaultProblem>("DEFAULT");

        Assert.NotNull(route.Error(429, new RangeProblem("range")));
        Assert.NotNull(route.Error(503, new DefaultProblem("default")));
    }

    [Fact]
    public void Numeric_string_returns_preserve_the_declared_status_key()
    {
        var route = Define.Get<Response>("/metadata").Returns<ExactProblem>("202");

        Assert.Equal("202", Assert.Single(route.RouteErrorResponses!).StatusKey);
        Assert.NotNull(route.Error(202, new ExactProblem("accepted")));
    }

    [Fact]
    public void Example_mutators_are_frozen_after_publication()
    {
        var route = Define.Get<Response>("/examples");
        _ = route.Success(new Response("published"));

        Assert.Throws<InvalidOperationException>(() => route.RequestExampleJson("{}"));
        Assert.Throws<InvalidOperationException>(() => route.RequestExampleRef("example", "{}"));
        Assert.Throws<InvalidOperationException>(() => route.ResponseExampleJson(200, "{}"));
        Assert.Throws<InvalidOperationException>(() => route.ResponseExampleJson("200", "{}"));
        Assert.Throws<InvalidOperationException>(() =>
            route.ResponseExampleRef(200, "example", "{}")
        );
        Assert.Throws<InvalidOperationException>(() =>
            route.ResponseExampleRef("200", "example", "{}")
        );
    }

    [Fact]
    public void Default_return_can_precede_a_different_success_status()
    {
        var route = Define
            .Get<Response>("/default-before-status")
            .Returns<DefaultProblem>("default")
            .Status(202);

        Assert.NotNull(route.Success(new Response("accepted")));
    }

    [Fact]
    public void Suppressed_success_rejects_success_but_keeps_declared_errors()
    {
        var route = Define
            .Get("/items")
            .SuppressImplicitResponse()
            .Returns<DefaultProblem>("default");

        Assert.Throws<RivetContractViolationException>(() => route.Success());
        Assert.NotNull(route.Error(503, new DefaultProblem("unavailable")));
    }

    [Fact]
    public void Bind_rejects_a_null_contract_input()
    {
        var route = Define.Post<Input, Response>("/items");

        Assert.Throws<ArgumentNullException>(() => route.Bind(null!));
    }

    [Fact]
    public void Body_terminal_rejects_a_status_that_forbids_a_body()
    {
        var route = Define.Get<Response>("/items").Status(StatusCodes.Status204NoContent);

        Assert.Throws<RivetContractViolationException>(() => route.Success(new Response("item_1")));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(199)]
    [InlineData(204)]
    [InlineData(205)]
    [InlineData(304)]
    public void File_terminal_rejects_a_status_that_forbids_a_body(int statusCode)
    {
        var route = Define.File("/file").Status(statusCode);

        var exception = Assert.Throws<RivetContractViolationException>(() => route.File([1, 2, 3]));

        Assert.Contains($"status {statusCode}", exception.Message);
    }

    [Fact]
    public void Publication_rejects_content_payload_that_conflicts_with_route_payload()
    {
        var route = Define
            .Get<Response>("/items")
            .ResponseContent<ExactProblem>(200, "application/json");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            route.Success(new Response("item_1"))
        );

        Assert.Contains(typeof(Response).FullName!, exception.Message);
        Assert.Contains(typeof(ExactProblem).FullName!, exception.Message);
    }

    [Fact]
    public void Publication_rejects_content_payload_that_conflicts_with_returns_payload()
    {
        var route = Define
            .Get("/items")
            .Returns<ExactProblem>(400)
            .ResponseContent<DefaultProblem>(400, "application/problem+json");

        var exception = Assert.Throws<InvalidOperationException>(() => route.Success());

        Assert.Contains(typeof(ExactProblem).FullName!, exception.Message);
        Assert.Contains(typeof(DefaultProblem).FullName!, exception.Message);
    }

    [Fact]
    public void Publication_rejects_multiple_content_payload_types()
    {
        var route = Define
            .Get("/items")
            .ResponseContent<Response>(200, "application/json")
            .ResponseContent<ExactProblem>(200, "application/problem+json");

        var exception = Assert.Throws<InvalidOperationException>(() => route.Success());

        Assert.Contains("multiple content payload types", exception.Message);
    }

    [Fact]
    public void Null_typed_success_and_error_payloads_are_rejected()
    {
        var route = Define.Get<Response>("/items").Returns<ExactProblem>(400);

        var success = Assert.Throws<RivetContractViolationException>(() => route.Success(null!));
        var error = Assert.Throws<RivetContractViolationException>(() =>
            route.Error<ExactProblem>(400, null!)
        );

        Assert.Contains("null payload", success.Message);
        Assert.Contains("null payload", error.Message);
    }

    [Fact]
    public void Schema_less_response_content_cannot_be_executed_as_bodyless()
    {
        var success = Define.Get("/events").ResponseContent(200, "text/event-stream");
        var error = Define.Get("/items").Returns(400).ResponseContent(400, "text/plain");

        Assert.Throws<RivetContractViolationException>(() => success.Success());
        Assert.Throws<RivetContractViolationException>(() => error.Error(400));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(600)]
    public void Invalid_success_status_is_rejected_during_fluent_construction(int statusCode)
    {
        Assert.Throws<InvalidOperationException>(() => Define.Get("/items").Status(statusCode));
    }

    [Fact]
    public void Concurrent_first_publication_is_idempotent_and_freezes_the_definition()
    {
        var route = Define.Get<Response>("/items");

        Parallel.For(
            0,
            64,
            index =>
            {
                Assert.NotNull(route.Success(new Response(index.ToString())));
            }
        );

        Assert.Throws<InvalidOperationException>(() => route.Summary("too late"));
    }

    [Fact]
    public async Task Publication_and_mutation_synchronize_on_the_same_gate()
    {
        var route = Define.Get<Response>("/items");
        var gate = route
            .GetType()
            .BaseType!.GetField("_publicationLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(route)!;
        using var ready = new CountdownEvent(2);

        Monitor.Enter(gate);
        Task<Exception?> mutation;
        Task<Exception?> publication;
        try
        {
            mutation = Task.Run(() =>
            {
                ready.Signal();
                return (Exception?)Record.Exception(() => route.Summary("racing"));
            });
            publication = Task.Run(() =>
            {
                ready.Signal();
                return (Exception?)Record.Exception(() => route.Success(new Response("item_1")));
            });

            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Thread.Sleep(TimeSpan.FromMilliseconds(100));
            Assert.False(mutation.IsCompleted);
            Assert.False(publication.IsCompleted);
        }
        finally
        {
            Monitor.Exit(gate);
        }

        var exceptions = await Task.WhenAll(mutation, publication);
        Assert.Null(exceptions[1]);
        Assert.True(exceptions[0] is null or InvalidOperationException);
        Assert.Throws<InvalidOperationException>(() => route.Description("too late"));
    }

    [Fact]
    public void Accepts_rejects_conversion_after_publication()
    {
        var route = Define.Put("/items");
        _ = route.Success();

        Assert.Throws<InvalidOperationException>(() => route.Accepts<Input>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_binary_content_establishes_file_capability(bool mvc)
    {
        var result = Define
            .Get("/file")
            .ResponseBinaryContent(200, "application/pdf")
            .File([1, 2, 3]);

        var response = await ExecuteAsync(result, mvc);

        Assert.Equal("application/pdf", response.ContentType);
        Assert.Equal([1, 2, 3], response.Body);
    }

    [Fact]
    public void Multiple_binary_success_representations_make_file_ambiguous()
    {
        var route = Define
            .Get("/file")
            .ResponseBinaryContent(200, "application/pdf")
            .ResponseBinaryContent(200, "image/png");

        var exception = Assert.Throws<RivetContractViolationException>(() => route.File([1, 2, 3]));

        Assert.Contains("ambiguous", exception.Message);
        Assert.Contains("application/pdf", exception.Message);
        Assert.Contains("image/png", exception.Message);
    }

    [Fact]
    public void Mixed_json_and_binary_success_requires_the_file_terminal()
    {
        var route = Define
            .Get<Response>("/items")
            .ResponseContent<Response>(200, "application/json")
            .ResponseBinaryContent(200, "application/pdf");

        Assert.Throws<RivetContractViolationException>(() => route.Success(new Response("item_1")));
        Assert.NotNull(route.File([1, 2, 3]));
    }

    [Fact]
    public void Binary_alternate_errors_are_rejected_for_bodyless_and_typed_payloads()
    {
        var bodyless = Define
            .Get("/items")
            .Returns(400)
            .ResponseBinaryContent(400, "application/octet-stream");
        var typed = Define
            .Get("/other-items")
            .Returns<ExactProblem>(400)
            .ResponseBinaryContent(400, "application/octet-stream");

        var bodylessException = Assert.Throws<RivetContractViolationException>(() =>
            bodyless.Error(400)
        );
        var typedException = Assert.Throws<RivetContractViolationException>(() =>
            typed.Error(400, new ExactProblem("bad"))
        );

        Assert.Contains("binary alternate response", bodylessException.Message);
        Assert.Contains("binary alternate response", typedException.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Byte_file_delegates_range_processing_to_the_host(bool mvc)
    {
        var result = Define
            .File("/file")
            .ContentType("text/plain")
            .File(Encoding.UTF8.GetBytes("abcdef"), enableRangeProcessing: true);

        var response = await ExecuteAsync(
            result,
            mvc,
            context =>
            {
                context.Request.Method = HttpMethods.Get;
                context.Request.Headers.Range = "bytes=1-3";
            }
        );

        Assert.Equal(StatusCodes.Status206PartialContent, response.StatusCode);
        Assert.Equal("bcd", Encoding.UTF8.GetString(response.Body));
        Assert.Equal("bytes 1-3/6", response.ContentRange);
        Assert.Equal("bytes", response.AcceptRanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsatisfiable_file_range_returns_416(bool mvc)
    {
        var result = Define
            .File("/file")
            .ContentType("text/plain")
            .File(Encoding.UTF8.GetBytes("abcdef"), enableRangeProcessing: true);

        var response = await ExecuteAsync(
            result,
            mvc,
            context =>
            {
                context.Request.Method = HttpMethods.Get;
                context.Request.Headers.Range = "bytes=20-30";
            }
        );

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */6", response.ContentRange);
        Assert.Empty(response.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_file_transfers_ownership_to_the_host(bool mvc)
    {
        var stream = new TrackingStream(Encoding.UTF8.GetBytes("stream"));
        var result = Define.File("/file").ContentType("text/plain").File(stream, "stream.txt");

        var response = await ExecuteAsync(result, mvc);

        Assert.Equal("stream", Encoding.UTF8.GetString(response.Body));
        Assert.True(stream.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_file_is_sent_by_the_host(bool mvc)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "physical");
            var result = Define.File("/file").ContentType("text/plain").File(path, "physical.txt");

            var response = await ExecuteAsync(result, mvc);

            Assert.Equal("physical", Encoding.UTF8.GetString(response.Body));
            Assert.Contains("physical.txt", response.ContentDisposition);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_validators_are_emitted_by_the_host(bool mvc)
    {
        var lastModified = new DateTimeOffset(2026, 7, 13, 10, 30, 0, TimeSpan.Zero);
        var result = Define
            .File("/file")
            .File([1, 2, 3], lastModified: lastModified, entityTag: "\"v1\"");

        var response = await ExecuteAsync(result, mvc);

        Assert.Equal("\"v1\"", response.ETag);
        Assert.Contains("Mon, 13 Jul 2026 10:30:00 GMT", response.LastModified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_non_get_binary_route_preserves_its_declared_baseline_status(bool mvc)
    {
        var result = Define
            .Post<Input, Response>("/documents")
            .ProducesFile("application/pdf")
            .Bind(new Input("request"))
            .File([0x25, 0x50, 0x44, 0x46], "document.pdf");

        var response = await ExecuteAsync(result, mvc);

        Assert.Equal(StatusCodes.Status201Created, response.StatusCode);
        Assert.Equal("application/pdf", response.ContentType);
        Assert.Contains("document.pdf", response.ContentDisposition);
    }

    [Fact]
    public void File_metadata_is_validated_at_terminal_construction()
    {
        var route = Define.File("/file");

        Assert.Throws<RivetContractViolationException>(() =>
            route.File([1, 2, 3], entityTag: "not-an-etag")
        );
        Assert.Throws<RivetContractViolationException>(() =>
            route.File(new NonSeekableStream([1, 2, 3]), enableRangeProcessing: true)
        );
        Assert.Throws<RivetContractViolationException>(() => route.File("relative/file.txt"));
        Assert.Throws<RivetContractViolationException>(() => route.File((byte[])null!));
        Assert.Throws<RivetContractViolationException>(() => route.File((Stream)null!));
        Assert.Throws<RivetContractViolationException>(() => route.File(" "));
        Assert.Throws<RivetContractViolationException>(() => route.File([1], entityTag: "*"));
        Assert.Throws<InvalidOperationException>(() =>
            Define.File("/empty-content-type").ContentType(" ")
        );
        Assert.Throws<InvalidOperationException>(() =>
            Define.File("/malformed-content-type").ContentType("invalid")
        );
        Assert.Throws<RivetContractViolationException>(() =>
            Define.Get("/imported-malformed").ResponseBinaryContent(200, "invalid").File([1])
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_serialization_uses_the_declared_polymorphic_type(bool mvc)
    {
        var result = Define.Get<Animal>("/animal").Success(new Dog("Fido", "collie"));

        var response = await ExecuteAsync(result, mvc);
        var json = JsonDocument.Parse(response.Body).RootElement;

        Assert.Equal("dog", json.GetProperty("kind").GetString());
        Assert.Equal("Fido", json.GetProperty("name").GetString());
        Assert.Equal("collie", json.GetProperty("breed").GetString());
    }

    [Fact]
    public void Unregistered_polymorphic_subtype_is_rejected_at_terminal_construction()
    {
        var route = Define.Get<UnregisteredAnimal>("/animal");

        Assert.Throws<RivetContractViolationException>(() =>
            route.Success(new UnregisteredDog("Fido"))
        );
    }

    [Fact]
    public void Native_framework_results_cannot_be_terminal_payloads()
    {
        var success = Define.Get<IResult>("/native");
        var error = Define.Get("/native-error").Returns<IActionResult>(400);

        Assert.Throws<RivetContractViolationException>(() => success.Success(TypedResults.Ok()));
        Assert.Throws<RivetContractViolationException>(() => error.Error(400, new OkResult()));
    }

    [Fact]
    public void Non_utf8_json_charset_is_rejected_before_adaptation()
    {
        var route = Define
            .Get<Response>("/utf16")
            .ProducesContentType("application/json; charset=utf-16");

        Assert.Throws<RivetContractViolationException>(() => route.Success(new Response("item_1")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_established_matching_bodyless_response_is_preserved(bool mvc)
    {
        var result = Define.Get("/login").Status(302).Success();

        var response = await ExecuteAsync(
            result,
            mvc,
            async context =>
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = "/identity";
                await context.Response.StartAsync();
            }
        );

        Assert.Equal(StatusCodes.Status302Found, response.StatusCode);
        Assert.Equal("/identity", response.Location);
        Assert.Empty(response.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_established_mismatched_response_is_rejected(bool mvc)
    {
        var result = Define.Get("/login").Status(302).Success();

        await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(
                result,
                mvc,
                context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
            )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_headers_and_cookies_survive_normal_adaptation(bool mvc)
    {
        var result = Define.Get<Response>("/items").Success(new Response("item_1"));

        var response = await ExecuteAsync(
            result,
            mvc,
            context =>
            {
                context.Response.Headers["X-Rivet-Test"] = "preserved";
                context.Response.Cookies.Append("session", "value");
            }
        );

        Assert.Equal("preserved", response.CustomHeader);
        Assert.Contains("session=value", response.SetCookie);
    }

    private static Task<ResponseObservation> ExecuteAsync(
        RivetResult result,
        bool mvc,
        Action<HttpContext>? configure
    ) =>
        ExecuteAsync(
            result,
            mvc,
            configure is null
                ? null
                : context =>
                {
                    configure(context);
                    return Task.CompletedTask;
                }
        );

    private static async Task<ResponseObservation> ExecuteAsync(
        RivetResult result,
        bool mvc,
        Func<HttpContext, Task>? configure = null
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddControllers();
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        if (configure is not null)
        {
            await configure(context);
        }

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

        return new ResponseObservation(
            context.Response.StatusCode,
            ((MemoryStream)context.Response.Body).ToArray(),
            context.Response.ContentType,
            context.Response.Headers.ContentRange.ToString(),
            context.Response.Headers.AcceptRanges.ToString(),
            context.Response.Headers.Location.ToString(),
            context.Response.Headers.ContentDisposition.ToString(),
            context.Response.Headers.ETag.ToString(),
            context.Response.Headers.LastModified.ToString(),
            context.Response.Headers["X-Rivet-Test"].ToString(),
            context.Response.Headers.SetCookie.ToString()
        );
    }

    public sealed record Input(string Value);

    public sealed record Response(string Id);

    public sealed record ExactProblem(string Message);

    public sealed record RangeProblem(string Message);

    public sealed record DefaultProblem(string Message);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Dog), "dog")]
    public abstract record Animal(string Name);

    public sealed record Dog(string Name, string Breed) : Animal(Name);

    [JsonPolymorphic]
    public abstract record UnregisteredAnimal;

    public sealed record UnregisteredDog(string Name) : UnregisteredAnimal;

    private sealed record ResponseObservation(
        int StatusCode,
        byte[] Body,
        string? ContentType,
        string ContentRange,
        string AcceptRanges,
        string Location,
        string ContentDisposition,
        string ETag,
        string LastModified,
        string CustomHeader,
        string SetCookie
    );

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    private sealed class TrackingStream(byte[] content) : MemoryStream(content)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await base.DisposeAsync();
        }
    }
}
