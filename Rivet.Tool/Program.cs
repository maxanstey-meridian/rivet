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
    var enums = walker.Enums;
    var typeGrouping = TypeGrouper.Group(definitions, brands, enums, walker.TypeNamespaces);
    var typeFileMap = typeGrouping.BuildTypeFileMap();
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

        // OpenAPI spec
        if (options.OpenApiPath is not null)
        {
            var securityConfig = SecurityParser.Parse(options.DefaultSecurity);
            var openApiJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, securityConfig);
            var openApiFilePath = Path.Combine(outputDir, options.OpenApiPath);
            await File.WriteAllTextAsync(openApiFilePath, openApiJson);
            Console.WriteLine($"  {options.OpenApiPath} → {openApiFilePath}");
        }

        // JSON Schema — emitted for --jsonschema or --compile (Zod validators depend on it)
        if (options.JsonSchema || mode == "compile")
        {
            var schemasOutput = JsonSchemaEmitter.Emit(walker.Definitions, walker.Brands, walker.Enums, endpoints);
            var schemasPath = Path.Combine(outputDir, "schemas.ts");
            await File.WriteAllTextAsync(schemasPath, schemasOutput);
            Console.WriteLine($"  schemas.ts → {schemasPath}");
        }

        Console.WriteLine($"Generated {definitions.Count} types, {endpoints.Count} endpoints in {FormatElapsed(sw.Elapsed)}.");

        // Compile step: emit Zod validators
        if (mode == "compile")
        {
            Console.WriteLine();
            Console.WriteLine("Emitting Zod validators...");

            // validators.ts (Zod-based, directly usable)
            if (endpoints.Count > 0)
            {
                var zodValidators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);
                if (zodValidators.Length > 0)
                {
                    var zodPath = Path.Combine(outputDir, "validators.ts");
                    await File.WriteAllTextAsync(zodPath, zodValidators);
                    Console.WriteLine($"  validators.ts → {zodPath}");
                }
            }

            // Re-emit per-controller clients with validator assertions wired in
            if (endpoints.Count > 0)
            {
                var clientDir = Path.Combine(outputDir, "client");
                var controllerGroups = ClientEmitter.GroupByController(endpoints);
                foreach (var (controllerName, controllerEndpoints) in controllerGroups)
                {
                    var validatedContent = ClientEmitter.EmitControllerClient(
                        controllerName, controllerEndpoints, typeFileMap, ValidateMode.Zod);
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

        if (options.OpenApiPath is not null)
        {
            var securityConfig = SecurityParser.Parse(options.DefaultSecurity);
            var openApiJson = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, securityConfig);
            Console.WriteLine();
            Console.WriteLine($"=== {options.OpenApiPath} ===");
            Console.Write(openApiJson);
        }

        if (options.JsonSchema)
        {
            var schemasOutput = JsonSchemaEmitter.Emit(walker.Definitions, walker.Brands, walker.Enums, endpoints);
            Console.WriteLine();
            Console.WriteLine("=== schemas.ts ===");
            Console.Write(schemasOutput);
        }

    }

    return 0;
}

static async Task<int> RunFromContract(RivetOptions options)
{
    var contractPath = options.FromContractPath!;
    if (!File.Exists(contractPath))
    {
        Console.Error.WriteLine($"error: file not found: {contractPath}");
        return 1;
    }

    var sw = Stopwatch.StartNew();
    var json = await File.ReadAllTextAsync(contractPath);
    var (types, enums, endpoints) = JsonContractReader.Read(json);

    var definitions = types.ToList();
    var typeGrouping = TypeGrouper.Group(definitions, [], enums, new Dictionary<string, string?>());
    var typeFileMap = typeGrouping.BuildTypeFileMap();
    var mode = options.Mode;

    if (mode == "compile" && options.OutputDir is null)
    {
        Console.Error.WriteLine("Error: --compile requires --output <dir>.");
        return 1;
    }

    if (options.OutputDir is not null)
    {
        var outputDir = options.OutputDir;
        Directory.CreateDirectory(outputDir);

        // Types
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

        var typesBarrel = TypeEmitter.EmitNamespacedBarrel(typeFileNames);
        await File.WriteAllTextAsync(Path.Combine(typesDir, "index.ts"), typesBarrel);
        Console.WriteLine($"  types/index.ts → {Path.Combine(typesDir, "index.ts")}");

        // Client
        if (endpoints.Count > 0)
        {
            var rivetBasePath = Path.Combine(outputDir, "rivet.ts");
            await File.WriteAllTextAsync(rivetBasePath, ClientEmitter.EmitRivetBase());
            Console.WriteLine($"  rivet.ts → {rivetBasePath}");

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

            var clientBarrel = TypeEmitter.EmitNamespacedBarrel(clientFileNames);
            await File.WriteAllTextAsync(Path.Combine(clientDir, "index.ts"), clientBarrel);
            Console.WriteLine($"  client/index.ts → {Path.Combine(clientDir, "index.ts")}");
        }

        // OpenAPI
        if (options.OpenApiPath is not null)
        {
            var defs = types.ToDictionary(t => t.Name);
            var brands = new Dictionary<string, TsType.Brand>();
            var securityConfig = SecurityParser.Parse(options.DefaultSecurity);
            var openApiJson = OpenApiEmitter.Emit(endpoints, defs, brands, enums, securityConfig);
            var openApiFilePath = Path.Combine(outputDir, options.OpenApiPath);
            await File.WriteAllTextAsync(openApiFilePath, openApiJson);
            Console.WriteLine($"  {options.OpenApiPath} → {openApiFilePath}");
        }

        // JSON Schema + Zod
        if (options.JsonSchema || mode == "compile")
        {
            var defs = types.ToDictionary(t => t.Name);
            var brands = new Dictionary<string, TsType.Brand>();
            var schemasOutput = JsonSchemaEmitter.Emit(defs, brands, enums, endpoints);
            var schemasPath = Path.Combine(outputDir, "schemas.ts");
            await File.WriteAllTextAsync(schemasPath, schemasOutput);
            Console.WriteLine($"  schemas.ts → {schemasPath}");
        }

        Console.WriteLine($"Generated {definitions.Count} types, {endpoints.Count} endpoints in {FormatElapsed(sw.Elapsed)}.");

        if (mode == "compile" && endpoints.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Emitting Zod validators...");

            var zodValidators = ZodValidatorEmitter.Emit(endpoints, typeFileMap);
            if (zodValidators.Length > 0)
            {
                var zodPath = Path.Combine(outputDir, "validators.ts");
                await File.WriteAllTextAsync(zodPath, zodValidators);
                Console.WriteLine($"  validators.ts → {zodPath}");
            }

            var clientDir = Path.Combine(outputDir, "client");
            var controllerGroups = ClientEmitter.GroupByController(endpoints);
            foreach (var (controllerName, controllerEndpoints) in controllerGroups)
            {
                var validatedContent = ClientEmitter.EmitControllerClient(
                    controllerName, controllerEndpoints, typeFileMap, ValidateMode.Zod);
                var clientPath = Path.Combine(clientDir, $"{controllerName}.ts");
                await File.WriteAllTextAsync(clientPath, validatedContent);
                Console.WriteLine($"  client/{controllerName}.ts → {clientPath} (validated)");
            }

            Console.WriteLine($"Compile complete in {FormatElapsed(sw.Elapsed)}.");
        }
    }
    else
    {
        // Preview to stdout
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

        if (options.OpenApiPath is not null)
        {
            var defs = types.ToDictionary(t => t.Name);
            var brands = new Dictionary<string, TsType.Brand>();
            var securityConfig = SecurityParser.Parse(options.DefaultSecurity);
            var openApiJson = OpenApiEmitter.Emit(endpoints, defs, brands, enums, securityConfig);
            Console.WriteLine();
            Console.WriteLine($"=== {options.OpenApiPath} ===");
            Console.Write(openApiJson);
        }
    }

    return 0;
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

static string FormatElapsed(TimeSpan elapsed) =>
    elapsed.TotalSeconds >= 1
        ? $"{elapsed.TotalSeconds:F2}s"
        : $"{elapsed.TotalMilliseconds:F0}ms";
