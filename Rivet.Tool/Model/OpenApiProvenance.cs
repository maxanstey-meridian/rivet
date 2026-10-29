using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

public sealed record OpenApiDocumentProvenance(
    OpenApiInfoProvenance Info,
    IReadOnlyList<OpenApiTagProvenance> Tags,
    OpenApiExternalDocsProvenance? ExternalDocs,
    IReadOnlyList<OpenApiServerProvenance> Servers,
    IReadOnlyList<OpenApiComponentExampleProvenance> ComponentExamples,
    IReadOnlyList<OpenApiComponentRequestBodyProvenance>? ComponentRequestBodies = null,
    IReadOnlyList<OpenApiPreservedFieldProvenance>? PreservedFields = null,
    IReadOnlyList<OpenApiRawComponentProvenance>? ComponentParameters = null,
    IReadOnlyList<OpenApiRawComponentProvenance>? ComponentResponses = null,
    IReadOnlyList<OpenApiRawComponentProvenance>? ComponentSchemas = null,
    IReadOnlyList<OpenApiImportedSourceFileProvenance>? ImportedSourceFiles = null,
    IReadOnlyList<OpenApiRawComponentProvenance>? ComponentHeaders = null
);

public sealed record OpenApiImportedSourceFileProvenance(string Path, string Fingerprint);

/// <summary>A component (parameter, response or schema) kept as its raw JSON.</summary>
public sealed record OpenApiRawComponentProvenance(string Name, string Json);

public sealed record OpenApiPreservedFieldProvenance(
    string OwnerPointer,
    string Name,
    string JsonValue
);

public sealed record OpenApiComponentRequestBodyProvenance(
    string Name,
    string? Description,
    bool Required,
    IReadOnlyList<OpenApiRequestBodyContentProvenance> Contents,
    IReadOnlyList<TsEndpointExample>? Examples = null
);

public sealed record OpenApiRequestBodyContentProvenance(
    string MediaType,
    string? CSharpTypeName,
    TsType? Schema,
    bool IsBinary = false,
    string? SchemaRef = null,
    string? SchemaType = null,
    string? Format = null,
    bool IsFormatSpecified = false,
    string? SchemaJson = null
);

public sealed record OpenApiComponentExampleProvenance
{
    public OpenApiComponentExampleProvenance(
        string name,
        string? summary,
        string? description,
        string? jsonValue,
        string? externalValue
    )
    {
        // OpenAPI makes both optional and only mutually exclusive: an example with neither
        // documents through its summary and description alone.
        if (jsonValue is not null && externalValue is not null)
        {
            throw new ArgumentException(
                "A component example carries at most one of JSON value and externalValue."
            );
        }

        Name = name;
        Summary = summary;
        Description = description;
        JsonValue = jsonValue;
        ExternalValue = externalValue;
    }

    public string Name { get; }
    public string? Summary { get; }
    public string? Description { get; }
    public string? JsonValue { get; }
    public string? ExternalValue { get; }
}

public sealed record OpenApiInfoProvenance(
    string Title,
    string Version,
    string? Description = null,
    string? TermsOfService = null,
    OpenApiContactProvenance? Contact = null,
    OpenApiLicenseProvenance? License = null
);

public sealed record OpenApiContactProvenance(string? Name, string? Url, string? Email);

public sealed record OpenApiLicenseProvenance(
    string Name,
    string? Url = null,
    string? Identifier = null
);

public sealed record OpenApiTagProvenance(
    string Name,
    string? Description = null,
    OpenApiExternalDocsProvenance? ExternalDocs = null
);

public sealed record OpenApiExternalDocsProvenance(string Url, string? Description = null);

public sealed record OpenApiServerProvenance(
    [property: JsonRequired] string Url,
    string? Description,
    [property: JsonRequired] IReadOnlyList<OpenApiServerVariableProvenance> Variables
);

public sealed record OpenApiServerVariableProvenance(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string DefaultValue,
    [property: JsonRequired] IReadOnlyList<string> AllowedValues,
    string? Description
);

public sealed record OpenApiOperationProvenance(
    [property: JsonRequired] bool OperationIdPresent,
    string? OperationId,
    [property: JsonRequired] IReadOnlyList<string> Tags,
    [property: JsonRequired] bool Deprecated,
    IReadOnlyList<OpenApiServerProvenance>? ServerOverride,
    string? RequestBodyDescription,
    OpenApiRivetIdentityProvenance? RivetIdentity,
    string? RequestBodyComponentId = null,
    IReadOnlyList<OpenApiParameterComponentReference>? ParameterComponentReferences = null,
    IReadOnlyList<OpenApiResponseComponentReference>? ResponseComponentReferences = null,
    OpenApiOperationSchemaProvenance? Schemas = null,
    IReadOnlyList<OpenApiResponseHeaderComponentReference>? ResponseHeaderComponentReferences = null
);

public sealed record OpenApiOperationSchemaProvenance(
    [property: JsonRequired] IReadOnlyList<OpenApiParameterSchemaProvenance> Parameters,
    [property: JsonRequired] IReadOnlyList<OpenApiRequestSchemaProvenance> Requests,
    [property: JsonRequired] IReadOnlyList<OpenApiResponseSchemaProvenance> Responses
);

public sealed record OpenApiParameterSchemaProvenance(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Location,
    [property: JsonRequired] string Json
);

public sealed record OpenApiRequestSchemaProvenance(
    [property: JsonRequired] string MediaType,
    [property: JsonRequired] string Json
);

public sealed record OpenApiResponseSchemaProvenance(
    [property: JsonRequired] string StatusKey,
    [property: JsonRequired] string MediaType,
    [property: JsonRequired] string Json
);

public sealed record OpenApiParameterComponentReference(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Location,
    [property: JsonRequired] string ComponentId
);

public sealed record OpenApiResponseComponentReference(
    [property: JsonRequired] string StatusKey,
    [property: JsonRequired] string ComponentId
);

public sealed record OpenApiResponseHeaderComponentReference(
    [property: JsonRequired] string StatusKey,
    [property: JsonRequired] string HeaderName,
    [property: JsonRequired] string ComponentId
);

public sealed record OpenApiRivetIdentityProvenance(string? Contract, string? Endpoint);
