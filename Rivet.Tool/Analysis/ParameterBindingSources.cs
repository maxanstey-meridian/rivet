using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Rivet.Tool.Analysis;

/// <summary>Where the host reads a handler input from.</summary>
internal enum InputSource
{
    Route,
    Query,
    Header,
    Form,
    Body,

    /// <summary>MVC with no binding source: form, route or query values, by name.</summary>
    ValueProviders,

    /// <summary>
    /// A simple parameter a minimal API or an [ApiController] action reads from the route when
    /// the route template has a parameter of that name, and from the query otherwise, left
    /// unresolved when the route isn't known (generation's RIV1110 doesn't pass one). Enum
    /// parsing is the same either way.
    /// </summary>
    RouteOrQuery,

    /// <summary>
    /// An unattributed complex parameter: a JSON body, unless it's a registered service, which
    /// static analysis can't see.
    /// </summary>
    BodyOrService,

    /// <summary>
    /// An Azure Functions parameter the worker fills from the trigger's binding data by name
    /// that isn't a route value of the handler's route: a query parameter, else a top-level
    /// property of a JSON body (never a header or a form field). A route value is
    /// <see cref="Route"/> when the route is known, and this when it isn't.
    /// </summary>
    TriggerMetadata,

    /// <summary>
    /// The request itself (HttpContext, HttpRequest, an Azure Functions trigger), a minimal-API
    /// BindAsync type or an MVC custom model binder: it can read anything.
    /// </summary>
    WholeRequest,

    /// <summary>IFormCollection or IFormFileCollection: every form field or file at once.</summary>
    WholeForm,

    /// <summary>
    /// A cookie. Only a contract declares one: ASP.NET has no attribute that binds a cookie,
    /// so a handler reads it from the request itself.
    /// </summary>
    Cookie,

    /// <summary>A service, a CancellationToken, a ClaimsPrincipal: nothing from the request.</summary>
    NotRequest,

    /// <summary>
    /// A complex parameter or member whose binder can't create its type
    /// (<see cref="FormBinding.CantCreate"/>): <c>[AsParameters]</c> refuses the endpoint at
    /// startup, MVC fails the request, and the form mapper binds none of its fields.
    /// <see cref="BoundInput.CantCreate"/> says why. The members the binder would bind if it
    /// could create the type follow it, so the rest of the comparison still reads them.
    /// </summary>
    Uncreatable,
}

/// <summary>How the host turns the text it binds into an enum.</summary>
internal enum EnumParser
{
    /// <summary>The input never reaches a text-to-enum parser (a body, a service, the request).</summary>
    None,

    /// <summary>Enum.TryParse, case-sensitive: minimal-API route, query, header and form values.</summary>
    Exact,

    /// <summary>
    /// The member name ignoring case: MVC's EnumTypeModelBinder, and the form mapper behind a
    /// minimal-API [FromForm] complex type (Enum.TryParse with ignoreCase).
    /// </summary>
    IgnoreCase,

    /// <summary>
    /// The Azure Functions worker's input converters: none of them turns a string into an
    /// enum, so a route or query value falls through to JsonPocoConverter, which parses the
    /// raw text as JSON. A member name, quoted or not, doesn't convert; only an integer does,
    /// and anything else fails the invocation with a 500 (pinned by tools/test_functions_host.py).
    /// </summary>
    FunctionsJson,
}

/// <summary>
/// One value a handler binds: a parameter, or a member of a complex parameter the host binds
/// member by member. <see cref="Path"/> names it for messages (<c>kind</c>, <c>request.Kind</c>);
/// <see cref="WireName"/> is the key the host looks up. A member of a complex member is
/// <see cref="Nested"/>: its key carries the outer member's prefix (<c>Filter.Kind</c>).
/// <see cref="CantCreate"/> is set on an <see cref="InputSource.Uncreatable"/> input alone: why
/// the host can't create it, and what it does instead.
/// </summary>
internal sealed record BoundInput(
    string Path,
    string WireName,
    InputSource Source,
    ITypeSymbol Type,
    IParameterSymbol Parameter,
    EnumParser Parser,
    bool Nested = false,
    string? CantCreate = null
);

/// <summary>
/// What ASP.NET (or the Functions worker) binds for each handler parameter, mirroring the
/// hosts' own rules: RequestDelegateFactory for minimal APIs; for MVC the explicit binding
/// sources, the type-based ones MvcCoreMvcOptionsSetup registers (IFormFile and friends,
/// CancellationToken) and InferParameterBindingInfoConvention under [ApiController]; and the
/// isolated worker's DefaultFunctionInputBindingFeature. RIV1110, RIV4005 and RIV4006 all read
/// binding through here.
/// </summary>
internal static class ParameterBindingSources
{
    private static readonly ConditionalWeakTable<
        Compilation,
        StrongBox<bool>
    > _suppressedInference = new();

    /// <summary>
    /// Everything <paramref name="parameter"/> binds. <paramref name="infersBody"/> is
    /// <see cref="EnumParameterBinding.MinimalApiInfersBody"/> or
    /// <see cref="EnumParameterBinding.MvcInfersBody"/> for the endpoint;
    /// <paramref name="routeParameters"/> are the route's parameter names, or null when the
    /// route isn't known, which leaves a simple parameter <see cref="InputSource.RouteOrQuery"/>.
    /// </summary>
    public static IEnumerable<BoundInput> Inputs(
        WellKnownTypes wkt,
        IParameterSymbol parameter,
        ParameterBindingHost host,
        bool infersBody,
        IReadOnlySet<string>? routeParameters
    ) =>
        host switch
        {
            ParameterBindingHost.MinimalApi => MinimalApi(
                wkt,
                parameter,
                parameter.Name,
                parameter.Name,
                parameter.Type,
                parameter.GetAttributes(),
                infersBody,
                routeParameters,
                topLevel: true
            ),
            ParameterBindingHost.Mvc => Mvc(wkt, parameter, infersBody, routeParameters),
            _ => Functions(wkt, parameter, routeParameters),
        };

    // ---------------------------------------------------------------
    // Minimal APIs (RequestDelegateFactory.CreateArgument)
    // ---------------------------------------------------------------

    private static IEnumerable<BoundInput> MinimalApi(
        WellKnownTypes wkt,
        IParameterSymbol parameter,
        string path,
        string name,
        ITypeSymbol type,
        IReadOnlyList<AttributeData> attributes,
        bool infersBody,
        IReadOnlySet<string>? routeParameters,
        bool topLevel
    )
    {
        BoundInput Leaf(InputSource source, AttributeData? named = null) =>
            new(
                path,
                NameOverride(named) ?? name,
                source,
                type,
                parameter,
                ReachesEnumParser(source) ? EnumParser.Exact : EnumParser.None
            );

        if (attributes.Any(attribute => attribute.Is(wkt.FromBody)))
        {
            return [Leaf(InputSource.Body)];
        }
        if (attributes.Any(attribute => IsServiceAttribute(wkt, attribute)))
        {
            return [Leaf(InputSource.NotRequest)];
        }
        if (attributes.FirstOrDefault(attribute => attribute.Is(wkt.FromRoute)) is { } route)
        {
            return [Leaf(InputSource.Route, route)];
        }
        if (attributes.FirstOrDefault(attribute => attribute.Is(wkt.FromQuery)) is { } query)
        {
            return [Leaf(InputSource.Query, query)];
        }
        if (attributes.FirstOrDefault(attribute => attribute.Is(wkt.FromHeader)) is { } header)
        {
            return [Leaf(InputSource.Header, header)];
        }
        if (attributes.FirstOrDefault(attribute => attribute.Is(wkt.FromForm)) is { } form)
        {
            // A [FromForm] complex type (.NET 8+) goes through the form mapper, which binds
            // each member by name, nested ones with a prefix, and parses enums ignoring case.
            return
                type is INamedTypeSymbol complex
                && IsComplex(wkt, type, ParameterBindingHost.MinimalApi)
                ? ComplexMembers(
                    wkt,
                    parameter,
                    path,
                    prefix: null,
                    complex,
                    InputSource.Form,
                    ParameterBindingHost.MinimalApi,
                    nested: false,
                    []
                )
                : [Leaf(InputSource.Form, form)];
        }
        if (topLevel && attributes.Any(attribute => attribute.Is(wkt.AsParameters)))
        {
            // Each member binds as if it were a parameter of its own. ASP.NET refuses a nested
            // [AsParameters], so members don't expand further.
            return type is INamedTypeSymbol container
                ? Uncreatable(
                        wkt,
                        parameter,
                        path,
                        name,
                        container,
                        FormBinding.Binder.AsParameters,
                        nested: false
                    )
                    .Concat(
                        FormBinding
                            .BoundMembers(wkt, container, FormBinding.Binder.AsParameters)
                            .SelectMany(member =>
                                MinimalApi(
                                    wkt,
                                    parameter,
                                    $"{path}.{member.Name}",
                                    member.Name,
                                    member.Type,
                                    member.Attributes,
                                    infersBody,
                                    routeParameters,
                                    topLevel: false
                                )
                            )
                    )
                : [];
        }

        switch (Classify(wkt, type))
        {
            case Special.Request:
                return [Leaf(InputSource.WholeRequest)];
            case Special.NotRequest:
                return [Leaf(InputSource.NotRequest)];
            case Special.WholeForm:
                return [Leaf(InputSource.WholeForm)];
            case Special.FormFile:
                return [Leaf(InputSource.Form)];
            case Special.RawBody:
                return [Leaf(InputSource.Body)];
        }

        if (HasPublicStaticMethod(type, "BindAsync"))
        {
            return [Leaf(InputSource.WholeRequest)];
        }
        if (IsSimple(wkt, type, ParameterBindingHost.MinimalApi))
        {
            return [Leaf(RouteOrQuery(name, routeParameters))];
        }

        // An array of simple values, or StringValues, comes from the query on a body-less verb
        // and is a JSON body otherwise. Any other collection, List<T> included, is a body, and
        // on a body-less verb ASP.NET refuses the endpoint when it builds it ("Body was
        // inferred but the method does not allow inferred body parameters"). A complex type is
        // a body or a registered service.
        if (
            IsStringValues(wkt, type)
            || type is IArrayTypeSymbol { ElementType: var element }
                && IsSimple(wkt, element, ParameterBindingHost.MinimalApi)
        )
        {
            return [Leaf(infersBody ? InputSource.Body : InputSource.Query)];
        }

        return [Leaf(InputSource.BodyOrService)];
    }

    // ---------------------------------------------------------------
    // MVC (binding sources, type-based sources, [ApiController] inference)
    // ---------------------------------------------------------------

    private static IEnumerable<BoundInput> Mvc(
        WellKnownTypes wkt,
        IParameterSymbol parameter,
        bool infersBody,
        IReadOnlySet<string>? routeParameters
    )
    {
        var type = parameter.Type;
        // The model name: a simple parameter's key, and the prefix a complex one's members
        // are read under, so [FromQuery(Name = "f")] SearchRecord f reads ?f.Search=, never
        // ?Search=. Without one, MVC falls back to the empty prefix: ?Search=.
        var modelName = FormBinding.MvcModelName(wkt, parameter.GetAttributes(), type);
        BoundInput Leaf(InputSource source) =>
            new(
                parameter.Name,
                modelName ?? parameter.Name,
                source,
                type,
                parameter,
                ReachesEnumParser(source) ? EnumParser.IgnoreCase : EnumParser.None
            );
        IEnumerable<BoundInput> Members(INamedTypeSymbol complex, InputSource source) =>
            ComplexMembers(
                wkt,
                parameter,
                parameter.Name,
                modelName,
                complex,
                source,
                ParameterBindingHost.Mvc,
                nested: false,
                []
            );

        if (MvcOwnSource(wkt, parameter.GetAttributes(), type) is { } own)
        {
            return
                own.Inherited is { } inherited
                && type is INamedTypeSymbol complex
                && IsComplex(wkt, type, ParameterBindingHost.Mvc)
                ? Members(complex, inherited)
                : [Leaf(own.Source)];
        }

        if (!infersBody)
        {
            // No binding source: model binding asks every value provider (form, route and
            // query) by name, and binds a complex type member by member from them.
            return
                type is INamedTypeSymbol complex && IsComplex(wkt, type, ParameterBindingHost.Mvc)
                ? Members(complex, InputSource.ValueProviders)
                : [Leaf(InputSource.ValueProviders)];
        }

        // InferParameterBindingInfoConvention: a simple type comes from the route when any of
        // the action's routes has a parameter of that name, and from the query otherwise. A
        // complex type (collections included) is the body, unless it's a service, or one of the
        // properties it binds carries a binding source of its own; then it gets none and is
        // bound member by member from the value providers.
        if (IsSimple(wkt, type, ParameterBindingHost.Mvc))
        {
            return [Leaf(RouteOrQuery(parameter.Name, routeParameters))];
        }

        return
            type is INamedTypeSymbol withSources
            && IsComplex(wkt, type, ParameterBindingHost.Mvc)
            && FormBinding
                .BoundMembers(wkt, withSources, FormBinding.Binder.Mvc)
                .Any(member => MvcOwnSource(wkt, member.MvcAttributes, member.Type) is not null)
            ? Members(withSources, InputSource.ValueProviders)
            : [Leaf(InputSource.BodyOrService)];
    }

    /// <summary>
    /// A binding source set on an MVC parameter or member itself: a [From*] attribute, a custom
    /// [ModelBinder] (on it or on its enum), or one MvcCoreMvcOptionsSetup registers for the
    /// type (IFormFile, IFormCollection, IFormFileCollection, IEnumerable&lt;IFormFile&gt;,
    /// CancellationToken). <see cref="OwnSource.Inherited"/> is the source a complex type's
    /// members inherit from it, set for route, query, form and header only.
    /// </summary>
    private static OwnSource? MvcOwnSource(
        WellKnownTypes wkt,
        IReadOnlyList<AttributeData> attributes,
        ITypeSymbol type
    )
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Is(wkt.FromBody))
            {
                return new(InputSource.Body, null);
            }
            if (IsServiceAttribute(wkt, attribute))
            {
                return new(InputSource.NotRequest, null);
            }
            if (IsModelBinder(wkt, attribute))
            {
                return new(InputSource.WholeRequest, null);
            }

            var source =
                attribute.Is(wkt.FromRoute) ? InputSource.Route
                : attribute.Is(wkt.FromQuery) ? InputSource.Query
                : attribute.Is(wkt.FromForm) ? InputSource.Form
                : attribute.Is(wkt.FromHeader) ? InputSource.Header
                : (InputSource?)null;
            if (source is { } named)
            {
                return new(named, named);
            }
        }

        if (
            BoundEnum(type) is { } enumType
            && enumType.GetAttributes().Any(attribute => IsModelBinder(wkt, attribute))
        )
        {
            return new(InputSource.WholeRequest, null);
        }

        return Classify(wkt, type) switch
        {
            Special.FormFile => new(InputSource.Form, null),
            Special.WholeForm => new(InputSource.WholeForm, null),
            _ when SymbolEqualityComparer.Default.Equals(type, wkt.CancellationToken) => new(
                InputSource.NotRequest,
                null
            ),
            _ => null,
        };
    }

    private sealed record OwnSource(InputSource Source, InputSource? Inherited);

    /// <summary>
    /// Where MVC reads a member from by its own attributes or type (see
    /// <see cref="MvcOwnSource"/>), or null when it inherits its container's source.
    /// </summary>
    internal static InputSource? MvcMemberSource(
        WellKnownTypes wkt,
        IReadOnlyList<AttributeData> attributes,
        ITypeSymbol type
    ) => MvcOwnSource(wkt, attributes, type)?.Source;

    /// <summary>
    /// The members of a complex type bound member by member. On MVC a member's own binding
    /// source wins (see <see cref="MvcOwnSource"/>), and otherwise it inherits
    /// <paramref name="source"/>; the minimal-API form mapper reads every member from the form,
    /// whatever attributes it carries. A complex member is listed itself, as the input its
    /// prefix names, and then recursed into with that prefix, the way MVC's
    /// ComplexObjectModelBinder and the form mapper bind nested types. Both parse an enum
    /// member ignoring case. A type already being expanded on the path stops the recursion.
    /// <paramref name="prefix"/> is the model name the members' keys start with (an MVC model
    /// name on the parameter, or the outer member's key); <paramref name="nested"/> marks
    /// members of a complex member. A type the binder can't create comes first as an
    /// <see cref="InputSource.Uncreatable"/> input, and its members still follow. Real-host
    /// pins: <c>MvcBindingHostTests</c>, <c>MinimalApiBindingHostTests</c>, and
    /// <c>FormPartNameTests</c>.
    /// </summary>
    private static IEnumerable<BoundInput> ComplexMembers(
        WellKnownTypes wkt,
        IParameterSymbol parameter,
        string path,
        string? prefix,
        INamedTypeSymbol type,
        InputSource source,
        ParameterBindingHost host,
        bool nested,
        HashSet<INamedTypeSymbol> expanding
    )
    {
        if (!expanding.Add(type))
        {
            yield break;
        }

        var mvc = host == ParameterBindingHost.Mvc;
        var binder = mvc ? FormBinding.Binder.Mvc : FormBinding.Binder.FormMapper;
        foreach (
            var input in Uncreatable(wkt, parameter, path, prefix ?? path, type, binder, nested)
        )
        {
            yield return input;
        }

        foreach (var member in FormBinding.BoundMembers(wkt, type, binder))
        {
            var (name, memberType) = (member.Name, member.Type);
            var attributes = mvc ? member.MvcAttributes : member.FormMapperAttributes;
            var own = mvc ? MvcOwnSource(wkt, attributes, memberType) : null;
            var effective = own?.Source ?? source;
            var wireName = MemberWireName(
                host,
                effective,
                prefix,
                name,
                mvc
                    ? FormBinding.MvcModelName(wkt, attributes, memberType)
                    : FormBinding.DataMemberName(wkt, attributes)
            );
            var memberPath = $"{path}.{name}";
            var reaches = ReachesEnumParser(effective);

            yield return new BoundInput(
                memberPath,
                wireName,
                effective,
                memberType,
                parameter,
                reaches ? EnumParser.IgnoreCase : EnumParser.None,
                Nested: nested
            );

            if (
                reaches
                && memberType is INamedTypeSymbol complex
                && IsComplex(wkt, memberType, host)
            )
            {
                foreach (
                    var input in ComplexMembers(
                        wkt,
                        parameter,
                        memberPath,
                        wireName,
                        complex,
                        own?.Inherited ?? source,
                        host,
                        nested: true,
                        expanding
                    )
                )
                {
                    yield return input;
                }
            }
        }

        expanding.Remove(type);
    }

    /// <summary>
    /// An <see cref="InputSource.Uncreatable"/> input for <paramref name="type"/> when
    /// <paramref name="binder"/> can't create it, carrying why and what the host does instead;
    /// nothing when it can.
    /// </summary>
    private static IEnumerable<BoundInput> Uncreatable(
        WellKnownTypes wkt,
        IParameterSymbol parameter,
        string path,
        string wireName,
        INamedTypeSymbol type,
        FormBinding.Binder binder,
        bool nested
    )
    {
        if (
            FormBinding.CantCreate(
                type,
                FormBinding.FormBoundProperties(wkt.Compilation, type),
                binder
            ) is
            { } why
        )
        {
            yield return new BoundInput(
                path,
                wireName,
                InputSource.Uncreatable,
                type,
                parameter,
                EnumParser.None,
                nested,
                $"{why}, {FormBinding.WhenItCantCreate(binder)}"
            );
        }
    }

    /// <summary>
    /// The key a member of a complex type is read under. MVC's ComplexObjectModelBinder
    /// names the member by its model name ?? its own name (the field name) and prefixes it
    /// with the container's model name (ModelNames.CreatePropertyModelName), so a renamed
    /// member under a prefix is <c>prefix.override</c>; HeaderModelBinder reads the field name
    /// alone, never prefixed. The minimal-API form mapper names the member by its
    /// [DataMember] name ?? its own name, under the outer member's key and never the
    /// parameter's.
    /// </summary>
    private static string MemberWireName(
        ParameterBindingHost host,
        InputSource source,
        string? prefix,
        string name,
        string? nameOverride
    )
    {
        var fieldName = nameOverride ?? name;
        return source == InputSource.Header && host == ParameterBindingHost.Mvc || prefix is null
            ? fieldName
            : $"{prefix}.{fieldName}";
    }

    // ---------------------------------------------------------------
    // Azure Functions (isolated worker)
    // ---------------------------------------------------------------

    /// <summary>
    /// The worker fills a parameter with no binding attribute from the trigger's binding data
    /// by name, ignoring case (DefaultFunctionInputBindingFeature). For an HTTP trigger the
    /// host puts the route values there first, then the query parameters, then a JSON body's
    /// top-level properties, whatever the request's content type; headers and form fields
    /// never get there, and a name none of them carries is null (a value type fails the
    /// invocation). Every such value reaches the worker's input converters, so an enum is
    /// parsed as JSON. tools/test_functions_host.py pins all of this on the real host. The
    /// trigger parameter itself is the request; FunctionContext and CancellationToken are the
    /// worker's own; any other attributed parameter is an input binding, except the HTTP
    /// extension's [FromBody].
    /// </summary>
    private static IEnumerable<BoundInput> Functions(
        WellKnownTypes wkt,
        IParameterSymbol parameter,
        IReadOnlySet<string>? routeParameters
    )
    {
        var attributes = parameter.GetAttributes();
        BoundInput Leaf(InputSource source) =>
            new(
                parameter.Name,
                parameter.Name,
                source,
                parameter.Type,
                parameter,
                source is InputSource.TriggerMetadata or InputSource.Route
                    ? EnumParser.FunctionsJson
                    : EnumParser.None
            );

        if (attributes.Any(attribute => attribute.Is(wkt.HttpTrigger)))
        {
            return [Leaf(InputSource.WholeRequest)];
        }
        if (attributes.Any(attribute => attribute.Is(wkt.FunctionsFromBody)))
        {
            return [Leaf(InputSource.Body)];
        }
        if (
            attributes.Length > 0
            || SymbolEqualityComparer.Default.Equals(parameter.Type, wkt.FunctionContext)
            || SymbolEqualityComparer.Default.Equals(parameter.Type, wkt.CancellationToken)
        )
        {
            return [Leaf(InputSource.NotRequest)];
        }

        return
        [
            Leaf(
                routeParameters?.Contains(parameter.Name) == true
                    ? InputSource.Route
                    : InputSource.TriggerMetadata
            ),
        ];
    }

    // ---------------------------------------------------------------
    // Shared rules
    // ---------------------------------------------------------------

    /// <summary>
    /// True when MVC's [ApiController] inference is switched off for the whole app: something
    /// in the compilation sets <c>ApiBehaviorOptions.SuppressInferBindingSourcesForParameters</c>
    /// to the constant <c>true</c>, as in
    /// <c>services.Configure&lt;ApiBehaviorOptions&gt;(o =&gt; o.SuppressInferBindingSourcesForParameters = true)</c>
    /// or an object initializer. ApiBehaviorApplicationModelProvider then never adds
    /// InferParameterBindingInfoConvention, so an unattributed parameter has no binding source
    /// and is bound from the value providers, as on a controller without [ApiController]. A
    /// value that isn't a constant can't be read statically, and every RIV1110, RIV4005 and
    /// RIV4006 answer for an MVC action depends on it, so it stops the run with RIV1115
    /// rather than guessing.
    /// </summary>
    public static bool SuppressesInferredBindingSources(Compilation compilation) =>
        _suppressedInference
            .GetValue(compilation, static c => new StrongBox<bool>(ScanForSuppression(c)))
            .Value;

    private static bool ScanForSuppression(Compilation compilation)
    {
        const string property = "SuppressInferBindingSourcesForParameters";
        var apiBehaviorOptions = compilation.GetTypeByMetadataName(
            "Microsoft.AspNetCore.Mvc.ApiBehaviorOptions"
        );
        var suppressed = false;
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!tree.GetText().ToString().Contains(property, StringComparison.Ordinal))
            {
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (
                var assignment in tree.GetRoot()
                    .DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Select(syntax => semanticModel.GetOperation(syntax))
                    .OfType<IAssignmentOperation>()
                    .Where(assignment =>
                        assignment.Target
                            is IPropertyReferenceOperation
                            {
                                Property: { Name: property, ContainingType: var options },
                            }
                        && SymbolEqualityComparer.Default.Equals(options, apiBehaviorOptions)
                    )
            )
            {
                if (
                    assignment
                    is not ISimpleAssignmentOperation
                    {
                        Value.ConstantValue: { HasValue: true, Value: bool value },
                    }
                )
                {
                    throw Diagnostics.Error(
                        Diagnostics.BindingInferenceUnreadable,
                        $"{assignment.Syntax.SourcePosition()}: "
                            + $"ApiBehaviorOptions.{property} is set to a value that isn't a compile-time "
                            + "constant, so Rivet can't tell whether MVC infers binding sources for "
                            + "[ApiController] actions, and every input and enum binding check on an MVC "
                            + "action depends on that. Assign the constant true or false (a const bool "
                            + "works too)."
                    );
                }

                suppressed |= value;
            }
        }

        return suppressed;
    }

    private static bool ReachesEnumParser(InputSource source) =>
        source
            is InputSource.Route
                or InputSource.Query
                or InputSource.Header
                or InputSource.Form
                or InputSource.ValueProviders
                or InputSource.RouteOrQuery;

    private static InputSource RouteOrQuery(string name, IReadOnlySet<string>? routeParameters) =>
        routeParameters is null ? InputSource.RouteOrQuery
        : routeParameters.Contains(name) ? InputSource.Route
        : InputSource.Query;

    /// <summary>The Name= a [From*] attribute renames the input with, if any.</summary>
    private static string? NameOverride(AttributeData? attribute) =>
        attribute?.NamedArgument("Name") is string { Length: > 0 } name ? name : null;

    private static bool IsServiceAttribute(WellKnownTypes wkt, AttributeData attribute) =>
        attribute.Is(wkt.FromServices) || attribute.Is(wkt.FromKeyedServices);

    /// <summary>[ModelBinder] or a subclass such as the generic [ModelBinder&lt;TBinder&gt;].</summary>
    private static bool IsModelBinder(WellKnownTypes wkt, AttributeData attribute) =>
        attribute.AttributeClass.DerivesFrom(wkt.ModelBinder);

    private enum Special
    {
        None,
        Request,
        NotRequest,
        WholeForm,
        FormFile,
        RawBody,
    }

    private static Special Classify(WellKnownTypes wkt, ITypeSymbol type)
    {
        bool Is(INamedTypeSymbol? candidate) =>
            SymbolEqualityComparer.Default.Equals(type, candidate);

        return type switch
        {
            _ when IsRequest(wkt, type) => Special.Request,
            _ when Is(wkt.HttpResponse) || Is(wkt.ClaimsPrincipal) || Is(wkt.CancellationToken) =>
                Special.NotRequest,
            _ when Is(wkt.IFormCollection) || Is(wkt.IFormFileCollection) => Special.WholeForm,
            _ when IsRawBody(wkt, type) => Special.RawBody,
            _ when IsFormFile(wkt, type) => Special.FormFile,
            _ => Special.None,
        };
    }

    /// <summary>HttpContext or HttpRequest: the request itself, from which anything can be read.</summary>
    public static bool IsRequest(WellKnownTypes wkt, ITypeSymbol type) =>
        Is(type, wkt.HttpContext) || Is(type, wkt.HttpRequest);

    /// <summary>
    /// True when an operation in a handler's body hands over the request or its route values
    /// without a parameter: a read of an HttpRequest-typed property (a controller's
    /// <c>Request</c>, <c>context.Request</c>, <c>accessor.HttpContext!.Request</c>) or a
    /// RouteData-typed one (a controller's <c>RouteData</c>), or an HttpContext or HttpRequest
    /// passed to a call, even as <c>object</c>, since the callee may read anything from it. An
    /// extension's receiver is an argument, so <c>HttpContext.GetRouteValue("id")</c> and a
    /// package's <c>HttpContext.GetTenantId()</c> both count. Reading the context's User,
    /// RequestAborted or Response isn't: they carry no input. This judges one operation; a
    /// helper whose source is in the compilation is followed into by
    /// <c>ContractInputBinding.RequestReads</c>, and counts only when its own body reads the
    /// request, so the argument rule decides only for a helper metadata alone knows.
    /// </summary>
    public static bool ReadsRequest(WellKnownTypes wkt, IOperation operation) =>
        operation switch
        {
            IArgumentOperation { Value: var value } => (
                value is IConversionOperation { IsImplicit: true } conversion
                    ? conversion.Operand
                    : value
            ).Type
                is { } type
                && IsRequest(wkt, type),
            IPropertyReferenceOperation { Property.Type: var type } => Is(type, wkt.HttpRequest)
                || Is(type, wkt.RouteData),
            _ => false,
        };

    /// <summary>Stream or PipeReader: the raw request body, whatever its media type.</summary>
    public static bool IsRawBody(WellKnownTypes wkt, ITypeSymbol type) =>
        Is(type, wkt.Stream) || Is(type, wkt.PipeReader);

    /// <summary>StringValues or <c>StringValues?</c>, which minimal APIs bind like a string array.</summary>
    private static bool IsStringValues(WellKnownTypes wkt, ITypeSymbol type) =>
        Is(
            type
                is INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                    TypeArguments: [var inner],
                }
                ? inner
                : type,
            wkt.StringValues
        );

    /// <summary>An IFormFile, or a collection of them (anything assignable to IEnumerable&lt;IFormFile&gt;).</summary>
    private static bool IsFormFile(WellKnownTypes wkt, ITypeSymbol type) =>
        wkt.IFormFile is { } formFile
        && (
            type.Implements(formFile)
            || type is IArrayTypeSymbol { ElementType: var element } && element.Implements(formFile)
            || type is INamedTypeSymbol { TypeArguments: [var item] } collection
                && IsEnumerableOf(collection, item)
                && item.Implements(formFile)
        );

    /// <summary>
    /// A type <paramref name="host"/> binds from one string: a primitive, string, decimal,
    /// DateTime, an enum, a type with a public static TryParse (Guid, DateTimeOffset, DateOnly,
    /// any IParsable&lt;T&gt;), Uri, or <c>T?</c> of one. MVC also counts a type carrying a
    /// [TypeConverter] (its IsComplexType test); minimal APIs never consult a TypeConverter, so
    /// for them such a type is a body or a service.
    /// </summary>
    private static bool IsSimple(WellKnownTypes wkt, ITypeSymbol type, ParameterBindingHost host)
    {
        if (
            type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                TypeArguments: [var inner],
            }
        )
        {
            return IsSimple(wkt, inner, host);
        }

        return type.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_String
            || type.SpecialType == SpecialType.System_DateTime
            || type.TypeKind == TypeKind.Enum
            || Is(type, wkt.Uri)
            || HasPublicStaticMethod(type, "TryParse")
            || type.Implements(wkt.IParsableOfT)
            || host == ParameterBindingHost.Mvc
                && type.GetAttributes()
                    .Any(attribute => Is(attribute.AttributeClass, wkt.TypeConverter));
    }

    /// <summary>A class or struct bound member by member: not simple, not a collection, not special.</summary>
    private static bool IsComplex(
        WellKnownTypes wkt,
        ITypeSymbol type,
        ParameterBindingHost host
    ) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } named
        && named.SpecialType == SpecialType.None
        && !IsSimple(wkt, type, host)
        && !named.AllInterfaces.Any(candidate =>
            candidate.OriginalDefinition.SpecialType
            == SpecialType.System_Collections_Generic_IEnumerable_T
        )
        && Classify(wkt, type) == Special.None;

    private static bool HasPublicStaticMethod(ITypeSymbol type, string name) =>
        type.GetMembers(name)
            .OfType<IMethodSymbol>()
            .Any(method =>
                method is { IsStatic: true, DeclaredAccessibility: Accessibility.Public }
            );

    /// <summary>The enum a parameter type binds: the enum, <c>T?</c>, or a collection of either.</summary>
    public static INamedTypeSymbol? BoundEnum(ITypeSymbol type) =>
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

    private static bool Is(ISymbol? symbol, INamedTypeSymbol? candidate) =>
        candidate is not null && SymbolEqualityComparer.Default.Equals(symbol, candidate);
}
