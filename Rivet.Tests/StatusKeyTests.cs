using Rivet.Tool;

namespace Rivet.Tests;

/// <summary>
/// G.10: a status key authored in C# is checked at generation by the rule <c>--from</c> applies
/// (<c>ResponseStatusValidation.IsStatusKey</c>), so a key OpenAPI can't hold stops generation
/// with RIV1120 instead of reaching the spec. <c>default</c> in any casing, which the runtime
/// accepts by design, is emitted as the lowercase field OpenAPI knows.
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
    [InlineData(".StatusKey(\"abc\")", "a status key 'abc'")]
    [InlineData(".StatusKey(\"20\")", "a status key '20'")]
    [InlineData(".Returns<ErrorDto>(\"6XX\")", "a status key '6XX'")]
    [InlineData(".Returns<ErrorDto>(\"4X0\")", "a status key '4X0'")]
    [InlineData(".ResponseContent(\"teapot\", \"text/plain\")", "a status key 'teapot'")]
    [InlineData(".Status(600)", "status code 600")]
    [InlineData(".Returns<ErrorDto>(99)", "status code 99")]
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
        Assert.Contains("status code 1000", error.Message);
    }
}
