using System.Text;

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
    /// one and corrupt the re-rendered template (FABLE_ROUNDTRIP #2).
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
