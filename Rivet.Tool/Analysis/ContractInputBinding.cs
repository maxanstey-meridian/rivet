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
/// BindAsync type, a custom model binder, or a body reading a property that hands over the
/// request, such as a controller's <c>Request</c>, <c>HttpContext</c> or
/// <c>ControllerContext.HttpContext</c>, or <c>IHttpContextAccessor.HttpContext</c>) may read
/// a declared input by hand, so it's never told an input is unbound; what it does bind is
/// still checked. ASP.NET has no attribute that binds a cookie, so a declared cookie needs
/// such a handler. An unattributed complex parameter that might be a service counts as the
/// body when the contract declares one and as a service when it doesn't. The
/// <c>Authorization</c> header, a <c>.QueryAuth()</c> token and the header or query parameter
/// of an apiKey scheme the endpoint's security names belong to the contract's security, not
/// its inputs. Azure Functions handlers aren't compared: they read the query, headers and
/// body from the request by hand, so only route values are bound.
/// </summary>
internal static class ContractInputBinding
{
    public static IEnumerable<InputBindingMismatch> Compare(
        WellKnownTypes wkt,
        TypeWalker walker,
        ContractSecurityMetadata? security,
        IFieldSymbol field,
        TsEndpointDefinition endpoint,
        IMethodSymbol handler,
        ParameterBindingHost host,
        bool infersBody,
        string? route
    )
    {
        if (host == ParameterBindingHost.Functions)
        {
            yield break;
        }

        var body = DeclaredBody(endpoint);
        var declared = DeclaredInputs(wkt, walker, field, endpoint, body);
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
            settled.UnionWith(sameName);
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
                        + $"{BindHint(other.Source, other.Name)}",
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
                    + $"{BindHint(input.Source, input.Name)} {remove}",
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
                            + "binds. Bind it with [FromForm] instead of [FromBody].",
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
    /// parts of a multipart body, and the fields of a form-encoded body. Those fields are the
    /// input type's request properties under their JSON names, less any a route token takes,
    /// as ContractWalker lowers them into the form schema.
    /// </summary>
    private static List<DeclaredInput> DeclaredInputs(
        WellKnownTypes wkt,
        TypeWalker walker,
        IFieldSymbol field,
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
            && InputType(wkt, field) is { } inputType
        )
        {
            var routeTokens = RouteParser
                .ParseRouteParamNames(endpoint.RouteTemplate)
                .Select(RouteParser.NormalizeForMatching)
                .ToHashSet(StringComparer.Ordinal);
            declared.AddRange(
                walker
                    .GetRequestProperties(inputType)
                    .Where(property =>
                        !routeTokens.Contains(RouteParser.NormalizeForMatching(property.Name))
                    )
                    .Select(property => new DeclaredInput(
                        walker.GetJsonMemberName(property) ?? Naming.ToCamelCase(property.Name),
                        InputSource.Form
                    ))
            );
        }

        return declared;
    }

    /// <summary>The contract's input type: the TInput its <c>.Bind(...)</c> takes.</summary>
    private static ITypeSymbol? InputType(WellKnownTypes wkt, IFieldSymbol field) =>
        field.Type is INamedTypeSymbol { TypeArguments: [var input, ..] } definition
        && new[]
        {
            wkt.RouteDefinitionOfTInputTOutput,
            wkt.InputRouteDefinitionOfT,
            wkt.FileRouteDefinitionOfT,
        }.Contains(definition.OriginalDefinition, SymbolEqualityComparer.Default)
            ? input
            : null;

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
        var query = source is InputSource.Query or InputSource.ValueProviders;
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
            InputSource.Query => $"query parameter '{name}'",
            InputSource.Header => $"header '{name}'",
            InputSource.Cookie => $"cookie '{name}'",
            InputSource.Form => $"form field '{name}'",
            InputSource.ValueProviders => $"route, query or form value '{name}'",
            _ => $"'{name}'",
        };

    private static string BindHint(InputSource source, string name) =>
        source switch
        {
            InputSource.Route =>
                $"Bind it with [FromRoute(Name = \"{name}\")], or a parameter named '{name}'.",
            InputSource.Query => $"Bind it with [FromQuery(Name = \"{name}\")].",
            InputSource.Header => $"Bind it with [FromHeader(Name = \"{name}\")].",
            InputSource.Cookie =>
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
            InputSource.Header =>
                "Declare it on the contract's input type with [RivetHeader], or stop binding it "
                    + "in the handler.",
            _ => "Declare it on the contract's input type (a property), or stop binding it in the "
                + "handler.",
        };

    /// <summary>
    /// True when the handler's body reads a property that hands over the request (one typed
    /// HttpContext or HttpRequest): a controller's <c>Request</c>, <c>HttpContext</c> or
    /// <c>ControllerContext.HttpContext</c>, <c>IHttpContextAccessor.HttpContext</c> on a
    /// parameter or an injected field, or <c>context.Request</c>. It can then pick any input
    /// out of the request by hand. Helpers the handler calls aren't followed.
    /// </summary>
    private static bool ReadsRequestInBody(IMethodSymbol handler)
    {
        if (
            handler.ContainingAssembly is not ISourceAssemblySymbol { Compilation: var compilation }
            || handler.DeclaringSyntaxReferences is not [var reference]
        )
        {
            return false;
        }

        var syntax = reference.GetSyntax();
        return compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax) is { } body
            && body.Descendants()
                .OfType<IPropertyReferenceOperation>()
                .Any(property => ParameterBindingSources.IsRequest(property.Property.Type));
    }
}
