namespace Rivet.Tool.Import;

/// <summary>
/// Mutable state accumulated during OpenAPI schema traversal.
/// </summary>
internal sealed class ResolutionContext(List<string> warnings)
{
    public List<GeneratedRecord> ExtraRecords { get; } = [];

    private readonly Dictionary<string, object> _syntheticsByName = new(StringComparer.Ordinal);

    /// <summary>
    /// Type names claimed by #/components/schemas (and generic templates) — synthetic records
    /// and enums must never reuse these, or two different shapes end up in one Types/{Name}.cs file.
    /// </summary>
    public HashSet<string> ReservedTypeNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a synthetic record, deduplicating by name + shape (I3 guard).
    /// Same name + identical shape → reuse the existing record. Same name + different shape
    /// (or a name reserved by a component schema) → the record gets a numeric-suffixed name so
    /// one consumer can never silently receive another consumer's type.
    /// Returns the name callers must reference.
    /// </summary>
    public string AddOrReuseExtraRecord(GeneratedRecord record)
    {
        var name = ResolveSyntheticName(record.Name, existing =>
            existing is GeneratedRecord other && SameShape(other, record));

        if (name is null)
        {
            return record.Name; // identical shape already registered under this name
        }

        var toAdd = record.Name == name ? record : record with { Name = name };
        ExtraRecords.Add(toAdd);
        _syntheticsByName[name] = toAdd;
        return name;
    }

    /// <summary>
    /// Registers a synthetic enum with the same name + shape dedup rules as AddOrReuseExtraRecord.
    /// Returns the name callers must reference.
    /// </summary>
    public string AddOrReuseExtraEnum(GeneratedEnum enumDef)
    {
        var name = ResolveSyntheticName(enumDef.Name, existing =>
            existing is GeneratedEnum other && other.Members.SequenceEqual(enumDef.Members));

        if (name is null)
        {
            return enumDef.Name;
        }

        var toAdd = enumDef.Name == name ? enumDef : enumDef with { Name = name };
        ExtraEnums.Add(toAdd);
        _syntheticsByName[name] = toAdd;
        return name;
    }

    /// <summary>
    /// Finds the name a new synthetic type must use: the base name when free, a numeric-suffixed
    /// variant when the base name is reserved/claimed by a different shape, or null when an
    /// identical shape is already registered under a matching name (caller reuses it).
    /// Returns null ONLY for exact-shape matches on the unsuffixed base name.
    /// </summary>
    private string? ResolveSyntheticName(string baseName, Func<object, bool> isSameShape)
    {
        var candidate = baseName;
        var suffix = 1;

        while (true)
        {
            if (!ReservedTypeNames.Contains(candidate))
            {
                if (!_syntheticsByName.TryGetValue(candidate, out var existing))
                {
                    return candidate;
                }

                if (isSameShape(existing))
                {
                    return null;
                }
            }

            suffix++;
            candidate = $"{baseName}{suffix}";
        }
    }

    private static bool SameShape(GeneratedRecord a, GeneratedRecord b)
    {
        return a.Properties.SequenceEqual(b.Properties)
            && (a.TypeParameters ?? []).SequenceEqual(b.TypeParameters ?? [])
            && a.Description == b.Description;
    }
    public List<GeneratedEnum> ExtraEnums { get; } = [];

    /// <summary>
    /// Records generated from #/components/schemas, keyed by final (deduped) C# name.
    /// Used for shape-checked reuse of synthesized parameter-input records (I3 residual).
    /// </summary>
    public Dictionary<string, GeneratedRecord> MappedComponentRecords { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = warnings;
    public HashSet<string> Resolving { get; } = [];
    public Dictionary<string, string> SchemaFingerprints { get; } = new();
    public Dictionary<string, string> SchemaNameMap { get; } = new();
    public int SyntheticCounter { get; set; }
    public int RecursionDepth { get; set; }

    public string NextSyntheticName(string prefix) => $"{prefix}{++SyntheticCounter}";
}
