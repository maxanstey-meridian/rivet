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
/// <c>MapControllers()</c>; the fallback policy, and the default policy a requirement naming
/// no policy of its own means; and an Azure Functions trigger's <c>AuthorizationLevel</c>.
/// App-wide setup in a helper applies where the helper is called. Custom authorization
/// filters and middleware are invisible.
/// <para>
/// Each piece of setup is a fact: present or not, and requiring an authenticated user or not.
/// A fact applied under a condition, or built from a policy that can't be read, is unknown.
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
    private readonly Dictionary<
        IMethodSymbol,
        (List<IInvocationOperation>, List<ExpressionSyntax>)
    > _calls = new(SymbolEqualityComparer.Default);

    // RequireAuthorization/AllowAnonymous conventions by the Map*/MapGroup/MapControllers call
    // they're applied to.
    private readonly Dictionary<(SyntaxTree Tree, TextSpan Span), List<Fact>> _conventions = [];

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

    private HandlerAuthorization(Compilation compilation, WellKnownTypes wkt)
    {
        _compilation = compilation;
        _wkt = wkt;
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

        foreach (
            var (tree, semanticModel) in TreesMentioning(
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
    public static HandlerAuthorization Scan(Compilation compilation, WellKnownTypes wkt) =>
        new(compilation, wkt);

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
    /// A minimal-API handler mapped by <paramref name="mapCall"/>, inside
    /// <paramref name="groups"/> (the MapGroup calls its receiver chain resolves to), with the
    /// chain's <paramref name="root"/>.
    /// </summary>
    public Verdict ForMinimalApi(
        IMethodSymbol handler,
        IInvocationOperation mapCall,
        IReadOnlyList<IInvocationOperation> groups,
        IOperation? root
    )
    {
        var facts = AttributeFacts(handler, "the handler").ToList();
        facts.AddRange(ConventionsOn(mapCall));
        foreach (var group in groups)
        {
            facts.AddRange(ConventionsOn(group));
        }

        // A route group the chain doesn't reach a MapGroup call for (a parameter, or a helper's
        // answer) has conventions --check can't see.
        if (root?.Type is { } rootType && rootType.DerivesFrom(_wkt.RouteGroupBuilder))
        {
            facts.Add(
                new Fact(
                    FactKind.Authorize,
                    Tri.Unknown(
                        $"it's mapped on the route group '{root.Syntax}' at {root.Syntax.SourcePosition()}, "
                            + "which --check can't trace to its MapGroup call, so it can't see that "
                            + "group's RequireAuthorization or AllowAnonymous"
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
        (bool Value, int Variable) Compile(Tri tri)
        {
            if (tri.Value is { } known)
            {
                return (known, -1);
            }

            if (tri.Why is { } why && !reasons.Contains(why))
            {
                reasons.Add(why);
            }

            return (false, variables++);
        }

        bool Requires(long bits)
        {
            bool Value((bool Value, int Variable) fact) =>
                fact.Variable < 0 ? fact.Value : ((bits >> fact.Variable) & 1) == 1;

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
                var ownPolicy =
                    attribute.ConstructorArguments.Any(argument => IsNamed(argument.Value))
                    || attribute.NamedArguments.Any(named =>
                        named.Key is "Policy" or "Roles" && IsNamed(named.Value.Value)
                    );
                yield return new Fact(
                    FactKind.Authorize,
                    Tri.True,
                    ownPolicy ? Tri.True : _defaultPolicy,
                    source
                );
            }
        }
    }

    private bool IsAuthorizeData(ITypeSymbol? type) =>
        type.Implements(_wkt.IAuthorizeData) || type.DerivesFrom(_wkt.Authorize);

    // A policy name or a role list: ASP.NET reads an empty or blank one as not given.
    private static bool IsNamed(object? value) =>
        value is string name && !string.IsNullOrWhiteSpace(name);

    // ---------------------------------------------------------------
    // Conventions: RequireAuthorization and AllowAnonymous
    // ---------------------------------------------------------------

    private IEnumerable<Fact> ConventionsOn(IInvocationOperation call) =>
        _conventions.GetValueOrDefault((call.Syntax.SyntaxTree, call.Syntax.Span)) ?? [];

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
        var traces = Origins(receiver.Value, new HashSet<ISymbol>(SymbolEqualityComparer.Default))
            .Distinct()
            .ToList();
        foreach (var (origin, callCondition) in traces)
        {
            var on = origin switch
            {
                null => "a builder",
                _ when IsMapControllers(origin) => "MapControllers()",
                _ when IsMapGroup(origin) => "its route group",
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
            var facts = Conditions(call.Syntax, receiver.Value, origin, callCondition)
                .Distinct()
                .Select(condition => new Fact(
                    kind,
                    condition is null
                        ? Tri.True
                        : Tri.Unknown($"{source} is applied only {condition}"),
                    requires,
                    source
                ));
            if (IsMapControllers(origin))
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

                onOrigin.AddRange(facts);
            }
        }
    }

    /// <summary>
    /// What a convention at <paramref name="site"/>, on a builder <paramref name="origin"/>
    /// made, runs only under: one entry for each way it comes to run, null when that way always
    /// does, and none when nothing runs it. Inside the function that holds the site, that's
    /// <see cref="ConditionWhy"/>. A local function that captures the builder runs where it's
    /// called, so each call's own condition, relative to the origin, is one way.
    /// <paramref name="traced"/> is what the trace to the origin ran under (a helper's
    /// parameter, whose call sites <see cref="Origins"/> has already composed).
    /// </summary>
    private List<string?> Conditions(
        SyntaxNode site,
        IOperation receiver,
        IInvocationOperation origin,
        string? traced
    )
    {
        if (ConditionWhy(site, origin.Syntax) is { } within)
        {
            return [within];
        }

        if (
            EnclosingFunction(site) is not LocalFunctionStatementSyntax function
            || Contains(function, origin.Syntax)
            || _compilation.GetSemanticModel(function.SyntaxTree).GetDeclaredSymbol(function)
                is not IMethodSymbol local
            || !Captures(receiver, local)
        )
        {
            return [traced];
        }

        return
        [
            .. CapturedCalls(
                    local,
                    origin.Syntax,
                    new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default)
                )
                .Select(condition => condition ?? traced),
        ];
    }

    /// <summary>
    /// What each call to the local function <paramref name="function"/> runs only under,
    /// relative to <paramref name="origin"/>, through the local functions that call it in
    /// turn. A call from inside its own call graph adds nothing; a delegate made from it
    /// can't be followed.
    /// </summary>
    private List<string?> CapturedCalls(
        IMethodSymbol function,
        SyntaxNode origin,
        HashSet<IMethodSymbol> visiting
    )
    {
        var conditions = new List<string?>();
        if (!visiting.Add(function))
        {
            return conditions;
        }

        var (calls, escapes) = CallsTo(function);
        foreach (var call in calls)
        {
            if (ConditionWhy(call.Syntax, origin) is { } condition)
            {
                conditions.Add(condition);
            }
            else if (
                EnclosingFunction(call.Syntax) is LocalFunctionStatementSyntax outer
                && !Contains(outer, origin)
                && _compilation.GetSemanticModel(outer.SyntaxTree).GetDeclaredSymbol(outer)
                    is IMethodSymbol caller
            )
            {
                conditions.AddRange(CapturedCalls(caller, origin, visiting));
            }
            else
            {
                conditions.Add(null);
            }
        }

        conditions.AddRange(
            escapes.Select(reference =>
                $"where {function.Name}() runs, which --check can't follow: it's handed on as "
                + $"a delegate at {reference.SourcePosition()}"
            )
        );
        return conditions;
    }

    /// <summary>
    /// <paramref name="receiver"/> reaches <paramref name="function"/> from outside it: through
    /// its own locals, and convention calls that hand the builder back, to a local or a
    /// parameter some enclosing function declares.
    /// </summary>
    private static bool Captures(IOperation receiver, IMethodSymbol function)
    {
        var seen = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        var current = Unwrap(receiver);
        while (true)
        {
            switch (current)
            {
                case ILocalReferenceOperation local when seen.Add(local.Local):
                    if (!Within(local.Local))
                    {
                        return true;
                    }

                    if (CoverageChecker.ProvenanceValue(local) is not { } provenance)
                    {
                        return false;
                    }

                    current = Unwrap(provenance);
                    break;
                case IParameterReferenceOperation parameter:
                    return !Within(parameter.Parameter);
                case IInvocationOperation call
                    when CoverageChecker.PassedBuilder(call) is { } passed:
                    current = Unwrap(passed);
                    break;
                default:
                    return false;
            }
        }

        bool Within(ISymbol symbol)
        {
            for (var container = symbol.ContainingSymbol; container is not null; )
            {
                if (SymbolEqualityComparer.Default.Equals(container, function))
                {
                    return true;
                }

                container = container.ContainingSymbol;
            }

            return false;
        }
    }

    /// <summary>
    /// The calls that made the builder <paramref name="value"/> is: a Map*, MapGroup or
    /// MapControllers call, through locals with one known value, convention calls that hand
    /// back the builder they're given (<c>.WithName(...)</c>), helpers with source, through
    /// the values they return, and a helper's parameter, through each call that passes it
    /// (with what that call runs only under, read <see cref="Conditions"/>' way). A call to a
    /// method without source that makes a new builder is its own origin. A null origin is a builder that can't be traced: a
    /// field, a lambda's parameter, or a helper's parameter nothing passes.
    /// <paramref name="path"/> holds the locals, helpers and parameters this trace is inside,
    /// so a cycle ends; another path reaching one of them traces it again.
    /// </summary>
    private List<(IInvocationOperation? Origin, string? Condition)> Origins(
        IOperation value,
        HashSet<ISymbol> path
    )
    {
        switch (Unwrap(value))
        {
            case ILocalReferenceOperation local:
                return Along(
                    local.Local,
                    () =>
                        CoverageChecker.ProvenanceValue(local) is { } provenance
                            ? Origins(provenance, path)
                            : [(null, null)]
                );
            case IInvocationOperation call when IsMapCall(call):
                return [(call, null)];
            case IInvocationOperation call when CoverageChecker.PassedBuilder(call) is { } passed:
                return Origins(passed, path);
            case IInvocationOperation call when call.TargetMethod.SourceBody() is { } body:
                return Along(
                    call.TargetMethod.OriginalDefinition,
                    () =>
                        CoverageChecker
                            .ReturnedValues(body)
                            .SelectMany(returned => Origins(returned, path))
                            .DefaultIfEmpty((null, null))
                            .ToList()
                );
            case IInvocationOperation call:
                return [(call, null)];
            case IParameterReferenceOperation
            {
                Parameter: { ContainingSymbol: IMethodSymbol method } parameter,
            } when method.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction:
                return Along(
                    parameter.OriginalDefinition,
                    () =>
                    {
                        var (calls, escapes) = CallsTo(method);
                        var traces = calls
                            .SelectMany(callSite =>
                                callSite
                                    .Arguments.Where(argument =>
                                        SymbolEqualityComparer.Default.Equals(
                                            argument.Parameter?.OriginalDefinition,
                                            parameter.OriginalDefinition
                                        )
                                    )
                                    .SelectMany(argument =>
                                        Origins(argument.Value, path)
                                            .SelectMany(trace =>
                                                trace.Origin is null
                                                    ? [trace]
                                                    : Conditions(
                                                            callSite.Syntax,
                                                            argument.Value,
                                                            trace.Origin,
                                                            null
                                                        )
                                                        .Select(condition =>
                                                            (
                                                                Origin: (IInvocationOperation?)
                                                                    trace.Origin,
                                                                Condition: trace.Condition
                                                                    ?? condition
                                                            )
                                                        )
                                            )
                                    )
                            )
                            .ToList();
                        // A helper nothing calls applies its convention to nothing, unless it's
                        // also handed on as a delegate, whose calls can't be followed.
                        return escapes.Count > 0 ? [.. traces, (null, null)] : traces;
                    }
                );
            default:
                return [(null, null)];
        }

        // A cycle back into the trace adds nothing: what it leads to is already in the union.
        List<(IInvocationOperation?, string?)> Along(
            ISymbol step,
            Func<List<(IInvocationOperation?, string?)>> trace
        )
        {
            if (!path.Add(step))
            {
                return [];
            }

            try
            {
                return trace();
            }
            finally
            {
                path.Remove(step);
            }
        }
    }

    /// <summary>
    /// The calls to <paramref name="method"/> with source in the compilation, and every other
    /// place it's named (a method group handed on as a delegate).
    /// </summary>
    private (List<IInvocationOperation> Calls, List<ExpressionSyntax> Escapes) CallsTo(
        IMethodSymbol method
    )
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (_calls.TryGetValue(definition, out var cached))
        {
            return cached;
        }

        var calls = new List<IInvocationOperation>();
        var escapes = new List<ExpressionSyntax>();
        foreach (var (name, semanticModel) in Mentions(definition))
        {
            var named = name.Parent switch
            {
                MemberAccessExpressionSyntax access when access.Name == name => access,
                MemberBindingExpressionSyntax binding when binding.Name == name => binding,
                _ => (ExpressionSyntax)name,
            };
            if (
                named.Parent is InvocationExpressionSyntax invocation
                && invocation.Expression == named
            )
            {
                if (semanticModel.GetOperation(invocation) is IInvocationOperation call)
                {
                    calls.Add(call);
                }
            }
            else if (name.Parent is not MethodDeclarationSyntax)
            {
                escapes.Add(named);
            }
        }

        _calls[definition] = (calls, escapes);
        return (calls, escapes);
    }

    /// <summary>The syntax trees whose text holds any of <paramref name="texts"/>, with their semantic models.</summary>
    private IEnumerable<(SyntaxTree Tree, SemanticModel SemanticModel)> TreesMentioning(
        params string[] texts
    )
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            if (texts.Any(wanted => text.Contains(wanted, StringComparison.Ordinal)))
            {
                yield return (tree, _compilation.GetSemanticModel(tree));
            }
        }
    }

    /// <summary>
    /// Every name in the compilation bound to <paramref name="definition"/> (a method's
    /// reduced extension form and any construction of a generic counting as it).
    /// </summary>
    private IEnumerable<(SimpleNameSyntax Name, SemanticModel SemanticModel)> Mentions(
        ISymbol definition
    )
    {
        foreach (var (tree, semanticModel) in TreesMentioning(definition.Name))
        {
            foreach (
                var name in tree.GetRoot()
                    .DescendantNodes()
                    .OfType<SimpleNameSyntax>()
                    .Where(name => name.Identifier.ValueText == definition.Name)
            )
            {
                var bound = semanticModel.GetSymbolInfo(name).Symbol;
                if (
                    SymbolEqualityComparer.Default.Equals(
                        bound is IMethodSymbol method
                            ? (method.ReducedFrom ?? method).OriginalDefinition
                            : bound?.OriginalDefinition,
                        definition
                    )
                )
                {
                    yield return (name, semanticModel);
                }
            }
        }
    }

    private bool IsMapCall(IInvocationOperation call) =>
        IsType(call.TargetMethod.ContainingType, _wkt.EndpointRouteBuilderExtensions)
        || IsMapControllers(call);

    private bool IsMapGroup(IInvocationOperation call) =>
        call.TargetMethod.Name == "MapGroup"
        && IsType(call.TargetMethod.ContainingType, _wkt.EndpointRouteBuilderExtensions);

    private bool IsMapControllers(IInvocationOperation call) =>
        call.TargetMethod.Name == "MapControllers"
        && IsType(call.TargetMethod.ContainingType, _wkt.ControllerEndpointRouteBuilderExtensions);

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
                    ? Tri.Unknown($"{source} is added only {present.Why}")
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
    private List<(Tri Requires, string? Condition, string Source)> PolicySites(
        string property,
        string name
    )
    {
        var sites = new List<(Tri, string?, string)>();
        foreach (var (tree, semanticModel) in TreesMentioning(property))
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
                if (value is null)
                {
                    continue;
                }

                var present = Presence(node);
                if (present.Value == false)
                {
                    continue;
                }

                var source = $"{name} set at {node.SourcePosition()}";
                var requires = Provenance(value).ConstantValue is { HasValue: true, Value: null }
                    ? Tri.False
                    : ReadPolicy(
                        value,
                        $"{source} is a policy --check can't read ('{value.Syntax}')"
                    );
                sites.Add((requires, present.Why, source));
            }
        }

        return sites;

        static IOperation? PolicyArgument(IInvocationOperation call, int ordinal) =>
            call
                .Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == ordinal)
                ?.Value;
    }

    /// <summary>
    /// A policy's verdict over every site that sets it, <paramref name="unset"/> when none
    /// does. When every site is conditional, none may run, so <paramref name="unset"/> is one
    /// more value it may take. Values that all agree decide it; anything else is unknown, since
    /// which set runs last can't be read.
    /// </summary>
    private static Tri Settled(
        IReadOnlyList<(Tri Requires, string? Condition, string Source)> sites,
        Tri unset
    )
    {
        if (sites.Count == 0)
        {
            return unset;
        }

        var always = sites.Where(site => site.Condition is null).ToList();
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
                    : site.Condition is not null ? $"{site.Source} only {site.Condition}"
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
        if (ConditionWhy(site, null) is { } condition)
        {
            return Tri.Unknown(condition);
        }

        var function = EnclosingFunction(site);
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
            var ways = WaysIn(definition, visiting);
            return ways.Any(way => way.Value == true) ? Tri.True
                : ways.FirstOrDefault(way => way.Value is null) is { Why: not null } unknown
                    ? unknown
                : Tri.False;
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

            var (calls, escapes) = CallsTo(target);
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
            foreach (var (name, semanticModel) in Mentions(candidate.OriginalDefinition))
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
        foreach (var (tree, semanticModel) in TreesMentioning("UseStartup"))
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
    /// Whether a policy argument requires an authenticated user: a policy name (a constant),
    /// an <c>IAuthorizeData</c> (<c>new AuthorizeAttribute(...)</c>), a policy built inline with
    /// <c>new AuthorizationPolicyBuilder()</c> and the builder's user requirements, or a
    /// configure lambda calling them, any of them through a local with one known value. An
    /// <c>IAuthorizeData</c> with no policy or role of its own, no policy names at all, and
    /// <c>options.DefaultPolicy</c> are the default policy. Anything else, a requirement it
    /// can't read included (<c>RequireAssertion</c>), is unknown.
    /// </summary>
    private Tri ReadPolicy(IOperation value, string unreadable)
    {
        var policy = Provenance(value);
        return policy switch
        {
            { ConstantValue: { HasValue: true, Value: string name } } => IsNamed(name)
                ? Tri.True
                : _defaultPolicy,
            IArrayCreationOperation { Initializer: { } initializer } => Policies(
                initializer.ElementValues.Select(element => ReadPolicy(element, unreadable))
            ),
            IArrayCreationOperation => _defaultPolicy,
            ICollectionExpressionOperation collection => Policies(
                collection.Elements.Select(element => ReadPolicy(element, unreadable))
            ),
            IObjectCreationOperation { Type: var type } creation when IsAuthorizeData(type) =>
                creation.Arguments.Any(argument => IsNamed(argument.Value.ConstantValue.Value))
                || creation.Initializer?.Initializers.Any(initializer =>
                    initializer
                        is ISimpleAssignmentOperation
                        {
                            Target: IPropertyReferenceOperation
                            {
                                Property.Name: "Policy" or "Roles",
                            },
                            Value.ConstantValue.Value: var named,
                        }
                    && IsNamed(named)
                ) == true
                    ? Tri.True
                    : _defaultPolicy,
            IPropertyReferenceOperation { Property: var property }
                when property.Name == "DefaultPolicy"
                    && IsType(property.ContainingType, _wkt.AuthorizationOptions) => _defaultPolicy,
            IInvocationOperation { TargetMethod.Name: "Build", Instance: { } builder }
                when IsType(policy.Type as INamedTypeSymbol, _wkt.AuthorizationPolicy) =>
                BuilderChain(builder, null) ? Tri.True : Tri.Unknown(unreadable),
            IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } =>
                ConfigureLambda(lambda) ? Tri.True : Tri.Unknown(unreadable),
            IAnonymousFunctionOperation lambda => ConfigureLambda(lambda)
                ? Tri.True
                : Tri.Unknown(unreadable),
            _ => Tri.Unknown(unreadable),
        };
    }

    /// <summary>
    /// True when <paramref name="operation"/> is a fluent chain of the policy builder's user
    /// requirements, with at least one that refuses an anonymous caller, down to a
    /// <c>new AuthorizationPolicyBuilder(...)</c> or the configure lambda's parameter.
    /// </summary>
    private bool BuilderChain(IOperation operation, IParameterSymbol? parameter)
    {
        var denies = false;
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
            else if (call.TargetMethod.Name != "AddAuthenticationSchemes")
            {
                return false;
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
        return rooted && denies;
    }

    /// <summary>
    /// A <c>RequireAuthorization(policy =&gt; ...)</c> lambda whose every statement is a chain
    /// of the builder's user requirements on its parameter, at least one refusing anonymous
    /// callers.
    /// </summary>
    private bool ConfigureLambda(IAnonymousFunctionOperation lambda)
    {
        if (lambda.Symbol.Parameters is not [var parameter])
        {
            return false;
        }

        var statements = lambda
            .Body.Operations.Where(operation =>
                operation is not IReturnOperation { ReturnedValue: null }
            )
            .ToList();
        var denies = false;
        foreach (var statement in statements)
        {
            var expression = statement switch
            {
                IExpressionStatementOperation expressionStatement => expressionStatement.Operation,
                IReturnOperation { ReturnedValue: { } returned } => returned,
                _ => null,
            };
            if (expression is null || !BuilderChain(expression, parameter))
            {
                return false;
            }

            denies = true;
        }

        return denies;
    }

    // ---------------------------------------------------------------
    // Conditions
    // ---------------------------------------------------------------

    /// <summary>
    /// What <paramref name="site"/> runs only under, relative to <paramref name="origin"/> (the
    /// call that made the builder it's applied to, so a requirement in the same branch as its
    /// endpoint isn't conditional): an <c>if</c> or <c>else</c>, a <c>?:</c> branch, a switch
    /// section or arm, a <c>catch</c>, a loop body, the right of <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>??</c> or <c>??=</c>, a <c>?.</c>, or a <c>return</c> earlier in an
    /// enclosing block. The walk stops at the method or local function holding the site, unless
    /// that holds the origin too: a function runs where it's called, so its callers' conditions
    /// are added there (<see cref="Presence(SyntaxNode)"/> for app-wide setup, and
    /// <see cref="Origins"/> and <see cref="Conditions"/> for a convention), and a
    /// <c>return</c> above a local function's declaration never stops it. Null when it always
    /// runs.
    /// </summary>
    private static string? ConditionWhy(SyntaxNode site, SyntaxNode? origin)
    {
        if (Contains(site, origin))
        {
            return null;
        }

        var child = site;
        var function = EnclosingFunction(site);
        foreach (var ancestor in site.Ancestors())
        {
            // Past the origin's own statement, the site runs whenever its endpoint is mapped,
            // unless a return between the two can leave first.
            if (Contains(ancestor, origin))
            {
                return ancestor is BlockSyntax shared ? EarlyReturn(shared, child, origin) : null;
            }

            if (ancestor == function)
            {
                return null;
            }

            var why = ancestor switch
            {
                BlockSyntax block => EarlyReturn(block, child, origin),
                IfStatementSyntax statement when child != statement.Condition =>
                    $"under if ({statement.Condition})",
                ConditionalExpressionSyntax conditional when child != conditional.Condition =>
                    $"under a branch of ({conditional.Condition}) ? ... : ...",
                SwitchSectionSyntax or SwitchExpressionArmSyntax => "under a switch",
                CatchClauseSyntax => "in a catch block",
                ForStatementSyntax loop when child == loop.Statement => "in a for loop",
                CommonForEachStatementSyntax loop when child == loop.Statement =>
                    "in a foreach loop",
                WhileStatementSyntax loop when child == loop.Statement => "in a while loop",
                ConditionalAccessExpressionSyntax access when child == access.WhenNotNull =>
                    $"behind {access.Expression}?.",
                BinaryExpressionSyntax binary
                    when child == binary.Right
                        && binary.Kind()
                            is SyntaxKind.LogicalAndExpression
                                or SyntaxKind.LogicalOrExpression
                                or SyntaxKind.CoalesceExpression =>
                    $"on the right of {binary.OperatorToken}",
                AssignmentExpressionSyntax assignment
                    when child == assignment.Right
                        && assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression) =>
                    "on the right of ??=",
                _ => null,
            };
            if (why is not null)
            {
                return why;
            }

            child = ancestor;
        }

        return null;
    }

    // node holds origin.
    private static bool Contains(SyntaxNode node, SyntaxNode? origin) =>
        origin is not null
        && node.SyntaxTree == origin.SyntaxTree
        && node.Span.Contains(origin.Span);

    // The method, local function, or accessor holding site, past any lambda: null at the top
    // level and in an initializer.
    private static SyntaxNode? EnclosingFunction(SyntaxNode site) =>
        site.Ancestors()
            .FirstOrDefault(node =>
                node
                    is BaseMethodDeclarationSyntax
                        or LocalFunctionStatementSyntax
                        or AccessorDeclarationSyntax
            );

    /// <summary>
    /// A <c>return</c> in a statement before <paramref name="child"/> in
    /// <paramref name="block"/> (after <paramref name="origin"/>, when that's in the block too),
    /// outside nested functions: it may leave before the site runs.
    /// </summary>
    private static string? EarlyReturn(BlockSyntax block, SyntaxNode child, SyntaxNode? origin) =>
        block
            .Statements.TakeWhile(statement => statement != child)
            .Where(statement =>
                origin is null
                || origin.SyntaxTree != block.SyntaxTree
                || !block.Span.Contains(origin.Span)
                || statement.SpanStart > origin.Span.End
            )
            .SelectMany(statement =>
                statement.DescendantNodesAndSelf(node =>
                    node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                )
            )
            .OfType<ReturnStatementSyntax>()
            .Select(returned =>
                $"after the return at {returned.SourcePosition()}, which may leave first"
            )
            .FirstOrDefault();

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static Tri All(IEnumerable<Tri> values)
    {
        var list = values.ToList();
        return list.FirstOrDefault(value => value.Value is null) is { Why: not null } unknown
            ? unknown
            : Tri.True;
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

    /// <summary>True, false, or unknown with the reason it can't be read.</summary>
    private readonly record struct Tri(bool? Value, string? Why, string? Source = null)
    {
        public static readonly Tri True = new(true, null);
        public static readonly Tri False = new(false, null);

        public static Tri Unknown(string? why) => new(null, why);
    }
}

/// <summary>
/// Whether a handler requires an authenticated caller: true, false, or null when
/// <see cref="Because"/> lists what --check couldn't read. A decided verdict's
/// <see cref="Because"/> names the setup that decided it (empty when nothing requires
/// authentication).
/// </summary>
internal sealed record Verdict(bool? RequiresAuthentication, IReadOnlyList<string> Because);
