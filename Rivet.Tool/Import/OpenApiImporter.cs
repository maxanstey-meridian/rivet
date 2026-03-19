using System.Text.Json;

namespace Rivet.Tool.Import;

/// <summary>
/// Entry point for importing an OpenAPI 3.1 JSON spec into C# contract + DTO source files.
/// </summary>
public static class OpenApiImporter
{
    public static ImportResult Import(string json, ImportOptions options)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var warnings = new List<string>();
        var files = new List<GeneratedFile>();

        // Parse schemas
        var schemas = root.TryGetProperty("components", out var components)
            && components.TryGetProperty("schemas", out var schemasEl)
            ? schemasEl
            : default;

        var schemaResult = schemas.ValueKind == JsonValueKind.Object
            ? SchemaMapper.MapSchemas(schemas, warnings)
            : new SchemaMapResult([], [], []);

        // Detect global security scheme from spec
        var globalSecurityScheme = options.SecurityScheme ?? DetectGlobalSecurity(root);

        // Parse paths → contracts
        var contracts = root.TryGetProperty("paths", out var paths)
            ? ContractBuilder.BuildContracts(paths, schemaResult, globalSecurityScheme, warnings)
            : [];

        // Emit type files (records → Types/, enums → Types/, brands → Domain/)
        var ns = options.Namespace;

        foreach (var record in schemaResult.Records)
        {
            var content = CSharpWriter.WriteRecord(record, ns);
            files.Add(new GeneratedFile($"Types/{record.Name}.cs", content));
        }

        foreach (var enumDef in schemaResult.Enums)
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

    private static string? DetectGlobalSecurity(JsonElement root)
    {
        if (!root.TryGetProperty("security", out var security))
        {
            return null;
        }

        if (security.GetArrayLength() == 0)
        {
            return null;
        }

        // First security requirement's first scheme name
        foreach (var req in security.EnumerateArray())
        {
            foreach (var scheme in req.EnumerateObject())
            {
                return scheme.Name;
            }
        }

        return null;
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
