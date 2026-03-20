using Rivet.Tool.Analysis;

namespace Rivet.Tests;

public sealed class RouteParserTests
{
    [Theory]
    [InlineData("/api/tasks/{id}", new[] { "id" })]
    [InlineData("/api/{orgId}/tasks/{taskId}", new[] { "orgId", "taskId" })]
    [InlineData("/api/tasks/{id:guid}", new[] { "id" })]
    [InlineData("/api/tasks/{id:guid:required}", new[] { "id" })]
    [InlineData("/api/tasks", new string[0])]
    [InlineData("/{a}/{b}/{c}", new[] { "a", "b", "c" })]
    public void ParseRouteParamNames(string template, string[] expected)
    {
        var result = RouteParser.ParseRouteParamNames(template);
        Assert.Equal(expected.ToHashSet(StringComparer.OrdinalIgnoreCase), result);
    }

    [Theory]
    [InlineData("/api/tasks/{id:guid}", "/api/tasks/{id}")]
    [InlineData("/api/{orgId:guid}/tasks/{taskId:int}", "/api/{orgId}/tasks/{taskId}")]
    [InlineData("/api/tasks/{id}", "/api/tasks/{id}")]
    [InlineData("/api/tasks", "/api/tasks")]
    public void StripRouteConstraints(string input, string expected)
    {
        Assert.Equal(expected, RouteParser.StripRouteConstraints(input));
    }
}
