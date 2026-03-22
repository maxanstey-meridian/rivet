namespace Rivet.Tool.Import;

/// <summary>
/// Mutable state accumulated during OpenAPI schema traversal.
/// </summary>
internal sealed class ResolutionContext(List<string> warnings)
{
    public List<GeneratedRecord> ExtraRecords { get; } = [];
    public List<GeneratedEnum> ExtraEnums { get; } = [];
    public List<string> Warnings { get; } = warnings;
    public HashSet<string> Resolving { get; } = [];
    public Dictionary<string, string> SchemaFingerprints { get; } = new();
    public Dictionary<string, string> SchemaNameMap { get; } = new();
    public int SyntheticCounter { get; set; }
    public int RecursionDepth { get; set; }

    public string NextSyntheticName(string prefix) => $"{prefix}{++SyntheticCounter}";
}
