using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace Rivet.Tool;

internal static class CompilationLoader
{
    public static async Task<Compilation?> LoadProjectAsync(string csprojPath)
    {
        EnsureMSBuildRegistered();

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

    private static void EnsureMSBuildRegistered()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        foreach (var sdkPath in GetSdkCandidatePaths())
        {
            if (!File.Exists(Path.Combine(sdkPath, "MSBuild.dll")))
            {
                continue;
            }

            MSBuildLocator.RegisterMSBuildPath(sdkPath);
            return;
        }

        MSBuildLocator.RegisterDefaults();
    }

    private static IEnumerable<string> GetSdkCandidatePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in GetDotNetRoots())
        {
            var sdkRoot = Path.Combine(root, "sdk");
            if (!Directory.Exists(sdkRoot))
            {
                continue;
            }

            var sdkDirectories = Directory.GetDirectories(sdkRoot)
                .OrderByDescending(static path => ParseVersion(Path.GetFileName(path)));

            foreach (var sdkDirectory in sdkDirectories)
            {
                var fullPath = Path.GetFullPath(sdkDirectory);
                if (seen.Add(fullPath))
                {
                    yield return fullPath;
                }
            }
        }
    }

    private static IEnumerable<string> GetDotNetRoots()
    {
        foreach (var envVar in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64" })
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }

        foreach (var root in new[]
                 {
                     "/usr/local/share/dotnet",
                     "/usr/local/share/dotnet/x64",
                     "/opt/homebrew/share/dotnet",
                     "/opt/homebrew/opt/dotnet/libexec",
                 })
        {
            if (Directory.Exists(root))
            {
                yield return root;
            }
        }

        const string homebrewCellar = "/opt/homebrew/Cellar/dotnet";
        if (!Directory.Exists(homebrewCellar))
        {
            yield break;
        }

        foreach (var cellarInstall in Directory.GetDirectories(homebrewCellar)
                     .OrderByDescending(static path => ParseVersion(Path.GetFileName(path))))
        {
            var libexecPath = Path.Combine(cellarInstall, "libexec");
            if (Directory.Exists(libexecPath))
            {
                yield return libexecPath;
            }
        }
    }

    private static Version ParseVersion(string value)
    {
        return Version.TryParse(value, out var version)
            ? version
            : new Version(0, 0);
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
