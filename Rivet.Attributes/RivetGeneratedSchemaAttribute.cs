namespace Rivet;

/// <summary>
/// Generated-code metadata for an OpenAPI non-object component that has no CLR type
/// declaration of its own.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetGeneratedSchemaAttribute : Attribute
{
    public RivetGeneratedSchemaAttribute(
        string name,
        string componentId,
        string? schemaType,
        string? format,
        bool nullable,
        string metadataJson,
        bool isEnum = false,
        string? schemaRef = null
    )
        : this(
            name,
            componentId,
            schemaType,
            format,
            nullable,
            metadataJson,
            isEnum,
            schemaRef,
            false,
            null
        ) { }

    public RivetGeneratedSchemaAttribute(
        string name,
        string componentId,
        string? schemaType,
        string? format,
        bool nullable,
        string metadataJson,
        bool isEnum,
        string? schemaRef,
        bool isArray,
        string? itemSchemaRef
    )
    {
        Name = name;
        ComponentId = componentId;
        SchemaType = schemaType;
        Format = format;
        Nullable = nullable;
        MetadataJson = metadataJson;
        IsEnum = isEnum;
        SchemaRef = schemaRef;
        IsArray = isArray;
        ItemSchemaRef = itemSchemaRef;
    }

    public string Name { get; }
    public string ComponentId { get; }
    public string? SchemaType { get; }
    public string? Format { get; }
    public bool Nullable { get; }
    public string MetadataJson { get; }
    public bool IsEnum { get; }
    public string? SchemaRef { get; }
    public bool IsArray { get; }
    public string? ItemSchemaRef { get; }
}

/// <summary>
/// Generated use-site provenance for a non-object OpenAPI component reference, at the
/// property schema itself or at a JSON pointer inside it (<c>/items</c>,
/// <c>/additionalProperties</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
public sealed class RivetSchemaRefAttribute(string name, string pointer = "") : Attribute
{
    public string Name { get; } = name;
    public string Pointer { get; } = pointer;
}
