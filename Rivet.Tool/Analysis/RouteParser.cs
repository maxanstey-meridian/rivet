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
    /// The route parameters of a template with what ASP.NET routing reads from each token:
    /// its inline constraints in order (<c>{id:int:min(1)}</c>), the optional marker
    /// (<c>{id?}</c>), a default value (<c>{page=1}</c>) and the catch-all prefix. Doubled
    /// braces in a constraint argument (<c>regex(^\d{{4}}$)</c>, the template's escape)
    /// come back single.
    /// </summary>
    public static IReadOnlyList<RouteToken> ParseRouteTokens(string template) =>
        [.. ScanRouteParams(template).Select(token => ParseToken(token.Name, token.Body))];

    private readonly record struct RouteParamToken(string Name, string Body, int Start, int Length);

    /// <summary>
    /// Reads a token body after its name the way ASP.NET's inline parameter parser does:
    /// <c>:constraint</c> or <c>:constraint(argument)</c> repeated, then <c>=default</c> or a
    /// trailing <c>?</c>. An argument ends at the first <c>)</c> followed by another
    /// constraint, a default, the optional marker or the end, so a regex may hold
    /// parentheses and colons of its own.
    /// </summary>
    private static RouteToken ParseToken(string name, string body)
    {
        var isCatchAll = body.StartsWith('*');
        var index = body.IndexOfAny([':', '=', '?']);
        var constraints = new List<RouteConstraint>();
        string? defaultValue = null;
        var isOptional = false;

        while (index >= 0 && index < body.Length && body[index] == ':')
        {
            var nameStart = index + 1;
            var nameEnd = nameStart;
            while (nameEnd < body.Length && body[nameEnd] is not ('(' or ':' or '=' or '?'))
            {
                nameEnd++;
            }

            string? argument = null;
            index = nameEnd;
            if (nameEnd < body.Length && body[nameEnd] == '(')
            {
                var close = ClosingParenthesis(body, nameEnd + 1);
                argument = Unescape(body[(nameEnd + 1)..close]);
                index = close + 1;
            }

            constraints.Add(new RouteConstraint(body[nameStart..nameEnd].Trim(), argument));
        }

        if (index >= 0 && index < body.Length)
        {
            if (body[index] == '=')
            {
                defaultValue = body[(index + 1)..];
            }
            else if (body[index] == '?')
            {
                isOptional = true;
            }
        }

        return new RouteToken(name, constraints, isOptional, defaultValue, isCatchAll);
    }

    private static int ClosingParenthesis(string body, int start)
    {
        for (var scan = start; scan < body.Length; scan++)
        {
            if (
                body[scan] == ')'
                && (
                    scan + 1 == body.Length
                    || body[scan + 1] is ':' or '='
                    || body[scan + 1] == '?' && scan + 2 == body.Length
                )
            )
            {
                return scan;
            }
        }

        // No closing parenthesis: routing refuses the template at startup. Read the rest
        // as the argument so the constraint is still checked rather than dropped.
        return body.Length;
    }

    private static string Unescape(string argument) =>
        argument.Replace("{{", "{").Replace("}}", "}");

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
                yield return new RouteParamToken(name, body, start, end - start + 1);
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
