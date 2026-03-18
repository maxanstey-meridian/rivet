using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Rivet.Tests;

/// <summary>
/// Creates in-memory C# compilations for testing the type walker.
/// </summary>
public static class CompilationHelper
{
    private static readonly MetadataReference[] CoreReferences = GetCoreReferences();

    /// <summary>
    /// Compiles C# source with Rivet.Attributes referenced and nullable enabled.
    /// </summary>
    public static Compilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            CoreReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        // Verify no compilation errors (warnings are OK)
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            var messages = string.Join("\n", errors.Select(e => e.ToString()));
            throw new InvalidOperationException($"Test source has compilation errors:\n{messages}");
        }

        return compilation;
    }

    private static MetadataReference[] GetCoreReferences()
    {
        // Reference the runtime assemblies + Rivet.Attributes
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(typeof(RivetTypeAttribute).Assembly.Location),
        ];
    }
}
