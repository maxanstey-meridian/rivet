using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

/// <summary>
/// Entry point for importing an OpenAPI 3.1 JSON spec into C# contract + DTO source files.
/// </summary>
public static class OpenApiImporter
{
    private const string ComponentExamplesPrefix = "#/components/examples/";
    private const string ComponentHeadersPrefix = "#/components/headers/";
    private const string ComponentRequestBodiesPrefix = "#/components/requestBodies/";
    private const string ComponentResponsesPrefix = "#/components/responses/";
    private const string ImportedParameterReferenceExtension =
        "x-rivet-imported-parameter-reference";
    private const string ImportedRequestBodyReferenceExtension =
        "x-rivet-imported-request-body-reference";

    private static readonly string[] _operationNames =
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

    public static ImportResult Import(string json, ImportOptions options)
    {
        var warnings = new List<string>();

        // I1: cyclic component-alias chains ("A": {$ref: B}, "B": {$ref: A}) overflow the
        // stack inside the OpenApi library's reference proxies on ANY member access, so
        // they must be broken at the raw-JSON level before parsing.
        json = BreakAliasCycles(json, warnings);
        json = NormalizeMappedVendorExtensions(json);
        json = NormalizeReservedContentTypeHeaders(json);
        var provenance = OpenApiProvenanceReader.Read(json, warnings);
        json = NormalizeLocalPathReferences(json);
        json = NormalizeSchemaReferenceMetadataSiblings(json);
        var swaggerSchemaLessResponses = ReadSwaggerSchemaLessResponses(json);
        json = OpenApiJsonNodeSerializer.EscapeLiteralSentinels(json);

        var readResult = OpenApiDocument.Parse(json, "json");
        var doc =
            readResult.Document
            ?? throw new InvalidOperationException("Failed to parse OpenAPI document.");
        RemoveConvertedSwaggerProducesContent(doc, swaggerSchemaLessResponses);
        var files = new List<GeneratedFile>();
        var mapper = new SchemaMapper(warnings);

        // Parse schemas
        var schemas = doc.Components?.Schemas;

        var schemaResult = schemas is { Count: > 0 }
            ? mapper.MapSchemas(schemas, ReadPreservedSchemaReferences(provenance.Document))
            : new SchemaMapResult([], [], [], []);

        if (schemaResult.ScalarSchemas.Count > 0)
        {
            files.Add(
                new GeneratedFile(
                    "RivetScalarSchemas.cs",
                    CSharpWriter.WriteScalarSchemas(schemaResult.ScalarSchemas)
                )
            );
        }

        var securityMetadata = ReadSecurityMetadata(json);
        if (securityMetadata.Schemes.Count > 0 || securityMetadata.GlobalRequirements is not null)
        {
            files.Add(
                new GeneratedFile(
                    "RivetSecurity.cs",
                    CSharpWriter.WriteSecurityMetadata(securityMetadata)
                )
            );
        }

        var globalSecurityScheme = options.SecurityScheme;

        // Parse paths → contracts
        var contracts = doc.Paths is { Count: > 0 }
            ? ContractBuilder.BuildContracts(
                doc.Paths,
                mapper,
                globalSecurityScheme,
                warnings,
                doc.Components?.Examples,
                provenance.Operations
            )
            : [];

        // Operations own the generated runtime type names. Materialize reusable
        // request-body provenance afterwards so used components reuse those names,
        // while genuinely unused components still receive stable synthetic types.
        var componentRequestBodies = ContractBuilder.BuildRequestBodyComponents(
            doc.Components?.RequestBodies,
            mapper,
            warnings,
            doc.Components?.Examples
        );
        var documentProvenance = provenance.Document with
        {
            ComponentRequestBodies = MergeRequestBodySchemaProvenance(
                componentRequestBodies,
                provenance.Document.ComponentRequestBodies ?? []
            ),
        };
        // Emit type files (records → Types/, enums → Types/, brands → Domain/)
        var ns = options.Namespace;

        foreach (var record in schemaResult.Records)
        {
            // P2 wave 5: contract building may have augmented a component record with
            // [RivetHeader] properties (header-aware input reuse) — write the replacement.
            var effective = mapper.GetComponentRecordOverride(record.Name) ?? record;
            var content = CSharpWriter.WriteRecord(effective, ns);
            files.Add(new GeneratedFile($"Types/{effective.Name}.cs", content));
        }

        // Emit synthetic records from inline objects
        foreach (var record in mapper.ExtraRecords)
        {
            var content = CSharpWriter.WriteRecord(record, ns);
            files.Add(new GeneratedFile($"Types/{record.Name}.cs", content));
        }

        foreach (var enumDef in schemaResult.Enums)
        {
            var content = CSharpWriter.WriteEnum(enumDef, ns);
            files.Add(new GeneratedFile($"Types/{enumDef.Name}.cs", content));
        }

        // Emit synthetic enums from inline enum properties
        foreach (var enumDef in mapper.ExtraEnums)
        {
            var content = CSharpWriter.WriteEnum(enumDef, ns);
            files.Add(new GeneratedFile($"Types/{enumDef.Name}.cs", content));
        }

        foreach (var brand in schemaResult.Brands)
        {
            var content = CSharpWriter.WriteBrand(brand, ns);
            files.Add(new GeneratedFile($"Domain/{brand.Name}.cs", content));
        }

        // Emit contract files
        foreach (var contract in contracts)
        {
            var content = CSharpWriter.WriteContract(contract, ns);
            files.Add(
                new GeneratedFile(
                    $"Contracts/{contract.ModuleName}/{contract.ClassName}.cs",
                    content
                )
            );
        }

        if (HasRawSchemaProvenance(documentProvenance, contracts))
        {
            documentProvenance = documentProvenance with
            {
                ImportedSourceFiles = files
                    .Select(file => new OpenApiImportedSourceFileProvenance(
                        file.FileName.Replace('\\', '/'),
                        ImportedSourceFingerprint.Compute(file.Content)
                    ))
                    .OrderBy(file => file.Path, StringComparer.Ordinal)
                    .ToList(),
            };
        }
        files.Add(
            new GeneratedFile(
                "RivetDocument.cs",
                CSharpWriter.WriteDocumentProvenance(documentProvenance, options.Namespace)
            )
        );

        return new ImportResult(files, warnings);
    }

    private static bool HasRawSchemaProvenance(
        OpenApiDocumentProvenance document,
        IReadOnlyList<GeneratedContract> contracts
    ) =>
        document.ComponentSchemas is { Count: > 0 }
        || document.ComponentParameters is { Count: > 0 }
        || document.ComponentResponses is { Count: > 0 }
        || document.ComponentRequestBodies?.Any(requestBody =>
            requestBody.Contents.Any(content => content.SchemaJson is not null)
        ) == true
        || contracts.Any(contract =>
            contract.Fields.Any(field => field.Provenance?.Schemas is not null)
        );

    private static IReadOnlyList<OpenApiComponentRequestBodyProvenance> MergeRequestBodySchemaProvenance(
        IReadOnlyList<OpenApiComponentRequestBodyProvenance> mapped,
        IReadOnlyList<OpenApiComponentRequestBodyProvenance> raw
    ) =>
        mapped
            .Select(requestBody =>
            {
                var rawRequestBody = raw.FirstOrDefault(candidate =>
                    candidate.Name == requestBody.Name
                );
                return requestBody with
                {
                    Contents = requestBody
                        .Contents.Select(content =>
                        {
                            var source = rawRequestBody?.Contents.FirstOrDefault(candidate =>
                                candidate.MediaType == content.MediaType
                            );
                            return content with { SchemaJson = source?.SchemaJson };
                        })
                        .ToList(),
                };
            })
            .ToList();

    private static IReadOnlySet<string> ReadPreservedSchemaReferences(
        OpenApiDocumentProvenance provenance
    )
    {
        const string prefix = "#/components/schemas/";
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var json in (provenance.ComponentParameters ?? [])
                .Select(component => component.Json)
                .Concat((provenance.ComponentResponses ?? []).Select(component => component.Json))
        )
        {
            using var document = JsonDocument.Parse(json);
            Collect(document.RootElement);
        }
        return result;

        void Collect(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (
                        property.NameEquals("$ref")
                        && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() is { } reference
                        && reference.StartsWith(prefix, StringComparison.Ordinal)
                    )
                    {
                        var token = reference[prefix.Length..];
                        if (!token.Contains('/', StringComparison.Ordinal))
                        {
                            result.Add(
                                Uri.UnescapeDataString(token)
                                    .Replace("~1", "/", StringComparison.Ordinal)
                                    .Replace("~0", "~", StringComparison.Ordinal)
                            );
                        }
                    }
                    else
                    {
                        Collect(property.Value);
                    }
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    Collect(item);
                }
            }
        }
    }

    private static string NormalizeSchemaReferenceMetadataSiblings(string json)
    {
        var root = JsonNode.Parse(json)!;
        NormalizeSchemaReferenceMetadataSiblings(root);
        return root.ToJsonString();
    }

    private static string NormalizeMappedVendorExtensions(string json)
    {
        var root = JsonNode.Parse(json)!;
        var swagger2 = root["swagger"] is not null;
        return NormalizeMappedVendorExtensions(root, swagger2, exampleObject: false)
            ? root.ToJsonString()
            : json;
    }

    private static bool NormalizeMappedVendorExtensions(
        JsonNode node,
        bool swagger2,
        bool exampleObject
    )
    {
        if (node is JsonArray array)
        {
            var changed = false;
            foreach (var child in array)
            {
                if (child is not null)
                {
                    changed |= NormalizeMappedVendorExtensions(
                        child,
                        swagger2,
                        exampleObject: false
                    );
                }
            }
            return changed;
        }

        if (node is not JsonObject obj)
        {
            return false;
        }

        var result = false;
        if (
            obj["deprecated"] is null
            && obj["x-is-deprecated"]?.GetValueKind() is JsonValueKind.True
        )
        {
            obj["deprecated"] = true;
            result = true;
        }
        if (obj["readOnly"] is null && obj["x-read-only"]?.GetValueKind() is JsonValueKind.True)
        {
            obj["readOnly"] = true;
            result = true;
        }
        if (obj["description"] is null)
        {
            foreach (
                var extensionName in new[] { "x-Description", "x-desc", "x-public-description" }
            )
            {
                if (obj[extensionName] is JsonValue extension)
                {
                    obj["description"] = extension.GetValue<string>();
                    result = true;
                    break;
                }
            }
        }

        foreach (var child in obj.ToList())
        {
            if (!swagger2 && child.Key == "examples" && child.Value is JsonObject examples)
            {
                foreach (var example in examples.Select(entry => entry.Value))
                {
                    if (example is not null)
                    {
                        result |= NormalizeMappedVendorExtensions(
                            example,
                            swagger2,
                            exampleObject: true
                        );
                    }
                }
            }
            else if (
                child.Value is not null
                && !(exampleObject && child.Key == "value")
                && !IsOpaqueOpenApiValue(child.Key)
            )
            {
                result |= NormalizeMappedVendorExtensions(
                    child.Value,
                    swagger2,
                    exampleObject: false
                );
            }
        }
        return result;
    }

    private static bool IsOpaqueOpenApiValue(string name) =>
        name is "const" or "default" or "enum" or "example" or "examples"
        || name.StartsWith("x-", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeReservedContentTypeHeaders(string json)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (parsed is not JsonObject root || root["paths"] is not JsonObject paths)
        {
            return json;
        }

        var changed = false;
        foreach (var (_, pathNode) in paths)
        {
            if (pathNode is not JsonObject pathItem)
            {
                continue;
            }
            foreach (var method in _operationNames)
            {
                if (pathItem[method] is not JsonObject operation)
                {
                    continue;
                }
                var declaredMediaTypes = ReadReservedContentTypes(
                    root,
                    pathItem["parameters"] as JsonArray,
                    operation["parameters"] as JsonArray
                );
                if (
                    declaredMediaTypes.Count == 0
                    || operation["requestBody"] is not JsonObject requestBody
                    || GetLocalReference(requestBody) is not null
                    || requestBody["content"] is not JsonObject content
                )
                {
                    continue;
                }
                var existing = content
                    .Select(entry => entry.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (declaredMediaTypes.All(existing.Contains))
                {
                    continue;
                }
                if (declaredMediaTypes.Count != 1 || content.Count != 1)
                {
                    continue;
                }

                var media = content.Single().Value?.DeepClone();
                content.Clear();
                foreach (var mediaType in declaredMediaTypes)
                {
                    content[mediaType] = media?.DeepClone();
                }
                changed = true;
            }
        }

        return changed ? root.ToJsonString() : json;
    }

    private static IReadOnlyList<string> ReadReservedContentTypes(
        JsonObject root,
        JsonArray? pathParameters,
        JsonArray? operationParameters
    )
    {
        JsonObject? contentTypeParameter = null;
        var contentTypeParameterIsReference = false;
        foreach (var rawParameter in (pathParameters ?? []).Concat(operationParameters ?? []))
        {
            if (rawParameter is not JsonObject parameter)
            {
                continue;
            }
            if (GetLocalReference(parameter) is not null)
            {
                var referenced = ResolveReferenceObject(parameter, root, "parameter");
                if (
                    referenced?["in"]?.GetValue<string>() == "header"
                    && referenced["name"]?.GetValue<string>() is { } referencedName
                    && referencedName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                )
                {
                    contentTypeParameter = null;
                    contentTypeParameterIsReference = true;
                }
                continue;
            }
            var resolved = parameter;
            if (
                resolved?["in"]?.GetValue<string>() == "header"
                && resolved["name"]?.GetValue<string>() is { } name
                && name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            )
            {
                contentTypeParameter = resolved;
                contentTypeParameterIsReference = false;
            }
        }
        if (
            contentTypeParameterIsReference
            || contentTypeParameter?["schema"] is not JsonObject schema
        )
        {
            return [];
        }
        if (
            schema["const"] is JsonValue constValue
            && constValue.TryGetValue<string>(out var value)
        )
        {
            return [value];
        }
        if (schema["enum"] is not JsonArray values)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in values)
        {
            if (
                item is not JsonValue itemValue
                || !itemValue.TryGetValue<string>(out var mediaType)
            )
            {
                return [];
            }
            if (!result.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(mediaType);
            }
        }
        return result;
    }

    private static void NormalizeSchemaReferenceMetadataSiblings(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    NormalizeSchemaReferenceMetadataSiblings(child);
                }
            }
            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var child in obj.ToList())
        {
            if (child.Value is not null)
            {
                NormalizeSchemaReferenceMetadataSiblings(child.Value);
            }
        }

        if (
            obj["$ref"] is not JsonValue reference
            || obj.ContainsKey("allOf")
            || !_schemaReferenceMetadataKeywords.Any(obj.ContainsKey)
        )
        {
            return;
        }

        var refValue = reference.GetValue<string>();
        obj.Remove("$ref");
        obj["allOf"] = new JsonArray(new JsonObject { ["$ref"] = refValue });
    }

    private static readonly string[] _schemaReferenceMetadataKeywords =
    [
        "title",
        "description",
        "default",
        "example",
        "examples",
        "deprecated",
        "readOnly",
        "writeOnly",
        "minLength",
        "maxLength",
        "pattern",
        "minimum",
        "maximum",
        "exclusiveMinimum",
        "exclusiveMaximum",
        "multipleOf",
        "minItems",
        "maxItems",
        "uniqueItems",
        "xml",
    ];

    private static ContractSecurityMetadata ReadSecurityMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var schemes = new Dictionary<string, SecuritySchemeDefinition>(StringComparer.Ordinal);
        JsonElement securitySchemes = default;
        var hasSecuritySchemes =
            root.TryGetProperty("components", out var components)
            && components.TryGetProperty("securitySchemes", out securitySchemes);
        if (!hasSecuritySchemes)
        {
            hasSecuritySchemes = root.TryGetProperty("securityDefinitions", out securitySchemes);
        }

        if (hasSecuritySchemes)
        {
            foreach (var scheme in securitySchemes.EnumerateObject())
            {
                schemes[scheme.Name] = ReadSecurityScheme(scheme.Name, scheme.Value);
            }
        }

        var globalRequirements = root.TryGetProperty("security", out var security)
            ? ReadSecurityRequirements(security)
            : null;
        return new ContractSecurityMetadata(schemes, globalRequirements);
    }

    private static SecuritySchemeDefinition ReadSecurityScheme(string name, JsonElement scheme)
    {
        var type = RequiredString(scheme, "type", $"security scheme '{name}'");
        var description = OptionalString(scheme, "description");
        return type switch
        {
            "apiKey" => new ApiKeySecurityScheme(
                RequiredString(scheme, "name", $"apiKey security scheme '{name}'"),
                ParseApiKeyLocation(
                    RequiredString(scheme, "in", $"apiKey security scheme '{name}'"),
                    name
                ),
                description
            ),
            "http" => new HttpSecurityScheme(
                RequiredString(scheme, "scheme", $"HTTP security scheme '{name}'"),
                OptionalString(scheme, "bearerFormat"),
                description
            ),
            "oauth2" => new OAuth2SecurityScheme(ReadOAuthFlows(name, scheme), description),
            "openIdConnect" => new OpenIdConnectSecurityScheme(
                RequiredString(
                    scheme,
                    "openIdConnectUrl",
                    $"OpenID Connect security scheme '{name}'"
                ),
                description
            ),
            "mutualTLS" => new MutualTlsSecurityScheme(description),
            _ => throw new InvalidOperationException(
                $"Security scheme '{name}' has unsupported type '{type}'."
            ),
        };
    }

    private static IReadOnlyList<OAuth2Flow> ReadOAuthFlows(string name, JsonElement scheme)
    {
        if (scheme.TryGetProperty("flows", out var flows))
        {
            return flows
                .EnumerateObject()
                .Select(flow => ReadOAuthFlow(ParseOAuthFlowType(flow.Name, name), flow.Value))
                .ToList();
        }

        // Swagger 2 carries one OAuth flow directly on the security definition.
        var swaggerFlow = RequiredString(scheme, "flow", $"OAuth2 security scheme '{name}'");
        return [ReadOAuthFlow(ParseOAuthFlowType(swaggerFlow, name), scheme)];
    }

    private static OAuth2Flow ReadOAuthFlow(OAuth2FlowType flowType, JsonElement flow)
    {
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (flow.TryGetProperty("scopes", out var sourceScopes))
        {
            foreach (var scope in sourceScopes.EnumerateObject())
            {
                scopes.Add(scope.Name, scope.Value.GetString() ?? "");
            }
        }

        return new OAuth2Flow(
            flowType,
            OptionalString(flow, "authorizationUrl"),
            OptionalString(flow, "tokenUrl"),
            OptionalString(flow, "refreshUrl"),
            scopes
        );
    }

    private static SecurityRequirements ReadSecurityRequirements(JsonElement requirements) =>
        new(
            requirements
                .EnumerateArray()
                .Select(requirement => new SecurityRequirement(
                    requirement
                        .EnumerateObject()
                        .Select(scheme => new SecurityRequirementScheme(
                            scheme.Name,
                            scheme
                                .Value.EnumerateArray()
                                .Select(scope => scope.GetString() ?? "")
                                .ToList()
                        ))
                        .ToList()
                ))
                .ToList()
        );

    private static string RequiredString(JsonElement owner, string property, string context) =>
        OptionalString(owner, property)
        ?? throw new InvalidOperationException($"{context} is missing required '{property}'.");

    private static string? OptionalString(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static SecurityApiKeyLocation ParseApiKeyLocation(string value, string schemeName) =>
        value switch
        {
            "query" => SecurityApiKeyLocation.Query,
            "header" => SecurityApiKeyLocation.Header,
            "cookie" => SecurityApiKeyLocation.Cookie,
            _ => throw new InvalidOperationException(
                $"apiKey security scheme '{schemeName}' has unsupported location '{value}'."
            ),
        };

    private static OAuth2FlowType ParseOAuthFlowType(string value, string schemeName) =>
        value switch
        {
            "implicit" => OAuth2FlowType.Implicit,
            "password" => OAuth2FlowType.Password,
            "clientCredentials" or "application" => OAuth2FlowType.ClientCredentials,
            "authorizationCode" or "accessCode" => OAuth2FlowType.AuthorizationCode,
            _ => throw new InvalidOperationException(
                $"OAuth2 security scheme '{schemeName}' has unsupported flow '{value}'."
            ),
        };

    private static HashSet<(
        string Path,
        string Method,
        string Status
    )> ReadSwaggerSchemaLessResponses(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (
            !root.TryGetProperty("swagger", out var version)
            || version.GetString() is not { } versionText
            || !versionText.StartsWith("2.", StringComparison.Ordinal)
            || !root.TryGetProperty("paths", out var paths)
        )
        {
            return [];
        }

        var result = new HashSet<(string Path, string Method, string Status)>();
        foreach (var path in paths.EnumerateObject())
        {
            foreach (var method in _operationNames)
            {
                if (
                    !path.Value.TryGetProperty(method, out var operation)
                    || !operation.TryGetProperty("responses", out var responses)
                )
                {
                    continue;
                }

                foreach (var response in responses.EnumerateObject())
                {
                    if (
                        response.Value.ValueKind == JsonValueKind.Object
                        && !response.Value.TryGetProperty("schema", out _)
                        && !response.Value.TryGetProperty("$ref", out _)
                    )
                    {
                        result.Add((path.Name, method, response.Name));
                    }
                }
            }
        }

        return result;
    }

    private static void RemoveConvertedSwaggerProducesContent(
        OpenApiDocument document,
        IReadOnlySet<(string Path, string Method, string Status)> schemaLessResponses
    )
    {
        foreach (var (path, method, status) in schemaLessResponses)
        {
            if (
                document.Paths.TryGetValue(path, out var pathItem)
                && pathItem
                    .Operations?.FirstOrDefault(operation =>
                        operation.Key.Method.Equals(method, StringComparison.OrdinalIgnoreCase)
                    )
                    .Value
                    is { } operation
                && operation.Responses?.TryGetValue(status, out var response) is true
            )
            {
                response.Content?.Clear();
            }
        }
    }

    private static string NormalizeLocalPathReferences(string json)
    {
        // Microsoft.OpenApi does not reliably resolve these non-component references.
        // Keep the rewrite on operation object surfaces; schema nodes stay opaque.
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (parsed is not JsonObject root || root["paths"] is not JsonObject paths)
        {
            return json;
        }

        if (!paths.Any(entry => entry.Value is JsonObject item && RequiresNormalization(item)))
        {
            return json;
        }

        foreach (var (path, node) in paths.ToList())
        {
            if (node is JsonObject pathItem)
            {
                paths[path] = ResolvePathItem(pathItem, root, new HashSet<string>());
            }
        }

        return root.ToJsonString();
    }

    private static bool RequiresNormalization(JsonObject pathItem)
    {
        if (GetLocalReference(pathItem) is not null || HasLocalParameterReference(pathItem))
        {
            return true;
        }

        return _operationNames.Any(method =>
            pathItem[method] is JsonObject operation && OperationRequiresNormalization(operation)
        );
    }

    private static bool OperationRequiresNormalization(JsonObject operation)
    {
        return HasLocalParameterReference(operation)
            || operation["requestBody"] is JsonObject requestBody
                && (
                    ShouldNormalizeReference(requestBody, ComponentRequestBodiesPrefix)
                    || ContentHasLocalExampleReference(requestBody)
                )
            || operation["responses"] is JsonObject responses
                && responses.Any(response =>
                    response.Value is JsonObject responseObject
                    && ResponseRequiresNormalization(responseObject)
                );
    }

    private static bool ResponseRequiresNormalization(JsonObject response)
    {
        return ShouldNormalizeReference(response, ComponentResponsesPrefix)
            || ContentHasLocalExampleReference(response)
            || response["headers"] is JsonObject headers
                && headers.Any(header =>
                    header.Value is JsonObject headerObject
                    && (
                        ShouldNormalizeReference(headerObject, ComponentHeadersPrefix)
                        || HasLocalExampleReference(headerObject)
                        || ContentHasLocalExampleReference(headerObject)
                    )
                );
    }

    private static bool ContentHasLocalExampleReference(JsonObject owner)
    {
        return owner["content"] is JsonObject content
            && content.Any(media =>
                media.Value is JsonObject mediaObject && HasLocalExampleReference(mediaObject)
            );
    }

    private static bool HasLocalExampleReference(JsonObject owner)
    {
        return owner["examples"] is JsonObject examples
            && examples.Any(example =>
                example.Value is JsonObject exampleObject
                && ShouldNormalizeExampleReference(exampleObject)
            );
    }

    private static bool ShouldNormalizeExampleReference(JsonObject example)
    {
        return ShouldNormalizeReference(example, ComponentExamplesPrefix);
    }

    private static bool ShouldNormalizeReference(JsonObject value, string componentPrefix)
    {
        return GetLocalReference(value) is { } reference
            && !reference.StartsWith(componentPrefix, StringComparison.Ordinal);
    }

    private static bool HasLocalParameterReference(JsonObject owner)
    {
        return owner["parameters"] is JsonArray parameters
            && parameters.Any(parameter =>
                parameter is JsonObject parameterObject
                && (
                    GetLocalReference(parameterObject) is not null
                    || HasLocalExampleReference(parameterObject)
                    || ContentHasLocalExampleReference(parameterObject)
                )
            );
    }

    private static JsonObject ResolvePathItem(
        JsonObject pathItem,
        JsonObject root,
        HashSet<string> referenceChain
    )
    {
        JsonObject? referenced = null;
        if (GetLocalReference(pathItem) is { } reference)
        {
            var pointer = DecodePointer(reference);
            if (!referenceChain.Add(pointer))
            {
                throw new InvalidOperationException(
                    $"Cyclic local path-item reference detected at '{reference}'."
                );
            }

            referenced =
                ResolvePointer(root, reference) as JsonObject
                ?? throw new InvalidOperationException(
                    $"Local path-item reference '{reference}' does not target an object."
                );
            referenced = ResolvePathItem(referenced, root, referenceChain);
            referenceChain.Remove(pointer);
        }

        var local = (JsonObject)pathItem.DeepClone();
        local.Remove("$ref");
        NormalizePathItemContents(local, root);
        if (referenced is null)
        {
            return local;
        }

        var merged = (JsonObject)referenced.DeepClone();
        foreach (var (name, value) in local)
        {
            if (
                name == "parameters"
                && merged["parameters"] is JsonArray baseParameters
                && value is JsonArray localParameters
            )
            {
                merged[name] = MergeParameterArrays(baseParameters, localParameters);
            }
            else
            {
                merged[name] = value?.DeepClone();
            }
        }

        return merged;
    }

    private static void NormalizePathItemContents(JsonObject pathItem, JsonObject root)
    {
        if (pathItem["parameters"] is JsonArray pathParameters)
        {
            pathItem["parameters"] = NormalizeParameterArray(pathParameters, root);
        }

        foreach (var method in _operationNames)
        {
            if (pathItem[method] is not JsonObject operation)
            {
                continue;
            }

            if (operation["parameters"] is JsonArray operationParameters)
            {
                operation["parameters"] = NormalizeParameterArray(operationParameters, root);
            }

            if (operation["requestBody"] is JsonObject requestBody)
            {
                var reference = GetLocalReference(requestBody);
                var resolved = ShouldNormalizeReference(requestBody, ComponentRequestBodiesPrefix)
                    ? ResolveReferenceObject(requestBody, root, "request body")
                    : (JsonObject)requestBody.DeepClone();
                if (
                    reference is not null
                    && ShouldNormalizeReference(requestBody, ComponentRequestBodiesPrefix)
                )
                {
                    resolved[ImportedRequestBodyReferenceExtension] = reference;
                }
                if (GetLocalReference(resolved) is null)
                {
                    NormalizeContentExamples(resolved, root);
                }
                operation["requestBody"] = resolved;
            }

            if (operation["responses"] is JsonObject responses)
            {
                foreach (var (status, response) in responses.ToList())
                {
                    if (response is JsonObject responseObject)
                    {
                        var resolved = ShouldNormalizeReference(
                            responseObject,
                            ComponentResponsesPrefix
                        )
                            ? ResolveReferenceObject(responseObject, root, "response")
                            : (JsonObject)responseObject.DeepClone();
                        if (GetLocalReference(resolved) is null)
                        {
                            NormalizeResponse(resolved, root);
                        }
                        responses[status] = resolved;
                    }
                }
            }
        }
    }

    private static void NormalizeResponse(JsonObject response, JsonObject root)
    {
        NormalizeContentExamples(response, root);
        if (response["headers"] is not JsonObject headers)
        {
            return;
        }

        foreach (var (name, header) in headers.ToList())
        {
            if (header is JsonObject headerObject)
            {
                var resolved = ShouldNormalizeReference(headerObject, ComponentHeadersPrefix)
                    ? ResolveReferenceObject(headerObject, root, "response header")
                    : (JsonObject)headerObject.DeepClone();
                if (GetLocalReference(resolved) is null)
                {
                    NormalizeExamples(resolved, root);
                    NormalizeContentExamples(resolved, root);
                }
                headers[name] = resolved;
            }
        }
    }

    private static void NormalizeContentExamples(JsonObject owner, JsonObject root)
    {
        if (owner["content"] is not JsonObject content)
        {
            return;
        }

        foreach (var media in content.Select(entry => entry.Value).OfType<JsonObject>())
        {
            NormalizeExamples(media, root);
        }
    }

    private static void NormalizeExamples(JsonObject owner, JsonObject root)
    {
        if (owner["examples"] is not JsonObject examples)
        {
            return;
        }

        foreach (var (name, example) in examples.ToList())
        {
            if (example is JsonObject exampleObject)
            {
                examples[name] = ShouldNormalizeExampleReference(exampleObject)
                    ? ResolveReferenceObject(exampleObject, root, "example")
                    : exampleObject.DeepClone();
            }
        }
    }

    private static JsonArray NormalizeParameterArray(JsonArray parameters, JsonObject root)
    {
        var normalized = new JsonArray();
        foreach (var parameter in parameters)
        {
            if (parameter is not JsonObject parameterObject)
            {
                normalized.Add(parameter?.DeepClone());
                continue;
            }

            var reference = GetLocalReference(parameterObject);
            var resolved = ResolveReferenceObject(parameterObject, root, "parameter");
            if (reference is not null)
            {
                resolved[ImportedParameterReferenceExtension] = reference;
            }
            NormalizeExamples(resolved, root);
            NormalizeContentExamples(resolved, root);
            normalized.Add(resolved);
        }

        return normalized;
    }

    private static JsonObject ResolveReferenceObject(
        JsonObject value,
        JsonObject root,
        string kind,
        HashSet<string>? referenceChain = null
    )
    {
        if (GetLocalReference(value) is not { } reference)
        {
            return (JsonObject)value.DeepClone();
        }

        referenceChain ??= new HashSet<string>();
        var pointer = DecodePointer(reference);
        if (!referenceChain.Add(pointer))
        {
            throw new InvalidOperationException(
                $"Cyclic local {kind} reference detected at '{reference}'."
            );
        }

        var target =
            ResolvePointer(root, reference) as JsonObject
            ?? throw new InvalidOperationException(
                $"Local {kind} reference '{reference}' does not target an object."
            );
        var resolved = ResolveReferenceObject(target, root, kind, referenceChain);
        referenceChain.Remove(pointer);

        foreach (var sibling in new[] { "summary", "description" })
        {
            if (value.TryGetPropertyValue(sibling, out var siblingValue))
            {
                resolved[sibling] = siblingValue?.DeepClone();
            }
        }

        return resolved;
    }

    private static JsonArray MergeParameterArrays(
        JsonArray baseParameters,
        JsonArray localParameters
    )
    {
        var merged = new JsonArray(
            baseParameters.Select(parameter => parameter?.DeepClone()).ToArray()
        );
        var indexes = new Dictionary<(string Name, string In), int>();
        for (var index = 0; index < merged.Count; index++)
        {
            if (GetParameterKey(merged[index]) is { } key)
            {
                indexes[key] = index;
            }
        }

        foreach (var parameter in localParameters)
        {
            var clone = parameter?.DeepClone();
            if (GetParameterKey(parameter) is { } key && indexes.TryGetValue(key, out var index))
            {
                merged[index] = clone;
            }
            else
            {
                if (GetParameterKey(parameter) is { } newKey)
                {
                    indexes[newKey] = merged.Count;
                }

                merged.Add(clone);
            }
        }

        return merged;
    }

    private static (string Name, string In)? GetParameterKey(JsonNode? parameter)
    {
        if (
            parameter is JsonObject obj
            && obj["name"]?.GetValue<string>() is { } name
            && obj["in"]?.GetValue<string>() is { } location
        )
        {
            return (name, location);
        }

        return null;
    }

    private static string? GetLocalReference(JsonObject obj)
    {
        return obj["$ref"]?.GetValue<string>() is { } reference && reference.StartsWith('#')
            ? reference
            : null;
    }

    private static JsonNode ResolvePointer(JsonObject root, string reference)
    {
        var pointer = DecodePointer(reference);
        JsonNode current = root;
        if (pointer.Length == 0)
        {
            return current;
        }

        foreach (var encodedToken in pointer[1..].Split('/'))
        {
            var token = encodedToken.Replace("~1", "/").Replace("~0", "~");
            current = current switch
            {
                JsonObject obj
                    when obj.TryGetPropertyValue(token, out var child) && child is not null =>
                    child,
                JsonArray array
                    when int.TryParse(token, out var index)
                        && index >= 0
                        && index < array.Count
                        && array[index] is { } child => child,
                _ => throw new InvalidOperationException(
                    $"Local JSON reference '{reference}' targets a missing value."
                ),
            };
        }

        return current;
    }

    private static string DecodePointer(string reference)
    {
        string pointer;
        try
        {
            pointer = Uri.UnescapeDataString(reference[1..]);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidOperationException(
                $"Local JSON reference '{reference}' has invalid percent encoding.",
                exception
            );
        }

        if (pointer.Length > 0 && pointer[0] != '/')
        {
            throw new InvalidOperationException(
                $"Local JSON reference '{reference}' is not a JSON Pointer."
            );
        }

        return pointer;
    }

    /// <summary>
    /// I1: detects components/schemas entries that are pure $ref aliases forming a cycle
    /// and replaces them with empty placeholder schemas, with a loud warning per entry.
    /// Returns the input unchanged when no cycle exists (the common case).
    /// </summary>
    private static string BreakAliasCycles(string json, List<string> warnings)
    {
        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return json; // let the real parser produce its own error
        }

        if (root?["components"]?["schemas"] is not System.Text.Json.Nodes.JsonObject schemas)
        {
            return json;
        }

        const string prefix = "#/components/schemas/";
        var aliasTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in schemas)
        {
            if (
                node is System.Text.Json.Nodes.JsonObject obj
                && obj.TryGetPropertyValue("$ref", out var refNode)
                && refNode is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<string>(out var refString)
                && refString.StartsWith(prefix, StringComparison.Ordinal)
            )
            {
                aliasTargets[key] = refString[prefix.Length..];
            }
        }

        if (aliasTargets.Count == 0)
        {
            return json;
        }

        var cyclic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in aliasTargets.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { key };
            var current = key;
            while (aliasTargets.TryGetValue(current, out var next))
            {
                if (!visited.Add(next))
                {
                    // Everything on the chase path is unresolvable (in or pointing into the cycle)
                    cyclic.UnionWith(visited);
                    break;
                }

                current = next;
            }
        }

        if (cyclic.Count == 0)
        {
            return json;
        }

        foreach (var key in cyclic.OrderBy(k => k, StringComparer.Ordinal))
        {
            warnings.Add(
                Diagnostics.Prefix(
                    Diagnostics.ImportAliasCycleBroken,
                    $"Alias schema '{key}' is part of a $ref cycle — replaced with an empty schema; consumers resolve to an untyped object."
                )
            );
            schemas[key] = new System.Text.Json.Nodes.JsonObject
            {
                ["description"] = "[rivet:unsupported] cyclic $ref alias",
            };
        }

        return root.ToJsonString();
    }
}

public sealed record ImportOptions(string Namespace, string? SecurityScheme = null);

public sealed record ImportResult(
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<string> Warnings
);

public sealed record GeneratedFile(string FileName, string Content);
