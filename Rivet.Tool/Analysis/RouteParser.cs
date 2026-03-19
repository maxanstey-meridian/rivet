using System.Text.RegularExpressions;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Shared route template utilities used by both EndpointWalker and ContractWalker.
/// </summary>
public static partial class RouteParser
{
    /// <summary>
    /// Extracts route parameter names from a route template.
    /// e.g. "/api/tasks/{id}/comments/{commentId}" → {"id", "commentId"}
    /// </summary>
    public static HashSet<string> ParseRouteParamNames(string template)
    {
        return RouteParamRegex().Matches(template)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips route constraints: {id:guid} → {id}, {slug:minlength(3)} → {slug}
    /// </summary>
    public static string StripRouteConstraints(string route)
    {
        return RouteConstraintRegex().Replace(route, "{$1}");
    }

    /// <summary>
    /// Matches route constraints like {id:guid}, {slug:minlength(3)}, {page:int}
    /// </summary>
    [GeneratedRegex(@"\{(\w+):[^}]+\}")]
    internal static partial Regex RouteConstraintRegex();

    /// <summary>
    /// Matches route params: {id}, {id:guid}, {slug:minlength(3)}
    /// </summary>
    [GeneratedRegex(@"\{(\w+)(?::[^}]+)?\}")]
    internal static partial Regex RouteParamRegex();
}
