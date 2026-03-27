using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

/// <summary>
/// A full type declaration: export type Foo&lt;T&gt; = { prop: Type; ... }
/// </summary>
public sealed record TsTypeDefinition(
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<TsPropertyDefinition> Properties,
    string? Description = null);

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
