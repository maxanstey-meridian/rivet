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
        var readResult = OpenApiDocument.Parse(json, "json");
        var doc = readResult.Document ?? throw new InvalidOperationException("Failed to parse OpenAPI document.");
        var warnings = new List<string>();
        var files = new List<GeneratedFile>();
        var mapper = new SchemaMapper(warnings);

        // Parse schemas
        var schemas = doc.Components?.Schemas;

        var schemaResult = schemas is { Count: > 0 }
            ? mapper.MapSchemas(schemas)
            : new SchemaMapResult([], [], []);

        // Detect global security scheme from spec
        var globalSecurityScheme = options.SecurityScheme ?? DetectGlobalSecurity(doc);

        // Parse paths → contracts
        var contracts = doc.Paths is { Count: > 0 }
            ? ContractBuilder.BuildContracts(doc.Paths, mapper, globalSecurityScheme)
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

    private static string? DetectGlobalSecurity(OpenApiDocument doc)
    {
        if (doc.Security is null || doc.Security.Count == 0)
        {
            return null;
        }

        foreach (var req in doc.Security)
        {
            foreach (var (scheme, _) in req)
            {
                return scheme.Reference?.Id;
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
