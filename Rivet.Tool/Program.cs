using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Rivet.Tool.Analysis;
using Rivet.Tool.Compile;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

return await Run(args);

static async Task<int> Run(string[] args)
{
    var options = ParseArgs(args);

    if (options is null)
    {
        PrintUsage();
        return 1;
    }

    // Import mode: OpenAPI → C# contracts
    if (options.FromOpenApiPath is not null)
    {
        return RunImport(options);
    }

    var sw = Stopwatch.StartNew();
    var (projectPath, outputDir, mode, files) = (options.ProjectPath, options.OutputDir, options.Mode, options.Files);

    if (mode == "compile" && outputDir is null)
    {
        Console.Error.WriteLine("Error: --compile requires --output <dir>.");
        return 1;
    }

    Compilation compilation;

    if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
    {
        compilation = await LoadProjectCompilation(projectPath);
    }
    else
    {
        compilation = CompileFiles(options.Files);
    }

    var walker = TypeWalker.Create(compilation);
    var endpoints = EndpointWalker.Walk(compilation, walker);
    var contractEndpoints = ContractWalker.Walk(compilation, walker);

    if (options.Check)
    {
        var coverageWarnings = CoverageChecker.Check(compilation, contractEndpoints);
        foreach (var w in coverageWarnings)
        {
            Console.Error.WriteLine($"warning: [{w.Kind}] {w.ContractName}.{w.FieldName}: expected {w.Expected}, got {w.Actual}");
        }

        var totalFields = contractEndpoints.Count;
        var missingCount = coverageWarnings.Count(w => w.Kind == CoverageWarningKind.MissingImplementation);
        var coveredCount = totalFields - missingCount;
        var mismatchCount = coverageWarnings.Count - missingCount;

        if (coverageWarnings.Count == 0)
        {
            Console.Error.WriteLine($"Coverage: {coveredCount}/{totalFields} endpoints covered. All OK.");
        }
        else
        {
            Console.Error.WriteLine($"Coverage: {coveredCount}/{totalFields} endpoints covered, {mismatchCount} mismatch(es), {missingCount} missing.");
        }

        if (coverageWarnings.Count > 0 && outputDir is null)
        {
            return 1;
        }
    }

    // Merge: contract endpoints win on (ControllerName, Name) collision
    var seen = new HashSet<(string, string)>(
        contractEndpoints.Select(e => (e.ControllerName, e.Name)));
    var merged = new List<TsEndpointDefinition>(contractEndpoints);
    foreach (var ep in endpoints)
    {
        if (seen.Add((ep.ControllerName, ep.Name)))
        {
            merged.Add(ep);
        }
    }

    endpoints = merged;

    if (options.Routes)
    {
        PrintRoutes(merged);
        return 0;
    }

    var definitions = walker.Definitions.Values.ToList();

    var brands = walker.Brands.Values.ToList();
    var enums = walker.Enums;
    var typeGrouping = TypeGrouper.Group(definitions, brands, enums, walker.TypeNamespaces);
    var typeFileMap = typeGrouping.BuildTypeFileMap();
    var validatorsOutput = endpoints.Count > 0
        ? ValidatorEmitter.Emit(endpoints, typeFileMap)
        : null;

    if (outputDir is not null)
    {
        Directory.CreateDirectory(outputDir);

        // Emit grouped type files into types/ directory
        var typesDir = Path.Combine(outputDir, "types");
        Directory.CreateDirectory(typesDir);

        var typeFileNames = new List<string>();
        foreach (var group in typeGrouping.Groups)
        {
            var content = TypeEmitter.EmitGroupFile(group);
            var filePath = Path.Combine(typesDir, $"{group.FileName}.ts");
            await File.WriteAllTextAsync(filePath, content);
            Console.WriteLine($"  types/{group.FileName}.ts → {filePath}");
            typeFileNames.Add(group.FileName);
        }

        // Barrel for types/
        var typesBarrel = TypeEmitter.EmitNamespacedBarrel(typeFileNames);
        var typesBarrelPath = Path.Combine(typesDir, "index.ts");
        await File.WriteAllTextAsync(typesBarrelPath, typesBarrel);
        Console.WriteLine($"  types/index.ts → {typesBarrelPath}");

        // Emit shared rivet.ts
        if (endpoints.Count > 0)
        {
            var rivetBasePath = Path.Combine(outputDir, "rivet.ts");
            await File.WriteAllTextAsync(rivetBasePath, ClientEmitter.EmitRivetBase());
            Console.WriteLine($"  rivet.ts → {rivetBasePath}");

            // Emit per-controller client files
            var clientDir = Path.Combine(outputDir, "client");
            Directory.CreateDirectory(clientDir);

            var controllerGroups = ClientEmitter.GroupByController(endpoints);
            var clientFileNames = new List<string>();
            foreach (var (controllerName, controllerEndpoints) in controllerGroups)
            {
                var clientContent = ClientEmitter.EmitControllerClient(controllerName, controllerEndpoints, typeFileMap);
                var clientPath = Path.Combine(clientDir, $"{controllerName}.ts");
                await File.WriteAllTextAsync(clientPath, clientContent);
                Console.WriteLine($"  client/{controllerName}.ts → {clientPath}");
                clientFileNames.Add(controllerName);
            }

            // Barrel for client/
            var clientBarrel = TypeEmitter.EmitNamespacedBarrel(clientFileNames);
            var clientBarrelPath = Path.Combine(clientDir, "index.ts");
            await File.WriteAllTextAsync(clientBarrelPath, clientBarrel);
            Console.WriteLine($"  client/index.ts → {clientBarrelPath}");
        }

        if (validatorsOutput is not null && validatorsOutput.Length > 0)
        {
            var validatorsPath = Path.Combine(outputDir, "validators.ts");
            await File.WriteAllTextAsync(validatorsPath, validatorsOutput);
            Console.WriteLine($"  validators.ts → {validatorsPath}");
        }

        // OpenAPI spec
        if (options.OpenApiPath is not null)
        {
            var securityConfig = SecurityParser.Parse(options.DefaultSecurity);
            var openApiJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, securityConfig);
            var openApiFilePath = Path.Combine(outputDir, options.OpenApiPath);
            await File.WriteAllTextAsync(openApiFilePath, openApiJson);
            Console.WriteLine($"  {options.OpenApiPath} → {openApiFilePath}");
        }

        Console.WriteLine($"Generated {definitions.Count} types, {endpoints.Count} endpoints in {FormatElapsed(sw.Elapsed)}.");

        // Compile step: run tsc + typia, then re-emit validated client
        if (mode == "compile")
        {
            Console.WriteLine();
            Console.WriteLine("Compiling validators...");

            var brandOverrides = brands
                .Select(b => (b.Name, TypeEmitter.EmitTypeString(b.Inner)))
                .ToList();
            var compileOk = await TypiaCompiler.CompileAsync(outputDir, brandOverrides);
            if (!compileOk)
            {
                Console.Error.WriteLine("Typia compilation failed.");
                return 1;
            }

            // Re-emit per-controller clients with validator assertions wired in
            if (endpoints.Count > 0)
            {
                var clientDir = Path.Combine(outputDir, "client");
                var controllerGroups = ClientEmitter.GroupByController(endpoints);
                foreach (var (controllerName, controllerEndpoints) in controllerGroups)
                {
                    var validatedContent = ClientEmitter.EmitControllerClient(
                        controllerName, controllerEndpoints, typeFileMap, validated: true);
                    var clientPath = Path.Combine(clientDir, $"{controllerName}.ts");
                    await File.WriteAllTextAsync(clientPath, validatedContent);
                    Console.WriteLine($"  client/{controllerName}.ts → {clientPath} (validated)");
                }
            }

            Console.WriteLine($"Compile complete in {FormatElapsed(sw.Elapsed)}.");
        }
    }
    else if (!options.Quiet)
    {
        // Print to stdout (preview mode)
        foreach (var group in typeGrouping.Groups)
        {
            Console.WriteLine($"=== types/{group.FileName}.ts ===");
            Console.Write(TypeEmitter.EmitGroupFile(group));
            Console.WriteLine();
        }

        Console.WriteLine("=== types/index.ts ===");
        Console.Write(TypeEmitter.EmitNamespacedBarrel(typeGrouping.Groups.Select(g => g.FileName).ToList()));

        if (endpoints.Count > 0)
        {
            var controllerGroups = ClientEmitter.GroupByController(endpoints);
            foreach (var (controllerName, controllerEndpoints) in controllerGroups)
            {
                Console.WriteLine();
                Console.WriteLine($"=== client/{controllerName}.ts ===");
                Console.Write(ClientEmitter.EmitControllerClient(controllerName, controllerEndpoints, typeFileMap));
            }
        }

        if (validatorsOutput is not null && validatorsOutput.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== validators.ts ===");
            Console.Write(validatorsOutput);
        }

        if (options.OpenApiPath is not null)
        {
            var securityConfig = SecurityParser.Parse(options.DefaultSecurity);
            var openApiJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, securityConfig);
            Console.WriteLine();
            Console.WriteLine($"=== {options.OpenApiPath} ===");
            Console.Write(openApiJson);
        }

    }

    return 0;
}

static async Task<Compilation> LoadProjectCompilation(string csprojPath)
{
    // MSBuildLocator.RegisterDefaults() fails when the tool targets a different
    // framework than the SDK on PATH (e.g. net8.0 tool, net10 Homebrew SDK).
    // Probe all known dotnet roots and register the newest MSBuild we can find.
    var registered = false;

    foreach (var root in GetDotnetRoots())
    {
        var sdkDir = Path.Combine(root, "sdk");
        if (!Directory.Exists(sdkDir))
        {
            continue;
        }

        // Find the newest SDK version directory
        var newest = Directory.GetDirectories(sdkDir)
            .OrderByDescending(d => d)
            .FirstOrDefault();

        if (newest is null)
        {
            continue;
        }

        var msbuildDll = Path.Combine(newest, "MSBuild.dll");
        if (!File.Exists(msbuildDll))
        {
            continue;
        }

        MSBuildLocator.RegisterMSBuildPath(newest);
        registered = true;
        break;
    }

    if (!registered)
    {
        MSBuildLocator.RegisterDefaults();
    }

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

static RivetOptions? ParseArgs(string[] args)
{
    if (args.Length == 0)
    {
        return null;
    }

    string? projectPath = null;
    string? outputDir = null;
    var mode = "generate";
    string? openApiPath = null;
    string? defaultSecurity = null;
    string? fromOpenApiPath = null;
    string? importNamespace = null;
    var check = false;
    var quiet = false;
    var routes = false;
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
            case "--compile":
                mode = "compile";
                break;
            case "--openapi":
                openApiPath = i + 1 < args.Length && !args[i + 1].StartsWith('-')
                    ? args[++i]
                    : "openapi.json";
                break;
            case "--security" when i + 1 < args.Length:
                defaultSecurity = args[++i];
                break;
            case "--from-openapi" when i + 1 < args.Length:
                fromOpenApiPath = args[++i];
                break;
            case "--namespace" when i + 1 < args.Length:
                importNamespace = args[++i];
                break;
            case "--check":
                check = true;
                break;
            case "--quiet" or "-q":
                quiet = true;
                break;
            case "--routes":
                routes = true;
                break;
            default:
                files.Add(args[i]);
                break;
        }
    }

    // Import mode doesn't need a project path
    if (fromOpenApiPath is not null)
    {
        return new RivetOptions(
            fromOpenApiPath, outputDir, mode, files.ToArray(),
            openApiPath, defaultSecurity, fromOpenApiPath, importNamespace, check, quiet, routes);
    }

    projectPath ??= files.FirstOrDefault();

    if (projectPath is null)
    {
        return null;
    }

    return new RivetOptions(projectPath, outputDir, mode, files.ToArray(), openApiPath, defaultSecurity, Check: check, Quiet: quiet, Routes: routes);
}

static int RunImport(RivetOptions options)
{
    var json = File.ReadAllText(options.FromOpenApiPath!);
    var importOptions = new ImportOptions(
        options.ImportNamespace ?? "Generated",
        options.DefaultSecurity);
    var result = OpenApiImporter.Import(json, importOptions);

    foreach (var warning in result.Warnings)
    {
        Console.Error.WriteLine($"warning: {warning}");
    }

    if (options.OutputDir is not null)
    {
        foreach (var file in result.Files)
        {
            var path = Path.Combine(options.OutputDir, file.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
            Console.WriteLine($"  {file.FileName} → {path}");
        }

        Console.WriteLine($"Generated {result.Files.Count} file(s).");
    }
    else
    {
        // Preview to stdout
        foreach (var file in result.Files)
        {
            Console.WriteLine($"// === {file.FileName} ===");
            Console.WriteLine(file.Content);
        }
    }

    return 0;
}

static void PrintRoutes(IReadOnlyList<TsEndpointDefinition> endpoints)
{
    var sorted = endpoints
        .OrderBy(e => e.RouteTemplate)
        .ThenBy(e => e.HttpMethod)
        .ToList();

    // Column widths
    var methodLabel = "Method";
    var routeLabel = "Route";
    var handlerLabel = "Handler";
    var methodWidth = Math.Max(methodLabel.Length, sorted.Max(e => e.HttpMethod.Length));
    var routeWidth = Math.Max(routeLabel.Length, sorted.Max(e => e.RouteTemplate.Length));

    Console.WriteLine($"  {methodLabel.PadRight(methodWidth)}  {routeLabel.PadRight(routeWidth)}  {handlerLabel}");
    Console.WriteLine($"  {"".PadRight(methodWidth, '─')}  {"".PadRight(routeWidth, '─')}  {"".PadRight(handlerLabel.Length, '─')}");

    foreach (var ep in sorted)
    {
        var method = ep.HttpMethod.PadRight(methodWidth);
        var route = ep.RouteTemplate.PadRight(routeWidth);
        var methodColor = ep.HttpMethod switch
        {
            "GET" => "\x1b[32m",     // green
            "POST" => "\x1b[33m",    // yellow
            "PUT" => "\x1b[34m",     // blue
            "PATCH" => "\x1b[36m",   // cyan
            "DELETE" => "\x1b[31m",  // red
            _ => "",
        };
        var coloredRoute = System.Text.RegularExpressions.Regex.Replace(
            route, @"\{[^}]+\}", m => $"\x1b[33m{m.Value}\x1b[0m");
        Console.WriteLine($"  {methodColor}{method}\x1b[0m  {coloredRoute}  \x1b[90m{ep.ControllerName}.{ep.Name}\x1b[0m");
    }

    Console.Error.WriteLine($"{sorted.Count} route(s).");
}

static string FormatElapsed(TimeSpan elapsed) =>
    elapsed.TotalSeconds >= 1
        ? $"{elapsed.TotalSeconds:F2}s"
        : $"{elapsed.TotalMilliseconds:F0}ms";

static void PrintUsage()
{
    Console.Error.WriteLine("Rivet — C# to TypeScript type generator");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet rivet --project <path.csproj> --output <dir>");
    Console.Error.WriteLine("  dotnet rivet <file.cs> [file2.cs ...] [--output <dir>]");
    Console.Error.WriteLine("  dotnet rivet --from-openapi <spec.json> --namespace <ns> [--output <dir>]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  -p, --project <path>       Path to .csproj file");
    Console.Error.WriteLine("  -o, --output <dir>         Output directory (omit for stdout preview)");
    Console.Error.WriteLine("  --compile                  Also run typia compilation (requires node on PATH)");
    Console.Error.WriteLine("  --openapi [file]           Emit OpenAPI 3.1 JSON spec (default: openapi.json)");
    Console.Error.WriteLine("  --security <spec>          Default security scheme (bearer, bearer:jwt, cookie:name, apikey:in:name)");
    Console.Error.WriteLine("  --from-openapi <spec.json> Import OpenAPI spec → C# contracts + DTOs");
    Console.Error.WriteLine("  --namespace <ns>           Namespace for generated C# files (default: Generated)");
    Console.Error.WriteLine("  --check                    Verify contract coverage (missing impls, route/method mismatches)");
    Console.Error.WriteLine("  --routes                   List all discovered endpoints (method, route, handler)");
    Console.Error.WriteLine("  -q, --quiet                Suppress codegen output (useful with --check)");
}

static IEnumerable<string> GetDotnetRoots()
{
    // Well-known dotnet install locations
    var candidates = new List<string>
    {
        "/usr/local/share/dotnet",
        "/usr/share/dotnet",
    };

    // Homebrew: /opt/homebrew/Cellar/dotnet*/*/libexec
    try
    {
        foreach (var dir in Directory.GetDirectories("/opt/homebrew/Cellar", "dotnet*"))
        {
            foreach (var version in Directory.GetDirectories(dir))
            {
                candidates.Add(Path.Combine(version, "libexec"));
            }
        }
    }
    catch { }

    foreach (var path in candidates)
    {
        if (Directory.Exists(Path.Combine(path, "sdk")))
        {
            yield return path;
        }
    }

    // Also try resolving from dotnet --info
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = "--info",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    string? output = null;
    try
    {
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is not null)
        {
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }
    }
    catch { }

    if (output is not null)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Base Path:", StringComparison.OrdinalIgnoreCase))
            {
                var basePath = trimmed["Base Path:".Length..].Trim().TrimEnd('/');
                var sdkDir = Path.GetDirectoryName(basePath);
                if (sdkDir is not null)
                {
                    var root = Path.GetDirectoryName(sdkDir);
                    if (root is not null)
                    {
                        yield return root;
                    }
                }
            }
        }
    }
}

sealed record RivetOptions(
    string ProjectPath, string? OutputDir, string Mode, string[] Files,
    string? OpenApiPath = null, string? DefaultSecurity = null,
    string? FromOpenApiPath = null, string? ImportNamespace = null,
    bool Check = false,
    bool Quiet = false,
    bool Routes = false);
