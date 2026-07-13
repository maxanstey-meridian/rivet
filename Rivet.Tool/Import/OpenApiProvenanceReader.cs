using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

internal sealed record ImportedOpenApiProvenance(
    OpenApiDocumentProvenance Document,
    IReadOnlyDictionary<(string Path, string Method), OpenApiOperationProvenance> Operations
);

internal static class OpenApiProvenanceReader
{
    private static readonly HashSet<string> _preservedVendorExtensions =
    [
        "x-ds-api-status",
        "x-ds-examples",
        "x-ds-in-sdk",
        "x-enum-elements",
        "x-is-beta",
        "x-release-status",
        "x-sq-version",
        "x-twilio",
        "x-visibility",
    ];

    private static readonly string[] _methods =
    [
        "get",
        "put",
        "post",
        "delete",
        "options",
        "head",
        "patch",
        "trace",
    ];

    public static ImportedOpenApiProvenance Read(string json, List<string> warnings)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var swagger2 =
            root.TryGetProperty("swagger", out var swagger)
            && swagger.GetString()?.StartsWith("2.", StringComparison.Ordinal) == true;
        var info = ReadInfo(root.GetProperty("info"));
        var tags = root.TryGetProperty("tags", out var tagArray)
            ? tagArray.EnumerateArray().Select(ReadTag).ToList()
            : [];
        var externalDocs = root.TryGetProperty("externalDocs", out var docs)
            ? ReadExternalDocs(docs)
            : null;
        var rootServers = swagger2
            ? ReadSwaggerServers(root, warnings)
            : ReadServersProperty(root) ?? [];
        var componentExamples = ReadComponentExamples(root);
        var componentRequestBodies = ReadComponentRequestBodies(root);
        var componentParameters = ReadComponents(root, "parameters")
            .Select(component => new OpenApiComponentParameterProvenance(
                component.Name,
                component.Json
            ))
            .ToList();
        var componentResponses = ReadComponents(root, "responses")
            .Select(component => new OpenApiComponentResponseProvenance(
                component.Name,
                component.Json
            ))
            .ToList();
        var schemaComponents = ReadComponents(root, "schemas");
        var componentSchemas = schemaComponents
            .Where(component => NeedsSchemaProvenance(component.Value))
            .Select(component => new OpenApiComponentSchemaProvenance(
                component.Name,
                component.Json
            ))
            .ToList();
        var opaqueSchemaPointers = schemaComponents
            .Where(component => NeedsSchemaProvenance(component.Value))
            .Select(component => $"#/components/schemas/{EscapePointerToken(component.Name)}")
            .ToHashSet(StringComparer.Ordinal);
        var vendorExtensions = ReadVendorExtensions(root, swagger2, opaqueSchemaPointers);
        var document = new OpenApiDocumentProvenance(
            info,
            tags,
            externalDocs,
            rootServers,
            componentExamples,
            ComponentRequestBodies: componentRequestBodies,
            VendorExtensions: vendorExtensions,
            ComponentParameters: componentParameters,
            ComponentResponses: componentResponses,
            ComponentSchemas: componentSchemas
        );
        var operations = new Dictionary<(string Path, string Method), OpenApiOperationProvenance>();

        if (!root.TryGetProperty("paths", out var paths))
        {
            return new ImportedOpenApiProvenance(document, operations);
        }

        foreach (var path in paths.EnumerateObject())
        {
            var pathServers = swagger2 ? null : ReadServersProperty(path.Value);
            foreach (var method in _methods)
            {
                if (!path.Value.TryGetProperty(method, out var operation))
                {
                    continue;
                }

                var operationServers = swagger2 ? null : ReadServersProperty(operation);
                var serverOverride = operationServers ?? pathServers;
                var operationIdPresent = operation.TryGetProperty(
                    "operationId",
                    out var operationId
                );
                var operationIdValue = operationIdPresent
                    ? ReadRequiredString(
                        operationId,
                        $"operationId for {method.ToUpperInvariant()} {path.Name}"
                    )
                    : null;
                var operationTags = operation.TryGetProperty("tags", out var operationTagArray)
                    ? operationTagArray
                        .EnumerateArray()
                        .Select(value =>
                            ReadRequiredString(
                                value,
                                $"tag for {method.ToUpperInvariant()} {path.Name}"
                            )
                        )
                        .ToList()
                    : [];
                var deprecated =
                    operation.TryGetProperty("deprecated", out var deprecatedValue)
                    && deprecatedValue.ValueKind == JsonValueKind.True;
                var requestBodyDescription = swagger2
                    ? ReadSwaggerBodyDescription(root, path.Value, operation)
                    : ReadRequestBodyDescription(
                        root,
                        operation,
                        $"{method.ToUpperInvariant()} {path.Name}"
                    );

                operations[(path.Name, method)] = new OpenApiOperationProvenance(
                    operationIdPresent,
                    operationIdValue,
                    operationTags,
                    deprecated,
                    serverOverride,
                    requestBodyDescription,
                    ReadRivetIdentity(operation),
                    ReadRequestBodyComponentId(operation),
                    ReadParameterComponentReferences(root, path.Value, operation),
                    ReadResponseComponentReferences(operation),
                    ReadOperationSchemas(path.Value, operation)
                );
            }
        }

        return new ImportedOpenApiProvenance(document, operations);
    }

    private static IReadOnlyList<(string Name, string Json, JsonElement Value)> ReadComponents(
        JsonElement root,
        string kind
    )
    {
        if (
            !root.TryGetProperty("components", out var components)
            || !components.TryGetProperty(kind, out var values)
        )
        {
            return [];
        }

        return values
            .EnumerateObject()
            .Select(value =>
                (value.Name, Json: JsonSerializer.Serialize(value.Value), Value: value.Value)
            )
            .ToList();
    }

    private static IReadOnlyList<OpenApiComponentRequestBodyProvenance> ReadComponentRequestBodies(
        JsonElement root
    )
    {
        if (
            !root.TryGetProperty("components", out var components)
            || !components.TryGetProperty("requestBodies", out var requestBodies)
        )
        {
            return [];
        }

        return requestBodies
            .EnumerateObject()
            .Select(requestBody => new OpenApiComponentRequestBodyProvenance(
                requestBody.Name,
                requestBody.Value.TryGetProperty("description", out var description)
                    ? description.GetString()
                    : null,
                requestBody.Value.TryGetProperty("required", out var required)
                    && required.ValueKind == JsonValueKind.True,
                requestBody.Value.TryGetProperty("content", out var content)
                    ? content
                        .EnumerateObject()
                        .Select(media => new OpenApiRequestBodyContentProvenance(
                            media.Name,
                            null,
                            null,
                            SchemaJson: media.Value.TryGetProperty("schema", out var schema)
                                ? JsonSerializer.Serialize(schema)
                                : null
                        ))
                        .ToList()
                    : []
            ))
            .ToList();
    }

    private static bool NeedsSchemaProvenance(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (schema.TryGetProperty("$ref", out _))
        {
            return true;
        }
        if (
            schema.TryGetProperty("allOf", out _)
            || schema.TryGetProperty("oneOf", out _)
            || schema.TryGetProperty("anyOf", out _)
            || schema.TryGetProperty("discriminator", out _)
        )
        {
            return true;
        }
        if (
            schema.TryGetProperty("enum", out _)
            && schema.TryGetProperty("type", out var enumType)
            && enumType.ValueKind == JsonValueKind.String
            && enumType.GetString() is "number" or "boolean"
        )
        {
            return true;
        }
        if (!schema.TryGetProperty("type", out var type))
        {
            if (
                schema.TryGetProperty("items", out _)
                || schema.TryGetProperty("nullable", out var nullable)
                    && nullable.ValueKind == JsonValueKind.True
            )
            {
                return true;
            }
        }
        else if (type.ValueKind == JsonValueKind.String && type.GetString() == "array")
        {
            return schema.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Object
                && !items.TryGetProperty("$ref", out _);
        }
        else if (
            type.ValueKind == JsonValueKind.String
            && type.GetString() == "object"
            && schema.TryGetProperty("additionalProperties", out _)
        )
        {
            return true;
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                if (NeedsSchemaProvenance(property.Value))
                {
                    return true;
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (NeedsSchemaProvenance(item))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static OpenApiOperationSchemaProvenance? ReadOperationSchemas(
        JsonElement pathItem,
        JsonElement operation
    )
    {
        var parameters =
            new Dictionary<(string Name, string Location), OpenApiParameterSchemaProvenance>();
        AddParameters(pathItem);
        AddParameters(operation);

        var requests = new List<OpenApiRequestSchemaProvenance>();
        if (
            operation.TryGetProperty("requestBody", out var requestBody)
            && !requestBody.TryGetProperty("$ref", out _)
            && requestBody.TryGetProperty("content", out var requestContent)
        )
        {
            foreach (var media in requestContent.EnumerateObject())
            {
                if (media.Value.TryGetProperty("schema", out var schema))
                {
                    requests.Add(
                        new OpenApiRequestSchemaProvenance(
                            media.Name,
                            JsonSerializer.Serialize(schema)
                        )
                    );
                }
            }
        }

        var responses = new List<OpenApiResponseSchemaProvenance>();
        if (operation.TryGetProperty("responses", out var responseValues))
        {
            foreach (var response in responseValues.EnumerateObject())
            {
                if (
                    response.Value.TryGetProperty("$ref", out _)
                    || !response.Value.TryGetProperty("content", out var responseContent)
                )
                {
                    continue;
                }
                foreach (var media in responseContent.EnumerateObject())
                {
                    if (media.Value.TryGetProperty("schema", out var schema))
                    {
                        responses.Add(
                            new OpenApiResponseSchemaProvenance(
                                response.Name,
                                media.Name,
                                JsonSerializer.Serialize(schema)
                            )
                        );
                    }
                }
            }
        }

        return parameters.Count == 0 && requests.Count == 0 && responses.Count == 0
            ? null
            : new OpenApiOperationSchemaProvenance(parameters.Values.ToList(), requests, responses);

        void AddParameters(JsonElement owner)
        {
            if (!owner.TryGetProperty("parameters", out var sourceParameters))
            {
                return;
            }
            foreach (var parameter in sourceParameters.EnumerateArray())
            {
                if (
                    parameter.TryGetProperty("$ref", out _)
                    || !parameter.TryGetProperty("name", out var name)
                    || !parameter.TryGetProperty("in", out var location)
                    || !parameter.TryGetProperty("schema", out var schema)
                )
                {
                    continue;
                }
                var key = (name.GetString() ?? "", location.GetString() ?? "");
                parameters[key] = new OpenApiParameterSchemaProvenance(
                    key.Item1,
                    key.Item2,
                    JsonSerializer.Serialize(schema)
                );
            }
        }
    }

    private static IReadOnlyList<OpenApiParameterComponentReference> ReadParameterComponentReferences(
        JsonElement root,
        JsonElement pathItem,
        JsonElement operation
    )
    {
        var merged =
            new Dictionary<(string Name, string Location), OpenApiParameterComponentReference>();
        Add(pathItem);
        Add(operation);
        return merged.Values.ToList();

        void Add(JsonElement owner)
        {
            if (!owner.TryGetProperty("parameters", out var parameters))
            {
                return;
            }
            foreach (var parameter in parameters.EnumerateArray())
            {
                var hasComponentReference = TryReadComponentReference(
                    parameter,
                    "parameters",
                    out var componentId
                );
                if (hasComponentReference)
                {
                    if (
                        !TryResolveCanonicalParameterDefinition(
                            root,
                            componentId!,
                            out var definition
                        ) || !TryReadParameterIdentity(definition, out var name, out var location)
                    )
                    {
                        continue;
                    }

                    merged[(name, location)] = new OpenApiParameterComponentReference(
                        name,
                        location,
                        componentId!
                    );
                }
                else if (TryReadParameterIdentity(parameter, out var name, out var location))
                {
                    merged.Remove((name, location));
                }
            }
        }
    }

    private static bool TryResolveCanonicalParameterDefinition(
        JsonElement root,
        string componentId,
        out JsonElement definition
    )
    {
        definition = default;
        if (
            !root.TryGetProperty("components", out var components)
            || !components.TryGetProperty("parameters", out var parameters)
        )
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentId = componentId;
        while (visited.Add(currentId) && parameters.TryGetProperty(currentId, out var current))
        {
            if (!TryReadComponentReference(current, "parameters", out var targetId))
            {
                definition = current;
                return true;
            }
            currentId = targetId!;
        }
        return false;
    }

    private static bool TryReadParameterIdentity(
        JsonElement parameter,
        out string name,
        out string location
    )
    {
        name = "";
        location = "";
        if (
            parameter.ValueKind != JsonValueKind.Object
            || !parameter.TryGetProperty("name", out var nameValue)
            || nameValue.ValueKind != JsonValueKind.String
            || !parameter.TryGetProperty("in", out var locationValue)
            || locationValue.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        name = nameValue.GetString()!;
        location = locationValue.GetString()!;
        return true;
    }

    private static IReadOnlyList<OpenApiResponseComponentReference> ReadResponseComponentReferences(
        JsonElement operation
    )
    {
        if (!operation.TryGetProperty("responses", out var responses))
        {
            return [];
        }
        return responses
            .EnumerateObject()
            .Where(response => TryReadComponentReference(response.Value, "responses", out _))
            .Select(response =>
            {
                TryReadComponentReference(response.Value, "responses", out var componentId);
                return new OpenApiResponseComponentReference(response.Name, componentId!);
            })
            .ToList();
    }

    private static bool TryReadComponentReference(
        JsonElement value,
        string kind,
        out string? componentId
    )
    {
        componentId = null;
        if (
            !value.TryGetProperty("$ref", out var referenceValue)
            || referenceValue.ValueKind != JsonValueKind.String
            || referenceValue.GetString() is not { } reference
            || !reference.StartsWith($"#/components/{kind}/", StringComparison.Ordinal)
        )
        {
            return false;
        }
        componentId = Uri.UnescapeDataString(reference[$"#/components/{kind}/".Length..])
            .Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
        return true;
    }

    private static IReadOnlyList<OpenApiVendorExtensionProvenance> ReadVendorExtensions(
        JsonElement root,
        bool swagger2,
        IReadOnlySet<string> opaqueSchemaPointers
    )
    {
        var result = new List<OpenApiVendorExtensionProvenance>();
        ReadVendorExtensions(
            root,
            "#",
            swagger2,
            opaqueSchemaPointers,
            exampleObject: false,
            result
        );
        return result;
    }

    private static void ReadVendorExtensions(
        JsonElement value,
        string pointer,
        bool swagger2,
        IReadOnlySet<string> opaqueSchemaPointers,
        bool exampleObject,
        List<OpenApiVendorExtensionProvenance> result
    )
    {
        if (IsOpaqueProvenanceRoot(pointer, opaqueSchemaPointers))
        {
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ReadVendorExtensions(
                    item,
                    $"{pointer}/{index++}",
                    swagger2,
                    opaqueSchemaPointers,
                    exampleObject: false,
                    result
                );
            }
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (_preservedVendorExtensions.Contains(property.Name))
            {
                result.Add(
                    new OpenApiVendorExtensionProvenance(
                        NormalizeOwnerPointer(pointer, swagger2),
                        property.Name,
                        property.Value.GetRawText()
                    )
                );
            }
            else if (
                !swagger2
                && property.Name == "examples"
                && property.Value.ValueKind == JsonValueKind.Object
            )
            {
                foreach (var example in property.Value.EnumerateObject())
                {
                    ReadVendorExtensions(
                        example.Value,
                        $"{pointer}/examples/{EscapePointerToken(example.Name)}",
                        swagger2,
                        opaqueSchemaPointers,
                        exampleObject: true,
                        result
                    );
                }
            }
            else if (
                !(exampleObject && property.Name == "value") && !IsOpaqueOpenApiValue(property.Name)
            )
            {
                ReadVendorExtensions(
                    property.Value,
                    $"{pointer}/{EscapePointerToken(property.Name)}",
                    swagger2,
                    opaqueSchemaPointers,
                    exampleObject: false,
                    result
                );
            }
        }
    }

    private static bool IsOpaqueOpenApiValue(string name) =>
        name is "const" or "default" or "enum" or "example" or "examples"
        || name.StartsWith("x-", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpaqueProvenanceRoot(
        string pointer,
        IReadOnlySet<string> opaqueSchemaPointers
    )
    {
        if (opaqueSchemaPointers.Contains(pointer))
        {
            return true;
        }
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        var tokens = pointer[2..].Split('/');
        if (tokens.Length == 3 && tokens[0] == "components")
        {
            return tokens[1] is "parameters" or "responses";
        }
        if (
            tokens.Length == 6
            && tokens[0] == "components"
            && tokens[1] == "requestBodies"
            && tokens[3] == "content"
            && tokens[5] == "schema"
        )
        {
            return true;
        }
        if (tokens.Length < 5 || tokens[0] != "paths")
        {
            return false;
        }

        if (tokens[2] == "parameters")
        {
            return tokens.Length == 5 && tokens[4] == "schema";
        }
        if (!_methods.Contains(tokens[2], StringComparer.Ordinal))
        {
            return false;
        }

        return tokens.Length == 6 && tokens[3] == "parameters" && tokens[5] == "schema"
            || tokens.Length == 7
                && tokens[3] == "requestBody"
                && tokens[4] == "content"
                && tokens[6] == "schema"
            || tokens.Length == 8
                && tokens[3] == "responses"
                && tokens[5] == "content"
                && tokens[7] == "schema";
    }

    private static string NormalizeOwnerPointer(string pointer, bool swagger2)
    {
        if (!swagger2)
        {
            return pointer;
        }

        return pointer switch
        {
            "#/definitions" => "#/components/schemas",
            "#/securityDefinitions" => "#/components/securitySchemes",
            _ when pointer.StartsWith("#/definitions/", StringComparison.Ordinal) =>
                "#/components/schemas/" + pointer["#/definitions/".Length..],
            _ when pointer.StartsWith("#/securityDefinitions/", StringComparison.Ordinal) =>
                "#/components/securitySchemes/" + pointer["#/securityDefinitions/".Length..],
            _ => pointer,
        };
    }

    private static string EscapePointerToken(string value) =>
        value
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static IReadOnlyList<OpenApiComponentExampleProvenance> ReadComponentExamples(
        JsonElement root
    )
    {
        if (
            !root.TryGetProperty("components", out var components)
            || !components.TryGetProperty("examples", out var examples)
        )
        {
            return [];
        }

        var result = new List<OpenApiComponentExampleProvenance>();
        foreach (var example in examples.EnumerateObject())
        {
            var hasValue = example.Value.TryGetProperty("value", out var value);
            var externalValue = ReadOptionalString(example.Value, "externalValue");
            if (hasValue == (externalValue is not null))
            {
                throw new InvalidOperationException(
                    $"Component example '{example.Name}' requires exactly one of value or externalValue."
                );
            }

            result.Add(
                new OpenApiComponentExampleProvenance(
                    example.Name,
                    ReadOptionalString(example.Value, "summary"),
                    ReadOptionalString(example.Value, "description"),
                    hasValue ? value.GetRawText() : null,
                    externalValue
                )
            );
        }

        return result;
    }

    private static OpenApiInfoProvenance ReadInfo(JsonElement info)
    {
        var contact = info.TryGetProperty("contact", out var contactValue)
            ? new OpenApiContactProvenance(
                ReadOptionalString(contactValue, "name"),
                ReadOptionalString(contactValue, "url"),
                ReadOptionalString(contactValue, "email")
            )
            : null;
        var license = info.TryGetProperty("license", out var licenseValue)
            ? new OpenApiLicenseProvenance(
                ReadRequiredPropertyString(licenseValue, "name", "info.license"),
                ReadOptionalString(licenseValue, "url"),
                ReadOptionalString(licenseValue, "identifier")
            )
            : null;

        return new OpenApiInfoProvenance(
            ReadRequiredPropertyString(info, "title", "info"),
            ReadRequiredPropertyString(info, "version", "info"),
            ReadOptionalString(info, "description"),
            ReadOptionalString(info, "termsOfService"),
            contact,
            license
        );
    }

    private static OpenApiRivetIdentityProvenance? ReadRivetIdentity(JsonElement operation)
    {
        var contract = ReadOptionalString(operation, "x-rivet-contract");
        var endpoint = ReadOptionalString(operation, "x-rivet-endpoint");
        return contract is null && endpoint is null
            ? null
            : new OpenApiRivetIdentityProvenance(contract, endpoint);
    }

    private static string? ReadRequestBodyComponentId(JsonElement operation)
    {
        if (
            !operation.TryGetProperty("requestBody", out var requestBody)
            || !requestBody.TryGetProperty("$ref", out var referenceValue)
            || referenceValue.ValueKind != JsonValueKind.String
            || referenceValue.GetString() is not { } reference
            || !reference.StartsWith("#/components/requestBodies/", StringComparison.Ordinal)
        )
        {
            return null;
        }

        return Uri.UnescapeDataString(reference["#/components/requestBodies/".Length..])
            .Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
    }

    private static OpenApiTagProvenance ReadTag(JsonElement tag)
    {
        return new OpenApiTagProvenance(
            ReadRequiredPropertyString(tag, "name", "tag"),
            ReadOptionalString(tag, "description"),
            tag.TryGetProperty("externalDocs", out var docs) ? ReadExternalDocs(docs) : null
        );
    }

    private static OpenApiExternalDocsProvenance ReadExternalDocs(JsonElement docs)
    {
        return new OpenApiExternalDocsProvenance(
            ReadRequiredPropertyString(docs, "url", "externalDocs"),
            ReadOptionalString(docs, "description")
        );
    }

    private static IReadOnlyList<OpenApiServerProvenance>? ReadServersProperty(JsonElement owner)
    {
        if (!owner.TryGetProperty("servers", out var servers))
        {
            return null;
        }

        return servers.EnumerateArray().Select(ReadServer).ToList();
    }

    private static OpenApiServerProvenance ReadServer(JsonElement server)
    {
        var variables = new List<OpenApiServerVariableProvenance>();
        if (server.TryGetProperty("variables", out var variableMap))
        {
            foreach (var variable in variableMap.EnumerateObject())
            {
                var allowedValues = variable.Value.TryGetProperty("enum", out var values)
                    ? values
                        .EnumerateArray()
                        .Select(value =>
                            ReadRequiredString(value, $"server variable '{variable.Name}' enum")
                        )
                        .ToList()
                    : [];
                variables.Add(
                    new OpenApiServerVariableProvenance(
                        variable.Name,
                        ReadRequiredPropertyString(
                            variable.Value,
                            "default",
                            $"server variable '{variable.Name}'"
                        ),
                        allowedValues,
                        ReadOptionalString(variable.Value, "description")
                    )
                );
            }
        }

        return new OpenApiServerProvenance(
            ReadRequiredPropertyString(server, "url", "server"),
            ReadOptionalString(server, "description"),
            variables
        );
    }

    private static IReadOnlyList<OpenApiServerProvenance> ReadSwaggerServers(
        JsonElement root,
        List<string> warnings
    )
    {
        var host = ReadOptionalString(root, "host");
        var basePath = ReadOptionalString(root, "basePath");
        var schemes = root.TryGetProperty("schemes", out var schemeArray)
            ? schemeArray
                .EnumerateArray()
                .Select(value => ReadRequiredString(value, "Swagger schemes"))
                .ToList()
            : [];

        if (host is not null && schemes.Count > 0)
        {
            return schemes
                .Select(scheme => new OpenApiServerProvenance(
                    $"{scheme}://{host}{basePath ?? ""}",
                    null,
                    []
                ))
                .ToList();
        }

        if (host is not null || schemes.Count > 0)
        {
            warnings.Add(
                "RIV3019: Swagger host/schemes cannot be projected without both an explicit host and at least one explicit scheme; no server was invented."
            );
            return [];
        }

        return basePath is not null ? [new OpenApiServerProvenance(basePath, null, [])] : [];
    }

    private static string? ReadSwaggerBodyDescription(
        JsonElement root,
        JsonElement pathItem,
        JsonElement operation
    )
    {
        return TryFindSwaggerBodyDescription(root, operation, out var operationDescription)
                ? operationDescription
            : TryFindSwaggerBodyDescription(root, pathItem, out var pathDescription)
                ? pathDescription
            : null;
    }

    private static string? ReadRequestBodyDescription(
        JsonElement root,
        JsonElement operation,
        string context
    )
    {
        if (!operation.TryGetProperty("requestBody", out var requestBody))
        {
            return null;
        }

        return ReadRequestBodyDescription(
            root,
            requestBody,
            context,
            new HashSet<string>(StringComparer.Ordinal)
        );
    }

    private static string? ReadRequestBodyDescription(
        JsonElement root,
        JsonElement requestBody,
        string context,
        HashSet<string> references
    )
    {
        if (requestBody.TryGetProperty("description", out var description))
        {
            return ReadRequiredString(description, $"request body description for {context}");
        }
        if (!requestBody.TryGetProperty("$ref", out var referenceValue))
        {
            return null;
        }

        var reference = ReadRequiredString(referenceValue, $"request body reference for {context}");
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }
        if (!references.Add(reference))
        {
            throw new InvalidOperationException(
                $"Cyclic request body reference '{reference}' for {context}."
            );
        }

        if (!TryResolveLocalReference(root, reference, out var target))
        {
            return null;
        }
        var result = ReadRequestBodyDescription(root, target, context, references);
        references.Remove(reference);
        return result;
    }

    private static JsonElement ResolveLocalReference(JsonElement root, string reference)
    {
        if (TryResolveLocalReference(root, reference, out var target))
        {
            return target;
        }

        throw new InvalidOperationException(
            $"Local request body reference '{reference}' targets a missing value."
        );
    }

    private static bool TryResolveLocalReference(
        JsonElement root,
        string reference,
        out JsonElement target
    )
    {
        var current = root;
        foreach (var encodedSegment in reference[2..].Split('/'))
        {
            var segment = Uri.UnescapeDataString(encodedSegment)
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (!current.TryGetProperty(segment, out current))
            {
                target = default;
                return false;
            }
        }
        target = current;
        return true;
    }

    private static bool TryFindSwaggerBodyDescription(
        JsonElement root,
        JsonElement owner,
        out string? description
    )
    {
        description = null;
        if (!owner.TryGetProperty("parameters", out var parameters))
        {
            return false;
        }

        foreach (var parameter in parameters.EnumerateArray())
        {
            var resolved = parameter.TryGetProperty("$ref", out var referenceValue)
                ? ResolveLocalReference(
                    root,
                    ReadRequiredString(referenceValue, "Swagger body parameter reference")
                )
                : parameter;
            if (ReadOptionalString(resolved, "in") != "body")
            {
                continue;
            }

            description = ReadOptionalString(resolved, "description");
            return true;
        }

        return false;
    }

    private static string ReadRequiredPropertyString(
        JsonElement owner,
        string property,
        string context
    )
    {
        if (!owner.TryGetProperty(property, out var value))
        {
            throw new InvalidOperationException($"Missing required {context}.{property} value.");
        }

        return ReadRequiredString(value, $"{context}.{property}");
    }

    private static string ReadRequiredString(JsonElement value, string context)
    {
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidOperationException($"{context} must be a string.");
    }

    private static string? ReadOptionalString(JsonElement owner, string property)
    {
        return owner.TryGetProperty(property, out var value)
            ? ReadRequiredString(value, property)
            : null;
    }
}
