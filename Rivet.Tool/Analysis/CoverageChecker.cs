using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

public enum CoverageWarningKind
{
    MissingImplementation,
    OrphanedBinding,
    HttpMethodMismatch,
    RouteMismatch,
    UnbindableEnumParameter,
    InputBindingMismatch,

    /// <summary>RIV4008: the contract's security and the handler's authorization disagree.</summary>
    SecurityMismatch,

    /// <summary>RIV4009: whether the handler requires authentication can't be read.</summary>
    AuthorizationUnreadable,
}

public sealed record CoverageWarning(
    CoverageWarningKind Kind,
    string ContractName,
    string FieldName,
    string? Expected,
    string? Actual,
    Location? Location,
    string? Message = null
);

public static class CoverageChecker
{
    private static readonly Dictionary<string, string> _minimalApiMethodMap = new(
        StringComparer.Ordinal
    )
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
    };

    public static IReadOnlyList<CoverageWarning> Check(
        Compilation compilation,
        WellKnownTypes wkt,
        DiscoveredSymbols discovered,
        IReadOnlyList<ContractEndpoint> contractEndpoints,
        ContractSecurityMetadata? security,
        string functionsRoutePrefix
    )
    {
        var fieldMap = new Dictionary<IFieldSymbol, TsEndpointDefinition>(
            SymbolEqualityComparer.Default
        );
        foreach (var (endpoint, field) in contractEndpoints)
        {
            if (field is not null)
            {
                fieldMap[field] = endpoint;
            }
        }

        var origins = new BuilderOrigins(compilation, wkt);
        if (fieldMap.Count == 0)
        {
            return
            [
                .. SecurityWarnings(
                    compilation,
                    wkt,
                    origins,
                    discovered,
                    security,
                    fieldMap,
                    new Dictionary<IFieldSymbol, List<TerminalImplementation>>(
                        SymbolEqualityComparer.Default
                    ),
                    mapHandlers: null
                ),
            ];
        }

        var adapterType = compilation.GetTypeByMetadataName("Rivet.RivetResultExtensions");
        var mapHandlers = IndexMapHandlers(compilation, wkt);

        var implementations = new Dictionary<IFieldSymbol, List<TerminalImplementation>>(
            SymbolEqualityComparer.Default
        );
        var bindings = new List<ContractBinding>();
        var consumedBindings = new HashSet<SyntaxNode>();
        var bindingHandlers = new HashSet<SyntaxNode>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (
                var syntax in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            )
            {
                if (semanticModel.GetOperation(syntax) is not IInvocationOperation invocation)
                {
                    continue;
                }

                if (
                    IsBind(wkt, invocation)
                    && ResolveContractReference(wkt, invocation.Instance, fieldMap, []) is { } bound
                )
                {
                    bindings.Add(new ContractBinding(bound.Field, syntax));
                    if (
                        RouteHandler(wkt, mapHandlers, invocation, functionsRoutePrefix)
                            is { } handler
                        && bindingHandlers.Add(handler.Syntax)
                    )
                    {
                        consumedBindings.UnionWith(
                            BindingsPassedOnReturn(wkt, adapterType, handler, fieldMap)
                        );
                    }
                }

                if (
                    !IsTerminal(wkt, invocation)
                    || ResolveContractReference(wkt, invocation.Instance, fieldMap, [])
                        is not { } reference
                )
                {
                    continue;
                }

                var contexts = ResolveImplementations(
                    wkt,
                    origins,
                    adapterType,
                    mapHandlers,
                    invocation,
                    fieldMap,
                    functionsRoutePrefix
                );
                if (contexts.Count == 0)
                {
                    continue;
                }

                if (reference.Binding is not null)
                {
                    consumedBindings.Add(reference.Binding);
                }

                if (!implementations.TryGetValue(reference.Field, out var fieldImplementations))
                {
                    fieldImplementations = [];
                    implementations[reference.Field] = fieldImplementations;
                }

                fieldImplementations.AddRange(
                    contexts.Select(context => new TerminalImplementation(syntax, context))
                );
            }
        }

        var warnings = BuildWarnings(
            wkt,
            security,
            fieldMap,
            implementations,
            bindings,
            consumedBindings
        );
        return
        [
            .. warnings,
            .. SecurityWarnings(
                compilation,
                wkt,
                origins,
                discovered,
                security,
                fieldMap,
                implementations,
                mapHandlers
            ),
        ];
    }

    // Matched on the receiver's type, not the method's declaring type: Error and File are
    // declared once on shared base classes, and the route types are sealed.
    private static bool IsTerminal(WellKnownTypes wkt, IInvocationOperation invocation) =>
        invocation
            is {
                TargetMethod.Name: "Success" or "Error" or "File",
                Instance.Type: INamedTypeSymbol receiver,
            }
        && IsOneOf(
            receiver.OriginalDefinition,
            wkt.RouteDefinition,
            wkt.RouteDefinitionOfT,
            wkt.FileRouteDefinition,
            wkt.BoundRouteDefinition,
            wkt.BoundRouteDefinitionOfT,
            wkt.BoundFileRouteDefinition
        );

    private static bool IsBind(WellKnownTypes wkt, IInvocationOperation invocation) =>
        invocation is { TargetMethod.Name: "Bind", Instance: not null }
        && IsOneOf(
            invocation.TargetMethod.ContainingType.OriginalDefinition,
            wkt.RouteDefinitionOfTInputTOutput,
            wkt.InputRouteDefinitionOfT,
            wkt.FileRouteDefinitionOfT
        );

    /// <summary>
    /// The contract field a route-definition value comes from: the field itself, a local
    /// with a single statically known value, or a .Bind(...) of either (recorded as the
    /// binding the terminal consumes).
    /// </summary>
    private static ContractReference? ResolveContractReference(
        WellKnownTypes wkt,
        IOperation? operation,
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap,
        HashSet<ILocalSymbol> visitedLocals
    ) =>
        WithoutImplicitConversions(operation) switch
        {
            IFieldReferenceOperation { Field: var field } when fieldMap.ContainsKey(field) =>
                new ContractReference(field, null),
            ILocalReferenceOperation local
                when visitedLocals.Add(local.Local) && ProvenanceValue(local) is { } value =>
                ResolveContractReference(wkt, value, fieldMap, visitedLocals),
            IInvocationOperation bind when IsBind(wkt, bind) => ResolveContractReference(
                wkt,
                bind.Instance,
                fieldMap,
                visitedLocals
            )
                is { } reference
                ? reference with
                {
                    Binding = bind.Syntax,
                }
                : null,
            _ => null,
        };

    /// <summary>
    /// The template a Map* route argument holds: a compile-time constant, or a contract
    /// field's .Route read through any value <see cref="ResolveContractReference"/>
    /// resolves. The field's RouteTemplate is its Define… literal with route constraints
    /// stripped, and NormalizeRoute strips them from every Map* route, so it compares
    /// exactly as the literal would.
    /// </summary>
    private static string? ResolveRouteArgument(
        WellKnownTypes wkt,
        IOperation pattern,
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap
    ) =>
        pattern.ConstantValue is { HasValue: true, Value: string constant } ? constant
        : WithoutImplicitConversions(pattern)
            is IPropertyReferenceOperation
            {
                Property: { Name: "Route", ContainingType: var declaringType },
            } route
        && IsOneOf(declaringType.OriginalDefinition, wkt.RouteDefinitionBase)
        && ResolveContractReference(wkt, route.Instance, fieldMap, []) is { } reference
            ? fieldMap[reference.Field].RouteTemplate
        : null;

    /// <summary>
    /// The single value a local holds at <paramref name="use"/>, or null when it cannot
    /// be known statically: the local is ref, passed by ref/out, or assigned more than
    /// its initializer. A local declared without an initializer qualifies only through
    /// one simple assignment statement in the declaring block, between the declaration
    /// and the statement using it. A top-level statement's local is scoped to the file's
    /// top-level program.
    /// </summary>
    internal static IOperation? ProvenanceValue(ILocalReferenceOperation use)
    {
        var local = use.Local;
        if (
            local.RefKind != RefKind.None
            || local.DeclaringSyntaxReferences is not [var reference]
            || reference.GetSyntax() is not VariableDeclaratorSyntax declaratorSyntax
            || use.SemanticModel is not { } semanticModel
        )
        {
            return null;
        }

        var scopeSyntax =
            declaratorSyntax.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() as SyntaxNode
            ?? declaratorSyntax.FirstAncestorOrSelf<LocalFunctionStatementSyntax>() as SyntaxNode
            ?? declaratorSyntax.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>() as SyntaxNode
            ?? (
                declaratorSyntax.FirstAncestorOrSelf<GlobalStatementSyntax>() is not null
                    ? declaratorSyntax.FirstAncestorOrSelf<CompilationUnitSyntax>()
                    : null
            );
        if (scopeSyntax is null || semanticModel.GetOperation(scopeSyntax) is not { } scope)
        {
            return null;
        }

        var scopeOperations = scope.Descendants().ToList();
        if (
            scopeOperations
                .OfType<IVariableDeclaratorOperation>()
                .FirstOrDefault(declarator =>
                    SymbolEqualityComparer.Default.Equals(declarator.Symbol, local)
                )
            is not { } declarator
        )
        {
            return null;
        }

        var assignments = scopeOperations
            .OfType<IAssignmentOperation>()
            .Where(assignment => IsLocal(assignment.Target, local))
            .ToList();
        if (
            scopeOperations
                .OfType<IArgumentOperation>()
                .Any(argument =>
                    argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out
                    && IsLocal(argument.Value, local)
                )
        )
        {
            return null;
        }

        if (declarator.GetVariableInitializer() is { Value: var initializer })
        {
            return assignments.Count == 0 ? initializer : null;
        }

        var useStatement = Ancestors(use)
            .FirstOrDefault(operation =>
                !operation.IsImplicit && operation.Parent is IBlockOperation { IsImplicit: false }
            );
        return
            assignments
                is [
                    ISimpleAssignmentOperation
                    {
                        Parent: IExpressionStatementOperation
                        {
                            Parent: IBlockOperation assignmentBlock
                        },
                    } assignment,
                ]
            && declarator.Parent?.Parent
                is IVariableDeclarationGroupOperation { Parent: IBlockOperation declarationBlock }
            && IsSameSyntax(assignmentBlock.Syntax, declarationBlock.Syntax)
            && useStatement?.Parent is { } useBlock
            && IsSameSyntax(useBlock.Syntax, declarationBlock.Syntax)
            && assignment.Syntax.SpanStart > declarator.Syntax.Span.End
            && assignment.Syntax.Span.End < useStatement.Syntax.SpanStart
            ? assignment.Value
            : null;

        static bool IsLocal(IOperation operation, ILocalSymbol local) =>
            WithoutImplicitConversions(operation) is ILocalReferenceOperation reference
            && SymbolEqualityComparer.Default.Equals(reference.Local, local);
    }

    private static IReadOnlyList<CoverageWarning> BuildWarnings(
        WellKnownTypes wkt,
        ContractSecurityMetadata? security,
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap,
        IReadOnlyDictionary<IFieldSymbol, List<TerminalImplementation>> implementations,
        IReadOnlyList<ContractBinding> bindings,
        IReadOnlySet<SyntaxNode> consumedBindings
    )
    {
        var warnings = new List<CoverageWarning>();
        var requestReads = new ContractInputBinding.RequestReads();
        foreach (var (field, endpoint) in fieldMap)
        {
            if (!implementations.TryGetValue(field, out var fieldImplementations))
            {
                warnings.Add(
                    new CoverageWarning(
                        CoverageWarningKind.MissingImplementation,
                        field.ContainingType.Name,
                        field.Name,
                        Expected: $"{endpoint.HttpMethod} {endpoint.RouteTemplate}",
                        Actual: "(none)",
                        Location: field.Locations.FirstOrDefault()
                    )
                );
                continue;
            }

            // Every mapping of a handler is read, for RIV4005 and RIV4006 alike: its route
            // values and its body inference both depend on the Map* call, and a method group
            // mapped with both MapGet and MapPost binds an unattributed array from the query on
            // one and the body on the other. Each mapping is read once, however many terminals
            // it returns, and a warning more than one mapping gives (they point at the handler
            // or its parameter, or two mappings share a route and verb) is reported once.
            var reported = new HashSet<CoverageWarning>();
            var mappings = new HashSet<(IMethodSymbol, SyntaxTree?, TextSpan?, string?, bool)>();
            void Report(CoverageWarning warning)
            {
                if (reported.Add(warning))
                {
                    warnings.Add(warning);
                }
            }

            foreach (var implementation in fieldImplementations)
            {
                if (
                    implementation.Context
                        is {
                            Handler: { } handler,
                            BindingHost: { } host,
                            InfersBody: var infersBody,
                            MapCall: var mapCall,
                            Route: var mappedRoute,
                        }
                    && mappings.Add(
                        (
                            handler.OriginalDefinition,
                            mapCall?.Syntax.SyntaxTree,
                            mapCall?.Syntax.Span,
                            mappedRoute,
                            infersBody
                        )
                    )
                )
                {
                    warnings.AddRange(
                        UnbindableEnumParameters(wkt, field, endpoint, handler, host, infersBody)
                            .Concat(
                                ContractInputBinding
                                    .Compare(
                                        wkt,
                                        security,
                                        endpoint,
                                        handler,
                                        host,
                                        infersBody,
                                        implementation.Context.Route,
                                        requestReads
                                    )
                                    .Select(mismatch => new CoverageWarning(
                                        CoverageWarningKind.InputBindingMismatch,
                                        field.ContainingType.Name,
                                        field.Name,
                                        mismatch.Expected,
                                        mismatch.Actual,
                                        mismatch.Location,
                                        Message: $"{field.ContainingType.Name}.{field.Name} "
                                            + $"({endpoint.HttpMethod} {endpoint.RouteTemplate}): "
                                            + mismatch.Problem
                                    ))
                            )
                            .Where(reported.Add)
                    );
                }

                if (
                    implementation.Context.HttpMethods.Count > 0
                    && !implementation.Context.HttpMethods.Contains(
                        endpoint.HttpMethod,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    Report(
                        new CoverageWarning(
                            CoverageWarningKind.HttpMethodMismatch,
                            field.ContainingType.Name,
                            field.Name,
                            Expected: endpoint.HttpMethod,
                            Actual: string.Join(", ", implementation.Context.HttpMethods),
                            Location: implementation.Invocation.GetLocation()
                        )
                    );
                }

                if (
                    implementation.Context.RouteError is not null
                    || (
                        implementation.Context.Route is not null
                        && !RoutesMatch(endpoint.RouteTemplate, implementation.Context.Route)
                    )
                )
                {
                    Report(
                        new CoverageWarning(
                            CoverageWarningKind.RouteMismatch,
                            field.ContainingType.Name,
                            field.Name,
                            Expected: endpoint.RouteTemplate,
                            Actual: implementation.Context.RouteError
                                ?? implementation.Context.Route,
                            Location: implementation.Invocation.GetLocation()
                        )
                    );
                }
            }
        }

        foreach (
            var binding in bindings
                .Where(binding => !consumedBindings.Contains(binding.Invocation))
                .OrderBy(binding => binding.Invocation.SyntaxTree.FilePath, StringComparer.Ordinal)
                .ThenBy(binding => binding.Invocation.SpanStart)
        )
        {
            warnings.Add(
                new CoverageWarning(
                    CoverageWarningKind.OrphanedBinding,
                    binding.Field.ContainingType.Name,
                    binding.Field.Name,
                    Expected: "returned terminal, or a returned helper taking the binding",
                    Actual: "(none)",
                    Location: binding.Invocation.GetLocation()
                )
            );
        }

        return warnings;
    }

    /// <summary>
    /// K.5: each handler's authorization against the security its spec declares, asking only
    /// whether a caller must authenticate (schemes and policies can't be matched). The spec is
    /// a contract field's own security; for an override of an abstract <c>[RivetContract]</c>
    /// method, that method's <c>[RivetSecure]</c>-family attributes; for a
    /// <c>[RivetEndpoint]</c> method or a <c>[RivetClient]</c> controller's action, its own
    /// (<see cref="SecurityDeclaration.FromAttributes"/>); else the document's default. None at
    /// all is what the emitted OpenAPI says it is: no credentials needed. An instance method is
    /// an MVC action; a static one is a minimal-API handler, compared at each of its mappings:
    /// each Map* call that names it as a method group, in each route-group chain
    /// <see cref="BuilderOrigins.Chains"/> finds. A handler with more than one mapping is
    /// named by the one each warning is about. A secured spec over a handler anyone can reach,
    /// and a spec admitting anonymous callers over a handler that requires authentication, are
    /// RIV4008.
    /// When the document declares no security scheme at all, the handlers that require
    /// authentication under a spec that says nothing are one RIV4008, since one missing default
    /// is the cause. Authorization --check can't read is RIV4009, never a guess. A handler is
    /// compared under every contract field it serves, and then not again for its attributes:
    /// the merge holds the two to one security (RIV2012).
    /// </summary>
    private static IEnumerable<CoverageWarning> SecurityWarnings(
        Compilation compilation,
        WellKnownTypes wkt,
        BuilderOrigins origins,
        DiscoveredSymbols discovered,
        ContractSecurityMetadata? security,
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap,
        IReadOnlyDictionary<IFieldSymbol, List<TerminalImplementation>> implementations,
        MapHandlerIndex? mapHandlers
    )
    {
        var operations = AttributeOperations(wkt, discovered).ToList();
        if (fieldMap.Count == 0 && operations.Count == 0)
        {
            yield break;
        }

        var authorization = HandlerAuthorization.Scan(compilation, wkt, origins);
        var comparison = new SecurityComparison(security);
        var compared = new HashSet<(ISymbol, ISymbol, BuilderChain?)>();

        // Two mappings at one route (a helper called twice) give the same warning: it's
        // reported once.
        var reported = new HashSet<CoverageWarning>();
        var handled = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var (field, endpoint) in fieldMap)
        {
            if (!implementations.TryGetValue(field, out var fieldImplementations))
            {
                continue;
            }

            var spec = new SecuritySpec(
                field.ContainingType.Name,
                field.Name,
                $"{field.ContainingType.Name}.{field.Name} ({endpoint.HttpMethod} {endpoint.RouteTemplate})",
                endpoint.Security,
                AttributesOn: null
            );
            var contexts = fieldImplementations
                .Select(implementation => implementation.Context)
                .ToList();
            foreach (var context in contexts)
            {
                if (
                    context.Handler is not { } handler
                    || !compared.Add((field, handler, context.Chain))
                )
                {
                    continue;
                }

                handled.Add(handler);
                var verdict = context switch
                {
                    { BindingHost: ParameterBindingHost.Functions } => authorization.ForFunction(
                        handler
                    ),
                    { BindingHost: ParameterBindingHost.MinimalApi, Chain: { } chain } =>
                        authorization.ForMinimalApi(handler, chain),
                    { BindingHost: ParameterBindingHost.Mvc } => authorization.ForMvc(handler),
                    _ => null,
                };

                // A handler mapped more than once is named by the mapping each warning is about.
                var mappings = contexts
                    .Where(other =>
                        SymbolEqualityComparer.Default.Equals(other.Handler, handler)
                        && other.Chain is not null
                    )
                    .Select(other => other.Chain)
                    .Distinct()
                    .Count();
                var mappedSpec =
                    mappings > 1 && context.Chain is { } mapped
                        ? spec with
                        {
                            Label = $"{spec.Label}, {Mapping(mapped, context.Route)}",
                        }
                        : spec;
                if (
                    verdict is not null
                    && comparison.Compare(mappedSpec, verdict, handler.Locations.FirstOrDefault())
                        is { } warning
                    && reported.Add(warning)
                )
                {
                    yield return warning;
                }
            }
        }

        foreach (var (handler, spec) in operations)
        {
            if (!handled.Add(handler))
            {
                continue;
            }

            List<(Verdict Verdict, SecuritySpec Spec)> verdicts;
            if (EndpointWalker.HostOf(handler) == ParameterBindingHost.MinimalApi)
            {
                mapHandlers ??= IndexMapHandlers(compilation, wkt);
                var chains = handler.SourceBody() is { } body
                    ? (
                        mapHandlers.GetValueOrDefault((body.Syntax.SyntaxTree, body.Syntax.Span))
                        ?? []
                    )
                        .SelectMany(mapped =>
                            origins
                                .Chains(mapped.MapCall)
                                .Select(chain => (mapped.Symbol, Chain: chain))
                        )
                        .ToList()
                    : [];
                verdicts =
                [
                    .. chains.Select(mapped =>
                        (
                            authorization.ForMinimalApi(mapped.Symbol, mapped.Chain),
                            chains.Count > 1
                                ? spec with
                                {
                                    Label =
                                        $"{spec.Label}, {Mapping(mapped.Chain, MappedRoute(mapped.Chain))}",
                                }
                                : spec
                        )
                    ),
                ];
            }
            else
            {
                verdicts = [(authorization.ForMvc(handler), spec)];
            }

            foreach (var (verdict, mappedSpec) in verdicts)
            {
                if (
                    comparison.Compare(mappedSpec, verdict, handler.Locations.FirstOrDefault())
                        is { } warning
                    && reported.Add(warning)
                )
                {
                    yield return warning;
                }
            }
        }

        if (comparison.Summary() is { } summary)
        {
            yield return summary;
        }
    }

    /// <summary>
    /// Every operation whose spec is declared by attributes, with that spec: an override of
    /// an abstract <c>[RivetContract]</c> method (the contract method's security), then a
    /// <c>[RivetEndpoint]</c> method or a <c>[RivetClient]</c> controller's action (its own),
    /// as <see cref="ContractWalker.AbstractOperations"/> and <see cref="EndpointWalker.Walk"/>
    /// find them. An abstract method has no handler of its own, and a method that isn't an
    /// operation (no [Http*] verb) has no spec.
    /// </summary>
    private static IEnumerable<(IMethodSymbol Handler, SecuritySpec Spec)> AttributeOperations(
        WellKnownTypes wkt,
        DiscoveredSymbols discovered
    )
    {
        var contractMethods = discovered
            .ContractTypes.SelectMany(ContractWalker.AbstractOperations)
            .ToHashSet(SymbolEqualityComparer.Default);
        if (contractMethods.Count > 0)
        {
            foreach (var method in discovered.Overrides)
            {
                foreach (var overridden in method.OverriddenChain())
                {
                    if (
                        contractMethods.Contains(overridden.OriginalDefinition)
                        && Spec((IMethodSymbol)overridden.OriginalDefinition, "the contract method")
                            is { } spec
                    )
                    {
                        yield return (method, spec);
                        break;
                    }
                }
            }
        }

        // EndpointWalker.Walk's operations: every [RivetEndpoint] method, and a [RivetClient]
        // class's.
        var annotated = discovered
            .EndpointMethods.Concat(EndpointWalker.ClientMethods(wkt, discovered.ClientTypes))
            .Where(method => !method.IsAbstract)
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<IMethodSymbol>();
        foreach (var method in annotated)
        {
            if (
                Spec(
                    method,
                    EndpointWalker.HostOf(method) == ParameterBindingHost.Mvc
                        ? "the action"
                        : "the method"
                ) is
                { } spec
            )
            {
                yield return (method, spec);
            }
        }

        SecuritySpec? Spec(IMethodSymbol declaring, string attributesOn)
        {
            var (httpMethod, route) = EndpointWalker.ResolveActionRoute(wkt, declaring);
            if (httpMethod is null)
            {
                return null;
            }

            var label = $"{declaring.ContainingType.Name}.{declaring.Name}";
            return new SecuritySpec(
                declaring.ContainingType.Name,
                declaring.Name,
                $"{label} ({httpMethod} {route ?? "(unresolved route)"})",
                SecurityDeclaration.FromAttributes(wkt, declaring, label),
                attributesOn
            );
        }
    }

    /// <summary>
    /// What a handler's spec says about security: its own requirement (null inherits the
    /// document's), and where it's declared, which decides how a diagnostic spells the fix:
    /// a contract field's builder calls (<paramref name="AttributesOn"/> null), or
    /// <c>[RivetSecure]</c>-family attributes on what <paramref name="AttributesOn"/> names.
    /// </summary>
    private sealed record SecuritySpec(
        string ContractName,
        string FieldName,
        string Label,
        SecurityRequirements? Own,
        string? AttributesOn
    );

    /// <summary>One spec's security against one handler's verdict (K.5).</summary>
    private sealed class SecurityComparison(ContractSecurityMetadata? security)
    {
        private readonly bool _noSchemes = security is null || security.Schemes.Count == 0;
        private readonly List<string> _undeclared = [];

        public CoverageWarning? Compare(SecuritySpec spec, Verdict verdict, Location? location)
        {
            var requirements = spec.Own ?? security?.GlobalRequirements;
            var inherited = spec.Own is null;
            if (verdict.RequiresAuthentication is not { } requiresAuthentication)
            {
                return Warning(
                    CoverageWarningKind.AuthorizationUnreadable,
                    $"{spec.Label}: --check can't tell whether the handler requires "
                        + $"authentication: {string.Join("; ", verdict.Because)}. Apply "
                        + "authorization unconditionally, from values --check can read (a "
                        + "policy name registered inline with AddPolicy, or a policy built "
                        + "inline with AuthorizationPolicyBuilder), or decide the endpoint with "
                        + "[AllowAnonymous]."
                );
            }

            var why =
                verdict.Because.Count > 0
                    ? string.Join(", ", verdict.Because)
                    : "nothing on it, its controller or route group, or the app requires an authenticated user";
            var anonymous = spec.AttributesOn is { } attributesOn
                ? $"declare [RivetAnonymous] on {attributesOn}"
                : "say .Anonymous() in the contract";
            if (requirements is null)
            {
                if (!requiresAuthentication)
                {
                    return null;
                }

                if (_noSchemes)
                {
                    if (!_undeclared.Contains(spec.Label))
                    {
                        _undeclared.Add(spec.Label);
                    }

                    return null;
                }

                var declare = spec.AttributesOn is null ? ".Secure(...)" : "[RivetSecure(...)]";
                return Warning(
                    CoverageWarningKind.SecurityMismatch,
                    $"{spec.Label}: the spec declares no security and the document has no "
                        + "default, so it says anyone may call it, but the handler requires "
                        + $"authentication ({why}), so an anonymous caller gets a 401. Declare "
                        + $"the document's default security, or the endpoint's with {declare}, "
                        + "or let anonymous callers in with [AllowAnonymous] (.AllowAnonymous() "
                        + "on a minimal API)."
                );
            }

            if (
                requirements.Alternatives.Count == 0
                || requirements.Alternatives.Any(alternative => alternative.Schemes.Count == 0)
            )
            {
                if (!requiresAuthentication)
                {
                    return null;
                }

                var admits =
                    inherited ? "the document's default security admits anonymous callers"
                    : requirements.Alternatives.Count == 0
                        ? spec.AttributesOn is { } anonymousOn
                                ? $"{anonymousOn}'s security is anonymous"
                            : "the contract says .Anonymous()"
                    : spec.AttributesOn is { } optionalOn
                        ? $"{optionalOn}'s security says [RivetOrAnonymous]"
                    : "the contract says .OrAnonymous()";
                return Warning(
                    CoverageWarningKind.SecurityMismatch,
                    $"{spec.Label}: {admits}, but the handler requires authentication ({why}), "
                        + "so an anonymous caller gets a 401. Let anonymous callers in with "
                        + "[AllowAnonymous] (.AllowAnonymous() on a minimal API), or stop "
                        + "admitting them in the spec."
                );
            }

            if (requiresAuthentication)
            {
                return null;
            }

            var requires = inherited
                ? "the spec requires credentials (the document's default security)"
                : "the spec requires credentials";
            return Warning(
                CoverageWarningKind.SecurityMismatch,
                $"{spec.Label}: {requires}, but anyone can call the handler ({why}). "
                    + "Authorize it ([Authorize], .RequireAuthorization() on it or its route "
                    + "group or MapControllers(), an AuthorizeFilter in MvcOptions.Filters, a "
                    + "fallback policy, or a Functions trigger level other than Anonymous), or "
                    + $"{anonymous}."
            );

            CoverageWarning Warning(CoverageWarningKind kind, string message) =>
                new(
                    kind,
                    spec.ContractName,
                    spec.FieldName,
                    Expected: null,
                    Actual: null,
                    location,
                    Message: message
                );
        }

        /// <summary>
        /// The one RIV4008 for every handler that requires authentication under a spec that
        /// says nothing, when the document declares no security scheme at all.
        /// </summary>
        public CoverageWarning? Summary()
        {
            if (_undeclared.Count == 0)
            {
                return null;
            }

            _undeclared.Sort(StringComparer.Ordinal);
            return new CoverageWarning(
                CoverageWarningKind.SecurityMismatch,
                ContractName: "",
                FieldName: "",
                Expected: null,
                Actual: null,
                Location: null,
                Message: $"{_undeclared.Count} endpoint(s) require authentication in their "
                    + "handlers, but the document declares no security scheme, so the spec says "
                    + "anyone may call them: "
                    + string.Join(", ", _undeclared)
                    + ". Declare the document's default security in source, so generation and "
                    + "--check read the same one: [assembly: RivetSecurityScheme(...)] for the "
                    + "scheme, then [assembly: RivetGlobalSecurity(0)] and "
                    + "[assembly: RivetGlobalSecurityScheme(0, \"<scheme>\", new string[0])]. "
                    + "Then mark the endpoints anyone may call .Anonymous() ([RivetAnonymous] "
                    + "where the security is declared with attributes)."
            );
        }
    }

    /// <summary>
    /// RIV4005: each value the handler binds from route, query, header or form (a parameter,
    /// or a member of a complex parameter bound member by member, at any depth) whose enum
    /// advertises a wire value the host's enum parser refuses. What reaches which parser is
    /// <see cref="ParameterBindingSources"/>' call. A handler binding a string and parsing it
    /// itself is never reported: it binds no enum.
    /// </summary>
    private static IEnumerable<CoverageWarning> UnbindableEnumParameters(
        WellKnownTypes wkt,
        IFieldSymbol field,
        TsEndpointDefinition endpoint,
        IMethodSymbol handler,
        ParameterBindingHost host,
        bool infersBody
    )
    {
        foreach (var parameter in handler.Parameters)
        {
            foreach (
                var (path, unbindable) in EnumParameterBinding.FindUnbindableValues(
                    wkt,
                    parameter,
                    host,
                    infersBody
                )
            )
            {
                yield return new CoverageWarning(
                    CoverageWarningKind.UnbindableEnumParameter,
                    field.ContainingType.Name,
                    field.Name,
                    Expected: unbindable.WireValue,
                    Actual: unbindable.MemberName,
                    Location: parameter.Locations.FirstOrDefault(),
                    Message: $"{field.ContainingType.Name}.{field.Name} "
                        + $"({endpoint.HttpMethod} {endpoint.RouteTemplate}): "
                        + EnumParameterBinding.Explain(host, path, unbindable)
                );
            }
        }
    }

    /// <summary>
    /// The endpoints <paramref name="terminal"/> implements: its MVC action or Azure Function,
    /// or else one per mapping of the minimal-API handler it's returned from, each Map* call
    /// in each route-group chain it's made in (<see cref="BuilderOrigins.Chains"/>). When
    /// there's more than one, an unresolved route says which mapping it is.
    /// </summary>
    private static IReadOnlyList<EndpointContext> ResolveImplementations(
        WellKnownTypes wkt,
        BuilderOrigins origins,
        INamedTypeSymbol? adapterType,
        MapHandlerIndex mapHandlers,
        IInvocationOperation terminal,
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap,
        string functionsRoutePrefix
    )
    {
        if (adapterType is null)
        {
            return [];
        }

        if (ReturningMethod(terminal, "ToActionResult", adapterType) is { } method)
        {
            var controller = TryResolveController(wkt, method);
            if (controller.IsEndpoint)
            {
                return [controller];
            }

            var function = TryResolveFunction(wkt, method, functionsRoutePrefix);
            if (function.IsEndpoint)
            {
                return [function];
            }
        }

        var contexts = MapHandler(mapHandlers, terminal)
            .Where(handler =>
                IsReturnedThroughAdapter(terminal, handler.Body, "ToResult", adapterType)
            )
            .SelectMany(handler =>
                origins
                    .Chains(handler.MapCall)
                    .Select(chain => TryResolveMinimalApi(wkt, handler, chain, fieldMap))
            )
            .Where(context => context.IsEndpoint)
            .ToList();
        return contexts.Count > 1
            ?
            [
                .. contexts.Select(context =>
                    context is { RouteError: { } routeError, Chain: { } chain }
                        ? context with
                        {
                            RouteError = $"{routeError} ({Mapping(chain, null)})",
                        }
                        : context
                ),
            ]
            : contexts;
    }

    /// <summary>
    /// Which mapping of a handler mapped more than once a warning is about: its route, or
    /// where --check lost the trace when that can't be read.
    /// </summary>
    private static string Mapping(BuilderChain chain, string? route) =>
        route is not null ? $"as mapped at {route}"
        : chain.Unresolved
            ? $"as mapped on '{chain.End.Syntax}' at {chain.End.Syntax.SourcePosition()}"
        : $"as mapped by the {chain.MapCall.TargetMethod.Name} call at {chain.MapCall.Syntax.SourcePosition()}";

    /// <summary>A chain's route from constants alone: its prefix and its Map* call's template.</summary>
    private static string? MappedRoute(BuilderChain chain) =>
        chain.Prefix() is { } prefix
        && Argument(chain.MapCall, "pattern")?.ConstantValue
            is { HasValue: true, Value: string template }
            ? TransportIdentity.NormalizeRoute($"{prefix}/{template.Trim('/')}")
            : null;

    /// <summary>
    /// The method declaring the terminal and its operation body, when the method returns
    /// a value built by the named adapter from the terminal.
    /// </summary>
    private static IMethodSymbol? ReturningMethod(
        IInvocationOperation terminal,
        string adapterName,
        INamedTypeSymbol adapterType
    )
    {
        var method = terminal.Syntax.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return
            method is not null
            && terminal.SemanticModel is { } semanticModel
            && semanticModel.GetDeclaredSymbol(method)
                is IMethodSymbol { ReturnsVoid: false } methodSymbol
            && semanticModel.GetOperation(method) is { } body
            && IsReturnedThroughAdapter(terminal, body, adapterName, adapterType)
            ? methodSymbol
            : null;
    }

    private static EndpointContext TryResolveController(WellKnownTypes wkt, IMethodSymbol method)
    {
        if (EndpointWalker.HostOf(method) != ParameterBindingHost.Mvc)
        {
            return EndpointContext.None;
        }

        var (httpMethod, route) = EndpointWalker.ResolveActionRoute(wkt, method);
        if (httpMethod is null)
        {
            return EndpointContext.None;
        }

        // An MVC action with no statically resolvable route ([HttpGet] with no
        // template on a controller without [Route]) is unresolved, not verified:
        // report it through the RouteError channel so BuildWarnings emits a warning
        // (the requested check exits nonzero), mirroring the minimal-API unresolved
        // reporting. The route itself comes from the same resolver extraction uses.
        var context = route is null
            ? new EndpointContext(
                true,
                [httpMethod],
                null,
                RouteError: "unresolved route: the action declares no route template and the controller declares no [Route]"
            )
            : new EndpointContext(true, [httpMethod], route);
        return context with
        {
            Handler = method,
            BindingHost = ParameterBindingHost.Mvc,
            InfersBody = EnumParameterBinding.MvcInfersBody(wkt, method.ContainingType),
        };
    }

    private static EndpointContext TryResolveMinimalApi(
        WellKnownTypes wkt,
        MappedHandler handler,
        BuilderChain chain,
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap
    )
    {
        var mapCall = handler.MapCall;

        IReadOnlyList<string> methods;
        if (_minimalApiMethodMap.TryGetValue(mapCall.TargetMethod.Name, out var verb))
        {
            methods = [verb];
        }
        else if (mapCall.TargetMethod.Name == "MapMethods")
        {
            // Constraint: only the MapMethods nonconstant branch may leave the method
            // axis unresolved — Functions triggers legitimately declare no methods
            // (any-method), so they must not produce an unresolved-method state.
            var constantMethods = Argument(mapCall, "httpMethods") is { } httpMethods
                ? httpMethods
                    .DescendantsAndSelf()
                    .Select(operation => operation.ConstantValue)
                    .Where(constant => constant is { HasValue: true, Value: string })
                    .Select(constant => (string)constant.Value!)
                    .Distinct()
                    .ToList()
                : [];
            if (constantMethods.Count == 0)
            {
                return new EndpointContext(
                    true,
                    [],
                    null,
                    RouteError: "unresolved HTTP method(s): MapMethods arguments are not compile-time constants"
                )
                {
                    Handler = handler.Symbol,
                    BindingHost = ParameterBindingHost.MinimalApi,
                    InfersBody = true,
                    MapCall = mapCall,
                    Chain = chain,
                };
            }

            methods = constantMethods;
        }
        else
        {
            return EndpointContext.None;
        }

        string? route = null;
        string? routeError;
        if (Argument(mapCall, "pattern") is not { } pattern)
        {
            routeError = "unresolved route: Map* call has no route argument";
        }
        else if (ResolveRouteArgument(wkt, pattern, fieldMap) is not { } template)
        {
            // Interpolation, concatenation, method calls and a .Route on anything but
            // a contract field stay unresolved rather than guessing a route.
            routeError =
                "unresolved route: the route is neither a compile-time constant nor a contract field's .Route";
        }
        else
        {
            // Constant MapGroup receiver chains: prepend accumulated group prefixes
            // (nested groups accumulate). Unresolvable receivers keep the route
            // unresolved rather than inventing a prefix.
            var groupPrefix = chain.Prefix();
            routeError = groupPrefix is null
                ? "unresolved route: receiver chain does not resolve to constant MapGroup prefixes"
                : null;
            route = groupPrefix is null
                ? null
                : TransportIdentity.NormalizeRoute($"{groupPrefix}/{template.Trim('/')}");
        }

        // The method-mismatch check compares verbs case-blind, but body inference reads them
        // as written: ASP.NET matches the body-less verbs ordinally, so a MapMethods "get"
        // still infers a body.
        var verbs = methods.Select(value => value.ToUpperInvariant()).ToArray();
        return new EndpointContext(true, verbs, route, routeError)
        {
            Handler = handler.Symbol,
            BindingHost = ParameterBindingHost.MinimalApi,
            InfersBody = EnumParameterBinding.MinimalApiInfersBody(methods),
            MapCall = mapCall,
            Chain = chain,
        };
    }

    /// <summary>
    /// Every Map* call's handler, keyed by the syntax of the function it runs: a lambda, a
    /// local holding one, a local function, or a method named as a method group
    /// (<c>app.MapGet(route, TaskHandlers.Get)</c>) that has source in the compilation. A
    /// function mapped more than once has an entry per Map* call. A method group naming a
    /// method without source has no body to read, so it's left out: no terminal can be found
    /// in it anyway.
    /// </summary>
    private static MapHandlerIndex IndexMapHandlers(Compilation compilation, WellKnownTypes wkt)
    {
        var index = new MapHandlerIndex();
        foreach (var tree in compilation.SyntaxTrees)
        {
            SemanticModel? semanticModel = null;
            foreach (
                var syntax in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            )
            {
                // Every Map* method's name says so; the semantic check below is the real one.
                if (
                    InvokedName(syntax) is not { } name
                    || !name.StartsWith("Map", StringComparison.Ordinal)
                )
                {
                    continue;
                }

                semanticModel ??= compilation.GetSemanticModel(tree);
                if (
                    semanticModel.GetOperation(syntax) is not IInvocationOperation call
                    || !SymbolEqualityComparer.Default.Equals(
                        call.TargetMethod.ContainingType,
                        wkt.EndpointRouteBuilderExtensions
                    )
                )
                {
                    continue;
                }

                foreach (var argument in call.Arguments)
                {
                    if (HandlerFunction(argument.Value) is not var (body, symbol))
                    {
                        continue;
                    }

                    var key = (body.Syntax.SyntaxTree, body.Syntax.Span);
                    if (!index.TryGetValue(key, out var mappings))
                    {
                        mappings = [];
                        index[key] = mappings;
                    }

                    mappings.Add(new MappedHandler(body, symbol, call));
                }
            }
        }

        return index;

        static string? InvokedName(InvocationExpressionSyntax syntax) =>
            syntax.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => null,
            };
    }

    /// <summary>
    /// The function a Map* handler argument runs and its symbol, through the delegate
    /// creation: a lambda (or a local holding one), or a method group's method with source.
    /// </summary>
    private static (IOperation Body, IMethodSymbol Symbol)? HandlerFunction(IOperation value) =>
        DelegateTarget(value, []) switch
        {
            IAnonymousFunctionOperation lambda => (lambda, lambda.Symbol),
            IMethodReferenceOperation { Method: var method } when method.SourceBody() is { } body =>
                (body, method),
            _ => null,
        };

    /// <summary>
    /// The Map* handlers <paramref name="operation"/> sits in: walking out from it, the first
    /// function some Map* call runs. It's the Map* lambda, not the nearest one: a terminal in
    /// a Match arm sits in a lambda of its own inside it.
    /// </summary>
    private static IReadOnlyList<MappedHandler> MapHandler(
        MapHandlerIndex mapHandlers,
        IOperation operation
    ) =>
        operation
            .Syntax.Ancestors()
            .Select(node => mapHandlers.GetValueOrDefault((node.SyntaxTree, node.Span)))
            .FirstOrDefault(mappings => mappings is not null)
        ?? [];

    /// <summary>
    /// The route handler <paramref name="operation"/> sits in: the function handed to Map*,
    /// or else its method when that's an MVC action or an Azure Function.
    /// </summary>
    private static IOperation? RouteHandler(
        WellKnownTypes wkt,
        MapHandlerIndex mapHandlers,
        IOperation operation,
        string functionsRoutePrefix
    )
    {
        if (MapHandler(mapHandlers, operation) is [var mapped, ..])
        {
            return mapped.Body;
        }

        var method = operation
            .Syntax.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        return
            method is not null
            && operation.SemanticModel is { } semanticModel
            && semanticModel.GetDeclaredSymbol(method) is IMethodSymbol symbol
            && (
                TryResolveController(wkt, symbol).IsEndpoint
                || TryResolveFunction(wkt, symbol, functionsRoutePrefix).IsEndpoint
            )
            ? semanticModel.GetOperation(method)
            : null;
    }

    /// <summary>
    /// The bindings <paramref name="handler"/> passes as an argument, an extension method's
    /// receiver included (classic or extension block), to a call within a value it returns, as in
    /// <c>return Done(endpoint.Refuse(r))</c>, or uses as the receiver of a terminal it passes
    /// that way, as in <c>return Done(endpoint.Success(x))</c>. The returned values are
    /// <see cref="ReturnedValues"/>'s, so Match arms count, held in a local or not. Inside them
    /// each call argument is searched through what <see cref="LeavesOf"/> looks through and
    /// through a local holding a call, but never into a lambda, so <c>Ok(items.Select(...))</c>
    /// doesn't count. A terminal handed straight to a returned Rivet adapter is the terminal
    /// path's business, not a helper's: a handler returning <c>endpoint.Success(x).ToResult()</c>
    /// has used its binding only if that's its host's adapter, which the terminal path decides.
    /// A helper's answer handed to the adapter, as in <c>NotFound(endpoint).ToResult()</c>, is
    /// still searched. Passing a binding makes it used, so it isn't orphaned. Covering a
    /// field is the terminal path's call: a terminal the handler returns through the adapter,
    /// itself or through a helper that adapts it (<see cref="IsReturnedThroughAdapter"/>).
    /// </summary>
    private static IEnumerable<SyntaxNode> BindingsPassedOnReturn(
        WellKnownTypes wkt,
        INamedTypeSymbol? adapterType,
        IOperation handler,
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap
    )
    {
        HashSet<ILocalSymbol> visitedLocals = [];
        return ReturnedValues(handler)
            .SelectMany(value =>
                value is IInvocationOperation { Arguments: [var receiver, ..] } adapter
                && SymbolEqualityComparer.Default.Equals(
                    adapter.TargetMethod.ContainingType,
                    adapterType
                )
                    ? Passed(receiver.Value)
                    : Passed(value)
            );

        IEnumerable<SyntaxNode> Passed(IOperation value) =>
            LeavesOf(value)
                .SelectMany(leaf =>
                    leaf switch
                    {
                        IInvocationOperation call => PassedArguments(call)
                            .SelectMany(passed => Argument(passed.Value)),
                        // Marked only once resolved: a use outside the declaring block may not
                        // resolve where a later one does.
                        ILocalReferenceOperation local
                            when ProvenanceValue(local) is { } provenance
                                && visitedLocals.Add(local.Local) => Passed(provenance),
                        _ => [],
                    }
                );

        IEnumerable<SyntaxNode> Argument(IOperation value) =>
            LeavesOf(value)
                .SelectMany(leaf =>
                    ResolveContractReference(wkt, leaf, fieldMap, []) is { Binding: { } binding }
                        ? [binding]
                    : leaf is IInvocationOperation { Instance: { } receiver } terminal
                    && IsTerminal(wkt, terminal)
                        ? Argument(receiver).Concat(Passed(leaf))
                    : Passed(leaf)
                );
    }

    private static EndpointContext TryResolveFunction(
        WellKnownTypes wkt,
        IMethodSymbol method,
        string functionsRoutePrefix
    )
    {
        if (wkt.HttpTrigger is null)
        {
            return EndpointContext.None;
        }

        var trigger = method
            .Parameters.SelectMany(parameter => parameter.GetAttributes())
            .FirstOrDefault(attribute => attribute.Is(wkt.HttpTrigger));
        var function = method.GetAttribute(wkt.Function);
        if (trigger is null || function is null)
        {
            return EndpointContext.None;
        }

        var methods = trigger
            .ConstructorArguments.SelectMany(argument => argument.Strings())
            .Select(value => value.ToUpperInvariant())
            .ToArray();
        var route =
            trigger.NamedArgument("Route") as string ?? function.StringArgument() ?? method.Name;

        return new EndpointContext(
            true,
            methods,
            TransportIdentity.NormalizeRoute($"{functionsRoutePrefix.Trim('/')}/{route.Trim('/')}"),
            route.StartsWith('/')
                ? $"Invalid Functions trigger route '{route}': remove the leading slash"
                : null
        )
        {
            Handler = method,
            BindingHost = ParameterBindingHost.Functions,
        };
    }

    /// <summary>
    /// True when <paramref name="function"/> returns, from a reachable return of its own,
    /// the named Rivet adapter applied to <paramref name="terminal"/>, or hands the terminal
    /// to a helper that does. Both the returned value and the adapter's receiver are expanded
    /// by <see cref="ValuesOf"/>, and the receiver also through a local holding it. A helper is
    /// any called method with source in the compilation, a local function included: it counts
    /// when one of its own returned values is the adapter applied to the parameter the terminal
    /// was passed in, as in <c>Done(r) =&gt; r.ToResult()</c>, or hands that parameter on to a
    /// helper that does. Helpers are followed transitively, each parameter once. One that
    /// throws the terminal away, or adapts something else, gives nothing.
    /// </summary>
    private static bool IsReturnedThroughAdapter(
        IInvocationOperation terminal,
        IOperation function,
        string adapterName,
        INamedTypeSymbol adapterType
    ) =>
        Adapts(
            function,
            value => IsSameSyntax(value.Syntax, terminal.Syntax),
            adapterName,
            adapterType,
            new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default)
        );

    private static bool Adapts(
        IOperation function,
        Func<IOperation, bool> isTarget,
        string adapterName,
        INamedTypeSymbol adapterType,
        HashSet<IParameterSymbol> followed
    ) =>
        ReturnedValues(function)
            .OfType<IInvocationOperation>()
            .Any(call =>
                SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, adapterType)
                    ? call.TargetMethod.Name == adapterName
                        && call.Arguments is [var receiver, ..]
                        && ResolvesTo(receiver.Value, isTarget, [])
                    : PassedArguments(call)
                        .Any(passed =>
                            ResolvesTo(passed.Value, isTarget, [])
                            && followed.Add(passed.Parameter.OriginalDefinition)
                            && call.TargetMethod.SourceBody() is { } helper
                            && Adapts(
                                helper,
                                value => IsParameter(value, passed.Parameter),
                                adapterName,
                                adapterType,
                                followed
                            )
                        )
            );

    private static bool ResolvesTo(
        IOperation value,
        Func<IOperation, bool> isTarget,
        HashSet<ILocalSymbol> visitedLocals
    ) =>
        ValuesOf(value)
            .Any(candidate =>
                isTarget(candidate)
                || candidate is ILocalReferenceOperation local
                    && visitedLocals.Add(local.Local)
                    && ProvenanceValue(local) is { } provenance
                    && ResolvesTo(provenance, isTarget, visitedLocals)
            );

    private static bool IsParameter(IOperation value, IParameterSymbol parameter) =>
        value is IParameterReferenceOperation reference
        && SymbolEqualityComparer.Default.Equals(
            reference.Parameter.OriginalDefinition,
            parameter.OriginalDefinition
        );

    /// <summary>
    /// Each value <paramref name="call"/> passes and the parameter it lands in. A classic
    /// extension method's receiver is its first argument; a C# 14 extension block's is the
    /// call's instance, landing in the block's parameter.
    /// </summary>
    private static IEnumerable<(IParameterSymbol Parameter, IOperation Value)> PassedArguments(
        IInvocationOperation call
    ) =>
        call
            .Arguments.Where(argument => argument.Parameter is not null)
            .Select(argument => (argument.Parameter!, argument.Value))
            .Concat(
                call
                    is {
                        TargetMethod.ContainingType:
                        { IsExtension: true, ExtensionParameter: { } extensionParameter },
                        Instance: { } receiver,
                    }
                    ? [(extensionParameter, receiver)]
                    : []
            );

    /// <summary>The values <paramref name="function"/> returns from its own reachable returns.</summary>
    internal static IEnumerable<IOperation> ReturnedValues(IOperation function)
    {
        // An iterator, so each enumeration starts with nothing expanded.
        foreach (
            var value in ReturnedValues(
                function,
                new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default)
            )
        )
        {
            yield return value;
        }
    }

    /// <summary>
    /// <see cref="ReturnedValues(IOperation)"/>, with the local functions already expanded:
    /// one reached again, as in <c>IResult Attempt() => Retry(Attempt);</c>, adds nothing new.
    /// </summary>
    private static IEnumerable<IOperation> ReturnedValues(
        IOperation function,
        HashSet<IMethodSymbol> expanded
    ) =>
        OwnDescendants(function)
            .OfType<IReturnOperation>()
            .Where(returned =>
                returned.Kind == OperationKind.Return
                && (
                    returned.IsImplicit
                    || returned.SemanticModel?.AnalyzeControlFlow(returned.Syntax)
                        is { Succeeded: true, StartPointIsReachable: true }
                )
            )
            .SelectMany(returned => ValuesOf(returned.ReturnedValue, expanded));

    /// <summary>
    /// The leaf values an expression can produce: each switch arm and conditional branch
    /// (recursively), the operand of an <c>await</c> (through <c>ConfigureAwait</c>) or a
    /// cast, and for an invocation, the invocation itself plus whatever each function passed
    /// straight to it returns, such as the arms of <c>outcome.Match(ok => ..., error => ...)</c>.
    /// A function passed straight is a lambda, a local holding one, or a local function, as
    /// <see cref="FunctionOf"/> resolves it. A lambda buried deeper, as in
    /// <c>Ok(items.Select(item => ...))</c>, isn't followed.
    /// </summary>
    private static IEnumerable<IOperation> ValuesOf(IOperation? expression)
    {
        foreach (
            var value in ValuesOf(
                expression,
                new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default)
            )
        )
        {
            yield return value;
        }
    }

    private static IEnumerable<IOperation> ValuesOf(
        IOperation? expression,
        HashSet<IMethodSymbol> expanded
    ) =>
        LeavesOf(expression)
            .SelectMany(leaf =>
                leaf is IInvocationOperation invocation
                    ? invocation
                        .Arguments.Select(argument => FunctionOf(argument.Value, expanded))
                        .OfType<IOperation>()
                        .SelectMany(function => ReturnedValues(function, expanded))
                        .Prepend(invocation)
                    : [leaf]
            );

    /// <summary>
    /// <see cref="ValuesOf"/> without following lambdas: each switch arm and conditional
    /// branch, and the operand of an <c>await</c> (through <c>ConfigureAwait</c>) or a cast.
    /// </summary>
    private static IEnumerable<IOperation> LeavesOf(IOperation? expression) =>
        WithoutImplicitConversions(expression) switch
        {
            null => [],
            ISwitchExpressionOperation switchExpression => switchExpression.Arms.SelectMany(arm =>
                LeavesOf(arm.Value)
            ),
            IConditionalOperation conditional => LeavesOf(conditional.WhenTrue)
                .Concat(LeavesOf(conditional.WhenFalse)),
            IAwaitOperation awaited => LeavesOf(awaited.Operation),
            IConversionOperation conversion => LeavesOf(conversion.Operand),
            IInvocationOperation { TargetMethod.Name: "ConfigureAwait", Instance: { } awaitable } =>
                LeavesOf(awaitable),
            var value => [value],
        };

    /// <summary>
    /// The function an argument value is, through its delegate creation: a lambda, a local
    /// holding one (whose single value <see cref="ProvenanceValue"/> knows), or a local
    /// function named as a method group. Each is the handler's own code, so its returns are
    /// the handler's answer. A method group naming any other method is a helper. A local
    /// function already in <paramref name="expanded"/> resolves to nothing, so one that names
    /// itself is read once.
    /// </summary>
    private static IOperation? FunctionOf(IOperation value, HashSet<IMethodSymbol> expanded) =>
        DelegateTarget(value, []) switch
        {
            IAnonymousFunctionOperation lambda => lambda,
            IMethodReferenceOperation { Method: { MethodKind: MethodKind.LocalFunction } local }
                when expanded.Add(local.OriginalDefinition) => local.SourceBody(),
            _ => null,
        };

    /// <summary>
    /// What a delegate-typed value is made from, through its delegate creation, conversions
    /// and a local with a single known value: a lambda or a method group, or null.
    /// </summary>
    private static IOperation? DelegateTarget(
        IOperation value,
        HashSet<ILocalSymbol> visitedLocals
    ) =>
        value switch
        {
            IAnonymousFunctionOperation or IMethodReferenceOperation => value,
            IDelegateCreationOperation creation => DelegateTarget(creation.Target, visitedLocals),
            IConversionOperation conversion => DelegateTarget(conversion.Operand, visitedLocals),
            ILocalReferenceOperation local
                when visitedLocals.Add(local.Local) && ProvenanceValue(local) is { } provenance =>
                DelegateTarget(provenance, visitedLocals),
            _ => null,
        };

    /// <summary>Descendants of a function body, excluding nested functions' bodies.</summary>
    private static IEnumerable<IOperation> OwnDescendants(IOperation operation)
    {
        foreach (var child in operation.ChildOperations)
        {
            yield return child;
            if (child is not (IAnonymousFunctionOperation or ILocalFunctionOperation))
            {
                foreach (var descendant in OwnDescendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static IEnumerable<IOperation> Ancestors(IOperation operation)
    {
        for (var parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            yield return parent;
        }
    }

    /// <summary>
    /// Operations from separate GetOperation calls are separate trees, so identity is
    /// compared through the syntax they were bound from.
    /// </summary>
    private static bool IsSameSyntax(SyntaxNode left, SyntaxNode right) =>
        left.SyntaxTree == right.SyntaxTree && left.Span == right.Span;

    private static IOperation? Argument(IInvocationOperation invocation, string parameter) =>
        invocation
            .Arguments.FirstOrDefault(argument => argument.Parameter?.Name == parameter)
            ?.Value;

    private static IOperation? WithoutImplicitConversions(IOperation? operation) =>
        operation is IConversionOperation { IsImplicit: true } conversion
            ? WithoutImplicitConversions(conversion.Operand)
            : operation;

    private static bool IsOneOf(INamedTypeSymbol actual, params INamedTypeSymbol?[] candidates) =>
        candidates.Any(candidate => SymbolEqualityComparer.Default.Equals(actual, candidate));

    private static bool RoutesMatch(string contractRoute, string implRoute) =>
        string.Equals(
            TransportIdentity.NormalizeRoute(contractRoute),
            TransportIdentity.NormalizeRoute(implRoute),
            StringComparison.OrdinalIgnoreCase
        );

    private sealed record TerminalImplementation(SyntaxNode Invocation, EndpointContext Context);

    private sealed record ContractBinding(IFieldSymbol Field, SyntaxNode Invocation);

    private sealed record ContractReference(IFieldSymbol Field, SyntaxNode? Binding);

    /// <summary>
    /// A function a Map* call runs: its operation (a lambda's, a local function's or a method
    /// body's), its symbol, whose parameters are what the host binds, and that Map* call.
    /// </summary>
    private sealed record MappedHandler(
        IOperation Body,
        IMethodSymbol Symbol,
        IInvocationOperation MapCall
    );

    /// <summary>Map* handlers by the syntax of the function they run (<see cref="IndexMapHandlers"/>).</summary>
    private sealed class MapHandlerIndex
        : Dictionary<(SyntaxTree Tree, TextSpan Span), List<MappedHandler>>;

    /// <summary>
    /// A resolved implementation. <see cref="Handler"/> and <see cref="BindingHost"/> are set
    /// for every handler whose binding <see cref="ParameterBindingSources"/> knows: minimal
    /// APIs, MVC actions and Azure Functions.
    /// </summary>
    private sealed record EndpointContext(
        bool IsEndpoint,
        IReadOnlyList<string> HttpMethods,
        string? Route,
        string? RouteError = null
    )
    {
        public static readonly EndpointContext None = new(false, [], null);

        public IMethodSymbol? Handler { get; init; }

        public ParameterBindingHost? BindingHost { get; init; }

        /// <summary>
        /// Whether the host infers binding sources: <see cref="EnumParameterBinding.MinimalApiInfersBody"/>
        /// or <see cref="EnumParameterBinding.MvcInfersBody"/>. Unused for Azure Functions.
        /// </summary>
        public bool InfersBody { get; init; }

        /// <summary>A minimal API's Map* call, whose conventions K.5 reads.</summary>
        public IInvocationOperation? MapCall { get; init; }

        /// <summary>The mapping a minimal API's Map* call is made in: its route-group chain.</summary>
        public BuilderChain? Chain { get; init; }
    }
}
