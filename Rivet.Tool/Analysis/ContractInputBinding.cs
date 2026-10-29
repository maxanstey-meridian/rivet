using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>One way a handler's binding disagrees with its contract's inputs (RIV4006).</summary>
internal sealed record InputBindingMismatch(
    string Expected,
    string Actual,
    string Problem,
    Location? Location
);

/// <summary>
/// RIV4006: the contract's inputs against what the handler implementing it binds. The spec
/// (and every client generated from it) sends each input where the contract declares it, by
/// the name it declares, so the handler has to read it there: route values, query parameters,
/// headers, cookies and form fields (a form-encoded body's fields included) are matched by
/// name, ignoring case as ASP.NET does, and a JSON or binary body by presence. What the
/// handler binds is <see cref="ParameterBindingSources"/>' reading of its parameters, the same
/// one RIV4005 uses. Three things are reported:
/// <list type="bullet">
/// <item>an input the handler binds that the contract doesn't declare, which no generated
/// client sends;</item>
/// <item>an input the contract declares that the handler never binds, which it ignores;</item>
/// <item>an input both have under one name but from different places, which never arrives.</item>
/// </list>
/// A handler that can read the request itself (an HttpContext or HttpRequest parameter, a
/// BindAsync type, a custom model binder, or a body that reads the request or its route
/// values, such as a controller's <c>Request</c> or <c>RouteData</c>,
/// <c>ControllerContext.HttpContext.Request</c>, <c>IHttpContextAccessor.HttpContext!.Request</c>
/// or <c>HttpContext.GetRouteValue</c>) may read a declared input by hand, so it's never told
/// an input is unbound; what it does bind is still checked. Touching only the HttpContext
/// (its User, RequestAborted or Response) reads no input. ASP.NET has no attribute that
/// binds a cookie, so a declared cookie needs such a handler. An unattributed complex parameter that might be a service counts as the
/// body when the contract declares one and as a service when it doesn't. The
/// <c>Authorization</c> header, a <c>.QueryAuth()</c> token and the header or query parameter
/// of an apiKey scheme the endpoint's security names belong to the contract's security, not
/// its inputs. An Azure Functions trigger hands over the whole request, so a function is never
/// told an input is unbound, but what its other parameters bind is checked: the worker fills
/// each by name from a route value, else the query, else a JSON body's top-level property
/// (<see cref="InputSource.TriggerMetadata"/>), and a name none of those carries is null. A
/// name that matches no route token, no declared query parameter and no top-level property of
/// a declared JSON body (<see cref="TsEndpointDefinition.BodyFieldNames"/>) is reported; only a
/// body with no closed list of names (a dictionary, an object with extension data) lets every
/// name through.
/// </summary>
internal static class ContractInputBinding
{
    public static IEnumerable<InputBindingMismatch> Compare(
        WellKnownTypes wkt,
        ContractSecurityMetadata? security,
        TsEndpointDefinition endpoint,
        IMethodSymbol handler,
        ParameterBindingHost host,
        bool infersBody,
        string? route
    )
    {
        var body = DeclaredBody(endpoint);
        var declared = DeclaredInputs(endpoint, body);
        var apiKeys = ApiKeySchemes(endpoint, security);
        var routeParameters = RouteParser.ParseRouteParamNames(route ?? endpoint.RouteTemplate);
        var inputs = handler
            .Parameters.SelectMany(parameter =>
                ParameterBindingSources.Inputs(wkt, parameter, host, infersBody, routeParameters)
            )
            .Where(input => !input.Nested)
            .ToList();
        var readsRequest =
            inputs.Any(input => input.Source == InputSource.WholeRequest)
            || ReadsRequestInBody(handler);
        var readsWholeForm = inputs.Any(input => input.Source == InputSource.WholeForm);

        var settled = new HashSet<DeclaredInput>();
        foreach (var input in inputs.Where(input => Accepts(input.Source).Count > 0))
        {
            if (IsSecurityInput(endpoint, apiKeys, input.Source, input.WireName))
            {
                continue;
            }

            var sameName = declared
                .Where(candidate =>
                    string.Equals(
                        candidate.Name,
                        input.WireName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToList();
            var matching = sameName
                .Where(candidate => Accepts(input.Source).Contains(candidate.Source))
                .ToList();
            if (
                matching.Count == 0
                && input.Source == InputSource.TriggerMetadata
                && body == DeclaredBodyKind.Json
                && (
                    endpoint.BodyFieldNames is not { } bodyNames
                    || bodyNames.Contains(input.WireName, StringComparer.OrdinalIgnoreCase)
                )
            )
            {
                // The worker fills it from the JSON body's property of that name (the body is
                // compared by presence), or the body takes names no list can hold.
                continue;
            }

            // A same-named input from another source (route {id} and form field id) stays
            // unsettled, so forgetting it is still reported.
            settled.UnionWith(matching.Count > 0 ? matching : sameName);
            if (matching.Count > 0)
            {
                continue;
            }

            if (sameName is [var other, ..])
            {
                yield return new InputBindingMismatch(
                    Describe(other.Source, other.Name),
                    Describe(input.Source, input.WireName),
                    $"the contract declares {Describe(other.Source, other.Name)}, but handler "
                        + $"parameter '{input.Path}' binds it as {Describe(input.Source, input.WireName)}, "
                        + "so the value clients send never reaches it. "
                        + $"{BindHint(host, other.Source, other.Name)}",
                    input.Parameter.Locations.FirstOrDefault()
                );
                continue;
            }

            yield return new InputBindingMismatch(
                "(not declared)",
                Describe(input.Source, input.WireName),
                $"handler parameter '{input.Path}' binds {Describe(input.Source, input.WireName)}, "
                    + "which the contract doesn't declare, so no client generated from the spec "
                    + $"sends it. {DeclareHint(input.Source, input.WireName)}",
                input.Parameter.Locations.FirstOrDefault()
            );
        }

        foreach (
            var mismatch in CompareBody(
                endpoint,
                body,
                inputs,
                readsRequest,
                readsWholeForm,
                handler,
                host,
                infersBody
            )
        )
        {
            yield return mismatch;
        }

        foreach (var input in declared.Where(input => !settled.Contains(input)))
        {
            if (readsRequest || input.Source == InputSource.Form && readsWholeForm)
            {
                continue;
            }

            var remove =
                input.Source == InputSource.Cookie
                    ? "Or remove the cookie parameter from the contract."
                    : "Or remove it from the contract's input type.";
            yield return new InputBindingMismatch(
                Describe(input.Source, input.Name),
                "(not bound)",
                $"the contract declares {Describe(input.Source, input.Name)}, but the handler "
                    + "never binds it, so the value clients send is ignored. "
                    + $"{BindHint(host, input.Source, input.Name)} {remove}",
                handler.Locations.FirstOrDefault()
            );
        }
    }

    private static IEnumerable<InputBindingMismatch> CompareBody(
        TsEndpointDefinition endpoint,
        DeclaredBodyKind body,
        IReadOnlyList<BoundInput> inputs,
        bool readsRequest,
        bool readsWholeForm,
        IMethodSymbol handler,
        ParameterBindingHost host,
        bool infersBody
    )
    {
        var bodies = inputs.Where(input => input.Source == InputSource.Body).ToList();
        var mayBeBody = inputs.Any(input => input.Source == InputSource.BodyOrService);
        switch (body)
        {
            case DeclaredBodyKind.None when bodies is [var undeclared, ..]:
                yield return new InputBindingMismatch(
                    "(no request body)",
                    "request body",
                    $"handler parameter '{undeclared.Path}' reads the request body, but "
                        + $"{endpoint.HttpMethod} {endpoint.RouteTemplate} declares none, so no client "
                        + "generated from the spec sends one. Give the contract an input type that "
                        + "lowers to a body (Define.Post<TInput, TOutput>, .Accepts<TInput>()), or "
                        + "stop binding the body in the handler.",
                    undeclared.Parameter.Locations.FirstOrDefault()
                );
                break;
            case DeclaredBodyKind.Json
            or DeclaredBodyKind.Binary when bodies.Count == 0 && !mayBeBody && !readsRequest:
                yield return new InputBindingMismatch(
                    "request body",
                    "(not bound)",
                    "the contract declares a request body, but the handler binds none, so the "
                        + "body clients send is ignored. Bind it with a [FromBody] parameter."
                        + (
                            host == ParameterBindingHost.Mvc && !infersBody
                                ? " Without [ApiController] inference, MVC binds an unattributed "
                                    + "complex parameter from the route, query and form, never the body."
                                : ""
                        ),
                    handler.Locations.FirstOrDefault()
                );
                break;
            case DeclaredBodyKind.Form:
                if (
                    bodies.FirstOrDefault(input =>
                        !ParameterBindingSources.IsRawBody(input.Type)
                    ) is
                    { } json
                )
                {
                    yield return new InputBindingMismatch(
                        "form body",
                        "JSON request body",
                        $"the contract declares a form body, but handler parameter '{json.Path}' "
                            + "reads the request body as JSON, so the form clients send never "
                            + "binds. "
                            + (
                                host == ParameterBindingHost.Functions
                                    ? "Read the form from the trigger's request (Form) instead of "
                                        + "a [FromBody] parameter."
                                    : "Bind it with [FromForm] instead of [FromBody]."
                            ),
                        json.Parameter.Locations.FirstOrDefault()
                    );
                }
                else if (
                    bodies.Count == 0
                    && !readsRequest
                    && !readsWholeForm
                    && !inputs.Any(input =>
                        input.Source is InputSource.Form or InputSource.ValueProviders
                    )
                )
                {
                    yield return new InputBindingMismatch(
                        "form body",
                        "(not bound)",
                        "the contract declares a form body, but the handler binds no form field, "
                            + "so the form clients send is ignored. Bind it with [FromForm].",
                        handler.Locations.FirstOrDefault()
                    );
                }

                break;
        }
    }

    private sealed record DeclaredInput(string Name, InputSource Source);

    private enum DeclaredBodyKind
    {
        None,
        Json,
        Form,
        Binary,
    }

    /// <summary>
    /// The contract's named inputs: route, query, header and cookie parameters, the named
    /// parts of a multipart body, and the fields of a form-encoded body, which ContractWalker
    /// records as it lowers the body (<see cref="TsEndpointDefinition.BodyFieldNames"/>).
    /// </summary>
    private static List<DeclaredInput> DeclaredInputs(
        TsEndpointDefinition endpoint,
        DeclaredBodyKind body
    )
    {
        var declared = endpoint
            .Params.Select(parameter =>
                parameter.Source switch
                {
                    ParamSource.Route => new DeclaredInput(parameter.Name, InputSource.Route),
                    ParamSource.Query => new DeclaredInput(parameter.Name, InputSource.Query),
                    ParamSource.Header => new DeclaredInput(parameter.Name, InputSource.Header),
                    ParamSource.Cookie => new DeclaredInput(parameter.Name, InputSource.Cookie),
                    ParamSource.File or ParamSource.FormField => new DeclaredInput(
                        parameter.Name,
                        InputSource.Form
                    ),
                    _ => null,
                }
            )
            .OfType<DeclaredInput>()
            .ToList();

        if (
            body == DeclaredBodyKind.Form
            && !declared.Any(input => input.Source == InputSource.Form)
        )
        {
            declared.AddRange(
                (endpoint.BodyFieldNames ?? []).Select(name => new DeclaredInput(
                    name,
                    InputSource.Form
                ))
            );
        }

        return declared;
    }

    private static DeclaredBodyKind DeclaredBody(TsEndpointDefinition endpoint) =>
        endpoint.Params.Any(parameter =>
            parameter.Source is ParamSource.File or ParamSource.FormField
        )
            ? DeclaredBodyKind.Form
        : endpoint.Params.Any(parameter => parameter.Source == ParamSource.Body)
            ? endpoint.IsFormEncoded ? DeclaredBodyKind.Form
                : DeclaredBodyKind.Json
        : endpoint.BinaryRequestContentType is not null ? DeclaredBodyKind.Binary
        : endpoint.RequestBodyPresent ? DeclaredBodyKind.Json
        : DeclaredBodyKind.None;

    /// <summary>The contract sources a handler input of <paramref name="source"/> can satisfy.</summary>
    private static IReadOnlyList<InputSource> Accepts(InputSource source) =>
        source switch
        {
            InputSource.Route => [InputSource.Route],
            InputSource.Query => [InputSource.Query],
            InputSource.Header => [InputSource.Header],
            InputSource.Form => [InputSource.Form],
            InputSource.ValueProviders => [InputSource.Route, InputSource.Query, InputSource.Form],
            InputSource.TriggerMetadata => [InputSource.Query],
            _ => [],
        };

    /// <summary>
    /// The apiKey schemes in the endpoint's security alternatives (its own, or the document's
    /// when it declares none): each puts its credential in a header or query parameter.
    /// </summary>
    private static List<ApiKeySecurityScheme> ApiKeySchemes(
        TsEndpointDefinition endpoint,
        ContractSecurityMetadata? security
    ) =>
        (endpoint.Security ?? security?.GlobalRequirements) is { } requirements
        && security is not null
            ? requirements
                .Alternatives.SelectMany(alternative => alternative.Schemes)
                .Select(scheme => security.Schemes.GetValueOrDefault(scheme.Name))
                .OfType<ApiKeySecurityScheme>()
                .ToList()
            : [];

    private static bool IsSecurityInput(
        TsEndpointDefinition endpoint,
        IReadOnlyList<ApiKeySecurityScheme> apiKeys,
        InputSource source,
        string name
    )
    {
        var query =
            source
            is InputSource.Query
                or InputSource.ValueProviders
                or InputSource.TriggerMetadata;
        return source == InputSource.Header
                && string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
            || query
                && endpoint.QueryAuth is { ParameterName: var token }
                && string.Equals(name, token, StringComparison.OrdinalIgnoreCase)
            || apiKeys.Any(scheme =>
                (
                    scheme.Location == SecurityApiKeyLocation.Header && source == InputSource.Header
                    || scheme.Location == SecurityApiKeyLocation.Query && query
                ) && string.Equals(name, scheme.Name, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static string Describe(InputSource source, string name) =>
        source switch
        {
            InputSource.Route => $"route value '{name}'",
            InputSource.Query or InputSource.TriggerMetadata => $"query parameter '{name}'",
            InputSource.Header => $"header '{name}'",
            InputSource.Cookie => $"cookie '{name}'",
            InputSource.Form => $"form field '{name}'",
            InputSource.ValueProviders => $"route, query or form value '{name}'",
            _ => $"'{name}'",
        };

    private static string BindHint(ParameterBindingHost host, InputSource source, string name) =>
        (host, source) switch
        {
            (ParameterBindingHost.Functions, InputSource.Route) =>
                $"Give the trigger's Route a {{{name}}} token, so the worker fills the parameter "
                    + "from it.",
            (ParameterBindingHost.Functions, _) =>
                "Read it from the trigger's request: the worker fills a parameter only from a "
                    + "route value, a query parameter or a JSON body property of its name.",
            (_, InputSource.Route) =>
                $"Bind it with [FromRoute(Name = \"{name}\")], or a parameter named '{name}'.",
            (_, InputSource.Query) => $"Bind it with [FromQuery(Name = \"{name}\")].",
            (_, InputSource.Header) => $"Bind it with [FromHeader(Name = \"{name}\")].",
            (_, InputSource.Cookie) =>
                $"ASP.NET has no attribute that binds a cookie, so read it from Request.Cookies[\"{name}\"] "
                    + "(on a minimal API, through an HttpRequest or HttpContext parameter).",
            _ =>
                $"Bind it with [FromForm(Name = \"{name}\")], or an IFormFile named '{name}' for a file.",
        };

    /// <summary>How to declare an input the handler binds but the contract doesn't.</summary>
    private static string DeclareHint(InputSource source, string name) =>
        source switch
        {
            InputSource.Route =>
                $"The contract's route has no {{{name}}} token: add one to it, or stop binding "
                    + "the value in the handler.",
            InputSource.TriggerMetadata =>
                "The Functions worker fills a parameter with no binding attribute by its name, "
                    + "from a route value, a query parameter or a JSON body property, and the "
                    + "contract declares no input of that name, so it's always null (a misspelt "
                    + "route token, say). Rename it to an input the contract declares, or declare "
                    + "it on the contract's input type.",
            InputSource.Header =>
                "Declare it on the contract's input type with [RivetHeader], or stop binding it "
                    + "in the handler.",
            _ => "Declare it on the contract's input type (a property), or stop binding it in the "
                + "handler.",
        };

    /// <summary>
    /// True when the handler's body reaches the request or its route values
    /// (<see cref="ParameterBindingSources.ReadsRequest"/>), so it can pick any input out by
    /// hand. A method or property getter it calls on <c>this</c> (its own type's or a base's)
    /// is followed when its source is in the compilation, transitively, each once: a
    /// controller's private helper reads the same <c>Request</c>. Any other helper reaches
    /// the request only through an argument, which counts already.
    /// </summary>
    private static bool ReadsRequestInBody(IMethodSymbol handler) =>
        ReadsRequestInBody(handler, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default));

    private static bool ReadsRequestInBody(IMethodSymbol method, HashSet<IMethodSymbol> followed) =>
        followed.Add(method.OriginalDefinition)
        && CoverageChecker.SourceBody(method) is { } body
        && body.Descendants()
            .Any(operation =>
                ParameterBindingSources.ReadsRequest(operation)
                || OwnHelper(operation) is { } helper && ReadsRequestInBody(helper, followed)
            );

    /// <summary>The method or property getter <paramref name="operation"/> calls on <c>this</c>.</summary>
    private static IMethodSymbol? OwnHelper(IOperation operation) =>
        operation switch
        {
            IInvocationOperation
            {
                Instance: IInstanceReferenceOperation
                {
                    ReferenceKind: InstanceReferenceKind.ContainingTypeInstance,
                },
                TargetMethod: var method,
            } => method,
            IPropertyReferenceOperation
            {
                Instance: IInstanceReferenceOperation
                {
                    ReferenceKind: InstanceReferenceKind.ContainingTypeInstance,
                },
                Property.GetMethod: { } getter,
            } => getter,
            _ => null,
        };
}
