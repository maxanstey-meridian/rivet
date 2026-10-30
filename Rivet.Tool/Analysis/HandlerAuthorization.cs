using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Whether a handler needs an authenticated caller, read from ASP.NET's own authorization
/// metadata only (K.5): <c>IAuthorizeData</c>/<c>IAllowAnonymous</c> attributes on the handler
/// (an action, its controller and base controllers, or a minimal-API handler); an
/// <c>AuthorizeFilter</c> added to <c>MvcOptions.Filters</c>; <c>RequireAuthorization</c> and
/// <c>AllowAnonymous</c> on the endpoint, the route groups it's mapped in, or
/// <c>MapControllers()</c>; the fallback policy, the default policy a requirement naming
/// no policy of its own means, and the <c>AddPolicy</c> registration a policy name means; and
/// an Azure Functions trigger's <c>AuthorizationLevel</c>. App-wide setup in a helper applies
/// where the helper is called. Custom authorization filters and middleware are invisible.
/// <para>
/// Each piece of setup is a fact: present or not, and requiring an authenticated user or not.
/// A policy requires one when it holds a user requirement, and admits anyone when its only
/// requirement is an assertion that returns <c>true</c>. A fact applied under a condition,
/// built from a policy that can't be read, or naming a policy with no registration --check
/// can read (none, a conditional one, or beside a policy provider) is unknown;
/// facts under one condition (<see cref="BuilderOrigins.ConditionWhy"/>'s key) share one.
/// The verdict is worked out for every way the unknowns could fall, ASP.NET's way: an
/// <c>IAllowAnonymous</c> anywhere wins; otherwise the endpoint's own authorization data
/// decides, and the fallback policy applies only when it has none; an MVC-wide
/// <c>AuthorizeFilter</c> applies to every action on top. If every case agrees, that's the
/// verdict, so <c>[AllowAnonymous]</c> still decides an endpoint under a conditional
/// requirement. If they don't, the verdict is unknown, and the reasons say what couldn't be read.
/// </para>
/// </summary>
internal sealed class HandlerAuthorization
{
    // An unknown is one boolean; past this many, the cases aren't enumerated.
    private const int MaxUnknowns = 16;

    private static readonly HashSet<string> _denyingPolicyCalls = new(StringComparer.Ordinal)
    {
        "RequireAuthenticatedUser",
        "RequireRole",
        "RequireClaim",
        "RequireUserName",
    };

    private readonly Compilation _compilation;
    private readonly WellKnownTypes _wkt;
    private readonly BuilderOrigins _origins;

    // RequireAuthorization/AllowAnonymous conventions by the Map*/MapGroup call they're applied
    // to, each with the helper calls the trace went into to reach it (ChainLink.Reaches).
    private readonly Dictionary<
        (SyntaxTree Tree, TextSpan Span),
        List<(ImmutableArray<IInvocationOperation> Descended, Fact Fact)>
    > _conventions = [];

    // Conventions on MapControllers(): every attribute-routed MVC action's.
    private readonly List<Fact> _mvcConventions = [];

    // Conventions on a builder that couldn't be traced to the call that made it, with the
    // hosts whose endpoints it could reach.
    private readonly List<(Fact Fact, bool Mvc, bool MinimalApi)> _untraced = [];

    private readonly List<Fact> _mvcFilters = [];
    private readonly Tri _fallback;

    // What [Authorize], RequireAuthorization() and new AuthorizeFilter() mean when they name no
    // policy or role of their own. ASP.NET's own default requires an authenticated user; an
    // app may set another.
    private readonly Tri _defaultPolicy = Tri.True;

    // Named policy registrations, by the name a lookup gives (ASP.NET's AuthorizationOptions
    // ignores case), and the sites that may register or serve any name: a registration whose
    // name --check can't read, and a registered IAuthorizationPolicyProvider.
    private readonly Dictionary<
        string,
        List<(Tri Requires, Tri Present, string Source)>
    > _namedPolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Tri Requires, Tri Present, string Source)> _anyPolicy = [];

    // The calls that hand UseStartup a Startup type, by that type, and the ones whose type
    // can't be read.
    private (
        Dictionary<INamedTypeSymbol, List<SyntaxNode>> Readable,
        List<SyntaxNode> Unreadable
    )? _startups;

    // The registrations that hand the framework a type or one derived from it, by that type.
    private readonly Dictionary<INamedTypeSymbol, List<Registered>> _registrations = new(
        SymbolEqualityComparer.Default
    );

    // The options interfaces ConfigureOptions registers a type under, as its
    // FindConfigurationServices reads them.
    private readonly List<INamedTypeSymbol> _optionsInterfaces;

    private HandlerAuthorization(
        Compilation compilation,
        WellKnownTypes wkt,
        BuilderOrigins origins
    )
    {
        _compilation = compilation;
        _wkt = wkt;
        _origins = origins;
        _optionsInterfaces =
        [
            .. new[]
            {
                wkt.IConfigureOptions,
                wkt.IPostConfigureOptions,
                wkt.IValidateOptions,
            }.OfType<INamedTypeSymbol>(),
        ];

        // The default policy first: every other site may mean it.
        _defaultPolicy = Settled(
            PolicySites("DefaultPolicy", "the default policy"),
            unset: Tri.True
        );

        // Named policies next: a convention or filter below may name one.
        IndexNamedPolicies();

        foreach (
            var (tree, semanticModel) in _origins.TreesMentioning(
                "RequireAuthorization",
                "AllowAnonymous",
                "AuthorizeFilter"
            )
        )
        {
            foreach (
                var node in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            )
            {
                if (semanticModel.GetOperation(node) is IInvocationOperation call)
                {
                    ReadConvention(call);
                    ReadMvcFilter(call);
                }
            }
        }

        _fallback = Settled(PolicySites("FallbackPolicy", "the fallback policy"), unset: Tri.False);
    }

    /// <summary>The app's authorization setup, scanned once.</summary>
    public static HandlerAuthorization Scan(
        Compilation compilation,
        WellKnownTypes wkt,
        BuilderOrigins origins
    ) => new(compilation, wkt, origins);

    /// <summary>An attribute-routed MVC action.</summary>
    public Verdict ForMvc(IMethodSymbol action)
    {
        var facts = new List<Fact>();
        foreach (var method in action.OverriddenChain().Prepend(action))
        {
            facts.AddRange(AttributeFacts(method, $"{action.ContainingType.Name}.{method.Name}"));
        }

        for (var type = action.ContainingType; type is not null; type = type.BaseType)
        {
            facts.AddRange(AttributeFacts(type, type.Name));
        }

        facts.AddRange(_mvcConventions);
        facts.AddRange(_untraced.Where(untraced => untraced.Mvc).Select(untraced => untraced.Fact));
        facts.AddRange(_mvcFilters);
        return Decide(facts, _fallback);
    }

    /// <summary>
    /// A minimal-API handler, in one of its mappings (<paramref name="chain"/>: its Map* call
    /// and the route groups that mapping is made in).
    /// </summary>
    public Verdict ForMinimalApi(IMethodSymbol handler, BuilderChain chain)
    {
        var facts = AttributeFacts(handler, "the handler").ToList();
        foreach (var link in chain.Links)
        {
            facts.AddRange(ConventionsOn(link));
        }

        // A builder the chain doesn't follow to its MapGroup call or the app (a field, a
        // helper's answer without source, a group parameter nothing passes) may be a group
        // whose conventions --check can't see.
        if (chain.Unresolved)
        {
            var receiver = chain.Links[^1].Call.Arguments[0].Value;
            var builder = Unwrap(receiver).Type.DerivesFrom(_wkt.RouteGroupBuilder)
                ? "the route group"
                : "the builder";
            var stops = BuilderOrigins.SameCall(receiver, chain.End)
                ? ""
                : $" (it stops at '{chain.End.Syntax}' at {chain.End.Syntax.SourcePosition()})";
            facts.Add(
                new Fact(
                    FactKind.Authorize,
                    Tri.Unknown(
                        $"it's mapped on {builder} '{receiver.Syntax}' at {receiver.Syntax.SourcePosition()}, "
                            + $"which --check can't trace to the MapGroup call or the app that made it{stops}, "
                            + "so it can't see that group's RequireAuthorization or AllowAnonymous"
                    ),
                    Tri.Unknown(null),
                    "its route group"
                )
            );
        }

        facts.AddRange(
            _untraced.Where(untraced => untraced.MinimalApi).Select(untraced => untraced.Fact)
        );
        return Decide(facts, _fallback);
    }

    /// <summary>
    /// An Azure Function: its trigger's <c>AuthorizationLevel</c> alone. A level other than
    /// <c>Anonymous</c> needs a key; no level given is the attribute's default,
    /// <c>Function</c>.
    /// </summary>
    public Verdict ForFunction(IMethodSymbol function)
    {
        var trigger = function
            .Parameters.SelectMany(parameter => parameter.GetAttributes())
            .FirstOrDefault(attribute => attribute.Is(_wkt.HttpTrigger));
        var arguments = trigger is null
            ? []
            : trigger.ConstructorArguments.Concat(
                trigger
                    .NamedArguments.Where(named => named.Key == "AuthLevel")
                    .Select(named => named.Value)
            );
        var level =
            arguments
                .Where(argument =>
                    IsType(argument.Type as INamedTypeSymbol, _wkt.FunctionsAuthorizationLevel)
                )
                .Select(argument =>
                    argument
                        .Type!.GetMembers()
                        .OfType<IFieldSymbol>()
                        .FirstOrDefault(member =>
                            member.HasConstantValue && Equals(member.ConstantValue, argument.Value)
                        )
                        ?.Name
                )
                .LastOrDefault()
            ?? "Function";
        var source = $"its trigger's AuthorizationLevel.{level}";
        return new Verdict(level != "Anonymous", [source]);
    }

    // ---------------------------------------------------------------
    // The verdict
    // ---------------------------------------------------------------

    private static Verdict Decide(IReadOnlyList<Fact> facts, Tri fallback)
    {
        // An [AllowAnonymous] that's certainly there decides it, however many unknowns
        // there are besides.
        var allowed = facts
            .Where(fact => fact.Kind == FactKind.AllowAnonymous && fact.Present.Value == true)
            .Select(fact => fact.Source)
            .Distinct()
            .ToList();
        if (allowed.Count > 0)
        {
            return new Verdict(false, allowed);
        }

        var reasons = new List<string>();
        var variables = 0;
        var keyed = new List<(ConditionKey Key, int Variable)>();
        var compiled = facts
            .Select(fact =>
                (fact.Kind, Present: Compile(fact.Present), Requires: Compile(fact.Requires))
            )
            .ToList();
        var compiledFallback = Compile(fallback);
        if (variables > MaxUnknowns)
        {
            return new Verdict(null, reasons);
        }

        bool? decided = null;
        for (var bits = 0L; bits < 1L << variables; bits++)
        {
            var requires = Requires(bits);
            if (decided is { } earlier && earlier != requires)
            {
                return new Verdict(null, reasons);
            }

            decided = requires;
        }

        var answer = decided ?? false;
        var authorizes = facts
            .Where(fact =>
                fact.Kind != FactKind.AllowAnonymous
                && fact.Present.Value == true
                && fact.Requires.Value == true
            )
            .Select(fact => fact.Source)
            .ToList();
        IReadOnlyList<string> because = answer
            ? authorizes
                .Concat(
                    fallback.Value == true
                    && !facts.Any(fact =>
                        fact.Kind == FactKind.Authorize && fact.Present.Value == true
                    )
                        ? [fallback.Source!]
                        : []
                )
                .Distinct()
                .ToList()
            : [];
        return new Verdict(answer, because);

        // A known value, or the next variable. An unknown without a reason of its own (the
        // requirement of a fact whose presence is the unknown) adds a case, never a reason.
        // Unknowns keyed by one condition (decision 2) share its variable, the other way round
        // for its other branch.
        (bool Value, int Variable, bool Negated) Compile(Tri tri)
        {
            if (tri.Value is { } known)
            {
                return (known, -1, false);
            }

            if (tri.Why is { } why && !reasons.Contains(why))
            {
                reasons.Add(why);
            }

            if (tri.Key is not { } key)
            {
                return (false, variables++, false);
            }

            foreach (var (earlier, variable) in keyed)
            {
                if (earlier.SameUnknown(key))
                {
                    return (false, variable, earlier.Positive != key.Positive);
                }
            }

            keyed.Add((key, variables));
            return (false, variables++, false);
        }

        bool Requires(long bits)
        {
            bool Value((bool Value, int Variable, bool Negated) fact) =>
                fact.Variable < 0
                    ? fact.Value
                    : (((bits >> fact.Variable) & 1) == 1) != fact.Negated;

            if (compiled.Any(fact => fact.Kind == FactKind.AllowAnonymous && Value(fact.Present)))
            {
                return false;
            }

            var data = compiled
                .Where(fact => fact.Kind == FactKind.Authorize && Value(fact.Present))
                .ToList();
            var middleware =
                data.Count > 0 ? data.Any(fact => Value(fact.Requires)) : Value(compiledFallback);
            return middleware
                || compiled.Any(fact =>
                    fact.Kind == FactKind.MvcFilter && Value(fact.Present) && Value(fact.Requires)
                );
        }
    }

    // ---------------------------------------------------------------
    // Attributes
    // ---------------------------------------------------------------

    private IEnumerable<Fact> AttributeFacts(ISymbol symbol, string on)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass is not { } type)
            {
                continue;
            }

            var source =
                $"[{type.Name.Replace("Attribute", "", StringComparison.Ordinal)}] on {on}";
            if (SecurityDeclaration.IsAllowAnonymous(_wkt, type))
            {
                yield return new Fact(FactKind.AllowAnonymous, Tri.True, Tri.True, source);
            }
            else if (IsAuthorizeData(type))
            {
                yield return new Fact(
                    FactKind.Authorize,
                    Tri.True,
                    AttributePolicy(attribute, source),
                    source
                );
            }
        }
    }

    // An IAuthorizeData attribute's Roles and Policy, set as named arguments or, for
    // AuthorizeAttribute's own constructor, the policy it takes. Another type's constructor
    // sets them where --check doesn't look.
    private Tri AttributePolicy(AttributeData attribute, string source)
    {
        string? policy = null;
        if (attribute.ConstructorArguments.Length > 0)
        {
            if (
                !IsType(attribute.AttributeConstructor?.ContainingType, _wkt.Authorize)
                || attribute.ConstructorArguments[0].Value is not string named
            )
            {
                return Tri.Unknown(
                    $"{source} is made by its own constructor, which may set a policy or roles --check can't read"
                );
            }

            policy = named;
        }

        return AuthorizeData(Named("Roles"), Named("Policy") ?? policy);

        string? Named(string key) =>
            attribute
                .NamedArguments.Where(named => named.Key == key)
                .Select(named => named.Value.Value as string)
                .LastOrDefault();
    }

    /// <summary>
    /// What an <c>IAuthorizeData</c> asks for, as ASP.NET's <c>AuthorizationPolicy.CombineAsync</c>
    /// reads it: roles require a user in one of them; a policy name means that policy;
    /// neither means the default policy.
    /// </summary>
    private Tri AuthorizeData(string? roles, string? policy) =>
        IsNamed(roles) ? Tri.True
        : IsNamed(policy) ? NamedPolicy(policy!)
        : _defaultPolicy;

    private bool IsAuthorizeData(ITypeSymbol? type) =>
        type.Implements(_wkt.IAuthorizeData) || type.DerivesFrom(_wkt.Authorize);

    // A policy name or a role list: ASP.NET reads an empty or blank one as not given.
    private static bool IsNamed(object? value) =>
        value is string name && !string.IsNullOrWhiteSpace(name);

    // ---------------------------------------------------------------
    // Conventions: RequireAuthorization and AllowAnonymous
    // ---------------------------------------------------------------

    private IEnumerable<Fact> ConventionsOn(ChainLink link) =>
        (_conventions.GetValueOrDefault((link.Call.Syntax.SyntaxTree, link.Call.Syntax.Span)) ?? [])
            .Where(convention => link.Reaches(convention.Descended))
            .Select(convention => convention.Fact);

    private void ReadConvention(IInvocationOperation call)
    {
        if (
            call.TargetMethod.Name is not ("RequireAuthorization" or "AllowAnonymous")
            || !IsType(
                call.TargetMethod.ContainingType,
                _wkt.AuthorizationEndpointConventionBuilderExtensions
            )
            || call.Arguments is not [var receiver, ..]
        )
        {
            return;
        }

        var policies = call.Arguments.Skip(1).ToList();
        var allow = call.TargetMethod.Name == "AllowAnonymous";
        var name = $"{call.TargetMethod.Name}()";
        foreach (var trace in _origins.Trace(receiver.Value))
        {
            // A helper nothing calls applies its convention to nothing.
            if (trace.End == TraceEnd.Uncalled)
            {
                continue;
            }

            var origin = trace.Origin;
            var on = origin switch
            {
                null => "a builder",
                _ when _origins.IsMapControllers(origin) => "MapControllers()",
                _ when _origins.IsMapGroup(origin) => "its route group",
                _ => "the endpoint",
            };
            var source = $"{name} on {on} at {call.Syntax.SourcePosition()}";
            var requires = allow
                ? Tri.True
                : Policies(
                    policies.Select(policy =>
                        ReadPolicy(
                            policy.Value,
                            $"{source} takes a policy --check can't read ('{policy.Syntax}')"
                        )
                    )
                );
            var kind = allow ? FactKind.AllowAnonymous : FactKind.Authorize;

            if (origin is null)
            {
                // It may reach any endpoint whose builder has this type.
                var builder = Unwrap(receiver.Value).Type;
                var mvc =
                    !builder.DerivesFrom(_wkt.RouteHandlerBuilder)
                    && !builder.DerivesFrom(_wkt.RouteGroupBuilder);
                var minimal = !builder.DerivesFrom(_wkt.ControllerActionEndpointConventionBuilder);
                _untraced.Add(
                    (
                        new Fact(
                            kind,
                            Tri.Unknown(
                                $"{source} is applied to a builder --check can't trace to the Map* "
                                    + "call that made it, so it can't tell which endpoints it covers"
                            ),
                            requires,
                            source
                        ),
                        mvc,
                        minimal
                    )
                );
                continue;
            }

            // One fact for each way the convention comes to run; none when it never does.
            var facts = _origins
                .Ways(trace)
                .SelectMany(traced =>
                    _origins.Conditions(call.Syntax, receiver.Value, origin, traced)
                )
                .Distinct()
                .Select(condition => new Fact(
                    kind,
                    condition is null
                        ? Tri.True
                        : Tri.Unknown($"{source} is applied only {condition.Why}", condition.Key),
                    requires,
                    source
                ));
            if (_origins.IsMapControllers(origin))
            {
                _mvcConventions.AddRange(facts);
            }
            else
            {
                var key = (origin.Syntax.SyntaxTree, origin.Syntax.Span);
                if (!_conventions.TryGetValue(key, out var onOrigin))
                {
                    onOrigin = [];
                    _conventions[key] = onOrigin;
                }

                onOrigin.AddRange(facts.Select(fact => (trace.Descended, fact)));
            }
        }
    }

    // ---------------------------------------------------------------
    // MVC filters
    // ---------------------------------------------------------------

    /// <summary>
    /// An <c>AuthorizeFilter</c> added to <c>MvcOptions.Filters</c> (a <c>FilterCollection</c>):
    /// <c>Filters.Add(new AuthorizeFilter(...))</c>, by instance, by type or as a service. Any
    /// other filter is a custom one, and invisible.
    /// </summary>
    private void ReadMvcFilter(IInvocationOperation call)
    {
        if (
            call.TargetMethod.Name is not ("Add" or "Insert" or "AddService")
            || call.Instance?.Type is not { } collection
            || !collection.DerivesFrom(_wkt.FilterCollection)
        )
        {
            return;
        }

        var source = $"the global AuthorizeFilter added at {call.Syntax.SourcePosition()}";
        Tri requires;
        if (call.TargetMethod.TypeArguments is [var typeArgument])
        {
            if (!typeArgument.DerivesFrom(_wkt.AuthorizeFilter))
            {
                return;
            }

            requires = ActivatedFilter(source);
        }
        else if (
            call
                .Arguments.Select(argument => Unwrap(argument.Value))
                .OfType<ITypeOfOperation>()
                .FirstOrDefault() is
            { } typeOf
        )
        {
            if (!typeOf.TypeOperand.DerivesFrom(_wkt.AuthorizeFilter))
            {
                return;
            }

            requires = ActivatedFilter(source);
        }
        else if (
            call.Arguments.LastOrDefault(argument =>
                argument.Parameter?.Type.SpecialType != SpecialType.System_Int32
            ) is
            { } filterArgument
        )
        {
            var filter = Provenance(filterArgument.Value);
            if (!filter.Type.DerivesFrom(_wkt.AuthorizeFilter))
            {
                return;
            }

            requires =
                filter is IObjectCreationOperation creation
                && IsType(creation.Type as INamedTypeSymbol, _wkt.AuthorizeFilter)
                    ? Policies(
                        creation.Arguments.Select(argument =>
                            ReadPolicy(
                                argument.Value,
                                $"{source} takes a policy --check can't read ('{argument.Syntax}')"
                            )
                        )
                    )
                    : Tri.Unknown(
                        $"{source} isn't a new AuthorizeFilter(...) --check can read ('{filterArgument.Syntax}')"
                    );
        }
        else
        {
            return;
        }

        var present = Presence(call.Syntax);
        if (present.Value == false)
        {
            return;
        }

        _mvcFilters.Add(
            new Fact(
                FactKind.MvcFilter,
                present.Value is null
                    ? Tri.Unknown($"{source} is added only {present.Why}", present.Key)
                    : present,
                requires,
                source
            )
        );

        static Tri ActivatedFilter(string source) =>
            Tri.Unknown(
                $"{source} is activated by type, so --check can't read the policy it's built with"
            );
    }

    // ---------------------------------------------------------------
    // The default and fallback policies
    // ---------------------------------------------------------------

    /// <summary>
    /// Every site that sets the app's <paramref name="property"/> (<c>DefaultPolicy</c> or
    /// <c>FallbackPolicy</c>): an assignment to <c>AuthorizationOptions</c>' property, or
    /// <c>AuthorizationBuilder</c>'s <c>Set…</c> or <c>Add…</c> call (<c>Add…</c> registers the
    /// policy under a name too, so its policy or configure lambda is the second argument). A
    /// site in a method nothing runs sets nothing.
    /// </summary>
    private List<(Tri Requires, Tri Present, string Source)> PolicySites(
        string property,
        string name
    )
    {
        var sites = new List<(Tri, Tri, string)>();
        foreach (var (tree, semanticModel) in _origins.TreesMentioning(property))
        {
            foreach (
                var node in tree.GetRoot()
                    .DescendantNodes()
                    .Where(node => node is InvocationExpressionSyntax or AssignmentExpressionSyntax)
            )
            {
                var value = semanticModel.GetOperation(node) switch
                {
                    IInvocationOperation call
                        when IsType(call.TargetMethod.ContainingType, _wkt.AuthorizationBuilder) =>
                        call.TargetMethod.Name == "Set" + property ? PolicyArgument(call, 0)
                        : call.TargetMethod.Name == "Add" + property ? PolicyArgument(call, 1)
                        : null,
                    ISimpleAssignmentOperation
                    {
                        Target: IPropertyReferenceOperation { Property: var assigned },
                        Value: var assignedValue,
                    }
                        when assigned.Name == property
                            && IsType(assigned.ContainingType, _wkt.AuthorizationOptions) =>
                        assignedValue,
                    _ => null,
                };
                if (
                    value is not null
                    && PolicySite(node, value, $"{name} set at {node.SourcePosition()}") is { } site
                )
                {
                    sites.Add(site);
                }
            }
        }

        return sites;
    }

    /// <summary>
    /// Indexes every named policy registration: <c>AuthorizationOptions.AddPolicy</c>, and
    /// <c>AuthorizationBuilder</c>'s <c>AddPolicy</c>, <c>AddDefaultPolicy</c>, and
    /// <c>AddFallbackPolicy</c>, each taking the name first and the policy or its configure
    /// lambda second. A registration whose name --check can't read, and a registered
    /// <c>IAuthorizationPolicyProvider</c>, may serve any name, so each joins every name's
    /// registrations as a policy --check can't read.
    /// </summary>
    private void IndexNamedPolicies()
    {
        foreach (
            var (tree, semanticModel) in _origins.TreesMentioning(
                "AddPolicy",
                "AddDefaultPolicy",
                "AddFallbackPolicy"
            )
        )
        {
            foreach (
                var node in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            )
            {
                if (
                    semanticModel.GetOperation(node) is not IInvocationOperation call
                    || !(
                        call.TargetMethod.Name == "AddPolicy"
                            && IsType(call.TargetMethod.ContainingType, _wkt.AuthorizationOptions)
                        || call.TargetMethod.Name
                            is "AddPolicy"
                                or "AddDefaultPolicy"
                                or "AddFallbackPolicy"
                            && IsType(call.TargetMethod.ContainingType, _wkt.AuthorizationBuilder)
                    )
                    || PolicyArgument(call, 0) is not { } name
                    || PolicyArgument(call, 1) is not { } value
                    || PolicySite(node, value, $"registered at {node.SourcePosition()}")
                        is not { } site
                )
                {
                    continue;
                }

                if (Provenance(name).ConstantValue is { HasValue: true, Value: string constant })
                {
                    if (!_namedPolicies.TryGetValue(constant, out var named))
                    {
                        named = [];
                        _namedPolicies[constant] = named;
                    }

                    named.Add(site);
                }
                else
                {
                    _anyPolicy.Add(
                        site with
                        {
                            Requires = Tri.Unknown(
                                $"may be registered at {node.SourcePosition()}, under a name "
                                    + $"--check can't read ('{name.Syntax}')"
                            ),
                        }
                    );
                }
            }
        }

        foreach (
            var (tree, semanticModel) in _origins.TreesMentioning("IAuthorizationPolicyProvider")
        )
        {
            foreach (
                var node in tree.GetRoot()
                    .DescendantNodes()
                    .Where(node =>
                        node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
                    )
            )
            {
                if (
                    semanticModel.GetOperation(node) is not { } operation
                    || Registration(operation) is not { } registration
                    || !registration.Services.Any(service =>
                        IsType(service as INamedTypeSymbol, _wkt.IAuthorizationPolicyProvider)
                    )
                    || (registration.Descriptor ? DescriptorAdded(node, semanticModel) : node)
                        is not { } site
                )
                {
                    continue;
                }

                var present = Presence(site);
                if (present.Value != false)
                {
                    var source =
                        $"the IAuthorizationPolicyProvider registered at {site.SourcePosition()}";
                    _anyPolicy.Add(
                        (
                            Tri.Unknown($"may be served by {source}, which --check can't read"),
                            present,
                            source
                        )
                    );
                }
            }
        }
    }

    /// <summary>
    /// The policy registered under <paramref name="name"/>, over every registration of it
    /// <see cref="IndexNamedPolicies"/> found. A name with no registration --check can read
    /// (none, only under a condition, or one that may be overridden by a site it can't read)
    /// is unknown, as ASP.NET would fail the request or serve another policy.
    /// </summary>
    private Tri NamedPolicy(string name)
    {
        List<(Tri Requires, Tri Present, string Source)> sites =
        [
            .. _namedPolicies.GetValueOrDefault(name) ?? [],
            .. _anyPolicy,
        ];
        var policy = Settled(
            sites,
            unset: Tri.Unknown($"no registration --check can read for policy '{name}'")
        );
        return policy.Value is null && sites.Count > 0
            ? Tri.Unknown($"policy '{name}' {policy.Why}")
            : policy;
    }

    /// <summary>
    /// One site that sets or registers a policy: whether it runs, and what its
    /// <paramref name="value"/> requires (null, which turns a fallback off, requires nothing).
    /// Null when it never runs.
    /// </summary>
    private (Tri Requires, Tri Present, string Source)? PolicySite(
        SyntaxNode node,
        IOperation value,
        string source
    )
    {
        var present = Presence(node);
        if (present.Value == false)
        {
            return null;
        }

        var requires = Provenance(value).ConstantValue is { HasValue: true, Value: null }
            ? Tri.False
            : ReadPolicy(value, $"{source} is a policy --check can't read ('{value.Syntax}')");
        return (requires, present, source);
    }

    private static IOperation? PolicyArgument(IInvocationOperation call, int ordinal) =>
        call.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == ordinal)?.Value;

    /// <summary>
    /// A policy's verdict over every site that sets it, <paramref name="unset"/> when none
    /// does. When every site is conditional, none may run, so <paramref name="unset"/> is one
    /// more value it may take. Values that all agree decide it; anything else is unknown, since
    /// which set runs last can't be read. One site under a keyed condition (decision 2), whose
    /// policy and <paramref name="unset"/> are both known and differ, is that condition: the
    /// policy where it holds, <paramref name="unset"/> where it doesn't.
    /// </summary>
    private static Tri Settled(
        IReadOnlyList<(Tri Requires, Tri Present, string Source)> sites,
        Tri unset
    )
    {
        if (sites.Count == 0)
        {
            return unset;
        }

        if (
            sites is [{ Present.Key: { } key, Requires.Value: { } requires } only]
            && unset.Value is { } unsetValue
            && requires != unsetValue
        )
        {
            return Tri.Unknown(
                $"{only.Source} only {only.Present.Why}",
                requires ? key : key.Negated()
            );
        }

        var always = sites.Where(site => site.Present.Value == true).ToList();
        var values = sites
            .Select(site => site.Requires.Value)
            .Concat(always.Count == 0 ? [unset.Value] : [])
            .Distinct()
            .ToList();
        if (values is [{ } value])
        {
            return new Tri(
                value,
                null,
                string.Join(", ", (always.Count > 0 ? always : sites).Select(site => site.Source))
            );
        }

        return Tri.Unknown(
            string.Join(
                "; ",
                sites.Select(site =>
                    site.Requires.Value is null ? site.Requires.Why!
                    : site.Present.Value is null ? $"{site.Source} only {site.Present.Why}"
                    : site.Source
                )
            )
        );
    }

    // ---------------------------------------------------------------
    // Whether app-wide setup runs
    // ---------------------------------------------------------------

    /// <summary>
    /// Whether app-wide setup at <paramref name="site"/> (an MVC filter, or a default or
    /// fallback policy) runs: always (true), never (false), or only under a condition
    /// (unknown, saying which). In a method or local function, past any configure lambda it's
    /// passed in, it runs where the method is called, and always when any call always runs.
    /// A call through an interface member the method implements, or a method it overrides,
    /// counts when its receiver is visibly an instance whose implementation is this method. A
    /// method handed to a call as a method group (<c>AddControllers(Configure)</c>) runs where
    /// that call does; one handed on any other way can't be followed. Beyond those, the host
    /// runs only what ASP.NET's own rules run: top-level statements and the entry point, the
    /// Startup methods <c>StartupLoader</c> finds on a type handed to <c>UseStartup</c>, and a
    /// framework interface's or virtual's implementation, where its type is registered with
    /// the framework under that interface. Anything else, a constructor, an accessor, or an
    /// initializer included, runs where --check doesn't follow, so it's unknown.
    /// </summary>
    private Tri Presence(SyntaxNode site) =>
        Presence(site, new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default));

    private Tri Presence(SyntaxNode site, HashSet<IMethodSymbol> visiting)
    {
        // The site's own condition, under whatever runs its function. It's keyed only in code
        // that runs once (A1): a method may run more than once, or under its callers'
        // conditions too.
        var own = _origins.ConditionWhy(site, null);
        var runs = Runs(site, visiting);
        return own is null || runs.Value == false ? runs
            : runs.Value == true
                ? Tri.Unknown(
                    own.Why,
                    _origins.RunsOnce(BuilderOrigins.EnclosingFunction(site), site) ? own.Key : null
                )
            : Tri.Unknown($"{own.Why}, and {runs.Why}");
    }

    /// <summary>Whether the function holding <paramref name="site"/> runs, past any condition inside it.</summary>
    private Tri Runs(SyntaxNode site, HashSet<IMethodSymbol> visiting)
    {
        var function = BuilderOrigins.EnclosingFunction(site);
        if (function is null)
        {
            // Top-level statements run; an initializer runs when its type is made.
            return site.Ancestors().Any(node => node is GlobalStatementSyntax)
                ? Tri.True
                : Tri.Unknown(
                    $"in the initializer at {site.SourcePosition()}, which runs where --check "
                        + "doesn't follow"
                );
        }

        var declared = _compilation
            .GetSemanticModel(function.SyntaxTree)
            .GetDeclaredSymbol(function);
        if (
            declared
            is not IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary
                    or MethodKind.LocalFunction
                    or MethodKind.ExplicitInterfaceImplementation,
            } method
        )
        {
            return Tri.Unknown(
                $"in {declared?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat) ?? "code"} "
                    + $"at {site.SourcePosition()}, which runs where --check doesn't follow"
            );
        }

        var definition = method.OriginalDefinition;
        if (
            SymbolEqualityComparer.Default.Equals(
                definition,
                _compilation.GetEntryPoint(CancellationToken.None)
            )
        )
        {
            return Tri.True;
        }

        if (!visiting.Add(definition))
        {
            // A call from inside its own call graph adds no way in.
            return Tri.False;
        }

        try
        {
            // A way in is a call or a registration, which may run more than once or under its
            // own callers' conditions, so it's never keyed.
            var ways = WaysIn(definition, visiting);
            if (ways.Any(way => way.Value == true))
            {
                return Tri.True;
            }

            // Any way in that may run makes it unknown; the first that says why names it.
            var unknowns = ways.Where(way => way.Value is null).ToList();
            return unknowns.Count == 0 ? Tri.False
                : unknowns.FirstOrDefault(way => way.Why is not null) is { Why: not null } unknown
                    ? unknown with
                    {
                        Key = null,
                    }
                : Tri.Unknown($"where {definition.Name}() is called in a way --check can't follow");
        }
        finally
        {
            visiting.Remove(definition);
        }
    }

    /// <summary>Every way <paramref name="method"/> can come to run, each true, false or unknown.</summary>
    private List<Tri> WaysIn(IMethodSymbol method, HashSet<IMethodSymbol> visiting)
    {
        var ways = new List<Tri>();
        var framework = new List<IMethodSymbol>();
        foreach (var target in Dispatches(method).Prepend(method))
        {
            if (target.OriginalDefinition.DeclaringSyntaxReferences.IsEmpty)
            {
                framework.Add(target);
                continue;
            }

            var (calls, escapes) = _origins.CallsTo(target);
            ways.AddRange(
                calls.Select(call =>
                    Dispatched(
                        target,
                        Presence(call.Syntax, visiting) switch
                        {
                            { Value: null, Why: var why } => Tri.Unknown(
                                $"{why}, where {target.Name}() is called"
                            ),
                            var way => way,
                        },
                        call.Instance,
                        call.IsVirtual,
                        call.Syntax
                    )
                )
            );
            ways.AddRange(
                escapes.Select(reference =>
                {
                    var way = reference.Parent
                        is ArgumentSyntax
                        {
                            Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call },
                        }
                        ? Presence(call, visiting)
                        : Tri.Unknown(
                            $"where {target.Name}() runs, which --check can't follow: it's "
                                + $"handed on as a delegate at {reference.SourcePosition()}"
                        );
                    var group =
                        _compilation.GetSemanticModel(reference.SyntaxTree).GetOperation(reference)
                        as IMethodReferenceOperation;
                    return Dispatched(
                        target,
                        way,
                        group?.Instance,
                        group?.IsVirtual ?? true,
                        reference
                    );
                })
            );
        }

        if (framework.Count > 0)
        {
            ways.AddRange(HandedToTheFramework(method, framework, visiting));
        }

        ways.AddRange(StartupWays(method, visiting));
        return ways;

        // A call to target runs this method only when the receiver's runtime type has it as
        // target's implementation.
        Tri Dispatched(
            IMethodSymbol target,
            Tri way,
            IOperation? receiver,
            bool isVirtual,
            SyntaxNode at
        )
        {
            if (way.Value == false)
            {
                return way;
            }

            switch (Runs(method, target, receiver, isVirtual))
            {
                case true:
                    return way;
                case false:
                    return Tri.False;
            }

            var why =
                $"{target.Name}() is called at {at.SourcePosition()} on a receiver --check can't "
                + $"tell is a {method.ContainingType.Name}";
            return Tri.Unknown(way.Value == true ? $"where {why}" : $"{way.Why}, and {why}");
        }
    }

    /// <summary>
    /// Whether a call to <paramref name="target"/> on <paramref name="receiver"/> runs
    /// <paramref name="method"/>: a call that isn't dispatched runs the method it names; a
    /// dispatched one runs the implementation its receiver's runtime type has, so it's read
    /// from a receiver made with <c>new</c> where --check can see it, and from a direct call
    /// to a method nothing in the compilation overrides. Null when that can't be told.
    /// </summary>
    private bool? Runs(
        IMethodSymbol method,
        IMethodSymbol target,
        IOperation? receiver,
        bool isVirtual
    )
    {
        var direct = Same(target, method);
        if (!isVirtual || receiver is null)
        {
            return direct;
        }

        if (Provenance(receiver) is IObjectCreationOperation { Type: { } created })
        {
            return Same(Implementation(created, target), method);
        }

        return direct && !Overridden(method) ? true : null;
    }

    /// <summary>The method <paramref name="type"/> runs for <paramref name="member"/>, an interface member or a virtual.</summary>
    private static IMethodSymbol? Implementation(ITypeSymbol type, IMethodSymbol member)
    {
        if (member.ContainingType.TypeKind == TypeKind.Interface)
        {
            return type.FindImplementationForInterfaceMember(member) as IMethodSymbol;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(member.Name).OfType<IMethodSymbol>())
            {
                if (
                    candidate.OverriddenChain().Prepend(candidate).Any(chain => Same(chain, member))
                )
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    // A source method that overrides method, so a call to it may run that one instead.
    private bool Overridden(IMethodSymbol method) =>
        (method.IsVirtual || method.IsAbstract || method.IsOverride)
        && !method.IsSealed
        && !method.ContainingType.IsSealed
        && _compilation
            .GetSymbolsWithName(method.Name, SymbolFilter.Member)
            .OfType<IMethodSymbol>()
            .Any(candidate =>
                candidate.OverriddenChain().Any(overridden => Same(overridden, method))
            );

    /// <summary>The interface members <paramref name="method"/> implements, and the methods it overrides.</summary>
    private static IEnumerable<IMethodSymbol> Dispatches(IMethodSymbol method)
    {
        var type = method.ContainingType;
        foreach (
            var member in type.AllInterfaces.SelectMany(candidate =>
                candidate.GetMembers().OfType<IMethodSymbol>()
            )
        )
        {
            if (
                SymbolEqualityComparer.Default.Equals(
                    type.FindImplementationForInterfaceMember(member),
                    method
                )
            )
            {
                yield return member;
            }
        }

        foreach (var overridden in method.OverriddenChain())
        {
            yield return overridden;
        }
    }

    private static bool Same(IMethodSymbol? method, IMethodSymbol? other) =>
        method is not null
        && SymbolEqualityComparer.Default.Equals(
            method.OriginalDefinition,
            other?.OriginalDefinition
        );

    /// <summary>
    /// A method the framework calls through its own interface or virtual
    /// (<paramref name="members"/>) runs where its type is registered with the framework
    /// under that interface, as ASP.NET's options system and service container find it:
    /// <c>ConfigureOptions&lt;T&gt;()</c>, <c>ConfigureOptions(typeof(T))</c> or
    /// <c>ConfigureOptions(new T())</c>, under each options interface <c>T</c> implements; or
    /// an unkeyed service registration whose service type is the interface (or one it
    /// extends, as <c>IConfigureNamedOptions&lt;T&gt;</c> extends
    /// <c>IConfigureOptions&lt;T&gt;</c>) and whose implementation, <c>T</c> or a type
    /// inheriting this method, is one --check can read. Any other mention of the type
    /// (<c>AddSingleton&lt;T&gt;()</c>, which registers it as itself, a <c>nameof</c>, or a
    /// field's type) hands the framework nothing. Of a setup that has
    /// <c>IConfigureNamedOptions&lt;T&gt;</c>, <c>OptionsFactory</c> calls only the named
    /// <c>Configure</c>. A type never registered that way may still be found some way --check
    /// can't see.
    /// </summary>
    private IEnumerable<Tri> HandedToTheFramework(
        IMethodSymbol method,
        IReadOnlyList<IMethodSymbol> members,
        HashSet<IMethodSymbol> visiting
    )
    {
        var type = method.ContainingType.OriginalDefinition;
        members = [.. members.Where(member => !NamedInstead(member, method.ContainingType))];
        if (members.Count == 0)
        {
            return [];
        }

        if (!_registrations.TryGetValue(type, out var registrations))
        {
            registrations = RegistrationsOf(type);
            _registrations[type] = registrations;
        }

        var ways = registrations
            .Where(registration =>
                members.Any(member =>
                    registration.Services.Any(service => Serves(member.ContainingType, service))
                    && Same(Implementation(registration.Implementation, member), method)
                    && !NamedInstead(member, registration.Implementation)
                )
            )
            .Select(registration => registration.Site)
            .Distinct()
            .Select(site =>
                Presence(site, visiting) switch
                {
                    { Value: null, Why: var why } => Tri.Unknown(
                        $"{why}, where {type.Name} is registered"
                    ),
                    var way => way,
                }
            )
            .ToList();
        if (ways.Count > 0)
        {
            return ways;
        }

        var under = members[0]
            .ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return
        [
            Tri.Unknown(
                $"where the framework calls {type.Name}.{method.Name}(), and nothing in the "
                    + $"project registers {type.Name} as {under} in a way --check can read, so "
                    + "it can't tell whether it does"
            ),
        ];

        // OptionsFactory calls an IConfigureNamedOptions<T> setup's Configure(name, options),
        // never its IConfigureOptions<T>.Configure(options).
        bool NamedInstead(IMethodSymbol member, ITypeSymbol implementation) =>
            IsType(member.ContainingType, _wkt.IConfigureOptions)
            && implementation.AllInterfaces.Any(candidate =>
                IsType(candidate, _wkt.IConfigureNamedOptions)
                && SymbolEqualityComparer.Default.Equals(
                    candidate.TypeArguments[0],
                    member.ContainingType.TypeArguments[0]
                )
            );

        // The framework resolves service and calls declaring's member on what it gets.
        static bool Serves(INamedTypeSymbol declaring, ITypeSymbol service)
        {
            for (
                INamedTypeSymbol? current = declaring;
                current is not null;
                current = current.BaseType
            )
            {
                if (SymbolEqualityComparer.Default.Equals(current, service))
                {
                    return true;
                }
            }

            return declaring.AllInterfaces.Contains(service, SymbolEqualityComparer.Default);
        }
    }

    /// <summary>
    /// The registrations of <paramref name="type"/>, or of a source type derived from it, that
    /// <see cref="Registration"/> reads, found from each place the project names the type.
    /// </summary>
    private List<Registered> RegistrationsOf(INamedTypeSymbol type)
    {
        var registrations = new List<Registered>();
        var types = _compilation
            .GetSymbolsWithName(_ => true, SymbolFilter.Type)
            .OfType<INamedTypeSymbol>()
            .Where(candidate => candidate.DerivesFrom(type));
        foreach (var candidate in types)
        {
            foreach (var (name, semanticModel) in _origins.Mentions(candidate.OriginalDefinition))
            {
                foreach (var node in name.Ancestors())
                {
                    if (
                        node
                        is StatementSyntax
                            or MemberDeclarationSyntax
                            or AnonymousFunctionExpressionSyntax
                    )
                    {
                        break;
                    }

                    if (
                        node
                            is not (
                                InvocationExpressionSyntax
                                or BaseObjectCreationExpressionSyntax
                            )
                        || semanticModel.GetOperation(node) is not { } operation
                        || Registration(operation) is not { } registration
                        || registration.Implementation is not { } implementation
                        || !SymbolEqualityComparer.Default.Equals(
                            implementation.OriginalDefinition,
                            candidate.OriginalDefinition
                        )
                    )
                    {
                        continue;
                    }

                    var site = registration.Descriptor
                        ? DescriptorAdded(node, semanticModel)
                        : node;
                    if (site is not null)
                    {
                        registrations.Add(
                            new Registered(site, registration.Services, implementation)
                        );
                    }

                    break;
                }
            }
        }

        return registrations;
    }

    /// <summary>
    /// What <paramref name="operation"/> registers with ASP.NET's service container: under each
    /// options interface the type implements for <c>ConfigureOptions</c> (as its
    /// <c>FindConfigurationServices</c> reads them), or under the service type an unkeyed
    /// <c>Add{Lifetime}</c>, <c>TryAdd{Lifetime}</c>, or <c>ServiceDescriptor</c> names, with
    /// the implementation it names (its <c>new</c> instance's type, or the service type itself
    /// when it names none). A factory's implementation is null: --check can't read it.
    /// </summary>
    private (
        IReadOnlyList<ITypeSymbol> Services,
        ITypeSymbol? Implementation,
        bool Descriptor
    )? Registration(IOperation operation)
    {
        switch (operation)
        {
            case IInvocationOperation call
                when call.TargetMethod.Name == "ConfigureOptions"
                    && IsType(
                        call.TargetMethod.ContainingType,
                        _wkt.OptionsServiceCollectionExtensions
                    ):
            {
                var configured =
                    TypeArgument(call.TargetMethod, "TConfigureOptions")
                    ?? TypeOf(Argument(call.Arguments, "configureType"))
                    ?? Created(Argument(call.Arguments, "configureInstance"));
                return configured is null
                    ? null
                    : (
                        [
                            .. configured.AllInterfaces.Where(candidate =>
                                _optionsInterfaces.Any(options => IsType(candidate, options))
                            ),
                        ],
                        configured,
                        false
                    );
            }
            case IInvocationOperation call
                when call.TargetMethod.Name
                    is "AddSingleton"
                        or "AddScoped"
                        or "AddTransient"
                        or "TryAddSingleton"
                        or "TryAddScoped"
                        or "TryAddTransient"
                    && (
                        IsType(
                            call.TargetMethod.ContainingType,
                            _wkt.ServiceCollectionServiceExtensions
                        )
                        || IsType(
                            call.TargetMethod.ContainingType,
                            _wkt.ServiceCollectionDescriptorExtensions
                        )
                    ):
                return Service(call.TargetMethod, call.Arguments, descriptor: false);
            case IInvocationOperation call
                when call.TargetMethod.Name is "Singleton" or "Scoped" or "Transient" or "Describe"
                    && IsType(call.TargetMethod.ContainingType, _wkt.ServiceDescriptor):
                return Service(call.TargetMethod, call.Arguments, descriptor: true);
            case IObjectCreationOperation { Constructor: { } constructor } creation
                when IsType(constructor.ContainingType, _wkt.ServiceDescriptor)
                    && !constructor.Parameters.Any(parameter => parameter.Name == "serviceKey"):
                return Service(constructor, creation.Arguments, descriptor: true);
            default:
                return null;
        }

        static (IReadOnlyList<ITypeSymbol>, ITypeSymbol?, bool)? Service(
            IMethodSymbol method,
            IReadOnlyList<IArgumentOperation> arguments,
            bool descriptor
        )
        {
            var service =
                TypeArgument(method, "TService")
                ?? TypeOf(Argument(arguments, "serviceType", "service"));
            if (service is null)
            {
                return null;
            }

            var instance = Argument(arguments, "implementationInstance", "instance");
            var implementation =
                TypeArgument(method, "TImplementation")
                ?? TypeOf(Argument(arguments, "implementationType"))
                ?? (
                    instance is not null ? Created(instance)
                    : Argument(arguments, "implementationFactory", "factory") is not null ? null
                    : service
                );
            return ([service], implementation, descriptor);
        }

        static ITypeSymbol? TypeArgument(IMethodSymbol method, string name) =>
            method.TypeParameters.FirstOrDefault(parameter => parameter.Name == name) is { } found
                ? method.TypeArguments[found.Ordinal]
                : null;

        static IOperation? Argument(
            IReadOnlyList<IArgumentOperation> arguments,
            params string[] names
        ) =>
            arguments
                .FirstOrDefault(argument =>
                    argument.Parameter is { } parameter && names.Contains(parameter.Name)
                )
                ?.Value;

        static ITypeSymbol? TypeOf(IOperation? value) =>
            value is not null && Provenance(value) is ITypeOfOperation typeOf
                ? typeOf.TypeOperand
                : null;

        static ITypeSymbol? Created(IOperation? value) =>
            value is not null && Provenance(value) is IObjectCreationOperation { Type: { } type }
                ? type
                : null;
    }

    /// <summary>
    /// The call that hands the <c>ServiceDescriptor</c> made at <paramref name="descriptor"/>
    /// straight to the service collection (<c>Add</c>, <c>TryAdd</c>,
    /// <c>TryAddEnumerable</c>, <c>Replace</c>, or <c>Insert</c>), or null: a descriptor
    /// nothing visibly adds registers nothing.
    /// </summary>
    private SyntaxNode? DescriptorAdded(SyntaxNode descriptor, SemanticModel semanticModel) =>
        descriptor.Parent
            is ArgumentSyntax
            {
                Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax add }
            }
        && semanticModel.GetOperation(add) is IInvocationOperation call
        && (
            call.TargetMethod.Name is "Add" or "TryAdd" or "TryAddEnumerable" or "Replace"
                && IsType(
                    call.TargetMethod.ContainingType,
                    _wkt.ServiceCollectionDescriptorExtensions
                )
            || call.TargetMethod.Name is "Add" or "Insert"
                && call.Instance?.Type is { } collection
                && collection.Implements(_wkt.IServiceCollection)
        )
            ? add
            : null;

    /// <summary>
    /// <c>StartupLoader</c>'s rule: on a type handed to <c>UseStartup</c>, the host runs the
    /// public <c>Configure{Env}Services</c>, <c>Configure{Env}Container</c> and
    /// <c>Configure{Env}</c> methods it finds by reflection, matching names without regard to
    /// case, where the <c>UseStartup</c> call runs: the most-derived of an instance method
    /// (one the type overrides or hides isn't found), and a static one only on the type
    /// itself. An environment's own method runs only in that environment, and the plain one
    /// only outside every environment that has its own. A <c>UseStartup</c> whose type
    /// --check can't read may run any such method.
    /// </summary>
    private IEnumerable<Tri> StartupWays(IMethodSymbol method, HashSet<IMethodSymbol> visiting)
    {
        if (
            method
                is not {
                    MethodKind: MethodKind.Ordinary,
                    DeclaredAccessibility: Accessibility.Public
                }
            || StartupRole(method.Name) is not (var family, var environment)
        )
        {
            yield break;
        }

        var (readable, unreadable) = Startups();
        foreach (var (startup, sites) in readable)
        {
            if (
                method.IsStatic
                    ? !SymbolEqualityComparer.Default.Equals(startup, method.ContainingType)
                    : !startup.DerivesFrom(method.ContainingType) || Hidden(startup)
            )
            {
                continue;
            }

            var others = PublicMethods(startup)
                .Select(other => (other.Name, Role: StartupRole(other.Name)))
                .Where(other =>
                    other.Role is (var otherFamily, var otherEnvironment)
                    && otherFamily == family
                    && otherEnvironment.Length > 0
                )
                .Select(other => other.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var only =
                environment.Length > 0
                    ? $"when the environment is {environment}, since ASP.NET runs "
                        + $"{startup.Name}.{method.Name}() only then"
                : others.Count > 0
                    ? $"when the environment has no method of its own ({string.Join(", ", others)}), "
                        + $"since ASP.NET runs {startup.Name}.{method.Name}() only then"
                : null;
            foreach (var site in sites)
            {
                yield return Combine(Presence(site, visiting), only);
            }
        }

        foreach (var site in unreadable)
        {
            yield return Combine(
                Presence(site, visiting),
                $"if the type UseStartup at {site.SourcePosition()} hands over, which --check "
                    + $"can't read, has {method.ContainingType.Name}.{method.Name}() run"
            );
        }

        // A way in, under only that too.
        static Tri Combine(Tri way, string? only) =>
            only is null || way.Value == false ? way
            : way.Value == true ? Tri.Unknown(only)
            : Tri.Unknown($"{way.Why}, and {only}");

        // Reflection finds the most-derived public method with this name and parameters.
        bool Hidden(INamedTypeSymbol startup)
        {
            for (
                INamedTypeSymbol? current = startup;
                current is not null
                    && !SymbolEqualityComparer.Default.Equals(
                        current.OriginalDefinition,
                        method.ContainingType.OriginalDefinition
                    );
                current = current.BaseType
            )
            {
                if (
                    current
                        .GetMembers(method.Name)
                        .OfType<IMethodSymbol>()
                        .Any(candidate =>
                            candidate
                                is {
                                    MethodKind: MethodKind.Ordinary,
                                    DeclaredAccessibility: Accessibility.Public,
                                    IsStatic: false,
                                }
                            && candidate
                                .Parameters.Select(parameter => parameter.Type)
                                .SequenceEqual(
                                    method.Parameters.Select(parameter => parameter.Type),
                                    SymbolEqualityComparer.Default
                                )
                        )
                )
                {
                    return true;
                }
            }

            return false;
        }

        static IEnumerable<IMethodSymbol> PublicMethods(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                foreach (
                    var candidate in current
                        .GetMembers()
                        .OfType<IMethodSymbol>()
                        .Where(candidate =>
                            candidate
                                is {
                                    MethodKind: MethodKind.Ordinary,
                                    DeclaredAccessibility: Accessibility.Public,
                                }
                            && (
                                !candidate.IsStatic
                                || current.Equals(type, SymbolEqualityComparer.Default)
                            )
                        )
                )
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>
    /// Which Startup method <paramref name="name"/> is to <c>StartupLoader</c>: its family
    /// (<c>Services</c>, <c>Container</c>, or empty for <c>Configure</c>) and the environment
    /// it's for (empty for every environment). Null when it isn't one.
    /// </summary>
    private static (string Family, string Environment)? StartupRole(string name)
    {
        const string prefix = "Configure";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = name[prefix.Length..];
        foreach (var family in (string[])["Services", "Container"])
        {
            if (rest.EndsWith(family, StringComparison.OrdinalIgnoreCase))
            {
                return (family, rest[..^family.Length]);
            }
        }

        return ("", rest);
    }

    /// <summary>
    /// The calls to ASP.NET's <c>UseStartup</c>, by the type they hand over: a type argument,
    /// a <c>typeof</c>, or what a factory lambda makes with <c>new</c>. A call whose type
    /// --check can't read (a <c>Type</c> from elsewhere, or the assembly-name overload, which
    /// finds the type by name at run time) is unreadable.
    /// </summary>
    private (
        Dictionary<INamedTypeSymbol, List<SyntaxNode>> Readable,
        List<SyntaxNode> Unreadable
    ) Startups()
    {
        if (_startups is { } found)
        {
            return found;
        }

        var readable = new Dictionary<INamedTypeSymbol, List<SyntaxNode>>(
            SymbolEqualityComparer.Default
        );
        var unreadable = new List<SyntaxNode>();
        foreach (var (tree, semanticModel) in _origins.TreesMentioning("UseStartup"))
        {
            foreach (
                var node in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            )
            {
                if (
                    semanticModel.GetOperation(node) is not IInvocationOperation call
                    || call.TargetMethod.Name != "UseStartup"
                    || !IsType(call.TargetMethod.ContainingType, _wkt.WebHostBuilderExtensions)
                        && !IsType(
                            call.TargetMethod.ContainingType,
                            _wkt.HostingAbstractionsWebHostBuilderExtensions
                        )
                )
                {
                    continue;
                }

                if (StartupType(call) is not { } startup)
                {
                    unreadable.Add(node);
                    continue;
                }

                if (!readable.TryGetValue(startup.OriginalDefinition, out var sites))
                {
                    sites = [];
                    readable[startup.OriginalDefinition] = sites;
                }

                sites.Add(node);
            }
        }

        _startups = (readable, unreadable);
        return (readable, unreadable);

        // GenericWebHostBuilder runs the type a factory's instance has, not its type argument.
        static INamedTypeSymbol? StartupType(IInvocationOperation call)
        {
            if (
                call.Arguments.FirstOrDefault(argument =>
                    argument.Parameter?.Name == "startupFactory"
                ) is
                { } factory
            )
            {
                var made = Unwrap(factory.Value)
                    is IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda }
                    ? CoverageChecker
                        .ReturnedValues(lambda)
                        .Select(returned => Provenance(returned))
                        .Select(returned =>
                            returned is IObjectCreationOperation { Type: INamedTypeSymbol type }
                                ? type.OriginalDefinition
                                : null
                        )
                        .Distinct<INamedTypeSymbol?>(SymbolEqualityComparer.Default)
                        .ToList()
                    : [];
                return made is [{ } only] ? only : null;
            }

            return call.TargetMethod.TypeArguments is [INamedTypeSymbol typeArgument]
                ? typeArgument
                : call
                    .Arguments.Where(argument => argument.Parameter?.Name == "startupType")
                    .Select(argument => Provenance(argument.Value))
                    .OfType<ITypeOfOperation>()
                    .Select(typeOf => typeOf.TypeOperand)
                    .OfType<INamedTypeSymbol>()
                    .FirstOrDefault();
        }
    }

    // ---------------------------------------------------------------
    // Policies
    // ---------------------------------------------------------------

    /// <summary>
    /// What a policy argument requires: a policy name (a constant), looked up among the app's
    /// registrations; an <c>IAuthorizeData</c> (<c>new AuthorizeAttribute(...)</c>), read as
    /// <see cref="AuthorizeData"/> does; a policy built inline with
    /// <c>new AuthorizationPolicyBuilder()</c> or a configure lambda, which requires a user
    /// when it holds a user requirement that always runs, whatever else it holds, and admits
    /// anyone when its only requirement is <c>RequireAssertion(_ =&gt; true)</c>
    /// (<see cref="BuilderChain"/>, <see cref="ConfigureLambda"/>); any of them through a
    /// local with one known value. No policy
    /// names at all, and <c>options.DefaultPolicy</c>, are the default policy. Anything else
    /// is unknown.
    /// </summary>
    private Tri ReadPolicy(IOperation value, string unreadable)
    {
        var policy = Provenance(value);
        return policy switch
        {
            { ConstantValue: { HasValue: true, Value: string name } } => AuthorizeData(null, name),
            IArrayCreationOperation { Initializer: { } initializer } => Policies(
                initializer.ElementValues.Select(element => ReadPolicy(element, unreadable))
            ),
            IArrayCreationOperation => _defaultPolicy,
            ICollectionExpressionOperation collection => Policies(
                collection.Elements.Select(element => ReadPolicy(element, unreadable))
            ),
            IObjectCreationOperation { Type: var type } creation when IsAuthorizeData(type) =>
                CreatedPolicy(creation, unreadable),
            IPropertyReferenceOperation { Property: var property }
                when property.Name == "DefaultPolicy"
                    && IsType(property.ContainingType, _wkt.AuthorizationOptions) => _defaultPolicy,
            IInvocationOperation { TargetMethod.Name: "Build", Instance: { } builder }
                when IsType(policy.Type as INamedTypeSymbol, _wkt.AuthorizationPolicy) => Read(
                BuilderChain(builder, null)
            ),
            IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } => Read(
                ConfigureLambda(lambda)
            ),
            IAnonymousFunctionOperation lambda => Read(ConfigureLambda(lambda)),
            _ => Tri.Unknown(unreadable),
        };

        Tri Read(bool? requires) =>
            requires is { } known ? (known ? Tri.True : Tri.False) : Tri.Unknown(unreadable);
    }

    // A new IAuthorizeData's Roles and Policy, from AuthorizeAttribute's own constructor and
    // the object initializer, each a constant. Another type's constructor sets them where
    // --check doesn't look.
    private Tri CreatedPolicy(IObjectCreationOperation creation, string unreadable)
    {
        string? policy = null;
        string? roles = null;
        if (creation.Arguments is [var argument, ..])
        {
            if (
                !IsType(creation.Constructor?.ContainingType, _wkt.Authorize)
                || !Constant(argument.Value, out policy)
            )
            {
                return Tri.Unknown(unreadable);
            }
        }

        foreach (var initializer in creation.Initializer?.Initializers ?? [])
        {
            if (
                initializer
                is not ISimpleAssignmentOperation
                {
                    Target: IPropertyReferenceOperation
                    {
                        Property.Name: "Policy" or "Roles"
                    } target,
                    Value: var assigned,
                }
            )
            {
                continue;
            }

            if (!Constant(assigned, out var named))
            {
                return Tri.Unknown(unreadable);
            }

            if (target.Property.Name == "Policy")
            {
                policy = named;
            }
            else
            {
                roles = named;
            }
        }

        return AuthorizeData(roles, policy);

        static bool Constant(IOperation value, out string? constant)
        {
            var read = Provenance(value).ConstantValue;
            constant = read.Value as string;
            return read.HasValue && (read.Value is null or string);
        }
    }

    /// <summary>
    /// What a fluent chain of the policy builder's calls, down to a
    /// <c>new AuthorizationPolicyBuilder(...)</c> or the configure lambda's
    /// <paramref name="parameter"/>, requires. ASP.NET passes a caller only when every
    /// requirement does, so a chain holding a user requirement that refuses an anonymous
    /// caller (<c>RequireAuthenticatedUser</c>, <c>RequireRole</c>, <c>RequireClaim</c>, or
    /// <c>RequireUserName</c>) is true, whatever its other calls. Without one, it's false when
    /// every call is <c>RequireAssertion</c> with a lambda returning the constant <c>true</c>
    /// or <c>AddAuthenticationSchemes</c>, the assertion there at least once, a policy anyone
    /// passes; and null for anything else. A chain that doesn't reach its root is null.
    /// </summary>
    private bool? BuilderChain(IOperation operation, IParameterSymbol? parameter)
    {
        var denies = false;
        var admits = false;
        var unread = false;
        var current = Unwrap(operation);
        while (
            current is IInvocationOperation { Instance: { } instance } call
            && IsType(call.TargetMethod.ContainingType, _wkt.AuthorizationPolicyBuilder)
        )
        {
            if (_denyingPolicyCalls.Contains(call.TargetMethod.Name))
            {
                denies = true;
            }
            else if (call.TargetMethod.Name == "RequireAssertion" && AlwaysTrue(call))
            {
                admits = true;
            }
            else if (call.TargetMethod.Name != "AddAuthenticationSchemes")
            {
                unread = true;
            }

            current = Unwrap(instance);
        }

        var rooted = current switch
        {
            IObjectCreationOperation creation => IsType(
                creation.Type as INamedTypeSymbol,
                _wkt.AuthorizationPolicyBuilder
            )
                && creation.Arguments.All(argument =>
                    argument.Value.Type
                        is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String }
                    || argument.Value.Type?.SpecialType == SpecialType.System_String
                ),
            IParameterReferenceOperation reference => SymbolEqualityComparer.Default.Equals(
                reference.Parameter,
                parameter
            ),
            _ => false,
        };
        return !rooted ? null
            : denies ? true
            : admits && !unread ? false
            : null;

        // RequireAssertion(_ => true): a handler that passes every caller.
        static bool AlwaysTrue(IInvocationOperation call) =>
            call.Arguments is [var handler]
            && Unwrap(handler.Value)
                is IDelegateCreationOperation
                {
                    Target: IAnonymousFunctionOperation
                    {
                        Body.Operations: [
                            IReturnOperation
                            {
                                ReturnedValue.ConstantValue: { HasValue: true, Value: true },
                            },
                        ],
                    },
                };
    }

    /// <summary>
    /// What a configure lambda (<c>policy =&gt; ...</c>) builds. A user requirement counts only
    /// where it always runs: the lambda is true when a statement that runs unconditionally,
    /// before any <c>return</c> or <c>throw</c>, is a <see cref="BuilderChain"/> on its
    /// parameter that's true. Otherwise it's false when every statement is a chain that admits
    /// anyone, and null for anything else.
    /// </summary>
    private bool? ConfigureLambda(IAnonymousFunctionOperation lambda)
    {
        if (lambda.Symbol.Parameters is not [var parameter])
        {
            return null;
        }

        foreach (var statement in lambda.Body.Operations)
        {
            var (chain, last) = statement switch
            {
                IExpressionStatementOperation expression when !Throws(expression) => (
                    expression.Operation,
                    false
                ),
                IReturnOperation { ReturnedValue: { } returned } when !Throws(statement) => (
                    returned,
                    true
                ),
                _ => ((IOperation?)null, Throws(statement) || Returns(statement)),
            };
            if (chain is not null && BuilderChain(chain, parameter) == true)
            {
                return true;
            }

            if (last)
            {
                break;
            }
        }

        var chains = lambda
            .Body.Operations.Where(operation =>
                operation is not IReturnOperation { ReturnedValue: null }
            )
            .Select(statement =>
                statement switch
                {
                    IExpressionStatementOperation expressionStatement => BuilderChain(
                        expressionStatement.Operation,
                        parameter
                    ),
                    IReturnOperation { ReturnedValue: { } returned } => BuilderChain(
                        returned,
                        parameter
                    ),
                    _ => null,
                }
            )
            .Distinct()
            .ToList();
        return chains is [false] ? false : null;

        static bool Throws(IOperation statement) =>
            statement.DescendantsAndSelf().Any(operation => operation is IThrowOperation);

        static bool Returns(IOperation statement) =>
            statement.DescendantsAndSelf().Any(operation => operation is IReturnOperation);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Policies combined, as ASP.NET combines them into one that needs every requirement:
    /// one that requires a user decides it, otherwise an unknown leaves it unknown, and
    /// otherwise anyone passes. Two or more combined are never one condition's unknown, so
    /// they carry no key.
    /// </summary>
    private static Tri All(IEnumerable<Tri> values)
    {
        var list = values.ToList();
        if (list.Any(value => value.Value == true))
        {
            return Tri.True;
        }

        var unknowns = list.Where(value => value.Value is null).ToList();
        if (unknowns.Count == 0)
        {
            return Tri.False;
        }

        // The first unknown that says why, so the verdict can; any unknown stays unknown.
        var unknown = unknowns.FirstOrDefault(value => value.Why is not null, unknowns[0]);
        return list.Count > 1 ? unknown with { Key = null } : unknown;
    }

    // A requirement's policies; none at all is the default policy, as ASP.NET reads it.
    private Tri Policies(IEnumerable<Tri> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? _defaultPolicy : All(list);
    }

    private static IOperation Provenance(IOperation value)
    {
        var visited = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        var current = Unwrap(value);
        while (
            current is ILocalReferenceOperation local
            && visited.Add(local.Local)
            && CoverageChecker.ProvenanceValue(local) is { } provenance
        )
        {
            current = Unwrap(provenance);
        }

        return current;
    }

    private static IOperation Unwrap(IOperation operation) =>
        operation is IConversionOperation conversion ? Unwrap(conversion.Operand) : operation;

    private static bool IsType(INamedTypeSymbol? actual, INamedTypeSymbol? expected) =>
        expected is not null
        && SymbolEqualityComparer.Default.Equals(actual?.OriginalDefinition, expected);

    private enum FactKind
    {
        AllowAnonymous,
        Authorize,
        MvcFilter,
    }

    /// <summary>
    /// A piece of authorization setup: whether it's there, and whether it requires an
    /// authenticated user, each true, false or unknown.
    /// </summary>
    private sealed record Fact(FactKind Kind, Tri Present, Tri Requires, string Source);

    /// <summary>
    /// A registration that hands the framework <paramref name="Implementation"/> under
    /// <paramref name="Services"/>, made at <paramref name="Site"/>.
    /// </summary>
    private sealed record Registered(
        SyntaxNode Site,
        IReadOnlyList<ITypeSymbol> Services,
        ITypeSymbol Implementation
    );

    /// <summary>
    /// True, false, or unknown with the reason it can't be read, and, when it's one keyed
    /// condition's unknown (decision 2), its key.
    /// </summary>
    private readonly record struct Tri(
        bool? Value,
        string? Why,
        string? Source = null,
        ConditionKey? Key = null
    )
    {
        public static readonly Tri True = new(true, null);
        public static readonly Tri False = new(false, null);

        public static Tri Unknown(string? why, ConditionKey? key = null) =>
            new(null, why, Key: key);
    }
}

/// <summary>
/// Whether a handler requires an authenticated caller: true, false, or null when
/// <see cref="Because"/> lists what --check couldn't read. A decided verdict's
/// <see cref="Because"/> names the setup that decided it (empty when nothing requires
/// authentication).
/// </summary>
internal sealed record Verdict(bool? RequiresAuthentication, IReadOnlyList<string> Because);
