namespace Rivet;

/// <summary>
/// Generated-code metadata for an OpenAPI scalar or intentionally untyped component
/// that has no CLR type of its own.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetGeneratedSchemaAttribute(
    string name,
    string componentId,
    string? schemaType,
    string? format,
    bool nullable,
    string metadataJson,
    bool isEnum = false,
    string? schemaRef = null
) : Attribute
{
    public string Name { get; } = name;
    public string ComponentId { get; } = componentId;
    public string? SchemaType { get; } = schemaType;
    public string? Format { get; } = format;
    public bool Nullable { get; } = nullable;
    public string MetadataJson { get; } = metadataJson;
    public bool IsEnum { get; } = isEnum;
    public string? SchemaRef { get; } = schemaRef;
}

/// <summary>Generated use-site provenance for a primitive OpenAPI component reference.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RivetSchemaRefAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
