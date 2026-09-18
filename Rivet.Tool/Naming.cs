using System.Text.Json;

namespace Rivet.Tool;

internal static class Naming
{
    public static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        return JsonNamingPolicy.CamelCase.ConvertName(name);
    }

    /// <summary>
    /// Casing policies for enum wire values declared by
    /// [RivetEnumNamingPolicy]. Applied to the exact C# member name.
    /// </summary>
    public static string ToPolicyCase(string name, RivetNamingPolicy policy) =>
        policy switch
        {
            RivetNamingPolicy.LowerCase => name.ToLowerInvariant(),
            RivetNamingPolicy.CamelCase => ToCamelCase(name),
            RivetNamingPolicy.SnakeCase => ToWordCase(name, '_'),
            RivetNamingPolicy.KebabCase => ToWordCase(name, '-'),
            _ => name,
        };

    /// <summary>
    /// Lowercase from word boundaries detected in the member name: a lowercase or
    /// digit followed by an uppercase letter, a digit run after letters, a letter
    /// run after digits, and a trailing acronym before a lowercase word
    /// ("RestrictedByway2A" → "restricted_byway2_a" / "restricted-byway2-a").
    /// Existing underscores pass through unchanged.
    /// </summary>
    private static string ToWordCase(string name, char separator)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var startsNewWord =
                i > 0
                && (
                    (char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                    || (
                        char.IsUpper(c)
                        && char.IsUpper(name[i - 1])
                        && i + 1 < name.Length
                        && char.IsLower(name[i + 1])
                    )
                    || (char.IsDigit(c) && i > 0 && char.IsLetter(name[i - 1]))
                    || (char.IsLetter(c) && char.IsLower(c) && i > 0 && char.IsDigit(name[i - 1]))
                );
            if (startsNewWord)
            {
                sb.Append(separator);
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
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
