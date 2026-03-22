namespace Rivet.Tool;

/// <summary>
/// Canonical set of ECMAScript reserved words (strict-mode context).
/// Used by emitters to escape identifiers in generated TypeScript.
/// </summary>
internal static class TsReservedWords
{
    public static readonly HashSet<string> All = new()
    {
        "break", "case", "catch", "class", "const", "continue", "debugger",
        "default", "delete", "do", "else", "enum", "export", "extends",
        "false", "finally", "for", "function", "if", "import", "in",
        "instanceof", "new", "null", "return", "super", "switch", "this",
        "throw", "true", "try", "typeof", "var", "void", "while", "with",
        // Strict-mode reserved
        "yield", "let", "static", "implements", "interface", "package",
        "private", "protected", "public", "await", "async",
    };
}
