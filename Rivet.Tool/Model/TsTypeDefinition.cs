using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

/// <summary>
/// A full type declaration: either an object type definition or a named type alias.
/// </summary>
public sealed record TsTypeDefinition
{
    [JsonConstructor]
    private TsTypeDefinition(
        string Name,
        IReadOnlyList<string> TypeParameters,
        TsType? Type = null,
        IReadOnlyList<TsPropertyDefinition>? Properties = null,
        string? Description = null)
    {
        this.Name = Name;
        this.TypeParameters = TypeParameters;
        this.Properties = Properties ?? [];
        this.Type = Type;
        this.Description = Description;
    }

    public TsTypeDefinition(
        string name,
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<TsPropertyDefinition> properties,
        string? Description = null)
        : this(name, typeParameters, Type: null, Properties: properties, Description: Description)
    {
    }

    public TsTypeDefinition(
        string name,
        IReadOnlyList<string> typeParameters,
        TsType type,
        string? Description = null)
        : this(name, typeParameters, Type: type, Properties: null, Description: Description)
    {
    }

    public string Name { get; }

    public IReadOnlyList<string> TypeParameters { get; }

    public IReadOnlyList<TsPropertyDefinition> Properties { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TsType? Type { get; }

    public string? Description { get; }
}

/// <summary>
/// A single property within a type definition.
/// </summary>
public sealed record TsPropertyDefinition(
    string Name,
    TsType Type,
    [property: JsonPropertyName("optional")] bool IsOptional,
    [property: JsonPropertyName("deprecated"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsDeprecated = false,
    string? Format = null,
    string? DefaultValue = null,
    TsPropertyConstraints? Constraints = null,
    string? Description = null,
    string? Example = null,
    [property: JsonPropertyName("readOnly"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsReadOnly = false,
    [property: JsonPropertyName("writeOnly"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsWriteOnly = false);

public sealed record TsPropertyConstraints(
    int? MinLength = null,
    int? MaxLength = null,
    string? Pattern = null,
    double? Minimum = null,
    double? Maximum = null,
    double? ExclusiveMinimum = null,
    double? ExclusiveMaximum = null,
    double? MultipleOf = null,
    int? MinItems = null,
    int? MaxItems = null,
    bool? UniqueItems = null)
{
    [JsonIgnore]
    public bool HasAny => MinLength.HasValue || MaxLength.HasValue || Pattern is not null
        || Minimum.HasValue || Maximum.HasValue || ExclusiveMinimum.HasValue || ExclusiveMaximum.HasValue
        || MultipleOf.HasValue || MinItems.HasValue || MaxItems.HasValue || UniqueItems == true;
}
