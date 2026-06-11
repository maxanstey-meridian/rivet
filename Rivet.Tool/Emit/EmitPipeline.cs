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

        var securityConfig = SecurityParser.Parse(options.DefaultSecurity);
        var openApiJson = OpenApiEmitter.Emit(endpoints, definitionsByName, input.BrandsByName, input.Enums, securityConfig);

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
