using Microsoft.CodeAnalysis;

namespace Rivet.Tool.Analysis;

/// <summary>
/// The ASP.NET host binding a route, query, header or form parameter, which decides the
/// enum parser it uses. Neither honours System.Text.Json converters.
/// </summary>
internal enum ParameterBindingHost
{
    /// <summary>Minimal APIs call Enum.TryParse: the exact member name, case included.</summary>
    MinimalApi,

    /// <summary>MVC's EnumTypeModelBinder: the member name, ignoring case.</summary>
    Mvc,
}

/// <summary>An advertised enum wire value the host's enum parser refuses.</summary>
internal sealed record UnbindableEnumValue(
    INamedTypeSymbol EnumType,
    string MemberName,
    string WireValue
);

/// <summary>
/// Invariant: every value the spec advertises for a parameter binds on the host that
/// implements it. The spec advertises a string enum's converter-derived wire names for
/// route, query, header and form parameters too, but ASP.NET binds those with its own
/// enum parser. Extra host leniency (integers, other casings) is host behaviour Rivet
/// disclaims; only advertised values that would be refused are reported.
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
    /// True when MVC infers [FromBody] for an action's unattributed complex parameters and
    /// collections: the controller is an API controller, through an IApiBehaviorMetadata
    /// attribute such as [ApiController] on the class, a base class (the attribute is
    /// inherited) or the assembly. Without one MVC binds them from route, query and form.
    /// </summary>
    public static bool MvcInfersBody(WellKnownTypes wkt, INamedTypeSymbol controller)
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
    /// the path naming it (<c>kind</c>, or <c>request.Kind</c> for a member). Besides a
    /// directly bound enum, the members of a complex parameter the host binds member by
    /// member are checked one level deep: a minimal-API [AsParameters] type, and on MVC a
    /// complex type with an explicit [FromQuery], [FromRoute], [FromForm] or [FromHeader],
    /// or an unattributed one when MVC infers no body. <paramref name="infersBody"/> is
    /// <see cref="MinimalApiInfersBody"/> or <see cref="MvcInfersBody"/> for the endpoint.
    /// </summary>
    public static IEnumerable<(string Path, UnbindableEnumValue Value)> FindUnbindableValues(
        WellKnownTypes wkt,
        IParameterSymbol parameter,
        ParameterBindingHost host,
        bool infersBody
    )
    {
        var attributes = parameter.GetAttributes();
        if (
            IsExempt(wkt, attributes, parameter.Type, host)
            || IsInferredBody(wkt, attributes, parameter.Type, host, infersBody)
        )
        {
            yield break;
        }

        if (FindUnbindableValue(wkt, parameter.Type, host) is { } direct)
        {
            yield return (parameter.Name, direct);
            yield break;
        }

        if (
            parameter.Type is not INamedTypeSymbol complex
            || !BindsMemberByMember(wkt, parameter, complex, host, infersBody)
        )
        {
            yield break;
        }

        // An MVC complex type binds every member from its own source (the parameter's
        // explicit one, or the value providers), so only [AsParameters] members are
        // inferred one by one.
        foreach (var (name, type, memberAttributes) in BoundMembers(complex))
        {
            if (
                !IsExempt(wkt, memberAttributes, type, host)
                && !(
                    host == ParameterBindingHost.MinimalApi
                    && IsInferredBody(wkt, memberAttributes, type, host, infersBody)
                )
                && FindUnbindableValue(wkt, type, host) is { } value
            )
            {
                yield return ($"{parameter.Name}.{name}", value);
            }
        }
    }

    /// <summary>
    /// True when the parameter does not reach the host's enum parser: a JSON body
    /// ([FromBody], read by System.Text.Json, which honours the converter), a service, or
    /// on MVC an explicit custom [ModelBinder] on the parameter or its enum. Minimal APIs
    /// ignore model binders and still call Enum.TryParse.
    /// </summary>
    private static bool IsExempt(
        WellKnownTypes wkt,
        IEnumerable<AttributeData> attributes,
        ITypeSymbol type,
        ParameterBindingHost host
    ) =>
        attributes.Any(attribute =>
            attribute.Is(wkt.FromBody)
            || attribute.Is(wkt.FromServices)
            || host == ParameterBindingHost.Mvc && IsModelBinder(wkt, attribute)
        )
        || host == ParameterBindingHost.Mvc
            && BoundEnum(type) is { } enumType
            && enumType.GetAttributes().Any(attribute => IsModelBinder(wkt, attribute));

    /// <summary>
    /// True when an unattributed enum collection never reaches the enum parser. A minimal
    /// API infers a <c>T[]</c> as a JSON body when <paramref name="infersBody"/>, and from the
    /// query otherwise. Any other collection, <c>List&lt;T&gt;</c> included, it infers as a
    /// body, and on a body-less verb ASP.NET refuses the endpoint when the endpoints are built
    /// (on the first request: "Body was inferred but the method does not allow inferred body
    /// parameters"): either way there
    /// is nothing to check. MVC treats every collection as complex, inferring [FromBody] only
    /// under [ApiController] and otherwise binding it from route, query and form. An explicit
    /// [FromQuery], [FromRoute], [FromHeader] or [FromForm] still binds it.
    /// </summary>
    private static bool IsInferredBody(
        WellKnownTypes wkt,
        IEnumerable<AttributeData> attributes,
        ITypeSymbol type,
        ParameterBindingHost host,
        bool infersBody
    ) =>
        BoundEnum(type) is not null
        && (
            type is IArrayTypeSymbol && infersBody
            || type is INamedTypeSymbol { TypeArguments: [var element] } collection
                && IsEnumerableOf(collection, element)
                && (host == ParameterBindingHost.MinimalApi || infersBody)
        )
        && !attributes.Any(attribute => IsExplicitSource(wkt, attribute));

    /// <summary>
    /// A minimal API binds [AsParameters] member by member. MVC does so for a complex type
    /// with an explicit source, and for an unattributed one when no [ApiController] infers
    /// it as a body.
    /// </summary>
    private static bool BindsMemberByMember(
        WellKnownTypes wkt,
        IParameterSymbol parameter,
        INamedTypeSymbol type,
        ParameterBindingHost host,
        bool infersBody
    ) =>
        host == ParameterBindingHost.MinimalApi
            ? parameter.HasAttribute(wkt.AsParameters)
            : type is { TypeKind: TypeKind.Class or TypeKind.Struct, SpecialType: SpecialType.None }
                && (
                    !infersBody
                    || parameter.GetAttributes().Any(attribute => IsExplicitSource(wkt, attribute))
                );

    private static bool IsExplicitSource(WellKnownTypes wkt, AttributeData attribute) =>
        attribute.Is(wkt.FromQuery)
        || attribute.Is(wkt.FromRoute)
        || attribute.Is(wkt.FromForm)
        || attribute.Is(wkt.FromHeader);

    /// <summary>
    /// The members ASP.NET binds on a complex type: the parameters of its single public
    /// parameterised constructor (a record's primary constructor), carrying the attributes
    /// of both the parameter and its same-named property, then any other public settable
    /// or init-only instance property, inherited ones included.
    /// </summary>
    private static IEnumerable<(
        string Name,
        ITypeSymbol Type,
        IReadOnlyList<AttributeData> Attributes
    )> BoundMembers(INamedTypeSymbol type)
    {
        var properties = new List<IPropertySymbol>();
        for (
            var current = type;
            current is not null && current.SpecialType == SpecialType.None;
            current = current.BaseType
        )
        {
            properties.AddRange(
                current
                    .GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(property =>
                        property is { IsStatic: false, IsIndexer: false }
                        && property.DeclaredAccessibility == Accessibility.Public
                        && properties.All(seen => seen.Name != property.Name)
                    )
            );
        }

        var constructorParameters = type
            .InstanceConstructors.Where(constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public
                && constructor.Parameters.Length > 0
            )
            .ToList()
            is [var constructor]
            ? constructor.Parameters
            : [];

        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in constructorParameters)
        {
            var property = properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
            );
            bound.Add(parameter.Name);
            yield return (
                property?.Name ?? parameter.Name,
                parameter.Type,
                [.. parameter.GetAttributes(), .. property?.GetAttributes() ?? []]
            );
        }

        foreach (
            var property in properties.Where(property =>
                property.SetMethod is { DeclaredAccessibility: Accessibility.Public }
                && !bound.Contains(property.Name)
            )
        )
        {
            yield return (property.Name, property.Type, property.GetAttributes());
        }
    }

    /// <summary>
    /// The first wire value, in declaration order, that the host refuses for the enum a
    /// non-body parameter of <paramref name="parameterType"/> binds (the enum itself,
    /// <c>T?</c>, or a collection of either); null when every value binds or the type
    /// binds no string-wire enum Rivet can represent.
    /// </summary>
    private static UnbindableEnumValue? FindUnbindableValue(
        WellKnownTypes wkt,
        ITypeSymbol parameterType,
        ParameterBindingHost host
    )
    {
        if (
            BoundEnum(parameterType) is not { } enumType
            || TypeWalker.StringEnumWireValues(wkt, enumType) is not { } values
        )
        {
            return null;
        }

        var comparison =
            host == ParameterBindingHost.MinimalApi
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
        return values
            .Where(value => !string.Equals(value.Member.Name, value.WireValue, comparison))
            .Select(value => new UnbindableEnumValue(enumType, value.Member.Name, value.WireValue))
            .FirstOrDefault();
    }

    /// <summary>The shared RIV1110/RIV4005 explanation and remedy.</summary>
    public static string Explain(
        ParameterBindingHost host,
        string parameterPath,
        UnbindableEnumValue value
    )
    {
        var (hostLabel, parser) = host switch
        {
            ParameterBindingHost.MinimalApi => (
                "minimal API",
                "Enum.TryParse, which matches member names exactly, case included"
            ),
            _ => ("MVC", "MVC's enum model binder, which matches member names ignoring case"),
        };
        var enumName = value.EnumType.Name;
        return $"{hostLabel} parameter '{parameterPath}' binds enum '{enumName}' with {parser}, "
            + $"not System.Text.Json, but the spec advertises '{value.WireValue}' for member "
            + $"{value.MemberName}, which the host refuses with 400. Use a plain enum (integers on "
            + $"the wire), the built-in JsonStringEnumConverter<{enumName}> without "
            + "[JsonStringEnumMemberName] renames, or bind a string and parse it in the handler.";
    }

    /// <summary>The enum a parameter type binds: the enum, <c>T?</c>, or a collection of either.</summary>
    private static INamedTypeSymbol? BoundEnum(ITypeSymbol type) =>
        type switch
        {
            INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType => enumType,
            INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                TypeArguments: [var inner],
            } => BoundEnum(inner),
            IArrayTypeSymbol { ElementType: var element } => BoundEnum(element),
            INamedTypeSymbol { TypeArguments: [var element] } collection
                when IsEnumerableOf(collection, element) => BoundEnum(element),
            _ => null,
        };

    private static bool IsEnumerableOf(INamedTypeSymbol collection, ITypeSymbol element) =>
        collection
            .AllInterfaces.Append(collection)
            .Any(candidate =>
                candidate.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_IEnumerable_T
                && SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0], element)
            );

    /// <summary>[ModelBinder] or a subclass such as the generic [ModelBinder&lt;TBinder&gt;].</summary>
    private static bool IsModelBinder(WellKnownTypes wkt, AttributeData attribute)
    {
        for (
            var type = attribute.AttributeClass;
            type is not null && wkt.ModelBinder is not null;
            type = type.BaseType
        )
        {
            if (SymbolEqualityComparer.Default.Equals(type, wkt.ModelBinder))
            {
                return true;
            }
        }

        return false;
    }
}
