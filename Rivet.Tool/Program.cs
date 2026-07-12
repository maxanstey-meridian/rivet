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
    var (projectPath, outputDir) = (options.ProjectPath, options.OutputDir);

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
    var endpoints = EndpointWalker.Walk(
        wkt,
        walker,
        discovered.EndpointMethods,
        discovered.ClientTypes
    );
    IReadOnlyList<TsEndpointDefinition> contractEndpoints;
    try
    {
        contractEndpoints = ContractWalker.Walk(compilation, wkt, walker, discovered.ContractTypes);
    }
    catch (ContractAnalysisException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }

    if (options.Check)
    {
        var coverageWarnings = CoverageChecker.Check(compilation, wkt, contractEndpoints);
        foreach (var w in coverageWarnings)
        {
            var id = w.Kind switch
            {
                CoverageWarningKind.MissingImplementation =>
                    Diagnostics.CoverageMissingImplementation,
                CoverageWarningKind.HttpMethodMismatch => Diagnostics.CoverageHttpMethodMismatch,
                CoverageWarningKind.RouteMismatch => Diagnostics.CoverageRouteMismatch,
                _ => throw new InvalidOperationException(
                    $"Unmapped coverage warning kind: {w.Kind}"
                ),
            };
            Diagnostics.Warn(
                id,
                $"[{w.Kind}] {w.ContractName}.{w.FieldName}: expected {w.Expected}, got {w.Actual}"
            );
        }

        var totalFields = contractEndpoints.Count;
        var missingCount = coverageWarnings.Count(w =>
            w.Kind == CoverageWarningKind.MissingImplementation
        );
        var coveredCount = totalFields - missingCount;
        var mismatchCount = coverageWarnings.Count - missingCount;

        if (coverageWarnings.Count == 0)
        {
            Console.Error.WriteLine(
                $"Coverage: {coveredCount}/{totalFields} endpoints covered. All OK."
            );
        }
        else
        {
            Console.Error.WriteLine(
                $"Coverage: {coveredCount}/{totalFields} endpoints covered, {mismatchCount} mismatch(es), {missingCount} missing."
            );
        }

        if (coverageWarnings.Count > 0 && outputDir is null)
        {
            return 1;
        }
    }

    // Merge: contract endpoints win on (ControllerName, Name) collision
    var merged = EndpointMerger.Merge(contractEndpoints, endpoints);
    endpoints = merged;

    if (options.Routes)
    {
        RoutePrinter.Print(merged);
        return 0;
    }

    var definitions = walker.Definitions.Values.ToList();
    var brands = walker.Brands.Values.ToList();

    var emitInput = new EmitPipeline.EmitInput(
        definitions,
        brands,
        walker.Enums,
        endpoints,
        walker.TypeNamespaces,
        walker.Definitions,
        walker.Brands
    );

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
    var (types, enums, endpoints, brands) = JsonContractReader.Read(json);

    var emitInput = new EmitPipeline.EmitInput(
        types.ToList(),
        brands.Values.ToList(),
        enums,
        endpoints,
        new Dictionary<string, string?>(),
        types.ToDictionary(t => t.Name),
        brands
    );

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
        options.DefaultSecurity
    );
    var result = OpenApiImporter.Import(json, importOptions);

    // Import warnings carry their RIV3xxx ID as a "RIV3001: " prefix (Diagnostics.Prefix),
    // so "warning {warning}" yields the canonical "warning RIV3001: <message>" line.
    foreach (var warning in result.Warnings)
    {
        Console.Error.WriteLine($"warning {warning}");
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
