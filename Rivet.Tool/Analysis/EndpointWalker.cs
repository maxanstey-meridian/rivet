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
                endpoints.Add(
                    BuildControllerEndpoint(
                        method,
                        Naming.ToCamelCase(MvcRouteValues.ControllerName(method.ContainingType)),
                        isContract: false,
                        wkt,
                        typeWalker
                    )
                );
            }
        }

        foreach (var method in ClientMethods(wkt, clientTypes))
        {
            if (seen.Add(method))
            {
                endpoints.Add(
                    BuildControllerEndpoint(
                        method,
                        Naming.ToCamelCase(MvcRouteValues.ControllerName(method.ContainingType)),
                        isContract: false,
                        wkt,
                        typeWalker
                    )
                );
            }
        }

        return endpoints;
    }

    /// <summary>
    /// Builds an endpoint from an MVC-attributed method: an annotated action
    /// ([RivetEndpoint]/[RivetClient]) or an abstract [RivetContract] method. Contract
    /// methods refuse duplicate statuses and do not read MVC request metadata
    /// ([Consumes], [FromForm] encoding, Rivet examples). A method with no [Http*] verb,
    /// or no route template on the action or its class, is refused (RIV1100): it was
    /// marked as an operation, so skipping it would drop it from the spec without a word.
    /// </summary>
    internal static TsEndpointDefinition BuildControllerEndpoint(
        IMethodSymbol method,
        string controllerName,
        bool isContract,
        WellKnownTypes wkt,
        TypeWalker typeWalker
    )
    {
        var (httpMethod, routeTemplate) = ResolveActionTemplate(wkt, method);
        var route = routeTemplate is null ? null : RouteParser.StripRouteConstraints(routeTemplate);
        var name = Naming.ToCamelCase(method.Name);
        var label = isContract
            ? $"contract endpoint '{MethodOwner(method)}.{method.Name}'"
            : $"endpoint '{MethodOwner(method)}.{method.Name}'";
        if (httpMethod is null)
        {
            throw Diagnostics.Error(
                Diagnostics.UnresolvedBindingSource,
                $"{label} "
                    + (
                        isContract
                            ? "is an abstract method on a [RivetContract] class"
                            : "is marked [RivetEndpoint]"
                    )
                    + " but has no [HttpGet], [HttpPost], [HttpPut], [HttpPatch] or [HttpDelete] attribute, "
                    + "so Rivet can't tell its HTTP method and would drop the operation. Add the verb attribute "
                    + "with its route template, or "
                    + (
                        isContract
                            ? "remove the method from the contract."
                            : "remove [RivetEndpoint]."
                    )
            );
        }

        if (route is null)
        {
            throw Diagnostics.Error(
                Diagnostics.UnresolvedBindingSource,
                $"{label} has no route template on its "
                    + $"{httpMethod} attribute or a [Route] on "
                    + "its class, so the operation has no path and would drop from the spec. Give the verb "
                    + "attribute a template, or put [Route] on the class."
            );
        }

        var (parameters, formInputTypeName) = ExtractParams(
            wkt,
            method,
            typeWalker,
            routeTemplate!
        );
        if (!isContract)
        {
            RejectUnbindableEnumParameters(wkt, method, httpMethod);
        }

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
                throw Diagnostics.Error(
                    Diagnostics.UnmappedTypedResult,
                    isContract
                        ? "contract endpoint "
                            + $"'{name}' declares no success response. "
                            + "Rivet reads explicit contract declarations — add .Status(...).Returns(...) "
                            + "(or a concrete payload output type) to declare the success response."
                        : "endpoint "
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
        var security = SecurityDeclaration.FromAttributes(wkt, method, label);
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
                Security: security,
                InputTypeName: formInputTypeName,
                ResponseContentTypeOverride: responseContentTypeOverride
            );
        }

        var isFormEncoded = HasFromFormBody(method, wkt);
        var requestExamples = ExtractRequestExamples(wkt, method, parameters, isFormEncoded);
        ApplyResponseExamples(
            responses,
            ExtractResponseExamples(wkt, method, responseContentTypeOverride),
            Diagnostics.ControllerExampleUndeclaredStatus,
            $"controller endpoint '{name}'",
            "declare the status on the action (e.g. [ProducesResponseType])"
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
            .SelectMany(a => a.Strings())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestMediaTypes.Length > 1)
        {
            throw Diagnostics.Error(
                Diagnostics.UnresolvedBindingSource,
                $"endpoint '{MethodOwner(method)}.{method.Name}' declares multiple request media types. Declare one supported [Consumes] media type."
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
            Security: security,
            InputTypeName: formInputTypeName,
            IsFormEncoded: isFormEncoded,
            RequestExamples: requestExamples,
            RequestContentTypeOverride: requestMediaTypes.SingleOrDefault(),
            ResponseContentTypeOverride: responseContentTypeOverride
        );
    }

    /// <summary>
    /// The HTTP method and full transport route of an MVC action: the [Http*] verb and
    /// template combined with the class [Route], its tokens replaced by the action's route
    /// values (<see cref="MvcRouteValues"/>), constraints stripped. HttpMethod is null when
    /// the method is not an action; Route is null when neither the action nor the class
    /// declares a template.
    /// </summary>
    internal static (string? HttpMethod, string? Route) ResolveActionRoute(
        WellKnownTypes wkt,
        IMethodSymbol method
    )
    {
        var (httpMethod, template) = ResolveActionTemplate(wkt, method);
        return (httpMethod, template is null ? null : RouteParser.StripRouteConstraints(template));
    }

    /// <summary>
    /// <see cref="ResolveActionRoute"/> with the route's constraints kept, for reading them.
    /// </summary>
    private static (string? HttpMethod, string? Template) ResolveActionTemplate(
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
        return (
            httpMethod,
            route is null
                ? null
                : MvcRouteValues.ReplaceTokens(
                    wkt,
                    route,
                    method,
                    $"{MethodOwner(method)}.{method.Name}"
                )
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
    /// diagnostic rather than this generic container refusal.
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
        // value selects status/content.
        return named.Implements(wkt.IResult) || named.Implements(wkt.IActionResult);
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
    /// <remarks>
    /// ProducesAttribute's constructor is (string contentType, params string[]
    /// additionalContentTypes), so the string positional is read before the array values.
    /// </remarks>
    private static string? ExtractProducesContentType(WellKnownTypes wkt, IMethodSymbol method) =>
        method
            .GetAttributes()
            .Concat(method.ContainingType.GetAttributes())
            .Where(attr => attr.Is(wkt.Produces))
            .SelectMany(attr => attr.ConstructorArguments)
            .SelectMany(argument => argument.Strings())
            .FirstOrDefault(contentType => contentType.Length > 0);

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

    /// <summary>
    /// The action's [RivetResponseExample]s. One without a media type lands where the spec
    /// puts its status's body: a success under the [Produces] media type when there is one,
    /// anything else under application/json.
    /// </summary>
    private static IReadOnlyList<(
        string StatusKey,
        TsEndpointExample Example
    )> ExtractResponseExamples(
        WellKnownTypes wkt,
        IMethodSymbol method,
        string? successContentType
    ) =>
        method
            .GetAttributes()
            .Where(attr => attr.Is(wkt.RivetResponseExample))
            .Select(attr => ToResponseExample(attr, successContentType))
            .OfType<(string, TsEndpointExample)>()
            .ToList();

    internal static string DefaultRequestExampleMediaType(
        IReadOnlyList<TsEndpointParam> parameters,
        bool isFormEncoded
    )
    {
        // The emitter's rule: files make a form multipart, and form fields alone are urlencoded.
        if (parameters.Any(parameter => parameter.Source == ParamSource.File))
        {
            return "multipart/form-data";
        }

        return
            isFormEncoded || parameters.Any(parameter => parameter.Source == ParamSource.FormField)
            ? "application/x-www-form-urlencoded"
            : "application/json";
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
        AttributeData attr,
        string? successContentType
    ) =>
        attr.ConstructorArguments is [{ Value: int statusCode }, { Value: string json }, ..]
            ? (
                statusCode.ToString(),
                ToEndpointExample(
                    statusCode is >= 200 and < 300
                        ? successContentType ?? "application/json"
                        : "application/json",
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
    /// by status. An example for an undeclared status fails generation: the spec has
    /// no response to carry it.
    /// </summary>
    internal static void ApplyResponseExamples(
        List<TsResponseType> responses,
        IReadOnlyList<(string StatusKey, TsEndpointExample Example)> examples,
        string undeclaredStatusDiagnostic,
        string endpointLabel,
        string declareStatusHint
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
                throw Diagnostics.Error(
                    undeclaredStatusDiagnostic,
                    $"{endpointLabel} has a response example for status "
                        + $"{group.Key}, which it does not declare, so the spec has nowhere to put it. "
                        + $"To keep the example, {declareStatusHint}; otherwise fix or remove the example's status code."
                );
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
    /// Reads [Route("...")] from the controller class, or else from the nearest base class
    /// that declares one: MVC lets a derived controller's routes override its base's, and
    /// takes a base's when it declares none.
    /// </summary>
    private static string? ExtractControllerRoute(
        WellKnownTypes wkt,
        INamedTypeSymbol containingType
    )
    {
        for (var type = containingType; type is not null; type = type.BaseType)
        {
            foreach (var attr in type.GetAttributes())
            {
                if (attr.Is(wkt.Route) && attr.ConstructorArguments.Length > 0)
                {
                    return attr.ConstructorArguments[0].Value as string;
                }
            }
        }

        return null;
    }

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

    /// <summary>
    /// The action's params, and the definition a <c>[FromForm]</c> record was lowered from
    /// (its parts become params), which the emitter describes the form body from.
    /// </summary>
    internal static (
        IReadOnlyList<TsEndpointParam> Parameters,
        string? FormInputTypeName
    ) ExtractParams(
        WellKnownTypes wkt,
        IMethodSymbol method,
        TypeWalker typeWalker,
        string routeTemplate
    )
    {
        var where =
            $"{RouteParser.StripRouteConstraints(routeTemplate)} (endpoint '{MethodOwner(method)}.{method.Name}')";
        // Routing's own reading of the template, which refuses a repeated token as the host
        // does at startup. Nothing here was imported: an action, a [RivetEndpoint] method, and
        // an abstract contract's method are all hand-written (import writes static contracts).
        var routeTokens = RouteConstraints
            .Tokens(routeTemplate, where, readRepeats: false)
            .ToDictionary(token => token.Name, StringComparer.OrdinalIgnoreCase);
        // The route param names, for implicit classification.
        var routeParamNames = routeTokens.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parameters = new List<TsEndpointParam>();
        // MVC rejects multiple body-bound parameters on one action; extraction must
        // not confidently emit a body surface MVC cannot execute. Any Body-classified
        // source occupies the single body slot.
        var bodySeen = false;
        var formSeen = false;
        string? bodyParameterName = null;
        string? formInputTypeName = null;

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
                    throw Diagnostics.Error(
                        Diagnostics.UnresolvedBindingSource,
                        $"parameter '{param.Name}' on endpoint "
                            + $"'{MethodOwner(method)}.{method.Name}' is {declaration} but route '{routeTemplate}' "
                            + $"contains no '{{{effectiveRouteName}}}' placeholder — the route binds by its "
                            + "template. Align the binding name with a route placeholder (e.g. {{id}})."
                    );
                }
            }

            // IFormFile maps to the Web API File type — don't walk it through Roslyn.
            // Collection-of-IFormFile params emit array-of-binary.
            var tsType =
                source == ParamSource.File
                    ? FormBinding.FilePartType(wkt, param.Type)
                    : typeWalker.MapType(param.Type);
            // A C# default value makes the param optional on the wire.
            // FromQuery(Name=)/FromRoute(Name=)/FromForm(Name=) rename the wire surface
            // to match the actual request key / route placeholder (IModelNameProvider).
            var wireName = GetBindingName(param, WireNamedSources(wkt)) ?? param.Name;
            // A second Body-classified parameter cannot share the single body slot:
            // report it and refuse rather than silently emitting a contract MVC
            // itself would reject at startup.
            if (bodySeen && source == ParamSource.Body)
            {
                throw Diagnostics.Error(
                    Diagnostics.UnresolvedBindingSource,
                    "endpoint "
                        + $"'{MethodOwner(method)}.{method.Name}' declares a second request body — "
                        + $"parameter '{param.Name}' of type '{param.Type.ToDisplayString()}' cannot share the "
                        + "single body slot with an earlier body-bound parameter. MVC itself rejects this at "
                        + "startup; declare one [FromBody] (or an explicit form surface with IFormFile fields)."
                );
            }
            if (
                source == ParamSource.Body
                && param.HasAttribute(wkt.FromForm)
                && typeWalker.IsParamLowerable(param.Type)
            )
            {
                // A [FromForm] record is bound member by member from the form, so its parts are
                // params like a contract's form input, named as the one host that serves the
                // method reads them: an instance method is an MVC action, whose model binder
                // reads each part under the parameter's own Name = as a prefix; a static one is
                // a minimal-API handler, whose form mapper reads [DataMember] names and never
                // prefixes.
                formInputTypeName = (tsType is TsType.Nullable nullable ? nullable.Inner : tsType)
                    is TsType.TypeRef typeRef
                    ? typeRef.Name
                    : null;
                parameters.AddRange(
                    ContractWalker.FormPartParams(
                        wkt,
                        typeWalker,
                        FormBinding.Parts(
                            wkt,
                            typeWalker,
                            param.Type,
                            new HashSet<string>(),
                            HostOf(method) == ParameterBindingHost.MinimalApi
                                ? FormBinding.FormHosts.MinimalApi
                                : FormBinding.FormHosts.Mvc,
                            where
                        ),
                        formInputTypeName,
                        HostOf(method) == ParameterBindingHost.Mvc
                            ? FormBinding.MvcModelName(wkt, param.GetAttributes(), param.Type)
                            : null
                    )
                );
                bodySeen = true;
                bodyParameterName = param.Name;
                continue;
            }

            var endpointParam = new TsEndpointParam(
                wireName,
                tsType,
                source,
                IsOptional: param.HasExplicitDefaultValue,
                DefaultValue: GetDefaultValueLiteral(param)
            );
            if (
                source
                is ParamSource.Route
                    or ParamSource.Query
                    or ParamSource.Header
                    or ParamSource.FormField
            )
            {
                endpointParam = typeWalker.AnnotatedParam(endpointParam, param);
            }
            if (
                source == ParamSource.Route
                && routeTokens.TryGetValue(wireName, out var routeToken)
            )
            {
                endpointParam = RouteConstraints.Apply(
                    endpointParam,
                    routeToken,
                    where,
                    $"its action parameter '{param.Name}'"
                );
            }
            parameters.Add(endpointParam);
            if (source == ParamSource.Body)
            {
                bodySeen = true;
                bodyParameterName = param.Name;
            }

            if (source is ParamSource.File or ParamSource.FormField)
            {
                formSeen = true;
            }
        }

        // A token no action parameter binds still sits in the path, and OpenAPI needs every
        // templated parameter declared. A type constraint declares it; with none its type
        // is unknown and RIV1019 stops generation.
        var unbound = routeTokens
            .Values.Where(token =>
                !parameters.Any(parameter =>
                    parameter.Source == ParamSource.Route
                    && string.Equals(parameter.Name, token.Name, StringComparison.OrdinalIgnoreCase)
                )
            )
            .Select(token =>
            {
                if (RouteConstraints.DeclaredType(token, where) is { } constrained)
                {
                    return RouteConstraints.Apply(
                        new TsEndpointParam(token.Name, constrained, ParamSource.Route),
                        token,
                        where,
                        "its type constraint"
                    );
                }

                throw Diagnostics.Error(
                    Diagnostics.RouteTokenUndeclared,
                    $"route token '{{{token.Name}}}' on {where} has no action parameter and no type "
                        + "constraint, so its type is unknown and the spec can't say what the handler "
                        + $"binds. Declare it: bind it with an action parameter named '{token.Name}', or "
                        + $"add a type constraint ('{{{token.Name}:guid}}', '{{{token.Name}:int}}', ...)."
                );
            })
            .ToList();
        parameters.InsertRange(0, unbound);

        if (formSeen && bodySeen)
        {
            throw Diagnostics.Error(
                Diagnostics.MixedFormFileParameters,
                $"endpoint '{MethodOwner(method)}.{method.Name}' "
                    + $"mixes body parameter '{bodyParameterName}' with separate form fields or files. Declare one form DTO, "
                    + "or separate scalar [FromForm] fields and files; put a JSON body on a separate endpoint."
            );
        }

        return (parameters, formInputTypeName);
    }

    /// <summary>
    /// RIV1110: an annotated endpoint is the spec's source, so every enum it binds from
    /// route, query, header or form, directly or as a member of a complex parameter with an
    /// explicit [FromQuery]/[FromRoute]/[FromForm]/[FromHeader] (whose schema advertises the
    /// member's values), must accept each advertised wire value. An instance method is an
    /// MVC action, whose enum model binder matches member names ignoring case; a static
    /// method is a minimal-API handler (MVC never treats one as an action), which calls
    /// Enum.TryParse and matches them exactly. Neither honours the JSON converter.
    /// Contract-first endpoints are checked by --check (RIV4005) instead, where the
    /// implementing handler and its host are visible.
    /// </summary>
    private static void RejectUnbindableEnumParameters(
        WellKnownTypes wkt,
        IMethodSymbol method,
        string httpMethod
    )
    {
        var host = HostOf(method);
        var infersBody =
            host == ParameterBindingHost.MinimalApi
                ? EnumParameterBinding.MinimalApiInfersBody([httpMethod])
                : EnumParameterBinding.MvcInfersBody(wkt, method.ContainingType);
        var (path, unbindable) = method
            .Parameters.SelectMany(param =>
                EnumParameterBinding.FindUnbindableValues(wkt, param, host, infersBody)
            )
            .FirstOrDefault();
        if (unbindable is not null)
        {
            throw Diagnostics.Error(
                Diagnostics.UnbindableEnumParameter,
                "endpoint "
                    + $"'{MethodOwner(method)}.{method.Name}': "
                    + EnumParameterBinding.Explain(host, path, unbindable)
            );
        }
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

                if (attr.NamedArgument("Name") is string name && !string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The literal source text of a parameter's C# default value (for example "20"
    /// for `int limit = 20`, "default" for a default literal), or null. Surfaced
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

        if (explicitSources.Distinct().Count() > 1)
        {
            ThrowContradictoryBinding(param, explicitSources);
        }

        if (SymbolEqualityComparer.Default.Equals(param.Type, wkt.CancellationToken))
        {
            if (explicitSources.Any(source => source != BindingSource.Services))
            {
                throw Diagnostics.Error(
                    Diagnostics.UnresolvedBindingSource,
                    $"CancellationToken parameter '{param.Name}' "
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
            if (FormBinding.CarriesFiles(wkt, typeWalker, param.Type))
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
        if (FormBinding.CarriesFiles(wkt, typeWalker, param.Type))
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
    /// Refusal for an unattributed parameter whose transport source cannot be
    /// established: the extraction aborts instead of emitting a contract that
    /// silently omits user input.
    /// Never returns normally — the return exists so `??` flow analysis can see it.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static ParamSource ThrowUnresolvedBinding(IMethodSymbol method, IParameterSymbol param)
    {
        throw Diagnostics.Error(
            Diagnostics.UnresolvedBindingSource,
            $"parameter '{param.Name}' of type "
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
        throw Diagnostics.Error(
            Diagnostics.UnresolvedBindingSource,
            $"parameter '{param.Name}' of type "
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
        throw Diagnostics.Error(
            Diagnostics.UnresolvedBindingSource,
            $"parameter '{param.Name}' of type "
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
                        // successful contract: refuse instead of warn-and-omit.
                        throw Diagnostics.Error(
                            Diagnostics.UnmappedTypedResult,
                            "typed result "
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
    /// generic [ProducesResponseType&lt;T&gt;(code)] (.NET 7+) attribute.
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

        // Generic ProducesResponseTypeAttribute`1 is a distinct symbol — the body
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
            // Covers the classic and the generic [ProducesResponseType<T>] forms
            var parsed = ReadProducesResponseType(wkt, attr);
            if (parsed is not { StatusCode: int declaredStatusCode })
            {
                continue;
            }

            var statusCode = ResponseStatusValidation.AuthoredStatusCode(
                declaredStatusCode,
                $"endpoint '{MethodOwner(method)}.{method.Name}' ([ProducesResponseType])"
            );

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
                        throw Diagnostics.Error(
                            Diagnostics.ConflictingResponseDeclaration,
                            "endpoint "
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
            responses = ResponseStatusValidation.NormalizeIr(
                responses,
                Naming.ToCamelCase(method.Name)
            );
        }

        return responses;
    }

    /// <summary>
    /// The host that serves a <c>[RivetEndpoint]</c> method or a handler: a static one is a
    /// minimal-API handler (MVC never routes a static method), and an instance one is an MVC
    /// action. Generation's form and enum rules and <c>--check</c>'s verdicts all read it.
    /// </summary>
    internal static ParameterBindingHost HostOf(IMethodSymbol method) =>
        method.IsStatic ? ParameterBindingHost.MinimalApi : ParameterBindingHost.Mvc;

    /// <summary>
    /// The operations a [RivetClient] class declares: its public methods with an [Http*]
    /// verb. <see cref="Walk"/> emits them and <c>--check</c> compares their security.
    /// </summary>
    internal static IEnumerable<IMethodSymbol> ClientMethods(
        WellKnownTypes wkt,
        IEnumerable<INamedTypeSymbol> clientTypes
    ) =>
        clientTypes
            .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
            .Where(method =>
                method.DeclaredAccessibility == Accessibility.Public
                && !method.IsImplicitlyDeclared
                && HasHttpMethodAttribute(wkt, method)
            );

    internal static bool HasHttpMethodAttribute(WellKnownTypes wkt, IMethodSymbol method) =>
        method
            .GetAttributes()
            .Any(a =>
                a.AttributeClass is not null
                && wkt.HttpMethodAttributes.ContainsKey(a.AttributeClass)
            );
}
