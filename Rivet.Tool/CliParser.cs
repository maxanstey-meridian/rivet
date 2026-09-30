using Rivet.Tool.Emit;

namespace Rivet.Tool;

internal static class CliParser
{
    private const string RemovedFlagMessage =
        "removed in v2: TS/Zod generation moved to the OpenAPI ecosystem (openapi-typescript, openapi-zod-client); see docs";

    private enum Arity
    {
        Flag,
        Value,

        // --openapi takes a path only when one follows; bare, it means openapi.json.
        OptionalValue,
    }

    private sealed record Option(string Name, string? Alias, Arity Arity);

    private static readonly Option[] _optionTable =
    [
        new("--project", "-p", Arity.Value),
        new("--output", "-o", Arity.Value),
        new("--openapi", null, Arity.OptionalValue),
        new("--security", null, Arity.Value),
        new("--from-openapi", null, Arity.Value),
        new("--from", null, Arity.Value),
        new("--namespace", null, Arity.Value),
        new("--title", null, Arity.Value),
        new("--version", null, Arity.Value),
        new("--server", null, Arity.Value),
        new("--check", null, Arity.Flag),
        new("--verify", null, Arity.Flag),
        new("--quiet", "-q", Arity.Flag),
        new("--routes", null, Arity.Flag),
    ];

    private static readonly Dictionary<string, Option> _options = _optionTable
        .SelectMany(option =>
            option.Alias is null
                ? new[] { (option.Name, option) }
                : [(option.Name, option), (option.Alias, option)]
        )
        .ToDictionary(entry => entry.Item1, entry => entry.option);

    public static RivetOptions? ParseArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        var values = new Dictionary<string, List<string>>();
        var files = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // Removed in v2 — fail loudly so old invocations don't silently degrade.
            if (arg is "--compile" or "--jsonschema")
            {
                Console.Error.WriteLine($"error: '{arg}' was {RemovedFlagMessage}");
                return null;
            }

            if (!_options.TryGetValue(arg, out var option))
            {
                // Unknown flags are an error, not a file path: silent acceptance turns
                // typos into "no contracts found" mysteries.
                if (arg.StartsWith('-'))
                {
                    Console.Error.WriteLine($"error: unknown flag '{arg}'");
                    return null;
                }

                files.Add(arg);
                continue;
            }

            string value;
            switch (option.Arity)
            {
                case Arity.Value when i + 1 < args.Length:
                    value = args[++i];
                    break;
                case Arity.Value:
                    Console.Error.WriteLine($"error: flag '{arg}' requires a value");
                    return null;
                case Arity.OptionalValue:
                    value =
                        i + 1 < args.Length && !args[i + 1].StartsWith('-')
                            ? args[++i]
                            : "openapi.json";
                    break;
                default:
                    value = "";
                    break;
            }

            if (!values.TryGetValue(option.Name, out var list))
            {
                values[option.Name] = list = [];
            }

            list.Add(value);
        }

        string? Last(string name) => values.TryGetValue(name, out var v) ? v[^1] : null;
        IReadOnlyList<string> All(string name) => values.TryGetValue(name, out var v) ? v : [];
        bool Has(string name) => values.ContainsKey(name);

        var servers = All("--server");
        if (servers.FirstOrDefault(server => !IsValidServerUrl(server)) is { } badServer)
        {
            Console.Error.WriteLine(
                $"error: '--server' value '{badServer}' is not a valid URL (expected an absolute http(s) URL or a path starting with '/')"
            );
            return null;
        }

        var options = new RivetOptions(
            Last("--output"),
            files,
            ProjectPath: Last("--project")
                ?? files.FirstOrDefault(file =>
                    file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ),
            OpenApiPath: Last("--openapi"),
            FromOpenApiPath: Last("--from-openapi"),
            FromContractPath: Last("--from"),
            ImportNamespace: Last("--namespace"),
            Check: Has("--check"),
            Quiet: Has("--quiet"),
            Routes: Has("--routes"),
            Title: Last("--title"),
            Version: Last("--version"),
            Servers: servers,
            Verify: Has("--verify"),
            SecuritySchemes: All("--security")
        );

        // --verify compares the would-be spec against an existing file; without a
        // target file (stdout preview) or in import mode there is nothing to compare.
        if (options.Verify && options.FromOpenApiPath is not null)
        {
            Console.Error.WriteLine(
                "error: '--verify' does not apply to --from-openapi (import generates C#, not a spec)"
            );
            return null;
        }

        if (options.Verify && options.OutputDir is null && options.OpenApiPath is null)
        {
            Console.Error.WriteLine(
                "error: '--verify' needs --output or --openapi — the committed spec to compare against"
            );
            return null;
        }

        // --routes is a listing mode that exits before emission, so it can never
        // perform the --verify comparison; accepting both would silently drop the
        // verification gate while printing routes and returning success.
        if (options.Verify && options.Routes)
        {
            Console.Error.WriteLine(
                "error: '--routes' and '--verify' cannot be combined — --routes lists endpoints and exits before any spec verification runs"
            );
            return null;
        }

        if (options.FromContractPath is not null)
        {
            return options;
        }

        if (options.FromOpenApiPath is not null)
        {
            if (
                options.SecuritySchemes is { Count: > 1 }
                || options.SecuritySchemes is [var importSecurity]
                    && !SecurityParser.IsValidSchemeName(importSecurity)
            )
            {
                Console.Error.WriteLine(
                    "error: --security with --from-openapi accepts one security scheme name, not an emit-time scheme definition"
                );
                return null;
            }

            return options;
        }

        return options.ProjectPath is null && files.Count == 0 ? null : options;
    }

    // OpenAPI server URLs are either absolute (http/https) or paths relative to the
    // host serving the spec — anything else is a typo, not a server.
    private static bool IsValidServerUrl(string value) =>
        value.StartsWith('/')
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");

    /// <summary>Help wins over every other argument, so a partial command line plus --help still shows usage.</summary>
    public static bool IsHelpRequest(string[] args) => args.Any(arg => arg is "--help" or "-h");

    public static void PrintUsage(TextWriter writer) =>
        writer.WriteLine(
            """
            Rivet — C# contracts to OpenAPI 3.1

            Usage:
              dotnet rivet --project <path.csproj> --output <dir>
              dotnet rivet <file.cs> [file2.cs ...] [--output <dir>]
              dotnet rivet --from-openapi <spec.json> --namespace <ns> [--output <dir>]
              dotnet rivet --from <contract.json> [--output <dir>]

            Writes an OpenAPI 3.1 spec (openapi.json) for the discovered contracts;
            omit --output for a stdout preview. Consume the spec with the OpenAPI
            ecosystem: openapi-typescript, openapi-fetch, openapi-zod-client, ...

            Options:
              -p, --project <path>       Path to .csproj file
              -o, --output <dir>         Output directory for openapi.json (omit for stdout preview)
              --openapi [file]           Explicit spec path override (relative paths resolve against --output)
              --security <[name=]spec>   Emit security scheme (repeatable); import accepts one scheme name
              --title <text>             Spec info.title (default: API)
              --version <text>           Spec info.version (default: 1.0.0) — there is no print-tool-version flag
              --server <url>             Spec servers entry (repeatable; omitted entirely when not given)
              --from <contract.json>     Emit OpenAPI from a Rivet contract JSON file
              --from-openapi <spec.json> Onboarding scaffold: one-shot import of an OpenAPI spec
                                         → C# contracts + DTOs; the C# becomes the source of
                                         truth (see docs/reference/import-profile)
              --namespace <ns>           Namespace for generated C# files (default: Generated)
              --check                    Verify contract coverage (missing impls, route/method mismatches)
              --verify                   Compare the spec against the existing file instead of writing —
                                         exit 1 on drift (CI gate for committed openapi.json)
              --routes                   List all discovered endpoints (method, route, handler)
              -q, --quiet                Suppress codegen output (useful with --check)
              -h, --help                 Show this help
            """
        );
}

internal sealed record RivetOptions(
    string? OutputDir,
    IReadOnlyList<string> Files,
    string? ProjectPath = null,
    string? OpenApiPath = null,
    string? FromOpenApiPath = null,
    string? ImportNamespace = null,
    bool Check = false,
    bool Quiet = false,
    bool Routes = false,
    string? FromContractPath = null,
    string? Title = null,
    string? Version = null,
    IReadOnlyList<string>? Servers = null,
    bool Verify = false,
    IReadOnlyList<string>? SecuritySchemes = null
);
