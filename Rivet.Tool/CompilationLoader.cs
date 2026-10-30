using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace Rivet.Tool;

internal static class CompilationLoader
{
    public static async Task<Compilation> LoadProjectAsync(string csprojPath)
    {
        var fullPath = Path.GetFullPath(csprojPath);
        if (!File.Exists(fullPath))
        {
            throw Diagnostics.Error(
                Diagnostics.InputPathNotFound,
                $"--project: no file at {fullPath}"
            );
        }

        EnsureMSBuildRegistered();

        using var workspace = MSBuildWorkspace.Create();

        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                Console.Error.WriteLine($"  Workspace: {e.Diagnostic.Message}");
            }
        });

        Console.Error.WriteLine($"Loading {fullPath}...");

        Project project;
        try
        {
            project = await workspace.OpenProjectAsync(fullPath);
        }
        catch (InvalidOperationException exception)
        {
            // MSBuildWorkspace opens only a project file of a language it has loaded: a
            // solution, another language's project, or a stray file is refused here.
            throw Diagnostics.Error(
                Diagnostics.CompilationFailed,
                $"--project: MSBuild can't open {fullPath} as a C# project: {exception.Message}"
            );
        }

        var compilation =
            await project.GetCompilationAsync()
            ?? throw Diagnostics.Error(
                Diagnostics.CompilationFailed,
                $"{fullPath} yields no compilation, so there's no C# to read"
            );

        return WithoutErrors(compilation);
    }

    /// <summary>The compilation, or <c>RIV1126</c> listing its first errors.</summary>
    private static Compilation WithoutErrors(Compilation compilation)
    {
        var errors = Errors(compilation);

        if (errors.Count == 0)
        {
            return compilation;
        }

        throw Diagnostics.Error(
            Diagnostics.CompilationFailed,
            $"the C# has {errors.Count} compiler error(s), so Rivet can't read it:"
                + string.Concat(errors.Take(10).Select(error => $"{Environment.NewLine}  {error}"))
        );
    }

    private static List<Diagnostic> Errors(Compilation compilation) =>
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    private static void EnsureMSBuildRegistered()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        try
        {
            MSBuildLocator.RegisterDefaults();
            return;
        }
        catch (InvalidOperationException)
        {
            // Fall back to explicit SDK probing for published apphosts that
            // do not present a default discoverable SDK installation.
        }

        foreach (var sdkPath in GetSdkCandidatePaths())
        {
            if (!File.Exists(Path.Combine(sdkPath, "MSBuild.dll")))
            {
                continue;
            }

            Environment.SetEnvironmentVariable("MSBuildSDKsPath", Path.Combine(sdkPath, "Sdks"));
            MSBuildLocator.RegisterMSBuildPath(sdkPath);
            return;
        }

        throw Diagnostics.Error(
            Diagnostics.CompilationFailed,
            "no MSBuild instance could be found to load the project; install the .NET SDK, or point DOTNET_ROOT at it"
        );
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

            var sdkDirectories = Directory
                .GetDirectories(sdkRoot)
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

        foreach (
            var root in new[]
            {
                "/usr/local/share/dotnet",
                "/usr/local/share/dotnet/x64",
                "/opt/homebrew/share/dotnet",
                "/opt/homebrew/opt/dotnet/libexec",
                "/usr/share/dotnet",
                "/usr/lib/dotnet",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dotnet"
                ),
            }
        )
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

        foreach (
            var cellarInstall in Directory
                .GetDirectories(homebrewCellar)
                .OrderByDescending(static path => ParseVersion(Path.GetFileName(path)))
        )
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
        return Version.TryParse(value, out var version) ? version : new Version(0, 0);
    }

    /// <summary>
    /// A positional directory argument means "every .cs file under it" — an
    /// imported contract set is thousands of files (cloudflare: 11k), which
    /// overflows ARG_MAX long before a shell glob could deliver them.
    /// </summary>
    private static IEnumerable<string> ExpandDirectories(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (
                    var file in Directory
                        .EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                        .OrderBy(static file => file, StringComparer.Ordinal)
                )
                {
                    yield return file;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// The loose-file path must offer the same surface a default csproj would:
    /// a curated assembly shortlist silently fails on anything the importer
    /// itself emits (System.Text.Json polymorphism, DataAnnotations
    /// constraints, IFormFile multipart params), so reference the whole
    /// NETCore.App shared framework plus its AspNetCore.App sibling.
    /// </summary>
    private static List<MetadataReference> CollectFrameworkReferences()
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(Rivet.RivetTypeAttribute).Assembly.Location),
        };

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var frameworkDirs = new List<string> { runtimeDir };

        // runtimeDir is <root>/shared/Microsoft.NETCore.App/<version>; the
        // ASP.NET shared framework sits beside it at the same version when
        // installed. Best-effort: loose-file users without it just cannot
        // compile IFormFile params, same as before.
        var runtimeVersion = Path.GetFileName(runtimeDir);
        var sharedRoot = Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir));
        if (sharedRoot is not null)
        {
            var aspNetDir = Path.Combine(sharedRoot, "Microsoft.AspNetCore.App", runtimeVersion);
            if (Directory.Exists(aspNetDir))
            {
                frameworkDirs.Add(aspNetDir);
            }
        }

        foreach (var frameworkDir in frameworkDirs)
        {
            foreach (var dll in Directory.GetFiles(frameworkDir, "*.dll"))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(dll));
                }
                catch (BadImageFormatException)
                {
                    // Native or resource-only binary — not referenceable.
                }
            }
        }

        return references;
    }

    public static Compilation CompileFromFiles(string[] paths)
    {
        var syntaxTrees = new List<SyntaxTree>();

        foreach (var path in ExpandDirectories(paths))
        {
            if (!File.Exists(path))
            {
                throw Diagnostics.Error(
                    Diagnostics.InputPathNotFound,
                    $"source file: no file at {path}"
                );
            }

            var source = File.ReadAllText(path);
            syntaxTrees.Add(
                CSharpSyntaxTree.ParseText(
                    source,
                    new CSharpParseOptions(LanguageVersion.Latest),
                    path
                )
            );
        }

        var references = CollectFrameworkReferences();

        var library = CSharpCompilation.Create(
            "RivetInput",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        var app = library.WithOptions(
            library.Options.WithOutputKind(OutputKind.ConsoleApplication)
        );

        // Files holding a program's entry point compile as the app they come from, so --check
        // sees the setup that runs at startup (Compilation.GetEntryPoint), as it does for a
        // project. Top-level statements compile only as an app (a library refuses them, CS8805),
        // and the parser puts them only directly under the compilation unit.
        if (
            syntaxTrees.Any(tree =>
                tree.GetCompilationUnitRoot().Members.Any(member => member is GlobalStatementSyntax)
            )
        )
        {
            return WithoutErrors(app);
        }

        // A Main counts only when the compiler takes it as the entry point and the files compile
        // as that app; files it can't build as one app (two viable Mains, an async void Main, or a
        // lone Main without an entry-point signature) stay the library they compiled as.
        if (app.GetEntryPoint(CancellationToken.None) is not null && Errors(app).Count == 0)
        {
            return app;
        }

        return WithoutErrors(library);
    }
}
