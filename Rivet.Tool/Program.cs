using Rivet.Tool;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

try
{
    return await Run(args);
}
catch (RivetUserException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<int> Run(string[] args)
{
    if (CliParser.IsHelpRequest(args))
    {
        CliParser.PrintUsage(Console.Out);
        return 0;
    }

    var options = CliParser.ParseArgs(args);

    if (options is null)
    {
        CliParser.PrintUsage(Console.Error);
        return 1;
    }

    // Contract JSON mode: the same emitter as the Roslyn path
    if (options.FromContractPath is not null)
    {
        return await RunFromContract(options);
    }

    // Import mode: OpenAPI → C# contracts
    if (options.FromOpenApiPath is not null)
    {
        return RunImport(options);
    }

    var compilation = options.ProjectPath is { } projectPath
        ? await CompilationLoader.LoadProjectAsync(projectPath)
        : CompilationLoader.CompileFromFiles([.. options.Files]);

    if (compilation is null)
    {
        Console.Error.WriteLine("Aborting — cannot proceed with compilation errors.");
        return 1;
    }

    // Single-pass discovery: scan source assembly types once instead of 4× full namespace walks
    var discovered = SymbolDiscovery.Discover(compilation);

    var wkt = new WellKnownTypes(compilation);
    var walker = TypeWalker.Create(compilation, wkt, discovered.RivetTypes);
    var endpoints = EndpointWalker.Walk(
        wkt,
        walker,
        discovered.EndpointMethods,
        discovered.ClientTypes
    );
    var contracts = ContractWalker.Walk(compilation, wkt, walker, discovered.ContractTypes);
    var contractEndpoints = contracts.Select(contract => contract.Endpoint).ToList();

    if (options.Check)
    {
        var functionsRoutePrefix = wkt.HttpTrigger is null
            ? "api"
            : FunctionsHostConfiguration.LoadRoutePrefix(options.ProjectPath);
        var coverageWarnings = CoverageChecker.Check(
            compilation,
            wkt,
            contracts,
            functionsRoutePrefix
        );
        foreach (var w in coverageWarnings)
        {
            var id = w.Kind switch
            {
                CoverageWarningKind.MissingImplementation =>
                    Diagnostics.CoverageMissingImplementation,
                CoverageWarningKind.HttpMethodMismatch => Diagnostics.CoverageHttpMethodMismatch,
                CoverageWarningKind.RouteMismatch => Diagnostics.CoverageRouteMismatch,
                CoverageWarningKind.OrphanedBinding => Diagnostics.CoverageOrphanedBinding,
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
        var orphanedCount = coverageWarnings.Count(w =>
            w.Kind == CoverageWarningKind.OrphanedBinding
        );
        var mismatchCount = coverageWarnings.Count - missingCount - orphanedCount;

        if (coverageWarnings.Count == 0)
        {
            Console.Error.WriteLine(
                $"Coverage: {coveredCount}/{totalFields} endpoints covered. All OK."
            );
        }
        else
        {
            Console.Error.WriteLine(
                $"Coverage: {coveredCount}/{totalFields} endpoints covered, {mismatchCount} mismatch(es), {orphanedCount} orphaned binding(s), {missingCount} missing."
            );
        }

        if (coverageWarnings.Count > 0)
        {
            return 1;
        }
    }

    var merged = EndpointMerger.Merge(contractEndpoints, endpoints);

    if (options.Routes)
    {
        RoutePrinter.Print(merged);
        return 0;
    }

    var securityMetadata = SecurityMetadataWalker.Walk(compilation);
    var documentProvenance = OpenApiProvenanceWalker.Walk(compilation, walker);

    var emitInput = new EmitPipeline.EmitInput(
        walker.Enums,
        merged,
        walker.Definitions,
        walker.Brands,
        securityMetadata,
        documentProvenance
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
    IReadOnlyList<TsTypeDefinition> types;
    Dictionary<string, TsType> enums;
    IReadOnlyList<TsEndpointDefinition> endpoints;
    Dictionary<string, TsType.Brand> brands;
    try
    {
        (types, enums, endpoints, brands) = JsonContractReader.Read(json);
    }
    catch (System.Text.Json.JsonException exception)
    {
        throw new RivetUserException(
            $"error: invalid contract JSON in {contractPath}: {exception.Message}"
        );
    }

    var emitInput = new EmitPipeline.EmitInput(
        enums,
        endpoints,
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
        options.SecuritySchemes?.FirstOrDefault()
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
