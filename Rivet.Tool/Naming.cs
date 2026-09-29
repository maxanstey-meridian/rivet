using System.Text.Json;

namespace Rivet.Tool;

/// <summary>
/// Casing conventions the tool recognizes on a string-wire enum's
/// [JsonConverter] declaration. The wire value casing must match the runtime
/// converter of the same name exactly — each STJ policy here is the same
/// object the matching Rivet*EnumConverter passes to its base class, and
/// CamelCase is the shared JsonNamingPolicy.CamelCase instance on both sides.
/// </summary>
internal enum RivetNamingPolicy
{
    LowerCase,
    CamelCase,
    SnakeCase,
    KebabCase,
}

internal static class Naming
{
    /// <summary>
    /// Rivet's one disambiguation scheme, shared by import and emit: <c>Name</c>, then
    /// <c>Name_2</c>, <c>Name_3</c>… The separator keeps a suffix from reading as part of a
    /// name that already ends in a digit.
    /// </summary>
    public static IEnumerable<string> NameCandidates(string baseName)
    {
        yield return baseName;
        for (var suffix = 2; ; suffix++)
        {
            yield return $"{baseName}_{suffix}";
        }
    }

    /// <summary>The first free <see cref="NameCandidates"/> entry, claimed in <paramref name="used"/>.</summary>
    public static string UniqueName(string baseName, ISet<string> used) =>
        NameCandidates(baseName).First(used.Add);

    public static string ToCamelCase(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary>
    /// Casing policies for enum wire values declared by a
    /// [RivetCamelCaseEnumConverter<...>]-family converter. Applied to the exact
    /// C# member name. Each policy delegates to the same System.Text.Json
    /// naming-policy instance the matching runtime converter passes to its base
    /// class — emission and runtime wire casing are one algorithm, not two that
    /// must be kept in sync (a hand-rolled word-boundary heuristic diverged from
    /// SnakeCaseLower/KebabCaseLower at digit boundaries).
    /// </summary>
    public static string ToPolicyCase(string name, RivetNamingPolicy policy) =>
        policy switch
        {
            RivetNamingPolicy.LowerCase => name.ToLowerInvariant(),
            RivetNamingPolicy.CamelCase => ToCamelCase(name),
            RivetNamingPolicy.SnakeCase => JsonNamingPolicy.SnakeCaseLower.ConvertName(name),
            RivetNamingPolicy.KebabCase => JsonNamingPolicy.KebabCaseLower.ConvertName(name),
            _ => name,
        };

    /// <summary>
    /// The camelCase token carried on the IR ("namingPolicy") and the OpenAPI
    /// extension ("x-rivet-enum-naming-policy") for a policy — the import
    /// round-trip's single carrier.
    /// </summary>
    public static string ToPolicyToken(RivetNamingPolicy policy) => ToCamelCase(policy.ToString());

    /// <summary>
    /// Parses a naming-policy token; false for anything that is not a declared
    /// policy — the import path refuses to guess an unknown token's casing.
    /// </summary>
    public static bool TryPolicyFromToken(string token, out RivetNamingPolicy policy)
    {
        foreach (var candidate in Enum.GetValues<RivetNamingPolicy>())
        {
            if (ToPolicyToken(candidate) == token)
            {
                policy = candidate;
                return true;
            }
        }

        policy = default;
        return false;
    }

    /// <summary>
    /// PascalCased property names that cannot be record members: object/record
    /// machinery the compiler reserves. A positional parameter with one of
    /// these names is CS8866 at emit time (it resolves to object.Equals etc.);
    /// Deconstruct/EqualityContract collide with record-synthesized members.
    /// </summary>
    public static bool IsReservedRecordMemberName(string name) =>
        name
            is "Equals"
                or "GetHashCode"
                or "GetType"
                or "ToString"
                or "Clone"
                or "Deconstruct"
                or "EqualityContract";

    public static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsUpper(name[0]))
        {
            return name;
        }

        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// PascalCase from delimited segments: snake_case, kebab-case, space-separated,
    /// slash-separated, dot-separated. Strips characters that are invalid in C# identifiers.
    /// Already-PascalCase input passes through unchanged.
    /// </summary>
    public static string ToPascalCaseFromSegments(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "_";
        }

        // Already PascalCase — only if no delimiters present (or only trailing _N suffix)
        // Still strip invalid chars in case input contains <, >, etc.
        if (
            char.IsUpper(input[0])
            && !input.Contains('-')
            && !input.Contains('/')
            && !input.Contains('.')
            && !input.Contains(' ')
        )
        {
            // Allow underscore only as a trailing dedup suffix (_2, _3, etc.)
            var underscoreIdx = input.IndexOf('_');
            if (
                underscoreIdx < 0
                || (
                    underscoreIdx > 0
                    && underscoreIdx + 1 < input.Length
                    && input[(underscoreIdx + 1)..].All(char.IsDigit)
                )
            )
            {
                return StripInvalidIdentifierChars(input);
            }
        }

        var parts = input.Split(['_', '-', ' ', '/', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "_";
        }

        var result = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

        // Strip any remaining characters invalid in C# identifiers
        var stripped = StripInvalidIdentifierChars(result);
        return string.IsNullOrEmpty(stripped) ? "_" : stripped;
    }

    /// <summary>
    /// Removes characters that are not valid in a C# identifier.
    /// If the result starts with a digit, prepends an underscore.
    /// </summary>
    public static string StripInvalidIdentifierChars(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var chars = input.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
        var result = new string(chars);

        if (result.Length == 0)
        {
            return "_";
        }

        if (char.IsDigit(result[0]))
        {
            result = "_" + result;
        }

        // Ensure first letter is uppercase to maintain PascalCase after stripping
        if (result.Length > 0 && char.IsLower(result[0]))
        {
            result = char.ToUpperInvariant(result[0]) + result[1..];
        }

        return result;
    }
}
