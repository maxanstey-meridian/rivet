using System.Text.Json;
using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

internal sealed class OpenApiEmissionException(string message) : InvalidOperationException(message);

/// <summary>
/// Emits an OpenAPI 3.1 JSON spec from the Rivet model.
/// </summary>
public static class OpenApiEmitter
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Per-Emit-call naming state. Component names must be unique per shape — the pure
    /// name suffixes are lossy in places ("Enum", "Object"), so a pre-pass assigns every
    /// distinct shape a distinct deterministic name (numeric suffix on residual collisions)
    /// that all $ref emission sites then consult. Also accumulates tagged-union variant
    /// component schemas discovered while mapping types.
    /// </summary>
    private sealed class EmitContext
    {
        public IReadOnlyDictionary<string, TsTypeDefinition> Definitions { get; init; } =
            new Dictionary<string, TsTypeDefinition>();

        public IReadOnlyDictionary<string, TsType.Brand> Brands { get; init; } =
            new Dictionary<string, TsType.Brand>();

        public IReadOnlyDictionary<string, TsType> Enums { get; init; } =
            new Dictionary<string, TsType>();

        public HashSet<string> InliningSyntheticTypes { get; } = new(StringComparer.Ordinal);

        /// <summary>Canonical shape hash → assigned component name for monomorphised generics.</summary>
        public Dictionary<string, string> GenericNames { get; } = [];

        /// <summary>Canonical shape hash → base component name for tagged unions.</summary>
        public Dictionary<string, string> TaggedUnionNames { get; } = [];

        /// <summary>Controller/endpoint/shape identity → assigned route-filtered body component name.</summary>
        public Dictionary<string, string> FilteredBodyNames { get; } = [];

        /// <summary>Variant component schemas synthesized for tagged unions.</summary>
        public Dictionary<string, object> ExtraComponents { get; } = [];
    }

    [ThreadStatic]
    private static EmitContext? _ctx;

    public static string Emit(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        SecurityConfig? security,
        OpenApiDocumentInfo? documentInfo = null
    ) =>
        EmitWithSecurityMetadata(
            endpoints,
            definitions,
            brands,
            enums,
            ToSecurityMetadata(security),
            documentInfo
        );

    public static string EmitWithSecurityMetadata(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        ContractSecurityMetadata? security,
        OpenApiDocumentInfo? documentInfo = null
    )
    {
        _ctx = new EmitContext
        {
            Definitions = definitions,
            Brands = brands,
            Enums = enums,
        };
        try
        {
            var normalizedEndpoints = endpoints
                .Select(endpoint =>
                    endpoint with
                    {
                        Responses = ResponseStatusValidation.NormalizeIrAndEnsureResponse(
                            endpoint.Responses,
                            endpoint
                        ),
                    }
                )
                .ToList();
            AssignComponentNames(normalizedEndpoints, definitions, brands, enums, _ctx);
            return EmitCore(
                normalizedEndpoints,
                definitions,
                brands,
                enums,
                security,
                documentInfo ?? new OpenApiDocumentInfo()
            );
        }
        finally
        {
            _ctx = null;
        }
    }

    private static string EmitCore(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        ContractSecurityMetadata? security,
        OpenApiDocumentInfo documentInfo
    )
    {
        var requestBodyComponents = documentInfo.Provenance?.ComponentRequestBodies ?? [];
        var parameterComponents = documentInfo.Provenance?.ComponentParameters ?? [];
        var responseComponents = documentInfo.Provenance?.ComponentResponses ?? [];
        var paths = BuildPaths(
            endpoints,
            definitions,
            requestBodyComponents
                .Select(component => component.Name)
                .ToHashSet(StringComparer.Ordinal),
            parameterComponents
                .Select(component => component.Name)
                .ToHashSet(StringComparer.Ordinal),
            responseComponents.Select(component => component.Name).ToHashSet(StringComparer.Ordinal)
        );
        var schemas = BuildSchemas(endpoints, definitions, brands, enums);
        foreach (var schema in documentInfo.Provenance?.ComponentSchemas ?? [])
        {
            schemas[schema.Name] = ParseSchemaObject(
                schema.Json,
                $"preserved component schema '{schema.Name}'"
            );
        }
        var examples = BuildComponentExamples(
            endpoints,
            documentInfo.Provenance?.ComponentExamples ?? [],
            requestBodyComponents
        );
        var requestBodies = BuildComponentRequestBodies(requestBodyComponents);

        // Tagged-union variant components synthesized while mapping types above
        if (_ctx is not null)
        {
            foreach (var (name, schema) in _ctx.ExtraComponents)
            {
                if (!schemas.TryAdd(name, schema))
                {
                    Diagnostics.Warn(
                        Diagnostics.TaggedUnionComponentCollision,
                        $"tagged-union variant component '{name}' collides with an existing schema — existing schema wins"
                    );
                }
            }
        }

        var info = new Dictionary<string, object>
        {
            ["title"] = documentInfo.Title,
            ["version"] = documentInfo.Version,
        };
        if (documentInfo.Provenance?.Info.Description is { } infoDescription)
        {
            info["description"] = infoDescription;
        }
        if (documentInfo.Provenance?.Info.TermsOfService is { } termsOfService)
        {
            info["termsOfService"] = termsOfService;
        }
        if (documentInfo.Provenance?.Info.Contact is { } contact)
        {
            var contactValue = new Dictionary<string, object>();
            AddOptionalString(contactValue, "name", contact.Name);
            AddOptionalString(contactValue, "url", contact.Url);
            AddOptionalString(contactValue, "email", contact.Email);
            info["contact"] = contactValue;
        }
        if (documentInfo.Provenance?.Info.License is { } license)
        {
            var licenseValue = new Dictionary<string, object> { ["name"] = license.Name };
            AddOptionalString(licenseValue, "url", license.Url);
            AddOptionalString(licenseValue, "identifier", license.Identifier);
            info["license"] = licenseValue;
        }

        var doc = new Dictionary<string, object> { ["openapi"] = "3.1.0", ["info"] = info };

        if (documentInfo.Servers is { Count: > 0 })
        {
            doc["servers"] = documentInfo
                .Servers.Select(object (url) => new Dictionary<string, object> { ["url"] = url })
                .ToList();
        }
        else if (documentInfo.Provenance?.Servers is { Count: > 0 } provenanceServers)
        {
            doc["servers"] = provenanceServers.Select(BuildServer).ToList();
        }

        // W4: operations carry tags — declare them in the global tags array
        // (operation-tag-defined; docs-UI consumers use it for grouping/ordering).
        if (documentInfo.Provenance is { } documentProvenance)
        {
            if (documentProvenance.Tags.Count > 0)
            {
                doc["tags"] = documentProvenance.Tags.Select(BuildTag).ToList();
            }
            if (documentProvenance.ExternalDocs is { } externalDocs)
            {
                doc["externalDocs"] = BuildExternalDocs(externalDocs);
            }
        }
        else
        {
            var tags = endpoints
                .Select(ep => UpperFirst(ep.ControllerName))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .ToList();
            if (tags.Count > 0)
            {
                doc["tags"] = tags.Select(
                        object (tag) => new Dictionary<string, object> { ["name"] = tag }
                    )
                    .ToList();
            }
        }

        doc["paths"] = paths;

        var components = new Dictionary<string, object>();

        if (schemas.Count > 0)
        {
            components["schemas"] = schemas;
        }

        if (examples.Count > 0)
        {
            components["examples"] = examples;
        }

        if (requestBodies.Count > 0)
        {
            components["requestBodies"] = requestBodies;
        }
        AddJsonComponents(
            components,
            "parameters",
            parameterComponents.Select(value => (value.Name, value.Json))
        );
        AddJsonComponents(
            components,
            "responses",
            responseComponents.Select(value => (value.Name, value.Json))
        );

        var securitySchemes = new Dictionary<string, object>();

        if (security is not null)
        {
            foreach (var (name, definition) in security.Schemes)
            {
                if (!securitySchemes.TryAdd(name, BuildSecurityScheme(definition)))
                {
                    throw new OpenApiEmissionException(
                        $"error {Diagnostics.DuplicateSecuritySchemeDefinition}: duplicate security scheme definition '{name}'"
                    );
                }
            }

            if (security.GlobalRequirements is { } globalRequirements)
            {
                ValidateSecurityRequirements(globalRequirements, security.Schemes, "root");
                doc["security"] = BuildSecurityRequirements(globalRequirements);
            }
        }

        // Every endpoint security requirement must reference the configured scheme. Its type
        // cannot be inferred from a name, so generation fails rather than inventing semantics.
        var endpointSchemes = endpoints
            .Select(ep => ep.Security?.Scheme)
            .Where(scheme => scheme is not null)
            .Distinct()
            .OrderBy(scheme => scheme, StringComparer.Ordinal);

        foreach (var scheme in endpointSchemes)
        {
            if (securitySchemes.ContainsKey(scheme!))
            {
                continue;
            }

            throw new OpenApiEmissionException(
                $"error {Diagnostics.UndefinedSecurityScheme}: security scheme '{scheme}' is referenced by "
                    + $"an endpoint's .Secure(\"{scheme}\") but has no definition; define the same scheme with --security"
            );
        }

        foreach (
            var endpoint in endpoints.Where(endpoint => endpoint.SecurityRequirements is not null)
        )
        {
            ValidateSecurityRequirements(
                endpoint.SecurityRequirements!,
                security?.Schemes ?? new Dictionary<string, SecuritySchemeDefinition>(),
                $"operation {endpoint.HttpMethod} {endpoint.RouteTemplate}"
            );
        }

        if (securitySchemes.Count > 0)
        {
            components["securitySchemes"] = securitySchemes;
        }

        EnsureReferencedSchemaComponents(paths, components, schemas);
        if (schemas.Count > 0)
        {
            components["schemas"] = schemas;
        }

        if (components.Count > 0)
        {
            doc["components"] = components;
        }

        var vendorExtensions = documentInfo.Provenance?.VendorExtensions ?? [];
        RetainVendorExtensionPathItemOwners(paths, vendorExtensions);
        AttachVendorExtensions(doc, vendorExtensions);

        return JsonSerializer.Serialize(doc, _jsonOptions);
    }

    private static void EnsureReferencedSchemaComponents(
        Dictionary<string, object> paths,
        Dictionary<string, object> components,
        Dictionary<string, object> schemas
    )
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        CollectSchemaReferences(paths, referenced);
        CollectSchemaReferences(components, referenced);
        foreach (var componentId in referenced.Order(StringComparer.Ordinal))
        {
            if (schemas.ContainsKey(componentId))
            {
                continue;
            }

            Diagnostics.Warn(
                Diagnostics.UnknownTypeUntypedSchema,
                $"schema component '{componentId}' is referenced by emitted OpenAPI but has no recovered definition — emitting an untyped fallback component"
            );
            schemas[componentId] = new Dictionary<string, object>();
        }
    }

    private static void CollectSchemaReferences(object value, HashSet<string> referenced)
    {
        switch (value)
        {
            case Dictionary<string, object> dictionary:
                foreach (var (name, child) in dictionary)
                {
                    if (name == "$ref" && child is string reference)
                    {
                        AddSchemaReference(reference, referenced);
                    }
                    else
                    {
                        CollectSchemaReferences(child, referenced);
                    }
                }
                break;
            case IEnumerable<object> sequence:
                foreach (var child in sequence)
                {
                    CollectSchemaReferences(child, referenced);
                }
                break;
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                foreach (var property in element.EnumerateObject())
                {
                    if (
                        property.NameEquals("$ref")
                        && property.Value.ValueKind == JsonValueKind.String
                    )
                    {
                        AddSchemaReference(property.Value.GetString()!, referenced);
                    }
                    else
                    {
                        CollectSchemaReferences(property.Value, referenced);
                    }
                }
                break;
            case JsonElement { ValueKind: JsonValueKind.Array } element:
                foreach (var child in element.EnumerateArray())
                {
                    CollectSchemaReferences(child, referenced);
                }
                break;
        }
    }

    private static void AddSchemaReference(string reference, HashSet<string> referenced)
    {
        const string prefix = "#/components/schemas/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            return;
        }

        var token = reference[prefix.Length..];
        if (token.Contains('/', StringComparison.Ordinal))
        {
            return;
        }
        referenced.Add(
            Uri.UnescapeDataString(token)
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal)
        );
    }

    private static void RetainVendorExtensionPathItemOwners(
        Dictionary<string, object> paths,
        IReadOnlyList<OpenApiVendorExtensionProvenance> extensions
    )
    {
        const string prefix = "#/paths/";
        foreach (var extension in extensions)
        {
            if (
                !extension.OwnerPointer.StartsWith(prefix, StringComparison.Ordinal)
                || extension.OwnerPointer[prefix.Length..].Contains('/', StringComparison.Ordinal)
            )
            {
                continue;
            }

            var encodedPath = extension.OwnerPointer[prefix.Length..];
            var path = Uri.UnescapeDataString(encodedPath)
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            paths.TryAdd(path, new Dictionary<string, object>());
        }
    }

    private static void AttachVendorExtensions(
        Dictionary<string, object> document,
        IReadOnlyList<OpenApiVendorExtensionProvenance> extensions
    )
    {
        foreach (var extension in extensions)
        {
            var owner = ResolveObjectPointer(document, extension.OwnerPointer);
            if (owner is null)
            {
                throw new OpenApiEmissionException(
                    $"Cannot attach preserved vendor extension '{extension.Name}': emitted owner '{extension.OwnerPointer}' does not exist or is not an object."
                );
            }
            if (owner.ContainsKey(extension.Name))
            {
                throw new OpenApiEmissionException(
                    $"Cannot attach preserved vendor extension '{extension.Name}' at '{extension.OwnerPointer}': the emitted owner already contains that property."
                );
            }

            try
            {
                owner[extension.Name] = JsonSerializer.Deserialize<JsonElement>(
                    extension.JsonValue
                );
            }
            catch (JsonException exception)
            {
                throw new OpenApiEmissionException(
                    $"Cannot attach preserved vendor extension '{extension.Name}' at '{extension.OwnerPointer}': invalid JSON value ({exception.Message})."
                );
            }
        }
    }

    private static Dictionary<string, object>? ResolveObjectPointer(
        Dictionary<string, object> document,
        string pointer
    )
    {
        if (pointer == "#")
        {
            return document;
        }
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        object current = document;
        foreach (var encodedToken in pointer[2..].Split('/'))
        {
            var token = Uri.UnescapeDataString(encodedToken)
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                Dictionary<string, object> obj when obj.TryGetValue(token, out var child) => child,
                List<object> array
                    when int.TryParse(token, out var index) && index >= 0 && index < array.Count =>
                    array[index],
                _ => null!,
            };
            if (current is null)
            {
                return null;
            }
        }

        return current as Dictionary<string, object>;
    }

    private static ContractSecurityMetadata? ToSecurityMetadata(SecurityConfig? security)
    {
        if (security is null)
        {
            return null;
        }

        var schemes = new Dictionary<string, SecuritySchemeDefinition>(StringComparer.Ordinal)
        {
            [security.SchemeName] = security.SchemeDefinition,
        };
        if (security.AdditionalSchemeDefinitions is not null)
        {
            foreach (var (name, definition) in security.AdditionalSchemeDefinitions)
            {
                if (!schemes.TryAdd(name, definition))
                {
                    throw new OpenApiEmissionException(
                        $"error {Diagnostics.DuplicateSecuritySchemeDefinition}: duplicate security scheme definition '{name}'"
                    );
                }
            }
        }

        var globalRequirements = new SecurityRequirements([
            new SecurityRequirement([new SecurityRequirementScheme(security.SchemeName, [])]),
        ]);
        return new ContractSecurityMetadata(schemes, globalRequirements);
    }

    private static Dictionary<string, object> BuildSecurityScheme(
        SecuritySchemeDefinition definition
    )
    {
        var result = new Dictionary<string, object>();
        if (definition.Description is not null)
        {
            result["description"] = definition.Description;
        }

        switch (definition)
        {
            case ApiKeySecurityScheme apiKey:
                result["type"] = "apiKey";
                result["name"] = apiKey.Name;
                result["in"] = apiKey.Location.ToString().ToLowerInvariant();
                break;
            case HttpSecurityScheme http:
                result["type"] = "http";
                result["scheme"] = http.Scheme;
                if (http.BearerFormat is not null)
                {
                    result["bearerFormat"] = http.BearerFormat;
                }
                break;
            case OAuth2SecurityScheme oauth2:
                result["type"] = "oauth2";
                result["flows"] = oauth2.Flows.ToDictionary(
                    flow => OAuthFlowName(flow.Type),
                    BuildOAuthFlow,
                    StringComparer.Ordinal
                );
                break;
            case OpenIdConnectSecurityScheme openId:
                result["type"] = "openIdConnect";
                result["openIdConnectUrl"] = openId.OpenIdConnectUrl;
                break;
            case MutualTlsSecurityScheme:
                result["type"] = "mutualTLS";
                break;
            default:
                throw new OpenApiEmissionException(
                    $"Unsupported security scheme model '{definition.GetType().Name}'."
                );
        }

        return result;
    }

    private static Dictionary<string, object> BuildOAuthFlow(OAuth2Flow flow)
    {
        var result = new Dictionary<string, object> { ["scopes"] = flow.Scopes };
        if (flow.AuthorizationUrl is not null)
        {
            result["authorizationUrl"] = flow.AuthorizationUrl;
        }
        if (flow.TokenUrl is not null)
        {
            result["tokenUrl"] = flow.TokenUrl;
        }
        if (flow.RefreshUrl is not null)
        {
            result["refreshUrl"] = flow.RefreshUrl;
        }
        return result;
    }

    private static string OAuthFlowName(OAuth2FlowType type) =>
        type switch
        {
            OAuth2FlowType.Implicit => "implicit",
            OAuth2FlowType.Password => "password",
            OAuth2FlowType.ClientCredentials => "clientCredentials",
            OAuth2FlowType.AuthorizationCode => "authorizationCode",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    private static List<object> BuildSecurityRequirements(SecurityRequirements requirements) =>
        requirements
            .Alternatives.Select(
                object (requirement) =>
                    requirement.Schemes.ToDictionary(
                        scheme => scheme.Name,
                        scheme => (object)scheme.Scopes,
                        StringComparer.Ordinal
                    )
            )
            .ToList();

    private static void ValidateSecurityRequirements(
        SecurityRequirements requirements,
        IReadOnlyDictionary<string, SecuritySchemeDefinition> schemes,
        string context
    )
    {
        foreach (
            var name in requirements.Alternatives.SelectMany(requirement =>
                requirement.Schemes.Select(scheme => scheme.Name)
            )
        )
        {
            if (!schemes.ContainsKey(name))
            {
                throw new OpenApiEmissionException(
                    $"error {Diagnostics.UndefinedSecurityScheme}: security scheme '{name}' is referenced by {context} security requirements but has no definition"
                );
            }
        }
    }

    /// <summary>
    /// OpenAPI 3.x rule: Accept, Content-Type and Authorization must not be declared
    /// as header parameters.
    /// </summary>
    private static bool IsReservedHeaderName(string name) =>
        name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object> BuildPaths(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlySet<string> requestBodyComponentIds,
        IReadOnlySet<string> parameterComponentIds,
        IReadOnlySet<string> responseComponentIds
    )
    {
        var paths = new Dictionary<string, object>();

        foreach (var ep in endpoints)
        {
            var pathKey = ep.RouteTemplate;

            if (!paths.TryGetValue(pathKey, out var existing))
            {
                existing = new Dictionary<string, object>();
                paths[pathKey] = existing;
            }

            var pathItem = (Dictionary<string, object>)existing;
            var methodKey = ep.HttpMethod.ToLowerInvariant();
            if (pathItem.ContainsKey(methodKey))
            {
                Diagnostics.Warn(
                    Diagnostics.DuplicateEndpoint,
                    $"duplicate endpoint {ep.HttpMethod} {pathKey} — later definition wins"
                );
            }
            var operation = BuildOperation(
                ep,
                definitions,
                requestBodyComponentIds,
                parameterComponentIds,
                responseComponentIds
            );
            pathItem[methodKey] = operation;
        }

        return paths;
    }

    private static Dictionary<string, object> BuildOperation(
        TsEndpointDefinition ep,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlySet<string> requestBodyComponentIds,
        IReadOnlySet<string> parameterComponentIds,
        IReadOnlySet<string> responseComponentIds
    )
    {
        var operation = new Dictionary<string, object>();
        if (ep.Provenance is null)
        {
            // Rivet identity is independent of authored operationId/tags. Imported documents
            // do not gain these extensions unless they already carried Rivet identity.
            operation["x-rivet-contract"] = ep.ControllerName;
            operation["x-rivet-endpoint"] = ep.Name;
        }
        else if (ep.Provenance.RivetIdentity is { } identity)
        {
            AddOptionalString(operation, "x-rivet-contract", identity.Contract);
            AddOptionalString(operation, "x-rivet-endpoint", identity.Endpoint);
        }
        if (ep.Provenance is { } provenance)
        {
            if (provenance.OperationIdPresent)
            {
                operation["operationId"] = provenance.OperationId!;
            }
            if (provenance.Tags.Count > 0)
            {
                operation["tags"] = provenance.Tags;
            }
            if (provenance.Deprecated)
            {
                operation["deprecated"] = true;
            }
            if (provenance.ServerOverride is { } serverOverride)
            {
                operation["servers"] = serverOverride.Select(BuildServer).ToList();
            }
        }
        else
        {
            operation["operationId"] = $"{ep.ControllerName}_{ep.Name}";
            operation["tags"] = new List<string> { UpperFirst(ep.ControllerName) };
        }

        if (ep.Summary is not null)
        {
            operation["summary"] = ep.Summary;
        }

        if (ep.Description is not null)
        {
            operation["description"] = ep.Description;
        }

        // Parameters (route + query)
        var parameters = new List<object>();
        TsEndpointParam? bodyParam = null;
        var fileParams = new List<TsEndpointParam>();
        var formFieldParams = new List<TsEndpointParam>();

        foreach (var param in ep.Params)
        {
            switch (param.Source)
            {
                case ParamSource.Route:
                    parameters.Add(
                        BuildParameter(
                            param,
                            "path",
                            required: true,
                            $"param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                        )
                    );
                    break;

                case ParamSource.Query:
                    parameters.Add(
                        BuildParameter(
                            param,
                            "query",
                            param.Type is not TsType.Nullable && !param.IsOptional,
                            $"param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                        )
                    );
                    break;

                case ParamSource.Header:
                    // OpenAPI 3.x: Accept/Content-Type/Authorization are not legal header
                    // parameters (they belong to content negotiation / securitySchemes) —
                    // diagnose and skip rather than emit an invalid spec.
                    if (IsReservedHeaderName(param.Name))
                    {
                        Diagnostics.Warn(
                            Diagnostics.ReservedHeaderParameterSkipped,
                            $"header param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}' is reserved by OpenAPI "
                                + "(Accept/Content-Type/Authorization are described by content/securitySchemes) — omitted from the spec"
                        );
                        break;
                    }

                    parameters.Add(
                        BuildParameter(
                            param,
                            "header",
                            param.Type is not TsType.Nullable && !param.IsOptional,
                            $"param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                        )
                    );
                    break;

                case ParamSource.Cookie:
                    parameters.Add(
                        BuildParameter(
                            param,
                            "cookie",
                            param.Type is not TsType.Nullable && !param.IsOptional,
                            $"param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                        )
                    );
                    break;

                case ParamSource.Body:
                    bodyParam = param;
                    break;

                case ParamSource.File:
                    fileParams.Add(param);
                    break;

                case ParamSource.FormField:
                    formFieldParams.Add(param);
                    break;
            }
        }

        // QueryAuth: emit auth token as a required query parameter
        if (ep.QueryAuth is not null)
        {
            parameters.Add(
                new Dictionary<string, object>
                {
                    ["name"] = ep.QueryAuth.ParameterName,
                    ["in"] = "query",
                    ["required"] = true,
                    ["schema"] = new Dictionary<string, object> { ["type"] = "string" },
                }
            );
        }

        if (parameters.Count > 0)
        {
            foreach (var reference in ep.Provenance?.ParameterComponentReferences ?? [])
            {
                if (!parameterComponentIds.Contains(reference.ComponentId))
                {
                    continue;
                }
                var index = parameters.FindIndex(value =>
                {
                    var parameter = (Dictionary<string, object>)value;
                    return parameter.GetValueOrDefault("name") as string == reference.Name
                        && parameter.GetValueOrDefault("in") as string == reference.Location;
                });
                if (index >= 0)
                {
                    parameters[index] = ComponentReference("parameters", reference.ComponentId);
                }
            }
            operation["parameters"] = parameters;
        }

        // Request body
        if (ep.RequestContents is not null)
        {
            var content = new Dictionary<string, object>();
            foreach (var entry in ep.RequestContents)
            {
                var media = new Dictionary<string, object>();
                if (entry.IsBinary)
                {
                    media["schema"] = BinarySchema();
                }
                else if (entry.Schema is not null)
                {
                    media["schema"] = BuildSchemaWithLeafProvenance(
                        entry.Schema,
                        entry.SchemaType,
                        entry.Format,
                        entry.IsFormatSpecified,
                        $"request content '{entry.MediaType}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                    );
                }

                content[entry.MediaType] = media;
            }

            var primaryRequestType = ep.RequestType ?? bodyParam?.Type;
            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] =
                    ep.RequestBodyRequired ?? (primaryRequestType is not TsType.Nullable),
                ["content"] = WithExamples(content, ep.RequestExamples),
            };
        }
        else if (ep.BinaryRequestContentType is not null)
        {
            // .AcceptsBinary(): the body is the raw bytes — never a JSON/multipart schema,
            // even if a Body param somehow survived upstream (the walker prevents it).
            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = ep.RequestBodyRequired ?? true,
                ["content"] = new Dictionary<string, object>
                {
                    [ep.BinaryRequestContentType] = new Dictionary<string, object>
                    {
                        ["schema"] = BinarySchema(),
                    },
                },
            };
        }
        else if (fileParams.Count > 0)
        {
            Dictionary<string, object> multipartSchema;

            if (ep.InputTypeName is not null && definitions.ContainsKey(ep.InputTypeName))
            {
                multipartSchema = MapTypeReference(
                    new TsType.TypeRef(ep.InputTypeName),
                    $"multipart input on endpoint '{ep.ControllerName}.{ep.Name}'"
                );
            }
            else
            {
                // BUG-2 (mirrors the E6 generic fallback): the TS lowerer decomposes the
                // multipart input into params and never ships the input type definition, so
                // a $ref to ep.InputTypeName would dangle — every consumer rejects that.
                // Build the multipart request schema inline from the endpoint's params instead.
                if (ep.InputTypeName is not null)
                {
                    Diagnostics.Warn(
                        Diagnostics.MultipartInputTypeMissing,
                        $"multipart input type '{ep.InputTypeName}' on endpoint '{ep.ControllerName}.{ep.Name}' "
                            + "is not present in the contract's type definitions — building the multipart request schema "
                            + "inline from the endpoint's params; fix the upstream producer to include the input type definition"
                    );
                }

                // Anonymous file upload — inline the schema. Single files emit the
                // binary File schema; collection-of-file params (List<IFormFile>,
                // FABLE_GAPS §7 item 12) emit array-of-binary — both via the File
                // primitive mapping.
                var multipartProps = new Dictionary<string, object>();
                foreach (var fp in fileParams)
                {
                    multipartProps[fp.Name] = MapTsTypeToJsonSchema(
                        fp.Type,
                        $"file param '{fp.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                    );
                }
                foreach (var ff in formFieldParams)
                {
                    multipartProps[ff.Name] = MapTsTypeToJsonSchema(
                        ff.Type,
                        $"form field '{ff.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                    );
                }

                var requiredFields = new List<string>();
                foreach (var fp in fileParams)
                {
                    // N1/E8: honour explicit optionality (rivet-ts `file?: Blob`)
                    if (!fp.IsOptional)
                    {
                        requiredFields.Add(fp.Name);
                    }
                }
                foreach (var ff in formFieldParams)
                {
                    if (ff.Type is not TsType.Nullable && !ff.IsOptional)
                    {
                        requiredFields.Add(ff.Name);
                    }
                }

                multipartSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = multipartProps,
                };

                if (requiredFields.Count > 0)
                {
                    multipartSchema["required"] = requiredFields;
                }

                // WP-1.1: pin the record name the importer synthesizes for this inline
                // body — without the extension it falls back to the operationId-derived
                // {fieldName}Request convention, which breaks under hand-edited ids.
                // A declared-but-undefined input type name (TS lowerer) takes precedence.
                multipartSchema["x-rivet-input-type"] =
                    ep.InputTypeName ?? SynthesizedInputTypeName(ep);
            }

            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = ep.RequestBodyRequired ?? true,
                ["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        ["multipart/form-data"] = new Dictionary<string, object>
                        {
                            ["schema"] = multipartSchema,
                        },
                    },
                    ep.RequestExamples
                ),
            };
        }
        else if (bodyParam is not null)
        {
            var bodyContentType = ep.IsFormEncoded
                ? "application/x-www-form-urlencoded"
                : ep.RequestContentTypeOverride ?? "application/json";
            operation["requestBody"] = new Dictionary<string, object>
            {
                // E11: a Nullable body type means the request body is optional
                ["required"] = ep.RequestBodyRequired ?? (bodyParam.Type is not TsType.Nullable),
                ["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        [bodyContentType] = new Dictionary<string, object>
                        {
                            ["schema"] = BuildBodySchema(bodyParam.Type, ep, definitions),
                        },
                    },
                    ep.RequestExamples
                ),
            };
        }
        else if (ep.RequestType is not null)
        {
            var requestTypeContentType = ep.IsFormEncoded
                ? "application/x-www-form-urlencoded"
                : ep.RequestContentTypeOverride ?? "application/json";
            operation["requestBody"] = new Dictionary<string, object>
            {
                // E11: a Nullable body type means the request body is optional
                ["required"] = ep.RequestBodyRequired ?? (ep.RequestType is not TsType.Nullable),
                ["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        [requestTypeContentType] = new Dictionary<string, object>
                        {
                            ["schema"] = BuildBodySchema(ep.RequestType, ep, definitions),
                        },
                    },
                    ep.RequestExamples
                ),
            };
        }
        else if (ep.RequestBodyPresent)
        {
            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = ep.RequestBodyRequired ?? false,
                ["content"] = WithExamples(new Dictionary<string, object>(), ep.RequestExamples),
            };
        }

        if (
            ep.Provenance?.RequestBodyComponentId is { } requestBodyComponentId
            && requestBodyComponentIds.Contains(requestBodyComponentId)
        )
        {
            var requestBodyReference = new Dictionary<string, object>
            {
                ["$ref"] =
                    $"#/components/requestBodies/{EscapeJsonPointerToken(requestBodyComponentId)}",
            };
            AddOptionalString(
                requestBodyReference,
                "description",
                ep.Provenance.RequestBodyDescription
            );
            operation["requestBody"] = requestBodyReference;
        }
        else if (
            ep.Provenance?.RequestBodyDescription is { } requestBodyDescription
            && operation.TryGetValue("requestBody", out var requestBodyValue)
        )
        {
            ((Dictionary<string, object>)requestBodyValue)["description"] = requestBodyDescription;
        }

        // Responses
        var responses = new Dictionary<string, object>();
        var fileResponseStatusKey = ep.FileContentType is null
            ? null
            : ep
                .Responses.FirstOrDefault(response => response.StatusCode is >= 200 and < 300)
                ?.EffectiveStatusKey;

        foreach (var resp in ep.Responses)
        {
            var respObj = new Dictionary<string, object>();

            respObj["description"] =
                resp.Description
                ?? (resp.StatusCode == 0 ? "Response" : DefaultStatusDescription(resp.StatusCode));

            // Declared response headers are spec-only at runtime.
            // required is emitted only on explicit opt-in — Rivet cannot enforce presence,
            // so defaulting it would over-promise.
            if (resp.Headers is { Count: > 0 })
            {
                var headerObjs = new Dictionary<string, object>();
                foreach (var header in resp.Headers)
                {
                    var headerObj = new Dictionary<string, object>();
                    if (header.Description is not null)
                    {
                        headerObj["description"] = header.Description;
                    }

                    if (header.Required)
                    {
                        headerObj["required"] = true;
                    }

                    if (header.IsDeprecated)
                    {
                        headerObj["deprecated"] = true;
                    }
                    if (header.Style is not null)
                    {
                        headerObj["style"] = header.Style;
                    }
                    if (header.Explode is { } explode)
                    {
                        headerObj["explode"] = explode;
                    }
                    if (header.AllowReserved)
                    {
                        headerObj["allowReserved"] = true;
                    }
                    if (header.AllowEmptyValue)
                    {
                        headerObj["allowEmptyValue"] = true;
                    }
                    if (header.Example is { } example)
                    {
                        headerObj["example"] = example;
                    }
                    if (header.Examples is { } examples)
                    {
                        headerObj["examples"] = examples;
                    }

                    var headerSchema = MapTsTypeToJsonSchema(
                        header.Type,
                        $"response header '{header.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                    );
                    if (header.SchemaExamples is { } schemaExamples)
                    {
                        headerSchema["examples"] = schemaExamples;
                    }
                    if (header.ContentType is not null)
                    {
                        headerObj["content"] = new Dictionary<string, object>
                        {
                            [header.ContentType] = new Dictionary<string, object>
                            {
                                ["schema"] = headerSchema,
                            },
                        };
                    }
                    else
                    {
                        headerObj["schema"] = headerSchema;
                    }
                    headerObjs[header.Name] = headerObj;
                }

                respObj["headers"] = headerObjs;
            }

            if (resp.Contents is { Count: > 0 })
            {
                var content = new Dictionary<string, object>();
                foreach (var entry in resp.Contents)
                {
                    var media = new Dictionary<string, object>();
                    if (entry.IsBinary)
                    {
                        media["schema"] = BinarySchema();
                    }
                    else if (entry.Schema is not null)
                    {
                        var responseSchema = BuildSchemaWithLeafProvenance(
                            entry.Schema,
                            entry.SchemaType,
                            entry.Format,
                            entry.IsFormatSpecified,
                            $"response {resp.StatusCode} content '{entry.MediaType}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                        );
                        if (entry.SchemaDescription is not null)
                        {
                            responseSchema["description"] = entry.SchemaDescription;
                        }
                        media["schema"] = responseSchema;
                    }

                    content[entry.MediaType] = media;
                }

                respObj["content"] = WithExamples(content, resp.Examples);
            }
            else if (resp.DataType is not null)
            {
                // .ProducesContentType() overrides the SUCCESS response's media
                // type only — declared error responses stay application/json.
                var responseContentType = resp.StatusCode is >= 200 and < 300
                    ? ep.ResponseContentTypeOverride ?? "application/json"
                    : "application/json";
                respObj["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        [responseContentType] = new Dictionary<string, object>
                        {
                            ["schema"] = MapTsTypeToJsonSchema(
                                resp.DataType,
                                $"response {resp.StatusCode} on endpoint '{ep.ControllerName}.{ep.Name}'"
                            ),
                        },
                    },
                    resp.Examples
                );
            }
            else if (
                ep.FileContentType is not null
                && resp.EffectiveStatusKey == fileResponseStatusKey
            )
            {
                respObj["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        [ep.FileContentType] = new Dictionary<string, object>
                        {
                            ["schema"] = BinarySchema(),
                        },
                    },
                    resp.Examples
                );
            }
            else if (resp.Examples is not null)
            {
                var content = WithExamples(new Dictionary<string, object>(), resp.Examples);
                if (content.Count > 0)
                {
                    respObj["content"] = content;
                }
            }

            responses[resp.EffectiveStatusKey] = respObj;
        }

        foreach (var reference in ep.Provenance?.ResponseComponentReferences ?? [])
        {
            if (
                responseComponentIds.Contains(reference.ComponentId)
                && responses.ContainsKey(reference.StatusKey)
            )
            {
                responses[reference.StatusKey] = ComponentReference(
                    "responses",
                    reference.ComponentId
                );
            }
        }

        operation["responses"] = responses;

        // Security
        if (ep.SecurityRequirements is { } securityRequirements)
        {
            operation["security"] = BuildSecurityRequirements(securityRequirements);
        }
        else if (ep.Security is not null)
        {
            if (ep.Security.IsAnonymous)
            {
                operation["security"] = new List<object>();
            }
            else if (ep.Security.Scheme is not null)
            {
                operation["security"] = new List<object>
                {
                    new Dictionary<string, object> { [ep.Security.Scheme] = Array.Empty<string>() },
                };
            }
        }

        // QueryAuth: emit extension for round-trip fidelity
        if (ep.QueryAuth is not null)
        {
            operation["x-rivet-query-auth"] = new Dictionary<string, object>
            {
                ["parameterName"] = ep.QueryAuth.ParameterName,
            };
        }

        ApplyOperationSchemaProvenance(operation, ep.Provenance?.Schemas);

        return operation;
    }

    private static void ApplyOperationSchemaProvenance(
        Dictionary<string, object> operation,
        OpenApiOperationSchemaProvenance? provenance
    )
    {
        if (provenance is null)
        {
            return;
        }

        if (operation.TryGetValue("parameters", out var parameterValue))
        {
            var parameters = (List<object>)parameterValue;
            foreach (var source in provenance.Parameters)
            {
                var parameter = parameters
                    .OfType<Dictionary<string, object>>()
                    .FirstOrDefault(candidate =>
                        candidate.GetValueOrDefault("name") as string == source.Name
                        && candidate.GetValueOrDefault("in") as string == source.Location
                    );
                if (parameter is not null)
                {
                    parameter["schema"] = ParseSchemaObject(
                        source.Json,
                        $"parameter '{source.Location}:{source.Name}'"
                    );
                }
            }
        }

        if (
            operation.TryGetValue("requestBody", out var requestBodyValue)
            && requestBodyValue is Dictionary<string, object> requestBody
            && requestBody.TryGetValue("content", out var requestContentValue)
            && requestContentValue is Dictionary<string, object> requestContent
        )
        {
            foreach (var source in provenance.Requests)
            {
                if (
                    requestContent.TryGetValue(source.MediaType, out var mediaValue)
                    && mediaValue is Dictionary<string, object> media
                )
                {
                    media["schema"] = ParseSchemaObject(
                        source.Json,
                        $"request content '{source.MediaType}'"
                    );
                }
            }
        }

        if (
            operation.TryGetValue("responses", out var responseValue)
            && responseValue is Dictionary<string, object> responses
        )
        {
            foreach (var source in provenance.Responses)
            {
                if (
                    responses.TryGetValue(source.StatusKey, out var statusValue)
                    && statusValue is Dictionary<string, object> status
                    && status.TryGetValue("content", out var contentValue)
                    && contentValue is Dictionary<string, object> content
                    && content.TryGetValue(source.MediaType, out var mediaValue)
                    && mediaValue is Dictionary<string, object> media
                )
                {
                    media["schema"] = ParseSchemaObject(
                        source.Json,
                        $"response '{source.StatusKey}' content '{source.MediaType}'"
                    );
                }
            }
        }
    }

    private static Dictionary<string, object> ParseSchemaObject(string json, string context) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(json)
        ?? throw new OpenApiEmissionException($"{context} is not a JSON object.");

    private static Dictionary<string, object> BuildServer(OpenApiServerProvenance server)
    {
        var result = new Dictionary<string, object> { ["url"] = server.Url };
        AddOptionalString(result, "description", server.Description);
        if (server.Variables.Count > 0)
        {
            result["variables"] = server.Variables.ToDictionary(
                variable => variable.Name,
                variable =>
                {
                    var value = new Dictionary<string, object>
                    {
                        ["default"] = variable.DefaultValue,
                    };
                    if (variable.AllowedValues.Count > 0)
                    {
                        value["enum"] = variable.AllowedValues;
                    }
                    AddOptionalString(value, "description", variable.Description);
                    return (object)value;
                },
                StringComparer.Ordinal
            );
        }
        return result;
    }

    private static Dictionary<string, object> BuildTag(OpenApiTagProvenance tag)
    {
        var result = new Dictionary<string, object> { ["name"] = tag.Name };
        AddOptionalString(result, "description", tag.Description);
        if (tag.ExternalDocs is { } externalDocs)
        {
            result["externalDocs"] = BuildExternalDocs(externalDocs);
        }
        return result;
    }

    private static Dictionary<string, object> BuildExternalDocs(
        OpenApiExternalDocsProvenance externalDocs
    )
    {
        var result = new Dictionary<string, object> { ["url"] = externalDocs.Url };
        AddOptionalString(result, "description", externalDocs.Description);
        return result;
    }

    private static void AddOptionalString(
        Dictionary<string, object> target,
        string name,
        string? value
    )
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }

    private static Dictionary<string, object> BuildParameter(
        TsEndpointParam parameter,
        string location,
        bool required,
        string context
    )
    {
        var schema = BuildSchemaWithLeafProvenance(
            parameter.Type,
            parameter.SchemaType,
            parameter.Format,
            parameter.IsFormatSpecified,
            context
        );
        if (parameter.DefaultValue is not null)
        {
            schema["default"] = JsonSerializer.Deserialize<JsonElement>(parameter.DefaultValue);
        }
        SchemaEnricher.EnrichConstraints(schema, parameter.Constraints);
        if (parameter.SchemaExamples is { } schemaExamples)
        {
            schema["examples"] = schemaExamples;
        }

        var result = new Dictionary<string, object>
        {
            ["name"] = parameter.Name,
            ["in"] = location,
            ["required"] = required,
            ["schema"] = schema,
        };
        if (parameter.Description is not null)
        {
            result["description"] = parameter.Description;
        }
        if (parameter.IsDeprecated)
        {
            result["deprecated"] = true;
        }
        if (parameter.Example is { } example)
        {
            result["example"] = example;
        }
        if (parameter.Examples is { } examples)
        {
            result["examples"] = examples;
        }
        if (parameter.Style is not null)
        {
            result["style"] = parameter.Style;
        }
        if (parameter.Explode is { } explode)
        {
            result["explode"] = explode;
        }
        if (location == "query" && parameter.AllowEmptyValue)
        {
            result["allowEmptyValue"] = true;
        }

        return result;
    }

    private static Dictionary<string, object> BuildSchemaWithLeafProvenance(
        TsType type,
        string? schemaType,
        string? format,
        bool isFormatSpecified,
        string context
    )
    {
        if (schemaType is not null)
        {
            var schema = new Dictionary<string, object>
            {
                ["type"] =
                    type is TsType.Nullable ? new List<string> { schemaType, "null" } : schemaType,
            };
            if (format is not null)
            {
                schema["format"] = format;
            }
            return schema;
        }

        var inferred = MapTsTypeToJsonSchema(type, context);
        if (isFormatSpecified)
        {
            if (format is null)
            {
                inferred.Remove("format");
            }
            else
            {
                inferred["format"] = format;
            }
        }
        return inferred;
    }

    /// <summary>
    /// WP-1.1: the record name the importer should synthesize for an inline request-body
    /// schema (emitted as <c>x-rivet-input-type</c>). Mirrors the importer's
    /// <c>{fieldName}Request</c> convention but pins it explicitly, so the name survives
    /// operationId/tag hand-edits.
    /// </summary>
    private static string SynthesizedInputTypeName(TsEndpointDefinition ep) =>
        ep.InputTypeName ?? Naming.ToPascalCaseFromSegments(ep.Name) + "Request";

    private static Dictionary<string, object> BinarySchema() =>
        new() { ["type"] = "string", ["format"] = "binary" };

    /// <summary>
    /// Maps a request-body type to its schema; inline object bodies get
    /// <c>x-rivet-input-type</c> so the importer synthesizes the same record name
    /// every loop ($ref bodies carry their name in the reference itself).
    /// </summary>
    private static Dictionary<string, object> BuildBodySchema(
        TsType bodyType,
        TsEndpointDefinition ep,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions
    )
    {
        if (
            TryBuildRouteFilteredBodySchema(
                bodyType,
                ep,
                definitions,
                out _,
                out var filteredSchema
            )
        )
        {
            if (bodyType is not TsType.Nullable)
            {
                return filteredSchema;
            }

            return new Dictionary<string, object>
            {
                ["oneOf"] = new List<object>
                {
                    filteredSchema,
                    new Dictionary<string, object> { ["type"] = "null" },
                },
            };
        }

        var schema = MapTsTypeToJsonSchema(
            bodyType,
            $"request body on endpoint '{ep.ControllerName}.{ep.Name}'"
        );
        if (bodyType is TsType.InlineObject && !schema.ContainsKey("$ref"))
        {
            schema["x-rivet-input-type"] = SynthesizedInputTypeName(ep);
        }

        return schema;
    }

    private static bool TryBuildRouteFilteredBodySchema(
        TsType bodyType,
        TsEndpointDefinition ep,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        out string inputTypeName,
        out Dictionary<string, object> schema
    )
    {
        inputTypeName = null!;
        schema = null!;
        if (!TryResolveBodyProperties(bodyType, definitions, out _, out var sourceTypeName))
        {
            return false;
        }

        if (!TryGetRouteFilteredBodyProperties(bodyType, ep, definitions, out var bodyProperties))
        {
            return false;
        }

        var identity = FilteredBodyIdentity(ep, bodyProperties);
        if (_ctx is null || !_ctx.FilteredBodyNames.TryGetValue(identity, out var assignedName))
        {
            throw new OpenApiEmissionException(
                $"route-filtered request body name was not allocated for endpoint '{ep.ControllerName}.{ep.Name}'"
            );
        }

        inputTypeName = assignedName;
        schema = BuildObjectSchema(bodyProperties, typeName: sourceTypeName);
        return true;
    }

    private static string FilteredBodyIdentity(
        TsEndpointDefinition endpoint,
        IReadOnlyList<TsPropertyDefinition> properties
    )
    {
        var shape = new TsType.InlineObject(
            properties
                .Select(property => new TsType.InlineObjectField(
                    property.Name,
                    property.Type,
                    property.IsOptional
                ))
                .ToList()
        );
        return endpoint.ControllerName
            + "\0"
            + endpoint.Name
            + "\0"
            + InlineTypeExtractor.CanonicalHash(shape);
    }

    private static bool TryGetRouteFilteredBodyProperties(
        TsType? bodyType,
        TsEndpointDefinition ep,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        out IReadOnlyList<TsPropertyDefinition> bodyProperties
    )
    {
        bodyProperties = [];
        if (!TryResolveBodyProperties(bodyType, definitions, out var sourceProperties, out _))
        {
            return false;
        }

        var matchedBodyNames = ep
            .Params.Where(param => param.Source == ParamSource.Route)
            .Select(param => param.BodyPropertyName)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        if (matchedBodyNames.Count == 0)
        {
            return false;
        }

        bodyProperties = sourceProperties
            .Where(prop => !matchedBodyNames.Contains(prop.Name))
            .ToList();
        return bodyProperties.Count != sourceProperties.Count;
    }

    private static bool TryResolveBodyProperties(
        TsType? bodyType,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        out IReadOnlyList<TsPropertyDefinition> properties,
        out string typeName
    )
    {
        properties = [];
        typeName = null!;
        var unwrapped = bodyType is TsType.Nullable nullable ? nullable.Inner : bodyType;
        string definitionName;
        IReadOnlyList<TsType>? typeArguments = null;

        switch (unwrapped)
        {
            case TsType.TypeRef typeRef:
                definitionName = typeRef.Name;
                break;
            case TsType.Generic generic:
                definitionName = generic.Name;
                typeArguments = generic.TypeArguments;
                break;
            default:
                return false;
        }

        if (
            !definitions.TryGetValue(definitionName, out var definition)
            || definition.Type is not null
        )
        {
            return false;
        }

        typeName = definition.Name;
        if (typeArguments is null)
        {
            properties = definition.Properties;
            return true;
        }

        if (definition.TypeParameters.Count != typeArguments.Count)
        {
            return false;
        }

        var substitutions = definition
            .TypeParameters.Select((name, index) => (name, typeArguments[index]))
            .ToDictionary(pair => pair.name, pair => pair.Item2, StringComparer.Ordinal);
        properties = definition
            .Properties.Select(property =>
                property with
                {
                    Type = TsType.ResolveTypeParams(property.Type, substitutions),
                }
            )
            .ToList();
        return true;
    }

    private static Dictionary<string, object> BuildComponentExamples(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyList<OpenApiComponentExampleProvenance> authoredExamples,
        IReadOnlyList<OpenApiComponentRequestBodyProvenance> requestBodies
    )
    {
        var examples = new Dictionary<string, object>();

        foreach (var example in authoredExamples)
        {
            examples.Add(example.Name, BuildComponentExample(example));
        }

        foreach (var endpoint in endpoints)
        {
            AddComponentExamples(examples, endpoint.RequestExamples);

            foreach (var response in endpoint.Responses)
            {
                AddComponentExamples(examples, response.Examples);
            }
        }
        foreach (var requestBody in requestBodies)
        {
            AddComponentExamples(examples, requestBody.Examples);
        }

        return examples;
    }

    private static Dictionary<string, object> BuildComponentRequestBodies(
        IReadOnlyList<OpenApiComponentRequestBodyProvenance> requestBodies
    )
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var requestBody in requestBodies)
        {
            var content = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var entry in requestBody.Contents)
            {
                var media = new Dictionary<string, object>();
                if (entry.IsBinary)
                {
                    media["schema"] = BinarySchema();
                }
                else if (entry.SchemaJson is not null)
                {
                    media["schema"] = ParseSchemaObject(
                        entry.SchemaJson,
                        $"request-body component '{requestBody.Name}' content '{entry.MediaType}'"
                    );
                }
                else if (entry.Schema is not null)
                {
                    media["schema"] = BuildSchemaWithLeafProvenance(
                        entry.Schema,
                        entry.SchemaType,
                        entry.Format,
                        entry.IsFormatSpecified,
                        $"request-body component '{requestBody.Name}' content '{entry.MediaType}'"
                    );
                }
                content[entry.MediaType] = media;
            }

            var value = new Dictionary<string, object>
            {
                ["required"] = requestBody.Required,
                ["content"] = WithExamples(content, requestBody.Examples),
            };
            AddOptionalString(value, "description", requestBody.Description);
            result.Add(requestBody.Name, value);
        }
        return result;
    }

    private static void AddJsonComponents(
        Dictionary<string, object> components,
        string kind,
        IEnumerable<(string Name, string Json)> values
    )
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, json) in values)
        {
            result.Add(
                name,
                JsonSerializer.Deserialize<Dictionary<string, object>>(json)
                    ?? throw new OpenApiEmissionException(
                        $"Preserved component {kind} '{name}' is not a JSON object."
                    )
            );
        }
        if (result.Count > 0)
        {
            components[kind] = result;
        }
    }

    private static Dictionary<string, object> BuildComponentExample(
        OpenApiComponentExampleProvenance example
    )
    {
        var result = new Dictionary<string, object>();
        AddOptionalString(result, "summary", example.Summary);
        AddOptionalString(result, "description", example.Description);
        if (example.JsonValue is { } jsonValue)
        {
            result["value"] = ParseJson(jsonValue)!;
        }
        else
        {
            result["externalValue"] = example.ExternalValue!;
        }

        return result;
    }

    private static void AddComponentExamples(
        Dictionary<string, object> target,
        IReadOnlyList<TsEndpointExample>? examples
    )
    {
        if (examples is null)
        {
            return;
        }

        foreach (var example in examples)
        {
            foreach (
                var (componentId, json) in example.ReferencedComponents
                    ?? new Dictionary<string, string>()
            )
            {
                target.TryAdd(
                    componentId,
                    new Dictionary<string, object> { ["value"] = ParseJson(json)! }
                );
            }

            if (
                example.ComponentExampleId is null
                || example.ResolvedJson is null
                || target.ContainsKey(example.ComponentExampleId)
            )
            {
                continue;
            }

            // null is a legal example value (`value: null`) — see ParseJson.
            target[example.ComponentExampleId] = new Dictionary<string, object>
            {
                ["value"] = ParseJson(example.ResolvedJson)!,
            };
        }
    }

    private static Dictionary<string, object> WithExamples(
        Dictionary<string, object> content,
        IReadOnlyList<TsEndpointExample>? examples
    )
    {
        if (examples is null || examples.Count == 0)
        {
            return content;
        }

        var templateSchema = content
            .Values.OfType<Dictionary<string, object>>()
            .Select(entry => entry.TryGetValue("schema", out var schema) ? schema : null)
            .FirstOrDefault(schema => schema is not null);

        foreach (var group in examples.GroupBy(example => example.MediaType))
        {
            var createdMediaContent = false;
            if (!content.TryGetValue(group.Key, out var mediaContentObj))
            {
                var mediaContent = new Dictionary<string, object>();
                if (templateSchema is not null)
                {
                    mediaContent["schema"] = templateSchema;
                }

                content[group.Key] = mediaContent;
                mediaContentObj = mediaContent;
                createdMediaContent = true;
            }

            var mediaContentDict = (Dictionary<string, object>)mediaContentObj;
            var groupedExamples = group.ToList();

            if (
                groupedExamples.Count == 1
                && groupedExamples[0].Name is null
                && groupedExamples[0].Json is not null
                && groupedExamples[0].ComponentExampleId is null
            )
            {
                var inlineExampleJson = groupedExamples[0].Json;
                // null is a legal example value (`example: null`) — see ParseJson.
                mediaContentDict["example"] = ParseJson(inlineExampleJson!)!;
                continue;
            }

            var examplesDict = new Dictionary<string, object>();
            for (var index = 0; index < groupedExamples.Count; index++)
            {
                var example = groupedExamples[index];
                var key = example.Name ?? $"example{index + 1}";
                var renderedExample = ToOpenApiExample(example);
                if (renderedExample is not null)
                {
                    examplesDict[key] = renderedExample;
                }
            }

            if (examplesDict.Count == 0)
            {
                if (createdMediaContent && mediaContentDict.Count == 0)
                {
                    content.Remove(group.Key);
                }

                continue;
            }

            mediaContentDict["examples"] = examplesDict;
        }

        return content;
    }

    private static object? ToOpenApiExample(TsEndpointExample example)
    {
        if (example.ComponentExampleId is not null && example.ResolvedJson is not null)
        {
            return new Dictionary<string, object>
            {
                ["$ref"] = $"#/components/examples/{example.ComponentExampleId}",
            };
        }

        var json = example.Json ?? example.ResolvedJson;
        if (json is null)
        {
            return null;
        }

        // null is a legal example value (`value: null`) — see ParseJson.
        return new Dictionary<string, object> { ["value"] = ParseJson(json)! };
    }

    // Returns null only for the JSON literal `null` — a legal example value
    // (the importer converts Microsoft.OpenApi's null sentinel back to it);
    // malformed JSON throws JsonException. Callers store the null in an
    // object-valued dictionary slot, which System.Text.Json serializes as null.
    private static object? ParseJson(string json) => JsonSerializer.Deserialize<object>(json);

    public static Dictionary<string, object> MapTsTypeToJsonSchema(
        TsType type,
        string? context = null
    )
    {
        return type switch
        {
            TsType.Primitive p => MapPrimitive(p, context),

            TsType.Nullable n => MapNullable(n, context),

            TsType.Array a => BuildArraySchema(a, context),

            TsType.Dictionary d => BuildDictionarySchema(d, context),

            TsType.StringUnion su => MapStringUnion(su),

            TsType.IntUnion iu => MapIntUnion(iu),

            TsType.Literal literal => new Dictionary<string, object>
            {
                ["const"] = JsonElementValue(literal.Value),
            },

            TsType.TypeRef r => MapTypeReference(r, context),

            TsType.Generic g => new Dictionary<string, object>
            {
                ["$ref"] = $"#/components/schemas/{MonomorphisedName(g)}",
            },

            TsType.Brand b => MapBrandReference(b, context),

            TsType.TypeParam tp => FallbackTypeParam(tp, context),

            TsType.InlineObject obj => BuildInlineObjectSchema(obj, context),

            TsType.TaggedUnion tu => BuildTaggedUnionSchema(tu, context),

            // Undiscriminated union ([RivetUnion] wrapper): a plain oneOf — no
            // discriminator, variants may be inline primitive schemas.
            TsType.Union u => new Dictionary<string, object>
            {
                ["oneOf"] = u
                    .Variants.Select(object (variant) => MapTsTypeToJsonSchema(variant, context))
                    .ToList(),
            },

            _ => new Dictionary<string, object> { ["type"] = "object" },
        };
    }

    private static Dictionary<string, object> MapTypeReference(
        TsType.TypeRef reference,
        string? context
    )
    {
        if (_ctx?.Definitions.TryGetValue(reference.Name, out var definition) == true)
        {
            if (definition.Metadata?.Provenance == TsTypeProvenance.Synthetic)
            {
                if (_ctx is null || !_ctx.InliningSyntheticTypes.Add(reference.Name))
                {
                    throw new OpenApiEmissionException(
                        $"synthetic type '{reference.Name}' is recursive and cannot be inlined without recursive schema algebra"
                    );
                }

                try
                {
                    return BuildDefinitionSchema(definition);
                }
                finally
                {
                    _ctx.InliningSyntheticTypes.Remove(reference.Name);
                }
            }

            return ComponentReference(definition.Metadata?.ComponentId ?? reference.Name);
        }

        if (_ctx?.Enums.TryGetValue(reference.Name, out var enumType) == true)
        {
            var metadata = GetMetadata(enumType);
            if (metadata?.Provenance == TsTypeProvenance.Synthetic)
            {
                return MapTsTypeToJsonSchema(enumType, context);
            }

            return ComponentReference(metadata?.ComponentId ?? reference.Name);
        }

        if (_ctx?.Brands.TryGetValue(reference.Name, out var brand) == true)
        {
            return MapBrandReference(brand, context);
        }

        return ComponentReference(reference.Name);
    }

    private static Dictionary<string, object> MapBrandReference(TsType.Brand brand, string? context)
    {
        if (brand.Metadata?.Provenance == TsTypeProvenance.Synthetic)
        {
            return MapTsTypeToJsonSchema(brand.Inner, context);
        }

        return ComponentReference(brand.Metadata?.ComponentId ?? brand.Name);
    }

    private static TsTypeMetadata? GetMetadata(TsType type) =>
        type switch
        {
            TsType.StringUnion stringUnion => stringUnion.Metadata,
            TsType.IntUnion intUnion => intUnion.Metadata,
            TsType.Brand brand => brand.Metadata,
            _ => null,
        };

    private static Dictionary<string, object> ComponentReference(string componentId) =>
        new() { ["$ref"] = $"#/components/schemas/{EscapeJsonPointerToken(componentId)}" };

    private static Dictionary<string, object> ComponentReference(string kind, string componentId) =>
        new() { ["$ref"] = $"#/components/{kind}/{EscapeJsonPointerToken(componentId)}" };

    private static string EscapeJsonPointerToken(string value) =>
        value
            .Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static Dictionary<string, object> MapIntUnion(TsType.IntUnion union)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] =
                union.ScalarMetadata?.IsNullable == true
                    ? new List<string> { "integer", "null" }
                    : "integer",
            ["enum"] = union.Members.ToList(),
        };
        if (union.Format is not null)
        {
            schema["format"] = union.Format;
        }
        if (union.Description is not null)
        {
            schema["description"] = union.Description;
        }
        EnrichScalarSchema(schema, union.ScalarMetadata);

        return schema;
    }

    private static Dictionary<string, object> MapStringUnion(TsType.StringUnion union)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] =
                union.ScalarMetadata?.IsNullable == true
                    ? new List<string> { "string", "null" }
                    : "string",
            ["enum"] = union.Members.ToList(),
        };
        if (union.Format is not null)
        {
            schema["format"] = union.Format;
        }
        if (union.Description is not null)
        {
            schema["description"] = union.Description;
        }
        EnrichScalarSchema(schema, union.ScalarMetadata);
        return schema;
    }

    private static object JsonElementValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException(
                $"Unsupported scalar literal kind '{value.ValueKind}'."
            ),
        };

    private static Dictionary<string, object> BuildInlineObjectSchema(
        TsType.InlineObject obj,
        string? context = null
    )
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var field in obj.Fields)
        {
            properties[field.Name] = MapTsTypeToJsonSchema(field.Type, context);
            if (!field.Optional)
            {
                required.Add(field.Name);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static Dictionary<string, object> BuildTaggedUnionSchema(
        TsType.TaggedUnion tu,
        string? context = null
    )
    {
        // OpenAPI `discriminator` is only meaningful on a oneOf of $ref'd named schemas with
        // a tag→$ref mapping — consumers reject or ignore a discriminator over inline schemas
        // (E11). Each inline variant becomes a named component schema referenced via $ref.
        var baseName =
            _ctx?.TaggedUnionNames.GetValueOrDefault(InlineTypeExtractor.CanonicalHash(tu))
            ?? TsType.GetNameSuffix(tu);

        var oneOf = new List<object>();
        var mapping = new Dictionary<string, object>();

        foreach (var variant in tu.Variants)
        {
            var variantSchema = MapTsTypeToJsonSchema(variant.Type, context);

            string refPath;
            if (variantSchema.Count == 1 && variantSchema.TryGetValue("$ref", out var existingRef))
            {
                // Variant is already a named schema (TypeRef/Generic/Brand) — ref it directly.
                refPath = (string)existingRef;
            }
            else
            {
                var componentName =
                    variant.Metadata?.ComponentId ?? $"{baseName}_{UpperFirst(variant.Tag)}";
                _ctx?.ExtraComponents.TryAdd(componentName, variantSchema);
                refPath = $"#/components/schemas/{EscapeJsonPointerToken(componentName)}";
            }

            oneOf.Add(new Dictionary<string, object> { ["$ref"] = refPath });
            mapping[variant.Tag] = refPath;
        }

        return new Dictionary<string, object>
        {
            ["oneOf"] = oneOf,
            ["discriminator"] = new Dictionary<string, object>
            {
                ["propertyName"] = tu.Discriminator,
                ["mapping"] = mapping,
            },
        };
    }

    private static Dictionary<string, object> MapPrimitive(
        TsType.Primitive p,
        string? context = null
    )
    {
        if (p.Name == "File")
        {
            return new Dictionary<string, object>
            {
                ["x-rivet-file"] = true,
                ["type"] = "string",
                ["format"] = "binary",
            };
        }

        if (p.Name == "unknown")
        {
            if (p.CSharpType is null)
            {
                // The catch-all used to name no symbol at all (FABLE_GAPS §7 item 12) —
                // context threads the offending type/property or endpoint site through.
                Diagnostics.Warn(
                    Diagnostics.UnknownTypeUntypedSchema,
                    $"'unknown' type (JsonElement/JsonNode or an unmapped C# type) in OpenAPI schema{AtContext(context)} — emitting as untyped"
                );
            }

            var unknownSchema = new Dictionary<string, object>();
            // JsonNode gets x-rivet-csharp-type on the primitive itself.
            // JsonObject/JsonArray are handled by BuildDictionarySchema/BuildArraySchema on the parent.
            if (p.CSharpType is "JsonNode")
            {
                unknownSchema["x-rivet-csharp-type"] = p.CSharpType;
            }
            return unknownSchema;
        }

        // byte[] (FABLE_GAPS spec/wire divergence): System.Text.Json serializes byte[]
        // as a base64 string on the wire, so the schema is type: string with
        // contentEncoding: base64 — the OpenAPI 3.1 idiom (`format: byte` is the
        // deprecated 3.0 spelling). x-rivet-csharp-type carries the exact C# type
        // for lossless import round-trips.
        if (p is { Name: "string", Format: "base64" })
        {
            var base64Schema = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["contentEncoding"] = "base64",
            };
            if (p.CSharpType is not null)
            {
                base64Schema["x-rivet-csharp-type"] = p.CSharpType;
            }
            return base64Schema;
        }

        // char (P2 wave 6): System.Text.Json serializes char as a single-character
        // JSON string on the wire, so the schema is a string with both length bounds
        // pinned to 1. x-rivet-csharp-type carries the exact C# type for lossless
        // import round-trips (a plain length-1 string stays a C# string).
        if (p is { Name: "string", CSharpType: "char" })
        {
            return new Dictionary<string, object>
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = 1,
                ["x-rivet-csharp-type"] = "char",
            };
        }

        // CLR integer primitives are represented internally as number + format.
        // An imported schema can legitimately use an integer-looking format on a string.
        var type =
            p.Name == "number"
            && p.Format
                is "int32"
                    or "int64"
                    or "int16"
                    or "uint16"
                    or "int8"
                    or "uint8"
                    or "uint32"
                    or "uint64"
                ? "integer"
                : p.Name;

        var schema = new Dictionary<string, object> { ["type"] = type };

        if (p.Format is not null)
        {
            schema["format"] = p.Format;
        }

        if (p.CSharpType is not null)
        {
            schema["x-rivet-csharp-type"] = p.CSharpType;
        }

        return schema;
    }

    private static Dictionary<string, object> MapNullable(TsType.Nullable n, string? context = null)
    {
        var inner = MapTsTypeToJsonSchema(n.Inner, context);

        // OpenAPI 3.1 / JSON Schema 2020-12: null is a type. Schemas with a single
        // type become a type array; everything else gets an explicit null branch.
        if (inner.TryGetValue("type", out var typeValue) && typeValue is string typeName)
        {
            inner["type"] = new List<string> { typeName, "null" };
            return inner;
        }

        // $ref: a sibling `type: "null"` would be ANDed with the referenced schema in
        // 2020-12, so the null alternative must be a oneOf branch instead.
        if (inner.ContainsKey("$ref"))
        {
            return new Dictionary<string, object>
            {
                ["oneOf"] = new List<object>
                {
                    inner,
                    new Dictionary<string, object> { ["type"] = "null" },
                },
            };
        }

        // Untyped schema (unknown/JsonElement) already admits null.
        if (inner.Count == 0)
        {
            return inner;
        }

        // Remaining typeless composites (tagged-union oneOf, x-rivet-csharp-type
        // untyped schemas): anyOf, because an untyped branch also matches null and
        // would make a oneOf ambiguous.
        return new Dictionary<string, object>
        {
            ["anyOf"] = new List<object>
            {
                inner,
                new Dictionary<string, object> { ["type"] = "null" },
            },
        };
    }

    private static Dictionary<string, object> FallbackTypeParam(
        TsType.TypeParam tp,
        string? context = null
    )
    {
        Diagnostics.Warn(
            Diagnostics.UnresolvedTypeParameter,
            $"unresolved type parameter '{tp.Name}' in OpenAPI schema{AtContext(context)} — emitting as object"
        );
        return new Dictionary<string, object> { ["type"] = "object" };
    }

    private static string AtContext(string? context) => context is null ? "" : $" at {context}";

    private static string MonomorphisedName(TsType.Generic g)
    {
        // The pure suffix scheme is lossy in places (4+-member unions → "Enum", 4+-field
        // inline objects → "Object"), so distinct instantiations can share a pure name.
        // The per-emit registry assigns each distinct shape a distinct deterministic name.
        if (
            _ctx is not null
            && _ctx.GenericNames.TryGetValue(InlineTypeExtractor.CanonicalHash(g), out var assigned)
        )
        {
            return assigned;
        }

        return TsType.MonomorphisedName(g);
    }

    /// <summary>
    /// Pre-pass: walks every type reachable from endpoints and definitions and assigns each
    /// distinct generic instantiation / tagged union a unique component (base) name.
    /// Identical shapes share a name; distinct shapes whose pure names collide get a
    /// deterministic numeric suffix (discovery order).
    /// </summary>
    private static void AssignComponentNames(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        EmitContext ctx
    )
    {
        var generics = new List<TsType.Generic>();
        var taggedUnions = new List<TsType.TaggedUnion>();

        void Walk(TsType? type)
        {
            switch (type)
            {
                case TsType.Generic g:
                    generics.Add(g);
                    foreach (var arg in g.TypeArguments)
                    {
                        Walk(arg);
                    }

                    break;
                case TsType.TaggedUnion tu:
                    taggedUnions.Add(tu);
                    foreach (var v in tu.Variants)
                    {
                        Walk(v.Type);
                    }

                    break;
                case TsType.Array a:
                    Walk(a.Element);
                    break;
                case TsType.Nullable n:
                    Walk(n.Inner);
                    break;
                case TsType.Dictionary d:
                    Walk(d.Value);
                    if (d.Key is not null)
                    {
                        Walk(d.Key);
                    }

                    break;
                case TsType.Brand b:
                    Walk(b.Inner);
                    break;
                case TsType.InlineObject obj:
                    foreach (var field in obj.Fields)
                    {
                        Walk(field.Type);
                    }

                    break;
            }
        }

        foreach (var ep in endpoints)
        {
            foreach (var p in ep.Params)
            {
                Walk(p.Type);
            }

            Walk(ep.ReturnType);
            foreach (var r in ep.Responses)
            {
                Walk(r.DataType);
            }

            Walk(ep.RequestType);
        }

        foreach (var (_, def) in definitions)
        {
            if (def.Type is not null)
            {
                Walk(def.Type);
                continue;
            }

            foreach (var prop in def.Properties)
            {
                Walk(prop.Type);
            }
        }

        foreach (var (_, brand) in brands)
        {
            Walk(brand.Inner);
        }

        foreach (var (_, enumType) in enums)
        {
            Walk(enumType);
        }

        // Names already claimed by emitted definition/brand/enum schemas
        var usedNames = new HashSet<string>(
            definitions.Where(kv => kv.Value.TypeParameters.Count == 0).Select(kv => kv.Key)
        );
        usedNames.UnionWith(brands.Keys);
        usedNames.UnionWith(enums.Keys);

        foreach (var g in generics)
        {
            var hash = InlineTypeExtractor.CanonicalHash(g);
            if (ctx.GenericNames.ContainsKey(hash))
            {
                continue;
            }

            ctx.GenericNames[hash] = ClaimName(TsType.MonomorphisedName(g), usedNames);
        }

        // Tagged unions that ARE a named type alias keep the alias as base name
        foreach (var (name, def) in definitions)
        {
            if (def.Type is TsType.TaggedUnion aliased)
            {
                ctx.TaggedUnionNames.TryAdd(
                    InlineTypeExtractor.CanonicalHash(aliased),
                    def.Metadata?.ComponentId ?? name
                );
            }
        }

        foreach (var tu in taggedUnions)
        {
            var hash = InlineTypeExtractor.CanonicalHash(tu);
            if (ctx.TaggedUnionNames.ContainsKey(hash))
            {
                continue;
            }

            ctx.TaggedUnionNames[hash] = ClaimName(TsType.GetNameSuffix(tu), usedNames);
        }

        foreach (var endpoint in endpoints)
        {
            var bodyType =
                endpoint.Params.FirstOrDefault(param => param.Source == ParamSource.Body)?.Type
                ?? endpoint.RequestType;
            if (
                !TryGetRouteFilteredBodyProperties(
                    bodyType,
                    endpoint,
                    definitions,
                    out var properties
                )
            )
            {
                continue;
            }

            var identity = FilteredBodyIdentity(endpoint, properties);
            if (ctx.FilteredBodyNames.ContainsKey(identity))
            {
                continue;
            }

            var endpointName = Naming.ToPascalCaseFromSegments(endpoint.Name) + "Request";
            var controllerName =
                Naming.ToPascalCaseFromSegments(endpoint.ControllerName) + endpointName;
            var matchingDefinitionName = definitions
                .Where(pair => pair.Value.TypeParameters.Count == 0 && pair.Value.Type is null)
                .Where(pair =>
                    SchemasEqual(BuildDefinitionSchema(pair.Value), BuildObjectSchema(properties))
                )
                .OrderByDescending(pair => pair.Key == endpointName)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (matchingDefinitionName is not null)
            {
                ctx.FilteredBodyNames[identity] = matchingDefinitionName;
            }
            else
            {
                ctx.FilteredBodyNames[identity] = usedNames.Add(endpointName)
                    ? endpointName
                    : ClaimName(controllerName, usedNames);
            }
        }
    }

    private static bool SchemasEqual(
        Dictionary<string, object> left,
        Dictionary<string, object> right
    ) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static string ClaimName(string pureName, HashSet<string> usedNames)
    {
        var name = pureName;
        var i = 2;
        while (!usedNames.Add(name))
        {
            name = pureName + i;
            i++;
        }

        return name;
    }

    private static Dictionary<string, object> BuildSchemas(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums
    )
    {
        var schemas = new Dictionary<string, object>();

        foreach (var (name, def) in definitions)
        {
            if (def.TypeParameters.Count > 0)
            {
                // Generic definitions are emitted as monomorphised variants — skip the template
                continue;
            }

            if (def.Metadata?.Provenance != TsTypeProvenance.Synthetic)
            {
                schemas[def.Metadata?.ComponentId ?? name] = BuildDefinitionSchema(def);
            }
        }

        // Monomorphised generics: find all Generic type refs used across definitions and endpoints
        var genericInstances = new Dictionary<string, TsType.Generic>();
        CollectGenericInstances(endpoints, definitions, genericInstances);

        // E6: templates are skipped during collection (their unresolved Generic refs are
        // garbage like PagedResult_T), so nested instantiations only surface when a
        // template's properties are resolved against concrete type args. Iterate to a
        // fixpoint so e.g. Wrapper<X> { PagedResult<X> } registers PagedResult_X too.
        var pending = new Queue<TsType.Generic>(genericInstances.Values);
        while (pending.Count > 0)
        {
            var instance = pending.Dequeue();
            if (!definitions.TryGetValue(instance.Name, out var template))
            {
                continue;
            }

            var instanceMap = new Dictionary<string, TsType>();
            for (
                var i = 0;
                i < Math.Min(template.TypeParameters.Count, instance.TypeArguments.Count);
                i++
            )
            {
                instanceMap[template.TypeParameters[i]] = instance.TypeArguments[i];
            }

            var discovered = new Dictionary<string, TsType.Generic>();
            if (template.Type is not null)
            {
                CollectGenericsFromType(ResolveTypeParams(template.Type, instanceMap), discovered);
            }
            else
            {
                foreach (var prop in template.Properties)
                {
                    CollectGenericsFromType(ResolveTypeParams(prop.Type, instanceMap), discovered);
                }
            }

            foreach (var (discoveredName, discoveredInstance) in discovered)
            {
                if (genericInstances.TryAdd(discoveredName, discoveredInstance))
                {
                    pending.Enqueue(discoveredInstance);
                }
            }
        }

        foreach (var (monoName, generic) in genericInstances)
        {
            if (!definitions.TryGetValue(generic.Name, out var genericDef))
            {
                // E6: a generic instantiation whose template is absent from definitions used
                // to emit a $ref with no matching component — a dangling reference every
                // consumer rejects (GAP-1). Never emit a dangling $ref: warn loudly and
                // synthesize a valid free-form fallback component under the $ref'd name.
                Diagnostics.Warn(
                    Diagnostics.GenericTemplateMissing,
                    $"generic template '{generic.Name}' (instantiated as '{monoName}') is not present in the contract's type definitions — emitting a free-form object schema; fix the upstream producer to include the template definition"
                );

                schemas[monoName] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["description"] =
                        $"Unresolved generic instantiation of '{generic.Name}' — template definition missing from source contract",
                };
                continue;
            }

            // Build a type parameter → concrete type mapping
            var typeParamMap = new Dictionary<string, TsType>();
            for (
                var i = 0;
                i < Math.Min(genericDef.TypeParameters.Count, generic.TypeArguments.Count);
                i++
            )
            {
                typeParamMap[genericDef.TypeParameters[i]] = generic.TypeArguments[i];
            }

            var monoSchema = BuildMonomorphisedSchema(genericDef, typeParamMap);
            monoSchema["x-rivet-generic"] = new Dictionary<string, object>
            {
                ["name"] = generic.Name,
                ["typeParams"] = genericDef.TypeParameters.ToList(),
                ["args"] = typeParamMap.ToDictionary(
                    kv => kv.Key,
                    kv => (object)GetCSharpTypeName(kv.Value)
                ),
            };
            schemas[monoName] = monoSchema;
        }

        // Brands as schemas with x-rivet-brand extension
        foreach (var (name, brand) in brands)
        {
            if (brand.Metadata?.Provenance == TsTypeProvenance.Synthetic)
            {
                continue;
            }
            var brandSchema = MapTsTypeToJsonSchema(brand.Inner, $"brand '{name}'");
            brandSchema["x-rivet-brand"] = name;
            if (brand.Description is not null)
            {
                brandSchema["description"] = brand.Description;
            }
            schemas[brand.Metadata?.ComponentId ?? name] = brandSchema;
        }

        // Enums as schemas
        foreach (var (name, enumType) in enums)
        {
            var metadata = GetMetadata(enumType);
            if (metadata?.Provenance == TsTypeProvenance.Synthetic)
            {
                continue;
            }
            var enumSchema = MapTsTypeToJsonSchema(enumType, $"enum '{name}'");
            if (definitions.TryGetValue(name, out var scalarDefinition))
            {
                if (scalarDefinition.Type is TsType.Nullable)
                {
                    enumSchema["type"] = new List<string>
                    {
                        enumType is TsType.IntUnion ? "integer" : "string",
                        "null",
                    };
                }
                EnrichScalarSchema(enumSchema, scalarDefinition.ScalarMetadata);
            }
            schemas[metadata?.ComponentId ?? name] = enumSchema;
        }

        return schemas;
    }

    private static Dictionary<string, object> BuildDefinitionSchema(TsTypeDefinition def)
    {
        if (def.Type is not null)
        {
            var schema = MapTsTypeToJsonSchema(def.Type, $"type '{def.Name}'");
            if (def.Description is not null)
            {
                schema["description"] = def.Description;
            }

            EnrichScalarSchema(schema, def.ScalarMetadata);

            return schema;
        }

        return BuildObjectSchema(def.Properties, def.Description, def.Name, def.ScalarMetadata);
    }

    private static void EnrichScalarSchema(
        Dictionary<string, object> schema,
        TsScalarMetadata? metadata
    )
    {
        if (metadata is null)
        {
            return;
        }

        // Nullability changes schema algebra, so apply it before adding annotation
        // siblings. Wrapping annotations inside anyOf/oneOf makes a second import read
        // them from the branch rather than the property schema and loses them.
        if (metadata.IsNullable)
        {
            ApplyNullableMetadata(schema);
        }

        if (metadata.Description is not null)
        {
            schema["description"] = metadata.Description;
        }
        if (metadata.Title is not null)
        {
            schema["title"] = metadata.Title;
        }
        if (metadata.IsFormatSpecified)
        {
            if (metadata.Format is null)
            {
                schema.Remove("format");
            }
            else
            {
                schema["format"] = metadata.Format;
            }
        }
        if (metadata.Required is { Count: > 0 })
        {
            schema["required"] = metadata.Required;
        }

        if (metadata.DefaultValue is not null)
        {
            schema["default"] = JsonSerializer.Deserialize<JsonElement>(metadata.DefaultValue);
        }
        if (metadata.Example is not null)
        {
            schema["example"] = JsonSerializer.Deserialize<JsonElement>(metadata.Example);
        }
        if (metadata.Examples is not null)
        {
            schema["examples"] = JsonSerializer.Deserialize<JsonElement>(metadata.Examples);
        }
        if (metadata.IsDeprecated)
        {
            schema["deprecated"] = true;
        }
        if (metadata.IsReadOnly)
        {
            schema["readOnly"] = true;
        }
        if (metadata.IsWriteOnly)
        {
            schema["writeOnly"] = true;
        }
        SchemaEnricher.EnrichConstraints(schema, metadata.Constraints);
        if (metadata.Xml is { } xml)
        {
            var value = new Dictionary<string, object>();
            AddOptionalString(value, "name", xml.Name);
            AddOptionalString(value, "namespace", xml.Namespace);
            AddOptionalString(value, "prefix", xml.Prefix);
            if (xml.IsAttribute)
            {
                value["attribute"] = true;
            }
            if (xml.IsWrapped)
            {
                value["wrapped"] = true;
            }
            schema["xml"] = value;
        }
    }

    private static void ApplyNullableMetadata(Dictionary<string, object> schema)
    {
        if (schema.TryGetValue("type", out var value) && value is string type)
        {
            schema["type"] = new List<string> { type, "null" };
            return;
        }
        if (value is IEnumerable<string> types && types.Contains("null", StringComparer.Ordinal))
        {
            return;
        }

        var inner = new Dictionary<string, object>(schema);
        schema.Clear();
        schema[inner.ContainsKey("$ref") ? "oneOf" : "anyOf"] = new List<object>
        {
            inner,
            new Dictionary<string, object> { ["type"] = "null" },
        };
    }

    private static Dictionary<string, object> BuildObjectSchema(
        IReadOnlyList<TsPropertyDefinition> propertiesDefinition,
        string? description = null,
        string? typeName = null,
        TsScalarMetadata? metadata = null
    )
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in propertiesDefinition)
        {
            var propSchema = MapTsTypeToJsonSchema(
                prop.Type,
                typeName is null ? $"property '{prop.Name}'" : $"property '{typeName}.{prop.Name}'"
            );
            SchemaEnricher.EnrichPropertySchema(propSchema, prop);
            EnrichScalarSchema(propSchema, prop.ScalarMetadata);
            properties[prop.Name] = propSchema;

            if (!prop.IsOptional)
            {
                required.Add(prop.Name);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (description is not null)
        {
            schema["description"] = description;
        }

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        if (properties.Count == 0)
        {
            schema["x-rivet-empty-record"] = true;
        }

        EnrichScalarSchema(schema, metadata);

        return schema;
    }

    private static Dictionary<string, object> BuildArraySchema(
        TsType.Array a,
        string? context = null
    )
    {
        var items = MapTsTypeToJsonSchema(a.Element, context);
        EnrichScalarSchema(items, a.ElementMetadata);
        var schema = new Dictionary<string, object> { ["type"] = "array", ["items"] = items };

        // JsonArray is represented internally as an array of unknown values.
        // Other element-side CLR tags describe the item, not its parent.
        if (a.Element is TsType.Primitive { Name: "unknown", CSharpType: "JsonArray" } p)
        {
            schema["x-rivet-csharp-type"] = p.CSharpType;
        }

        return schema;
    }

    private static Dictionary<string, object> BuildDictionarySchema(
        TsType.Dictionary d,
        string? context = null
    )
    {
        var value = MapTsTypeToJsonSchema(d.Value, context);
        EnrichScalarSchema(value, d.ValueMetadata);
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = value,
        };

        // Non-string key types constrain the keys via propertyNames (OpenAPI 3.1 /
        // JSON Schema 2020-12): enum/brand keys $ref their component schema; primitive
        // keys stay string-typed with the original format, x-rivet-csharp-type pinning
        // the exact C# key type for import round-trips.
        if (d.Key is not null)
        {
            schema["propertyNames"] = BuildDictionaryKeySchema(d.Key, context);
        }

        // JsonObject is represented internally as a dictionary of unknown values.
        // Other value-side CLR tags describe the dictionary element, not its parent.
        if (d.Value is TsType.Primitive { Name: "unknown", CSharpType: "JsonObject" } p)
        {
            schema["x-rivet-csharp-type"] = p.CSharpType;
        }

        return schema;
    }

    private static Dictionary<string, object> BuildDictionaryKeySchema(TsType key, string? context)
    {
        // Primitive keys are built inline rather than via MapPrimitive: property names
        // are always strings, but a numeric format (int32, …) would flip MapPrimitive's
        // emitted type to integer — invalid under propertyNames.
        if (key is TsType.Primitive p)
        {
            var schema = new Dictionary<string, object> { ["type"] = "string" };
            if (p.Format is not null)
            {
                schema["format"] = p.Format;
            }
            // char keys (P2 wave 6): single-character property names on the wire —
            // same length-1 shape as the char property schema.
            if (p.CSharpType is "char")
            {
                schema["minLength"] = 1;
                schema["maxLength"] = 1;
            }
            if (p.CSharpType is not null)
            {
                schema["x-rivet-csharp-type"] = p.CSharpType;
            }
            return schema;
        }

        // TypeRef (enum) / Brand → $ref to the named component schema
        return MapTsTypeToJsonSchema(key, context);
    }

    private static Dictionary<string, object> BuildMonomorphisedSchema(
        TsTypeDefinition genericDef,
        Dictionary<string, TsType> typeParamMap
    )
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        if (genericDef.Type is not null)
        {
            return MapTsTypeToJsonSchema(
                ResolveTypeParams(genericDef.Type, typeParamMap),
                $"type '{genericDef.Name}'"
            );
        }

        foreach (var prop in genericDef.Properties)
        {
            var resolvedType = ResolveTypeParams(prop.Type, typeParamMap);
            var propSchema = MapTsTypeToJsonSchema(
                resolvedType,
                $"property '{genericDef.Name}.{prop.Name}'"
            );
            SchemaEnricher.EnrichPropertySchema(propSchema, prop);
            properties[prop.Name] = propSchema;

            if (!prop.IsOptional)
            {
                required.Add(prop.Name);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static TsType ResolveTypeParams(TsType type, Dictionary<string, TsType> map) =>
        TsType.ResolveTypeParams(type, map);

    private static void CollectGenericInstances(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        Dictionary<string, TsType.Generic> genericInstances
    )
    {
        // Walk endpoint params and responses for Generic type usages
        foreach (var ep in endpoints)
        {
            foreach (var param in ep.Params)
            {
                CollectGenericsFromType(param.Type, genericInstances);
            }

            foreach (var resp in ep.Responses)
            {
                if (resp.DataType is not null)
                {
                    CollectGenericsFromType(resp.DataType, genericInstances);
                }
            }

            if (ep.RequestType is not null)
            {
                CollectGenericsFromType(ep.RequestType, genericInstances);
            }
        }

        // Walk all definitions' properties (all schemas are emitted, so all generics must be monomorphised)
        foreach (var (_, def) in definitions)
        {
            // E6: skip generic TEMPLATE definitions — their Generic refs still contain
            // unresolved TypeParams and used to register garbage Foo_T instances. Only
            // concrete instantiations monomorphise (nested ones via the fixpoint pass).
            if (def.TypeParameters.Count > 0)
            {
                continue;
            }

            if (def.Type is not null)
            {
                CollectGenericsFromType(def.Type, genericInstances);
                continue;
            }

            foreach (var prop in def.Properties)
            {
                CollectGenericsFromType(prop.Type, genericInstances);
            }
        }
    }

    private static void CollectGenericsFromType(
        TsType type,
        Dictionary<string, TsType.Generic> instances
    )
    {
        switch (type)
        {
            case TsType.Generic g:
                instances.TryAdd(MonomorphisedName(g), g);
                foreach (var arg in g.TypeArguments)
                {
                    CollectGenericsFromType(arg, instances);
                }
                break;
            case TsType.Array a:
                CollectGenericsFromType(a.Element, instances);
                break;
            case TsType.Nullable n:
                CollectGenericsFromType(n.Inner, instances);
                break;
            case TsType.Dictionary d:
                CollectGenericsFromType(d.Value, instances);
                if (d.Key is not null)
                {
                    CollectGenericsFromType(d.Key, instances);
                }
                break;
            case TsType.InlineObject obj:
                foreach (var field in obj.Fields)
                {
                    CollectGenericsFromType(field.Type, instances);
                }
                break;
            case TsType.TaggedUnion tu:
                foreach (var variant in tu.Variants)
                {
                    CollectGenericsFromType(variant.Type, instances);
                }
                break;
            case TsType.Brand b:
                CollectGenericsFromType(b.Inner, instances);
                break;
        }
    }

    private static string DefaultStatusDescription(int statusCode)
    {
        return statusCode switch
        {
            200 => "Success",
            201 => "Created",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            422 => "Unprocessable Entity",
            500 => "Internal Server Error",
            _ => $"Status {statusCode}",
        };
    }

    /// <summary>
    /// Converts a TsType to a C# type name string for x-rivet-generic args.
    /// </summary>
    private static string GetCSharpTypeName(TsType type)
    {
        return type switch
        {
            TsType.Primitive p => p.CSharpType
                ?? (
                    p.Format switch
                    {
                        "int32" => "int",
                        "int64" => "long",
                        "float" => "float",
                        "double" => "double",
                        "decimal" => "decimal",
                        "uuid" => "Guid",
                        "date-time" => "DateTime",
                        "date" => "DateOnly",
                        "time" => "TimeOnly",
                        "uri" => "Uri",
                        _ => p.Name switch
                        {
                            "string" => "string",
                            "number" => "int",
                            "boolean" => "bool",
                            _ => p.Name,
                        },
                    }
                ),
            TsType.TypeRef r => r.Name,
            TsType.Array a => $"List<{GetCSharpTypeName(a.Element)}>",
            TsType.Nullable n => $"{GetCSharpTypeName(n.Inner)}?",
            TsType.Dictionary d => $"Dictionary<string, {GetCSharpTypeName(d.Value)}>",
            TsType.Generic g =>
                $"{g.Name}<{string.Join(", ", g.TypeArguments.Select(GetCSharpTypeName))}>",
            TsType.Brand b => b.Name,
            TsType.TaggedUnion => "object",
            _ => "object",
        };
    }

    private static string UpperFirst(string s) => Naming.ToPascalCase(s);
}
