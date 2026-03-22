namespace Rivet.Tool;

internal static class CliParser
{
    public static RivetOptions? ParseArgs(string[] args)
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
        var jsonSchema = false;
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
                    // Accept and ignore legacy "--compile zod" argument
                    if (i + 1 < args.Length && args[i + 1] == "zod")
                    {
                        i++;
                    }
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
                case "--jsonschema":
                    jsonSchema = true;
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
                openApiPath, defaultSecurity, fromOpenApiPath, importNamespace, check, quiet, routes, jsonSchema);
        }

        projectPath ??= files.FirstOrDefault();

        if (projectPath is null)
        {
            return null;
        }

        return new RivetOptions(projectPath, outputDir, mode, files.ToArray(), openApiPath, defaultSecurity, Check: check, Quiet: quiet, Routes: routes, JsonSchema: jsonSchema);
    }

    public static void PrintUsage()
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
        Console.Error.WriteLine("  --compile                  Emit Zod validators (fromJSONSchema, requires zod in consumer project)");
        Console.Error.WriteLine("  --openapi [file]           Emit OpenAPI 3.0 JSON spec (default: openapi.json)");
        Console.Error.WriteLine("  --security <spec>          Default security scheme (bearer, bearer:jwt, cookie:name, apikey:in:name)");
        Console.Error.WriteLine("  --from-openapi <spec.json> Import OpenAPI spec → C# contracts + DTOs");
        Console.Error.WriteLine("  --namespace <ns>           Namespace for generated C# files (default: Generated)");
        Console.Error.WriteLine("  --jsonschema               Emit standalone JSON Schema definitions (schemas.ts)");
        Console.Error.WriteLine("  --check                    Verify contract coverage (missing impls, route/method mismatches)");
        Console.Error.WriteLine("  --routes                   List all discovered endpoints (method, route, handler)");
        Console.Error.WriteLine("  -q, --quiet                Suppress codegen output (useful with --check)");
    }
}

sealed record RivetOptions(
    string ProjectPath, string? OutputDir, string Mode, string[] Files,
    string? OpenApiPath = null, string? DefaultSecurity = null,
    string? FromOpenApiPath = null, string? ImportNamespace = null,
    bool Check = false,
    bool Quiet = false,
    bool Routes = false,
    bool JsonSchema = false);
