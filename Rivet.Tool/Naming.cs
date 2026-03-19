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
    /// PascalCase from delimited segments: snake_case, kebab-case, space-separated.
    /// Already-PascalCase input passes through unchanged.
    /// </summary>
    public static string ToPascalCaseFromSegments(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        // Already PascalCase
        if (char.IsUpper(input[0]) && !input.Contains('_') && !input.Contains('-'))
        {
            return input;
        }

        var parts = input.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p =>
            char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
