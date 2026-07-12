using Microsoft.OpenApi;
using System.Text.Json;
using Rivet.Tool.Model;

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
        var doc =
            readResult.Document
            ?? throw new InvalidOperationException("Failed to parse OpenAPI document.");
        var files = new List<GeneratedFile>();
        var mapper = new SchemaMapper(warnings);

        // Parse schemas
        var schemas = doc.Components?.Schemas;

        var schemaResult = schemas is { Count: > 0 }
            ? mapper.MapSchemas(schemas)
            : new SchemaMapResult([], [], []);

        var securityMetadata = ReadSecurityMetadata(json);
        if (securityMetadata.Schemes.Count > 0 || securityMetadata.GlobalRequirements is not null)
        {
            files.Add(
                new GeneratedFile(
                    "RivetSecurity.cs",
                    CSharpWriter.WriteSecurityMetadata(securityMetadata)
                )
            );
        }

        var globalSecurityScheme = options.SecurityScheme;

        // Parse paths → contracts
        var contracts = doc.Paths is { Count: > 0 }
            ? ContractBuilder.BuildContracts(
                doc.Paths,
                mapper,
                globalSecurityScheme,
                warnings,
                doc.Components?.Examples
            )
            : [];

        // Emit type files (records → Types/, enums → Types/, brands → Domain/)
        var ns = options.Namespace;

        foreach (var record in schemaResult.Records)
        {
            // P2 wave 5: contract building may have augmented a component record with
            // [RivetHeader] properties (header-aware input reuse) — write the replacement.
            var effective = mapper.GetComponentRecordOverride(record.Name) ?? record;
            var content = CSharpWriter.WriteRecord(effective, ns);
            files.Add(new GeneratedFile($"Types/{effective.Name}.cs", content));
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
            files.Add(
                new GeneratedFile(
                    $"Contracts/{contract.ModuleName}/{contract.ClassName}.cs",
                    content
                )
            );
        }

        return new ImportResult(files, warnings);
    }

    private static ContractSecurityMetadata ReadSecurityMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var schemes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (
            root.TryGetProperty("components", out var components)
            && components.TryGetProperty("securitySchemes", out var securitySchemes)
        )
        {
            foreach (var scheme in securitySchemes.EnumerateObject())
            {
                schemes[scheme.Name] = scheme.Value.Clone();
            }
        }

        JsonElement? globalRequirements = root.TryGetProperty("security", out var security)
            ? security.Clone()
            : null;
        return new ContractSecurityMetadata(schemes, globalRequirements);
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
            if (
                node is System.Text.Json.Nodes.JsonObject obj
                && obj.TryGetPropertyValue("$ref", out var refNode)
                && refNode is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out var refString)
                && refString.StartsWith(prefix, StringComparison.Ordinal)
            )
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
                Diagnostics.Prefix(
                    Diagnostics.ImportAliasCycleBroken,
                    $"Alias schema '{key}' is part of a $ref cycle — replaced with an empty schema; consumers resolve to an untyped object."
                )
            );
            schemas[key] = new System.Text.Json.Nodes.JsonObject
            {
                ["description"] = "[rivet:unsupported] cyclic $ref alias",
            };
        }

        return root.ToJsonString();
    }

}

public sealed record ImportOptions(string Namespace, string? SecurityScheme = null);

public sealed record ImportResult(
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<string> Warnings
);

public sealed record GeneratedFile(string FileName, string Content);
