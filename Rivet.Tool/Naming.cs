namespace Rivet.Tool;

internal static class Naming
{
    public static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
        {
            return name;
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

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

        // Already PascalCase — only if no delimiters present
        // Still strip invalid chars in case input contains <, >, etc.
        if (char.IsUpper(input[0])
            && !input.Contains('_') && !input.Contains('-')
            && !input.Contains('/') && !input.Contains('.')
            && !input.Contains(' '))
        {
            return StripInvalidIdentifierChars(input);
        }

        var parts = input.Split(['_', '-', ' ', '/', '.'], StringSplitOptions.RemoveEmptyEntries);
        var result = string.Concat(parts.Select(p =>
            char.ToUpperInvariant(p[0]) + p[1..]));

        // Strip any remaining characters invalid in C# identifiers
        return StripInvalidIdentifierChars(result);
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

    /// <summary>
    /// Caps an identifier to a maximum length. If truncation is needed, keeps a
    /// readable prefix and appends a short hash of the full name for uniqueness.
    /// </summary>
    public static string CapIdentifierLength(string name, int maxLength = 120)
    {
        if (string.IsNullOrEmpty(name) || name.Length <= maxLength)
        {
            return name;
        }

        // 8-char hex hash suffix + underscore separator = 9 chars reserved
        var hash = ComputeShortHash(name);
        var prefixLength = maxLength - 9;
        return name[..prefixLength] + "_" + hash;
    }

    private static string ComputeShortHash(string input)
    {
        // FNV-1a 32-bit — fast, deterministic, no crypto dependency
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in input)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return hash.ToString("x8");
        }
    }
}
