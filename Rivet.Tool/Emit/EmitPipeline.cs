using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Shared emit logic used by both the Roslyn (csproj) and JSON contract (--from) code paths.
/// Runs the inline-type extraction pass, then emits the OpenAPI 3.1 spec.
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
        // Extraction pass: find duplicate/large InlineObjects, replace with named TypeRefs.
        // OpenApiEmitter resolves those TypeRefs through DefinitionsByName, so the extracted
        // definitions must be merged in before emission.
        var extraction = InlineTypeExtractor.Extract(input.Endpoints, input.Definitions.ToList());

        var definitionsByName = new Dictionary<string, TsTypeDefinition>(input.DefinitionsByName);
        foreach (var def in extraction.ExtractedTypes)
            definitionsByName[def.Name] = def;

        var endpoints = extraction.Endpoints;

        var documentInfo = new OpenApiDocumentInfo(
            options.Title ?? "API",
            options.Version ?? "1.0.0",
            options.Servers is { Count: > 0 } ? options.Servers : null);
        string openApiJson;
        try
        {
            var securityConfig = options.SecuritySchemes is { Count: > 0 }
                ? SecurityParser.ParseMany(options.SecuritySchemes)
                : SecurityParser.Parse(options.DefaultSecurity);
            openApiJson = OpenApiEmitter.Emit(
                endpoints, definitionsByName, input.BrandsByName, input.Enums, securityConfig, documentInfo);
        }
        catch (Exception exception) when (exception is OpenApiEmissionException or SecurityConfigurationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        // Resolve the spec path: --openapi overrides (resolved against --output when relative);
        // otherwise --output <dir> writes <dir>/openapi.json.
        string? specPath = null;
        if (options.OpenApiPath is not null)
        {
            specPath = Path.IsPathRooted(options.OpenApiPath)
                ? options.OpenApiPath
                : Path.GetFullPath(Path.Combine(options.OutputDir ?? Directory.GetCurrentDirectory(), options.OpenApiPath));
        }
        else if (options.OutputDir is not null)
        {
            specPath = Path.GetFullPath(Path.Combine(options.OutputDir, "openapi.json"));
        }

        // --verify: emission is deterministic, so freshness is plain string equality
        // between the would-be spec and the committed file. Never writes.
        if (options.Verify)
        {
            if (specPath is null)
            {
                // CliParser rejects --verify without a target; belt-and-braces.
                Console.Error.WriteLine("error: '--verify' needs --output or --openapi — the committed spec to compare against");
                return 1;
            }

            if (!File.Exists(specPath))
            {
                Console.Error.WriteLine($"error: --verify: {specPath} does not exist — run the same command without --verify to generate it");
                return 1;
            }

            var committed = await File.ReadAllTextAsync(specPath);
            if (!string.Equals(committed, openApiJson, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"error: --verify: {specPath} is stale — the source no longer matches it. Regenerate (run without --verify) and commit the result.");
                return 1;
            }

            if (!options.Quiet)
            {
                Console.WriteLine($"Spec is up to date: {specPath}");
            }

            return 0;
        }

        if (specPath is not null)
        {
            if (options.OutputDir is not null)
            {
                Directory.CreateDirectory(options.OutputDir);
            }

            var specDirectory = Path.GetDirectoryName(specPath);
            if (!string.IsNullOrWhiteSpace(specDirectory))
            {
                Directory.CreateDirectory(specDirectory);
            }

            await File.WriteAllTextAsync(specPath, openApiJson);

            if (!options.Quiet)
            {
                Console.WriteLine($"  openapi.json → {specPath}");
                Console.WriteLine($"Generated OpenAPI spec: {definitionsByName.Count} schemas, {endpoints.Count} endpoints.");
            }
        }
        else if (!options.Quiet)
        {
            // Preview to stdout
            Console.WriteLine(openApiJson);
        }

        return 0;
    }
}
