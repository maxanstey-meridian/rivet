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
/// <c>MapControllers()</c>; the fallback policy; and an Azure Functions trigger's
/// <c>AuthorizationLevel</c>. Custom authorization filters and middleware are invisible.
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
    private readonly Dictionary<IMethodSymbol, (List<IInvocationOperation>, bool)> _calls = new(
        SymbolEqualityComparer.Default
    );

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

    private HandlerAuthorization(Compilation compilation, WellKnownTypes wkt)
    {
        _compilation = compilation;
        _wkt = wkt;
        var fallbackSites = new List<(Tri Requires, string? Condition, string Source)>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            if (
                !text.Contains("RequireAuthorization", StringComparison.Ordinal)
                && !text.Contains("AllowAnonymous", StringComparison.Ordinal)
                && !text.Contains("AuthorizeFilter", StringComparison.Ordinal)
                && !text.Contains("FallbackPolicy", StringComparison.Ordinal)
            )
            {
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax
                        when semanticModel.GetOperation(node) is IInvocationOperation call:
                        ReadConvention(call);
                        ReadMvcFilter(call);
                        if (
                            call.TargetMethod.Name == "SetFallbackPolicy"
                            && IsType(call.TargetMethod.ContainingType, wkt.AuthorizationBuilder)
                            && call.Arguments is [var policy, ..]
                        )
                        {
                            fallbackSites.Add(FallbackSite(call.Syntax, policy.Value));
                        }
                        break;
                    case AssignmentExpressionSyntax
                        when semanticModel.GetOperation(node)
                            is ISimpleAssignmentOperation
                            {
                                Target: IPropertyReferenceOperation
                                {
                                    Property: { Name: "FallbackPolicy", ContainingType: var owner },
                                },
                            } assignment
                            && IsType(owner, wkt.AuthorizationOptions):
                        fallbackSites.Add(FallbackSite(assignment.Syntax, assignment.Value));
                        break;
                }
            }
        }

        _fallback = Fallback(fallbackSites);
    }

    /// <summary>The app's authorization setup, scanned once.</summary>
    public static HandlerAuthorization Scan(Compilation compilation, WellKnownTypes wkt) =>
        new(compilation, wkt);

    /// <summary>An attribute-routed MVC action.</summary>
    public Verdict ForMvc(IMethodSymbol action)
    {
        var facts = new List<Fact>();
        for (var method = action; method is not null; method = method.OverriddenMethod)
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
        if (root?.Type is { } rootType && DerivesFrom(rootType, _wkt.RouteGroupBuilder))
        {
            facts.Add(
                new Fact(
                    FactKind.Authorize,
                    Tri.Unknown(
                        $"it's mapped on the route group '{root.Syntax}' at {Where(root.Syntax)}, "
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
                .Where(argument => argument.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum })
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
            : facts
                .Where(fact => fact.Kind == FactKind.AllowAnonymous && fact.Present.Value == true)
                .Select(fact => fact.Source)
                .Distinct()
                .ToList();
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
            if (Implements(type, _wkt.IAllowAnonymous) || DerivesFrom(type, _wkt.AllowAnonymous))
            {
                yield return new Fact(FactKind.AllowAnonymous, Tri.True, Tri.True, source);
            }
            else if (Implements(type, _wkt.IAuthorizeData) || DerivesFrom(type, _wkt.Authorize))
            {
                yield return new Fact(FactKind.Authorize, Tri.True, Tri.True, source);
            }
        }
    }

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
            var source = $"{name} on {on} at {Where(call.Syntax)}";
            var condition = ConditionWhy(call.Syntax, origin?.Syntax) ?? callCondition;
            var present = condition is null
                ? Tri.True
                : Tri.Unknown($"{source} is applied only {condition}");
            var requires = allow
                ? Tri.True
                : All(
                    policies.Select(policy =>
                        ReadPolicy(
                            policy.Value,
                            $"{source} takes a policy --check can't read ('{policy.Syntax}')"
                        )
                    )
                );
            var fact = new Fact(
                allow ? FactKind.AllowAnonymous : FactKind.Authorize,
                present,
                requires,
                source
            );

            if (origin is null)
            {
                // It may reach any endpoint whose builder has this type.
                var builder = Unwrap(receiver.Value).Type;
                var mvc =
                    !DerivesFrom(builder, _wkt.RouteHandlerBuilder)
                    && !DerivesFrom(builder, _wkt.RouteGroupBuilder);
                var minimal = !DerivesFrom(builder, _wkt.ControllerActionEndpointConventionBuilder);
                _untraced.Add(
                    (
                        fact with
                        {
                            Present = Tri.Unknown(
                                $"{source} is applied to a builder --check can't trace to the Map* "
                                    + "call that made it, so it can't tell which endpoints it covers"
                            ),
                        },
                        mvc,
                        minimal
                    )
                );
            }
            else if (IsMapControllers(origin))
            {
                _mvcConventions.Add(fact);
            }
            else
            {
                var key = (origin.Syntax.SyntaxTree, origin.Syntax.Span);
                if (!_conventions.TryGetValue(key, out var facts))
                {
                    facts = [];
                    _conventions[key] = facts;
                }

                facts.Add(fact);
            }
        }
    }

    /// <summary>
    /// The calls that made the builder <paramref name="value"/> is: a Map*, MapGroup or
    /// MapControllers call, through locals with one known value, convention calls that hand
    /// back the builder they're given (<c>.WithName(...)</c>), helpers with source, through
    /// the values they return, and a helper's parameter, through each call that passes it
    /// (with what that call runs only under). A call to a method without source that makes a
    /// new builder is its own origin. A null origin is a builder that can't be traced: a
    /// field, a lambda's parameter, or a helper's parameter nothing passes.
    /// </summary>
    private IEnumerable<(IInvocationOperation? Origin, string? Condition)> Origins(
        IOperation value,
        HashSet<ISymbol> visited
    )
    {
        switch (Unwrap(value))
        {
            case ILocalReferenceOperation local
                when visited.Add(local.Local)
                    && CoverageChecker.ProvenanceValue(local) is { } provenance:
                return Origins(provenance, visited);
            case IInvocationOperation call when IsMapCall(call):
                return [(call, null)];
            case IInvocationOperation call when CoverageChecker.PassedBuilder(call) is { } passed:
                return Origins(passed, visited);
            case IInvocationOperation call
                when CoverageChecker.SourceBody(call.TargetMethod) is { } body:
                return visited.Add(call.TargetMethod.OriginalDefinition)
                    ? CoverageChecker
                        .ReturnedValues(body)
                        .SelectMany(returned => Origins(returned, visited))
                        .DefaultIfEmpty((null, null))
                        .ToList()
                    : [];
            case IInvocationOperation call:
                return [(call, null)];
            case IParameterReferenceOperation
            {
                Parameter: { ContainingSymbol: IMethodSymbol method } parameter,
            } when method.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction:
                if (!visited.Add(parameter.OriginalDefinition))
                {
                    return [];
                }

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
                            .SelectMany(argument => Origins(argument.Value, visited))
                            .Select(trace =>
                                (
                                    trace.Origin,
                                    trace.Condition
                                        ?? ConditionWhy(callSite.Syntax, trace.Origin?.Syntax)
                                )
                            )
                    )
                    .ToList();
                // A helper nothing calls applies its convention to nothing, unless it's also
                // handed on as a delegate, whose calls can't be followed.
                return escapes ? [.. traces, (null, null)] : traces;
            default:
                return [(null, null)];
        }
    }

    /// <summary>
    /// The calls to <paramref name="method"/> with source in the compilation, and whether it's
    /// also named anywhere it isn't called (a method group handed on as a delegate).
    /// </summary>
    private (List<IInvocationOperation> Calls, bool Escapes) CallsTo(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (_calls.TryGetValue(definition, out var cached))
        {
            return cached;
        }

        var calls = new List<IInvocationOperation>();
        var escapes = false;
        foreach (var tree in _compilation.SyntaxTrees)
        {
            if (!tree.GetText().ToString().Contains(method.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var semanticModel = _compilation.GetSemanticModel(tree);
            foreach (
                var name in tree.GetRoot()
                    .DescendantNodes()
                    .OfType<SimpleNameSyntax>()
                    .Where(name => name.Identifier.ValueText == method.Name)
            )
            {
                var named =
                    name.Parent is MemberAccessExpressionSyntax access && access.Name == name
                        ? access
                        : (ExpressionSyntax)name;
                if (
                    named.Parent is InvocationExpressionSyntax invocation
                    && invocation.Expression == named
                )
                {
                    if (
                        semanticModel.GetOperation(invocation) is IInvocationOperation call
                        && SymbolEqualityComparer.Default.Equals(
                            (call.TargetMethod.ReducedFrom ?? call.TargetMethod).OriginalDefinition,
                            definition
                        )
                    )
                    {
                        calls.Add(call);
                    }
                }
                else if (
                    semanticModel.GetSymbolInfo(name).Symbol is IMethodSymbol referenced
                    && SymbolEqualityComparer.Default.Equals(
                        (referenced.ReducedFrom ?? referenced).OriginalDefinition,
                        definition
                    )
                    && name.Parent is not MethodDeclarationSyntax
                )
                {
                    escapes = true;
                }
            }
        }

        _calls[definition] = (calls, escapes);
        return (calls, escapes);
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
            || !DerivesFrom(collection, _wkt.FilterCollection)
        )
        {
            return;
        }

        var source = $"the global AuthorizeFilter added at {Where(call.Syntax)}";
        Tri requires;
        if (call.TargetMethod.TypeArguments is [var typeArgument])
        {
            if (!DerivesFrom(typeArgument, _wkt.AuthorizeFilter))
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
            if (!DerivesFrom(typeOf.TypeOperand, _wkt.AuthorizeFilter))
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
            if (!DerivesFrom(filter.Type, _wkt.AuthorizeFilter))
            {
                return;
            }

            requires =
                filter is IObjectCreationOperation creation
                && IsType(creation.Type as INamedTypeSymbol, _wkt.AuthorizeFilter)
                    ? All(
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

        var condition = ConditionWhy(call.Syntax, null);
        _mvcFilters.Add(
            new Fact(
                FactKind.MvcFilter,
                condition is null ? Tri.True : Tri.Unknown($"{source} is added only {condition}"),
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
    // The fallback policy
    // ---------------------------------------------------------------

    private (Tri Requires, string? Condition, string Source) FallbackSite(
        SyntaxNode site,
        IOperation value
    )
    {
        var source = $"the fallback policy set at {Where(site)}";
        var requires = Provenance(value).ConstantValue is { HasValue: true, Value: null }
            ? Tri.False
            : ReadPolicy(value, $"{source} is a policy --check can't read ('{value.Syntax}')");
        return (requires, ConditionWhy(site, null), source);
    }

    /// <summary>
    /// The fallback policy's verdict over every site that sets it. Sets that all agree, at
    /// least one of them unconditional, decide it; anything else is unknown, since which set
    /// runs last can't be read.
    /// </summary>
    private static Tri Fallback(
        IReadOnlyList<(Tri Requires, string? Condition, string Source)> sites
    )
    {
        if (sites.Count == 0)
        {
            return Tri.False;
        }

        var values = sites.Select(site => site.Requires.Value).Distinct().ToList();
        if (values is [{ } value] && sites.Any(site => site.Condition is null))
        {
            return new Tri(
                value,
                null,
                string.Join(
                    ", ",
                    sites.Where(site => site.Condition is null).Select(site => site.Source)
                )
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
    // Policies
    // ---------------------------------------------------------------

    /// <summary>
    /// Whether a policy argument requires an authenticated user: a policy name (a constant),
    /// an <c>IAuthorizeData</c> (<c>new AuthorizeAttribute(...)</c>), a policy built inline with
    /// <c>new AuthorizationPolicyBuilder()</c> and the builder's user requirements, or a
    /// configure lambda calling them, any of them through a local with one known value. Anything
    /// else, a requirement it can't read included (<c>RequireAssertion</c>), is unknown.
    /// </summary>
    private Tri ReadPolicy(IOperation value, string unreadable)
    {
        var policy = Provenance(value);
        return policy switch
        {
            { ConstantValue: { HasValue: true, Value: string } } => Tri.True,
            IArrayCreationOperation { Initializer: { } initializer } => All(
                initializer.ElementValues.Select(element => ReadPolicy(element, unreadable))
            ),
            IArrayCreationOperation => Tri.True,
            ICollectionExpressionOperation collection => All(
                collection.Elements.Select(element => ReadPolicy(element, unreadable))
            ),
            IObjectCreationOperation { Type: var type }
                when Implements(type, _wkt.IAuthorizeData) || DerivesFrom(type, _wkt.Authorize) =>
                Tri.True,
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
    /// enclosing block. Null when it always runs.
    /// </summary>
    internal static string? ConditionWhy(SyntaxNode site, SyntaxNode? origin)
    {
        if (Contains(site, origin))
        {
            return null;
        }

        var child = site;
        foreach (var ancestor in site.Ancestors())
        {
            // Past the origin's own statement, the site runs whenever its endpoint is mapped,
            // unless a return between the two can leave first.
            if (Contains(ancestor, origin))
            {
                return ancestor is BlockSyntax shared ? EarlyReturn(shared, child, origin) : null;
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

        static bool Contains(SyntaxNode node, SyntaxNode? origin) =>
            origin is not null
            && node.SyntaxTree == origin.SyntaxTree
            && node.Span.Contains(origin.Span);
    }

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
                || statement.SpanStart > origin.Span.End
            )
            .SelectMany(statement =>
                statement.DescendantNodesAndSelf(node =>
                    node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                )
            )
            .OfType<ReturnStatementSyntax>()
            .Select(returned => $"after the return at {Where(returned)}, which may leave first")
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

    internal static string Where(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})";
    }

    private static bool IsType(INamedTypeSymbol? actual, INamedTypeSymbol? expected) =>
        expected is not null
        && SymbolEqualityComparer.Default.Equals(actual?.OriginalDefinition, expected);

    private static bool DerivesFrom(ITypeSymbol? type, INamedTypeSymbol? target)
    {
        if (target is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Implements(ITypeSymbol? type, INamedTypeSymbol? target) =>
        target is not null
        && type is not null
        && type.AllInterfaces.Any(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate, target)
        );

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
