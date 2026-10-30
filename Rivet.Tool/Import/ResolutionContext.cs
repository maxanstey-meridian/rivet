using System.Diagnostics;
using Microsoft.OpenApi;

namespace Rivet.Tool.Import;

/// <summary>
/// Mutable state accumulated during OpenAPI schema traversal.
/// </summary>
internal sealed class ResolutionContext(List<string> warnings)
{
    public List<GeneratedRecord> ExtraRecords { get; } = [];

    // Case-insensitive (like ReservedTypeNames): names become Types/{Name}.cs files,
    // and case-insensitive filesystems clobber case-variant siblings at write time.
    private readonly Dictionary<string, GeneratedRecord> _syntheticRecords = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<string, GeneratedEnum> _syntheticEnums = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Type names claimed by #/components/schemas (and generic templates) — synthetic records
    /// and enums must never reuse these, or two different shapes end up in one Types/{Name}.cs file.
    /// </summary>
    public HashSet<string> ReservedTypeNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a synthetic record, deduplicating by name + shape.
    /// Same name + identical shape → reuse the existing record. Same name + different shape
    /// (or a name reserved by a component schema) → the record gets a numeric-suffixed name so
    /// one consumer can never silently receive another consumer's type.
    /// Returns the name callers must reference.
    /// </summary>
    public string AddOrReuseExtraRecord(GeneratedRecord record)
    {
        var (name, existingName) = ResolveSyntheticName(
            record.Name,
            _syntheticRecords,
            existing => SameShape(existing, record),
            existing => existing.Name
        );
        if (name is null)
        {
            // Identical shape already registered — reuse ITS name (which may
            // differ in case from the caller's under the IgnoreCase comparer).
            return existingName!;
        }

        var toAdd = record.Name == name ? record : record with { Name = name };
        ExtraRecords.Add(toAdd);
        _syntheticRecords[name] = toAdd;
        return name;
    }

    /// <summary>
    /// Registers a synthetic enum with the same name + shape dedup rules as AddOrReuseExtraRecord.
    /// Returns the name callers must reference.
    /// </summary>
    public string AddOrReuseExtraEnum(GeneratedEnum enumDef)
    {
        var (name, existingName) = ResolveSyntheticName(
            enumDef.Name,
            _syntheticEnums,
            existing => existing.Members.SequenceEqual(enumDef.Members),
            existing => existing.Name
        );
        if (name is null)
        {
            return existingName!;
        }

        var toAdd = enumDef.Name == name ? enumDef : enumDef with { Name = name };
        ExtraEnums.Add(toAdd);
        _syntheticEnums[name] = toAdd;
        return name;
    }

    /// <summary>
    /// Finds the name a new synthetic type must use: (name, null) when free —
    /// the base name or a numeric-suffixed variant when the base is
    /// reserved/claimed by a different shape — or (null, existingName) when an
    /// identical shape of the same kind is already registered (caller reuses the
    /// REGISTERED name, which may differ in case under the IgnoreCase comparer).
    /// </summary>
    private (string? NewName, string? ExistingName) ResolveSyntheticName<T>(
        string baseName,
        Dictionary<string, T> sameKind,
        Func<T, bool> isSameShape,
        Func<T, string> nameOf
    )
    {
        foreach (var candidate in Naming.NameCandidates(baseName))
        {
            if (ReservedTypeNames.Contains(candidate))
            {
                continue;
            }

            if (sameKind.TryGetValue(candidate, out var existing) && isSameShape(existing))
            {
                return (null, nameOf(existing));
            }

            if (
                !_syntheticRecords.ContainsKey(candidate) && !_syntheticEnums.ContainsKey(candidate)
            )
            {
                return (candidate, null);
            }
        }

        throw new UnreachableException();
    }

    private static bool SameShape(GeneratedRecord a, GeneratedRecord b)
    {
        return a.Properties.SequenceEqual(b.Properties)
            && (a.TypeParameters ?? []).SequenceEqual(b.TypeParameters ?? [])
            && a.Description == b.Description
            && a.HasExtensionData == b.HasExtensionData
            && a.DeclaresAdditionalProperties == b.DeclaresAdditionalProperties
            && a.IsClosed == b.IsClosed;
    }

    public List<GeneratedEnum> ExtraEnums { get; } = [];

    /// <summary>
    /// Records generated from #/components/schemas, keyed by final (deduped) C# name.
    /// Used for shape-checked reuse of synthesized parameter-input records.
    /// </summary>
    public Dictionary<string, GeneratedRecord> MappedComponentRecords { get; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Component records that gained [RivetHeader] properties during contract
    /// building (header-aware input reuse). MapSchemas has already materialized its result
    /// by then, so OpenApiImporter consults these replacements when writing Types/ files.
    /// </summary>
    public Dictionary<string, GeneratedRecord> ComponentRecordOverrides { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Replaces a mapped component record (header augmentation) in both registries.</summary>
    public void ReplaceComponentRecord(string name, GeneratedRecord replacement)
    {
        MappedComponentRecords[name] = replacement;
        ComponentRecordOverrides[name] = replacement;
    }

    /// <summary>
    /// Each component record an allOf composes as a <c>$ref</c>'d branch, with the first
    /// composition that does. A closed branch can't be closed on its own (RIV3033).
    /// </summary>
    public Dictionary<string, string> AllOfBranches { get; } = new(StringComparer.Ordinal);

    /// <summary>Why an allOf composition can't be closed (RIV3033).</summary>
    public const string ClosedAllOfReason =
        "it is an allOf composition, where a closed part refuses the members the other parts add";

    /// <summary>Why a oneOf or anyOf union can't be closed (RIV3033).</summary>
    public const string ClosedUnionReason =
        "it is a oneOf/anyOf union, whose members come from its variants, which its own additionalProperties: false refuses";

    /// <summary>
    /// Where each schema object sits in the document, for naming an inline one
    /// (<see cref="SchemaLocations.Read"/>).
    /// </summary>
    public IReadOnlyDictionary<IOpenApiSchema, string> SchemaPointers { get; set; } =
        new Dictionary<IOpenApiSchema, string>();

    private readonly HashSet<string> _closedSchemasLeftOpen = new(StringComparer.Ordinal);

    /// <summary>
    /// Warns, once per schema, that the component <paramref name="componentKey"/> says
    /// additionalProperties: false but its record stays open, because of
    /// <paramref name="why"/> (RIV3033).
    /// </summary>
    public void WarnClosedSchemaLeftOpen(string componentKey, string why) =>
        WarnClosedLeftOpen($"'{componentKey}'", why);

    /// <summary>
    /// <see cref="WarnClosedSchemaLeftOpen(string, string)"/> for <paramref name="schema"/>,
    /// named by its component key, or by its pointer when it's inline. One the document
    /// doesn't hold is named by <paramref name="recordName"/>, the record it imports as.
    /// </summary>
    public void WarnClosedSchemaLeftOpen(IOpenApiSchema schema, string recordName, string why) =>
        WarnClosedLeftOpen(
            schema is OpenApiSchemaReference reference
                    ? $"'{SchemaMapper.DecodeComponentId(reference.Reference.Id)}'"
                : SchemaPointers.TryGetValue(schema, out var pointer)
                    ? SchemaLocations.ComponentKey(pointer) is { } key ? $"'{key}'"
                        : $"the schema at {pointer}"
                : $"the schema imported as '{recordName}'",
            why
        );

    private void WarnClosedLeftOpen(string schema, string why)
    {
        if (_closedSchemasLeftOpen.Add(schema))
        {
            Warnings.Add(
                Diagnostics.Prefix(
                    Diagnostics.ImportClosedSchemaLeftOpen,
                    $"Closed schema imported open: {schema} says additionalProperties: false, but {why}. "
                        + "Its record accepts and drops an unknown member instead of refusing it."
                )
            );
        }
    }

    /// <summary>The component key a component record's C# name was mapped from.</summary>
    public string? ComponentKey(string typeName) =>
        SchemaNameMap.FirstOrDefault(entry => entry.Value == typeName).Key;

    public List<string> Warnings { get; } = warnings;
    public HashSet<string> Resolving { get; } = [];
    public Dictionary<string, string> SchemaFingerprints { get; } = new();
    public Dictionary<string, string> SchemaNameMap { get; } = new();
    public int SyntheticCounter { get; set; }
    public int RecursionDepth { get; set; }

    public string NextSyntheticName(string prefix) => $"{prefix}{++SyntheticCounter}";

    /// <summary>The C# name mapped for a component key, or its PascalCase form.</summary>
    public string TypeName(string componentId) =>
        SchemaNameMap.TryGetValue(componentId, out var mapped)
            ? mapped
            : Naming.ToPascalCaseFromSegments(componentId);
}

/// <summary>Where each schema object sits in a document, as a JSON pointer.</summary>
internal static class SchemaLocations
{
    private const string ComponentPrefix = "#/components/schemas/";

    public static IReadOnlyDictionary<IOpenApiSchema, string> Read(OpenApiDocument document)
    {
        var visitor = new Visitor();
        new OpenApiWalker(visitor).Walk(document);
        return visitor.Pointers;
    }

    /// <summary>The component key <paramref name="pointer"/> names a schema component by, if it does.</summary>
    public static string? ComponentKey(string pointer) =>
        pointer.StartsWith(ComponentPrefix, StringComparison.Ordinal)
        && pointer.IndexOf('/', ComponentPrefix.Length) < 0
            ? JsonPointer.Unescape(pointer[ComponentPrefix.Length..])
            : null;

    private sealed class Visitor : OpenApiVisitorBase
    {
        public Dictionary<IOpenApiSchema, string> Pointers { get; } =
            new(ReferenceEqualityComparer.Instance);

        public override void Visit(IOpenApiSchema schema) => Pointers.TryAdd(schema, PathString);
    }
}
