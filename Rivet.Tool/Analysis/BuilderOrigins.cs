using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Rivet.Tool.Analysis;

/// <summary>
/// The one way --check follows a minimal-API builder, for its routes and its authorization
/// alike: from a builder value back to the calls that made it. It follows locals with one
/// known value; a convention without source that hands back the builder it's given
/// (<see cref="PassedBuilder"/>); a helper with source, through the values it returns, with
/// its parameters bound to that call's arguments; and a helper's parameter, when the trace
/// starts inside the helper, through each call that passes it (<see cref="CallsTo"/>). Each
/// way a builder comes to be is its own trace, so a helper called from two route groups gives
/// two, each with its own prefix, groups, and conventions. Built once per check.
/// </summary>
internal sealed class BuilderOrigins(Compilation compilation, WellKnownTypes wkt)
{
    private readonly Dictionary<
        IMethodSymbol,
        (List<IInvocationOperation>, List<ExpressionSyntax>)
    > _calls = new(SymbolEqualityComparer.Default);

    private IMethodSymbol? _entryPoint;
    private bool _entryPointRead;

    // ---------------------------------------------------------------
    // The trace
    // ---------------------------------------------------------------

    /// <summary>Every way the builder <paramref name="value"/> comes to be.</summary>
    public IReadOnlyList<BuilderTrace> Trace(IOperation value) => Trace(value, [], []);

    /// <summary>
    /// <paramref name="descended"/> holds the helper calls the trace went into to follow what
    /// they return, outermost first, so a parameter of one of them is that call's argument.
    /// <paramref name="path"/> holds the calls and parameters the trace is inside, so a cycle
    /// ends; another path reaching one of them traces it again.
    /// </summary>
    private List<BuilderTrace> Trace(
        IOperation value,
        ImmutableArray<IInvocationOperation> descended,
        HashSet<object> path
    )
    {
        var current = Unwrap(value);
        switch (current)
        {
            case ILocalReferenceOperation local:
                return CoverageChecker.ProvenanceValue(local) is { } provenance
                    ? Trace(provenance, descended, path)
                    : [Ends(TraceEnd.Untraced)];
            case IInvocationOperation call when IsMapCall(call):
                return [Ends(TraceEnd.Made)];
            case IInvocationOperation call when call.TargetMethod.SourceBody() is { } body:
                return Along(
                    call.Syntax,
                    () =>
                        [
                            .. CoverageChecker
                                .ReturnedValues(body)
                                .SelectMany(returned => Trace(returned, descended.Add(call), path))
                                .DefaultIfEmpty(Ends(TraceEnd.Untraced)),
                        ]
                );
            case IInvocationOperation call when PassedBuilder(call) is { } passed:
                return Trace(passed, descended, path);
            case IInvocationOperation:
                return [Ends(TraceEnd.Made)];
            case IParameterReferenceOperation { Parameter: var parameter }:
                return Parameter(parameter);
            case IInstanceReferenceOperation when descended is [.., var into]:
                // A helper method returning this hands back the instance it was called on.
                return into.Instance is { } instance
                    ? Trace(instance, descended.RemoveAt(descended.Length - 1), path)
                    : [Ends(TraceEnd.Untraced)];
            default:
                return [Ends(TraceEnd.Untraced)];
        }

        BuilderTrace Ends(TraceEnd end) => new(end, current, descended, []);

        List<BuilderTrace> Parameter(IParameterSymbol parameter)
        {
            // Inside a helper the trace went into, the parameter is that call's argument.
            for (var index = descended.Length - 1; index >= 0; index--)
            {
                if (Declares(descended[index], parameter))
                {
                    return BoundArgument(descended[index], parameter) is { } argument
                        ? Trace(argument, [.. descended.Take(index)], path)
                        : [Ends(TraceEnd.Untraced)];
                }
            }

            if (
                parameter.ContainingSymbol is IMethodSymbol
                {
                    MethodKind: MethodKind.AnonymousFunction
                }
            )
            {
                return [Ends(TraceEnd.Lambda)];
            }

            if (
                parameter.ContainingSymbol
                is not IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction,
                } method
            )
            {
                return [Ends(TraceEnd.Untraced)];
            }

            // Where the trace started inside the helper: each call that passes the parameter.
            return Along(
                parameter.OriginalDefinition,
                () =>
                {
                    var (calls, escapes) = CallsTo(method);
                    if (calls.Count == 0 && escapes.Count == 0)
                    {
                        return [Ends(TraceEnd.Uncalled)];
                    }

                    var traces = calls
                        .SelectMany(call =>
                            BoundArgument(call, parameter) is { } argument
                                ? Trace(argument, [], path)
                                    .Select(trace =>
                                        trace with
                                        {
                                            Ascents = trace.Ascents.Add(new Ascent(call, argument)),
                                        }
                                    )
                                : []
                        )
                        .ToList();

                    // A helper also handed on as a delegate runs where --check can't follow.
                    if (escapes.Count > 0)
                    {
                        traces.Add(Ends(TraceEnd.Untraced));
                    }

                    return traces;
                }
            );
        }

        // A cycle back into the trace adds nothing: what it leads to is already in the union.
        List<BuilderTrace> Along(object step, Func<List<BuilderTrace>> trace)
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
    /// The route-group chains <paramref name="mapCall"/>, a Map* call, is made in: one for each
    /// way its receiver comes to be and each call of the helpers it's made in, each the Map*
    /// call then its MapGroup calls outward. A chain that doesn't end at the app is
    /// unresolved: its prefix and its groups' conventions can't be seen.
    /// </summary>
    public IReadOnlyList<BuilderChain> Chains(IInvocationOperation mapCall)
    {
        var chains = new List<BuilderChain>();
        if (mapCall.Arguments is not [var receiver, ..])
        {
            return chains;
        }

        Walk([new ChainLink(mapCall, [])], receiver.Value, []);
        return [.. chains.Distinct()];

        void Walk(
            ImmutableArray<ChainLink> links,
            IOperation builder,
            ImmutableArray<IInvocationOperation> descended
        )
        {
            foreach (var trace in Trace(builder, descended, []))
            {
                // A parameter followed out to its callers: every link found so far runs in
                // the helper, under that call.
                var ascended = trace.Ascents.Select(ascent => ascent.Call).ToImmutableArray();
                var here = ascended.IsEmpty
                    ? links
                    :
                    [
                        .. links.Select(link =>
                            link with
                            {
                                Descended = ascended.AddRange(link.Descended),
                            }
                        ),
                    ];
                if (trace.Origin is { } origin && IsMapGroup(origin))
                {
                    var link = new ChainLink(origin, trace.Descended);
                    if (here.Any(earlier => SameCall(earlier.Call, origin)))
                    {
                        // A helper that nests groups by recursion: no fixed chain.
                        Anchor(new BuilderChain(here, Unresolved: true, trace.At));
                        continue;
                    }

                    Walk(here.Add(link), origin.Arguments[0].Value, trace.Descended);
                    continue;
                }

                Anchor(new BuilderChain(here, Unresolved: !IsApp(trace), trace.At));
            }
        }

        // A chain made in a helper's body, with the app or a group the helper didn't get
        // from a parameter, is made again on each call of the helper: one chain for each,
        // every link under that call, so a convention on what one call returns reaches its
        // own. A helper nothing calls keeps the chain as it is, as does one also handed on as
        // a delegate, for the runs --check can't follow. A call the chain is already under is
        // recursion: the chain under it once stands for every deeper one, as Trace's cut does.
        void Anchor(BuilderChain chain)
        {
            var under = chain.Links[0].Descended;
            var (calls, escapes) = CallsToFunctionHolding(
                under is [var outermost, ..] ? outermost.Syntax : chain.MapCall.Syntax
            );
            var climbing = calls
                .Where(call => !under.Any(descended => SameCall(descended, call)))
                .ToList();
            if (climbing.Count == 0 || escapes.Count > 0)
            {
                chains.Add(chain);
            }

            foreach (var call in climbing)
            {
                Anchor(
                    chain with
                    {
                        Links =
                        [
                            .. chain.Links.Select(link =>
                                link with
                                {
                                    Descended = link.Descended.Insert(0, call),
                                }
                            ),
                        ],
                    }
                );
            }
        }
    }

    /// <summary>
    /// <see cref="CallsTo"/> for the method or local function holding <paramref name="site"/>;
    /// none at the top level or in an accessor, constructor, or initializer.
    /// </summary>
    private (
        List<IInvocationOperation> Calls,
        List<ExpressionSyntax> Escapes
    ) CallsToFunctionHolding(SyntaxNode site) =>
        EnclosingFunction(site) is { } function
        && compilation.GetSemanticModel(function.SyntaxTree).GetDeclaredSymbol(function)
            is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction } method
            ? CallsTo(method)
            : ([], []);

    /// <summary>
    /// Whether a chain's end is the app rather than a route group: a builder whose type is an
    /// <c>IEndpointRouteBuilder</c> but not a <c>RouteGroupBuilder</c>. A parameter nothing in
    /// the project passes, or a lambda's, is handed in from outside, so its declared type
    /// decides; anything else (a field, a call without source) must be a class that can't be
    /// a group, as <c>WebApplication</c> is.
    /// </summary>
    private bool IsApp(BuilderTrace trace) =>
        trace.At.Type is { } type
        && type.Implements(wkt.EndpointRouteBuilder)
        && !type.DerivesFrom(wkt.RouteGroupBuilder)
        && (trace.End is TraceEnd.Uncalled or TraceEnd.Lambda || type.TypeKind == TypeKind.Class);

    // call is a call to the method declaring parameter.
    private static bool Declares(IInvocationOperation call, IParameterSymbol parameter) =>
        SymbolEqualityComparer.Default.Equals(
            (call.TargetMethod.ReducedFrom ?? call.TargetMethod).OriginalDefinition,
            parameter.ContainingSymbol.OriginalDefinition
        );

    private static IOperation? BoundArgument(
        IInvocationOperation call,
        IParameterSymbol parameter
    ) =>
        call
            .Arguments.FirstOrDefault(argument =>
                SymbolEqualityComparer.Default.Equals(
                    argument.Parameter?.OriginalDefinition,
                    parameter.OriginalDefinition
                )
            )
            ?.Value;

    internal static bool SameCall(IOperation left, IOperation right) =>
        left.Syntax.SyntaxTree == right.Syntax.SyntaxTree && left.Syntax.Span == right.Syntax.Span;

    // ---------------------------------------------------------------
    // Calls
    // ---------------------------------------------------------------

    /// <summary>
    /// The calls to <paramref name="method"/> with source in the compilation, and every other
    /// place it's named (a method group handed on as a delegate).
    /// </summary>
    public (List<IInvocationOperation> Calls, List<ExpressionSyntax> Escapes) CallsTo(
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
    public IEnumerable<(SyntaxTree Tree, SemanticModel SemanticModel)> TreesMentioning(
        params string[] texts
    )
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            if (texts.Any(wanted => text.Contains(wanted, StringComparison.Ordinal)))
            {
                yield return (tree, compilation.GetSemanticModel(tree));
            }
        }
    }

    /// <summary>
    /// Every name in the compilation bound to <paramref name="definition"/> (a method's
    /// reduced extension form and any construction of a generic counting as it).
    /// </summary>
    public IEnumerable<(SimpleNameSyntax Name, SemanticModel SemanticModel)> Mentions(
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

    public bool IsMapCall(IInvocationOperation call) =>
        IsType(call.TargetMethod.ContainingType, wkt.EndpointRouteBuilderExtensions)
        || IsMapControllers(call);

    public bool IsMapGroup(IInvocationOperation call) =>
        call.TargetMethod.Name == "MapGroup"
        && IsType(call.TargetMethod.ContainingType, wkt.EndpointRouteBuilderExtensions);

    public bool IsMapControllers(IInvocationOperation call) =>
        call.TargetMethod.Name == "MapControllers"
        && IsType(call.TargetMethod.ContainingType, wkt.ControllerEndpointRouteBuilderExtensions);

    /// <summary>
    /// The builder a convention without source hands back, when it returns the type it takes:
    /// an extension method returning its <c>this</c> parameter's type, as
    /// <c>RequireAuthorization&lt;TBuilder&gt;(this TBuilder)</c> and <c>WithName</c> do, or an
    /// instance method returning its receiver's. Null for anything else: a call that makes a
    /// new builder (MapGroup, MapGet), and a helper declared in the project, whose body says
    /// what it returns.
    /// </summary>
    public static IOperation? PassedBuilder(IInvocationOperation call) =>
        !(call.TargetMethod.ReducedFrom ?? call.TargetMethod)
            .OriginalDefinition
            .DeclaringSyntaxReferences
            .IsEmpty
            ? null
            : call switch
            {
                {
                    TargetMethod: { IsExtensionMethod: true, ReturnType: var returned },
                    Arguments: [var first, ..]
                }
                    when first.Parameter is { Ordinal: 0 } parameter
                        && SymbolEqualityComparer.Default.Equals(returned, parameter.Type) =>
                    first.Value,
                {
                    Instance: { Type: var receiverType } instance,
                    TargetMethod.ReturnType: var returned,
                } when SymbolEqualityComparer.Default.Equals(returned, receiverType) => instance,
                _ => null,
            };

    // ---------------------------------------------------------------
    // Conditions
    // ---------------------------------------------------------------

    /// <summary>
    /// What reaching <paramref name="trace"/>'s origin ran under: one entry for each way, null
    /// when that way always does. Each call a parameter was followed out through adds its own
    /// condition, relative to the origin, read <see cref="Conditions"/>' way.
    /// </summary>
    public List<Condition?> Ways(BuilderTrace trace)
    {
        List<Condition?> ways = [null];
        if (trace.Origin is not { } origin)
        {
            return ways;
        }

        foreach (var ascent in trace.Ascents)
        {
            ways =
            [
                .. ways.SelectMany(outer =>
                    Conditions(ascent.Call.Syntax, ascent.Argument, origin, null)
                        .Select(inner => Condition.Both(outer, inner))
                ),
            ];
        }

        return [.. ways.Distinct()];
    }

    /// <summary>
    /// What a convention at <paramref name="site"/>, on a builder <paramref name="origin"/>
    /// made, runs only under: one entry for each way it comes to run, null when that way always
    /// does, and none when nothing runs it. Inside the function that holds the site, that's
    /// <see cref="ConditionWhy"/>. A local function that captures the builder runs where it's
    /// called, so each call's own condition, relative to the origin, is one way.
    /// <paramref name="traced"/> is what the trace to the origin ran under (<see cref="Ways"/>).
    /// A way under two conditions is never keyed.
    /// </summary>
    public List<Condition?> Conditions(
        SyntaxNode site,
        IOperation receiver,
        IInvocationOperation origin,
        Condition? traced
    )
    {
        if (ConditionWhy(site, origin.Syntax) is { } within)
        {
            return [Condition.Both(within, traced)];
        }

        if (
            EnclosingFunction(site) is not LocalFunctionStatementSyntax function
            || Contains(function, origin.Syntax)
            || compilation.GetSemanticModel(function.SyntaxTree).GetDeclaredSymbol(function)
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
                .Select(condition => Condition.Both(condition, traced)),
        ];
    }

    /// <summary>
    /// What each call to the local function <paramref name="function"/> runs only under,
    /// relative to <paramref name="origin"/>, through the local functions that call it in
    /// turn. A call from inside its own call graph adds nothing; a delegate made from it
    /// can't be followed.
    /// </summary>
    private List<Condition?> CapturedCalls(
        IMethodSymbol function,
        SyntaxNode origin,
        HashSet<IMethodSymbol> visiting
    )
    {
        var conditions = new List<Condition?>();
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
                && compilation.GetSemanticModel(outer.SyntaxTree).GetDeclaredSymbol(outer)
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
            escapes.Select(reference => new Condition(
                $"where {function.Name}() runs, which --check can't follow: it's handed on as "
                    + $"a delegate at {reference.SourcePosition()}",
                null
            ))
        );
        return conditions;
    }

    /// <summary>
    /// <paramref name="receiver"/> reaches <paramref name="function"/> from outside it: through
    /// its own locals, conventions without source that hand the builder back, and a helper
    /// with source that returns one of its parameters, to a local or a parameter some
    /// enclosing function declares. A helper it can't follow that way counts as reaching
    /// outside, which can only add the function's call conditions.
    /// </summary>
    private static bool Captures(IOperation receiver, IMethodSymbol function)
    {
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        return Reaches(receiver);

        bool Reaches(IOperation value)
        {
            switch (Unwrap(value))
            {
                case ILocalReferenceOperation local when seen.Add(local.Local):
                    if (!Within(local.Local))
                    {
                        return true;
                    }

                    return CoverageChecker.ProvenanceValue(local) is { } provenance
                        && Reaches(provenance);
                case IParameterReferenceOperation parameter:
                    return !Within(parameter.Parameter);
                case IInvocationOperation call
                    when call.TargetMethod.SourceBody() is { } body
                        && seen.Add(call.TargetMethod.OriginalDefinition):
                    return CoverageChecker
                        .ReturnedValues(body)
                        .Any(returned =>
                            Unwrap(returned) is IParameterReferenceOperation returnedParameter
                            && Declares(call, returnedParameter.Parameter)
                                ? BoundArgument(call, returnedParameter.Parameter)
                                    is not { } argument
                                    || Reaches(argument)
                                : true
                        );
                case IInvocationOperation call when PassedBuilder(call) is { } passed:
                    return Reaches(passed);
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
    /// What <paramref name="site"/> runs only under, relative to <paramref name="origin"/> (the
    /// call that made the builder it's applied to, so a requirement in the same branch as its
    /// endpoint isn't conditional): an <c>if</c> or <c>else</c>, a <c>?:</c> branch, a switch
    /// section or arm, a <c>catch</c>, a loop body, the right of <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>??</c> or <c>??=</c>, a <c>?.</c>, or a <c>return</c> earlier in an
    /// enclosing block. The walk stops at the method or local function holding the site, unless
    /// that holds the origin too: a function runs where it's called, so its callers' conditions
    /// are added there, and a <c>return</c> above a local function's declaration never stops
    /// it. Null when it always runs.
    /// <para>
    /// The innermost condition names it. It's keyed (decision 2) only when it's the one
    /// condition up to where the walk stops, it's an <c>if</c> or <c>?:</c>, and its key holds
    /// one value wherever it's read: a <c>const</c> or <c>static readonly</c> field always; a
    /// local with one known value, or the <c>if</c> or <c>?:</c> node itself, only where the
    /// walk reached the origin or code that runs once (top-level statements or the entry
    /// point), since a helper may run more than once.
    /// </para>
    /// </summary>
    public Condition? ConditionWhy(SyntaxNode site, SyntaxNode? origin)
    {
        if (Contains(site, origin))
        {
            return null;
        }

        Condition? found = null;
        var reachedOrigin = false;
        var child = site;
        var function = EnclosingFunction(site);
        foreach (var ancestor in site.Ancestors())
        {
            // Past the origin's own statement, the site runs whenever its endpoint is mapped,
            // unless a return between the two can leave first.
            Condition? here;
            var stop = false;
            if (Contains(ancestor, origin))
            {
                here =
                    ancestor is BlockSyntax shared
                    && EarlyReturn(shared, child, origin) is { } returned
                        ? new Condition(returned, null)
                        : null;
                reachedOrigin = true;
                stop = true;
            }
            else if (ancestor == function)
            {
                here = null;
                stop = true;
            }
            else
            {
                here = Why(ancestor, child);
            }

            if (here is not null)
            {
                if (found is not null)
                {
                    return found with { Key = null };
                }

                found = here;
            }

            if (stop)
            {
                break;
            }

            child = ancestor;
        }

        return
            found?.Key is { } key
            && key.Symbol is not IFieldSymbol
            && !reachedOrigin
            && !RunsOnce(function, site)
            ? found with
            {
                Key = null,
            }
            : found;
    }

    private Condition? Why(SyntaxNode ancestor, SyntaxNode child) =>
        ancestor switch
        {
            BlockSyntax block => EarlyReturn(block, child, null) is { } returned
                ? new Condition(returned, null)
                : null,
            IfStatementSyntax statement when child != statement.Condition => new Condition(
                $"under if ({statement.Condition})",
                Key(statement.Condition, statement, positive: child == statement.Statement)
            ),
            ConditionalExpressionSyntax conditional when child != conditional.Condition =>
                new Condition(
                    $"under a branch of ({conditional.Condition}) ? ... : ...",
                    Key(conditional.Condition, conditional, positive: child == conditional.WhenTrue)
                ),
            SwitchSectionSyntax or SwitchExpressionArmSyntax => new Condition(
                "under a switch",
                null
            ),
            CatchClauseSyntax => new Condition("in a catch block", null),
            ForStatementSyntax loop when child == loop.Statement => new Condition(
                "in a for loop",
                null
            ),
            CommonForEachStatementSyntax loop when child == loop.Statement => new Condition(
                "in a foreach loop",
                null
            ),
            WhileStatementSyntax loop when child == loop.Statement => new Condition(
                "in a while loop",
                null
            ),
            ConditionalAccessExpressionSyntax access when child == access.WhenNotNull =>
                new Condition($"behind {access.Expression}?.", null),
            BinaryExpressionSyntax binary
                when child == binary.Right
                    && binary.Kind()
                        is SyntaxKind.LogicalAndExpression
                            or SyntaxKind.LogicalOrExpression
                            or SyntaxKind.CoalesceExpression => new Condition(
                $"on the right of {binary.OperatorToken}",
                null
            ),
            AssignmentExpressionSyntax assignment
                when child == assignment.Right
                    && assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression) => new Condition(
                "on the right of ??=",
                null
            ),
            _ => null,
        };

    /// <summary>
    /// The unknown an <c>if</c> or <c>?:</c> branch is: a read, optionally under <c>!</c>, of a
    /// <c>const</c> or <c>static readonly</c> field or a local with one known value is keyed
    /// by what it reads, so two such conditions agree; anything else by the node, so only its
    /// own two branches do.
    /// </summary>
    private ConditionKey Key(ExpressionSyntax condition, SyntaxNode node, bool positive)
    {
        var expression = condition;
        var negated = false;
        while (true)
        {
            if (expression is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized.Expression;
            }
            else if (
                expression is PrefixUnaryExpressionSyntax not
                && not.IsKind(SyntaxKind.LogicalNotExpression)
            )
            {
                negated = !negated;
                expression = not.Operand;
            }
            else
            {
                break;
            }
        }

        ISymbol? read = compilation.GetSemanticModel(expression.SyntaxTree).GetOperation(expression)
            is { } operation
            ? Unwrap(operation) switch
            {
                ILocalReferenceOperation local
                    when CoverageChecker.ProvenanceValue(local) is not null => local.Local,
                IFieldReferenceOperation { Field: var field }
                    when field.IsConst || field.IsStatic && field.IsReadOnly => field,
                _ => null,
            }
            : null;
        return read is not null
            ? new ConditionKey(read, null, positive != negated)
            : new ConditionKey(null, node, positive);
    }

    /// <summary>
    /// <paramref name="site"/>, held by <paramref name="function"/>, is in code that runs
    /// once: top-level statements or the entry point, configure lambdas inside them included.
    /// </summary>
    public bool RunsOnce(SyntaxNode? function, SyntaxNode site)
    {
        if (function is null)
        {
            return site.Ancestors().Any(node => node is GlobalStatementSyntax);
        }

        if (!_entryPointRead)
        {
            _entryPoint = compilation.GetEntryPoint(CancellationToken.None);
            _entryPointRead = true;
        }

        return _entryPoint is not null
            && SymbolEqualityComparer.Default.Equals(
                compilation.GetSemanticModel(function.SyntaxTree).GetDeclaredSymbol(function),
                _entryPoint
            );
    }

    // node holds origin.
    private static bool Contains(SyntaxNode node, SyntaxNode? origin) =>
        origin is not null
        && node.SyntaxTree == origin.SyntaxTree
        && node.Span.Contains(origin.Span);

    /// <summary>
    /// The method, local function, or accessor holding <paramref name="site"/>, past any
    /// lambda: null at the top level and in an initializer.
    /// </summary>
    public static SyntaxNode? EnclosingFunction(SyntaxNode site) =>
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

    private static IOperation Unwrap(IOperation operation) =>
        operation is IConversionOperation conversion ? Unwrap(conversion.Operand) : operation;

    private static bool IsType(INamedTypeSymbol? actual, INamedTypeSymbol? expected) =>
        expected is not null
        && SymbolEqualityComparer.Default.Equals(actual?.OriginalDefinition, expected);
}

/// <summary>How a trace ends.</summary>
internal enum TraceEnd
{
    /// <summary>At the call that made the builder: a Map*, MapGroup, or MapControllers call, or another call without source.</summary>
    Made,

    /// <summary>At a parameter of a method nothing in the project calls or names.</summary>
    Uncalled,

    /// <summary>At a lambda's parameter.</summary>
    Lambda,

    /// <summary>At a value --check can't follow: a field, a local without one known value, or a helper's parameter handed on as a delegate.</summary>
    Untraced,
}

/// <summary>
/// One way a builder comes to be: where the trace ended (<see cref="At"/>, the call that
/// made it when <see cref="End"/> is <see cref="TraceEnd.Made"/>), the helper calls it went
/// into to get there (<see cref="Descended"/>, outermost first), and the calls it followed a
/// parameter out through (<see cref="Ascents"/>, outermost first).
/// </summary>
internal sealed record BuilderTrace(
    TraceEnd End,
    IOperation At,
    ImmutableArray<IInvocationOperation> Descended,
    ImmutableArray<Ascent> Ascents
)
{
    public IInvocationOperation? Origin => End == TraceEnd.Made ? At as IInvocationOperation : null;
}

/// <summary>A call a helper's parameter was followed out through, with the argument it passes.</summary>
internal sealed record Ascent(IInvocationOperation Call, IOperation Argument);

/// <summary>
/// A Map*, MapGroup, or MapControllers call, under the helper calls that make one runtime
/// instance of it (<see cref="Descended"/>, outermost first): a group made inside a helper
/// is a different group for each call.
/// </summary>
internal sealed record ChainLink(
    IInvocationOperation Call,
    ImmutableArray<IInvocationOperation> Descended
)
{
    /// <summary>
    /// A convention found under <paramref name="conventionDescended"/> reaches this link when
    /// those calls end its own: the convention runs in a function this link's path passes
    /// through, on the instance that path makes.
    /// </summary>
    public bool Reaches(ImmutableArray<IInvocationOperation> conventionDescended) =>
        conventionDescended.Length <= Descended.Length
        && conventionDescended
            .Zip(Descended.Skip(Descended.Length - conventionDescended.Length))
            .All(pair => BuilderOrigins.SameCall(pair.First, pair.Second));

    public bool Equals(ChainLink? other) =>
        other is not null
        && BuilderOrigins.SameCall(Call, other.Call)
        && Descended.Length == other.Descended.Length
        && Descended
            .Zip(other.Descended)
            .All(pair => BuilderOrigins.SameCall(pair.First, pair.Second));

    public override int GetHashCode() => HashCode.Combine(Call.Syntax.Span, Descended.Length);
}

/// <summary>
/// One mapping of a minimal-API handler: its Map* call, then the MapGroup calls it's made
/// in, outward (<see cref="Links"/>), and where the trace ended (<see cref="End"/>). An
/// unresolved chain didn't end at the app, so its prefix and conventions can't be seen.
/// </summary>
internal sealed record BuilderChain(
    ImmutableArray<ChainLink> Links,
    bool Unresolved,
    IOperation End
)
{
    public IInvocationOperation MapCall => Links[0].Call;

    /// <summary>The MapGroup calls, innermost first.</summary>
    public IEnumerable<IInvocationOperation> Groups => Links.Skip(1).Select(link => link.Call);

    /// <summary>
    /// The accumulated prefix, or null when it can't be known statically: the chain is
    /// unresolved, or a prefix isn't a compile-time constant (mirroring the extractor's
    /// constant-string policy).
    /// </summary>
    public string? Prefix()
    {
        if (Unresolved)
        {
            return null;
        }

        var prefix = "";
        foreach (var group in Groups.Reverse())
        {
            if (
                group
                    .Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "prefix")
                    ?.Value.ConstantValue
                is not { HasValue: true, Value: string part }
            )
            {
                return null;
            }

            prefix = TransportIdentity.NormalizeRoute($"{prefix}/{part.Trim('/')}");
        }

        return prefix;
    }

    public bool Equals(BuilderChain? other) =>
        other is not null && Unresolved == other.Unresolved && Links.SequenceEqual(other.Links);

    public override int GetHashCode() => HashCode.Combine(Links.Length, Links[0]);
}

/// <summary>
/// What a site runs only under (<see cref="Why"/>), and, when it's one condition whose value
/// another can share, its <see cref="Key"/>.
/// </summary>
internal sealed record Condition(string Why, ConditionKey? Key)
{
    /// <summary>
    /// A way that runs under both: the first one's reason, and no key when there are two, since
    /// a key stands for one condition.
    /// </summary>
    public static Condition? Both(Condition? first, Condition? second) =>
        first is null ? second
        : second is null ? first
        : first with { Key = null };
}

/// <summary>
/// The one unknown a condition is: what it reads (<see cref="Symbol"/>) or the
/// <c>if</c>/<c>?:</c> node itself (<see cref="Node"/>), and whether the site runs when that's
/// true (<see cref="Positive"/>).
/// </summary>
internal sealed record ConditionKey(ISymbol? Symbol, SyntaxNode? Node, bool Positive)
{
    public ConditionKey Negated() => this with { Positive = !Positive };

    /// <summary>Both stand for the same unknown, whichever way round.</summary>
    public bool SameUnknown(ConditionKey other) =>
        Symbol is not null
            ? SymbolEqualityComparer.Default.Equals(Symbol, other.Symbol)
            : Node is not null && Node == other.Node;
}
