using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

public enum CoverageWarningKind
{
    MissingImplementation,
    OrphanedBinding,
    HttpMethodMismatch,
    RouteMismatch,
}

public sealed record CoverageWarning(
    CoverageWarningKind Kind,
    string ContractName,
    string FieldName,
    string? Expected,
    string? Actual,
    Location? Location
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
        IReadOnlyList<ContractEndpoint> contractEndpoints,
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
        if (fieldMap.Count == 0)
        {
            return [];
        }

        var adapterType = compilation.GetTypeByMetadataName("Rivet.RivetResultExtensions");

        var implementations = new Dictionary<IFieldSymbol, List<TerminalImplementation>>(
            SymbolEqualityComparer.Default
        );
        var bindings = new List<ContractBinding>();
        var consumedBindings = new HashSet<SyntaxNode>();

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
                }

                if (
                    !IsTerminal(wkt, invocation)
                    || ResolveContractReference(wkt, invocation.Instance, fieldMap, [])
                        is not { } reference
                )
                {
                    continue;
                }

                var context = ResolveImplementation(
                    wkt,
                    adapterType,
                    invocation,
                    functionsRoutePrefix
                );
                if (!context.IsEndpoint)
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

                fieldImplementations.Add(new TerminalImplementation(syntax, context));
            }
        }

        return BuildWarnings(fieldMap, implementations, bindings, consumedBindings);
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
    /// The single value a local holds at <paramref name="use"/>, or null when it cannot
    /// be known statically: the local is ref, passed by ref/out, or assigned more than
    /// its initializer. A local declared without an initializer qualifies only through
    /// one simple assignment statement in the declaring block, between the declaration
    /// and the statement using it.
    /// </summary>
    private static IOperation? ProvenanceValue(ILocalReferenceOperation use)
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
            ?? declaratorSyntax.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
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
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap,
        IReadOnlyDictionary<IFieldSymbol, List<TerminalImplementation>> implementations,
        IReadOnlyList<ContractBinding> bindings,
        IReadOnlySet<SyntaxNode> consumedBindings
    )
    {
        var warnings = new List<CoverageWarning>();
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

            foreach (var implementation in fieldImplementations)
            {
                if (
                    implementation.Context.HttpMethods.Count > 0
                    && !implementation.Context.HttpMethods.Contains(
                        endpoint.HttpMethod,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    warnings.Add(
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
                    warnings.Add(
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
                    Expected: "returned terminal implementation",
                    Actual: "(none)",
                    Location: binding.Invocation.GetLocation()
                )
            );
        }

        return warnings;
    }

    private static EndpointContext ResolveImplementation(
        WellKnownTypes wkt,
        INamedTypeSymbol? adapterType,
        IInvocationOperation terminal,
        string functionsRoutePrefix
    )
    {
        if (adapterType is null)
        {
            return EndpointContext.None;
        }

        var controller = TryResolveController(wkt, adapterType, terminal);
        if (controller.IsEndpoint)
        {
            return controller;
        }

        var function = TryResolveFunction(wkt, adapterType, terminal, functionsRoutePrefix);
        if (function.IsEndpoint)
        {
            return function;
        }

        return TryResolveMinimalApi(wkt, adapterType, terminal);
    }

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

    private static EndpointContext TryResolveController(
        WellKnownTypes wkt,
        INamedTypeSymbol adapterType,
        IInvocationOperation terminal
    )
    {
        if (ReturningMethod(terminal, "ToActionResult", adapterType) is not { } method)
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
        return route is null
            ? new EndpointContext(
                true,
                [httpMethod],
                null,
                RouteError: "unresolved route: the action declares no route template and the controller declares no [Route]"
            )
            : new EndpointContext(true, [httpMethod], route);
    }

    private static EndpointContext TryResolveMinimalApi(
        WellKnownTypes wkt,
        INamedTypeSymbol adapterType,
        IInvocationOperation terminal
    )
    {
        if (
            Ancestors(terminal)
                .FirstOrDefault(operation =>
                    operation is IAnonymousFunctionOperation or ILocalFunctionOperation
                )
                is not IAnonymousFunctionOperation handler
            || !IsReturnedThroughAdapter(terminal, handler, "ToResult", adapterType)
            || Ancestors(handler)
                .FirstOrDefault(operation =>
                    operation is not (IDelegateCreationOperation or IConversionOperation)
                )
                is not IArgumentOperation { Parent: IInvocationOperation mapCall }
            || !SymbolEqualityComparer.Default.Equals(
                mapCall.TargetMethod.ContainingType,
                wkt.EndpointRouteBuilderExtensions
            )
        )
        {
            return EndpointContext.None;
        }

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
                );
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
        else if (pattern.ConstantValue is not { HasValue: true, Value: string template })
        {
            // An unresolvable receiver or nonconstant route stays unresolved rather
            // than inventing a prefix.
            routeError = "unresolved route: route template is not a compile-time constant";
        }
        else
        {
            // Constant MapGroup receiver chains: prepend accumulated group prefixes
            // (nested groups accumulate). Unresolvable receivers keep the route
            // unresolved rather than inventing a prefix.
            var groupPrefix = ResolveMapGroupPrefix(wkt, mapCall.Arguments[0].Value, []);
            routeError = groupPrefix.Unresolved
                ? "unresolved route: receiver chain does not resolve to constant MapGroup prefixes"
                : null;
            route = groupPrefix.Unresolved
                ? null
                : TransportIdentity.NormalizeRoute($"{groupPrefix.Prefix}/{template.Trim('/')}");
        }

        return new EndpointContext(
            true,
            methods.Select(value => value.ToUpperInvariant()).ToArray(),
            route,
            routeError
        );
    }

    /// <summary>
    /// Accumulated constant MapGroup prefixes for a minimal-API receiver, or an
    /// unresolved marker when the receiver chain cannot be resolved statically.
    /// Only compile-time-constant prefixes are supported (mirroring the extractor's
    /// constant-string policy); receivers that do not resolve keep the route
    /// unresolved instead of inventing a prefix.
    /// </summary>
    private readonly record struct MapGroupPrefix(string Prefix, bool Unresolved);

    private static MapGroupPrefix ResolveMapGroupPrefix(
        WellKnownTypes wkt,
        IOperation receiver,
        HashSet<ILocalSymbol> visitedLocals
    )
    {
        switch (WithoutImplicitConversions(receiver))
        {
            // A local provenance hop: var group = ...; group.MapGet(...)
            case ILocalReferenceOperation local:
                return visitedLocals.Add(local.Local) && ProvenanceValue(local) is { } value
                    ? ResolveMapGroupPrefix(wkt, value, visitedLocals)
                    : new MapGroupPrefix(Prefix: "", Unresolved: true);

            // Chained group: inner.MapGroup("/v3") — its prefix follows the receiver's.
            case IInvocationOperation { TargetMethod.Name: "MapGroup" } group
                when SymbolEqualityComparer.Default.Equals(
                    group.TargetMethod.ContainingType,
                    wkt.EndpointRouteBuilderExtensions
                ):
                if (
                    Argument(group, "prefix")?.ConstantValue
                    is not { HasValue: true, Value: string prefix }
                )
                {
                    return new MapGroupPrefix(Prefix: "", Unresolved: true);
                }

                var outer = ResolveMapGroupPrefix(wkt, group.Arguments[0].Value, visitedLocals);
                return outer.Unresolved
                    ? outer
                    : new MapGroupPrefix(
                        TransportIdentity.NormalizeRoute($"{outer.Prefix}/{prefix.Trim('/')}"),
                        Unresolved: false
                    );

            // Root receiver (app / IEndpointRouteBuilder, or any other base): no prefix.
            default:
                return new MapGroupPrefix(Prefix: "", Unresolved: false);
        }
    }

    private static EndpointContext TryResolveFunction(
        WellKnownTypes wkt,
        INamedTypeSymbol adapterType,
        IInvocationOperation terminal,
        string functionsRoutePrefix
    )
    {
        if (
            wkt.HttpTrigger is null
            || ReturningMethod(terminal, "ToActionResult", adapterType) is not { } method
        )
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
        );
    }

    /// <summary>
    /// True when <paramref name="function"/> returns, from a reachable return of its own,
    /// the named Rivet adapter applied to <paramref name="terminal"/> (directly or through
    /// a local holding it). The adapter call may be the returned value itself, a switch
    /// expression arm or a conditional branch.
    /// </summary>
    private static bool IsReturnedThroughAdapter(
        IInvocationOperation terminal,
        IOperation function,
        string adapterName,
        INamedTypeSymbol adapterType
    )
    {
        foreach (var returned in OwnDescendants(function).OfType<IReturnOperation>())
        {
            if (
                returned.Kind != OperationKind.Return
                || !returned.IsImplicit
                    && returned.SemanticModel?.AnalyzeControlFlow(returned.Syntax)
                        is not { Succeeded: true, StartPointIsReachable: true }
            )
            {
                continue;
            }

            foreach (var candidate in ReturnedValues(returned.ReturnedValue))
            {
                if (
                    candidate is IInvocationOperation { Arguments: [var receiver, ..] } adapter
                    && adapter.TargetMethod.Name == adapterName
                    && SymbolEqualityComparer.Default.Equals(
                        adapter.TargetMethod.ContainingType,
                        adapterType
                    )
                    && ResolvesTo(receiver.Value, terminal)
                )
                {
                    return true;
                }
            }
        }

        return false;

        static bool ResolvesTo(IOperation receiver, IInvocationOperation terminal) =>
            WithoutImplicitConversions(receiver) switch
            {
                { } value when IsSameSyntax(value.Syntax, terminal.Syntax) => true,
                ILocalReferenceOperation local => WithoutImplicitConversions(ProvenanceValue(local))
                    is { } value
                    && IsSameSyntax(value.Syntax, terminal.Syntax),
                _ => false,
            };
    }

    private static IEnumerable<IOperation> ReturnedValues(IOperation? returned)
    {
        switch (WithoutImplicitConversions(returned))
        {
            case null:
                yield break;
            case ISwitchExpressionOperation switchExpression:
                yield return switchExpression;
                foreach (var arm in switchExpression.Arms)
                {
                    yield return WithoutImplicitConversions(arm.Value)!;
                }
                break;
            case IConditionalOperation conditional:
                yield return conditional;
                yield return WithoutImplicitConversions(conditional.WhenTrue)!;
                if (conditional.WhenFalse is { } whenFalse)
                {
                    yield return WithoutImplicitConversions(whenFalse)!;
                }
                break;
            case var value:
                yield return value;
                break;
        }
    }

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

    private sealed record EndpointContext(
        bool IsEndpoint,
        IReadOnlyList<string> HttpMethods,
        string? Route,
        string? RouteError = null
    )
    {
        public static readonly EndpointContext None = new(false, [], null);
    }
}
