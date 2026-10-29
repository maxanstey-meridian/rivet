using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using Rivet.Tool;

namespace Rivet.Tests;

/// <summary>
/// G.10: a status key authored in C# is checked at generation by the rule the runtime refuses
/// it by (<c>ResponseStatusKey</c>), so a key OpenAPI can't hold stops generation with RIV1120
/// instead of reaching the spec. <c>default</c> in any casing, which the runtime accepts by
/// design, is emitted as the lowercase field OpenAPI knows.
/// </summary>
public sealed class StatusKeyTests
{
    private static string Contract(string calls) =>
        $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetType]
            public sealed record ErrorDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks"){{calls}};
            }
            """;

    private static IReadOnlyList<string> ResponseKeys(string source)
    {
        using var document = CompilationHelper.EmitOpenApi(source);
        return document
            .RootElement.GetProperty("paths")
            .GetProperty("/api/tasks")
            .GetProperty("get")
            .GetProperty("responses")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();
    }

    [Theory]
    [InlineData(".StatusKey(\"abc\")", "the status key 'abc'")]
    [InlineData(".StatusKey(\"20\")", "the status key '20'")]
    [InlineData(".Returns<ErrorDto>(\"6XX\")", "the status key '6XX'")]
    [InlineData(".Returns<ErrorDto>(\"4X0\")", "the status key '4X0'")]
    [InlineData(".ResponseContent(\"teapot\", \"text/plain\")", "the status key 'teapot'")]
    [InlineData(".Status(600)", "the status code 600")]
    [InlineData(".Returns<ErrorDto>(99)", "the status code 99")]
    public void A_key_OpenAPI_cannot_hold_stops_generation(string calls, string named)
    {
        var error = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(Contract(calls))
        );

        Assert.StartsWith("error RIV1120:", error.Message);
        Assert.Contains("contract endpoint 'getTask'", error.Message);
        Assert.Contains(named, error.Message);
    }

    [Theory]
    [InlineData(".StatusKey(\"DEFAULT\")")]
    [InlineData(".StatusKey(\"Default\")")]
    public void Default_in_any_casing_is_emitted_as_default(string calls)
    {
        Assert.Equal(["default"], ResponseKeys(Contract(calls)));
    }

    [Fact]
    public void A_returned_default_in_any_casing_is_emitted_as_default()
    {
        Assert.Equal(["200", "default"], ResponseKeys(Contract(".Returns<ErrorDto>(\"DEFAULT\")")));
    }

    [Fact]
    public void A_range_keeps_its_spelling()
    {
        Assert.Equal(["200", "4xx"], ResponseKeys(Contract(".Returns<ErrorDto>(\"4xx\")")));
    }

    [Fact]
    public void A_controller_status_code_OpenAPI_cannot_hold_stops_generation()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("")]
                [ProducesResponseType(typeof(TaskDto), 200)]
                [ProducesResponseType(1000)]
                public IActionResult Get() => Ok();
            }
            """;

        var error = Assert.Throws<RivetUserException>(() => CompilationHelper.WalkMerged(source));

        Assert.StartsWith("error RIV1120:", error.Message);
        Assert.Contains("'TasksController.Get' ([ProducesResponseType])", error.Message);
        Assert.Contains("the status code 1000", error.Message);
    }

    private sealed record TaskDto(string Id);

    private sealed record ErrorDto(string Message);

    /// <summary>
    /// Every status-taking builder call, as C# for the tool to walk and as the runtime call
    /// itself. <c>{0}</c> is the status argument.
    /// </summary>
    private static readonly (string Source, Func<string, object> Runtime)[] _keyCalls =
    [
        (".StatusKey({0})", key => Define.Get<TaskDto>("/t").StatusKey(key)),
        (".Returns<ErrorDto>({0})", key => Define.Get<TaskDto>("/t").Returns<ErrorDto>(key)),
        (".Returns({0})", key => Define.Get<TaskDto>("/t").Returns(key)),
        (
            ".ResponseContent({0}, \"text/plain\")",
            key => Define.Get<TaskDto>("/t").ResponseContent(key, "text/plain")
        ),
        (
            ".ResponseContent<ErrorDto>({0}, \"application/problem+json\")",
            key =>
                Define.Get<TaskDto>("/t").ResponseContent<ErrorDto>(key, "application/problem+json")
        ),
        (
            ".ResponseBinaryContent({0}, \"application/pdf\")",
            key => Define.Get<TaskDto>("/t").ResponseBinaryContent(key, "application/pdf")
        ),
        (
            ".WithResponseHeaderKey({0}, \"X-Id\", required: true)",
            key => Define.Get<TaskDto>("/t").WithResponseHeaderKey(key, "X-Id", required: true)
        ),
        (
            ".WithResponseHeaderKey<string>({0}, \"X-Id\")",
            key => Define.Get<TaskDto>("/t").WithResponseHeaderKey<string>(key, "X-Id")
        ),
        (
            ".ResponseExampleJson({0}, \"{{}}\")",
            key => Define.Get<TaskDto>("/t").ResponseExampleJson(key, "{}")
        ),
        (
            ".ResponseExampleRef({0}, \"task\", \"{{}}\")",
            key => Define.Get<TaskDto>("/t").ResponseExampleRef(key, "task", "{}")
        ),
    ];

    private static readonly (string Source, Func<int, object> Runtime)[] _codeCalls =
    [
        (".Status({0})", code => Define.Get<TaskDto>("/t").Status(code)),
        (".Returns<ErrorDto>({0})", code => Define.Get<TaskDto>("/t").Returns<ErrorDto>(code)),
        (".Returns({0})", code => Define.Get<TaskDto>("/t").Returns(code)),
        (
            ".ResponseContent({0}, \"text/plain\")",
            code => Define.Get<TaskDto>("/t").ResponseContent(code, "text/plain")
        ),
        (
            ".ResponseContent<ErrorDto>({0}, \"application/problem+json\")",
            code =>
                Define
                    .Get<TaskDto>("/t")
                    .ResponseContent<ErrorDto>(code, "application/problem+json")
        ),
        (
            ".ResponseBinaryContent({0}, \"application/pdf\")",
            code => Define.Get<TaskDto>("/t").ResponseBinaryContent(code, "application/pdf")
        ),
        (
            ".WithResponseHeader({0}, \"X-Id\", required: true)",
            code => Define.Get<TaskDto>("/t").WithResponseHeader(code, "X-Id", required: true)
        ),
        (
            ".WithResponseHeader<string>({0}, \"X-Id\")",
            code => Define.Get<TaskDto>("/t").WithResponseHeader<string>(code, "X-Id")
        ),
        (
            ".ResponseExampleJson({0}, \"{{}}\")",
            code => Define.Get<TaskDto>("/t").ResponseExampleJson(code, "{}")
        ),
        (
            ".ResponseExampleRef({0}, \"task\", \"{{}}\")",
            code => Define.Get<TaskDto>("/t").ResponseExampleRef(code, "task", "{}")
        ),
    ];

    /// <summary>
    /// Decision 34: generation and the runtime refuse the same keys, on every call that takes
    /// one. The tool walks the call as C#; the runtime makes it.
    /// </summary>
    [Theory]
    [InlineData("404", true)]
    [InlineData("599", true)]
    [InlineData("4XX", true)]
    [InlineData("4xx", true)]
    [InlineData("4xX", true)]
    [InlineData("default", true)]
    [InlineData("DEFAULT", true)]
    [InlineData("abc", false)]
    [InlineData("20", false)]
    [InlineData("6XX", false)]
    [InlineData("4X0", false)]
    [InlineData("600", false)]
    [InlineData("099", false)]
    [InlineData("+202", false)]
    [InlineData(" 202", false)]
    [InlineData("404\n", false)]
    [InlineData("", false)]
    public void Tool_and_runtime_agree_on_which_status_keys_a_call_takes(string key, bool taken)
    {
        var literal = SymbolDisplay.FormatLiteral(key, quote: true);
        foreach (var (source, runtime) in _keyCalls)
        {
            var call = string.Format(CultureInfo.InvariantCulture, source, literal);
            Assert.True(
                ToolTakes(call) == taken,
                $"generation {(taken ? "refuses" : "takes")} {call}"
            );
            Assert.True(
                RuntimeTakes(() => runtime(key)) == taken,
                $"the runtime {(taken ? "refuses" : "takes")} {call}"
            );
        }
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(599, true)]
    [InlineData(99, false)]
    [InlineData(600, false)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1000, false)]
    public void Tool_and_runtime_agree_on_which_status_codes_a_call_takes(int code, bool taken)
    {
        foreach (var (source, runtime) in _codeCalls)
        {
            var call = string.Format(CultureInfo.InvariantCulture, source, code);
            Assert.True(
                ToolTakes(call) == taken,
                $"generation {(taken ? "refuses" : "takes")} {call}"
            );
            Assert.True(
                RuntimeTakes(() => runtime(code)) == taken,
                $"the runtime {(taken ? "refuses" : "takes")} {call}"
            );
        }
    }

    /// <summary>
    /// Whether generation takes the key: anything but RIV1120, since a call made on its own
    /// can stop generation for another reason (an example on a status nothing declares).
    /// </summary>
    private static bool ToolTakes(string calls)
    {
        try
        {
            CompilationHelper.WalkContract(Contract(calls));
            return true;
        }
        catch (RivetUserException error)
        {
            return !error.Message.StartsWith("error RIV1120:", StringComparison.Ordinal);
        }
    }

    private static bool RuntimeTakes(Action call)
    {
        try
        {
            call();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
