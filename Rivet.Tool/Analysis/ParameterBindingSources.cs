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

    /// <summary>An Azure Functions parameter filled from the trigger's metadata (route values).</summary>
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
    /// enum, so the route value falls through to JsonPocoConverter, which parses the raw text
    /// as JSON. An unquoted member name isn't JSON; only an integer converts.
    /// </summary>
    FunctionsJson,
}

/// <summary>
/// One value a handler binds: a parameter, or a member of a complex parameter the host binds
/// member by member. <see cref="Path"/> names it for messages (<c>kind</c>, <c>request.Kind</c>);
/// <see cref="WireName"/> is the key the host looks up. A member of a complex member is
/// <see cref="Nested"/>: its key carries the outer member's prefix (<c>Filter.Kind</c>).
/// </summary>
internal sealed record BoundInput(
    string Path,
    string WireName,
    InputSource Source,
    ITypeSymbol Type,
    IParameterSymbol Parameter,
    EnumParser Parser,
    bool Nested = false
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
    private static readonly SymbolDisplayFormat _fullName = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
    );

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
            _ => Functions(wkt, parameter),
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
                ? BoundMembers(container)
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
        if (IsSimple(type, ParameterBindingHost.MinimalApi))
        {
            return [Leaf(RouteOrQuery(name, routeParameters))];
        }

        // An array of simple values, or StringValues, comes from the query on a body-less verb
        // and is a JSON body otherwise. Any other collection, List<T> included, is a body, and
        // on a body-less verb ASP.NET refuses the endpoint when it builds it ("Body was
        // inferred but the method does not allow inferred body parameters"). A complex type is
        // a body or a registered service.
        if (
            IsStringValues(type)
            || type is IArrayTypeSymbol { ElementType: var element }
                && IsSimple(element, ParameterBindingHost.MinimalApi)
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
        BoundInput Leaf(InputSource source, AttributeData? named = null) =>
            new(
                parameter.Name,
                NameOverride(named) ?? parameter.Name,
                source,
                type,
                parameter,
                ReachesEnumParser(source) ? EnumParser.IgnoreCase : EnumParser.None
            );
        IEnumerable<BoundInput> Members(
            INamedTypeSymbol complex,
            InputSource source,
            string? prefix = null
        ) =>
            ComplexMembers(
                wkt,
                parameter,
                parameter.Name,
                prefix,
                complex,
                source,
                ParameterBindingHost.Mvc,
                nested: false,
                []
            );

        // On a complex parameter, Name= is the model name its members are prefixed with:
        // [FromQuery(Name = "f")] SearchRecord f reads ?f.Search=, never ?Search=.
        if (MvcOwnSource(wkt, parameter.GetAttributes(), type) is { } own)
        {
            return
                own.Inherited is { } inherited
                && type is INamedTypeSymbol complex
                && IsComplex(wkt, type, ParameterBindingHost.Mvc)
                ? Members(complex, inherited, NameOverride(own.Attribute))
                : [Leaf(own.Source, own.Attribute)];
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
        if (IsSimple(type, ParameterBindingHost.Mvc))
        {
            return [Leaf(RouteOrQuery(parameter.Name, routeParameters))];
        }

        return
            type is INamedTypeSymbol withSources
            && IsComplex(wkt, type, ParameterBindingHost.Mvc)
            && BoundMembers(withSources)
                .Any(member => MvcOwnSource(wkt, member.Attributes, member.Type) is not null)
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
                return new(InputSource.Body, null, attribute);
            }
            if (IsServiceAttribute(wkt, attribute))
            {
                return new(InputSource.NotRequest, null, attribute);
            }
            if (IsModelBinder(wkt, attribute))
            {
                return new(InputSource.WholeRequest, null, attribute);
            }

            var source =
                attribute.Is(wkt.FromRoute) ? InputSource.Route
                : attribute.Is(wkt.FromQuery) ? InputSource.Query
                : attribute.Is(wkt.FromForm) ? InputSource.Form
                : attribute.Is(wkt.FromHeader) ? InputSource.Header
                : (InputSource?)null;
            if (source is { } named)
            {
                return new(named, named, attribute);
            }
        }

        if (
            BoundEnum(type) is { } enumType
            && enumType.GetAttributes().Any(attribute => IsModelBinder(wkt, attribute))
        )
        {
            return new(InputSource.WholeRequest, null, null);
        }

        return Classify(wkt, type) switch
        {
            Special.FormFile => new(InputSource.Form, null, null),
            Special.WholeForm => new(InputSource.WholeForm, null, null),
            _ when FullName(type) == "System.Threading.CancellationToken" => new(
                InputSource.NotRequest,
                null,
                null
            ),
            _ => null,
        };
    }

    private sealed record OwnSource(
        InputSource Source,
        InputSource? Inherited,
        AttributeData? Attribute
    );

    /// <summary>
    /// The members of a complex type bound member by member: a member's own binding source
    /// wins (see <see cref="MvcOwnSource"/>), and otherwise it inherits
    /// <paramref name="source"/>. A complex member is listed itself, as the input its prefix
    /// names, and then recursed into with that prefix, the way MVC's ComplexObjectModelBinder
    /// and the minimal-API form mapper bind nested types. Both parse an enum member ignoring
    /// case. A type already being expanded on the path stops the recursion.
    /// <paramref name="prefix"/> is the model name the members' keys start with (an MVC
    /// <c>Name =</c> on the parameter, or the outer member's key); <paramref name="nested"/>
    /// marks members of a complex member.
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

        foreach (var (name, memberType, attributes) in BoundMembers(type))
        {
            var own = MvcOwnSource(wkt, attributes, memberType);
            var effective = own?.Source ?? source;
            var wireName = MemberWireName(
                host,
                effective,
                prefix,
                name,
                NameOverride(own?.Attribute)
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
    /// The key a member of a complex type is read under. MVC's ComplexObjectModelBinder
    /// names the member <c>Name =</c> ?? its own name (the field name) and prefixes it with
    /// the container's model name (ModelNames.CreatePropertyModelName), so a renamed member
    /// under a prefix is <c>prefix.override</c>; HeaderModelBinder reads the field name alone,
    /// never prefixed. The minimal-API form mapper takes the override as the whole key.
    /// </summary>
    private static string MemberWireName(
        ParameterBindingHost host,
        InputSource source,
        string? prefix,
        string name,
        string? nameOverride
    )
    {
        if (host != ParameterBindingHost.Mvc)
        {
            return nameOverride ?? (prefix is null ? name : $"{prefix}.{name}");
        }

        var fieldName = nameOverride ?? name;
        return source == InputSource.Header || prefix is null ? fieldName : $"{prefix}.{fieldName}";
    }

    // ---------------------------------------------------------------
    // Azure Functions (isolated worker)
    // ---------------------------------------------------------------

    /// <summary>
    /// The worker fills a parameter with no binding attribute from the trigger's metadata by
    /// name (DefaultFunctionInputBindingFeature), which for an HTTP trigger carries the route
    /// values. The trigger parameter itself is the request; FunctionContext and
    /// CancellationToken are the worker's own; any other attributed parameter is an input
    /// binding, except the HTTP extension's [FromBody].
    /// </summary>
    private static IEnumerable<BoundInput> Functions(WellKnownTypes wkt, IParameterSymbol parameter)
    {
        var attributes = parameter.GetAttributes();
        BoundInput Leaf(InputSource source) =>
            new(
                parameter.Name,
                parameter.Name,
                source,
                parameter.Type,
                parameter,
                source == InputSource.TriggerMetadata ? EnumParser.FunctionsJson : EnumParser.None
            );

        if (attributes.Any(attribute => attribute.Is(wkt.HttpTrigger)))
        {
            return [Leaf(InputSource.WholeRequest)];
        }
        if (
            attributes.Any(attribute =>
                FullName(attribute.AttributeClass)
                == "Microsoft.Azure.Functions.Worker.Http.FromBodyAttribute"
            )
        )
        {
            return [Leaf(InputSource.Body)];
        }
        if (
            attributes.Length > 0
            || FullName(parameter.Type)
                is "Microsoft.Azure.Functions.Worker.FunctionContext"
                    or "System.Threading.CancellationToken"
        )
        {
            return [Leaf(InputSource.NotRequest)];
        }

        return [Leaf(InputSource.TriggerMetadata)];
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
                        && FullName(options) == "Microsoft.AspNetCore.Mvc.ApiBehaviorOptions"
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
                    var span = assignment.Syntax.GetLocation().GetLineSpan();
                    throw new RivetUserException(
                        $"error {Diagnostics.BindingInferenceUnreadable}: "
                            + $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): "
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
        attribute.Is(wkt.FromServices)
        || FullName(attribute.AttributeClass)
            == "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute";

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

    private enum Special
    {
        None,
        Request,
        NotRequest,
        WholeForm,
        FormFile,
        RawBody,
    }

    private static Special Classify(WellKnownTypes wkt, ITypeSymbol type) =>
        FullName(type) switch
        {
            _ when IsRequest(type) => Special.Request,
            "Microsoft.AspNetCore.Http.HttpResponse"
            or "System.Security.Claims.ClaimsPrincipal"
            or "System.Threading.CancellationToken" => Special.NotRequest,
            "Microsoft.AspNetCore.Http.IFormCollection"
            or "Microsoft.AspNetCore.Http.IFormFileCollection" => Special.WholeForm,
            _ when IsRawBody(type) => Special.RawBody,
            _ when IsFormFile(wkt, type) => Special.FormFile,
            _ => Special.None,
        };

    /// <summary>HttpContext or HttpRequest: the request itself, from which anything can be read.</summary>
    public static bool IsRequest(ITypeSymbol type) =>
        FullName(type)
            is "Microsoft.AspNetCore.Http.HttpContext"
                or "Microsoft.AspNetCore.Http.HttpRequest";

    /// <summary>
    /// True when an operation in a handler's body hands over the request or its route values
    /// without a parameter: a read of an HttpRequest-typed property (a controller's
    /// <c>Request</c>, <c>context.Request</c>, <c>accessor.HttpContext!.Request</c>) or a
    /// RouteData-typed one (a controller's <c>RouteData</c>), or a call to
    /// <c>HttpContext.GetRouteValue</c> or <c>GetRouteData</c>. Reading HttpContext alone
    /// isn't: its User, RequestAborted and Response carry no input.
    /// </summary>
    public static bool ReadsRequest(IOperation operation) =>
        operation switch
        {
            IPropertyReferenceOperation { Property.Type: var type } => FullName(type)
                is "Microsoft.AspNetCore.Http.HttpRequest"
                    or "Microsoft.AspNetCore.Routing.RouteData",
            IInvocationOperation
            {
                TargetMethod: { Name: "GetRouteValue" or "GetRouteData" } method,
            } => FullName(method.ContainingType)
                == "Microsoft.AspNetCore.Routing.RoutingHttpContextExtensions",
            _ => false,
        };

    /// <summary>Stream or PipeReader: the raw request body, whatever its media type.</summary>
    public static bool IsRawBody(ITypeSymbol type) =>
        FullName(type) is "System.IO.Stream" or "System.IO.Pipelines.PipeReader";

    /// <summary>StringValues or <c>StringValues?</c>, which minimal APIs bind like a string array.</summary>
    private static bool IsStringValues(ITypeSymbol type) =>
        FullName(
            type
                is INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                    TypeArguments: [var inner],
                }
                ? inner
                : type
        ) == "Microsoft.Extensions.Primitives.StringValues";

    /// <summary>An IFormFile, or a collection of them (anything assignable to IEnumerable&lt;IFormFile&gt;).</summary>
    private static bool IsFormFile(WellKnownTypes wkt, ITypeSymbol type) =>
        wkt.IFormFile is { } formFile
        && (
            Implements(type, formFile)
            || type is IArrayTypeSymbol { ElementType: var element }
                && Implements(element, formFile)
            || type is INamedTypeSymbol { TypeArguments: [var item] } collection
                && IsEnumerableOf(collection, item)
                && Implements(item, formFile)
        );

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol target) =>
        SymbolEqualityComparer.Default.Equals(type, target)
        || type.AllInterfaces.Contains(target, SymbolEqualityComparer.Default);

    /// <summary>
    /// A type <paramref name="host"/> binds from one string: a primitive, string, decimal,
    /// DateTime, an enum, a type with a public static TryParse (Guid, DateTimeOffset, DateOnly,
    /// any IParsable&lt;T&gt;), Uri, or <c>T?</c> of one. MVC also counts a type carrying a
    /// [TypeConverter] (its IsComplexType test); minimal APIs never consult a TypeConverter, so
    /// for them such a type is a body or a service.
    /// </summary>
    private static bool IsSimple(ITypeSymbol type, ParameterBindingHost host)
    {
        if (
            type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                TypeArguments: [var inner],
            }
        )
        {
            return IsSimple(inner, host);
        }

        return type.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_String
            || type.SpecialType == SpecialType.System_DateTime
            || type.TypeKind == TypeKind.Enum
            || FullName(type) == "System.Uri"
            || HasPublicStaticMethod(type, "TryParse")
            || type.AllInterfaces.Any(candidate =>
                FullName(candidate.OriginalDefinition) == "System.IParsable<TSelf>"
            )
            || host == ParameterBindingHost.Mvc
                && type.GetAttributes()
                    .Any(attribute =>
                        FullName(attribute.AttributeClass)
                        == "System.ComponentModel.TypeConverterAttribute"
                    );
    }

    /// <summary>A class or struct bound member by member: not simple, not a collection, not special.</summary>
    private static bool IsComplex(
        WellKnownTypes wkt,
        ITypeSymbol type,
        ParameterBindingHost host
    ) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } named
        && named.SpecialType == SpecialType.None
        && !IsSimple(type, host)
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

    private static string? FullName(ISymbol? symbol) => symbol?.ToDisplayString(_fullName);
}
