using System.Diagnostics;
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

    /// <summary>Tagged-union canonical shape hash + variant tag → assigned variant component name.</summary>
    private readonly Dictionary<(string UnionHash, string Tag), string> _variantNames = [];

    /// <summary>
    /// Every component schema name already spoken for: emitted definitions, brands, enums,
    /// preserved provenance schemas, producer-pinned variant ids, and every name the pre-pass
    /// or a variant claim handed out. Variant names are claimed against it lazily.
    /// </summary>
    private readonly HashSet<string> _claimedNames = new(StringComparer.Ordinal);

    /// <summary>Variant component schemas synthesized for tagged unions, with the variant that asked for each.</summary>
    private readonly Dictionary<string, (JsonObject Schema, string Origin)> _extraComponents = [];

    /// <summary>The typed schemas of preserved request-body components, which can use generics too.</summary>
    private readonly List<TsType> _requestBodyComponentSchemas = [];

    /// <summary>Component schemas the producer preserved verbatim (generated document provenance).</summary>
    private readonly HashSet<string> _preservedSchemaNames = new(StringComparer.Ordinal);

    /// <summary>
    /// The one answer to which contract entry stands for the schema at each component id, in
    /// the order <see cref="BuildSchemas"/> writes them. At a preserved id <see cref="BuildSchemas"/>
    /// writes the preserved schema instead, and the entry stands in for it, so its metadata
    /// (nullability, say) must mirror that schema. Filled by <see cref="ResolveComponentSources"/>.
    /// </summary>
    private readonly Dictionary<string, ComponentSource> _componentSources = new(
        StringComparer.Ordinal
    );

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
                        endpoint.Name,
                        endpoint.HttpMethod,
                        endpoint.ReturnType
                    ),
                }
            )
            .ToList();
        documentInfo ??= new OpenApiDocumentInfo();
        var emitter = new OpenApiEmitter(definitions, brands, enums);
        emitter._requestBodyComponentSchemas.AddRange(
            (documentInfo.Provenance?.ComponentRequestBodies ?? [])
                .SelectMany(requestBody => requestBody.Contents)
                .Select(content => content.Schema)
                .OfType<TsType>()
        );
        emitter.AssignComponentNames(
            normalizedEndpoints,
            (documentInfo.Provenance?.ComponentSchemas ?? []).Select(schema => schema.Name)
        );
        emitter.ResolveComponentSources(normalizedEndpoints);
        return emitter.EmitCore(normalizedEndpoints, security, documentInfo);
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
        var headerComponents = documentInfo.Provenance?.ComponentHeaders ?? [];
        var paths = BuildPaths(
            endpoints,
            requestBodyComponents
                .Select(component => component.Name)
                .ToHashSet(StringComparer.Ordinal),
            parameterComponents
                .Select(component => component.Name)
                .ToHashSet(StringComparer.Ordinal),
            responseComponents
                .Select(component => component.Name)
                .ToHashSet(StringComparer.Ordinal),
            headerComponents.Select(component => component.Name).ToHashSet(StringComparer.Ordinal)
        );
        var schemas = BuildSchemas(documentInfo.Provenance?.ComponentSchemas ?? []);
        var examples = BuildComponentExamples(
            endpoints,
            documentInfo.Provenance?.ComponentExamples ?? [],
            requestBodyComponents
        );
        var requestBodies = BuildComponentRequestBodies(requestBodyComponents);

        // Tagged-union variant components synthesized while mapping types above. Synthesized
        // names were claimed against every other component name, so only a producer-pinned
        // componentId can land on a taken name — sharing it is fine when the shapes agree.
        foreach (var (name, (schema, origin)) in _extraComponents)
        {
            if (!schemas.TryGetPropertyValue(name, out var existing))
            {
                schemas[name] = schema;
            }
            else if (!JsonNode.DeepEquals(existing, schema))
            {
                throw PinnedVariantCollision(name, origin, "another component schema");
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

        // Operations carry tags — declare them in the global tags array
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
        AddJsonComponents(
            components,
            "headers",
            headerComponents.Select(value => (value.Name, value.Json))
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

        // Every endpoint security requirement must reference a defined scheme. Its type
        // cannot be inferred from a name, so generation fails rather than inventing semantics.
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.Security is not null))
        {
            ValidateSecurityRequirements(
                endpoint.Security!,
                security?.Schemes ?? new Dictionary<string, SecuritySchemeDefinition>(),
                $"operation {endpoint.HttpMethod} {endpoint.RouteTemplate}"
            );
        }

        if (securitySchemes.Count > 0)
        {
            components["securitySchemes"] = securitySchemes;
        }

        if (components.Count > 0)
        {
            doc["components"] = components;
        }

        var preservedFields = documentInfo.Provenance?.PreservedFields ?? [];
        RetainPreservedFieldOwners(doc, paths, components, preservedFields);
        AttachPreservedFields(doc, preservedFields);

        // Last, over the finished document: a preserved field (an encoding's headers, say)
        // can reference a schema component too.
        RejectDanglingSchemaReferences(doc);

        return doc.ToJsonString(_jsonOptions);
    }

    private static void RejectDanglingSchemaReferences(JsonObject document)
    {
        var schemas = (document["components"] as JsonObject)?["schemas"] as JsonObject;
        // Component id → JSON pointer of its first reference, so the error names a site.
        var referenced = new Dictionary<string, string>(StringComparer.Ordinal);
        OpenApiStructure.VisitObjects(
            document,
            swagger2: false,
            (obj, pointer) =>
            {
                if (
                    obj["$ref"] is JsonValue value
                    && value.TryGetValue<string>(out var reference)
                    && JsonPointer.TryGetComponentName(reference, "schemas", out var name)
                )
                {
                    referenced.TryAdd(name, $"{pointer}/$ref");
                }
            }
        );
        foreach (
            var (componentId, site) in referenced.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        )
        {
            if (schemas?.ContainsKey(componentId) == true)
            {
                continue;
            }

            throw Diagnostics.Error(
                Diagnostics.UnknownTypeUntypedSchema,
                $"schema component '{componentId}' is referenced at {site} "
                    + "but no type definition, enum, brand or preserved component schema defines it. "
                    + $"Add a definition named '{componentId}' to the contract's types (a --from producer must ship every type it references), "
                    + "or point the reference at a type that exists"
            );
        }
    }

    /// <summary>
    /// A preserved field can belong to an owner the contracts alone don't produce: a path
    /// item with no operations, or an otherwise empty components object.
    /// </summary>
    private static void RetainPreservedFieldOwners(
        JsonObject document,
        JsonObject paths,
        JsonObject components,
        IReadOnlyList<OpenApiPreservedFieldProvenance> fields
    )
    {
        foreach (var field in fields)
        {
            switch (JsonPointer.FromUriFragment(field.OwnerPointer))
            {
                case ["paths", var path]:
                    paths.TryAdd(path, new JsonObject());
                    break;
                case ["components", ..]:
                    document.TryAdd("components", components);
                    break;
            }
        }
    }

    private static void AttachPreservedFields(
        JsonObject document,
        IReadOnlyList<OpenApiPreservedFieldProvenance> fields
    )
    {
        foreach (var field in fields)
        {
            if (
                !JsonPointer.TryResolve(document, field.OwnerPointer, out var target)
                || target is not JsonObject owner
            )
            {
                throw Diagnostics.Error(
                    Diagnostics.PreservedFieldUnattachable,
                    $"cannot attach preserved field '{field.Name}': emitted owner '{field.OwnerPointer}' does not exist or is not an object. "
                        + "The C# no longer emits what the field was preserved on; re-import the OpenAPI document, or remove the [RivetPreservedField]."
                );
            }
            if (owner.ContainsKey(field.Name))
            {
                throw Diagnostics.Error(
                    Diagnostics.PreservedFieldUnattachable,
                    $"cannot attach preserved field '{field.Name}' at '{field.OwnerPointer}': the emitted owner already contains that property. "
                        + "The C# now says it itself; remove the [RivetPreservedField], or re-import the OpenAPI document."
                );
            }

            try
            {
                owner[field.Name] = JsonNode.Parse(field.JsonValue);
            }
            catch (JsonException exception)
            {
                // OpenApiProvenanceWalker refuses a preserved field whose JSON doesn't parse
                // (RIV1122), so only a Rivet bug gets here.
                throw new InvalidOperationException(
                    $"Preserved field '{field.Name}' at '{field.OwnerPointer}' reached emission with invalid JSON ({exception.Message}).",
                    exception
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
                throw new InvalidOperationException(
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
                throw Diagnostics.Error(
                    Diagnostics.UndefinedSecurityScheme,
                    $"security scheme '{name}' is referenced by {context} security requirements but has no definition; "
                        + "define it with --security or [assembly: RivetSecurityScheme(...)]"
                );
            }
        }
    }

    private JsonObject BuildPaths(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlySet<string> requestBodyComponentIds,
        IReadOnlySet<string> parameterComponentIds,
        IReadOnlySet<string> responseComponentIds,
        IReadOnlySet<string> headerComponentIds
    )
    {
        var paths = new JsonObject();

        // Order-independent operationId allocation: unique endpoint names keep the
        // baseline {Controller}_{Name}; a colliding name group gets a deterministic
        // route-derived disambiguator on ALL of its members.
        var operationIds = AssignOperationIds(endpoints);

        // OpenAPI identifies a path by its template with the parameter names erased, so
        // /items/{id} and /items/{itemId} are one path; writing both is an invalid document.
        var pathKeysByShape = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var ep in endpoints)
        {
            var pathKey = ep.RouteTemplate;

            if (paths[pathKey] is not JsonObject pathItem)
            {
                var shape = RouteParser.EraseParameterNames(pathKey);
                if (pathKeysByShape.TryGetValue(shape, out var twin))
                {
                    throw Diagnostics.Error(
                        Diagnostics.EquivalentPathTemplates,
                        $"paths '{twin}' and '{pathKey}' differ only in parameter names, which OpenAPI forbids ('{ep.ControllerName}.{ep.Name}' declares the second). "
                            + "Use one set of parameter names for the routes."
                    );
                }
                pathKeysByShape[shape] = pathKey;
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
                    throw Diagnostics.Error(
                        Diagnostics.ConflictingOperations,
                        $"transport identity {ep.HttpMethod.ToUpperInvariant()} {TransportIdentity.NormalizeRoute(pathKey)} is declared by two incompatible operations: "
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
                responseComponentIds,
                headerComponentIds
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
        IReadOnlySet<string> responseComponentIds,
        IReadOnlySet<string> headerComponentIds
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

                // OpenAPI 3.x ignores a header parameter named Accept, Content-Type or
                // Authorization (media types and security schemes describe them), so the spec
                // would never carry what the handler reads.
                case ParamSource.Header
                    when OpenApiStructure.IsReservedHeaderName(param.Name, isResponseHeader: false):
                    throw Diagnostics.Error(
                        Diagnostics.ReservedHeaderParameter,
                        $"header param '{param.Name}' on endpoint "
                            + $"'{ep.ControllerName}.{ep.Name}' is ignored by OpenAPI — Accept and Content-Type "
                            + "are described by media types (.ProducesContentType(...) / .AcceptsContentType(...)) "
                            + "and Authorization by a security scheme "
                            + "(.Secure(...) or --security); use a custom header name for anything else"
                    );

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
                throw Diagnostics.Error(
                    Diagnostics.UnresolvedBindingSource,
                    $"form endpoint '{ep.ControllerName}.{ep.Name}' "
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
        else if (fileParams.Count > 0 || formFieldParams.Count > 0)
        {
            operation["requestBody"] = RequestBody(
                ep.RequestBodyRequired ?? true,
                MediaContent(formContentType, FormBodySchema(ep, fileParams, formFieldParams)),
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
            // emission aborts before any output is written (a RivetUserException, exit 1,
            // as for RIV2012). The parse-side guard in ResponseStatusValidation runs on
            // every frontend; this re-check catches any path that assembled content
            // without passing through it.
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
                throw Diagnostics.Error(
                    Diagnostics.BodyForbiddenStatusExample,
                    $"endpoint '{ep.ControllerName}.{ep.Name}' "
                        + $"authors response content on body-forbidden status {resp.EffectiveStatusKey} — "
                        + "HTTP forbids a message body on 1xx/204/205/304, so the authored example/content "
                        + "could never reach the wire; move it to a status that allows a body or remove it"
                );
            }

            respObj["description"] =
                resp.Description
                ?? (resp.StatusCode == 0 ? "Response" : DefaultStatusDescription(resp.StatusCode));

            // required is emitted only on explicit opt-in: the adapters then refuse a response
            // that leaves the header out, so defaulting it would break handlers that never set it.
            if (resp.Headers is { Count: > 0 })
            {
                var headerObjs = new JsonObject();
                foreach (var header in resp.Headers)
                {
                    // OpenAPI says a response header named Content-Type "SHALL be ignored",
                    // so the spec would drop it while the adapters demand it.
                    if (OpenApiStructure.IsReservedHeaderName(header.Name, isResponseHeader: true))
                    {
                        throw Diagnostics.Error(
                            Diagnostics.ReservedHeaderParameter,
                            $"response header '{header.Name}' on status {resp.EffectiveStatusKey} of endpoint "
                                + $"'{ep.ControllerName}.{ep.Name}' is ignored by OpenAPI — a response's "
                                + "media type is its content, so declare it with .ProducesContentType(...) "
                                + "(or the response's content types) instead of a header"
                        );
                    }

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
                    var headerContext =
                        $"response header '{header.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'";
                    if (header.Examples is { } examples)
                    {
                        headerObj["examples"] = SchemaEnricher.ExampleMap(
                            examples,
                            $"examples of {headerContext}"
                        );
                    }
                    var headerSchema = MapTsTypeToJsonSchema(header.Type, headerContext);
                    if (header.SchemaExamples is { } schemaExamples)
                    {
                        headerSchema["examples"] = SchemaEnricher.ParseJsonArray(
                            schemaExamples.GetRawText(),
                            $"schema examples of {headerContext}"
                        );
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

        foreach (var reference in ep.Provenance?.ResponseHeaderComponentReferences ?? [])
        {
            if (
                headerComponentIds.Contains(reference.ComponentId)
                && responses[reference.StatusKey] is JsonObject response
                && response["headers"] is JsonObject headers
                && headers.ContainsKey(reference.HeaderName)
            )
            {
                headers[reference.HeaderName] = ComponentReference(
                    "headers",
                    reference.ComponentId
                );
            }
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

        if (ep.Security is { } securityRequirements)
        {
            operation["security"] = BuildSecurityRequirements(securityRequirements);
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

    /// <summary>
    /// A schema the importer kept verbatim in provenance metadata. One that doesn't read as a
    /// JSON object is stale or hand-edited generated code (RIV1122).
    /// </summary>
    private static JsonObject ParseSchemaObject(string json, string context)
    {
        try
        {
            if (JsonSerializer.Deserialize<JsonObject>(json) is { } schema)
            {
                return schema;
            }
        }
        catch (JsonException exception)
        {
            throw MalformedProvenanceSchema(context, exception.Message);
        }

        throw MalformedProvenanceSchema(context, "it isn't an object");
    }

    private static RivetUserException MalformedProvenanceSchema(string context, string why) =>
        Diagnostics.Error(
            Diagnostics.MalformedGeneratedMetadata,
            $"the provenance schema of {context} is not a JSON object ({why}). Re-import the OpenAPI document."
        );

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
        var result = new JsonObject
        {
            ["name"] = parameter.Name,
            ["in"] = location,
            ["required"] = required,
            ["schema"] = BuildParamSchema(parameter, context),
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
            result["examples"] = SchemaEnricher.ExampleMap(examples, $"examples of {context}");
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
        if (location == "query" && parameter.AllowReserved)
        {
            result["allowReserved"] = true;
        }

        return result;
    }

    /// <summary>
    /// A param's schema with the annotations that live on the schema: the leaf's pinned
    /// type and format, the default, the constraints and the schema examples. A route,
    /// query or header param wraps it in a Parameter Object; a form field is a property of
    /// the form body's schema (<see cref="FormSchema"/>).
    /// </summary>
    private JsonObject BuildParamSchema(TsEndpointParam parameter, string context)
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
            schema["examples"] = SchemaEnricher.ParseJsonArray(
                schemaExamples.GetRawText(),
                $"schema examples of {context}"
            );
        }

        return schema;
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
    /// The record name the importer should synthesize for an inline request-body
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
    /// A form body's schema. Parts lowered from an input record name the property each one
    /// is (<see cref="TsEndpointParam.BodyPropertyName"/>): when the record's component lists
    /// exactly those, under the part names, the body references it; otherwise the parts are
    /// spelled out inline, each with its property's schema where the component has the
    /// property, in the order the record declares them. Parts with no record behind them (a
    /// <c>--from</c> contract, <c>[FromForm]</c> action parameters) are spelled out from the
    /// params, unless the contract names a component to reference. An inline body pins the
    /// record name the importer synthesizes for it.
    /// </summary>
    private JsonObject FormBodySchema(
        TsEndpointDefinition ep,
        IReadOnlyList<TsEndpointParam> fileParams,
        IReadOnlyList<TsEndpointParam> formFieldParams
    )
    {
        var definition =
            ep.InputTypeName is not null
            && _definitions.TryGetValue(ep.InputTypeName, out var found)
            && found.Type is null
                ? found
                : null;
        // Files first, then fields: the order a body with no record behind it has always had.
        IReadOnlyList<TsEndpointParam> parts = [.. fileParams, .. formFieldParams];
        if (definition is not null && parts.All(part => part.BodyPropertyName is not null))
        {
            var properties = definition.Properties.ToDictionary(
                property => property.Name,
                StringComparer.Ordinal
            );
            // What the component says a request carries: every property but the readOnly
            // ones. A route-bound member is in it, so a route-split input is inline, and so
            // is a body a host needs a part of that the component calls optional (a
            // contract's positional record, whose constructor-bound parts are required).
            var sent = definition
                .Properties.Where(property => !property.IsReadOnly)
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (
                parts.All(part => part.Name == part.BodyPropertyName)
                && sent.SetEquals(parts.Select(part => part.BodyPropertyName!))
                && !parts.Any(part =>
                    !part.IsOptional
                    && properties.GetValueOrDefault(part.BodyPropertyName!)?.IsOptional == true
                )
            )
            {
                return MapTypeReference(
                    new TsType.TypeRef(ep.InputTypeName!),
                    $"form input on endpoint '{ep.ControllerName}.{ep.Name}'"
                );
            }

            // In the record's declaration order, as the walker lists them.
            var inline = FormSchema(
                ep,
                ep.Params.Where(param => param.Source is ParamSource.File or ParamSource.FormField)
                    .ToList(),
                part => properties.GetValueOrDefault(part.BodyPropertyName!)
            );
            AddAdditionalProperties(inline, definition.AdditionalProperties);
            inline["x-rivet-input-type"] = SynthesizedInputTypeName(ep);
            return inline;
        }

        if (definition is not null && fileParams.Count > 0)
        {
            // A --from contract's multipart input named by a definition it ships.
            return MapTypeReference(
                new TsType.TypeRef(ep.InputTypeName!),
                $"multipart input on endpoint '{ep.ControllerName}.{ep.Name}'"
            );
        }

        // The TS lowerer decomposes the multipart input into params and deliberately never
        // ships the input type definition, so InputTypeName, when present, is only a name
        // hint for this inline body. Pinning the record name the importer synthesizes keeps
        // it under hand-edited operationIds, which the {fieldName}Request fallback doesn't.
        var schema = FormSchema(ep, parts, _ => null);
        if (fileParams.Count > 0)
        {
            schema["x-rivet-input-type"] = SynthesizedInputTypeName(ep);
        }

        return schema;
    }

    /// <summary>
    /// The object schema of a form body's <paramref name="parts"/>, in order. A part with a
    /// record property behind it (<paramref name="sourceProperty"/>) takes that property's
    /// schema, and is required when the property or the part says so (a host can need a part
    /// its property calls optional). Otherwise a file part is required unless explicitly optional,
    /// and a form field also becomes optional when nullable; a form field is a property, so
    /// the annotations a Parameter Object carries beside its schema (description,
    /// deprecated, a single example) go on the property's schema here.
    /// </summary>
    private JsonObject FormSchema(
        TsEndpointDefinition ep,
        IReadOnlyList<TsEndpointParam> parts,
        Func<TsEndpointParam, TsPropertyDefinition?> sourceProperty
    )
    {
        var properties = new JsonObject();
        var required = new List<string>();
        foreach (var part in parts)
        {
            if (sourceProperty(part) is { } property)
            {
                properties[part.Name] = BuildPropertySchema(
                    property,
                    $"form part '{part.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                );
                if (!property.IsOptional || !part.IsOptional)
                {
                    required.Add(part.Name);
                }

                continue;
            }

            if (part.Source == ParamSource.File)
            {
                properties[part.Name] = MapTsTypeToJsonSchema(
                    part.Type,
                    $"file param '{part.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"
                );
                if (!part.IsOptional)
                {
                    required.Add(part.Name);
                }

                continue;
            }

            var field = part;
            var context = $"form field '{field.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'";
            var fieldSchema = BuildParamSchema(field, context);
            if (field.Description is not null)
            {
                fieldSchema["description"] = field.Description;
            }
            if (field.IsDeprecated)
            {
                fieldSchema["deprecated"] = true;
            }
            if (field.Example is { } example)
            {
                // JSON Schema 2020-12 has no singular `example`; it joins the examples.
                var examples = fieldSchema["examples"] as JsonArray ?? [];
                examples.Add(Node(example));
                fieldSchema["examples"] = examples;
            }
            properties[field.Name] = fieldSchema;
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

        // The body is the type's JSON less its route-bound members, so it's as open or as
        // closed as the type.
        return BuildObjectSchema(
            bodyProperties,
            typeName: sourceTypeName,
            additionalProperties: _definitions[sourceTypeName].AdditionalProperties
        );
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

        // Only a route-bound member forces an inline body. Without one the body $refs the
        // component, whose readOnly members OpenAPI already keeps out of a request.
        if (
            !ep.Params.Any(param =>
                param.Source == ParamSource.Route && param.BodyPropertyName is not null
            )
        )
        {
            return false;
        }

        bodyProperties = TsEndpointDefinition.RequestBodyProperties(ep.Params, sourceProperties);
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
        // Who registered each component first, for RIV2015's message.
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var example in authoredExamples)
        {
            examples.Add(example.Name, BuildComponentExample(example));
            origins[example.Name] = "the document's components.examples";
        }

        foreach (var endpoint in endpoints)
        {
            var origin = $"endpoint '{endpoint.ControllerName}.{endpoint.Name}'";
            AddComponentExamples(examples, origins, endpoint.RequestExamples, origin);

            foreach (var response in endpoint.Responses)
            {
                AddComponentExamples(examples, origins, response.Examples, origin);
            }
        }
        foreach (var requestBody in requestBodies)
        {
            AddComponentExamples(
                examples,
                origins,
                requestBody.Examples,
                $"request-body component '{requestBody.Name}'"
            );
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
                    // OpenApiProvenanceWalker refuses a component that isn't a JSON object
                    // (RIV1122), so only a Rivet bug gets here.
                    ?? throw new InvalidOperationException(
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

    /// <summary>
    /// Registers the components an example pins: its own <c>componentExampleId</c> and the
    /// ones its value embeds. One id is one component, so a second example pinning it must
    /// say the same thing: an equal value shares it, a different one is RIV2015 (the
    /// second <c>$ref</c> would otherwise point at the first value). A summary or
    /// description is documentation of the component: one example stating it is enough,
    /// and two stating different ones conflict like two values.
    /// </summary>
    private static void AddComponentExamples(
        JsonObject target,
        Dictionary<string, string> origins,
        IReadOnlyList<TsEndpointExample>? examples,
        string origin
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
                AddComponentExample(
                    target,
                    origins,
                    componentId,
                    ParseJson(json),
                    summary: null,
                    description: null,
                    $"{origin} (embedded in an example value)"
                );
            }

            if (example.ComponentExampleId is null || example.ResolvedJson is null)
            {
                continue;
            }

            // null is a legal example value (`value: null`) — see ParseJson.
            AddComponentExample(
                target,
                origins,
                example.ComponentExampleId,
                ParseJson(example.ResolvedJson),
                example.Summary,
                example.Description,
                origin
            );
        }
    }

    private static void AddComponentExample(
        JsonObject target,
        Dictionary<string, string> origins,
        string componentId,
        JsonNode? value,
        string? summary,
        string? description,
        string origin
    )
    {
        if (target[componentId] is not JsonObject existing)
        {
            var component = new JsonObject();
            AddOptionalString(component, "summary", summary);
            AddOptionalString(component, "description", description);
            component["value"] = value;
            target[componentId] = component;
            origins[componentId] = origin;
            return;
        }

        var conflict =
            !existing.TryGetPropertyValue("value", out var existingValue)
                ? "an externalValue where this one has a value"
            : !JsonNode.DeepEquals(existingValue, value) ? "a different value"
            : DisagreeingText(existing, "summary", summary)
                ?? DisagreeingText(existing, "description", description);
        if (conflict is not null)
        {
            throw Diagnostics.Error(
                Diagnostics.ConflictingComponentExample,
                $"component example '{componentId}' is pinned by {origins[componentId]} and by {origin} with {conflict}. "
                    + "One components.examples entry holds one example, so the second $ref would point at the first. "
                    + "Give one of them its own component id, or make the two the same."
            );
        }

        // The first example left it unstated; this one states it.
        if (summary is not null && existing["summary"] is null)
        {
            existing.Insert(0, "summary", summary);
        }
        if (description is not null && existing["description"] is null)
        {
            existing.Insert(existing.ContainsKey("summary") ? 1 : 0, "description", description);
        }
    }

    private static string? DisagreeingText(JsonObject existing, string key, string? stated) =>
        stated is not null
        && existing[key]?.GetValue<string>() is { } existingText
        && !string.Equals(existingText, stated, StringComparison.Ordinal)
            ? $"a different {key} ('{existingText}' against '{stated}')"
            : null;

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
                && groupedExamples[0].Summary is null
                && groupedExamples[0].Description is null
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
            // OpenAPI 3.1 Reference Objects allow summary and description beside $ref.
            var reference = ComponentReference("examples", example.ComponentExampleId);
            AddOptionalString(reference, "summary", example.Summary);
            AddOptionalString(reference, "description", example.Description);
            return reference;
        }

        var json = example.Json ?? example.ResolvedJson;
        if (json is null && example.Summary is null && example.Description is null)
        {
            return null;
        }

        var result = new JsonObject();
        AddOptionalString(result, "summary", example.Summary);
        AddOptionalString(result, "description", example.Description);
        if (json is not null)
        {
            // null is a legal example value (`value: null`) — see ParseJson.
            result["value"] = ParseJson(json);
        }
        return result;
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

            TsType.TypeParam tp => throw UnresolvedTypeParam(tp, context),

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
                    throw Diagnostics.Error(
                        Diagnostics.MalformedGeneratedMetadata,
                        $"synthetic type '{reference.Name}' is recursive and cannot be inlined without recursive schema algebra. "
                            + "Give the type a component of its own (provenance 'component'), or re-import the OpenAPI document it came from."
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

        foreach (var property in obj.Properties)
        {
            properties[property.Name] = BuildPropertySchema(property, context);
            if (!property.IsOptional)
            {
                required.Add(property.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };

        if (required.Count > 0)
        {
            schema["required"] = StringArray(required);
        }

        AddAdditionalProperties(schema, obj.AdditionalProperties);
        return schema;
    }

    /// <summary>
    /// An object that takes members beyond its properties says so (<c>{}</c>, which reads as
    /// "any value" to a generator where an absent keyword often reads as a closed type), and
    /// one that refuses them says <c>false</c>. Unstated stays unstated.
    /// </summary>
    private static void AddAdditionalProperties(JsonObject schema, bool? additionalProperties)
    {
        if (additionalProperties is { } open)
        {
            schema["additionalProperties"] = open ? new JsonObject() : false;
        }
    }

    private JsonObject BuildTaggedUnionSchema(TsType.TaggedUnion tu, string? context = null)
    {
        // OpenAPI `discriminator` is only meaningful on a oneOf of $ref'd named schemas with
        // a tag→$ref mapping — consumers reject or ignore a discriminator over inline schemas.
        // Each inline variant becomes a named component schema referenced via $ref.
        var baseName = TaggedUnionBaseName(tu);
        var oneOf = new JsonArray();
        var mapping = new JsonObject();

        foreach (var variant in tu.Variants)
        {
            var refPath = VariantReference(tu, variant, baseName, context);
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

    private string TaggedUnionBaseName(TsType.TaggedUnion tu) =>
        _taggedUnionNames.GetValueOrDefault(InlineTypeExtractor.CanonicalHash(tu))
        ?? TsType.GetNameSuffix(tu);

    /// <summary>
    /// A tagged-union base whose source schema was preserved verbatim stands as the source
    /// wrote it, so its own schema is never built. The variants it names are source components
    /// all the same (Jira's <c>oneOf</c> over <c>$ref</c>s), so a variant pinned to a component
    /// id still gets that component; one with no id would be the emitter's own
    /// <c>{Base}_{Tag}</c> invention, which the preserved schema never refs.
    /// </summary>
    private void BuildPinnedVariants(TsTypeDefinition definition)
    {
        if (definition.Type is not TsType.TaggedUnion tu)
        {
            return;
        }

        var baseName = TaggedUnionBaseName(tu);
        foreach (
            var variant in tu.Variants.Where(variant => variant.Metadata?.ComponentId is not null)
        )
        {
            VariantReference(tu, variant, baseName, $"type '{definition.Name}'");
        }
    }

    /// <summary>The $ref a variant is reached by, registering its component when it needs one.</summary>
    private string VariantReference(
        TsType.TaggedUnion tu,
        TsType.TaggedUnionVariant variant,
        string baseName,
        string? context
    )
    {
        if (
            variant.Metadata?.ComponentId is { } preserved
            && _preservedSchemaNames.Contains(preserved)
        )
        {
            // The producer pinned the variant to a component it also preserved verbatim
            // because the C# type can't carry all of it: that copy is the variant's schema,
            // and the shape built from the C# type is only an approximation of it, so it
            // isn't built at all.
            return $"#/components/schemas/{JsonPointer.Escape(preserved)}";
        }

        var variantSchema = MapTsTypeToJsonSchema(variant.Type, context);
        ApplyVariantAnnotations(variantSchema, tu.Discriminator, variant);

        if (variantSchema.Count == 1 && variantSchema["$ref"] is JsonValue existingRef)
        {
            // Variant is already a named schema (TypeRef/Generic/Brand) — ref it directly.
            return existingRef.GetValue<string>();
        }

        var componentName = VariantComponentName(
            InlineTypeExtractor.CanonicalHash(tu),
            baseName,
            variant
        );
        var origin = $"variant '{variant.Tag}' of the tagged union '{baseName}'";
        if (!_extraComponents.TryGetValue(componentName, out var claimed))
        {
            _extraComponents[componentName] = (variantSchema, origin);
        }
        else if (!JsonNode.DeepEquals(claimed.Schema, variantSchema))
        {
            // Synthesized names are unique per (union, tag), so only two pinned
            // componentIds can meet here.
            throw PinnedVariantCollision(componentName, origin, claimed.Origin);
        }
        return $"#/components/schemas/{JsonPointer.Escape(componentName)}";
    }

    /// <summary>
    /// Writes what the derived type says about the variant as a whole: its description and
    /// type-level schema metadata, as its own component would carry them, and its tag property
    /// as the source authored it in place of the synthesized single-value <c>enum</c>. The
    /// walker and the contract reader only let a variant carry them when it is an inline object
    /// with the tag property and a tag schema object that admits its tag.
    /// </summary>
    private static void ApplyVariantAnnotations(
        JsonObject variantSchema,
        string discriminator,
        TsType.TaggedUnionVariant variant
    )
    {
        if (variant is { Description: null, ScalarMetadata: null, DiscriminatorSchema: null })
        {
            return;
        }

        if (variant.Description is not null)
        {
            variantSchema["description"] = variant.Description;
        }
        // The tag goes in before the scalar metadata, so whatever shape nullability gives the
        // schema already carries it.
        if (variant.DiscriminatorSchema is { } authoredTag)
        {
            variantSchema["properties"]![discriminator] = JsonNode.Parse(authoredTag)!.AsObject();
        }
        EnrichScalarSchema(variantSchema, variant.ScalarMetadata);
    }

    /// <summary>
    /// A variant the producer pinned (<c>metadata.componentId</c>) keeps its id. Every other
    /// variant claims <c>{Base}_{Tag}</c> once per (union shape, tag), against every name
    /// already taken, with the same numeric-suffix scheme the pre-pass uses — so two tags that
    /// PascalCase alike, or a <c>{Base}_{Tag}</c> that a real type already owns, still give
    /// each variant a <c>$ref</c> to its own schema.
    /// </summary>
    private string VariantComponentName(
        string unionHash,
        string baseName,
        TsType.TaggedUnionVariant variant
    )
    {
        if (variant.Metadata?.ComponentId is { } pinned)
        {
            return pinned;
        }

        var key = (unionHash, variant.Tag);
        if (!_variantNames.TryGetValue(key, out var name))
        {
            name = ClaimName($"{baseName}_{Naming.ToPascalCase(variant.Tag)}", _claimedNames);
            _variantNames[key] = name;
        }

        return name;
    }

    private static RivetUserException PinnedVariantCollision(
        string componentName,
        string origin,
        string other
    ) =>
        Diagnostics.Error(
            Diagnostics.TaggedUnionComponentCollision,
            $"{origin} pins component id '{componentName}', "
                + $"which {other} already uses with a different schema — one id cannot $ref two shapes. "
                + "Give the variant's metadata.componentId a name of its own, or make the variant a ref to the existing type"
        );

    // System.Text.Json writes TimeSpan in the "c" format: [-][d.]hh:mm:ss[.fffffff].
    private const string TimeSpanPattern =
        @"^-?(\d{1,8}\.)?([01]\d|2[0-3]):[0-5]\d:[0-5]\d(\.\d{1,7})?$";

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
            // "Any JSON value" has one honest schema, the empty one: TypeScript's `unknown`
            // (no csharpType), object, JsonElement, JsonDocument and the JsonNode family all
            // map to it silently. Any other csharpType names a type with no schema mapping at all,
            // and an untyped stand-in would advertise "anything goes" for a fixed wire shape.
            if (!IsAnyJsonCSharpType(p.CSharpType))
            {
                throw Diagnostics.Error(
                    Diagnostics.UnknownTypeUntypedSchema,
                    $"type '{p.CSharpType}'{AtContext(context)} has no schema mapping. "
                        + "Replace it with a supported type, or with JsonElement/object if the value really is any JSON"
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

        // byte[]: System.Text.Json serializes byte[]
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

        // char: System.Text.Json serializes char as a single-character
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

        // TimeSpan: the pattern describes what the server writes, System.Text.Json's invariant
        // "c" format. Its reader is more lenient, but a client sending the written form is safe.
        if (p is { Name: "string", CSharpType: "TimeSpan" })
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = TimeSpanPattern,
                ["x-rivet-csharp-type"] = "TimeSpan",
            };
        }

        // CLR integer primitives are represented internally as number + format.
        // An imported schema can legitimately use an integer-looking format on a string.
        var type =
            p.Name == "number"
            && (
                p.Format
                    is "int32"
                        or "int64"
                        or "int16"
                        or "uint16"
                        or "int8"
                        or "uint8"
                        or "uint32"
                        or "uint64"
                || p.CSharpType is "Int128" or "UInt128"
            )
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

        // A schema that already admits null needs no null alternative: a oneOf of a component
        // and {type: null} would match null twice, which oneOf rejects. A primitive spells its
        // nullability below, where an untyped one's null branch is how import reads JsonNode?.
        if (
            n.Inner is not TsType.Primitive
            && AdmitsNull(n.Inner, new HashSet<string>(StringComparer.Ordinal))
        )
        {
            return inner;
        }

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

    /// <summary>
    /// "Any JSON value" CLR types, which all map to the empty schema (JsonNode adds only its
    /// x-rivet-csharp-type). Any other csharpType on an unknown primitive has no schema.
    /// </summary>
    private static bool IsAnyJsonCSharpType(string? csharpType) =>
        csharpType
            is null
                or "object"
                or "JsonElement"
                or "JsonDocument"
                or "JsonNode"
                or "JsonObject"
                or "JsonArray"
                or "JsonValue";

    /// <summary>
    /// Whether the schema <paramref name="type"/> maps to admits null. References resolve the
    /// way <see cref="MapTypeReference"/> and <see cref="MapBrandReference"/> resolve them, to
    /// the contract entry that stands for the component at the referenced id: the one
    /// <see cref="BuildSchemas"/> builds there, or, at a preserved id, the one whose metadata
    /// mirrors the preserved schema. <paramref name="visited"/> stops an alias cycle.
    /// </summary>
    private bool AdmitsNull(TsType type, HashSet<string> visited) =>
        type switch
        {
            TsType.Nullable => true,
            TsType.Primitive { Name: "unknown", CSharpType: var csharpType } => IsAnyJsonCSharpType(
                csharpType
            ),
            TsType.StringUnion stringUnion => stringUnion.ScalarMetadata?.IsNullable == true,
            TsType.IntUnion intUnion => intUnion.ScalarMetadata?.IsNullable == true,
            TsType.TypeRef reference => TypeReferenceAdmitsNull(reference.Name, visited),
            TsType.Brand brand => BrandAdmitsNull(brand, visited),
            TsType.Union union => union.Variants.Any(variant => AdmitsNull(variant, visited)),
            TsType.Generic generic => ComponentAdmitsNull(MonomorphisedName(generic), visited),
            _ => false,
        };

    private bool TypeReferenceAdmitsNull(string name, HashSet<string> visited)
    {
        if (_definitions.TryGetValue(name, out var definition))
        {
            return definition.Metadata?.Provenance == TsTypeProvenance.Synthetic
                ? visited.Add($"inline:{name}") && DefinitionAdmitsNull(definition, visited)
                : ComponentAdmitsNull(definition.Metadata?.ComponentId ?? name, visited);
        }
        if (_enums.TryGetValue(name, out var enumType))
        {
            var metadata = GetMetadata(enumType);
            return metadata?.Provenance == TsTypeProvenance.Synthetic
                ? AdmitsNull(enumType, visited)
                : ComponentAdmitsNull(metadata?.ComponentId ?? name, visited);
        }
        return _brands.TryGetValue(name, out var brand)
            ? BrandAdmitsNull(brand, visited)
            : ComponentAdmitsNull(name, visited);
    }

    // A synthetic brand is inlined, so its scalar definition's metadata never applies.
    private bool BrandAdmitsNull(TsType.Brand brand, HashSet<string> visited) =>
        brand.Metadata?.Provenance == TsTypeProvenance.Synthetic
            ? AdmitsNull(brand.Inner, visited)
            : ComponentAdmitsNull(brand.Metadata?.ComponentId ?? brand.Name, visited);

    private bool DefinitionAdmitsNull(TsTypeDefinition definition, HashSet<string> visited) =>
        definition.ScalarMetadata?.IsNullable == true
        || definition.Type is { } aliased && AdmitsNull(aliased, visited);

    /// <summary>
    /// Whether the component schema at <paramref name="componentId"/> admits null, read from the
    /// contract entry that stands for it (at a preserved id, its metadata mirrors the preserved schema).
    /// </summary>
    private bool ComponentAdmitsNull(string componentId, HashSet<string> visited)
    {
        if (
            !visited.Add($"component:{componentId}")
            || !_componentSources.TryGetValue(componentId, out var source)
        )
        {
            return false;
        }

        switch (source)
        {
            case ComponentSource.Definition(var name):
                return DefinitionAdmitsNull(_definitions[name], visited);
            case ComponentSource.Generic(var instance):
                return _definitions.TryGetValue(instance.Name, out var template)
                    && template.Type is { } body
                    && AdmitsNull(
                        TsType.ResolveTypeParams(body, TypeParameterMap(template, instance)),
                        visited
                    );
            case ComponentSource.Brand(var name):
                _definitions.TryGetValue(name, out var brandScalar);
                return AdmitsNull(_brands[name].Inner, visited)
                    || brandScalar?.ScalarMetadata?.IsNullable == true;
            case ComponentSource.Enum(var name):
                _definitions.TryGetValue(name, out var enumScalar);
                return AdmitsNull(_enums[name], visited)
                    || enumScalar?.Type is TsType.Nullable
                    || enumScalar?.ScalarMetadata?.IsNullable == true;
            default:
                throw new UnreachableException($"Unknown component source {source}.");
        }
    }

    /// <summary>
    /// A contract entry that stands for a component: <see cref="BuildSchemas"/> writes its schema,
    /// or at a preserved id the preserved schema its metadata mirrors.
    /// </summary>
    private abstract record ComponentSource
    {
        public sealed record Definition(string Name) : ComponentSource;

        /// <summary>A monomorphised instantiation of a generic template.</summary>
        public sealed record Generic(TsType.Generic Instance) : ComponentSource;

        public sealed record Brand(string Name) : ComponentSource;

        public sealed record Enum(string Name) : ComponentSource;
    }

    /// <summary>
    /// Decides the contract entry at each component id: definitions, then monomorphised
    /// generics, then brands, then enums, a later kind replacing an earlier one at the same id.
    /// Runs after <see cref="AssignComponentNames"/>, which names the generics.
    /// </summary>
    private void ResolveComponentSources(IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        foreach (var (name, definition) in _definitions)
        {
            if (
                definition.TypeParameters.Count == 0
                && definition.Metadata?.Provenance != TsTypeProvenance.Synthetic
            )
            {
                _componentSources[definition.Metadata?.ComponentId ?? name] =
                    new ComponentSource.Definition(name);
            }
        }
        foreach (var (hash, generic) in CollectGenericInstantiations(endpoints))
        {
            _componentSources[_genericNames[hash]] = new ComponentSource.Generic(generic);
        }
        foreach (var (name, brand) in _brands)
        {
            if (brand.Metadata?.Provenance != TsTypeProvenance.Synthetic)
            {
                _componentSources[brand.Metadata?.ComponentId ?? name] = new ComponentSource.Brand(
                    name
                );
            }
        }
        foreach (var (name, enumType) in _enums)
        {
            var metadata = GetMetadata(enumType);
            if (metadata?.Provenance != TsTypeProvenance.Synthetic)
            {
                _componentSources[metadata?.ComponentId ?? name] = new ComponentSource.Enum(name);
            }
        }
    }

    private static RivetUserException UnresolvedTypeParam(
        TsType.TypeParam tp,
        string? context = null
    ) =>
        Diagnostics.Error(
            Diagnostics.UnresolvedTypeParameter,
            $"type parameter '{tp.Name}'{AtContext(context)} is never bound to a concrete type, "
                + "so there is no schema to emit for it. Use a closed generic (Wrapper<OrderDto>, not Wrapper<T>), "
                + "and give every generic instantiation one type argument per template parameter"
        );

    private static string AtContext(string? context) => context is null ? "" : $" at {context}";

    /// <summary>
    /// The component name <see cref="AssignComponentNames"/> claimed for an instantiation.
    /// The pure suffix scheme is lossy in places (4+-member unions → "Enum", 4+-field inline
    /// objects → "Object"), so distinct instantiations can share a pure name; the pre-pass
    /// gives each distinct shape a distinct deterministic one.
    /// </summary>
    private string MonomorphisedName(TsType.Generic g) =>
        _genericNames.TryGetValue(InlineTypeExtractor.CanonicalHash(g), out var assigned)
            ? assigned
            : throw new InvalidOperationException(
                $"generic '{TsType.MonomorphisedName(g)}' reached the schema without a claimed component name; "
                    + "CollectGenericInstantiations must find every instantiation the emit maps."
            );

    /// <summary>
    /// Pre-pass: walks every type reachable from endpoints and definitions and assigns each
    /// distinct generic instantiation / tagged union a unique component (base) name.
    /// Identical shapes share a name; distinct shapes whose pure names collide get a
    /// deterministic numeric suffix (discovery order).
    /// </summary>
    private void AssignComponentNames(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IEnumerable<string> preservedSchemaNames
    )
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
        var taggedUnions = roots
            .SelectMany(root => root.SelfAndDescendants())
            .OfType<TsType.TaggedUnion>()
            .ToList();

        // Names already claimed by emitted definition/brand/enum schemas (under their
        // pinned component ids too), preserved provenance schemas, and variant ids a
        // producer pinned — a synthesized name must never land on any of them.
        var usedNames = _claimedNames;
        foreach (var (name, def) in _definitions.Where(kv => kv.Value.TypeParameters.Count == 0))
        {
            usedNames.Add(name);
            usedNames.Add(def.Metadata?.ComponentId ?? name);
        }
        foreach (var (name, brand) in _brands)
        {
            usedNames.Add(name);
            usedNames.Add(brand.Metadata?.ComponentId ?? name);
        }
        foreach (var (name, enumType) in _enums)
        {
            usedNames.Add(name);
            usedNames.Add(GetMetadata(enumType)?.ComponentId ?? name);
        }
        _preservedSchemaNames.UnionWith(preservedSchemaNames);
        usedNames.UnionWith(_preservedSchemaNames);
        usedNames.UnionWith(
            taggedUnions
                .SelectMany(tu => tu.Variants)
                .Select(variant => variant.Metadata?.ComponentId)
                .OfType<string>()
        );

        // Every instantiation that becomes a component, the fixpoint's included (a
        // PagedResult<X> only Wrapper<X>'s template mentions), so a synthesized variant name
        // can never land on one of them.
        foreach (var (hash, g) in CollectGenericInstantiations(endpoints))
        {
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

    /// <summary>
    /// Every component schema: a preserved verbatim copy stands at its id in place of what
    /// the contract entry there would build, and is never built itself. Building it would
    /// leave behind what the build invents on the way, such as <c>{Base}_{Tag}</c> variants
    /// beside the <c>allOf</c> hierarchy or <c>oneOf</c> its source wrote; only the source
    /// variant components it names are built (<see cref="BuildPinnedVariants"/>).
    /// </summary>
    private JsonObject BuildSchemas(IReadOnlyList<OpenApiRawComponentProvenance> preserved)
    {
        var preservedById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var schema in preserved)
        {
            preservedById[schema.Name] = schema.Json;
        }
        var schemas = new JsonObject();
        foreach (var (componentId, source) in _componentSources)
        {
            if (preservedById.ContainsKey(componentId))
            {
                if (source is ComponentSource.Definition(var preservedName))
                {
                    BuildPinnedVariants(_definitions[preservedName]);
                }
                schemas[componentId] = null;
                continue;
            }

            schemas[componentId] = source switch
            {
                ComponentSource.Definition(var name) => BuildDefinitionSchema(_definitions[name]),
                ComponentSource.Generic(var instance) => BuildMonomorphisedSchema(
                    componentId,
                    instance
                ),
                ComponentSource.Brand(var name) => BuildBrandSchema(name),
                ComponentSource.Enum(var name) => BuildEnumSchema(name),
                _ => throw new UnreachableException($"Unknown component source {source}."),
            };
        }

        // A contract entry's slot keeps its place; the rest follow in the source's order.
        foreach (var (name, json) in preservedById)
        {
            schemas[name] = ParseSchemaObject(json, $"preserved component schema '{name}'");
        }

        return schemas;
    }

    /// <summary>
    /// Every generic instantiation that becomes a component, by canonical hash in discovery
    /// order: the ones endpoints, concrete definitions and brands use, and the ones only a
    /// template's resolved members reach. The one source for naming them
    /// (<see cref="AssignComponentNames"/>) and for building them.
    /// </summary>
    private Dictionary<string, TsType.Generic> CollectGenericInstantiations(
        IReadOnlyList<TsEndpointDefinition> endpoints
    )
    {
        var genericInstances = new Dictionary<string, TsType.Generic>(StringComparer.Ordinal);
        CollectGenericInstances(endpoints, genericInstances);

        // Templates are skipped during collection (their unresolved Generic refs are
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

            var discovered = new Dictionary<string, TsType.Generic>(StringComparer.Ordinal);
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

            foreach (var (discoveredHash, discoveredInstance) in discovered)
            {
                if (genericInstances.TryAdd(discoveredHash, discoveredInstance))
                {
                    pending.Enqueue(discoveredInstance);
                }
            }
        }

        return genericInstances;
    }

    private JsonObject BuildMonomorphisedSchema(string monoName, TsType.Generic generic)
    {
        if (!_definitions.TryGetValue(generic.Name, out var genericDef))
        {
            // A generic instantiation without its template has no properties to
            // monomorphise. A free-form stand-in would advertise "any object" for a fixed
            // wire shape, so the contract is refused, naming the producer's omission.
            throw Diagnostics.Error(
                Diagnostics.GenericTemplateMissing,
                $"generic '{generic.Name}' is instantiated as '{monoName}' "
                    + "but the contract's types carry no template definition for it. "
                    + $"Add a '{generic.Name}' definition with its typeParameters to the contract's types "
                    + "(a --from producer must ship the template of every generic it instantiates)"
            );
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
                typeName: genericDef.Name,
                additionalProperties: genericDef.AdditionalProperties
            );
        monoSchema["x-rivet-generic"] = new JsonObject
        {
            ["name"] = generic.Name,
            ["typeParams"] = StringArray(genericDef.TypeParameters),
            ["args"] = ObjectOf(
                typeParamMap.Select(kv => (kv.Key, (JsonNode?)GetCSharpTypeName(kv.Value)))
            ),
        };
        return monoSchema;
    }

    private JsonObject BuildBrandSchema(string name)
    {
        var brand = _brands[name];
        var brandSchema = MapTsTypeToJsonSchema(brand.Inner, $"brand '{name}'");
        brandSchema["x-rivet-brand"] = name;
        if (brand.Description is not null)
        {
            brandSchema["description"] = brand.Description;
        }
        if (_definitions.TryGetValue(name, out var scalarDefinition))
        {
            EnrichScalarSchema(brandSchema, scalarDefinition.ScalarMetadata);
        }
        return brandSchema;
    }

    private JsonObject BuildEnumSchema(string name)
    {
        var enumType = _enums[name];
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
        return enumSchema;
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

        return BuildObjectSchema(
            def.Properties,
            def.Description,
            def.Name,
            def.ScalarMetadata,
            def.AdditionalProperties
        );
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
            schema["examples"] = SchemaEnricher.ParseJsonArray(
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
        if (metadata.Enum is not null)
        {
            schema["enum"] = SchemaEnricher.ParseJsonArray(metadata.Enum, "schema metadata enum");
        }
        if (
            metadata.EnumListsNull
            && schema["enum"] is JsonArray members
            && !members.Any(member => member is null)
        )
        {
            members.Add(null);
        }
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
        TsScalarMetadata? metadata = null,
        bool? additionalProperties = null
    )
    {
        var properties = new JsonObject();
        var required = new List<string>();

        foreach (var prop in propertiesDefinition)
        {
            properties[prop.Name] = BuildPropertySchema(
                prop,
                typeName is null ? $"property '{prop.Name}'" : $"property '{typeName}.{prop.Name}'"
            );

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

        AddAdditionalProperties(schema, additionalProperties);
        EnrichScalarSchema(schema, metadata);

        return schema;
    }

    /// <summary>A property's schema with every annotation it carries: a record's or an inline object's.</summary>
    private JsonObject BuildPropertySchema(TsPropertyDefinition property, string? context)
    {
        var schema = MapTsTypeToJsonSchema(property.Type, context);
        SchemaEnricher.EnrichPropertySchema(schema, property);
        EnrichScalarSchema(schema, property.ScalarMetadata);
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
            // char keys: single-character property names on the wire —
            // same length-1 shape as the char property schema.
            if (p.CSharpType is "char")
            {
                schema["minLength"] = 1;
                schema["maxLength"] = 1;
            }
            if (p.CSharpType is "TimeSpan")
            {
                schema["pattern"] = TimeSpanPattern;
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
    )
    {
        if (template.TypeParameters.Count != instance.TypeArguments.Count)
        {
            throw Diagnostics.Error(
                Diagnostics.UnresolvedTypeParameter,
                $"generic '{instance.Name}' is instantiated with "
                    + $"{instance.TypeArguments.Count} type argument(s), but its template declares "
                    + $"{template.TypeParameters.Count} ({string.Join(", ", template.TypeParameters)}), "
                    + "so the arguments can't be matched to the parameters. "
                    + "Give every generic instantiation one type argument per template parameter"
            );
        }

        return template
            .TypeParameters.Zip(instance.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
    }

    private void CollectGenericInstances(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        Dictionary<string, TsType.Generic> genericInstances
    )
    {
        foreach (var (_, type) in endpoints.SelectMany(endpoint => endpoint.AllTypes()))
        {
            CollectGenericsFromType(type, genericInstances);
        }
        foreach (
            var type in _requestBodyComponentSchemas.Concat(_brands.Values.Select(b => b.Inner))
        )
        {
            CollectGenericsFromType(type, genericInstances);
        }

        // Every definition schema is emitted, so every generic it uses must be monomorphised.
        foreach (var (_, def) in _definitions)
        {
            // Skip generic TEMPLATE definitions — their Generic refs still contain
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
        foreach (var generic in type.SelfAndDescendants().OfType<TsType.Generic>())
        {
            instances.TryAdd(InlineTypeExtractor.CanonicalHash(generic), generic);
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
