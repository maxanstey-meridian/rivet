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
    // A2: optional, catch-all, default-value, and brace-containing constraint params
    [InlineData("/api/tasks/{id?}", new[] { "id" })]
    [InlineData("/files/{*path}", new[] { "path" })]
    [InlineData("/files/{**path}", new[] { "path" })]
    [InlineData("/api/x/{id=5}", new[] { "id" })]
    [InlineData("/x/{code:regex(^\\d{4}$)}", new[] { "code" })]
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
    // A2: brace-containing regex constraints must not corrupt the route,
    // and optional/catch-all/default markers normalize to bare {name}
    [InlineData("/x/{code:regex(^\\d{4}$)}", "/x/{code}")]
    [InlineData("/api/tasks/{id?}", "/api/tasks/{id}")]
    [InlineData("/files/{*path}", "/files/{path}")]
    [InlineData("/files/{**path}", "/files/{path}")]
    [InlineData("/api/x/{id=5}", "/api/x/{id}")]
    public void StripRouteConstraints(string input, string expected)
    {
        Assert.Equal(expected, RouteParser.StripRouteConstraints(input));
    }
}
