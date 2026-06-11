using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Rivet.Tool.Import;

/// <summary>
/// Entry point for importing an OpenAPI 3.1 JSON spec into C# contract + DTO source files.
/// </summary>
public static class OpenApiImporter
{
    public static ImportResult Import(string json, ImportOptions options)
    {
        var warnings = new List<string>();

        // I1: cyclic component-alias chains ("A": {$ref: B}, "B": {$ref: A}) overflow the
        // stack inside the OpenApi library's reference proxies on ANY member access, so
        // they must be broken at the raw-JSON level before parsing.
        json = BreakAliasCycles(json, warnings);

        var readResult = OpenApiDocument.Parse(json, "json");
        var doc = readResult.Document ?? throw new InvalidOperationException("Failed to parse OpenAPI document.");
        var files = new List<GeneratedFile>();
        var mapper = new SchemaMapper(warnings);

        // Parse schemas
        var schemas = doc.Components?.Schemas;

        var schemaResult = schemas is { Count: > 0 }
            ? mapper.MapSchemas(schemas)
            : new SchemaMapResult([], [], []);

        // Detect global security scheme from spec
        var globalSecurityScheme = options.SecurityScheme ?? DetectGlobalSecurity(doc, warnings);

        // Parse paths → contracts
        var contracts = doc.Paths is { Count: > 0 }
            ? ContractBuilder.BuildContracts(doc.Paths, mapper, globalSecurityScheme, warnings)
            : [];

        // Emit type files (records → Types/, enums → Types/, brands → Domain/)
        var ns = options.Namespace;

        foreach (var record in schemaResult.Records)
        {
            var content = CSharpWriter.WriteRecord(record, ns);
            files.Add(new GeneratedFile($"Types/{record.Name}.cs", content));
        }

        // Emit synthetic records from inline objects
        foreach (var record in mapper.ExtraRecords)
        {
            var content = CSharpWriter.WriteRecord(record, ns);
            files.Add(new GeneratedFile($"Types/{record.Name}.cs", content));
        }

        foreach (var enumDef in schemaResult.Enums)
        {
            var content = CSharpWriter.WriteEnum(enumDef, ns);
            files.Add(new GeneratedFile($"Types/{enumDef.Name}.cs", content));
        }

        // Emit synthetic enums from inline enum properties
        foreach (var enumDef in mapper.ExtraEnums)
        {
            var content = CSharpWriter.WriteEnum(enumDef, ns);
            files.Add(new GeneratedFile($"Types/{enumDef.Name}.cs", content));
        }

        foreach (var brand in schemaResult.Brands)
        {
            var content = CSharpWriter.WriteBrand(brand, ns);
            files.Add(new GeneratedFile($"Domain/{brand.Name}.cs", content));
        }

        // Emit contract files
        foreach (var contract in contracts)
        {
            var content = CSharpWriter.WriteContract(contract, ns);
            files.Add(new GeneratedFile($"Contracts/{contract.ClassName}.cs", content));
        }

        return new ImportResult(files, warnings);
    }

    /// <summary>
    /// I1: detects components/schemas entries that are pure $ref aliases forming a cycle
    /// and replaces them with empty placeholder schemas, with a loud warning per entry.
    /// Returns the input unchanged when no cycle exists (the common case).
    /// </summary>
    private static string BreakAliasCycles(string json, List<string> warnings)
    {
        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return json; // let the real parser produce its own error
        }

        if (root?["components"]?["schemas"] is not System.Text.Json.Nodes.JsonObject schemas)
        {
            return json;
        }

        const string prefix = "#/components/schemas/";
        var aliasTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in schemas)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj
                && obj.TryGetPropertyValue("$ref", out var refNode)
                && refNode is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out var refString)
                && refString.StartsWith(prefix, StringComparison.Ordinal))
            {
                aliasTargets[key] = refString[prefix.Length..];
            }
        }

        if (aliasTargets.Count == 0)
        {
            return json;
        }

        var cyclic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in aliasTargets.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { key };
            var current = key;
            while (aliasTargets.TryGetValue(current, out var next))
            {
                if (!visited.Add(next))
                {
                    // Everything on the chase path is unresolvable (in or pointing into the cycle)
                    cyclic.UnionWith(visited);
                    break;
                }

                current = next;
            }
        }

        if (cyclic.Count == 0)
        {
            return json;
        }

        foreach (var key in cyclic.OrderBy(k => k, StringComparer.Ordinal))
        {
            warnings.Add(
                $"Alias schema '{key}' is part of a $ref cycle — replaced with an empty schema; consumers resolve to an untyped object.");
            schemas[key] = new System.Text.Json.Nodes.JsonObject
            {
                ["description"] = "[rivet:unsupported] cyclic $ref alias",
            };
        }

        return root.ToJsonString();
    }

    private static string? DetectGlobalSecurity(OpenApiDocument doc, List<string> warnings)
    {
        if (doc.Security is null || doc.Security.Count == 0)
        {
            return null;
        }

        // I12: the contract model carries a single global scheme — OR alternatives, AND
        // combinations and scopes collapse to the first resolvable scheme, loudly.
        var schemeIds = doc.Security
            .SelectMany(req => req.Keys)
            .Select(scheme => scheme.Reference?.Id)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();

        if (schemeIds.Count > 1)
        {
            warnings.Add(
                $"Security schemes dropped: document declares [{string.Join(", ", schemeIds)}] — only the first scheme '{schemeIds[0]}' is imported; alternatives and scopes are not represented.");
        }

        return schemeIds.FirstOrDefault();
    }
}

public sealed record ImportOptions(
    string Namespace,
    string? SecurityScheme = null);

public sealed record ImportResult(
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<string> Warnings);

public sealed record GeneratedFile(
    string FileName,
    string Content);
