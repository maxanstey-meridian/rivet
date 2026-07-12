namespace Rivet.Tool.Model;

public sealed record OpenApiDocumentProvenance(
    OpenApiInfoProvenance Info,
    IReadOnlyList<OpenApiTagProvenance> Tags,
    OpenApiExternalDocsProvenance? ExternalDocs,
    IReadOnlyList<OpenApiServerProvenance> Servers,
    IReadOnlyList<OpenApiComponentExampleProvenance> ComponentExamples,
    IReadOnlyList<OpenApiComponentRequestBodyProvenance>? ComponentRequestBodies = null,
    IReadOnlyList<OpenApiVendorExtensionProvenance>? VendorExtensions = null
);

public sealed record OpenApiVendorExtensionProvenance(
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
    bool IsFormatSpecified = false
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
        if ((jsonValue is null) == (externalValue is null))
        {
            throw new ArgumentException(
                "A component example requires exactly one of JSON value or externalValue."
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
    string Url,
    string? Description,
    IReadOnlyList<OpenApiServerVariableProvenance> Variables
);

public sealed record OpenApiServerVariableProvenance(
    string Name,
    string DefaultValue,
    IReadOnlyList<string> AllowedValues,
    string? Description
);

public sealed record OpenApiOperationProvenance(
    bool OperationIdPresent,
    string? OperationId,
    IReadOnlyList<string> Tags,
    bool Deprecated,
    IReadOnlyList<OpenApiServerProvenance>? ServerOverride,
    string? RequestBodyDescription,
    OpenApiRivetIdentityProvenance? RivetIdentity,
    string? RequestBodyComponentId = null
);

public sealed record OpenApiRivetIdentityProvenance(string? Contract, string? Endpoint);
