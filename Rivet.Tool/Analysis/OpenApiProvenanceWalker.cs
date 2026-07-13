using System.Text.Json;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

internal static class OpenApiProvenanceWalker
{
    public static OpenApiDocumentProvenance? Walk(
        Compilation compilation,
        TypeWalker? typeWalker = null
    )
    {
        var attributes = compilation.Assembly.GetAttributes();
        var infoAttributes = attributes
            .Where(attribute => Is(attribute, "Rivet.RivetDocumentInfoAttribute"))
            .ToList();
        if (infoAttributes.Count == 0)
        {
            return null;
        }
        if (infoAttributes.Count != 1)
        {
            throw new ContractAnalysisException("Multiple Rivet document info declarations found.");
        }

        var infoArguments = infoAttributes[0].ConstructorArguments;
        if (infoArguments.Length != 11)
        {
            throw new ContractAnalysisException("Invalid Rivet document info metadata.");
        }

        var contact = RequiredBool(infoArguments[7], "document contact presence")
            ? new OpenApiContactProvenance(
                StringValue(infoArguments[4]),
                StringValue(infoArguments[5]),
                StringValue(infoArguments[6])
            )
            : null;
        var license = StringValue(infoArguments[8]) is { } licenseName
            ? new OpenApiLicenseProvenance(
                licenseName,
                StringValue(infoArguments[9]),
                StringValue(infoArguments[10])
            )
            : null;
        var info = new OpenApiInfoProvenance(
            RequiredString(infoArguments[0], "document title"),
            RequiredString(infoArguments[1], "document version"),
            StringValue(infoArguments[2]),
            StringValue(infoArguments[3]),
            contact,
            license
        );

        var tags = attributes
            .Where(attribute => Is(attribute, "Rivet.RivetDocumentTagAttribute"))
            .Select(attribute =>
            {
                var args = attribute.ConstructorArguments;
                var docsUrl = StringValue(args[3]);
                return (
                    Order: RequiredInt(args[0], "document tag order"),
                    Tag: new OpenApiTagProvenance(
                        RequiredString(args[1], "document tag name"),
                        StringValue(args[2]),
                        docsUrl is null
                            ? null
                            : new OpenApiExternalDocsProvenance(docsUrl, StringValue(args[4]))
                    )
                );
            })
            .OrderBy(value => value.Order)
            .Select(value => value.Tag)
            .ToList();
        var externalDocsAttributes = attributes
            .Where(attribute => Is(attribute, "Rivet.RivetDocumentExternalDocsAttribute"))
            .ToList();
        if (externalDocsAttributes.Count > 1)
        {
            throw new ContractAnalysisException(
                "Multiple Rivet document external-docs declarations found."
            );
        }
        var externalDocs =
            externalDocsAttributes.Count == 1
                ? new OpenApiExternalDocsProvenance(
                    RequiredString(
                        externalDocsAttributes[0].ConstructorArguments[0],
                        "document external-docs URL"
                    ),
                    StringValue(externalDocsAttributes[0].ConstructorArguments[1])
                )
                : null;
        var servers = ReadServers(
            attributes,
            "Rivet.RivetDocumentServerAttribute",
            "Rivet.RivetDocumentServerVariableAttribute"
        );
        var componentExamples = attributes
            .Where(attribute => Is(attribute, "Rivet.RivetDocumentExampleAttribute"))
            .Select(attribute =>
            {
                var args = attribute.ConstructorArguments;
                if (args.Length != 6)
                {
                    throw new ContractAnalysisException(
                        "Invalid Rivet document component-example metadata."
                    );
                }

                var name = RequiredString(args[1], "document component-example name");
                var jsonValue = StringValue(args[4]);
                var externalValue = StringValue(args[5]);
                if ((jsonValue is null) == (externalValue is null))
                {
                    throw new ContractAnalysisException(
                        $"Rivet document component example '{name}' requires exactly one of JSON value or externalValue."
                    );
                }

                return (
                    Order: RequiredInt(args[0], "document component-example order"),
                    Example: new OpenApiComponentExampleProvenance(
                        name,
                        StringValue(args[2]),
                        StringValue(args[3]),
                        jsonValue,
                        externalValue
                    )
                );
            })
            .OrderBy(value => value.Order)
            .Select(value => value.Example)
            .ToList();
        var requestBodyContents = attributes
            .Where(attribute => Is(attribute, "Rivet.RivetDocumentRequestBodyContentAttribute"))
            .Select(attribute =>
            {
                var args = attribute.ConstructorArguments;
                if (args.Length != 9)
                {
                    throw new ContractAnalysisException(
                        "Invalid Rivet document request-body content metadata."
                    );
                }

                var schemaType = args[3].Value as ITypeSymbol;
                TsType? schema = null;
                if (schemaType is not null)
                {
                    if (typeWalker is null)
                    {
                        throw new ContractAnalysisException(
                            "Rivet document request-body content requires a type walker."
                        );
                    }
                    var openApiSchemaType = StringValue(args[6]);
                    var format = StringValue(args[7]);
                    schema = ApplySchemaLeafMetadata(
                        typeWalker.MapType(schemaType),
                        openApiSchemaType,
                        format
                    );
                    schema = typeWalker.ApplyGeneratedSchemaRef(
                        schema,
                        StringValue(args[5]),
                        $"document request-body content '{RequiredString(args[2], "media type")}'"
                    );
                }

                return (
                    RequestBodyOrder: RequiredInt(args[0], "request-body order"),
                    ContentOrder: RequiredInt(args[1], "request-body content order"),
                    Content: new OpenApiRequestBodyContentProvenance(
                        RequiredString(args[2], "request-body media type"),
                        null,
                        schema,
                        RequiredBool(args[4], "request-body binary flag"),
                        StringValue(args[5]),
                        StringValue(args[6]),
                        StringValue(args[7]),
                        RequiredBool(args[8], "request-body format-presence flag")
                    )
                );
            })
            .ToList();
        var requestBodyExamples = attributes
            .Where(attribute => Is(attribute, "Rivet.RivetDocumentRequestBodyExampleAttribute"))
            .Select(attribute =>
            {
                var args = attribute.ConstructorArguments;
                if (args.Length != 8)
                {
                    throw new ContractAnalysisException(
                        "Invalid Rivet document request-body example metadata."
                    );
                }
                var referencedComponentsJson = StringValue(args[7]);
                return (
                    RequestBodyOrder: RequiredInt(args[0], "request-body order"),
                    ExampleOrder: RequiredInt(args[1], "request-body example order"),
                    Example: new TsEndpointExample(
                        RequiredString(args[2], "request-body example media type"),
                        StringValue(args[3]),
                        StringValue(args[4]),
                        StringValue(args[5]),
                        StringValue(args[6]),
                        referencedComponentsJson is null
                            ? null
                            : JsonSerializer.Deserialize<Dictionary<string, string>>(
                                referencedComponentsJson
                            )
                    )
                );
            })
            .ToList();
        var componentRequestBodies = attributes
            .Where(attribute => Is(attribute, "Rivet.RivetDocumentRequestBodyAttribute"))
            .Select(attribute =>
            {
                var args = attribute.ConstructorArguments;
                if (args.Length != 4)
                {
                    throw new ContractAnalysisException(
                        "Invalid Rivet document request-body metadata."
                    );
                }
                var order = RequiredInt(args[0], "request-body order");
                return (
                    Order: order,
                    RequestBody: new OpenApiComponentRequestBodyProvenance(
                        RequiredString(args[1], "request-body name"),
                        StringValue(args[2]),
                        RequiredBool(args[3], "request-body required flag"),
                        requestBodyContents
                            .Where(content => content.RequestBodyOrder == order)
                            .OrderBy(content => content.ContentOrder)
                            .Select(content => content.Content)
                            .ToList(),
                        requestBodyExamples
                            .Where(example => example.RequestBodyOrder == order)
                            .OrderBy(example => example.ExampleOrder)
                            .Select(example => example.Example)
                            .ToList()
                    )
                );
            })
            .OrderBy(value => value.Order)
            .Select(value => value.RequestBody)
            .ToList();
        var componentParameters = ReadJsonComponents<OpenApiComponentParameterProvenance>(
            attributes,
            "Rivet.RivetDocumentParameterAttribute",
            "parameter",
            static (name, json) => new OpenApiComponentParameterProvenance(name, json)
        );
        var componentResponses = ReadJsonComponents<OpenApiComponentResponseProvenance>(
            attributes,
            "Rivet.RivetDocumentResponseAttribute",
            "response",
            static (name, json) => new OpenApiComponentResponseProvenance(name, json)
        );
        var vendorExtensions = attributes
            .Where(attribute => Is(attribute, "Rivet.RivetVendorExtensionAttribute"))
            .Select(attribute =>
            {
                var args = attribute.ConstructorArguments;
                if (args.Length != 3)
                {
                    throw new ContractAnalysisException("Invalid Rivet vendor-extension metadata.");
                }

                var extension = new OpenApiVendorExtensionProvenance(
                    RequiredString(args[0], "vendor-extension owner pointer"),
                    RequiredString(args[1], "vendor-extension name"),
                    RequiredString(args[2], "vendor-extension JSON value")
                );
                if (!extension.Name.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ContractAnalysisException(
                        $"Invalid Rivet vendor-extension name '{extension.Name}'."
                    );
                }
                try
                {
                    using var _ = JsonDocument.Parse(extension.JsonValue);
                }
                catch (JsonException exception)
                {
                    throw new ContractAnalysisException(
                        $"Invalid Rivet vendor-extension JSON for '{extension.Name}': {exception.Message}"
                    );
                }
                return extension;
            })
            .ToList();

        return new OpenApiDocumentProvenance(
            info,
            tags,
            externalDocs,
            servers,
            componentExamples,
            componentRequestBodies,
            vendorExtensions,
            componentParameters,
            componentResponses
        );
    }

    internal static OpenApiOperationProvenance? ReadOperation(IFieldSymbol field)
    {
        var attributes = field.GetAttributes();
        var provenanceAttributes = attributes
            .Where(attribute => Is(attribute, "Rivet.RivetOperationProvenanceAttribute"))
            .ToList();
        if (provenanceAttributes.Count == 0)
        {
            return null;
        }
        if (provenanceAttributes.Count != 1)
        {
            throw new ContractAnalysisException(
                $"Multiple Rivet operation provenance declarations found on '{field.Name}'."
            );
        }

        var args = provenanceAttributes[0].ConstructorArguments;
        var hasServerOverride = RequiredBool(args[5], "operation server override presence");
        var rivetContract = StringValue(args[6]);
        var rivetEndpoint = StringValue(args[7]);
        return new OpenApiOperationProvenance(
            RequiredBool(args[0], "operationId presence"),
            StringValue(args[1]),
            StringArray(args[3], "operation tags"),
            RequiredBool(args[2], "operation deprecation"),
            hasServerOverride
                ? ReadServers(
                    attributes,
                    "Rivet.RivetOperationServerAttribute",
                    "Rivet.RivetOperationServerVariableAttribute"
                )
                : null,
            StringValue(args[4]),
            rivetContract is not null || rivetEndpoint is not null
                ? new OpenApiRivetIdentityProvenance(rivetContract, rivetEndpoint)
                : null,
            StringValue(args[8]),
            attributes
                .Where(attribute =>
                    Is(attribute, "Rivet.RivetOperationParameterComponentAttribute")
                )
                .Select(attribute =>
                {
                    var values = attribute.ConstructorArguments;
                    return (
                        Order: RequiredInt(values[0], "operation parameter component order"),
                        Reference: new OpenApiParameterComponentReference(
                            RequiredString(values[1], "operation parameter component name"),
                            RequiredString(values[2], "operation parameter component location"),
                            RequiredString(values[3], "operation parameter component ID")
                        )
                    );
                })
                .OrderBy(value => value.Order)
                .Select(value => value.Reference)
                .ToList(),
            attributes
                .Where(attribute => Is(attribute, "Rivet.RivetOperationResponseComponentAttribute"))
                .Select(attribute =>
                {
                    var values = attribute.ConstructorArguments;
                    return (
                        Order: RequiredInt(values[0], "operation response component order"),
                        Reference: new OpenApiResponseComponentReference(
                            RequiredString(values[1], "operation response component status"),
                            RequiredString(values[2], "operation response component ID")
                        )
                    );
                })
                .OrderBy(value => value.Order)
                .Select(value => value.Reference)
                .ToList()
        );
    }

    private static IReadOnlyList<T> ReadJsonComponents<T>(
        IReadOnlyList<AttributeData> attributes,
        string attributeName,
        string kind,
        Func<string, string, T> create
    ) =>
        attributes
            .Where(attribute => Is(attribute, attributeName))
            .Select(attribute =>
            {
                var args = attribute.ConstructorArguments;
                var order = RequiredInt(args[0], $"document component {kind} order");
                var name = RequiredString(args[1], $"document component {kind} name");
                var json = RequiredString(args[2], $"document component {kind} JSON");
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("root value is not an object");
                    }
                }
                catch (JsonException exception)
                {
                    throw new ContractAnalysisException(
                        $"Invalid Rivet document component {kind} JSON for '{name}': {exception.Message}"
                    );
                }
                return (Order: order, Component: create(name, json));
            })
            .OrderBy(value => value.Order)
            .Select(value => value.Component)
            .ToList();

    private static TsType ApplySchemaLeafMetadata(TsType type, string? schemaType, string? format)
    {
        var explicitFormat = format == "" ? null : format;
        return type switch
        {
            TsType.Primitive primitive => primitive with
            {
                Name = schemaType ?? primitive.Name,
                Format = format is null ? primitive.Format : explicitFormat,
            },
            TsType.Nullable { Inner: TsType.Primitive primitive } => new TsType.Nullable(
                primitive with
                {
                    Name = schemaType ?? primitive.Name,
                    Format = format is null ? primitive.Format : explicitFormat,
                }
            ),
            _ => type,
        };
    }

    private static IReadOnlyList<OpenApiServerProvenance> ReadServers(
        IReadOnlyList<AttributeData> attributes,
        string serverAttributeName,
        string variableAttributeName
    )
    {
        var variables = attributes
            .Where(attribute => Is(attribute, variableAttributeName))
            .Select(attribute =>
            {
                var args = attribute.ConstructorArguments;
                return (
                    ServerOrder: RequiredInt(args[0], "server order"),
                    VariableOrder: RequiredInt(args[1], "server variable order"),
                    Variable: new OpenApiServerVariableProvenance(
                        RequiredString(args[2], "server variable name"),
                        RequiredString(args[3], "server variable default"),
                        StringArray(args[4], "server variable enum"),
                        StringValue(args[5])
                    )
                );
            })
            .ToList();

        return attributes
            .Where(attribute => Is(attribute, serverAttributeName))
            .Select(attribute =>
            {
                var args = attribute.ConstructorArguments;
                var order = RequiredInt(args[0], "server order");
                return (
                    Order: order,
                    Server: new OpenApiServerProvenance(
                        RequiredString(args[1], "server URL"),
                        StringValue(args[2]),
                        variables
                            .Where(variable => variable.ServerOrder == order)
                            .OrderBy(variable => variable.VariableOrder)
                            .Select(variable => variable.Variable)
                            .ToList()
                    )
                );
            })
            .OrderBy(value => value.Order)
            .Select(value => value.Server)
            .ToList();
    }

    private static bool Is(AttributeData attribute, string metadataName) =>
        attribute.AttributeClass?.ToDisplayString() == metadataName;

    private static string? StringValue(TypedConstant value) => value.Value as string;

    private static string RequiredString(TypedConstant value, string context) =>
        StringValue(value)
        ?? throw new ContractAnalysisException($"Invalid Rivet {context} metadata.");

    private static int RequiredInt(TypedConstant value, string context) =>
        value.Value is int result
            ? result
            : throw new ContractAnalysisException($"Invalid Rivet {context} metadata.");

    private static bool RequiredBool(TypedConstant value, string context) =>
        value.Value is bool result
            ? result
            : throw new ContractAnalysisException($"Invalid Rivet {context} metadata.");

    private static IReadOnlyList<string> StringArray(TypedConstant value, string context)
    {
        if (value.Kind != TypedConstantKind.Array || value.IsNull)
        {
            throw new ContractAnalysisException($"Invalid Rivet {context} metadata.");
        }

        return value.Values.Select(item => RequiredString(item, context)).ToList();
    }
}
