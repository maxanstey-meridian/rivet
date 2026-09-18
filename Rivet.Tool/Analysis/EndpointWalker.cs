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
        if (responses.Count == 0)
        {
            // No synthesis: Rivet does not guess MVC's runtime result selection. An
            // action with no declared success response is an incomplete contract —
            // refuse rather than fabricate 204/200 (acceptance:no-synthetic-void-or-
            // iactionresult-success). The retained narrow convenience: a genuinely
            // concrete payload return after Task/ValueTask unwrapping may represent
            // the 200 payload; ambiguous result containers (IActionResult,
            // ActionResult, ActionResult<T>, IResult, variable-status typed
            // results) and void/non-generic Task refuse (planner-constraint:concrete-return-convenience-narrowed).
            var unwrapped = UnwrapTask(wkt, method.ReturnType, out _);
            if (unwrapped is null || IsStatusSelectingResultContainer(wkt, unwrapped))
            {
                throw new ContractAnalysisException(
                    $"error {Diagnostics.UnmappedTypedResult}: endpoint "
                        + $"'{MethodOwner(method)}.{method.Name}' declares no success response. "
                        + "Rivet reads explicit response declarations, not MVC runtime defaults — "
                        + "add [ProducesResponseType(typeof(T), 200)] (or a concrete payload return / "
                        + "a fixed-status typed result) to declare the success response."
                );
            }

            responses.Add(new TsResponseType(200, ExtractReturnType(wkt, method, typeWalker)));
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
    /// True for result containers whose runtime value selects the response's status
    /// and/or shape: any type assignable to IActionResult/IResult whose status is not
    /// statically encoded. Detection is the declared interface boundary — not a list of
    /// concrete result classes — so framework types (ContentHttpResult,
    /// RedirectHttpResult, StatusCodeHttpResult, variable-status typed results) and
    /// user-defined IResult/IActionResult implementations are all covered. Genuinely
    /// fixed-status typed results (the TypedResultStatusCodes table) and Results&lt;T1,..&gt;
    /// arities are excluded: their status/branches are read statically elsewhere, and
    /// Results&lt;...&gt; unmapped branches refuse with the specific unmapped-result
    /// diagnostic rather than this generic container refusal
    /// (planner-constraint:container-guard-excludes-fixed-and-results).
    /// </summary>
    internal static bool IsStatusSelectingResultContainer(WellKnownTypes wkt, ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        var original = named.OriginalDefinition;

        // Fixed-status typed results keep their mappings — read before any container check.
        if (wkt.TypedResultStatusCodes.ContainsKey(original))
        {
            return false;
        }

        // Results<T1, ...> is validated branch-by-branch by CollectTypedResultMappings;
        // an unmapped branch must refuse with RIV1006, not the container refusal.
        if (wkt.ResultsArities.Contains(original))
        {
            return false;
        }

        // The exact MVC wrapper forms keep their existing recognition.
        if (
            SymbolEqualityComparer.Default.Equals(original, wkt.IActionResult)
            || SymbolEqualityComparer.Default.Equals(original, wkt.ActionResult)
            || SymbolEqualityComparer.Default.Equals(original, wkt.ActionResultOfT)
        )
        {
            return true;
        }

        // Declared-type check: assignable to IResult or IActionResult means the runtime
        // value selects status/content. IResult itself matches by exact type (it is an
        // interface); implementations match through their interface list.
        if (
            wkt.IResult is not null
            && (
                SymbolEqualityComparer.Default.Equals(original, wkt.IResult)
                || named.AllInterfaces.Any(interfaceType =>
                    SymbolEqualityComparer.Default.Equals(interfaceType, wkt.IResult)
                )
            )
        )
        {
            return true;
        }

        return wkt.IActionResult is not null
            && named.AllInterfaces.Any(interfaceType =>
                SymbolEqualityComparer.Default.Equals(interfaceType, wkt.IActionResult)
            );
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
        // not confidently emit a body surface MVC cannot execute. Any Body-classified
        // source occupies the single body slot.
        var bodySeen = false;
        var fileSeen = false;

        foreach (var param in method.Parameters)
        {
            // A10: [FromServices] params are DI plumbing — excluded from the contract entirely.
            if (HasAttribute(param, wkt.FromServices))
            {
                continue;
            }

            // CancellationToken is host plumbing, never user input. It is excluded
            // BEFORE classification: an unattributed struct has no binding declaration,
            // and the extraction refuses unknown transport sources rather than guessing.
            // Interfaces and other unclassified params keep flowing into ClassifyParam
            // so they refuse with the unresolved-binding diagnostic (RIV1100) instead of
            // being silently dropped or fabricated into a body
            // (acceptance:no-neighbour-inference-regression).
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

            // [FromHeader] flows through ClassifyParam like every other explicit
            // source so contradictory declarations refuse with one diagnostic
            // (planner-constraint:fromheader-joins-contradiction-validation). The
            // attribute's Name property keeps the wire casing ("X-Api-Key") via the
            // shared GetBindingName path below; without one the C# parameter name
            // is the header name.

            var source =
                ClassifyParam(wkt, typeWalker, param, routeParamNames)
                ?? ThrowUnresolvedBinding(method, param);

            // A Route-bound parameter's effective wire name (explicit [FromRoute(Name =
            // "...")] or the parameter name) must name a placeholder the resolved route
            // template actually contains — otherwise the emitted path parameter would be
            // one the route never supplies
            // (acceptance:explicit-route-name-must-match-template). The plain wire-name
            // fix (Name= wins over the parameter name) is preserved for valid names; the
            // convention path matches a template token by construction, so it never
            // trips this check.
            if (source == ParamSource.Route)
            {
                var explicitRouteName = GetBindingName(param, WireNamedSources(wkt));
                var effectiveRouteName = explicitRouteName ?? param.Name;
                var declaration = explicitRouteName is null
                    ? "[FromRoute]"
                    : $"[FromRoute(Name = \"{explicitRouteName}\")]";
                if (
                    !routeParamNames.Contains(effectiveRouteName)
                    && !routeParamNames
                        .Select(RouteParser.NormalizeForMatching)
                        .Contains(RouteParser.NormalizeForMatching(effectiveRouteName))
                )
                {
                    throw new ContractAnalysisException(
                        $"error {Diagnostics.UnresolvedBindingSource}: parameter '{param.Name}' on endpoint "
                            + $"'{MethodOwner(method)}.{method.Name}' is {declaration} but route '{routeTemplate}' "
                            + $"contains no '{{{effectiveRouteName}}}' placeholder — the route binds by its "
                            + "template. Align the binding name with a route placeholder (e.g. {{id}})."
                    );
                }
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
            // report it and refuse rather than silently emitting a contract MVC
            // itself would reject at startup.
            if (bodySeen && source == ParamSource.Body)
            {
                throw new ContractAnalysisException(
                    $"error {Diagnostics.UnresolvedBindingSource}: endpoint "
                        + $"'{MethodOwner(method)}.{method.Name}' declares a second request body — "
                        + $"parameter '{param.Name}' of type '{param.Type.ToDisplayString()}' cannot share the "
                        + "single body slot with an earlier body-bound parameter. MVC itself rejects this at "
                        + "startup; declare one [FromBody] (or an explicit form surface with IFormFile fields)."
                );
            }
            parameters.Add(
                new TsEndpointParam(
                    wireName,
                    tsType,
                    source,
                    IsOptional: param.HasExplicitDefaultValue,
                    DefaultValue: GetDefaultValueLiteral(param)
                )
            );
            if (source == ParamSource.Body)
            {
                bodySeen = true;
            }

            if (source == ParamSource.File)
            {
                fileSeen = true;
            }
        }

        // Mixed form/file: a Body-classified parameter ([FromForm] DTO or [FromBody])
        // beside an IFormFile parameter would make the multipart body carry a recursive
        // form object — faithfully lowering it requires emulating MVC's recursive form
        // binder, so refuse instead of silently dropping the explicitly declared
        // parameter (acceptance:mixed-form-file-never-drops-explicit-input). Scalar
        // [FromForm] fields beside files keep emitting both parts.
        if (fileSeen && bodySeen)
        {
            var formBodyParam = method.Parameters.First(param =>
                !SymbolEqualityComparer.Default.Equals(param.Type, wkt.IFormFile)
                && !typeWalker.IsCollectionOf(param.Type, wkt.IFormFile)
                && ClassifyParam(wkt, typeWalker, param, routeParamNames) is ParamSource.Body
            );

            // The remedy depends on what the body declares: a [FromForm] DTO can split
            // into scalar form fields, a [FromBody] JSON body has no form representation
            // at all and must move off the multipart endpoint.
            var remedy = HasAttribute(formBodyParam, wkt.FromBody)
                ? "a [FromBody] body cannot ride a multipart request — move the JSON body to a "
                    + "separate endpoint, or drop the file parameter"
                : "emit the DTO fields as separate scalar [FromForm] parameters, or drop the "
                    + "file parameter";

            throw new ContractAnalysisException(
                $"error {Diagnostics.MixedFormFileParameters}: endpoint "
                    + $"'{MethodOwner(method)}.{method.Name}' mixes IFormFile file parameters with an "
                    + $"explicit body parameter '{formBodyParam.Name}' of type "
                    + $"'{formBodyParam.Type.ToDisplayString()}' — the multipart body cannot faithfully carry "
                    + $"a recursive form object without emulating MVC form binding. {remedy}."
            );
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

    /// <summary>
    /// Classifies a parameter from explicit ASP.NET transport declarations plus the
    /// deliberately tiny allowlist of unambiguous conventions:
    /// <code>
    /// [FromServices]      → exclude (handled before classification)
    /// CancellationToken  → exclude (host plumbing)
    /// explicit [From*]    → declared source (contradictions refuse)
    /// IFormFile(+collection) → file (unattributed or [FromForm] only)
    /// exact route match   → route
    /// otherwise           → unresolved contract (refusal)
    /// </code>
    /// There is no MVC model-binding reconstruction: a scalar never becomes Query and
    /// a class/record/struct never becomes Body from its CLR shape. Explicit attributes
    /// are collected first and validated as one coherent declaration before any
    /// convention applies — an IFormFile type never overrides explicit metadata, and
    /// contradictory attributes refuse rather than first-attr-wins
    /// (acceptance:explicit-binding-resolution-is-authoritative).
    /// </summary>
    private static ParamSource? ClassifyParam(
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        IParameterSymbol param,
        HashSet<string> routeParamNames
    )
    {
        // Collect every explicit binding declaration on the parameter.
        var explicitSources = new List<ParamSource>();
        foreach (var attr in param.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromBody))
            {
                explicitSources.Add(ParamSource.Body);
            }
            else if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromForm))
            {
                // [FromForm] declares its surface from the parameter's own declared
                // shape, not from neighbouring files. File-type detection runs first:
                // IFormFile/collection under [FromForm] is the coherent explicit file
                // case and classifies as FormField here so the apply step maps it to
                // File — IsSimpleFormType(IFormFile) is false because IFormFile is an
                // interface, and the type must not refuse or become Body
                // (planner-constraint:fromform-file-forms-classify-file). A scalar/
                // simple parameter is a single form field (Name= honored), a DTO/record
                // is the form body whose wire representation is the IsFormEncoded
                // x-www-form-urlencoded content. No file-presence probe exists;
                // unattributed params never reach this branch.
                explicitSources.Add(
                    typeWalker.IsSimpleFormType(param.Type)
                    || IsFormFileType(wkt, typeWalker, param)
                        ? ParamSource.FormField
                        : ParamSource.Body
                );
            }
            else if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromQuery))
            {
                explicitSources.Add(ParamSource.Query);
            }
            else if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromRoute))
            {
                explicitSources.Add(ParamSource.Route);
            }
            else if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromHeader))
            {
                explicitSources.Add(ParamSource.Header);
            }
        }

        // One coherent declaration or none: contradictory explicit sources refuse with
        // one actionable diagnostic instead of whichever attribute is inspected first
        // (acceptance:contradictory-bindings-refuse).
        if (explicitSources.Count > 1 && explicitSources.Distinct().Count() > 1)
        {
            ThrowContradictoryBinding(param, explicitSources);
        }

        if (explicitSources.Count > 0)
        {
            var declared = explicitSources[0];

            // An explicit source on an IFormFile/collection parameter is honored only
            // when it is the coherent explicit file case ([FromForm] → File); any other
            // source (query/body/route/header) refuses rather than silently forcing File
            // (acceptance:contradictory-bindings-refuse).
            var isFormFile = IsFormFileType(wkt, typeWalker, param);
            if (isFormFile)
            {
                if (declared is ParamSource.FormField)
                {
                    return ParamSource.File;
                }

                ThrowIncompatibleFileSource(param, declared);
            }

            return declared;
        }

        // IFormFile parameter (single or collection) → File source. The type itself
        // is the strongly semantic transport declaration.
        if (IsFormFileType(wkt, typeWalker, param))
        {
            return ParamSource.File;
        }

        // Retained convention: a parameter named exactly like an explicit route
        // placeholder is route input.
        if (routeParamNames.Contains(param.Name))
        {
            return ParamSource.Route;
        }

        // No declaration, no convention: the transport source is unknown.
        return null;
    }

    /// <summary>
    /// True for IFormFile itself or a collection of it — the single strongly semantic
    /// file transport type. Shared by the explicit-source apply step and the
    /// unattributed convention so both sites cannot drift.
    /// </summary>
    private static bool IsFormFileType(
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        IParameterSymbol param
    ) =>
        SymbolEqualityComparer.Default.Equals(param.Type, wkt.IFormFile)
        || typeWalker.IsCollectionOf(param.Type, wkt.IFormFile);

    /// <summary>
    /// Refusal for an unattributed parameter whose transport source cannot be
    /// established: the extraction aborts instead of emitting a contract that
    /// silently omits user input (planner-constraint:unresolved-facts-refuse-emission).
    /// Never returns normally — the return exists so `??` flow analysis can see it.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static ParamSource ThrowUnresolvedBinding(IMethodSymbol method, IParameterSymbol param)
    {
        throw new ContractAnalysisException(
            $"error {Diagnostics.UnresolvedBindingSource}: parameter '{param.Name}' of type "
                + $"'{param.Type.ToDisplayString()}' on endpoint '{MethodOwner(method)}.{method.Name}' has no "
                + "binding source. Rivet reads explicit transport declarations, not MVC inference — add "
                + "[FromQuery]/[FromBody]/[FromRoute]/[FromHeader]/[FromForm], [FromServices] for DI plumbing, "
                + "or name the parameter exactly like a route placeholder."
        );
    }

    /// <summary>
    /// Refusal for a parameter carrying multiple incompatible explicit binding sources:
    /// the contract aborts with one actionable diagnostic instead of whichever attribute
    /// happens to be inspected first
    /// (acceptance:contradictory-bindings-refuse).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowContradictoryBinding(IParameterSymbol param, List<ParamSource> sources)
    {
        throw new ContractAnalysisException(
            $"error {Diagnostics.UnresolvedBindingSource}: parameter '{param.Name}' of type "
                + $"'{param.Type.ToDisplayString()}' carries contradictory binding attributes "
                + $"({string.Join(", ", sources.Select(source => ToWireLabel(source)))}) — one parameter "
                + "declares exactly one binding source. Rivet does not pick a winner; keep a single "
                + "explicit [From*] declaration on the parameter."
        );
    }

    /// <summary>
    /// Refusal for an IFormFile/collection parameter whose single explicit source is
    /// incompatible with file transport (query/body/route/header): MVC binds IFormFile
    /// from form files, so forcing File would silently ignore the declaration and
    /// honoring the declaration would invent a transport the wire cannot carry.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowIncompatibleFileSource(IParameterSymbol param, ParamSource declared)
    {
        throw new ContractAnalysisException(
            $"error {Diagnostics.UnresolvedBindingSource}: parameter '{param.Name}' of type "
                + $"'{param.Type.ToDisplayString()}' is IFormFile but is explicitly declared "
                + $"{ToWireLabel(declared)} — an incompatible source for file transport. Remove the "
                + "explicit attribute (IFormFile alone is the retained file convention) or use [FromForm] "
                + "on the file parameter."
        );
    }

    private static string ToWireLabel(ParamSource source) =>
        source switch
        {
            ParamSource.Body => "[FromBody]",
            ParamSource.FormField or ParamSource.File => "[FromForm]",
            ParamSource.Query => "[FromQuery]",
            ParamSource.Route => "[FromRoute]",
            ParamSource.Header => "[FromHeader]",
            _ => "[FromServices]",
        };

    private static string MethodOwner(IMethodSymbol method) =>
        method.ContainingType?.Name ?? "<unknown>";

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
    internal static ITypeSymbol? UnwrapTask(
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
                    else
                    {
                        // An unmapped Results<> branch must never vanish from a
                        // successful contract: refuse instead of warn-and-omit
                        // (planner-constraint:unresolved-facts-refuse-emission).
                        throw new ContractAnalysisException(
                            $"error {Diagnostics.UnmappedTypedResult}: typed result "
                                + $"'{resultArg.ToDisplayString()}' in Results<...> on endpoint "
                                + $"'{(warnContext ?? "<unknown>")}' does not "
                                + "statically encode its HTTP status. Rivet maps only genuinely fixed typed "
                                + "results — replace the branch with one (or add explicit "
                                + "[ProducesResponseType] metadata for every response the endpoint returns)."
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

        // IActionResult / ActionResult are runtime result containers: their success
        // body/status is selected at runtime, not by the declared contract. They are
        // never a 200 payload source (planner-constraint:concrete-return-convenience-
        // narrowed); BuildEndpoint refuses them when no explicit response metadata
        // exists.
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

        // No 200/T invention: [ProducesResponseType] entries declare exactly the
        // responses the developer wrote. If they declare only error statuses, that
        // is the contract — BuildEndpoint refuses the missing success instead of
        // inserting one from ActionResult<T> (acceptance:no-actionresult-t-success-
        // invention).

        // Results<T1, ...> branches stay visible even behind response attributes
        // (acceptance:results-branches-cannot-hide-behind-attributes): attributes may
        // describe responses, but an unmapped branch (ProblemHttpResult, JsonHttpResult<T>,
        // …) is still an unresolved declared surface and refuses with the existing
        // unmapped-result diagnostic. Mapped fixed branches merge only into statuses the
        // attributes have not already declared — blind appends would trip the
        // duplicate-response refusal on contracts that declare .Status/.Returns for a
        // fixed branch (planner-constraint:results-validation-merges-not-appends).
        var unwrappedForValidation = UnwrapTask(wkt, method.ReturnType, out _);
        if (
            unwrappedForValidation is INamedTypeSymbol resultsType
            && IsTypedResults(wkt, resultsType)
        )
        {
            var declaredStatuses = responses.Select(response => response.StatusCode).ToHashSet();
            foreach (var mapping in CollectTypedResultMappings(wkt, resultsType, method.Name))
            {
                if (declaredStatuses.Contains(mapping.StatusCode))
                {
                    continue;
                }

                var tsType = mapping.BodyType is not null
                    ? typeWalker.MapType(mapping.BodyType)
                    : null;
                responses.Add(new TsResponseType(mapping.StatusCode, tsType));
                declaredStatuses.Add(mapping.StatusCode);
            }
        }

        // If no [ProducesResponseType] found, try typed results from return type
        if (responses.Count == 0)
        {
            var unwrapped = UnwrapTask(wkt, method.ReturnType, out _);
            if (unwrapped is INamedTypeSymbol namedType && !IsTypedResults(wkt, namedType))
            {
                // A8: warn loudly for unmapped Results<> branches (only on this path so
                // the warning is emitted once per endpoint). Results<> arities were
                // validated above; this path covers the single fixed typed result.
                foreach (var mapping in CollectTypedResultMappings(wkt, namedType, method.Name))
                {
                    var tsType = mapping.BodyType is not null
                        ? typeWalker.MapType(mapping.BodyType)
                        : null;
                    responses.Add(new TsResponseType(mapping.StatusCode, tsType));
                }

                // No bare ActionResult<T> → 200/T fallback: ActionResult<T> does not
                // itself declare a complete response set; BuildEndpoint's refusal
                // above handles the missing success.
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
