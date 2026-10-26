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
        string? Description = null,
        TsTypeMetadata? Metadata = null,
        TsScalarMetadata? ScalarMetadata = null
    )
    {
        // Exactly one of Type (an alias) or Properties (an object), as the schema's oneOf says.
        if (Type is not null && Properties is not null)
        {
            throw new ArgumentException(
                $"type '{Name}' has both 'type' and 'properties'; an alias carries its shape in 'type' and an object type in 'properties', so give it one"
            );
        }

        if (Type is null && Properties is null)
        {
            throw new ArgumentException(
                $"type '{Name}' has neither 'type' nor 'properties'; give an alias its 'type' or an object type its 'properties' (an empty object is \"properties\": [])"
            );
        }

        this.Name = Name;
        this.TypeParameters = TypeParameters;
        this.Properties = Properties ?? [];
        this.Type = Type;
        this.Description = Description;
        this.Metadata = Metadata;
        this.ScalarMetadata = ScalarMetadata;
    }

    public TsTypeDefinition(
        string name,
        IReadOnlyList<string> typeParameters,
        IReadOnlyList<TsPropertyDefinition> properties,
        string? Description = null,
        TsTypeMetadata? Metadata = null,
        TsScalarMetadata? ScalarMetadata = null
    )
        : this(
            name,
            typeParameters,
            Type: null,
            Properties: properties,
            Description: Description,
            Metadata: Metadata,
            ScalarMetadata: ScalarMetadata
        ) { }

    public TsTypeDefinition(
        string name,
        IReadOnlyList<string> typeParameters,
        TsType type,
        string? Description = null,
        TsTypeMetadata? Metadata = null,
        TsScalarMetadata? ScalarMetadata = null
    )
        : this(
            name,
            typeParameters,
            Type: type,
            Properties: null,
            Description: Description,
            Metadata: Metadata,
            ScalarMetadata: ScalarMetadata
        ) { }

    // Required members need a setter System.Text.Json can see, even when the constructor binds them.
    [JsonRequired, JsonInclude]
    public string Name { get; private init; }

    [JsonRequired, JsonInclude]
    public IReadOnlyList<string> TypeParameters { get; private init; }

    public IReadOnlyList<TsPropertyDefinition> Properties { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TsType? Type { get; }

    public string? Description { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TsTypeMetadata? Metadata { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TsScalarMetadata? ScalarMetadata { get; }
}

public enum TsTypeProvenance
{
    Component,
    Synthetic,
}

/// <summary>
/// A generated type's OpenAPI identity. <c>DiscriminatorSchema</c> belongs to a polymorphic
/// variant: the JSON of its tag property's schema as the source authored it (a plain
/// <c>{ "type": "string" }</c> with a description, say), emitted in place of <c>enum: [tag]</c>.
/// </summary>
public sealed record TsTypeMetadata(
    string? ComponentId,
    [property: JsonRequired] TsTypeProvenance Provenance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DiscriminatorSchema = null
);

public sealed record TsScalarMetadata(
    string? Description = null,
    string? DefaultValue = null,
    string? Example = null,
    string? Examples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsDeprecated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsReadOnly = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsWriteOnly = false,
    TsPropertyConstraints? Constraints = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsNullable = false,
    string? Title = null,
    TsSchemaXmlMetadata? Xml = null,
    string? Format = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsFormatSpecified = false,
    IReadOnlyList<string>? Required = null,
    // An enum the C# type cannot spell (booleans, numbers, arrays, objects, null), as JSON.
    string? Enum = null,
    // The authored enum lists null as a member (distinct from nullability itself).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool EnumListsNull =
        false
);

public sealed record TsSchemaXmlMetadata(
    string? Name = null,
    string? Namespace = null,
    string? Prefix = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsAttribute = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsWrapped = false
);

/// <summary>
/// A single property within a type definition.
/// </summary>
public sealed record TsPropertyDefinition(
    [property: JsonRequired] string Name,
    [property: JsonRequired] TsType Type,
    [property: JsonRequired, JsonPropertyName("optional")] bool IsOptional,
    [property:
        JsonPropertyName("deprecated"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        bool IsDeprecated = false,
    string? Format = null,
    string? DefaultValue = null,
    TsPropertyConstraints? Constraints = null,
    string? Description = null,
    string? Example = null,
    [property:
        JsonPropertyName("readOnly"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        bool IsReadOnly = false,
    [property:
        JsonPropertyName("writeOnly"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        bool IsWriteOnly = false,
    TsScalarMetadata? ScalarMetadata = null
);

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
    bool? UniqueItems = null,
    int? MinProperties = null,
    int? MaxProperties = null
)
{
    [JsonIgnore]
    public bool HasAny =>
        MinLength.HasValue
        || MaxLength.HasValue
        || Pattern is not null
        || Minimum.HasValue
        || Maximum.HasValue
        || ExclusiveMinimum.HasValue
        || ExclusiveMaximum.HasValue
        || MultipleOf.HasValue
        || MinItems.HasValue
        || MaxItems.HasValue
        || UniqueItems == true
        || MinProperties.HasValue
        || MaxProperties.HasValue;
}
