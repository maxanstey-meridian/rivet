using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

return await Run(args);

static async Task<int> Run(string[] args)
{
    var parsed = ParseArgs(args);

    if (parsed is null)
    {
        PrintUsage();
        return 1;
    }

    var (projectPath, outputDir, mode, files) = parsed.Value;

    Compilation compilation;

    if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
    {
        compilation = await LoadProjectCompilation(projectPath);
    }
    else
    {
        // Legacy mode: compile standalone .cs files
        compilation = CompileFiles(parsed.Value.Files);
    }

    var walker = TypeWalker.Create(compilation);
    var endpoints = EndpointWalker.Walk(compilation, walker);
    var definitions = walker.Definitions.Values.ToList();

    var typesOutput = TypeEmitter.Emit(definitions);
    var clientOutput = endpoints.Count > 0
        ? ClientEmitter.Emit(endpoints, definitions)
        : null;

    if (outputDir is not null)
    {
        Directory.CreateDirectory(outputDir);

        var typesPath = Path.Combine(outputDir, "types.ts");
        await File.WriteAllTextAsync(typesPath, typesOutput);
        Console.WriteLine($"  types.ts → {typesPath}");

        if (clientOutput is not null)
        {
            var clientPath = Path.Combine(outputDir, "client.ts");
            await File.WriteAllTextAsync(clientPath, clientOutput);
            Console.WriteLine($"  client.ts → {clientPath}");
        }

        Console.WriteLine($"Generated {definitions.Count} types, {endpoints.Count} endpoints.");
    }
    else
    {
        // Print to stdout (preview mode)
        Console.WriteLine("=== types.ts ===");
        Console.Write(typesOutput);

        if (clientOutput is not null)
        {
            Console.WriteLine();
            Console.WriteLine("=== client.ts ===");
            Console.Write(clientOutput);
        }
    }

    return 0;
}

static async Task<Compilation> LoadProjectCompilation(string csprojPath)
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
    }

    return compilation;
}

static Compilation CompileFiles(string[] paths)
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
    }

    return compilation;
}

static (string ProjectPath, string? OutputDir, string Mode, string[] Files)? ParseArgs(string[] args)
{
    if (args.Length == 0)
    {
        return null;
    }

    string? projectPath = null;
    string? outputDir = null;
    var mode = "generate";
    var files = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--project" or "-p" when i + 1 < args.Length:
                projectPath = args[++i];
                break;
            case "--output" or "-o" when i + 1 < args.Length:
                outputDir = args[++i];
                break;
            default:
                files.Add(args[i]);
                break;
        }
    }

    // If no --project flag, first file arg is the project path
    projectPath ??= files.FirstOrDefault();

    if (projectPath is null)
    {
        return null;
    }

    return (projectPath, outputDir, mode, files.ToArray());
}

static void PrintUsage()
{
    Console.Error.WriteLine("Rivet — C# to TypeScript type generator");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --project Rivet.Tool -- --project <path.csproj> --output <dir>");
    Console.Error.WriteLine("  dotnet run --project Rivet.Tool -- <file.cs> [file2.cs ...]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  -p, --project <path>   Path to .csproj file");
    Console.Error.WriteLine("  -o, --output <dir>     Output directory (omit for stdout preview)");
}
