using Rivet.Tool.Emit;

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
        var securitySchemes = new List<string>();
        string? fromOpenApiPath = null;
        string? fromContractPath = null;
        string? importNamespace = null;
        string? title = null;
        string? version = null;
        var servers = new List<string>();
        var check = false;
        var verify = false;
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
                    securitySchemes.Add(args[++i]);
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
                case "--title" when i + 1 < args.Length:
                    title = args[++i];
                    break;
                case "--version" when i + 1 < args.Length:
                    version = args[++i];
                    break;
                case "--server" when i + 1 < args.Length:
                    var server = args[++i];
                    if (!IsValidServerUrl(server))
                    {
                        Console.Error.WriteLine(
                            $"error: '--server' value '{server}' is not a valid URL (expected an absolute http(s) URL or a path starting with '/')");
                        return null;
                    }

                    servers.Add(server);
                    break;
                case "--check":
                    check = true;
                    break;
                case "--verify":
                    verify = true;
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
                    or "--from-openapi" or "--from" or "--namespace"
                    or "--title" or "--version" or "--server":
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

        // --verify compares the would-be spec against an existing file; without a
        // target file (stdout preview) or in import mode there is nothing to compare.
        if (verify && fromOpenApiPath is not null)
        {
            Console.Error.WriteLine("error: '--verify' does not apply to --from-openapi (import generates C#, not a spec)");
            return null;
        }

        if (verify && outputDir is null && openApiPath is null)
        {
            Console.Error.WriteLine("error: '--verify' needs --output or --openapi — the committed spec to compare against");
            return null;
        }

        // Contract JSON mode doesn't need a project path
        if (fromContractPath is not null)
        {
            return new RivetOptions(
                fromContractPath, outputDir, files.ToArray(),
                OpenApiPath: openApiPath, DefaultSecurity: securitySchemes.FirstOrDefault(), SecuritySchemes: securitySchemes,
                Quiet: quiet, FromContractPath: fromContractPath,
                Title: title, Version: version, Servers: servers, Verify: verify);
        }

        // Import mode doesn't need a project path
        if (fromOpenApiPath is not null)
        {
            if (securitySchemes.Count > 1
                || securitySchemes is [var importSecurity]
                    && !SecurityParser.IsValidSchemeName(importSecurity))
            {
                Console.Error.WriteLine(
                    "error: --security with --from-openapi accepts one security scheme name, not an emit-time scheme definition");
                return null;
            }

            return new RivetOptions(
                fromOpenApiPath, outputDir, files.ToArray(),
                openApiPath, securitySchemes.FirstOrDefault(), FromOpenApiPath: fromOpenApiPath, ImportNamespace: importNamespace, Check: check, Quiet: quiet, Routes: routes, Title: title, Version: version, Servers: servers, SecuritySchemes: securitySchemes);
        }

        projectPath ??= files.FirstOrDefault();

        if (projectPath is null)
        {
            return null;
        }

        return new RivetOptions(projectPath, outputDir, files.ToArray(), openApiPath, securitySchemes.FirstOrDefault(), Check: check, Quiet: quiet, Routes: routes, Title: title, Version: version, Servers: servers, Verify: verify, SecuritySchemes: securitySchemes);
    }

    // OpenAPI server URLs are either absolute (http/https) or paths relative to the
    // host serving the spec — anything else is a typo, not a server.
    private static bool IsValidServerUrl(string value) =>
        value.StartsWith('/')
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https");

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
        Console.Error.WriteLine("  --security <[name=]spec>   Emit security scheme (repeatable); import accepts one scheme name");
        Console.Error.WriteLine("  --title <text>             Spec info.title (default: API)");
        Console.Error.WriteLine("  --version <text>           Spec info.version (default: 1.0.0) — there is no print-tool-version flag");
        Console.Error.WriteLine("  --server <url>             Spec servers entry (repeatable; omitted entirely when not given)");
        Console.Error.WriteLine("  --from <contract.json>     Emit OpenAPI from a Rivet contract JSON file");
        Console.Error.WriteLine("  --from-openapi <spec.json> Onboarding scaffold: one-shot import of an OpenAPI spec");
        Console.Error.WriteLine("                             → C# contracts + DTOs; the C# becomes the source of");
        Console.Error.WriteLine("                             truth (see docs/reference/import-profile)");
        Console.Error.WriteLine("  --namespace <ns>           Namespace for generated C# files (default: Generated)");
        Console.Error.WriteLine("  --check                    Verify contract coverage (missing impls, route/method mismatches)");
        Console.Error.WriteLine("  --verify                   Compare the spec against the existing file instead of writing —");
        Console.Error.WriteLine("                             exit 1 on drift (CI gate for committed openapi.json)");
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
    string? FromContractPath = null,
    string? Title = null,
    string? Version = null,
    IReadOnlyList<string>? Servers = null,
    bool Verify = false,
    IReadOnlyList<string>? SecuritySchemes = null);
