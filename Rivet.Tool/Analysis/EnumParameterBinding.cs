using Microsoft.CodeAnalysis;

namespace Rivet.Tool.Analysis;

/// <summary>
/// The host binding a handler's parameters, which decides the binding rules and the enum
/// parser it uses. None of them honours System.Text.Json converters for a route, query,
/// header or form value.
/// </summary>
internal enum ParameterBindingHost
{
    /// <summary>Minimal APIs call Enum.TryParse: the exact member name, case included.</summary>
    MinimalApi,

    /// <summary>MVC's EnumTypeModelBinder: the member name, ignoring case.</summary>
    Mvc,

    /// <summary>
    /// An Azure Functions isolated worker: route and query values reach the worker's input
    /// converters, which parse an enum's text as JSON.
    /// </summary>
    Functions,
}

/// <summary>An advertised enum wire value the host's enum parser refuses.</summary>
internal sealed record UnbindableEnumValue(
    INamedTypeSymbol EnumType,
    string MemberName,
    string WireValue,
    EnumParser Parser
);

/// <summary>
/// Invariant: every value the spec advertises for a parameter binds on the host that
/// implements it. The spec advertises a string enum's converter-derived wire names for
/// route, query, header and form parameters too, but the host binds those with its own
/// enum parser. Extra host leniency (integers, other casings) is host behaviour Rivet
/// disclaims; only advertised values that would be refused are reported. Which values reach
/// which parser is <see cref="ParameterBindingSources"/>' call.
/// </summary>
internal static class EnumParameterBinding
{
    /// <summary>
    /// The verbs that normally carry no body. A minimal-API endpoint mapping any of these
    /// turns off inferred bodies (RouteEndpointDataSource.ShouldDisableInferredBodyParameters),
    /// compared ordinally on the verbs as written: MapGet and friends pass the uppercase
    /// constants, but a MapMethods "get" matches none of them and still infers a body.
    /// </summary>
    private static readonly HashSet<string> _bodylessMethods = new(
        ["GET", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"],
        StringComparer.Ordinal
    );

    /// <summary>
    /// True when a minimal API mapped to <paramref name="httpMethods"/> (as written, not
    /// case-normalised) infers a JSON body for an unattributed array: none of its verbs is
    /// body-less. An endpoint with no known verbs accepts any request, so it infers a body too.
    /// </summary>
    public static bool MinimalApiInfersBody(IEnumerable<string> httpMethods) =>
        !httpMethods.Any(_bodylessMethods.Contains);

    /// <summary>
    /// True when MVC infers binding sources for an action's unattributed parameters: the
    /// controller is an API controller, through an IApiBehaviorMetadata attribute such as
    /// [ApiController] on the class, a base class (the attribute is inherited) or the
    /// assembly, and the app doesn't switch the inference off with
    /// <c>SuppressInferBindingSourcesForParameters</c>
    /// (<see cref="ParameterBindingSources.SuppressesInferredBindingSources"/>). Without it MVC
    /// binds every parameter from the value providers: route, query and form.
    /// </summary>
    public static bool MvcInfersBody(WellKnownTypes wkt, INamedTypeSymbol controller)
    {
        // The setting is read only for an API controller: a non-constant one refuses
        // (RIV1115), and it can't change how a plain controller binds.
        return IsApiController(wkt, controller)
            && !(
                controller.ContainingAssembly
                    is ISourceAssemblySymbol { Compilation: var compilation }
                && ParameterBindingSources.SuppressesInferredBindingSources(compilation)
            );
    }

    private static bool IsApiController(WellKnownTypes wkt, INamedTypeSymbol controller)
    {
        for (var type = controller; type is not null; type = type.BaseType)
        {
            if (type.GetAttributes().Any(attribute => IsApiBehavior(wkt, attribute)))
            {
                return true;
            }
        }

        return controller
            .ContainingAssembly.GetAttributes()
            .Any(attribute => IsApiBehavior(wkt, attribute));
    }

    private static bool IsApiBehavior(WellKnownTypes wkt, AttributeData attribute) =>
        wkt.ApiBehaviorMetadata is { } apiBehavior
        && attribute.AttributeClass is { } attributeClass
        && attributeClass.AllInterfaces.Contains(apiBehavior, SymbolEqualityComparer.Default);

    /// <summary>
    /// Every advertised enum wire value the host would refuse for a handler parameter, with
    /// the path naming it (<c>kind</c>, or <c>request.Kind</c> for a member, at any depth).
    /// <paramref name="infersBody"/> is <see cref="MinimalApiInfersBody"/> or
    /// <see cref="MvcInfersBody"/> for the endpoint, and unused for Azure Functions.
    /// </summary>
    public static IEnumerable<(string Path, UnbindableEnumValue Value)> FindUnbindableValues(
        WellKnownTypes wkt,
        IParameterSymbol parameter,
        ParameterBindingHost host,
        bool infersBody
    ) =>
        ParameterBindingSources
            .Inputs(wkt, parameter, host, infersBody, routeParameters: null)
            .Where(input => input.Parser != EnumParser.None)
            .Select(input => (input.Path, Value: FindUnbindableValue(wkt, input)))
            .Where(found => found.Value is not null)
            .Select(found => (found.Path, found.Value!));

    /// <summary>
    /// The first wire value, in declaration order, that the input's parser refuses for the
    /// enum it binds (the enum itself, <c>T?</c>, or a collection of either); null when every
    /// value binds or the type binds no string-wire enum Rivet can represent. The Functions
    /// worker refuses every name, so its first value is the answer.
    /// </summary>
    private static UnbindableEnumValue? FindUnbindableValue(WellKnownTypes wkt, BoundInput input)
    {
        if (
            ParameterBindingSources.BoundEnum(input.Type) is not { } enumType
            || TypeWalker.StringEnumWireValues(wkt, enumType) is not { } values
        )
        {
            return null;
        }

        return values
            .Where(value =>
                input.Parser switch
                {
                    EnumParser.Exact => !string.Equals(
                        value.Member.Name,
                        value.WireValue,
                        StringComparison.Ordinal
                    ),
                    EnumParser.IgnoreCase => !string.Equals(
                        value.Member.Name,
                        value.WireValue,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    _ => true,
                }
            )
            .Select(value => new UnbindableEnumValue(
                enumType,
                value.Member.Name,
                value.WireValue,
                input.Parser
            ))
            .FirstOrDefault();
    }

    /// <summary>The shared RIV1110/RIV4005 explanation and remedy.</summary>
    public static string Explain(
        ParameterBindingHost host,
        string parameterPath,
        UnbindableEnumValue value
    )
    {
        var enumName = value.EnumType.Name;
        if (value.Parser == EnumParser.FunctionsJson)
        {
            return $"Azure Functions parameter '{parameterPath}' gets enum '{enumName}' from the "
                + "worker's input converters, which read a route or query value as JSON, so the "
                + $"'{value.WireValue}' the spec advertises for member {value.MemberName} fails to "
                + "convert, quoted or not, and the function never runs (only an integer converts). "
                + "Use a plain enum "
                + "(integers on the wire), or bind a string and parse it in the function.";
        }

        var hostLabel = host == ParameterBindingHost.MinimalApi ? "minimal API" : "MVC";
        var parser = (host, value.Parser) switch
        {
            (ParameterBindingHost.MinimalApi, EnumParser.Exact) =>
                "Enum.TryParse, which matches member names exactly, case included",
            (ParameterBindingHost.MinimalApi, _) =>
                "the form mapper, which matches member names ignoring case",
            _ => "MVC's enum model binder, which matches member names ignoring case",
        };
        return $"{hostLabel} parameter '{parameterPath}' binds enum '{enumName}' with {parser}, "
            + $"not System.Text.Json, but the spec advertises '{value.WireValue}' for member "
            + $"{value.MemberName}, which the host refuses with 400. Use a plain enum (integers on "
            + $"the wire), the built-in JsonStringEnumConverter<{enumName}> without "
            + "[JsonStringEnumMemberName] renames, or bind a string and parse it in the handler.";
    }
}
