namespace Rivet;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RivetDocumentInfoAttribute(
    string title,
    string version,
    string? description = null,
    string? termsOfService = null,
    string? contactName = null,
    string? contactUrl = null,
    string? contactEmail = null,
    bool contactPresent = false,
    string? licenseName = null,
    string? licenseUrl = null,
    string? licenseIdentifier = null
) : Attribute
{
    public string Title { get; } = title;
    public string Version { get; } = version;
    public string? Description { get; } = description;
    public string? TermsOfService { get; } = termsOfService;
    public string? ContactName { get; } = contactName;
    public string? ContactUrl { get; } = contactUrl;
    public string? ContactEmail { get; } = contactEmail;
    public bool ContactPresent { get; } = contactPresent;
    public string? LicenseName { get; } = licenseName;
    public string? LicenseUrl { get; } = licenseUrl;
    public string? LicenseIdentifier { get; } = licenseIdentifier;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetDocumentTagAttribute(
    int order,
    string name,
    string? description = null,
    string? externalDocsUrl = null,
    string? externalDocsDescription = null
) : Attribute
{
    public int Order { get; } = order;
    public string Name { get; } = name;
    public string? Description { get; } = description;
    public string? ExternalDocsUrl { get; } = externalDocsUrl;
    public string? ExternalDocsDescription { get; } = externalDocsDescription;
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RivetDocumentExternalDocsAttribute(string url, string? description = null)
    : Attribute
{
    public string Url { get; } = url;
    public string? Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetDocumentServerAttribute(int order, string url, string? description = null)
    : Attribute
{
    public int Order { get; } = order;
    public string Url { get; } = url;
    public string? Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetDocumentServerVariableAttribute(
    int serverOrder,
    int variableOrder,
    string name,
    string defaultValue,
    string[] allowedValues,
    string? description = null
) : Attribute
{
    public int ServerOrder { get; } = serverOrder;
    public int VariableOrder { get; } = variableOrder;
    public string Name { get; } = name;
    public string DefaultValue { get; } = defaultValue;
    public IReadOnlyList<string> AllowedValues { get; } = allowedValues;
    public string? Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetDocumentExampleAttribute(
    int order,
    string name,
    string? summary,
    string? description,
    string? jsonValue,
    string? externalValue
) : Attribute
{
    public int Order { get; } = order;
    public string Name { get; } = name;
    public string? Summary { get; } = summary;
    public string? Description { get; } = description;
    public string? JsonValue { get; } = jsonValue;
    public string? ExternalValue { get; } = externalValue;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetDocumentRequestBodyAttribute(
    int order,
    string name,
    string? description,
    bool required
) : Attribute
{
    public int Order { get; } = order;
    public string Name { get; } = name;
    public string? Description { get; } = description;
    public bool Required { get; } = required;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetDocumentRequestBodyContentAttribute(
    int requestBodyOrder,
    int contentOrder,
    string mediaType,
    Type? schemaType,
    bool isBinary,
    string? schemaRef,
    string? openApiSchemaType,
    string? format,
    bool isFormatSpecified
) : Attribute
{
    public int RequestBodyOrder { get; } = requestBodyOrder;
    public int ContentOrder { get; } = contentOrder;
    public string MediaType { get; } = mediaType;
    public Type? SchemaType { get; } = schemaType;
    public bool IsBinary { get; } = isBinary;
    public string? SchemaRef { get; } = schemaRef;
    public string? OpenApiSchemaType { get; } = openApiSchemaType;
    public string? Format { get; } = format;
    public bool IsFormatSpecified { get; } = isFormatSpecified;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetDocumentRequestBodyExampleAttribute(
    int requestBodyOrder,
    int exampleOrder,
    string mediaType,
    string? name,
    string? json,
    string? componentExampleId,
    string? resolvedJson,
    string? referencedComponentsJson
) : Attribute
{
    public int RequestBodyOrder { get; } = requestBodyOrder;
    public int ExampleOrder { get; } = exampleOrder;
    public string MediaType { get; } = mediaType;
    public string? Name { get; } = name;
    public string? Json { get; } = json;
    public string? ComponentExampleId { get; } = componentExampleId;
    public string? ResolvedJson { get; } = resolvedJson;
    public string? ReferencedComponentsJson { get; } = referencedComponentsJson;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetVendorExtensionAttribute(
    string ownerPointer,
    string name,
    string jsonValue
) : Attribute
{
    public string OwnerPointer { get; } = ownerPointer;
    public string Name { get; } = name;
    public string JsonValue { get; } = jsonValue;
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class RivetOperationProvenanceAttribute(
    bool operationIdPresent,
    string? operationId,
    bool deprecated,
    string[] tags,
    string? requestBodyDescription = null,
    bool hasServerOverride = false,
    string? rivetContract = null,
    string? rivetEndpoint = null,
    string? requestBodyComponentId = null
) : Attribute
{
    public bool OperationIdPresent { get; } = operationIdPresent;
    public string? OperationId { get; } = operationId;
    public bool Deprecated { get; } = deprecated;
    public IReadOnlyList<string> Tags { get; } = tags;
    public string? RequestBodyDescription { get; } = requestBodyDescription;
    public bool HasServerOverride { get; } = hasServerOverride;
    public string? RivetContract { get; } = rivetContract;
    public string? RivetEndpoint { get; } = rivetEndpoint;
    public string? RequestBodyComponentId { get; } = requestBodyComponentId;
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
public sealed class RivetOperationServerAttribute(int order, string url, string? description = null)
    : Attribute
{
    public int Order { get; } = order;
    public string Url { get; } = url;
    public string? Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
public sealed class RivetOperationServerVariableAttribute(
    int serverOrder,
    int variableOrder,
    string name,
    string defaultValue,
    string[] allowedValues,
    string? description = null
) : Attribute
{
    public int ServerOrder { get; } = serverOrder;
    public int VariableOrder { get; } = variableOrder;
    public string Name { get; } = name;
    public string DefaultValue { get; } = defaultValue;
    public IReadOnlyList<string> AllowedValues { get; } = allowedValues;
    public string? Description { get; } = description;
}
