using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Shared emit logic used by both the Roslyn (csproj) and JSON contract (--from) code paths.
/// Groups types, then writes or previews all output artifacts.
/// </summary>
internal static class EmitPipeline
{
    internal sealed record EmitInput(
        IReadOnlyList<TsTypeDefinition> Definitions,
        IReadOnlyList<TsType.Brand> Brands,
        IReadOnlyDictionary<string, TsType> Enums,
        IReadOnlyList<TsEndpointDefinition> Endpoints,
        IReadOnlyDictionary<string, string?> TypeNamespaces,
        IReadOnlyDictionary<string, TsTypeDefinition> DefinitionsByName,
        IReadOnlyDictionary<string, TsType.Brand> BrandsByName);

    internal static async Task<int> RunAsync(EmitInput input, RivetOptions options)
    {
        // Extraction pass: find duplicate/large InlineObjects, replace with named TypeRefs
        var extraction = InlineTypeExtractor.Extract(input.Endpoints, input.Definitions.ToList());

        var allDefinitions = input.Definitions.Concat(extraction.ExtractedTypes).ToList();

        var allNamespaces = new Dictionary<string, string?>(input.TypeNamespaces);
        foreach (var (name, ns) in extraction.TypeNamespaces)
            allNamespaces[name] = ns;

        var definitionsByName = new Dictionary<string, TsTypeDefinition>(input.DefinitionsByName);
        foreach (var def in extraction.ExtractedTypes)
            definitionsByName[def.Name] = def;

        var merged = input with
        {
            Definitions = allDefinitions,
            Endpoints = extraction.Endpoints,
            TypeNamespaces = allNamespaces,
            DefinitionsByName = definitionsByName,
        };

        var typeGrouping = TypeGrouper.Group(merged.Definitions, merged.Brands, merged.Enums, allNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var mode = options.Mode;
        var endpoints = merged.Endpoints;

        if (mode == "compile" && options.OutputDir is null)
        {
            Console.Error.WriteLine("Error: --compile requires --output <dir>.");
            return 1;
        }

        if (options.OutputDir is not null)
        {
            await WriteOutput(merged, options, typeGrouping, typeFileMap, mode, endpoints);
        }
        else if (!options.Quiet)
        {
            PreviewToStdout(merged, options, typeGrouping, typeFileMap, endpoints);
        }

        return 0;
    }

    private static async Task WriteOutput(
        EmitInput input, RivetOptions options,
        TypeGrouper.TypeGroupingResult typeGrouping,
        Dictionary<string, string> typeFileMap,
        string mode,
        IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        var outputDir = options.OutputDir!;
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
        var typesBarrelPath = Path.Combine(typesDir, "index.ts");
        await File.WriteAllTextAsync(typesBarrelPath, typesBarrel);
        Console.WriteLine($"  types/index.ts → {typesBarrelPath}");

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
            var clientBarrelPath = Path.Combine(clientDir, "index.ts");
            await File.WriteAllTextAsync(clientBarrelPath, clientBarrel);
            Console.WriteLine($"  client/index.ts → {clientBarrelPath}");
        }

        // OpenAPI
        if (options.OpenApiPath is not null)
        {
            var securityConfig = SecurityParser.Parse(options.DefaultSecurity);
            var openApiJson = OpenApiEmitter.Emit(endpoints, input.DefinitionsByName, input.BrandsByName, input.Enums, securityConfig);
            var openApiFilePath = Path.Combine(outputDir, options.OpenApiPath);
            await File.WriteAllTextAsync(openApiFilePath, openApiJson);
            Console.WriteLine($"  {options.OpenApiPath} → {openApiFilePath}");
        }

        // JSON Schema — emitted for --jsonschema or --compile (Zod validators depend on it)
        if (options.JsonSchema || mode == "compile")
        {
            var schemasOutput = JsonSchemaEmitter.Emit(input.DefinitionsByName, input.BrandsByName, input.Enums, endpoints);
            var schemasPath = Path.Combine(outputDir, "schemas.ts");
            await File.WriteAllTextAsync(schemasPath, schemasOutput);
            Console.WriteLine($"  schemas.ts → {schemasPath}");
        }

        Console.WriteLine($"Generated {input.Definitions.Count} types, {endpoints.Count} endpoints.");

        // Compile step: emit Zod validators
        if (mode == "compile")
        {
            Console.WriteLine();
            Console.WriteLine("Emitting Zod validators...");

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

            Console.WriteLine("Compile complete.");
        }
    }

    private static void PreviewToStdout(
        EmitInput input, RivetOptions options,
        TypeGrouper.TypeGroupingResult typeGrouping,
        Dictionary<string, string> typeFileMap,
        IReadOnlyList<TsEndpointDefinition> endpoints)
    {
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
            var openApiJson = OpenApiEmitter.Emit(endpoints, input.DefinitionsByName, input.BrandsByName, input.Enums, securityConfig);
            Console.WriteLine();
            Console.WriteLine($"=== {options.OpenApiPath} ===");
            Console.Write(openApiJson);
        }

        if (options.JsonSchema)
        {
            var schemasOutput = JsonSchemaEmitter.Emit(input.DefinitionsByName, input.BrandsByName, input.Enums, endpoints);
            Console.WriteLine();
            Console.WriteLine("=== schemas.ts ===");
            Console.Write(schemasOutput);
        }
    }
}
