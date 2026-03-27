using System.Diagnostics;
using Rivet.Tool;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

return await Run(args);

static async Task<int> Run(string[] args)
{
    var options = CliParser.ParseArgs(args);

    if (options is null)
    {
        CliParser.PrintUsage();
        return 1;
    }

    // Contract JSON mode: JSON → TypeScript (same emitters as Roslyn path)
    if (options.FromContractPath is not null)
    {
        return await RunFromContract(options);
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

    Microsoft.CodeAnalysis.Compilation? compilation;

    if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
    {
        compilation = await CompilationLoader.LoadProjectAsync(projectPath);
    }
    else
    {
        compilation = CompilationLoader.CompileFromFiles(options.Files);
    }

    if (compilation is null)
    {
        Console.Error.WriteLine("Aborting — cannot proceed with compilation errors.");
        return 1;
    }

    // Single-pass discovery: scan source assembly types once instead of 4× full namespace walks
    var discovered = SymbolDiscovery.Discover(compilation);

    var walker = TypeWalker.Create(compilation, discovered.RivetTypes);
    if (walker.HasErrors)
    {
        Console.Error.WriteLine("Aborting — type name collisions detected.");
        return 1;
    }

    var wkt = new WellKnownTypes(compilation);
    var endpoints = EndpointWalker.Walk(wkt, walker, discovered.EndpointMethods, discovered.ClientTypes);
    var contractEndpoints = ContractWalker.Walk(compilation, wkt, walker, discovered.ContractTypes);

    if (options.Check)
    {
        var coverageWarnings = CoverageChecker.Check(compilation, wkt, contractEndpoints);
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
        RoutePrinter.Print(merged);
        return 0;
    }

    var definitions = walker.Definitions.Values.ToList();
    var brands = walker.Brands.Values.ToList();

    var emitInput = new EmitPipeline.EmitInput(
        definitions, brands, walker.Enums, endpoints, walker.TypeNamespaces,
        walker.Definitions, walker.Brands);

    return await EmitPipeline.RunAsync(emitInput, options);
}

static async Task<int> RunFromContract(RivetOptions options)
{
    var contractPath = options.FromContractPath!;
    if (!File.Exists(contractPath))
    {
        Console.Error.WriteLine($"error: file not found: {contractPath}");
        return 1;
    }

    var json = await File.ReadAllTextAsync(contractPath);
    var (types, enums, endpoints) = JsonContractReader.Read(json);

    var emitInput = new EmitPipeline.EmitInput(
        types.ToList(),
        [],
        enums,
        endpoints,
        new Dictionary<string, string?>(),
        types.ToDictionary(t => t.Name),
        new Dictionary<string, TsType.Brand>());

    return await EmitPipeline.RunAsync(emitInput, options);
}

static int RunImport(RivetOptions options)
{
    if (!File.Exists(options.FromOpenApiPath!))
    {
        Console.Error.WriteLine($"error: file not found: {options.FromOpenApiPath}");
        return 1;
    }

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

