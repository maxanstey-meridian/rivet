namespace Rivet.Tool;

internal static class CliParser
{
    private const string RemovedFlagMessage =
        "removed in v2: TS/Zod generation moved to the OpenAPI ecosystem (openapi-typescript, openapi-zod-client); see docs";

    public static RivetOptions? ParseArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        string? projectPath = null;
        string? outputDir = null;
        string? openApiPath = null;
        string? defaultSecurity = null;
        string? fromOpenApiPath = null;
        string? fromContractPath = null;
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
                // Removed in v2 — fail loudly so old invocations don't silently degrade.
                case "--compile" or "--jsonschema":
                    Console.Error.WriteLine($"error: '{args[i]}' was {RemovedFlagMessage}");
                    return null;
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
                case "--from" when i + 1 < args.Length:
                    fromContractPath = args[++i];
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
                // C3: value-taking flags reached without a following value (the guarded cases
                // above didn't match) — error loudly instead of treating the flag as a file.
                case "--project" or "-p" or "--output" or "-o" or "--security"
                    or "--from-openapi" or "--from" or "--namespace":
                    Console.Error.WriteLine($"error: flag '{args[i]}' requires a value");
                    return null;
                default:
                    // C7: unknown flags are an error, not a file path — silent acceptance
                    // turns typos into "no contracts found" mysteries.
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"error: unknown flag '{args[i]}'");
                        return null;
                    }

                    files.Add(args[i]);
                    break;
            }
        }

        // Contract JSON mode doesn't need a project path
        if (fromContractPath is not null)
        {
            return new RivetOptions(
                fromContractPath, outputDir, files.ToArray(),
                OpenApiPath: openApiPath, DefaultSecurity: defaultSecurity,
                Quiet: quiet, FromContractPath: fromContractPath);
        }

        // Import mode doesn't need a project path
        if (fromOpenApiPath is not null)
        {
            return new RivetOptions(
                fromOpenApiPath, outputDir, files.ToArray(),
                openApiPath, defaultSecurity, FromOpenApiPath: fromOpenApiPath, ImportNamespace: importNamespace, Check: check, Quiet: quiet, Routes: routes);
        }

        projectPath ??= files.FirstOrDefault();

        if (projectPath is null)
        {
            return null;
        }

        return new RivetOptions(projectPath, outputDir, files.ToArray(), openApiPath, defaultSecurity, Check: check, Quiet: quiet, Routes: routes);
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine("Rivet — C# contracts to OpenAPI 3.1");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet rivet --project <path.csproj> --output <dir>");
        Console.Error.WriteLine("  dotnet rivet <file.cs> [file2.cs ...] [--output <dir>]");
        Console.Error.WriteLine("  dotnet rivet --from-openapi <spec.json> --namespace <ns> [--output <dir>]");
        Console.Error.WriteLine("  dotnet rivet --from <contract.json> [--output <dir>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Writes an OpenAPI 3.1 spec (openapi.json) for the discovered contracts;");
        Console.Error.WriteLine("omit --output for a stdout preview. Consume the spec with the OpenAPI");
        Console.Error.WriteLine("ecosystem: openapi-typescript, openapi-fetch, openapi-zod-client, ...");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  -p, --project <path>       Path to .csproj file");
        Console.Error.WriteLine("  -o, --output <dir>         Output directory for openapi.json (omit for stdout preview)");
        Console.Error.WriteLine("  --openapi [file]           Explicit spec path override (relative paths resolve against --output)");
        Console.Error.WriteLine("  --security <spec>          Default security scheme (bearer, bearer:jwt, cookie:name, apikey:in:name)");
        Console.Error.WriteLine("  --from <contract.json>     Emit OpenAPI from a Rivet contract JSON file");
        Console.Error.WriteLine("  --from-openapi <spec.json> Onboarding scaffold: one-shot import of an OpenAPI spec");
        Console.Error.WriteLine("                             → C# contracts + DTOs; the C# becomes the source of");
        Console.Error.WriteLine("                             truth (see docs/reference/import-profile)");
        Console.Error.WriteLine("  --namespace <ns>           Namespace for generated C# files (default: Generated)");
        Console.Error.WriteLine("  --check                    Verify contract coverage (missing impls, route/method mismatches)");
        Console.Error.WriteLine("  --routes                   List all discovered endpoints (method, route, handler)");
        Console.Error.WriteLine("  -q, --quiet                Suppress codegen output (useful with --check)");
    }
}

sealed record RivetOptions(
    string ProjectPath, string? OutputDir, string[] Files,
    string? OpenApiPath = null, string? DefaultSecurity = null,
    string? FromOpenApiPath = null, string? ImportNamespace = null,
    bool Check = false,
    bool Quiet = false,
    bool Routes = false,
    string? FromContractPath = null);
