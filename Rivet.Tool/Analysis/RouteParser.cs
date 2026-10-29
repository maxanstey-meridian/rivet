using System.Text;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Shared route template utilities used by both EndpointWalker and ContractWalker.
/// Handles the full ASP.NET Core route parameter grammar:
/// {id}, {id?}, {*path}, {**path}, {id=5}, {id:int}, {id:guid:required},
/// and brace-containing constraints like {code:regex(^\d{4}$)} (scanned with brace balancing).
/// </summary>
public static class RouteParser
{
    /// <summary>
    /// Extracts route parameter names from a route template.
    /// e.g. "/api/tasks/{id}/comments/{commentId}" → {"id", "commentId"}
    /// </summary>
    public static HashSet<string> ParseRouteParamNames(string template)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in ScanRouteParams(template))
        {
            names.Add(token.Name);
        }

        return names;
    }

    /// <summary>
    /// Normalizes route parameters to bare {name} placeholders:
    /// strips constraints ({id:guid} → {id}), default values ({id=5} → {id}),
    /// optional markers ({id?} → {id}), and catch-all prefixes ({*path}/{**path} → {path}).
    /// </summary>
    public static string StripRouteConstraints(string route)
    {
        var builder = new StringBuilder(route.Length);
        var position = 0;

        foreach (var token in ScanRouteParams(route))
        {
            builder.Append(route, position, token.Start - position);
            builder.Append('{').Append(token.Name).Append('}');
            position = token.Start + token.Length;
        }

        builder.Append(route, position, route.Length - position);
        return builder.ToString();
    }

    /// <summary>
    /// Erases parameter names so templates OpenAPI treats as one path compare equal:
    /// "/items/{id}" and "/items/{itemId:int}" both become "/items/{}".
    /// </summary>
    public static string EraseParameterNames(string route)
    {
        var builder = new StringBuilder(route.Length);
        var position = 0;

        foreach (var token in ScanRouteParams(route))
        {
            builder.Append(route, position, token.Start - position).Append("{}");
            position = token.Start + token.Length;
        }

        builder.Append(route, position, route.Length - position);
        return builder.ToString();
    }

    /// <summary>
    /// The route parameters of a template as ASP.NET routing reads them, by routing's own
    /// parser (<see cref="RoutePatternFactory.Parse(string)"/>): each token's inline
    /// constraints in order (<c>{id:int:min(1)}</c>), the optional marker (<c>{id?}</c>), a
    /// default value (<c>{page=1}</c>) and the catch-all prefix, with a constraint argument's
    /// doubled braces (<c>regex(^\d{{4}}$)</c>, the template's escape) come back single.
    /// A template routing refuses at startup throws its <see cref="RoutePatternException"/>,
    /// except for a token that repeats an earlier one's name: routing refuses that too, but
    /// an imported OpenAPI path can repeat one, so each repeat comes back as its own token
    /// under its own name rather than stopping the read.
    /// </summary>
    public static IReadOnlyList<RouteToken> ParseRouteTokens(string template)
    {
        var (parseable, repeats) = RenameRepeats(template);
        return
        [
            .. RoutePatternFactory
                .Parse(parseable)
                .Parameters.Select(parameter => new RouteToken(
                    repeats.GetValueOrDefault(parameter.Name, parameter.Name),
                    [
                        .. parameter.ParameterPolicies.Select(policy =>
                            Constraint(policy.Content ?? "")
                        ),
                    ],
                    parameter.IsOptional,
                    parameter.Default?.ToString(),
                    parameter.IsCatchAll
                )),
        ];
    }

    // The template with each repeated parameter name (compared ignoring case, as routing
    // does) replaced by a placeholder routing accepts, and the placeholder-to-name map.
    private static (string Template, Dictionary<string, string> Repeats) RenameRepeats(
        string template
    )
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repeats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder(template.Length);
        var position = 0;
        foreach (var token in ScanRouteParams(template))
        {
            if (seen.Add(token.Name))
            {
                continue;
            }

            var placeholder = $"rivetRepeat{repeats.Count}";
            while (
                repeats.ContainsKey(placeholder)
                || template.Contains(placeholder, StringComparison.OrdinalIgnoreCase)
            )
            {
                placeholder += "_";
            }

            repeats[placeholder] = token.Name;
            var nameStart = template.IndexOf(token.Name, token.Start, StringComparison.Ordinal);
            builder.Append(template, position, nameStart - position).Append(placeholder);
            position = nameStart + token.Name.Length;
        }

        builder.Append(template, position, template.Length - position);
        return (builder.ToString(), repeats);
    }

    // An inline policy's text: "int", or "min(1)" with its argument.
    private static RouteConstraint Constraint(string content) =>
        content.IndexOf('(') is var open and >= 0 && content.EndsWith(')')
            ? new RouteConstraint(content[..open], content[(open + 1)..^1])
            : new RouteConstraint(content, null);

    private readonly record struct RouteParamToken(string Name, int Start, int Length);

    /// <summary>
    /// Scans a route template for {…} parameter tokens, using brace-depth tracking so
    /// constraints containing braces (e.g. regex quantifiers) don't terminate the token early.
    /// </summary>
    private static IEnumerable<RouteParamToken> ScanRouteParams(string template)
    {
        var index = 0;

        while (index < template.Length)
        {
            if (template[index] != '{')
            {
                index++;
                continue;
            }

            var start = index;
            var depth = 0;
            var end = -1;

            for (var scan = index; scan < template.Length; scan++)
            {
                if (template[scan] == '{')
                {
                    depth++;
                }
                else if (template[scan] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = scan;
                        break;
                    }
                }
            }

            if (end < 0)
            {
                // Unbalanced brace — nothing more to match
                yield break;
            }

            var body = template.Substring(start + 1, end - start - 1);
            var name = ExtractParamName(body);

            if (name.Length > 0)
            {
                yield return new RouteParamToken(name, start, end - start + 1);
            }

            index = end + 1;
        }
    }

    /// <summary>
    /// Extracts the parameter name from a token body per the ASP.NET grammar:
    /// [*|**]name[:constraint…][?] or name=default. The name ends at the first
    /// ':' (constraint), '=' (default value), or '?' (optional marker) — and at
    /// nothing else: hyphenated names like {enterprise-team} (legal in OpenAPI
    /// path templates and common in imported specs) are one token, not a name
    /// truncated at the hyphen. Truncation here used to collide two params into
    /// one and corrupt the re-rendered template.
    /// </summary>
    private static string ExtractParamName(string body)
    {
        // Catch-all prefixes: {*path} and {**path}
        var nameStart = 0;
        while (nameStart < body.Length && body[nameStart] == '*')
        {
            nameStart++;
        }

        var nameEnd = nameStart;
        while (nameEnd < body.Length && body[nameEnd] is not (':' or '=' or '?'))
        {
            nameEnd++;
        }

        return body[nameStart..nameEnd].Trim();
    }

    /// <summary>
    /// Canonical form for matching a route token against a C# property name:
    /// case-folded with '_'/'-' separators stripped, so {thing_id} ↔ ThingId and
    /// {enterprise-team} ↔ EnterpriseTeam match without either side renaming the
    /// wire artifact. The route token always keeps its original spelling.
    /// </summary>
    public static string NormalizeForMatching(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var length = 0;
        foreach (var c in name)
        {
            if (c is '_' or '-')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }
}

/// <summary>A route parameter token and what ASP.NET routing reads from it.</summary>
public sealed record RouteToken(
    string Name,
    IReadOnlyList<RouteConstraint> Constraints,
    bool IsOptional,
    string? DefaultValue,
    bool IsCatchAll
);

/// <summary>One inline route constraint: <c>int</c>, or <c>min(1)</c> with its argument.</summary>
public sealed record RouteConstraint(string Name, string? Argument)
{
    public override string ToString() => Argument is null ? Name : $"{Name}({Argument})";
}
