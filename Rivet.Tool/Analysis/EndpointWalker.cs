using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Discovers [RivetEndpoint]-attributed methods and extracts HTTP method, route,
/// parameter bindings, and return type. Supports both minimal API (typed return)
/// and controller (ProducesResponseType + IActionResult) patterns.
/// </summary>
public static class EndpointWalker
{
    /// <summary>
    /// Discovers endpoints from [RivetEndpoint] methods and [RivetClient] classes.
    /// Use SymbolDiscovery.Discover() to obtain the method/type lists.
    /// </summary>
    public static IReadOnlyList<TsEndpointDefinition> Walk(
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        IReadOnlyList<IMethodSymbol> endpointMethods,
        IReadOnlyList<INamedTypeSymbol> clientTypes
    )
    {
        var endpoints = new List<TsEndpointDefinition>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        // [RivetEndpoint] on individual methods
        foreach (var method in endpointMethods)
        {
            if (seen.Add(method))
            {
                var endpoint = BuildEndpoint(method, wkt, typeWalker);
                if (endpoint is not null)
                {
                    endpoints.Add(endpoint);
                }
            }
        }

        // [RivetClient] on classes — all public methods with HTTP attributes
        var clientMethods = clientTypes
            .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>())
            .Where(m =>
                m.DeclaredAccessibility == Accessibility.Public
                && !m.IsImplicitlyDeclared
                && HasHttpMethodAttribute(wkt, m)
            );

        foreach (var method in clientMethods)
        {
            if (seen.Add(method))
            {
                var endpoint = BuildEndpoint(method, wkt, typeWalker);
                if (endpoint is not null)
                {
                    endpoints.Add(endpoint);
                }
            }
        }

        return endpoints;
    }

    private static TsEndpointDefinition? BuildEndpoint(
        IMethodSymbol method,
        WellKnownTypes wkt,
        TypeWalker typeWalker
    )
    {
        var (httpMethod, methodRoute) = ExtractHttpMethodAndRoute(wkt, method);
        if (httpMethod is null)
        {
            return null;
        }

        // Combine controller [Route] prefix with method route
        var controllerRoute = ExtractControllerRoute(wkt, method.ContainingType);
        var fullRoute = CombineRoutes(controllerRoute, methodRoute);

        if (fullRoute is null)
        {
            return null;
        }

        // A6: substitute [controller]/[action] tokens before constraint stripping
        fullRoute = SubstituteRouteTokens(fullRoute, method.ContainingType, method);

        // Strip route constraints: {id:guid} → {id}
        fullRoute = RouteParser.StripRouteConstraints(fullRoute);

        var parameters = ExtractParams(wkt, method, typeWalker, fullRoute);
        var responses = ExtractAllResponseTypes(wkt, method, typeWalker).ToList();
        var isVoidAction = IsVoidAction(wkt, method);
        if (responses.Count == 0)
        {
            // The annotation frontend owns its success default: MVC actually sends
            // 200 OK for non-void actions and 204 No Content for void actions —
            // regardless of the HTTP method. This is a host-truthful synthesis at
            // the extraction frontend, not a shared method-based default; explicit
            // [ProducesResponseType] metadata (responses.Count > 0) always wins.
            var defaultType = isVoidAction ? null : ExtractReturnType(wkt, method, typeWalker);
            responses.Add(
                isVoidAction
                    ? new TsResponseType(204, null, "No Content")
                    : new TsResponseType(200, defaultType)
            );
        }

        var successResponse = responses.FirstOrDefault(response =>
            response.StatusCode is >= 200 and < 300
        );
        var returnType =
            successResponse?.DataType
            ?? (responses.Count == 0 ? ExtractReturnType(wkt, method, typeWalker) : null);
        var isFormEncoded = HasFromFormBody(method, wkt);
        var requestExamples = ExtractRequestExamples(wkt, method, parameters, isFormEncoded);
        ApplyResponseExamples(
            responses,
            ExtractResponseExamples(wkt, method),
            Naming.ToCamelCase(method.Name)
        );
        var name = Naming.ToCamelCase(method.Name);
        var controllerName = DeriveControllerFileName(method.ContainingType);
        var responseContentTypeOverride = ResolveResponseContentType(
            wkt,
            method,
            successResponse?.DataType
        );

        return new TsEndpointDefinition(
            name,
            httpMethod,
            fullRoute,
            parameters,
            returnType,
            controllerName,
            responses,
            IsFormEncoded: isFormEncoded,
            RequestExamples: requestExamples,
            ResponseContentTypeOverride: responseContentTypeOverride
        );
    }

    /// <summary>
    /// True when the method's signature declares no result: void, Task or ValueTask
    /// (non-generic). Signature-based by design — a non-void action whose type maps
    /// to nothing (IActionResult, Task&lt;IActionResult&gt;) is NOT void; the host can
    /// absolutely return bodies there, so it synthesizes the 200 default.
    /// </summary>
    internal static bool IsVoidAction(WellKnownTypes wkt, IMethodSymbol method)
    {
        UnwrapTask(wkt, method.ReturnType, out var isVoidTask);
        return isVoidTask;
    }

    /// <summary>
    /// Resolves the success response's media type from statically-knowable MVC
    /// metadata: an explicit [Produces] content type (action or controller) wins;
    /// otherwise a plain string action carries MVC's text/plain formatter default;
    /// otherwise null leaves the application/json default in the emitter.
    /// </summary>
    internal static string? ResolveResponseContentType(
        WellKnownTypes wkt,
        IMethodSymbol method,
        TsType? successDataType
    )
    {
        var declared = ExtractProducesContentType(wkt, method);
        if (declared is not null)
        {
            return declared;
        }

        // MVC's string-formatting serializer writes plain string actions as
        // text/plain (matching EndpointRuntime's non-JSON-requires-string rule).
        // Only a truly plain string (no format, no CSharpType marker) qualifies —
        // byte[] (base64 string) is JSON.
        return
            successDataType is TsType.Primitive { Name: "string", Format: null, CSharpType: null }
            ? "text/plain"
            : null;
    }

    /// <summary>
    /// Reads the first content type declared via [Produces] on the action, falling
    /// back to the controller. Returns null when MVC metadata declares none.
    /// </summary>
    private static string? ExtractProducesContentType(WellKnownTypes wkt, IMethodSymbol method)
    {
        if (wkt.Produces is null)
        {
            return null;
        }

        foreach (var attr in method.GetAttributes().Concat(method.ContainingType.GetAttributes()))
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, wkt.Produces))
            {
                continue;
            }

            // ProducesAttribute's constructor is (string contentType,
            // params string[] additionalContentTypes), so even a single
            // [Produces("x")] carries two arguments (string + empty array).
            // Read the first declared content type across every argument:
            // the string positionals first, then any additional array values.
            foreach (var value in attr.ConstructorArguments)
            {
                if (value.Kind == TypedConstantKind.Array)
                {
                    foreach (var item in value.Values)
                    {
                        if (item.Value is string arrayContentType && arrayContentType.Length > 0)
                        {
                            return arrayContentType;
                        }
                    }
                }
                else if (value.Value is string contentType && contentType.Length > 0)
                {
                    return contentType;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<TsEndpointExample>? ExtractRequestExamples(
        WellKnownTypes wkt,
        IMethodSymbol method,
        IReadOnlyList<TsEndpointParam> parameters,
        bool isFormEncoded
    )
    {
        if (wkt.RivetRequestExample is null)
        {
            return null;
        }

        var examples = method
            .GetAttributes()
            .Where(attr =>
                SymbolEqualityComparer.Default.Equals(attr.AttributeClass, wkt.RivetRequestExample)
            )
            .Select(attr =>
                ToRequestExample(attr, DefaultRequestExampleMediaType(parameters, isFormEncoded))
            )
            .Where(example => example is not null)
            .Cast<TsEndpointExample>()
            .ToList();

        return examples.Count == 0 ? null : examples;
    }

    private static IReadOnlyList<PendingResponseExample> ExtractResponseExamples(
        WellKnownTypes wkt,
        IMethodSymbol method
    )
    {
        if (wkt.RivetResponseExample is null)
        {
            return [];
        }

        return method
            .GetAttributes()
            .Where(attr =>
                SymbolEqualityComparer.Default.Equals(attr.AttributeClass, wkt.RivetResponseExample)
            )
            .Select(ToPendingResponseExample)
            .Where(example => example is not null)
            .Cast<PendingResponseExample>()
            .ToList();
    }

    private static string DefaultRequestExampleMediaType(
        IReadOnlyList<TsEndpointParam> parameters,
        bool isFormEncoded
    )
    {
        if (
            parameters.Any(parameter =>
                parameter.Source is ParamSource.File or ParamSource.FormField
            )
        )
        {
            return "multipart/form-data";
        }

        return isFormEncoded ? "application/x-www-form-urlencoded" : "application/json";
    }

    private static bool HasFromFormBody(IMethodSymbol method, WellKnownTypes wkt)
    {
        return method.Parameters.Any(param =>
            param
                .GetAttributes()
                .Any(attr =>
                    attr.AttributeClass is not null
                    && SymbolEqualityComparer.Default.Equals(attr.AttributeClass, wkt.FromForm)
                    && !SymbolEqualityComparer.Default.Equals(param.Type, wkt.IFormFile)
                )
        );
    }

    private static TsEndpointExample? ToRequestExample(AttributeData attr, string defaultMediaType)
    {
        if (
            attr.ConstructorArguments.Length == 0
            || attr.ConstructorArguments[0].Value is not string json
        )
        {
            return null;
        }

        var componentExampleId = GetStringArg(attr, 1);
        return ToEndpointExample(
            defaultMediaType,
            GetStringArg(attr, 2),
            json,
            componentExampleId,
            GetStringArg(attr, 3)
        );
    }

    private static PendingResponseExample? ToPendingResponseExample(AttributeData attr)
    {
        if (
            attr.ConstructorArguments.Length < 2
            || attr.ConstructorArguments[0].Value is not int statusCode
            || attr.ConstructorArguments[1].Value is not string json
        )
        {
            return null;
        }

        return new PendingResponseExample(
            statusCode,
            GetStringArg(attr, 3),
            GetStringArg(attr, 4),
            json,
            GetStringArg(attr, 2)
        );
    }

    private static TsEndpointExample ToEndpointExample(
        string defaultMediaType,
        string? name,
        string jsonOrResolvedJson,
        string? componentExampleId,
        string? mediaType
    )
    {
        return componentExampleId is null
            ? new TsEndpointExample(mediaType ?? defaultMediaType, name, Json: jsonOrResolvedJson)
            : new TsEndpointExample(
                mediaType ?? defaultMediaType,
                name,
                ComponentExampleId: componentExampleId,
                ResolvedJson: jsonOrResolvedJson
            );
    }

    private static void ApplyResponseExamples(
        List<TsResponseType> responses,
        IReadOnlyList<PendingResponseExample> responseExamples,
        string endpointName
    )
    {
        if (responseExamples.Count == 0)
        {
            return;
        }

        foreach (var group in responseExamples.GroupBy(example => example.StatusCode))
        {
            var mappedExamples = group
                .Select(example =>
                    ToEndpointExample(
                        "application/json",
                        example.Name,
                        example.JsonOrResolvedJson,
                        example.ComponentExampleId,
                        example.MediaType
                    )
                )
                .ToList();

            var responseIndex = responses.FindIndex(response => response.StatusCode == group.Key);
            if (responseIndex < 0)
            {
                Diagnostics.Warn(
                    Diagnostics.ControllerExampleUndeclaredStatus,
                    $"ignoring response example for undeclared status {group.Key} on controller endpoint '{endpointName}'"
                );
                continue;
            }

            var response = responses[responseIndex];
            var mergedExamples = response.Examples is null
                ? mappedExamples
                : response.Examples.Concat(mappedExamples).ToList();
            responses[responseIndex] = response with { Examples = mergedExamples };
        }

        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));
    }

    private static string? GetStringArg(AttributeData attr, int index)
    {
        return attr.ConstructorArguments.Length > index
            ? attr.ConstructorArguments[index].Value as string
            : null;
    }

    private sealed record PendingResponseExample(
        int StatusCode,
        string? Name,
        string? MediaType,
        string JsonOrResolvedJson,
        string? ComponentExampleId
    );

    /// <summary>
    /// Derives a camelCase file name from the controller class.
    /// CaseStatusesController → caseStatuses, PublicFormsController → publicForms,
    /// Static class Endpoints → endpoints.
    /// </summary>
    private static string DeriveControllerFileName(INamedTypeSymbol? containingType)
    {
        if (containingType is null)
        {
            return "client";
        }

        var name = containingType.Name;

        // Strip "Controller" suffix
        if (name.EndsWith("Controller", StringComparison.Ordinal))
        {
            name = name[..^"Controller".Length];
        }

        return Naming.ToCamelCase(name);
    }

    internal static (string? HttpMethod, string? Route) ExtractHttpMethodAndRoute(
        WellKnownTypes wkt,
        IMethodSymbol method
    )
    {
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass is not { } attrClass)
            {
                continue;
            }

            if (!wkt.HttpMethodAttributes.TryGetValue(attrClass, out var httpMethod))
            {
                continue;
            }

            // Route template is the first constructor argument (if any)
            var route =
                attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value as string
                    : null;

            return (httpMethod, route);
        }

        return (null, null);
    }

    /// <summary>
    /// Reads [Route("...")] from the containing controller class.
    /// </summary>
    internal static string? ExtractControllerRoute(
        WellKnownTypes wkt,
        INamedTypeSymbol? containingType
    )
    {
        if (containingType is null)
        {
            return null;
        }

        foreach (var attr in containingType.GetAttributes())
        {
            if (
                SymbolEqualityComparer.Default.Equals(attr.AttributeClass, wkt.Route)
                && attr.ConstructorArguments.Length > 0
            )
            {
                return attr.ConstructorArguments[0].Value as string;
            }
        }

        return null;
    }

    /// <summary>
    /// A6: substitutes ASP.NET <c>[controller]</c>/<c>[action]</c> route tokens.
    /// <c>[controller]</c> resolves to the controller class name minus the
    /// "Controller" suffix; <c>[action]</c> to the action method name.
    /// Token matching is case-insensitive, matching ASP.NET conventions.
    /// </summary>
    internal static string SubstituteRouteTokens(
        string route,
        INamedTypeSymbol? containingType,
        IMethodSymbol method
    )
    {
        if (!route.Contains('['))
        {
            return route;
        }

        var controllerName = containingType?.Name ?? "";
        if (controllerName.EndsWith("Controller", StringComparison.Ordinal))
        {
            controllerName = controllerName[..^"Controller".Length];
        }

        return route
            .Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", method.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Combines controller route prefix with method route segment.
    /// e.g. "api/case-statuses" + "{id:guid}" → "/api/case-statuses/{id:guid}"
    /// </summary>
    internal static string? CombineRoutes(string? controllerRoute, string? methodRoute)
    {
        // If method route starts with / it's absolute — use as-is
        if (methodRoute is not null && methodRoute.StartsWith('/'))
        {
            return methodRoute;
        }

        if (controllerRoute is null && methodRoute is null)
        {
            return null;
        }

        var prefix = string.IsNullOrEmpty(controllerRoute)
            ? null
            : controllerRoute.TrimStart('/').TrimEnd('/');
        var suffix = string.IsNullOrEmpty(methodRoute)
            ? null
            : methodRoute.TrimStart('/').TrimEnd('/');

        var combined = (prefix, suffix) switch
        {
            (not null, not null) => $"/{prefix}/{suffix}",
            (not null, null) => $"/{prefix}",
            (null, not null) => $"/{suffix}",
            _ => null,
        };

        return combined;
    }

    internal static IReadOnlyList<TsEndpointParam> ExtractParams(
        WellKnownTypes wkt,
        IMethodSymbol method,
        TypeWalker typeWalker,
        string? routeTemplate
    )
    {
        // Extract route param names from the template for implicit classification
        var routeParamNames = routeTemplate is not null
            ? RouteParser.ParseRouteParamNames(routeTemplate)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var parameters = new List<TsEndpointParam>();
        // MVC rejects multiple body-bound parameters on one action; extraction must
        // not confidently emit a body surface MVC cannot execute
        // (planner-constraint:multi-body-diagnostic-production). Any Body-classified
        // source occupies the single body slot — explicit [FromBody]/[FromForm] body
        // as well as the inferred complex type.
        var bodySeen = false;

        // Pre-scan: if any parameter is IFormFile (or a collection of IFormFile,
        // FABLE_GAPS §7 item 12), non-route/non-file params become FormField
        var hasFileParam =
            wkt.IFormFile is not null
            && method.Parameters.Any(p =>
                SymbolEqualityComparer.Default.Equals(p.Type, wkt.IFormFile)
                || typeWalker.IsCollectionOf(p.Type, wkt.IFormFile)
            );

        foreach (var param in method.Parameters)
        {
            // A10: [FromServices] params are DI plumbing — excluded from the contract entirely.
            if (HasAttribute(param, wkt.FromServices))
            {
                continue;
            }

            // CancellationToken is host plumbing, never user input. It is excluded
            // BEFORE classification: unattributed, the struct would otherwise reach
            // the complex-body inference and be fabricated into a ("ct", Body)
            // request surface MVC never binds. Interfaces and other unclassified
            // params must NOT take this silent path — they keep flowing into
            // ClassifyParam so IsHostPlumbingType can warn-and-exclude (RIV1100).
            //
            // The identification is structural and reference-set-robust on purpose:
            // WellKnownTypes.CancellationToken resolves via GetTypeByMetadataName,
            // which returns null whenever more than one referenced assembly
            // declares System.Threading.CancellationToken (the loose-file CLI
            // references the whole NETCore.App framework, where System.Runtime and
            // System.Private.CoreLib both carry the struct). A symbol-equality
            // guard would then be inert and ct would be fabricated into a body —
            // so both guard sites identify the struct by name/namespace/shape.
            if (IsCancellationToken(param.Type))
            {
                continue;
            }

            // P2 wave 5 (retires RIV1005): [FromHeader] maps to ParamSource.Header. The
            // attribute's Name property keeps the wire casing ("X-Api-Key"); without one
            // the C# parameter name is the header name.
            if (HasAttribute(param, wkt.FromHeader))
            {
                parameters.Add(
                    new TsEndpointParam(
                        GetFromHeaderName(param, wkt) ?? param.Name,
                        typeWalker.MapType(param.Type),
                        ParamSource.Header,
                        IsOptional: param.HasExplicitDefaultValue,
                        DefaultValue: GetDefaultValueLiteral(param)
                    )
                );
                continue;
            }

            var source = ClassifyParam(wkt, typeWalker, param, routeParamNames);
            if (source is null)
            {
                // Skip infrastructure types (CancellationToken, DI services, etc.)
                if (IsInfrastructureType(wkt, param.Type))
                {
                    continue;
                }

                // In mixed upload methods, unclassified params are form fields.
                // FromForm(Name=) / plain form-field wire names must match the actual
                // request field (planner-constraint:fromform-name-binding).
                if (hasFileParam)
                {
                    parameters.Add(
                        new TsEndpointParam(
                            GetBindingName(param, FormFieldSources(wkt)) ?? param.Name,
                            typeWalker.MapType(param.Type),
                            ParamSource.FormField,
                            IsOptional: param.HasExplicitDefaultValue,
                            DefaultValue: GetDefaultValueLiteral(param)
                        )
                    );
                }

                continue;
            }

            // A [FromForm] scalar beside an IFormFile belongs to the multipart form —
            // MVC never binds it as a second JSON body. Its Name= (or parameter name)
            // is the form-field key (planner-constraint:fromform-name-binding).
            if (hasFileParam && source == ParamSource.Body && HasAttribute(param, wkt.FromForm))
            {
                parameters.Add(
                    new TsEndpointParam(
                        GetBindingName(param, FormFieldSources(wkt)) ?? param.Name,
                        typeWalker.MapType(param.Type),
                        ParamSource.FormField,
                        IsOptional: param.HasExplicitDefaultValue,
                        DefaultValue: GetDefaultValueLiteral(param)
                    )
                );
                continue;
            }

            // Without an explicit attribute, an unattributed scalar beside an IFormFile
            // binds from the multipart form — MVC's inference for non-IFormFile params
            // in a multipart action is form data, not the query string. An explicit
            // [FromQuery] keeps its query binding (explicit attributes take precedence);
            // reclassified fields keep the FormFieldSources Name= path
            // (planner-constraint:mixed-upload-explicit-query-precedence,
            // planner-constraint:mixed-upload-formfield-ordering).
            if (hasFileParam && source == ParamSource.Query && !HasAttribute(param, wkt.FromQuery))
            {
                parameters.Add(
                    new TsEndpointParam(
                        GetBindingName(param, FormFieldSources(wkt)) ?? param.Name,
                        typeWalker.MapType(param.Type),
                        ParamSource.FormField,
                        IsOptional: param.HasExplicitDefaultValue,
                        DefaultValue: GetDefaultValueLiteral(param)
                    )
                );
                continue;
            }

            // IFormFile maps to the Web API File type — don't walk it through Roslyn.
            // Collection-of-IFormFile params emit array-of-binary (FABLE_GAPS §7 item 12).
            var tsType =
                source == ParamSource.File
                    ? typeWalker.IsCollectionOf(param.Type, wkt.IFormFile)
                        ? new TsType.Array(new TsType.Primitive("File"))
                        : (TsType)new TsType.Primitive("File")
                    : typeWalker.MapType(param.Type);
            // E8: a C# default value makes the param optional on the wire.
            // FromQuery(Name=)/FromRoute(Name=)/FromForm(Name=) rename the wire surface
            // to match the actual request key / route placeholder (IModelNameProvider).
            var wireName = GetBindingName(param, WireNamedSources(wkt)) ?? param.Name;
            // A second Body-classified parameter cannot share the single body slot:
            // report it (RIV1100) and exclude it rather than silently emitting a
            // contract MVC itself would reject at startup.
            if (bodySeen && source == ParamSource.Body)
            {
                WarnUnresolvedBinding(param, "a second request body");
                continue;
            }
            parameters.Add(
                new TsEndpointParam(
                    wireName,
                    tsType,
                    source.Value,
                    IsOptional: param.HasExplicitDefaultValue,
                    DefaultValue: GetDefaultValueLiteral(param)
                )
            );
            if (source == ParamSource.Body)
            {
                bodySeen = true;
            }
        }

        return parameters;
    }

    /// <summary>
    /// Binding sources whose attribute exposes a Name (IModelNameProvider semantics):
    /// FromQuery, FromRoute, FromForm and FromHeader all carry a wire-name override.
    /// </summary>
    private static IReadOnlyList<INamedTypeSymbol?> WireNamedSources(WellKnownTypes wkt) =>
        [wkt.FromQuery, wkt.FromRoute, wkt.FromForm, wkt.FromHeader];

    /// <summary>
    /// Sources eligible for the mixed-upload FormField branch: only [FromForm] names a
    /// form field explicitly; the others never apply to a form field.
    /// </summary>
    private static IReadOnlyList<INamedTypeSymbol?> FormFieldSources(WellKnownTypes wkt) =>
        [wkt.FromForm];

    /// <summary>
    /// The Name= named argument on a binding attribute (FromQuery/FromRoute/FromForm/
    /// FromHeader), or null. Route names additionally must agree with the route
    /// template — an explicit name wins because MVC binds route data by it.
    /// </summary>
    private static string? GetBindingName(
        IParameterSymbol param,
        IReadOnlyList<INamedTypeSymbol?> sources
    )
    {
        foreach (var attr in param.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null)
            {
                continue;
            }

            foreach (var source in sources)
            {
                if (source is null || !SymbolEqualityComparer.Default.Equals(attrClass, source))
                {
                    continue;
                }

                var named = attr.NamedArguments.FirstOrDefault(kv => kv.Key == "Name");
                if (named.Value.Value is string name && !string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static bool HasAttribute(IParameterSymbol param, INamedTypeSymbol? attributeType) =>
        attributeType is not null
        && param
            .GetAttributes()
            .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));

    /// <summary>
    /// The literal source text of a parameter's C# default value (for example "20"
    /// for `int limit = 20`, "default" for a default literal), or null. E8: surfaced
    /// on the param so emitters can publish schema.default alongside IsOptional —
    /// the contract frontend already flows [RivetDefault] through the same field.
    /// </summary>
    private static string? GetDefaultValueLiteral(IParameterSymbol param)
    {
        if (!param.HasExplicitDefaultValue)
        {
            return null;
        }

        foreach (var reference in param.DeclaringSyntaxReferences)
        {
            var node = reference.GetSyntax();
            var parameterSyntax =
                node as ParameterSyntax
                ?? node.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault();
            var clause = parameterSyntax?.Default;
            if (clause?.Value is { } value)
            {
                return value.ToString();
            }
        }

        return null;
    }

    /// <summary>The [FromHeader(Name = "...")] value, or null when unset.</summary>
    private static string? GetFromHeaderName(IParameterSymbol param, WellKnownTypes wkt)
    {
        var attr = param
            .GetAttributes()
            .FirstOrDefault(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, wkt.FromHeader)
            );

        var named = attr?.NamedArguments.FirstOrDefault(kv => kv.Key == "Name");
        return named?.Value.Value as string;
    }

    /// <summary>
    /// True for System.Threading.CancellationToken regardless of which referenced
    /// assembly supplied the symbol. WellKnownTypes.CancellationToken goes through
    /// GetTypeByMetadataName, which returns null when two referenced assemblies
    /// declare the same full name (System.Runtime.dll and System.Private.CoreLib.dll
    /// both do) — so the ct guards must not rely on symbol equality.
    /// </summary>
    private static bool IsCancellationToken(ITypeSymbol type)
    {
        return type is INamedTypeSymbol named
            && named.TypeKind == TypeKind.Struct
            && named.Name == "CancellationToken"
            && named.ContainingNamespace?.ToDisplayString() == "System.Threading";
    }

    private static bool IsInfrastructureType(WellKnownTypes wkt, ITypeSymbol type)
    {
        // Structural ct identification (see ExtractParams): symbol equality via
        // WellKnownTypes is inert under an ambiguous reference set.
        if (IsCancellationToken(type))
        {
            return true;
        }

        // Interface types without [From*] attributes are DI services (but not IFormFile)
        return type.TypeKind == TypeKind.Interface
            && !SymbolEqualityComparer.Default.Equals(type, wkt.IFormFile);
    }

    private static ParamSource? ClassifyParam(
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        IParameterSymbol param,
        HashSet<string> routeParamNames
    )
    {
        // IFormFile parameter (single or collection, FABLE_GAPS §7 item 12) → File
        // source (before attribute check — IFormFile is the signal)
        if (
            SymbolEqualityComparer.Default.Equals(param.Type, wkt.IFormFile)
            || typeWalker.IsCollectionOf(param.Type, wkt.IFormFile)
        )
        {
            return ParamSource.File;
        }

        // Explicit attribute takes precedence
        foreach (var attr in param.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromBody))
            {
                return ParamSource.Body;
            }

            if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromForm))
            {
                return ParamSource.Body;
            }

            if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromQuery))
            {
                return ParamSource.Query;
            }

            if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromRoute))
            {
                return ParamSource.Route;
            }
        }

        // Implicit: param name matches a route template segment
        if (routeParamNames.Contains(param.Name))
        {
            return ParamSource.Route;
        }

        // MVC default inference (planner-constraint:mvc-default-inference-boundary):
        // the boundary is pinned to observed real binding in the MVC host fixture —
        // scalar/simple types bind to Query, declared complex types bind to the JSON
        // body. Host plumbing and anything unsupported by static host metadata must
        // never be confidently invented into a source.
        if (IsHostPlumbingType(param.Type))
        {
            WarnUnresolvedBinding(param, "host plumbing type");
            return null;
        }

        if (typeWalker.IsScalarQueryType(param.Type))
        {
            return ParamSource.Query;
        }

        if (IsComplexBodyCandidate(param.Type))
        {
            return ParamSource.Body;
        }

        // Unresolvable from static host metadata — report loudly, exclude the input.
        WarnUnresolvedBinding(param, "no supported binding source");
        return null;
    }

    /// <summary>
    /// Types whose MVC binding cannot be established from supported static host
    /// metadata: interfaces (DI services without [FromServices]) and the known host
    /// plumbing classes (HttpContext/HttpRequest/HttpResponse/ClaimsPrincipal-like).
    /// These are deliberately NOT invented into a Query or Body source.
    /// </summary>
    private static bool IsHostPlumbingType(ITypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Interface or TypeKind.TypeParameter)
        {
            return true;
        }

        var name = type.Name;
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns is null)
        {
            return false;
        }

        // HttpContext and friends (Microsoft.AspNetCore.*), ClaimsPrincipal, and
        // service-provider plumbing are runtime host state, not user input.
        if (ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
        {
            return name
                is "HttpContext"
                    or "HttpRequest"
                    or "HttpResponse"
                    or "ClaimsPrincipal"
                    or "ClaimsIdentity"
                    or "RouteData"
                    or "ActionContext"
                    or "ActionExecutingContext"
                    or "ControllerBase"
                    or "Controller";
        }

        if (ns is "Microsoft.Extensions.DependencyInjection" or "System.ServiceModel")
        {
            return name is "IServiceProvider" or "ServiceProvider" or "ServiceContainer";
        }

        return false;
    }

    /// <summary>
    /// A declared complex type binds to the request body under MVC's default inference.
    /// Everything that is not a scalar/collection-of-scalar and not host plumbing is a
    /// candidate — including unattributed class/record/struct DTOs.
    /// </summary>
    private static bool IsComplexBodyCandidate(ITypeSymbol type)
    {
        return type switch
        {
            IArrayTypeSymbol => false,
            INamedTypeSymbol named when named.TypeKind is TypeKind.Struct or TypeKind.Class => true,
            _ => false,
        };
    }

    private static void WarnUnresolvedBinding(IParameterSymbol param, string cause)
    {
        var endpoint = param.ContainingSymbol is IMethodSymbol method
            ? method.Name
            : param.ContainingSymbol?.Name ?? "<unknown>";
        var controller = param.ContainingSymbol?.ContainingType?.Name ?? "<unknown>";
        Diagnostics.Warn(
            Diagnostics.UnresolvedBindingSource,
            $"parameter '{param.Name}' of type '{param.Type.ToDisplayString()}' on endpoint "
                + $"'{controller}.{endpoint}' has {cause} — the input cannot be bound from static host "
                + "metadata and is EXCLUDED from the contract. Add an explicit binding "
                + "([FromQuery]/[FromBody]/[FromRoute]/[FromHeader]) or [FromServices] for DI plumbing."
        );
    }

    /// <summary>
    /// Maps an ASP.NET typed result type (e.g. Ok&lt;T&gt;, NotFound) to its HTTP status code
    /// and optional body type.
    /// </summary>
    private static (int StatusCode, ITypeSymbol? BodyType)? MapTypedResult(
        WellKnownTypes wkt,
        INamedTypeSymbol type
    )
    {
        if (!wkt.TypedResultStatusCodes.TryGetValue(type.OriginalDefinition, out var statusCode))
        {
            return null;
        }

        var bodyType = type.TypeArguments.Length > 0 ? type.TypeArguments[0] : null;
        return (statusCode, bodyType);
    }

    /// <summary>
    /// Unwraps Task&lt;T&gt; / ValueTask&lt;T&gt; from a return type.
    /// Returns the inner type, or null if it's a non-generic Task/ValueTask/void.
    /// Returns the type unchanged if it's not a task wrapper.
    /// The out parameter isVoidTask is true when the return type is Task or ValueTask (no result).
    /// </summary>
    private static ITypeSymbol? UnwrapTask(
        WellKnownTypes wkt,
        ITypeSymbol returnType,
        out bool isVoidTask
    )
    {
        isVoidTask = false;

        if (returnType is INamedTypeSymbol namedReturn)
        {
            var original = namedReturn.OriginalDefinition;
            if (
                SymbolEqualityComparer.Default.Equals(original, wkt.TaskOfT)
                || SymbolEqualityComparer.Default.Equals(original, wkt.ValueTaskOfT)
            )
            {
                return namedReturn.TypeArguments[0];
            }

            if (
                SymbolEqualityComparer.Default.Equals(original, wkt.Task)
                || SymbolEqualityComparer.Default.Equals(original, wkt.ValueTask)
            )
            {
                isVoidTask = true;
                return null;
            }
        }

        if (returnType.SpecialType == SpecialType.System_Void)
        {
            isVoidTask = true;
            return null;
        }

        return returnType;
    }

    /// <summary>
    /// Checks if a type is Results&lt;T1, T2, ...&gt; (arity 2-6).
    /// </summary>
    private static bool IsTypedResults(WellKnownTypes wkt, INamedTypeSymbol type)
    {
        return type.TypeArguments.Length >= 2
            && wkt.ResultsArities.Contains(type.OriginalDefinition);
    }

    /// <summary>
    /// Collects typed result mappings from a type that is either Results&lt;T1, T2, ...&gt;
    /// or a single typed result (e.g. Ok&lt;T&gt;). Returns an empty list if the type is neither.
    /// </summary>
    private static List<(int StatusCode, ITypeSymbol? BodyType)> CollectTypedResultMappings(
        WellKnownTypes wkt,
        INamedTypeSymbol type,
        string? warnContext = null
    )
    {
        var results = new List<(int StatusCode, ITypeSymbol? BodyType)>();

        if (IsTypedResults(wkt, type))
        {
            foreach (var arg in type.TypeArguments)
            {
                if (arg is INamedTypeSymbol resultArg)
                {
                    var mapped = MapTypedResult(wkt, resultArg);
                    if (mapped is not null)
                    {
                        results.Add(mapped.Value);
                    }
                    else if (warnContext is not null)
                    {
                        // A8: an unmapped Results<> branch must never vanish silently —
                        // the contract would advertise fewer responses than the handler returns
                        Diagnostics.Warn(
                            Diagnostics.UnmappedTypedResult,
                            $"unmapped typed result '{resultArg.Name}' in Results<...> on '{warnContext}' — "
                                + "this response branch is omitted from the contract. Add a mapping or use a supported typed result."
                        );
                    }
                }
            }
        }
        else
        {
            var mapped = MapTypedResult(wkt, type);
            if (mapped is not null)
            {
                results.Add(mapped.Value);
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts return type. Tries ProducesResponseType(typeof(T), 200) first (controllers),
    /// then falls back to method return type (minimal API).
    /// </summary>
    internal static TsType? ExtractReturnType(
        WellKnownTypes wkt,
        IMethodSymbol method,
        TypeWalker typeWalker
    )
    {
        // Try ProducesResponseType first (controller pattern)
        var producesType = ExtractProducesResponseType(wkt, method);
        if (producesType is not null)
        {
            return typeWalker.MapType(producesType);
        }

        // Fall back to method return type (minimal API pattern)
        var unwrapped = UnwrapTask(wkt, method.ReturnType, out var isVoidTask);
        if (isVoidTask || unwrapped is null)
        {
            return null;
        }

        // Check for typed results (Results<T1, T2, ...> or single e.g. Ok<T>)
        if (unwrapped is INamedTypeSymbol namedUnwrapped)
        {
            var resultMappings = CollectTypedResultMappings(wkt, namedUnwrapped);
            if (resultMappings.Count > 0)
            {
                // Prefer 2xx with body over 2xx without — order in Results<> shouldn't matter
                var successWithBody = resultMappings.FirstOrDefault(m =>
                    m.StatusCode is >= 200 and < 300 && m.BodyType is not null
                );

                return successWithBody.BodyType is not null
                    ? typeWalker.MapType(successWithBody.BodyType)
                    : null;
            }
        }

        // Unwrap ActionResult<T> → T
        if (
            unwrapped is INamedTypeSymbol actionResult
            && SymbolEqualityComparer.Default.Equals(
                actionResult.OriginalDefinition,
                wkt.ActionResultOfT
            )
        )
        {
            return typeWalker.MapType(actionResult.TypeArguments[0]);
        }

        // If it's IActionResult or non-generic ActionResult, we can't infer the type — skip
        if (
            SymbolEqualityComparer.Default.Equals(unwrapped, wkt.IActionResult)
            || SymbolEqualityComparer.Default.Equals(unwrapped, wkt.ActionResult)
        )
        {
            return null;
        }

        return typeWalker.MapType(unwrapped);
    }

    /// <summary>
    /// Finds [ProducesResponseType(typeof(T), 200)] on the method and returns T.
    /// Only considers 2xx status codes as the success response type.
    /// </summary>
    private static ITypeSymbol? ExtractProducesResponseType(
        WellKnownTypes wkt,
        IMethodSymbol method
    )
    {
        foreach (var attr in method.GetAttributes())
        {
            var parsed = ReadProducesResponseType(wkt, attr);
            if (parsed is { Type: not null, StatusCode: >= 200 and < 300 })
            {
                return parsed.Value.Type;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a [ProducesResponseType(typeof(T), code)] / [ProducesResponseType(code)] /
    /// generic [ProducesResponseType&lt;T&gt;(code)] (A7 — .NET 7+) attribute.
    /// Returns null when the attribute is neither form.
    /// </summary>
    private static (ITypeSymbol? Type, int? StatusCode)? ReadProducesResponseType(
        WellKnownTypes wkt,
        AttributeData attr
    )
    {
        var attrClass = attr.AttributeClass;
        if (attrClass is null)
        {
            return null;
        }

        // A7: generic ProducesResponseTypeAttribute`1 is a distinct symbol — the body
        // type rides on the attribute class's type argument, the status on ctor arg 0
        if (
            wkt.ProducesResponseTypeOfT is not null
            && SymbolEqualityComparer.Default.Equals(
                attrClass.OriginalDefinition,
                wkt.ProducesResponseTypeOfT
            )
        )
        {
            var type = attrClass.TypeArguments.Length == 1 ? attrClass.TypeArguments[0] : null;
            int? status =
                attr.ConstructorArguments.Length >= 1
                && attr.ConstructorArguments[0].Value is int genericCode
                    ? genericCode
                    : null;
            return (type, status);
        }

        if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.ProducesResponseType))
        {
            // ProducesResponseType(typeof(T), statusCode)
            if (
                attr.ConstructorArguments.Length >= 2
                && attr.ConstructorArguments[0].Value is ITypeSymbol typeArg
                && attr.ConstructorArguments[1].Value is int statusCode
            )
            {
                return (typeArg, statusCode);
            }

            // ProducesResponseType(statusCode) — no body
            if (
                attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is int codeOnly
            )
            {
                return (null, codeOnly);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts all [ProducesResponseType] attributes as typed responses.
    /// Falls back to Results&lt;T1, T2, ...&gt; or single typed results when no attributes are present.
    /// </summary>
    internal static IReadOnlyList<TsResponseType> ExtractAllResponseTypes(
        WellKnownTypes wkt,
        IMethodSymbol method,
        TypeWalker typeWalker,
        bool normalize = true
    )
    {
        var responses = new List<TsResponseType>();

        foreach (var attr in method.GetAttributes())
        {
            // A7: covers the classic and the generic [ProducesResponseType<T>] forms
            var parsed = ReadProducesResponseType(wkt, attr);
            if (parsed is not { StatusCode: int statusCode })
            {
                continue;
            }

            // typeof(void) as a declared response type means "no body": System.Text.Json
            // never serializes a void payload, so mapping it to a schema would advertise
            // a body the host cannot send (a false declaration on 204/205/304 and on
            // no-body 2xx alike). Keep the response, drop the DataType.
            var isVoidResponse =
                parsed.Value.Type is INamedTypeSymbol voidType
                && voidType.SpecialType == SpecialType.System_Void;
            var tsType =
                parsed.Value.Type is not null && !isVoidResponse
                    ? typeWalker.MapType(parsed.Value.Type)
                    : null;
            responses.Add(new TsResponseType(statusCode, tsType));
        }

        // If [ProducesResponseType] attributes exist but none are 2xx, synthesize the
        // success response from ActionResult<T> so the discriminated union has a success branch.
        if (responses.Count > 0 && !responses.Any(r => r.StatusCode is >= 200 and < 300))
        {
            var unwrapped = UnwrapTask(wkt, method.ReturnType, out _);
            if (
                unwrapped is INamedTypeSymbol actionResult
                && SymbolEqualityComparer.Default.Equals(
                    actionResult.OriginalDefinition,
                    wkt.ActionResultOfT
                )
            )
            {
                var tsType = typeWalker.MapType(actionResult.TypeArguments[0]);
                responses.Insert(0, new TsResponseType(200, tsType));
            }
        }

        // If no [ProducesResponseType] found, try typed results from return type
        if (responses.Count == 0)
        {
            var unwrapped = UnwrapTask(wkt, method.ReturnType, out _);
            if (unwrapped is INamedTypeSymbol namedType)
            {
                // A8: warn loudly for unmapped Results<> branches (only on this path so
                // the warning is emitted once per endpoint)
                foreach (var mapping in CollectTypedResultMappings(wkt, namedType, method.Name))
                {
                    var tsType = mapping.BodyType is not null
                        ? typeWalker.MapType(mapping.BodyType)
                        : null;
                    responses.Add(new TsResponseType(mapping.StatusCode, tsType));
                }

                // W2: a bare ActionResult<T> (no [ProducesResponseType], not a typed result)
                // implies a 200/T success response. Producing no response here used to let
                // the emitter's void default kick in — 204 No Content, with T silently dropped.
                if (
                    responses.Count == 0
                    && SymbolEqualityComparer.Default.Equals(
                        namedType.OriginalDefinition,
                        wkt.ActionResultOfT
                    )
                )
                {
                    responses.Add(
                        new TsResponseType(200, typeWalker.MapType(namedType.TypeArguments[0]))
                    );
                }
            }
        }

        if (normalize)
        {
            responses = ResponseStatusValidation.NormalizeIrKeepingFirst(
                responses,
                Naming.ToCamelCase(method.Name)
            );
        }

        return responses;
    }

    internal static bool HasHttpMethodAttribute(WellKnownTypes wkt, IMethodSymbol method) =>
        method
            .GetAttributes()
            .Any(a =>
                a.AttributeClass is not null
                && wkt.HttpMethodAttributes.ContainsKey(a.AttributeClass)
            );
}
