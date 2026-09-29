using System.Text.Json;
using System.Text.Json.Nodes;
using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Emits an OpenAPI 3.1 JSON spec from the Rivet model. One instance per emit call holds
/// the model and the naming state: component names must be unique per shape, and the pure
/// name suffixes are lossy in places ("Enum", "Object"), so a pre-pass assigns every
/// distinct shape a deterministic name that every $ref emission site then consults.
/// </summary>
public sealed class OpenApiEmitter
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly IReadOnlyDictionary<string, TsTypeDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, TsType.Brand> _brands;
    private readonly IReadOnlyDictionary<string, TsType> _enums;
    private readonly HashSet<string> _inliningSyntheticTypes = new(StringComparer.Ordinal);

    /// <summary>Canonical shape hash → assigned component name for monomorphised generics.</summary>
    private readonly Dictionary<string, string> _genericNames = [];

    /// <summary>Canonical shape hash → base component name for tagged unions.</summary>
    private readonly Dictionary<string, string> _taggedUnionNames = [];

    /// <summary>Controller/endpoint/shape identity → assigned route-filtered body component name.</summary>
    private readonly Dictionary<string, string> _filteredBodyNames = [];

    /// <summary>Variant component schemas synthesized for tagged unions.</summary>
    private readonly Dictionary<string, JsonObject> _extraComponents = [];

    private OpenApiEmitter(
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums
    )
    {
        _definitions = definitions;
        _brands = brands;
        _enums = enums;
    }

    public static string Emit(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        ContractSecurityMetadata? security,
        OpenApiDocumentInfo? documentInfo = null
    )
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
        var emitter = new OpenApiEmitter(definitions, brands, enums);
        emitter.AssignComponentNames(normalizedEndpoints);
        return emitter.EmitCore(
            normalizedEndpoints,
            security,
            documentInfo ?? new OpenApiDocumentInfo()
        );
    }

    private string EmitCore(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        ContractSecurityMetadata? security,
        OpenApiDocumentInfo documentInfo
    )
    {
        var requestBodyComponents = documentInfo.Provenance?.ComponentRequestBodies ?? [];
        var parameterComponents = documentInfo.Provenance?.ComponentParameters ?? [];
        var responseComponents = documentInfo.Provenance?.ComponentResponses ?? [];
        var paths = BuildPaths(
            endpoints,
            requestBodyComponents
                .Select(component => component.Name)
                .ToHashSet(StringComparer.Ordinal),
            parameterComponents
                .Select(component => component.Name)
                .ToHashSet(StringComparer.Ordinal),
            responseComponents.Select(component => component.Name).ToHashSet(StringComparer.Ordinal)
        );
        var schemas = BuildSchemas(endpoints);
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
        foreach (var (name, schema) in _extraComponents)
        {
            if (!schemas.TryAdd(name, schema))
            {
                Diagnostics.Warn(
                    Diagnostics.TaggedUnionComponentCollision,
                    $"tagged-union variant component '{name}' collides with an existing schema — existing schema wins"
                );
            }
        }

        var info = new JsonObject
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
            var contactValue = new JsonObject();
            AddOptionalString(contactValue, "name", contact.Name);
            AddOptionalString(contactValue, "url", contact.Url);
            AddOptionalString(contactValue, "email", contact.Email);
            info["contact"] = contactValue;
        }
        if (documentInfo.Provenance?.Info.License is { } license)
        {
            var licenseValue = new JsonObject { ["name"] = license.Name };
            AddOptionalString(licenseValue, "url", license.Url);
            AddOptionalString(licenseValue, "identifier", license.Identifier);
            info["license"] = licenseValue;
        }

        var doc = new JsonObject { ["openapi"] = "3.1.0", ["info"] = info };

        if (documentInfo.Servers is { Count: > 0 })
        {
            doc["servers"] = ArrayOf(
                documentInfo.Servers.Select(url => new JsonObject { ["url"] = url })
            );
        }
        else if (documentInfo.Provenance?.Servers is { Count: > 0 } provenanceServers)
        {
            doc["servers"] = ArrayOf(provenanceServers.Select(BuildServer));
        }

        // W4: operations carry tags — declare them in the global tags array
        // (operation-tag-defined; docs-UI consumers use it for grouping/ordering).
        if (documentInfo.Provenance is { } documentProvenance)
        {
            if (documentProvenance.Tags.Count > 0)
            {
                doc["tags"] = ArrayOf(documentProvenance.Tags.Select(BuildTag));
            }
            if (documentProvenance.ExternalDocs is { } externalDocs)
            {
                doc["externalDocs"] = BuildExternalDocs(externalDocs);
            }
        }
        else
        {
            var tags = endpoints
                .Select(ep => Naming.ToPascalCase(ep.ControllerName))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .ToList();
            if (tags.Count > 0)
            {
                doc["tags"] = ArrayOf(tags.Select(tag => new JsonObject { ["name"] = tag }));
            }
        }

        doc["paths"] = paths;

        var components = new JsonObject();

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

        var securitySchemes = new JsonObject();

        if (security is not null)
        {
            foreach (var (name, definition) in security.Schemes)
            {
                securitySchemes[name] = BuildSecurityScheme(definition);
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

            throw new RivetUserException(
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
        if (schemas.Count > 0 && !components.ContainsKey("schemas"))
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

        return doc.ToJsonString(_jsonOptions);
    }

    private static void EnsureReferencedSchemaComponents(
        JsonObject paths,
        JsonObject components,
        JsonObject schemas
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
            schemas[componentId] = new JsonObject();
        }
    }

    private static void CollectSchemaReferences(JsonNode? node, HashSet<string> referenced)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    if (
                        name == "$ref"
                        && child is JsonValue value
                        && value.TryGetValue<string>(out var reference)
                    )
                    {
                        AddSchemaReference(reference, referenced);
                    }
                    else
                    {
                        CollectSchemaReferences(child, referenced);
                    }
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    CollectSchemaReferences(child, referenced);
                }
                break;
        }
    }

    private static void AddSchemaReference(string reference, HashSet<string> referenced)
    {
        if (JsonPointer.TryGetComponentName(reference, "schemas", out var name))
        {
            referenced.Add(name);
        }
    }

    private static void RetainVendorExtensionPathItemOwners(
        JsonObject paths,
        IReadOnlyList<OpenApiVendorExtensionProvenance> extensions
    )
    {
        foreach (var extension in extensions)
        {
            if (JsonPointer.FromUriFragment(extension.OwnerPointer) is ["paths", var path])
            {
                paths.TryAdd(path, new JsonObject());
            }
        }
    }

    private static void AttachVendorExtensions(
        JsonObject document,
        IReadOnlyList<OpenApiVendorExtensionProvenance> extensions
    )
    {
        foreach (var extension in extensions)
        {
            if (
                !JsonPointer.TryResolve(document, extension.OwnerPointer, out var target)
                || target is not JsonObject owner
            )
            {
                throw new RivetUserException(
                    $"Cannot attach preserved vendor extension '{extension.Name}': emitted owner '{extension.OwnerPointer}' does not exist or is not an object."
                );
            }
            if (owner.ContainsKey(extension.Name))
            {
                throw new RivetUserException(
                    $"Cannot attach preserved vendor extension '{extension.Name}' at '{extension.OwnerPointer}': the emitted owner already contains that property."
                );
            }

            try
            {
                owner[extension.Name] = JsonNode.Parse(extension.JsonValue);
            }
            catch (JsonException exception)
            {
                throw new RivetUserException(
                    $"Cannot attach preserved vendor extension '{extension.Name}' at '{extension.OwnerPointer}': invalid JSON value ({exception.Message})."
                );
            }
        }
    }

    private static JsonObject BuildSecurityScheme(SecuritySchemeDefinition definition)
    {
        var result = new JsonObject();
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
                result["flows"] = ObjectOf(
                    oauth2.Flows.Select(flow =>
                        (OAuthFlowName(flow.Type), (JsonNode?)BuildOAuthFlow(flow))
                    )
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
                throw new RivetUserException(
                    $"Unsupported security scheme model '{definition.GetType().Name}'."
                );
        }

        return result;
    }

    private static JsonObject BuildOAuthFlow(OAuth2Flow flow)
    {
        var result = new JsonObject
        {
            ["scopes"] = ObjectOf(flow.Scopes.Select(scope => (scope.Key, (JsonNode?)scope.Value))),
        };
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

    private static JsonArray BuildSecurityRequirements(SecurityRequirements requirements) =>
        ArrayOf(
            requirements.Alternatives.Select(requirement =>
                ObjectOf(
                    requirement.Schemes.Select(scheme =>
                        (scheme.Name, (JsonNode?)StringArray(scheme.Scopes))
                    )
                )
            )
        );

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
                throw new RivetUserException(
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

    private JsonObject BuildPaths(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlySet<string> requestBodyComponentIds,
        IReadOnlySet<string> parameterComponentIds,
        IReadOnlySet<string> responseComponentIds
    )
    {
        var paths = new JsonObject();

        // Order-independent operationId allocation: unique endpoint names keep the
        // baseline {Controller}_{Name}; a colliding name group gets a deterministic
        // route-derived disambiguator on ALL of its members.
        var operationIds = AssignOperationIds(endpoints);

        foreach (var ep in endpoints)
        {
            var pathKey = ep.RouteTemplate;

            if (paths[pathKey] is not JsonObject pathItem)
            {
                pathItem = [];
                paths[pathKey] = pathItem;
            }

            var methodKey = ep.HttpMethod.ToLowerInvariant();
            if (pathItem.ContainsKey(methodKey))
            {
                // Defense in depth: the merger collapses equivalent declarations and
                // fails contradictory ones, but a direct emit call (e.g. --from contract
                // JSON, or a caller bypassing Merge) can still deliver two incompatible
                // operations for one path item. Fail BEFORE any output is written
                // instead of lossy last-wins overwrite.
                var existingEndpoint = endpoints.FirstOrDefault(candidate =>
                    string.Equals(candidate.RouteTemplate, pathKey, StringComparison.Ordinal)
                    && string.Equals(
                        candidate.HttpMethod.ToLowerInvariant(),
                        methodKey,
                        StringComparison.Ordinal
                    )
                    && !ReferenceEquals(candidate, ep)
                );
                if (
                    existingEndpoint is not null
                    && !EndpointMerger.EndpointSurfaceEquivalent(existingEndpoint, ep)
                )
                {
                    throw new RivetUserException(
                        $"error {Diagnostics.ConflictingOperations}: transport identity {ep.HttpMethod.ToUpperInvariant()} {TransportIdentity.NormalizeRoute(pathKey)} is declared by two incompatible operations: "
                            + $"'{existingEndpoint.ControllerName}.{existingEndpoint.Name}' and '{ep.ControllerName}.{ep.Name}'. "
                            + "Resolve the contradiction at the source — first-wins/last-wins cannot resolve conflicting declarations."
                    );
                }

                Diagnostics.Warn(
                    Diagnostics.DuplicateEndpoint,
                    $"duplicate endpoint {ep.HttpMethod} {pathKey} — an equivalent representative is emitted"
                );
            }
            var operation = BuildOperation(
                ep,
                operationIds,
                requestBodyComponentIds,
                parameterComponentIds,
                responseComponentIds
            );
            pathItem[methodKey] = operation;
        }

        return paths;
    }

    /// <summary>
    /// Assigns one operationId per endpoint. Endpoint names that appear only once in
    /// the output set keep the baseline <c>{Controller}_{Name}</c>; every member of a
    /// colliding name group is disambiguated with a deterministic, order-independent
    /// route-derived suffix so overloaded actions (Get() at /items, Get(id) at
    /// /items/{id}) and cross-frontend same-name operations stay distinct. Imported
    /// operations with their own provenance operationId are not touched.
    /// </summary>
    private static IReadOnlyDictionary<TsEndpointDefinition, string> AssignOperationIds(
        IReadOnlyList<TsEndpointDefinition> endpoints
    )
    {
        var result = new Dictionary<TsEndpointDefinition, string>();
        var nameGroups = endpoints
            .GroupBy<TsEndpointDefinition, (string ControllerName, string Name)>(ep =>
                (ep.ControllerName, ep.Name)
            )
            .ToList();

        foreach (var group in nameGroups)
        {
            if (group.Count() == 1)
            {
                result[group.First()] = $"{group.Key.ControllerName}_{group.Key.Name}";
                continue;
            }

            foreach (var ep in group)
            {
                var disambiguator = RouteDisambiguator(
                    TransportIdentity.NormalizeRoute(ep.RouteTemplate)
                );
                result[ep] = $"{ep.ControllerName}_{ep.Name}_{disambiguator}";
            }
        }

        return result;
    }

    /// <summary>
    /// Deterministic, order-independent route disambiguator: path segments joined
    /// with underscores, parameter tokens reduced to their bare names so constraints
    /// and casing drift do not change the id ({Id:guid} → id).
    /// </summary>
    private static string RouteDisambiguator(string normalizedRoute)
    {
        var segments = normalizedRoute
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
                segment.Length > 1 && segment.StartsWith('{') && segment.EndsWith('}')
                    ? segment[1..^1]
                    : segment
            )
            .ToList();

        var joined = string.Join("_", segments);
        return joined.Length == 0 ? "root" : joined;
    }

    private JsonObject BuildOperation(
        TsEndpointDefinition ep,
        IReadOnlyDictionary<TsEndpointDefinition, string> operationIds,
        IReadOnlySet<string> requestBodyComponentIds,
        IReadOnlySet<string> parameterComponentIds,
        IReadOnlySet<string> responseComponentIds
    )
    {
        var operation = new JsonObject();
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
                operation["tags"] = StringArray(provenance.Tags);
            }
            if (provenance.Deprecated)
            {
                operation["deprecated"] = true;
            }
            if (provenance.ServerOverride is { } serverOverride)
            {
                operation["servers"] = ArrayOf(serverOverride.Select(BuildServer));
            }
        }
        else
        {
            operation["operationId"] = operationIds[ep];
            operation["tags"] = new JsonArray { Naming.ToPascalCase(ep.ControllerName) };
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
        var parameters = new JsonArray();
        TsEndpointParam? bodyParam = null;
        var fileParams = new List<TsEndpointParam>();
        var formFieldParams = new List<TsEndpointParam>();

        foreach (var param in ep.Params)
        {
            switch (param.Source)
            {
                case ParamSource.Body:
                    bodyParam = param;
                    break;

                case ParamSource.File:
                    fileParams.Add(param);
                    break;

                case ParamSource.FormField:
                    formFieldParams.Add(param);
                    break;

                // OpenAPI 3.x: Accept/Content-Type/Authorization are not legal header
                // parameters (they belong to content negotiation / securitySchemes) —
                // diagnose and skip rather than emit an invalid spec.
                case ParamSource.Header when IsReservedHeaderName(param.Name):
                    Diagnostics.Warn(
                        Diagnostics.ReservedHeaderParameterSkipped,
                        $"header param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}' is reserved by OpenAPI "
                            + "(Accept/Content-Type/Authorization are described by content/securitySchemes) — omitted from the spec"
                    );
                    break;

                default:
                    parameters.Add(
                        BuildParameter(
                            param,
                            ParameterLocation(param.Source),
                            param.Source == ParamSource.Route
                                || (param.Type is not TsType.Nullable && !param.IsOptional),
                            $"param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                        )
                    );
                    break;
            }
        }

        // QueryAuth: emit auth token as a required query parameter
        if (ep.QueryAuth is not null)
        {
            parameters.Add(
                new JsonObject
                {
                    ["name"] = ep.QueryAuth.ParameterName,
                    ["in"] = "query",
                    ["required"] = true,
                    ["schema"] = new JsonObject { ["type"] = "string" },
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
                var match = parameters.FirstOrDefault(value =>
                    (string?)value!["name"] == reference.Name
                    && (string?)value["in"] == reference.Location
                );
                if (match is not null)
                {
                    parameters[parameters.IndexOf(match)] = ComponentReference(
                        "parameters",
                        reference.ComponentId
                    );
                }
            }
            operation["parameters"] = parameters;
        }

        var formContentType =
            fileParams.Count > 0 ? "multipart/form-data" : "application/x-www-form-urlencoded";
        if (
            ep.RequestContents is null
            && (fileParams.Count > 0 || formFieldParams.Count > 0 || ep.IsFormEncoded)
            && ep.RequestContentTypeOverride is { } declaredContentType
        )
        {
            var parsed = System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(
                declaredContentType,
                out var mediaType
            );
            var multipart = string.Equals(
                mediaType?.MediaType,
                "multipart/form-data",
                StringComparison.OrdinalIgnoreCase
            );
            var urlEncoded = string.Equals(
                mediaType?.MediaType,
                "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase
            );
            if (!parsed || (!multipart && !urlEncoded) || (fileParams.Count > 0 && !multipart))
            {
                throw new RivetUserException(
                    $"error {Diagnostics.UnresolvedBindingSource}: form endpoint '{ep.ControllerName}.{ep.Name}' "
                        + $"cannot use request content type '{declaredContentType}'. Use a supported form content type."
                );
            }
            formContentType = declaredContentType;
        }

        // Request body
        if (ep.RequestContents is not null)
        {
            var primaryRequestType = ep.RequestType ?? bodyParam?.Type;
            operation["requestBody"] = RequestBody(
                ep.RequestBodyRequired ?? (primaryRequestType is not TsType.Nullable),
                MediaContent(
                    ep.RequestContents,
                    entry =>
                        $"request content '{entry.MediaType}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                ),
                ep.RequestExamples
            );
        }
        else if (ep.BinaryRequestContentType is not null)
        {
            // .AcceptsBinary(): the body is the raw bytes — never a JSON/multipart schema,
            // even if a Body param somehow survived upstream (the walker prevents it).
            operation["requestBody"] = RequestBody(
                ep.RequestBodyRequired ?? true,
                MediaContent(ep.BinaryRequestContentType, BinarySchema()),
                examples: null
            );
        }
        else if (fileParams.Count > 0)
        {
            JsonObject multipartSchema;

            if (ep.InputTypeName is not null && _definitions.ContainsKey(ep.InputTypeName))
            {
                multipartSchema = MapTypeReference(
                    new TsType.TypeRef(ep.InputTypeName),
                    $"multipart input on endpoint '{ep.ControllerName}.{ep.Name}'"
                );
            }
            else
            {
                // The TS lowerer decomposes the multipart input into params and never ships
                // the input type definition, so a $ref to ep.InputTypeName would dangle.
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

                multipartSchema = FormSchema(ep, fileParams, formFieldParams);
                // Pin the record name the importer synthesizes for this inline body; without
                // the extension it falls back to the operationId-derived {fieldName}Request
                // convention, which breaks under hand-edited ids.
                multipartSchema["x-rivet-input-type"] = SynthesizedInputTypeName(ep);
            }

            operation["requestBody"] = RequestBody(
                ep.RequestBodyRequired ?? true,
                MediaContent(formContentType, multipartSchema),
                ep.RequestExamples
            );
        }
        else if (formFieldParams.Count > 0)
        {
            operation["requestBody"] = RequestBody(
                ep.RequestBodyRequired ?? true,
                MediaContent(formContentType, FormSchema(ep, [], formFieldParams)),
                ep.RequestExamples
            );
        }
        else if ((bodyParam?.Type ?? ep.RequestType) is { } bodyType)
        {
            operation["requestBody"] = RequestBody(
                // A Nullable body type means the request body is optional
                ep.RequestBodyRequired
                    ?? (bodyType is not TsType.Nullable),
                MediaContent(
                    ep.IsFormEncoded
                        ? formContentType
                        : ep.RequestContentTypeOverride ?? "application/json",
                    BuildBodySchema(bodyType, ep)
                ),
                ep.RequestExamples
            );
        }
        else if (ep.RequestBodyPresent)
        {
            operation["requestBody"] = RequestBody(
                ep.RequestBodyRequired ?? false,
                [],
                ep.RequestExamples
            );
        }

        if (
            ep.Provenance?.RequestBodyComponentId is { } requestBodyComponentId
            && requestBodyComponentIds.Contains(requestBodyComponentId)
        )
        {
            var requestBodyReference = new JsonObject
            {
                ["$ref"] =
                    $"#/components/requestBodies/{JsonPointer.Escape(requestBodyComponentId)}",
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
            && operation["requestBody"] is JsonObject requestBody
        )
        {
            requestBody["description"] = requestBodyDescription;
        }

        // Responses
        var responses = new JsonObject();
        var fileResponseStatusKey = ep.FileContentType is null
            ? null
            : ep
                .Responses.FirstOrDefault(response => response.StatusCode is >= 200 and < 300)
                ?.EffectiveStatusKey;

        foreach (var resp in ep.Responses)
        {
            var respObj = new JsonObject();

            // RIV1102 defense in depth: HTTP forbids a message body on 1xx/204/205/304.
            // Authored examples or contents there could never reach the wire, so
            // emission aborts before any output is written (mirrors the RIV2012
            // path: typed exception → EmitPipeline catch → exit 1). The parse-side
            // guard in ResponseStatusValidation runs on every frontend; this re-check
            // catches any path that assembled content without passing through it.
            // captures any path that assembled content without passing through it.
            // A bare DataType on a body-forbidden status is the synthesized
            // status-preservation artifact (e.g. a .Status(204) override with
            // TOutput in scope); it is not authored content, so it emits
            // description-only bodyless instead of fabricating JSON the host
            // could never send.
            var bodyForbidden = ResponseStatusValidation.IsBodyForbiddenStatusKey(
                resp.EffectiveStatusKey
            );
            if (
                bodyForbidden
                && (resp.Examples is { Count: > 0 } || resp.Contents is { Count: > 0 })
            )
            {
                throw new RivetUserException(
                    $"error {Diagnostics.BodyForbiddenStatusExample}: endpoint '{ep.ControllerName}.{ep.Name}' "
                        + $"authors response content on body-forbidden status {resp.EffectiveStatusKey} — "
                        + "HTTP forbids a message body on 1xx/204/205/304, so the authored example/content "
                        + "could never reach the wire; move it to a status that allows a body or remove it"
                );
            }

            respObj["description"] =
                resp.Description
                ?? (resp.StatusCode == 0 ? "Response" : DefaultStatusDescription(resp.StatusCode));

            // Declared response headers are spec-only at runtime.
            // required is emitted only on explicit opt-in — Rivet cannot enforce presence,
            // so defaulting it would over-promise.
            if (resp.Headers is { Count: > 0 })
            {
                var headerObjs = new JsonObject();
                foreach (var header in resp.Headers)
                {
                    var headerObj = new JsonObject();
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
                        headerObj["example"] = Node(example);
                    }
                    if (header.Examples is { } examples)
                    {
                        headerObj["examples"] = Node(examples);
                    }

                    var headerSchema = MapTsTypeToJsonSchema(
                        header.Type,
                        $"response header '{header.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                    );
                    if (header.SchemaExamples is { } schemaExamples)
                    {
                        headerSchema["examples"] = Node(schemaExamples);
                    }
                    if (header.ContentType is not null)
                    {
                        headerObj["content"] = MediaContent(header.ContentType, headerSchema);
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
                respObj["content"] = WithExamples(
                    MediaContent(
                        resp.Contents,
                        entry =>
                            $"response {resp.StatusCode} content '{entry.MediaType}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                    ),
                    resp.Examples
                );
            }
            else if (resp.DataType is not null && !bodyForbidden)
            {
                // .ProducesContentType() overrides the SUCCESS response's media
                // type only — declared error responses stay application/json.
                var responseContentType = resp.StatusCode is >= 200 and < 300
                    ? ep.ResponseContentTypeOverride ?? "application/json"
                    : "application/json";
                respObj["content"] = WithExamples(
                    MediaContent(
                        responseContentType,
                        MapTsTypeToJsonSchema(
                            resp.DataType,
                            $"response {resp.StatusCode} on endpoint '{ep.ControllerName}.{ep.Name}'"
                        )
                    ),
                    resp.Examples
                );
            }
            else if (
                ep.FileContentType is not null
                && resp.EffectiveStatusKey == fileResponseStatusKey
            )
            {
                respObj["content"] = WithExamples(
                    MediaContent(ep.FileContentType, BinarySchema()),
                    resp.Examples
                );
            }
            else if (resp.Examples is not null)
            {
                var content = WithExamples(new JsonObject(), resp.Examples);
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
                operation["security"] = new JsonArray();
            }
            else if (ep.Security.Scheme is not null)
            {
                operation["security"] = new JsonArray
                {
                    new JsonObject { [ep.Security.Scheme] = new JsonArray() },
                };
            }
        }

        // QueryAuth: emit extension for round-trip fidelity
        if (ep.QueryAuth is not null)
        {
            operation["x-rivet-query-auth"] = new JsonObject
            {
                ["parameterName"] = ep.QueryAuth.ParameterName,
            };
        }

        ApplyOperationSchemaProvenance(operation, ep.Provenance?.Schemas);

        return operation;
    }

    private static void ApplyOperationSchemaProvenance(
        JsonObject operation,
        OpenApiOperationSchemaProvenance? provenance
    )
    {
        if (provenance is null)
        {
            return;
        }

        if (operation["parameters"] is JsonArray parameters)
        {
            foreach (var source in provenance.Parameters)
            {
                var parameter = parameters
                    .OfType<JsonObject>()
                    .FirstOrDefault(candidate =>
                        (string?)candidate["name"] == source.Name
                        && (string?)candidate["in"] == source.Location
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

        if (operation["requestBody"]?["content"] is JsonObject requestContent)
        {
            foreach (var source in provenance.Requests)
            {
                if (requestContent[source.MediaType] is JsonObject media)
                {
                    media["schema"] = ParseSchemaObject(
                        source.Json,
                        $"request content '{source.MediaType}'"
                    );
                }
            }
        }

        if (operation["responses"] is JsonObject responses)
        {
            foreach (var source in provenance.Responses)
            {
                if (responses[source.StatusKey]?["content"]?[source.MediaType] is JsonObject media)
                {
                    media["schema"] = ParseSchemaObject(
                        source.Json,
                        $"response '{source.StatusKey}' content '{source.MediaType}'"
                    );
                }
            }
        }
    }

    private static JsonObject ParseSchemaObject(string json, string context) =>
        JsonSerializer.Deserialize<JsonObject>(json)
        ?? throw new RivetUserException($"{context} is not a JSON object.");

    private static JsonObject BuildServer(OpenApiServerProvenance server)
    {
        var result = new JsonObject { ["url"] = server.Url };
        AddOptionalString(result, "description", server.Description);
        if (server.Variables.Count > 0)
        {
            result["variables"] = ObjectOf(
                server.Variables.Select(variable =>
                {
                    var value = new JsonObject { ["default"] = variable.DefaultValue };
                    if (variable.AllowedValues.Count > 0)
                    {
                        value["enum"] = StringArray(variable.AllowedValues);
                    }
                    AddOptionalString(value, "description", variable.Description);
                    return (variable.Name, (JsonNode?)value);
                })
            );
        }
        return result;
    }

    private static JsonObject BuildTag(OpenApiTagProvenance tag)
    {
        var result = new JsonObject { ["name"] = tag.Name };
        AddOptionalString(result, "description", tag.Description);
        if (tag.ExternalDocs is { } externalDocs)
        {
            result["externalDocs"] = BuildExternalDocs(externalDocs);
        }
        return result;
    }

    private static JsonObject BuildExternalDocs(OpenApiExternalDocsProvenance externalDocs)
    {
        var result = new JsonObject { ["url"] = externalDocs.Url };
        AddOptionalString(result, "description", externalDocs.Description);
        return result;
    }

    private static void AddOptionalString(JsonObject target, string name, string? value)
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }

    private JsonObject BuildParameter(
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
            schema["default"] = SchemaEnricher.ParseJsonLiteral(
                parameter.DefaultValue,
                $"default of {context}"
            );
        }
        SchemaEnricher.EnrichConstraints(schema, parameter.Constraints);
        if (parameter.SchemaExamples is { } schemaExamples)
        {
            schema["examples"] = Node(schemaExamples);
        }

        var result = new JsonObject
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
            result["example"] = Node(example);
        }
        if (parameter.Examples is { } examples)
        {
            result["examples"] = Node(examples);
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

    private JsonObject BuildSchemaWithLeafProvenance(
        TsType type,
        string? schemaType,
        string? format,
        bool isFormatSpecified,
        string context
    )
    {
        if (schemaType is not null)
        {
            var schema = new JsonObject
            {
                ["type"] =
                    type is TsType.Nullable ? new JsonArray { schemaType, "null" } : schemaType,
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

    private static JsonNode? Node(JsonElement value) => JsonSerializer.SerializeToNode(value);

    private static JsonArray ArrayOf(IEnumerable<JsonNode?> items) => new([.. items]);

    private static JsonArray StringArray(IEnumerable<string> values) =>
        ArrayOf(values.Select(value => (JsonNode?)value));

    /// <summary>Duplicate names throw, as <c>ToDictionary</c> did.</summary>
    private static JsonObject ObjectOf(IEnumerable<(string Name, JsonNode? Value)> properties) =>
        new(properties.Select(property => KeyValuePair.Create(property.Name, property.Value)));

    private static JsonObject BinarySchema() =>
        new() { ["type"] = "string", ["format"] = "binary" };

    private static string ParameterLocation(ParamSource source) =>
        source switch
        {
            ParamSource.Route => "path",
            ParamSource.Query => "query",
            ParamSource.Header => "header",
            ParamSource.Cookie => "cookie",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
        };

    private static JsonObject RequestBody(
        bool required,
        JsonObject content,
        IReadOnlyList<TsEndpointExample>? examples
    ) => new() { ["required"] = required, ["content"] = WithExamples(content, examples) };

    private static JsonObject MediaContent(string mediaType, JsonObject schema) =>
        new() { [mediaType] = new JsonObject { ["schema"] = schema } };

    private JsonObject MediaContent(
        IReadOnlyList<TsMediaTypeContent> entries,
        Func<TsMediaTypeContent, string> context
    )
    {
        var content = new JsonObject();
        foreach (var entry in entries)
        {
            var media = new JsonObject();
            if (entry.IsBinary)
            {
                media["schema"] = BinarySchema();
            }
            else if (entry.Schema is not null)
            {
                var schema = BuildSchemaWithLeafProvenance(
                    entry.Schema,
                    entry.SchemaType,
                    entry.Format,
                    entry.IsFormatSpecified,
                    context(entry)
                );
                if (entry.SchemaDescription is not null)
                {
                    schema["description"] = entry.SchemaDescription;
                }
                media["schema"] = schema;
            }

            content[entry.MediaType] = media;
        }

        return content;
    }

    /// <summary>
    /// The object schema of a form body: file parts first, then form fields. A file part is
    /// required unless explicitly optional; a form field also becomes optional when nullable.
    /// </summary>
    private JsonObject FormSchema(
        TsEndpointDefinition ep,
        IReadOnlyList<TsEndpointParam> fileParams,
        IReadOnlyList<TsEndpointParam> formFieldParams
    )
    {
        var properties = new JsonObject();
        var required = new List<string>();
        foreach (var file in fileParams)
        {
            properties[file.Name] = MapTsTypeToJsonSchema(
                file.Type,
                $"file param '{file.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
            );
            if (!file.IsOptional)
            {
                required.Add(file.Name);
            }
        }
        foreach (var field in formFieldParams)
        {
            properties[field.Name] = MapTsTypeToJsonSchema(
                field.Type,
                $"form field '{field.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
            );
            if (field.Type is not TsType.Nullable && !field.IsOptional)
            {
                required.Add(field.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = StringArray(required);
        }

        return schema;
    }

    /// <summary>
    /// Maps a request-body type to its schema; inline object bodies get
    /// <c>x-rivet-input-type</c> so the importer synthesizes the same record name
    /// every loop ($ref bodies carry their name in the reference itself).
    /// </summary>
    private JsonObject BuildBodySchema(TsType bodyType, TsEndpointDefinition ep)
    {
        if (BuildRouteFilteredBodySchema(bodyType, ep) is { } filteredSchema)
        {
            if (bodyType is not TsType.Nullable)
            {
                return filteredSchema;
            }

            return new JsonObject
            {
                ["oneOf"] = new JsonArray
                {
                    filteredSchema,
                    new JsonObject { ["type"] = "null" },
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

    private JsonObject? BuildRouteFilteredBodySchema(TsType bodyType, TsEndpointDefinition ep)
    {
        if (
            !TryGetRouteFilteredBodyProperties(
                bodyType,
                ep,
                out var bodyProperties,
                out var sourceTypeName
            )
        )
        {
            return null;
        }

        if (!_filteredBodyNames.ContainsKey(FilteredBodyIdentity(ep, bodyProperties)))
        {
            throw new RivetUserException(
                $"route-filtered request body name was not allocated for endpoint '{ep.ControllerName}.{ep.Name}'"
            );
        }

        return BuildObjectSchema(bodyProperties, typeName: sourceTypeName);
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

    private bool TryGetRouteFilteredBodyProperties(
        TsType? bodyType,
        TsEndpointDefinition ep,
        out IReadOnlyList<TsPropertyDefinition> bodyProperties,
        out string sourceTypeName
    )
    {
        bodyProperties = [];
        if (!TryResolveBodyProperties(bodyType, out var sourceProperties, out sourceTypeName))
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

        // Route-filtered lowering is request surface: a response-only property
        // (serialized but never deserializable — derived readOnly, or an explicit
        // [RivetReadOnly]) cannot appear in a request body; route-bound properties
        // are excluded as before (they are bound by the route, not the JSON body)
        // (planner-constraint:component-schema-directionality).
        bodyProperties = sourceProperties
            .Where(prop => !matchedBodyNames.Contains(prop.Name) && !prop.IsReadOnly)
            .ToList();
        return bodyProperties.Count != sourceProperties.Count;
    }

    private bool TryResolveBodyProperties(
        TsType? bodyType,
        out IReadOnlyList<TsPropertyDefinition> properties,
        out string typeName
    )
    {
        properties = [];
        typeName = null!;
        var unwrapped = bodyType is TsType.Nullable nullable ? nullable.Inner : bodyType;
        var (definitionName, generic) = unwrapped switch
        {
            TsType.TypeRef typeRef => (typeRef.Name, null),
            TsType.Generic g => (g.Name, g),
            _ => ((string?)null, (TsType.Generic?)null),
        };

        if (
            definitionName is null
            || !_definitions.TryGetValue(definitionName, out var definition)
            || definition.Type is not null
        )
        {
            return false;
        }

        typeName = definition.Name;
        if (generic is null)
        {
            properties = definition.Properties;
            return true;
        }

        if (definition.TypeParameters.Count != generic.TypeArguments.Count)
        {
            return false;
        }

        var substitutions = TypeParameterMap(definition, generic);
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

    private static JsonObject BuildComponentExamples(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyList<OpenApiComponentExampleProvenance> authoredExamples,
        IReadOnlyList<OpenApiComponentRequestBodyProvenance> requestBodies
    )
    {
        var examples = new JsonObject();

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

    private JsonObject BuildComponentRequestBodies(
        IReadOnlyList<OpenApiComponentRequestBodyProvenance> requestBodies
    )
    {
        var result = new JsonObject();
        foreach (var requestBody in requestBodies)
        {
            var content = new JsonObject();
            foreach (var entry in requestBody.Contents)
            {
                var media = new JsonObject();
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

            var value = RequestBody(requestBody.Required, content, requestBody.Examples);
            AddOptionalString(value, "description", requestBody.Description);
            result.Add(requestBody.Name, value);
        }
        return result;
    }

    private static void AddJsonComponents(
        JsonObject components,
        string kind,
        IEnumerable<(string Name, string Json)> values
    )
    {
        var result = new JsonObject();
        foreach (var (name, json) in values)
        {
            result.Add(
                name,
                JsonSerializer.Deserialize<JsonObject>(json)
                    ?? throw new RivetUserException(
                        $"Preserved component {kind} '{name}' is not a JSON object."
                    )
            );
        }
        if (result.Count > 0)
        {
            components[kind] = result;
        }
    }

    private static JsonObject BuildComponentExample(OpenApiComponentExampleProvenance example)
    {
        var result = new JsonObject();
        AddOptionalString(result, "summary", example.Summary);
        AddOptionalString(result, "description", example.Description);
        if (example.JsonValue is { } jsonValue)
        {
            result["value"] = ParseJson(jsonValue);
        }
        else
        {
            result["externalValue"] = example.ExternalValue!;
        }

        return result;
    }

    private static void AddComponentExamples(
        JsonObject target,
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
                target.TryAdd(componentId, new JsonObject { ["value"] = ParseJson(json) });
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
            target[example.ComponentExampleId] = new JsonObject
            {
                ["value"] = ParseJson(example.ResolvedJson),
            };
        }
    }

    private static JsonObject WithExamples(
        JsonObject content,
        IReadOnlyList<TsEndpointExample>? examples
    )
    {
        if (examples is null || examples.Count == 0)
        {
            return content;
        }

        var templateSchema = content
            .Select(entry => entry.Value?["schema"])
            .FirstOrDefault(schema => schema is not null);

        foreach (var group in examples.GroupBy(example => example.MediaType))
        {
            var createdMediaContent = false;
            if (content[group.Key] is not JsonObject mediaContentDict)
            {
                mediaContentDict = [];
                if (templateSchema is not null)
                {
                    mediaContentDict["schema"] = templateSchema.DeepClone();
                }

                content[group.Key] = mediaContentDict;
                createdMediaContent = true;
            }

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
                mediaContentDict["example"] = ParseJson(inlineExampleJson!);
                continue;
            }

            var examplesDict = new JsonObject();
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

    private static JsonObject? ToOpenApiExample(TsEndpointExample example)
    {
        if (example.ComponentExampleId is not null && example.ResolvedJson is not null)
        {
            return ComponentReference("examples", example.ComponentExampleId);
        }

        var json = example.Json ?? example.ResolvedJson;
        if (json is null)
        {
            return null;
        }

        // null is a legal example value (`value: null`) — see ParseJson.
        return new JsonObject { ["value"] = ParseJson(json) };
    }

    // Returns null only for the JSON literal `null`, a legal example value (the importer
    // converts Microsoft.OpenApi's null sentinel back to it); malformed JSON throws.
    private static JsonNode? ParseJson(string json) => JsonNode.Parse(json);

    private JsonObject MapTsTypeToJsonSchema(TsType type, string? context = null)
    {
        return type switch
        {
            TsType.Primitive p => MapPrimitive(p, context),

            TsType.Nullable n => MapNullable(n, context),

            TsType.Array a => BuildArraySchema(a, context),

            TsType.Dictionary d => BuildDictionarySchema(d, context),

            TsType.StringUnion su => MapStringUnion(su),

            TsType.IntUnion iu => MapIntUnion(iu),

            TsType.Literal literal => new JsonObject
            {
                ["const"] = JsonElementValue(literal.Value),
            },

            TsType.TypeRef r => MapTypeReference(r, context),

            TsType.Generic g => new JsonObject
            {
                ["$ref"] = $"#/components/schemas/{MonomorphisedName(g)}",
            },

            TsType.Brand b => MapBrandReference(b, context),

            TsType.TypeParam tp => FallbackTypeParam(tp, context),

            TsType.InlineObject obj => BuildInlineObjectSchema(obj, context),

            TsType.TaggedUnion tu => BuildTaggedUnionSchema(tu, context),

            // Undiscriminated union ([RivetUnion] wrapper): a plain oneOf — no
            // discriminator, variants may be inline primitive schemas.
            TsType.Union u => new JsonObject
            {
                ["oneOf"] = ArrayOf(
                    u.Variants.Select(variant => MapTsTypeToJsonSchema(variant, context))
                ),
            },

            _ => new JsonObject { ["type"] = "object" },
        };
    }

    private JsonObject MapTypeReference(TsType.TypeRef reference, string? context)
    {
        if (_definitions.TryGetValue(reference.Name, out var definition))
        {
            if (definition.Metadata?.Provenance == TsTypeProvenance.Synthetic)
            {
                if (!_inliningSyntheticTypes.Add(reference.Name))
                {
                    throw new RivetUserException(
                        $"synthetic type '{reference.Name}' is recursive and cannot be inlined without recursive schema algebra"
                    );
                }

                try
                {
                    return BuildDefinitionSchema(definition);
                }
                finally
                {
                    _inliningSyntheticTypes.Remove(reference.Name);
                }
            }

            return ComponentReference(definition.Metadata?.ComponentId ?? reference.Name);
        }

        if (_enums.TryGetValue(reference.Name, out var enumType))
        {
            var metadata = GetMetadata(enumType);
            if (metadata?.Provenance == TsTypeProvenance.Synthetic)
            {
                return MapTsTypeToJsonSchema(enumType, context);
            }

            return ComponentReference(metadata?.ComponentId ?? reference.Name);
        }

        if (_brands.TryGetValue(reference.Name, out var brand))
        {
            return MapBrandReference(brand, context);
        }

        return ComponentReference(reference.Name);
    }

    private JsonObject MapBrandReference(TsType.Brand brand, string? context)
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

    private static JsonObject ComponentReference(string componentId) =>
        new() { ["$ref"] = $"#/components/schemas/{JsonPointer.Escape(componentId)}" };

    private static JsonObject ComponentReference(string kind, string componentId) =>
        new() { ["$ref"] = $"#/components/{kind}/{JsonPointer.Escape(componentId)}" };

    private static JsonObject MapIntUnion(TsType.IntUnion union)
    {
        var enumValues = ArrayOf(union.Members.Select(IntEnumLiteral.ToJson));
        var schema = new JsonObject
        {
            ["type"] =
                union.ScalarMetadata?.IsNullable == true
                    ? new JsonArray { "integer", "null" }
                    : "integer",
            ["enum"] = enumValues,
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

    private static JsonObject MapStringUnion(TsType.StringUnion union)
    {
        var schema = new JsonObject
        {
            ["type"] =
                union.ScalarMetadata?.IsNullable == true
                    ? new JsonArray { "string", "null" }
                    : "string",
            ["enum"] = StringArray(union.Members),
        };
        if (union.Format is not null)
        {
            schema["format"] = union.Format;
        }
        if (union.NamingPolicy is not null)
        {
            // The declared converter casing, round-tripped for the importer:
            // re-deriving the wire from the policy keeps imported enums on the
            // same family converter instead of blanket member pins.
            schema["x-rivet-enum-naming-policy"] = union.NamingPolicy;
        }
        if (union.Description is not null)
        {
            schema["description"] = union.Description;
        }
        EnrichScalarSchema(schema, union.ScalarMetadata);
        return schema;
    }

    private static JsonValue JsonElementValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => JsonValue.Create(value.GetString()!),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => JsonValue.Create(
                integer
            ),
            JsonValueKind.Number => JsonValue.Create(value.GetDouble()),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            _ => throw new InvalidOperationException(
                $"Unsupported scalar literal kind '{value.ValueKind}'."
            ),
        };

    private JsonObject BuildInlineObjectSchema(TsType.InlineObject obj, string? context = null)
    {
        var properties = new JsonObject();
        var required = new List<string>();

        foreach (var field in obj.Fields)
        {
            var fieldSchema = MapTsTypeToJsonSchema(field.Type, context);
            // Polymorphic-variant surface asymmetry becomes readOnly/writeOnly
            // (planner-constraint:tagged-union-variant-surface).
            if (field.Surface is TsType.InlineObjectFieldSurface.ResponseOnly)
            {
                fieldSchema["readOnly"] = true;
            }
            else if (field.Surface is TsType.InlineObjectFieldSurface.RequestOnly)
            {
                fieldSchema["writeOnly"] = true;
            }

            properties[field.Name] = fieldSchema;
            if (!field.Optional)
            {
                required.Add(field.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };

        if (required.Count > 0)
        {
            schema["required"] = StringArray(required);
        }

        return schema;
    }

    private JsonObject BuildTaggedUnionSchema(TsType.TaggedUnion tu, string? context = null)
    {
        // OpenAPI `discriminator` is only meaningful on a oneOf of $ref'd named schemas with
        // a tag→$ref mapping — consumers reject or ignore a discriminator over inline schemas
        // (E11). Each inline variant becomes a named component schema referenced via $ref.
        var baseName =
            _taggedUnionNames.GetValueOrDefault(InlineTypeExtractor.CanonicalHash(tu))
            ?? TsType.GetNameSuffix(tu);

        var oneOf = new JsonArray();
        var mapping = new JsonObject();

        foreach (var variant in tu.Variants)
        {
            var variantSchema = MapTsTypeToJsonSchema(variant.Type, context);

            string refPath;
            if (variantSchema.Count == 1 && variantSchema["$ref"] is JsonValue existingRef)
            {
                // Variant is already a named schema (TypeRef/Generic/Brand) — ref it directly.
                refPath = existingRef.GetValue<string>();
            }
            else
            {
                var componentName =
                    variant.Metadata?.ComponentId
                    ?? $"{baseName}_{Naming.ToPascalCase(variant.Tag)}";
                _extraComponents.TryAdd(componentName, variantSchema);
                refPath = $"#/components/schemas/{JsonPointer.Escape(componentName)}";
            }

            oneOf.Add(new JsonObject { ["$ref"] = refPath });
            mapping[variant.Tag] = refPath;
        }

        return new JsonObject
        {
            ["oneOf"] = oneOf,
            ["discriminator"] = new JsonObject
            {
                ["propertyName"] = tu.Discriminator,
                ["mapping"] = mapping,
            },
        };
    }

    private static JsonObject MapPrimitive(TsType.Primitive p, string? context = null)
    {
        if (p.Name == "File")
        {
            return new JsonObject
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

            var unknownSchema = new JsonObject();
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
            var base64Schema = new JsonObject
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
            return new JsonObject
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

        var schema = new JsonObject { ["type"] = type };

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

    private JsonObject MapNullable(TsType.Nullable n, string? context = null)
    {
        var inner = MapTsTypeToJsonSchema(n.Inner, context);

        // OpenAPI 3.1 / JSON Schema 2020-12: null is a type. Schemas with a single
        // type become a type array; everything else gets an explicit null branch.
        if (inner["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeName))
        {
            inner["type"] = new JsonArray { typeName, "null" };
            return inner;
        }

        // $ref: a sibling `type: "null"` would be ANDed with the referenced schema in
        // 2020-12, so the null alternative must be a oneOf branch instead.
        if (inner.ContainsKey("$ref"))
        {
            return new JsonObject
            {
                ["oneOf"] = new JsonArray
                {
                    inner,
                    new JsonObject { ["type"] = "null" },
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
        return new JsonObject
        {
            ["anyOf"] = new JsonArray
            {
                inner,
                new JsonObject { ["type"] = "null" },
            },
        };
    }

    private static JsonObject FallbackTypeParam(TsType.TypeParam tp, string? context = null)
    {
        Diagnostics.Warn(
            Diagnostics.UnresolvedTypeParameter,
            $"unresolved type parameter '{tp.Name}' in OpenAPI schema{AtContext(context)} — emitting as object"
        );
        return new JsonObject { ["type"] = "object" };
    }

    private static string AtContext(string? context) => context is null ? "" : $" at {context}";

    private string MonomorphisedName(TsType.Generic g)
    {
        // The pure suffix scheme is lossy in places (4+-member unions → "Enum", 4+-field
        // inline objects → "Object"), so distinct instantiations can share a pure name.
        // The per-emit registry assigns each distinct shape a distinct deterministic name.
        if (_genericNames.TryGetValue(InlineTypeExtractor.CanonicalHash(g), out var assigned))
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
    private void AssignComponentNames(IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        var roots = endpoints
            .SelectMany(endpoint => endpoint.AllTypes().Select(site => site.Type))
            .Concat(
                _definitions.Values.SelectMany(def =>
                    def.Type is not null ? [def.Type] : def.Properties.Select(prop => prop.Type)
                )
            )
            .Concat(_brands.Values.Select(brand => brand.Inner))
            .Concat(_enums.Values);
        var generics = new List<TsType.Generic>();
        var taggedUnions = new List<TsType.TaggedUnion>();
        foreach (var type in roots.SelectMany(root => root.SelfAndDescendants()))
        {
            switch (type)
            {
                case TsType.Generic g:
                    generics.Add(g);
                    break;
                case TsType.TaggedUnion tu:
                    taggedUnions.Add(tu);
                    break;
            }
        }

        // Names already claimed by emitted definition/brand/enum schemas
        var usedNames = new HashSet<string>(
            _definitions.Where(kv => kv.Value.TypeParameters.Count == 0).Select(kv => kv.Key)
        );
        usedNames.UnionWith(_brands.Keys);
        usedNames.UnionWith(_enums.Keys);

        foreach (var g in generics)
        {
            var hash = InlineTypeExtractor.CanonicalHash(g);
            if (_genericNames.ContainsKey(hash))
            {
                continue;
            }

            _genericNames[hash] = ClaimName(TsType.MonomorphisedName(g), usedNames);
        }

        // Tagged unions that ARE a named type alias keep the alias as base name
        foreach (var (name, def) in _definitions)
        {
            if (def.Type is TsType.TaggedUnion aliased)
            {
                _taggedUnionNames.TryAdd(
                    InlineTypeExtractor.CanonicalHash(aliased),
                    def.Metadata?.ComponentId ?? name
                );
            }
        }

        foreach (var tu in taggedUnions)
        {
            var hash = InlineTypeExtractor.CanonicalHash(tu);
            if (_taggedUnionNames.ContainsKey(hash))
            {
                continue;
            }

            _taggedUnionNames[hash] = ClaimName(TsType.GetNameSuffix(tu), usedNames);
        }

        foreach (var endpoint in endpoints)
        {
            var bodyType =
                endpoint.Params.FirstOrDefault(param => param.Source == ParamSource.Body)?.Type
                ?? endpoint.RequestType;
            if (!TryGetRouteFilteredBodyProperties(bodyType, endpoint, out var properties, out _))
            {
                continue;
            }

            var identity = FilteredBodyIdentity(endpoint, properties);
            if (_filteredBodyNames.ContainsKey(identity))
            {
                continue;
            }

            var endpointName = Naming.ToPascalCaseFromSegments(endpoint.Name) + "Request";
            var controllerName =
                Naming.ToPascalCaseFromSegments(endpoint.ControllerName) + endpointName;
            var matchingDefinitionName = _definitions
                .Where(pair => pair.Value.TypeParameters.Count == 0 && pair.Value.Type is null)
                .Where(pair =>
                    JsonNode.DeepEquals(
                        BuildDefinitionSchema(pair.Value),
                        BuildObjectSchema(properties)
                    )
                )
                .OrderByDescending(pair => pair.Key == endpointName)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (matchingDefinitionName is not null)
            {
                _filteredBodyNames[identity] = matchingDefinitionName;
            }
            else
            {
                _filteredBodyNames[identity] = usedNames.Add(endpointName)
                    ? endpointName
                    : ClaimName(controllerName, usedNames);
            }
        }
    }

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

    private JsonObject BuildSchemas(IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        var schemas = new JsonObject();

        foreach (var (name, def) in _definitions)
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
        CollectGenericInstances(endpoints, genericInstances);

        // E6: templates are skipped during collection (their unresolved Generic refs are
        // garbage like PagedResult_T), so nested instantiations only surface when a
        // template's properties are resolved against concrete type args. Iterate to a
        // fixpoint so e.g. Wrapper<X> { PagedResult<X> } registers PagedResult_X too.
        var pending = new Queue<TsType.Generic>(genericInstances.Values);
        while (pending.Count > 0)
        {
            var instance = pending.Dequeue();
            if (!_definitions.TryGetValue(instance.Name, out var template))
            {
                continue;
            }

            var instanceMap = TypeParameterMap(template, instance);

            var discovered = new Dictionary<string, TsType.Generic>();
            if (template.Type is not null)
            {
                CollectGenericsFromType(
                    TsType.ResolveTypeParams(template.Type, instanceMap),
                    discovered
                );
            }
            else
            {
                foreach (var prop in template.Properties)
                {
                    CollectGenericsFromType(
                        TsType.ResolveTypeParams(prop.Type, instanceMap),
                        discovered
                    );
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
            if (!_definitions.TryGetValue(generic.Name, out var genericDef))
            {
                // E6: a generic instantiation whose template is absent from definitions used
                // to emit a $ref with no matching component — a dangling reference every
                // consumer rejects (GAP-1). Never emit a dangling $ref: warn loudly and
                // synthesize a valid free-form fallback component under the $ref'd name.
                Diagnostics.Warn(
                    Diagnostics.GenericTemplateMissing,
                    $"generic template '{generic.Name}' (instantiated as '{monoName}') is not present in the contract's type definitions — emitting a free-form object schema; fix the upstream producer to include the template definition"
                );

                schemas[monoName] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] =
                        $"Unresolved generic instantiation of '{generic.Name}' — template definition missing from source contract",
                };
                continue;
            }

            var typeParamMap = TypeParameterMap(genericDef, generic);
            var monoSchema = genericDef.Type is not null
                ? MapTsTypeToJsonSchema(
                    TsType.ResolveTypeParams(genericDef.Type, typeParamMap),
                    $"type '{genericDef.Name}'"
                )
                : BuildObjectSchema(
                    genericDef
                        .Properties.Select(prop =>
                            prop with
                            {
                                Type = TsType.ResolveTypeParams(prop.Type, typeParamMap),
                            }
                        )
                        .ToList(),
                    typeName: genericDef.Name
                );
            monoSchema["x-rivet-generic"] = new JsonObject
            {
                ["name"] = generic.Name,
                ["typeParams"] = StringArray(genericDef.TypeParameters),
                ["args"] = ObjectOf(
                    typeParamMap.Select(kv => (kv.Key, (JsonNode?)GetCSharpTypeName(kv.Value)))
                ),
            };
            schemas[monoName] = monoSchema;
        }

        // Brands as schemas with x-rivet-brand extension
        foreach (var (name, brand) in _brands)
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
        foreach (var (name, enumType) in _enums)
        {
            var metadata = GetMetadata(enumType);
            if (metadata?.Provenance == TsTypeProvenance.Synthetic)
            {
                continue;
            }
            var enumSchema = MapTsTypeToJsonSchema(enumType, $"enum '{name}'");
            if (_definitions.TryGetValue(name, out var scalarDefinition))
            {
                if (scalarDefinition.Type is TsType.Nullable)
                {
                    enumSchema["type"] = new JsonArray
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

    private JsonObject BuildDefinitionSchema(TsTypeDefinition def)
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

    private static void EnrichScalarSchema(JsonObject schema, TsScalarMetadata? metadata)
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
            schema["required"] = StringArray(metadata.Required);
        }

        if (metadata.DefaultValue is not null)
        {
            schema["default"] = SchemaEnricher.ParseJsonLiteral(
                metadata.DefaultValue,
                "schema metadata default"
            );
        }
        if (metadata.Example is not null)
        {
            schema["example"] = SchemaEnricher.ParseJsonLiteral(
                metadata.Example,
                "schema metadata example"
            );
        }
        if (metadata.Examples is not null)
        {
            schema["examples"] = SchemaEnricher.ParseJsonLiteral(
                metadata.Examples,
                "schema metadata examples"
            );
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
            var value = new JsonObject();
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

    private static void ApplyNullableMetadata(JsonObject schema)
    {
        switch (schema["type"])
        {
            case JsonValue value when value.TryGetValue<string>(out var type):
                schema["type"] = new JsonArray { type, "null" };
                return;
            case JsonArray types when types.Any(type => (string?)type == "null"):
                return;
        }

        var inner = schema.DeepClone().AsObject();
        schema.Clear();
        schema[inner.ContainsKey("$ref") ? "oneOf" : "anyOf"] = new JsonArray
        {
            inner,
            new JsonObject { ["type"] = "null" },
        };
    }

    private JsonObject BuildObjectSchema(
        IReadOnlyList<TsPropertyDefinition> propertiesDefinition,
        string? description = null,
        string? typeName = null,
        TsScalarMetadata? metadata = null
    )
    {
        var properties = new JsonObject();
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

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };

        if (description is not null)
        {
            schema["description"] = description;
        }

        if (required.Count > 0)
        {
            schema["required"] = StringArray(required);
        }

        if (properties.Count == 0)
        {
            schema["x-rivet-empty-record"] = true;
        }

        EnrichScalarSchema(schema, metadata);

        return schema;
    }

    private JsonObject BuildArraySchema(TsType.Array a, string? context = null)
    {
        var items = MapTsTypeToJsonSchema(a.Element, context);
        EnrichScalarSchema(items, a.ElementMetadata);
        var schema = new JsonObject { ["type"] = "array", ["items"] = items };

        // JsonArray is represented internally as an array of unknown values.
        // Other element-side CLR tags describe the item, not its parent.
        if (a.Element is TsType.Primitive { Name: "unknown", CSharpType: "JsonArray" } p)
        {
            schema["x-rivet-csharp-type"] = p.CSharpType;
        }

        return schema;
    }

    private JsonObject BuildDictionarySchema(TsType.Dictionary d, string? context = null)
    {
        var value = MapTsTypeToJsonSchema(d.Value, context);
        EnrichScalarSchema(value, d.ValueMetadata);
        var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = value };

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

    private JsonObject BuildDictionaryKeySchema(TsType key, string? context)
    {
        // Primitive keys are built inline rather than via MapPrimitive: property names
        // are always strings, but a numeric format (int32, …) would flip MapPrimitive's
        // emitted type to integer — invalid under propertyNames.
        if (key is TsType.Primitive p)
        {
            var schema = new JsonObject { ["type"] = "string" };
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

    private static Dictionary<string, TsType> TypeParameterMap(
        TsTypeDefinition template,
        TsType.Generic instance
    ) =>
        template
            .TypeParameters.Zip(instance.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);

    private void CollectGenericInstances(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        Dictionary<string, TsType.Generic> genericInstances
    )
    {
        foreach (var (_, type) in endpoints.SelectMany(endpoint => endpoint.AllTypes()))
        {
            CollectGenericsFromType(type, genericInstances);
        }

        // Every definition schema is emitted, so every generic it uses must be monomorphised.
        foreach (var (_, def) in _definitions)
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

    private void CollectGenericsFromType(TsType type, Dictionary<string, TsType.Generic> instances)
    {
        foreach (var generic in type.SelfAndDescendants().OfType<TsType.Generic>())
        {
            instances.TryAdd(MonomorphisedName(generic), generic);
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
}
