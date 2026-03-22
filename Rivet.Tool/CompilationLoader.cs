using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace Rivet.Tool;

internal static class CompilationLoader
{
    public static async Task<Compilation?> LoadProjectAsync(string csprojPath)
    {
        MSBuildLocator.RegisterDefaults();

        using var workspace = MSBuildWorkspace.Create();

        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                Console.Error.WriteLine($"  Workspace: {e.Diagnostic.Message}");
            }
        });

        var fullPath = Path.GetFullPath(csprojPath);
        Console.Error.WriteLine($"Loading {fullPath}...");

        var project = await workspace.OpenProjectAsync(fullPath);
        var compilation = await project.GetCompilationAsync()
            ?? throw new InvalidOperationException("Failed to get compilation.");

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"Project has {errors.Count} compilation error(s):");
            foreach (var error in errors.Take(10))
            {
                Console.Error.WriteLine($"  {error}");
            }

            return null;
        }

        return compilation;
    }

    public static Compilation? CompileFromFiles(string[] paths)
    {
        var syntaxTrees = new List<SyntaxTree>();

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"File not found: {path}");
            }

            var source = File.ReadAllText(path);
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)));
        }

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new MetadataReference[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(typeof(Rivet.RivetTypeAttribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "RivetInput",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            Console.Error.WriteLine("C# compilation errors:");
            foreach (var error in errors)
            {
                Console.Error.WriteLine($"  {error}");
            }

            return null;
        }

        return compilation;
    }
}
