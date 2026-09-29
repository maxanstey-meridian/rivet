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
                var endpoint = BuildControllerEndpoint(
                    method,
                    Naming.ToCamelCase(ControllerBaseName(method.ContainingType)),
                    isContract: false,
                    wkt,
                    typeWalker
                );
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
                var endpoint = BuildControllerEndpoint(
                    method,
                    Naming.ToCamelCase(ControllerBaseName(method.ContainingType)),
                    isContract: false,
                    wkt,
                    typeWalker
                );
                if (endpoint is not null)
                {
                    endpoints.Add(endpoint);
                }
            }
        }

        return endpoints;
    }

    /// <summary>
    /// Builds an endpoint from an MVC-attributed method: an annotated action
    /// ([RivetEndpoint]/[RivetClient]) or an abstract [RivetContract] method. Contract
    /// methods refuse duplicate statuses and do not read MVC request metadata
    /// ([Consumes], [FromForm] encoding, Rivet examples).
    /// </summary>
    internal static TsEndpointDefinition? BuildControllerEndpoint(
        IMethodSymbol method,
        string controllerName,
        bool isContract,
        WellKnownTypes wkt,
        TypeWalker typeWalker
    )
    {
        var (httpMethod, route) = ResolveActionRoute(wkt, method);
        if (httpMethod is null || route is null)
        {
            return null;
        }

        var name = Naming.ToCamelCase(method.Name);
        var parameters = ExtractParams(wkt, method, typeWalker, route);
        var responses = ExtractAllResponseTypes(wkt, method, typeWalker, normalize: !isContract)
            .ToList();
        if (responses.Count == 0)
        {
            // Explicit-response boundary: a method with no declared success response is
            // an incomplete contract — refuse rather than fabricate one. The concrete
            // payload T convenience applies after Task/ValueTask unwrapping; result
            // containers and void stay unresolved.
            var unwrapped = UnwrapTask(wkt, method.ReturnType, out _);
            if (unwrapped is null || IsStatusSelectingResultContainer(wkt, unwrapped))
            {
                throw new RivetUserException(
                    isContract
                        ? $"error {Diagnostics.UnmappedTypedResult}: contract endpoint "
                            + $"'{name}' declares no success response. "
                            + "Rivet reads explicit contract declarations — add .Status(...).Returns(...) "
                            + "(or a concrete payload output type) to declare the success response."
                        : $"error {Diagnostics.UnmappedTypedResult}: endpoint "
                            + $"'{MethodOwner(method)}.{method.Name}' declares no response. "
                            + "Rivet reads explicit response declarations, not MVC runtime defaults — "
                            + "add [ProducesResponseType(typeof(T), 200)] (or a concrete payload return / "
                            + "a fixed-status typed result) to declare the success response."
                );
            }

            responses.Add(new TsResponseType(200, typeWalker.MapType(unwrapped)));
        }

        if (isContract)
        {
            ResponseStatusValidation.RejectContractDuplicates(responses, name);
            responses.Sort((left, right) => left.StatusCode.CompareTo(right.StatusCode));
        }

        var successResponse = responses.FirstOrDefault(response =>
            response.StatusCode is >= 200 and < 300
        );
        var responseContentTypeOverride = ResolveResponseContentType(
            wkt,
            method,
            successResponse?.DataType
        );
        if (isContract)
        {
            return new TsEndpointDefinition(
                name,
                httpMethod,
                route,
                parameters,
                successResponse?.DataType,
                controllerName,
                responses,
                ResponseContentTypeOverride: responseContentTypeOverride
            );
        }

        var isFormEncoded = HasFromFormBody(method, wkt);
        var requestExamples = ExtractRequestExamples(wkt, method, parameters, isFormEncoded);
        ApplyResponseExamples(
            responses,
            ExtractResponseExamples(wkt, method),
            Diagnostics.ControllerExampleUndeclaredStatus,
            $"controller endpoint '{name}'"
        );

        var consumes = method.GetAttributes().Where(a => a.Is(wkt.Consumes)).ToArray();
        if (consumes.Length == 0)
        {
            consumes = method
                .ContainingType.GetAttributes()
                .Where(a => a.Is(wkt.Consumes))
                .ToArray();
        }
        var requestMediaTypes = consumes
            .SelectMany(a => a.ConstructorArguments)
            .SelectMany(a =>
                a.Kind == TypedConstantKind.Array ? a.Values.AsEnumerable() : new[] { a }
            )
            .Select(a => a.Value)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestMediaTypes.Length > 1)
        {
            throw new RivetUserException(
                $"error {Diagnostics.UnresolvedBindingSource}: endpoint '{MethodOwner(method)}.{method.Name}' declares multiple request media types. Declare one supported [Consumes] media type."
            );
        }

        return new TsEndpointDefinition(
            name,
            httpMethod,
            route,
            parameters,
            successResponse?.DataType,
            controllerName,
            responses,
            IsFormEncoded: isFormEncoded,
            RequestExamples: requestExamples,
            RequestContentTypeOverride: requestMediaTypes.SingleOrDefault(),
            ResponseContentTypeOverride: responseContentTypeOverride
        );
    }

    /// <summary>
    /// The HTTP method and full transport route of an MVC action: the [Http*] verb and
    /// template combined with the class [Route], [controller]/[action] tokens
    /// substituted, constraints stripped. HttpMethod is null when the method is not an
    /// action; Route is null when neither the action nor the class declares a template.
    /// </summary>
    internal static (string? HttpMethod, string? Route) ResolveActionRoute(
        WellKnownTypes wkt,
        IMethodSymbol method
    )
    {
        var (httpMethod, methodRoute) = ExtractHttpMethodAndRoute(wkt, method);
        if (httpMethod is null)
        {
            return (null, null);
        }

        var route = CombineRoutes(ExtractControllerRoute(wkt, method.ContainingType), methodRoute);
        return route is null
            ? (httpMethod, null)
            : (httpMethod, RouteParser.StripRouteConstraints(SubstituteRouteTokens(route, method)));
    }

    /// <summary>
    /// True for result containers whose runtime value selects    /// <summary>
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
            if (!attr.Is(wkt.Produces))
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
            .Where(attr => attr.Is(wkt.RivetRequestExample))
            .Select(attr =>
                ToRequestExample(attr, DefaultRequestExampleMediaType(parameters, isFormEncoded))
            )
            .Where(example => example is not null)
            .Cast<TsEndpointExample>()
            .ToList();

        return examples.Count == 0 ? null : examples;
    }

    private static IReadOnlyList<(
        string StatusKey,
        TsEndpointExample Example
    )> ExtractResponseExamples(WellKnownTypes wkt, IMethodSymbol method) =>
        method
            .GetAttributes()
            .Where(attr => attr.Is(wkt.RivetResponseExample))
            .Select(ToResponseExample)
            .OfType<(string, TsEndpointExample)>()
            .ToList();

    internal static string DefaultRequestExampleMediaType(
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
                    && attr.Is(wkt.FromForm)
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

    private static (string StatusKey, TsEndpointExample Example)? ToResponseExample(
        AttributeData attr
    ) =>
        attr.ConstructorArguments is [{ Value: int statusCode }, { Value: string json }, ..]
            ? (
                statusCode.ToString(),
                ToEndpointExample(
                    "application/json",
                    GetStringArg(attr, 3),
                    json,
                    GetStringArg(attr, 2),
                    GetStringArg(attr, 4)
                )
            )
            : null;

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

    /// <summary>
    /// Attaches response examples to the responses declaring their status, then sorts
    /// by status. An example for an undeclared status is ignored with a warning.
    /// </summary>
    internal static void ApplyResponseExamples(
        List<TsResponseType> responses,
        IReadOnlyList<(string StatusKey, TsEndpointExample Example)> examples,
        string undeclaredStatusDiagnostic,
        string endpointLabel
    )
    {
        if (examples.Count == 0)
        {
            return;
        }

        foreach (
            var group in examples.GroupBy(
                example => example.StatusKey,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            var responseIndex = responses.FindIndex(response =>
                response.EffectiveStatusKey.Equals(group.Key, StringComparison.OrdinalIgnoreCase)
            );
            if (responseIndex < 0)
            {
                Diagnostics.Warn(
                    undeclaredStatusDiagnostic,
                    $"ignoring response example for undeclared status {group.Key} on {endpointLabel}"
                );
                continue;
            }

            var response = responses[responseIndex];
            var mapped = group.Select(example => example.Example);
            responses[responseIndex] = response with
            {
                Examples = (response.Examples ?? []).Concat(mapped).ToList(),
            };
        }

        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));
    }

    private static string? GetStringArg(AttributeData attr, int index)
    {
        return attr.ConstructorArguments.Length > index
            ? attr.ConstructorArguments[index].Value as string
            : null;
    }

    /// <summary>
    /// The controller class name without its "Controller" suffix
    /// (CaseStatusesController → CaseStatuses).
    /// </summary>
    private static string ControllerBaseName(INamedTypeSymbol type) =>
        type.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? type.Name[..^"Controller".Length]
            : type.Name;

    private static (string? HttpMethod, string? Route) ExtractHttpMethodAndRoute(
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
    private static string? ExtractControllerRoute(
        WellKnownTypes wkt,
        INamedTypeSymbol containingType
    )
    {
        foreach (var attr in containingType.GetAttributes())
        {
            if (attr.Is(wkt.Route) && attr.ConstructorArguments.Length > 0)
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
    private static string SubstituteRouteTokens(string route, IMethodSymbol method) =>
        route
            .Replace(
                "[controller]",
                ControllerBaseName(method.ContainingType),
                StringComparison.OrdinalIgnoreCase
            )
            .Replace("[action]", method.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Combines controller route prefix with method route segment.
    /// e.g. "api/case-statuses" + "{id:guid}" → "/api/case-statuses/{id:guid}"
    /// </summary>
    private static string? CombineRoutes(string? controllerRoute, string? methodRoute)
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
        var formSeen = false;

        foreach (var param in method.Parameters)
        {
            if (ClassifyParam(wkt, typeWalker, method, param, routeParamNames) is not { } source)
            {
                continue;
            }

            if (source == ParamSource.Route)
            {
                var explicitRouteName = GetBindingName(param, WireNamedSources(wkt));
                var effectiveRouteName = explicitRouteName ?? param.Name;
                var declaration = explicitRouteName is null
                    ? "[FromRoute]"
                    : $"[FromRoute(Name = \"{explicitRouteName}\")]";
                if (!routeParamNames.Contains(effectiveRouteName))
                {
                    throw new RivetUserException(
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
                throw new RivetUserException(
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

            if (source is ParamSource.File or ParamSource.FormField)
            {
                formSeen = true;
            }
        }

        if (formSeen && bodySeen)
        {
            throw new RivetUserException(
                $"error {Diagnostics.MixedFormFileParameters}: endpoint '{MethodOwner(method)}.{method.Name}' "
                    + $"mixes body parameter '{parameters.First(p => p.Source == ParamSource.Body).Name}' with separate form fields or files. Declare one form DTO, "
                    + "or separate scalar [FromForm] fields and files; put a JSON body on a separate endpoint."
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
    /// assembly supplied the symbol. GetTypeByMetadataName returns null when two referenced assemblies
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

    private enum BindingSource
    {
        Body,
        Form,
        Query,
        Route,
        Header,
        Services,
    }

    /// <summary>
    /// The transport source of an action parameter, or null for host plumbing
    /// ([FromServices], CancellationToken). Refuses a parameter whose source is
    /// contradictory or cannot be established.
    /// </summary>
    private static ParamSource? ClassifyParam(
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        IMethodSymbol method,
        IParameterSymbol param,
        HashSet<string> routeParamNames
    )
    {
        // Collect every explicit binding declaration on the parameter.
        var explicitSources = new List<BindingSource>();
        foreach (var attr in param.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromBody))
            {
                explicitSources.Add(BindingSource.Body);
            }
            else if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromForm))
            {
                explicitSources.Add(BindingSource.Form);
            }
            else if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromQuery))
            {
                explicitSources.Add(BindingSource.Query);
            }
            else if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromRoute))
            {
                explicitSources.Add(BindingSource.Route);
            }
            else if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromHeader))
            {
                explicitSources.Add(BindingSource.Header);
            }
            else if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromServices))
            {
                explicitSources.Add(BindingSource.Services);
            }
        }

        if (explicitSources.Count > 1 && explicitSources.Distinct().Count() > 1)
        {
            ThrowContradictoryBinding(param, explicitSources);
        }

        if (IsCancellationToken(param.Type))
        {
            if (explicitSources.Any(source => source != BindingSource.Services))
            {
                throw new RivetUserException(
                    $"error {Diagnostics.UnresolvedBindingSource}: CancellationToken parameter '{param.Name}' "
                        + "is host plumbing and cannot declare a transport binding."
                );
            }
            return null;
        }

        if (explicitSources.Count > 0)
        {
            var declared = explicitSources[0];
            if (declared == BindingSource.Services)
            {
                return null;
            }
            var source = declared switch
            {
                BindingSource.Body => ParamSource.Body,
                BindingSource.Form => typeWalker.IsSimpleFormType(param.Type)
                    ? ParamSource.FormField
                    : ParamSource.Body,
                BindingSource.Query => ParamSource.Query,
                BindingSource.Route => ParamSource.Route,
                BindingSource.Header => ParamSource.Header,
                _ => throw new InvalidOperationException(),
            };
            if (IsFormFileType(wkt, typeWalker, param))
            {
                if (declared == BindingSource.Form)
                {
                    return ParamSource.File;
                }
                ThrowIncompatibleFileSource(param, source);
            }
            return source;
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
        return ThrowUnresolvedBinding(method, param);
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
        throw new RivetUserException(
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
    private static void ThrowContradictoryBinding(
        IParameterSymbol param,
        List<BindingSource> sources
    )
    {
        throw new RivetUserException(
            $"error {Diagnostics.UnresolvedBindingSource}: parameter '{param.Name}' of type "
                + $"'{param.Type.ToDisplayString()}' carries contradictory binding attributes "
                + $"({string.Join(", ", sources.Select(source => $"[From{source}]"))}) — one parameter "
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
        throw new RivetUserException(
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
                        throw new RivetUserException(
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
    /// Merges fixed typed results with explicit metadata, rejecting conflicting body shapes.
    /// </summary>
    internal static IReadOnlyList<TsResponseType> ExtractAllResponseTypes(
        WellKnownTypes wkt,
        IMethodSymbol method,
        TypeWalker typeWalker,
        bool normalize = true
    )
    {
        var responses = new List<TsResponseType>();
        // Body-type symbols behind the declared statuses, retained for the
        // Results<> merge conflict check below — TsResponseType only carries the
        // mapped schema, and symbol equality is the honest comparison here.
        var declaredBodyTypes = new Dictionary<int, ITypeSymbol?>();

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
            declaredBodyTypes.TryAdd(
                statusCode,
                parsed.Value.Type is not null && !isVoidResponse ? parsed.Value.Type : null
            );
        }

        var unwrappedForValidation = UnwrapTask(wkt, method.ReturnType, out _);
        if (unwrappedForValidation is INamedTypeSymbol resultsType)
        {
            foreach (var mapping in CollectTypedResultMappings(wkt, resultsType, method.Name))
            {
                if (declaredBodyTypes.TryGetValue(mapping.StatusCode, out var declaredBody))
                {
                    var declarationsAgree =
                        (declaredBody is null && mapping.BodyType is null)
                        || (
                            declaredBody is not null
                            && mapping.BodyType is not null
                            && SymbolEqualityComparer.Default.Equals(declaredBody, mapping.BodyType)
                        );
                    if (!declarationsAgree)
                    {
                        throw new RivetUserException(
                            $"error {Diagnostics.ConflictingResponseDeclaration}: endpoint "
                                + $"'{method.ContainingType.Name}.{method.Name}' declares response status "
                                + $"{mapping.StatusCode} twice with different bodies — the earlier declaration "
                                + $"declares '{declaredBody?.ToDisplayString() ?? "no body"}' while the "
                                + $"typed result declares '{mapping.BodyType?.ToDisplayString() ?? "no body"}'. "
                                + "One response status carries exactly one shape: align the declaration and the "
                                + "typed result (or drop the redundant declaration)"
                        );
                    }

                    continue;
                }

                var tsType = mapping.BodyType is not null
                    ? typeWalker.MapType(mapping.BodyType)
                    : null;
                responses.Add(new TsResponseType(mapping.StatusCode, tsType));
                declaredBodyTypes.Add(mapping.StatusCode, mapping.BodyType);
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
