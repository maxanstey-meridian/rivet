using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        IReadOnlyList<TsEndpointDefinition> contractEndpoints
    ) => Check(compilation, wkt, contractEndpoints, "api");

    public static IReadOnlyList<CoverageWarning> Check(
        Compilation compilation,
        WellKnownTypes wkt,
        IReadOnlyList<TsEndpointDefinition> contractEndpoints,
        string functionsRoutePrefix
    )
    {
        var fieldMap = BuildContractFieldMap(compilation, contractEndpoints);
        if (fieldMap.Count == 0)
        {
            return [];
        }

        var adapterType = compilation.GetTypeByMetadataName("Rivet.RivetResultExtensions");

        var implementations = new Dictionary<IFieldSymbol, List<TerminalImplementation>>(
            SymbolEqualityComparer.Default
        );
        var bindings = new List<ContractBinding>();
        var consumedBindings = new HashSet<InvocationExpressionSyntax>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (TryResolveBinding(wkt, invocation, semanticModel, fieldMap, out var binding))
                {
                    bindings.Add(binding);
                }

                if (!IsRivetTerminalInvocation(wkt, invocation, semanticModel))
                {
                    continue;
                }

                var receiver = ((MemberAccessExpressionSyntax)invocation.Expression).Expression;
                var contractReference = ResolveContractReference(
                    wkt,
                    receiver,
                    semanticModel,
                    fieldMap,
                    new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)
                );
                if (contractReference is null)
                {
                    continue;
                }

                var context = ResolveImplementation(
                    wkt,
                    adapterType,
                    invocation,
                    semanticModel,
                    functionsRoutePrefix
                );
                if (!context.IsEndpoint)
                {
                    continue;
                }

                if (contractReference.Binding is not null)
                {
                    consumedBindings.Add(contractReference.Binding);
                }

                var field = contractReference.Field;
                if (!implementations.TryGetValue(field, out var fieldImplementations))
                {
                    fieldImplementations = [];
                    implementations[field] = fieldImplementations;
                }

                fieldImplementations.Add(new TerminalImplementation(invocation, context));
            }
        }

        return BuildWarnings(fieldMap, implementations, bindings, consumedBindings);
    }

    private static Dictionary<IFieldSymbol, TsEndpointDefinition> BuildContractFieldMap(
        Compilation compilation,
        IReadOnlyList<TsEndpointDefinition> contractEndpoints
    )
    {
        var contractAttr = compilation.GetTypeByMetadataName("Rivet.RivetContractAttribute");
        var defineType = compilation.GetTypeByMetadataName("Rivet.Define");
        var fieldMap = new Dictionary<IFieldSymbol, TsEndpointDefinition>(
            SymbolEqualityComparer.Default
        );

        if (contractAttr is null || defineType is null)
        {
            return fieldMap;
        }

        foreach (var type in RoslynExtensions.GetAllTypes(compilation.Assembly.GlobalNamespace))
        {
            if (
                type.IsAbstract && !type.IsStatic
                || !type.GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, contractAttr))
            )
            {
                continue;
            }

            var controllerName = ContractWalker.DeriveControllerName(type);
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (!ContractWalker.IsRivetEndpointField(field.Type, defineType))
                {
                    continue;
                }

                var fieldName = Naming.ToCamelCase(field.Name);
                var endpoint = contractEndpoints.FirstOrDefault(e =>
                    e.ControllerName == controllerName && e.Name == fieldName
                );
                if (endpoint is not null)
                {
                    fieldMap[field] = endpoint;
                }
            }
        }

        return fieldMap;
    }

    private static bool IsRivetTerminalInvocation(
        WellKnownTypes wkt,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel
    )
    {
        if (
            invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.ValueText is not ("Success" or "Error" or "File")
            || semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
        )
        {
            return false;
        }

        return IsOneOf(
            method.ContainingType.OriginalDefinition,
            wkt.RouteDefinition,
            wkt.RouteDefinitionOfT,
            wkt.FileRouteDefinition,
            wkt.BoundRouteDefinition,
            wkt.BoundRouteDefinitionOfT,
            wkt.BoundFileRouteDefinition
        );
    }

    private static bool TryResolveBinding(
        WellKnownTypes wkt,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap,
        out ContractBinding binding
    )
    {
        binding = null!;
        if (!TryGetRivetBindReceiver(wkt, invocation, semanticModel, out var receiver))
        {
            return false;
        }

        var contractReference = ResolveContractReference(
            wkt,
            receiver,
            semanticModel,
            fieldMap,
            new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)
        );
        if (contractReference is null)
        {
            return false;
        }

        binding = new ContractBinding(contractReference.Field, invocation);
        return true;
    }

    private static ContractReference? ResolveContractReference(
        WellKnownTypes wkt,
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap,
        HashSet<ILocalSymbol> visitedLocals
    )
    {
        expression = Unwrap(expression);
        var symbol = semanticModel.GetSymbolInfo(expression).Symbol;
        if (symbol is IFieldSymbol field && fieldMap.ContainsKey(field))
        {
            return new ContractReference(field, null);
        }

        if (symbol is ILocalSymbol local)
        {
            if (
                !visitedLocals.Add(local)
                || !TryGetProvenanceValue(local, expression, semanticModel, out var value)
            )
            {
                return null;
            }

            return ResolveContractReference(wkt, value, semanticModel, fieldMap, visitedLocals);
        }

        if (
            expression is InvocationExpressionSyntax bindInvocation
            && TryGetRivetBindReceiver(wkt, bindInvocation, semanticModel, out var bindReceiver)
        )
        {
            var contractReference = ResolveContractReference(
                wkt,
                bindReceiver,
                semanticModel,
                fieldMap,
                visitedLocals
            );
            return contractReference is null
                ? null
                : contractReference with
                {
                    Binding = bindInvocation,
                };
        }

        return null;
    }

    private static bool TryGetRivetBindReceiver(
        WellKnownTypes wkt,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        out ExpressionSyntax receiver
    )
    {
        receiver = null!;
        if (
            invocation.Expression is not MemberAccessExpressionSyntax bindAccess
            || bindAccess.Name.Identifier.ValueText != "Bind"
            || semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol bindMethod
            || !IsOneOf(
                bindMethod.ContainingType.OriginalDefinition,
                wkt.RouteDefinitionOfTInputTOutput,
                wkt.InputRouteDefinitionOfT,
                wkt.FileRouteDefinitionOfT
            )
        )
        {
            return false;
        }

        receiver = bindAccess.Expression;
        return true;
    }

    private static bool TryGetProvenanceValue(
        ILocalSymbol local,
        ExpressionSyntax use,
        SemanticModel semanticModel,
        out ExpressionSyntax value
    )
    {
        value = null!;
        if (
            local.RefKind != RefKind.None
            || local.DeclaringSyntaxReferences is not [var syntaxReference]
            || syntaxReference.GetSyntax() is not VariableDeclaratorSyntax declarator
        )
        {
            return false;
        }

        var scope =
            declarator.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() as SyntaxNode
            ?? declarator.FirstAncestorOrSelf<LocalFunctionStatementSyntax>() as SyntaxNode
            ?? declarator.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>() as SyntaxNode;
        if (scope is null)
        {
            return false;
        }

        var assignments = scope
            .DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment =>
                SymbolEqualityComparer.Default.Equals(GetReferencedSymbol(assignment.Left), local)
            )
            .ToArray();

        foreach (var argument in scope.DescendantNodes().OfType<ArgumentSyntax>())
        {
            if (
                argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                && SymbolEqualityComparer.Default.Equals(
                    GetReferencedSymbol(argument.Expression),
                    local
                )
            )
            {
                return false;
            }
        }

        if (declarator.Initializer is { Value: var initializer })
        {
            if (assignments.Length != 0)
            {
                return false;
            }

            value = initializer;
            return true;
        }

        if (
            assignments
                is not [
                    {
                        RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                        Right: var assignedValue,
                        Parent: ExpressionStatementSyntax { Parent: BlockSyntax assignmentBlock },
                    } assignment,
                ]
            || declarator.Parent?.Parent
                is not LocalDeclarationStatementSyntax { Parent: BlockSyntax declarationBlock }
            || !ReferenceEquals(assignmentBlock, declarationBlock)
            || use.AncestorsAndSelf()
                .OfType<StatementSyntax>()
                .FirstOrDefault(statement => statement.Parent is BlockSyntax)
                is not { Parent: BlockSyntax useBlock } useStatement
            || !ReferenceEquals(useBlock, declarationBlock)
            || assignment.SpanStart <= declarator.Span.End
            || assignment.Span.End >= useStatement.SpanStart
        )
        {
            return false;
        }

        value = assignedValue;
        return true;

        ISymbol? GetReferencedSymbol(ExpressionSyntax expression) =>
            semanticModel.GetSymbolInfo(Unwrap(expression)).Symbol;
    }

    private static IReadOnlyList<CoverageWarning> BuildWarnings(
        IReadOnlyDictionary<IFieldSymbol, TsEndpointDefinition> fieldMap,
        IReadOnlyDictionary<IFieldSymbol, List<TerminalImplementation>> implementations,
        IReadOnlyList<ContractBinding> bindings,
        IReadOnlySet<InvocationExpressionSyntax> consumedBindings
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
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        string functionsRoutePrefix
    )
    {
        if (adapterType is null)
        {
            return EndpointContext.None;
        }

        var controller = TryResolveController(wkt, adapterType, invocation, semanticModel);
        if (controller.IsEndpoint)
        {
            return controller;
        }

        var function = TryResolveFunction(
            wkt,
            adapterType,
            invocation,
            semanticModel,
            functionsRoutePrefix
        );
        if (function.IsEndpoint)
        {
            return function;
        }

        return TryResolveMinimalApi(wkt, adapterType, invocation, semanticModel);
    }

    private static EndpointContext TryResolveController(
        WellKnownTypes wkt,
        INamedTypeSymbol adapterType,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel
    )
    {
        var method = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (
            method is null
            || semanticModel.GetDeclaredSymbol(method) is not IMethodSymbol methodSymbol
            || methodSymbol.ReturnsVoid
            || !IsReturnedThroughAdapter(
                invocation,
                method,
                "ToActionResult",
                adapterType,
                semanticModel
            )
        )
        {
            return EndpointContext.None;
        }

        var (httpMethod, methodRoute) = EndpointWalker.ExtractHttpMethodAndRoute(wkt, methodSymbol);
        if (httpMethod is null)
        {
            return EndpointContext.None;
        }

        var controllerRoute = EndpointWalker.ExtractControllerRoute(
            wkt,
            methodSymbol.ContainingType
        );
        var fullRoute = EndpointWalker.CombineRoutes(controllerRoute, methodRoute);
        if (fullRoute is null)
        {
            // An MVC action with no statically resolvable route ([HttpGet] with no
            // template on a controller without [Route]) is unresolved, not verified:
            // report it through the established RouteError channel so BuildWarnings
            // emits a warning (the requested check exits nonzero) and the
            // unresolved state stays distinguishable from a known-mismatched
            // route, mirroring the minimal-API unresolved reporting.
            return new EndpointContext(
                true,
                [httpMethod],
                null,
                RouteError: "unresolved route: the action declares no route template and the controller declares no [Route]"
            );
        }

        // A6: substitute [controller]/[action] tokens exactly like extraction
        // (EndpointWalker.BuildEndpoint) so extraction and coverage agree on the
        // same declared transport route.
        fullRoute = EndpointWalker.SubstituteRouteTokens(
            fullRoute,
            methodSymbol.ContainingType,
            methodSymbol
        );

        return new EndpointContext(
            true,
            [httpMethod],
            RouteParser.StripRouteConstraints(fullRoute)
        );
    }

    private static EndpointContext TryResolveMinimalApi(
        WellKnownTypes wkt,
        INamedTypeSymbol adapterType,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel
    )
    {
        var handler = invocation
            .Ancestors()
            .OfType<AnonymousFunctionExpressionSyntax>()
            .FirstOrDefault();
        if (
            handler is null
            || !ReferenceEquals(GetContainingFunction(invocation), handler)
            || !IsReturnedThroughAdapter(
                invocation,
                handler,
                "ToResult",
                adapterType,
                semanticModel
            )
            || handler.Parent
                is not ArgumentSyntax
                {
                    Parent: ArgumentListSyntax
                    {
                        Parent: InvocationExpressionSyntax parentInvocation
                    },
                }
            || parentInvocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || semanticModel.GetSymbolInfo(parentInvocation).Symbol is not IMethodSymbol method
            || !SymbolEqualityComparer.Default.Equals(
                (method.ReducedFrom ?? method).ContainingType,
                wkt.EndpointRouteBuilderExtensions
            )
        )
        {
            return EndpointContext.None;
        }

        IReadOnlyList<string> methods;
        if (_minimalApiMethodMap.TryGetValue(memberAccess.Name.Identifier.ValueText, out var verb))
        {
            methods = [verb];
        }
        else if (memberAccess.Name.Identifier.ValueText == "MapMethods")
        {
            // Constraint: only the MapMethods nonconstant branch may leave the method
            // axis unresolved — Functions triggers legitimately declare no methods
            // (any-method), so they must not produce an unresolved-method state.
            var constantMethods =
                parentInvocation.ArgumentList.Arguments.Count > 1
                    ? ExtractConstantStrings(
                        parentInvocation.ArgumentList.Arguments[1].Expression,
                        semanticModel
                    )
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

        var route = ExtractMinimalRoute(parentInvocation, semanticModel, out var routeError);
        if (route is null && routeError is null)
        {
            // An unresolvable receiver or nonconstant route stays unresolved rather
            // than inventing a prefix.
            routeError = "unresolved route: route template is not a compile-time constant";
        }
        else if (route is not null)
        {
            // Constant MapGroup receiver chains: prepend accumulated group prefixes
            // (nested groups accumulate). Unresolvable receivers keep the route
            // unresolved rather than inventing a prefix.
            var groupPrefix = ResolveMapGroupPrefix(
                ((MemberAccessExpressionSyntax)parentInvocation.Expression).Expression,
                semanticModel
            );
            if (groupPrefix.Unresolved)
            {
                routeError =
                    "unresolved route: receiver chain does not resolve to constant MapGroup prefixes";
                route = null;
            }
            else if (groupPrefix.Prefix.Length > 0)
            {
                route = NormalizeRoute($"{groupPrefix.Prefix.Trim('/')}/{route.Trim('/')}");
            }
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
        ExpressionSyntax receiver,
        SemanticModel semanticModel
    ) =>
        ResolveMapGroupPrefix(
            receiver,
            semanticModel,
            new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)
        );

    private static MapGroupPrefix ResolveMapGroupPrefix(
        ExpressionSyntax receiver,
        SemanticModel semanticModel,
        HashSet<ILocalSymbol> visitedLocals
    )
    {
        receiver = Unwrap(receiver);

        // A local provenance hop: var group = ...; group.MapGet(...)
        if (semanticModel.GetSymbolInfo(receiver).Symbol is ILocalSymbol local)
        {
            if (
                !visitedLocals.Add(local)
                || !TryGetProvenanceValue(local, receiver, semanticModel, out var value)
            )
            {
                return new MapGroupPrefix(Prefix: "", Unresolved: true);
            }

            return ResolveMapGroupPrefix(value, semanticModel, visitedLocals);
        }

        // Direct chained group: inner.MapGroup("/v3") — recurse into its receiver.
        if (
            receiver
                is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax groupAccess,
                    ArgumentList: { Arguments: [var patternArgument, ..] },
                } groupInvocation
            && groupAccess.Name.Identifier.ValueText == "MapGroup"
            && semanticModel.GetSymbolInfo(receiver).Symbol is IMethodSymbol groupMethod
            && SymbolEqualityComparer.Default.Equals(
                (groupMethod.ReducedFrom ?? groupMethod).ContainingType,
                semanticModel.Compilation.GetTypeByMetadataName(
                    "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions"
                )
            )
        )
        {
            var ownPrefix = semanticModel.GetConstantValue(patternArgument.Expression);
            if (ownPrefix is not { HasValue: true, Value: string prefixText })
            {
                return new MapGroupPrefix(Prefix: "", Unresolved: true);
            }

            var outer = ResolveMapGroupPrefix(groupAccess.Expression, semanticModel, visitedLocals);
            if (outer.Unresolved)
            {
                return outer;
            }

            return new MapGroupPrefix(
                Prefix: NormalizeRoute($"{outer.Prefix.Trim('/')}/{prefixText.Trim('/')}"),
                Unresolved: false
            );
        }

        // Root receiver (app / IEndpointRouteBuilder variable, or any other base) —
        // no prefix contribution; treat the chain as resolved at this point.
        return new MapGroupPrefix(Prefix: "", Unresolved: false);
    }

    private static EndpointContext TryResolveFunction(
        WellKnownTypes wkt,
        INamedTypeSymbol adapterType,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        string functionsRoutePrefix
    )
    {
        var method = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (
            method is null
            || semanticModel.GetDeclaredSymbol(method) is not IMethodSymbol methodSymbol
            || wkt.HttpTrigger is null
            || methodSymbol.ReturnsVoid
            || !IsReturnedThroughAdapter(
                invocation,
                method,
                "ToActionResult",
                adapterType,
                semanticModel
            )
        )
        {
            return EndpointContext.None;
        }

        var trigger = methodSymbol
            .Parameters.SelectMany(parameter => parameter.GetAttributes())
            .FirstOrDefault(attribute =>
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wkt.HttpTrigger)
            );
        var function = methodSymbol
            .GetAttributes()
            .FirstOrDefault(attribute =>
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, wkt.Function)
            );
        if (trigger is null || function is null)
        {
            return EndpointContext.None;
        }

        var methods = trigger
            .ConstructorArguments.SelectMany(ExtractStrings)
            .Select(value => value.ToUpperInvariant())
            .ToArray();
        var route =
            trigger.NamedArguments.FirstOrDefault(argument => argument.Key == "Route").Value.Value
            as string;
        if (route is null)
        {
            route =
                function.ConstructorArguments.FirstOrDefault().Value as string ?? methodSymbol.Name;
        }

        return new EndpointContext(
            true,
            methods,
            NormalizeRoute($"{functionsRoutePrefix.Trim('/')}/{route.Trim('/')}"),
            route.StartsWith('/')
                ? $"Invalid Functions trigger route '{route}': remove the leading slash"
                : null
        );
    }

    private static bool IsReturnedThroughAdapter(
        InvocationExpressionSyntax terminal,
        SyntaxNode function,
        string adapterName,
        INamedTypeSymbol adapterType,
        SemanticModel semanticModel
    )
    {
        foreach (
            var adapterInvocation in function.DescendantNodes().OfType<InvocationExpressionSyntax>()
        )
        {
            if (
                !ReferenceEquals(GetContainingFunction(adapterInvocation), function)
                || !TryGetRivetAdapterReceiver(
                    adapterInvocation,
                    adapterName,
                    adapterType,
                    semanticModel,
                    out var receiver
                )
                || !ReceiverResolvesToTerminal(receiver, terminal, semanticModel)
                || !IsReturnedFromFunction(adapterInvocation, function, semanticModel)
            )
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool TryGetRivetAdapterReceiver(
        InvocationExpressionSyntax invocation,
        string adapterName,
        INamedTypeSymbol adapterType,
        SemanticModel semanticModel,
        out ExpressionSyntax receiver
    )
    {
        receiver = null!;
        if (
            invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.ValueText != adapterName
            || semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || !SymbolEqualityComparer.Default.Equals(
                (method.ReducedFrom ?? method).ContainingType,
                adapterType
            )
        )
        {
            return false;
        }

        if (method.ReducedFrom is not null)
        {
            receiver = memberAccess.Expression;
            return true;
        }

        if (
            method.IsExtensionMethod
            && invocation.ArgumentList.Arguments is [{ Expression: var value }, ..]
        )
        {
            receiver = value;
            return true;
        }

        return false;
    }

    private static bool ReceiverResolvesToTerminal(
        ExpressionSyntax receiver,
        InvocationExpressionSyntax terminal,
        SemanticModel semanticModel
    )
    {
        receiver = Unwrap(receiver);
        if (IsSameExpression(receiver, terminal))
        {
            return true;
        }

        return semanticModel.GetSymbolInfo(receiver).Symbol is ILocalSymbol local
            && TryGetProvenanceValue(local, receiver, semanticModel, out var value)
            && IsSameExpression(Unwrap(value), terminal);
    }

    private static bool IsSameExpression(ExpressionSyntax left, ExpressionSyntax right) =>
        ReferenceEquals(left.SyntaxTree, right.SyntaxTree) && left.Span.Equals(right.Span);

    private static bool IsReturnedFromFunction(
        InvocationExpressionSyntax invocation,
        SyntaxNode function,
        SemanticModel semanticModel
    ) =>
        function switch
        {
            MethodDeclarationSyntax method => IsReturnedFromMethod(
                invocation,
                method,
                semanticModel
            ),
            AnonymousFunctionExpressionSyntax anonymous => IsReturnedFromAnonymousFunction(
                invocation,
                anonymous,
                semanticModel
            ),
            _ => false,
        };

    private static bool IsReturnedFromMethod(
        InvocationExpressionSyntax invocation,
        MethodDeclarationSyntax method,
        SemanticModel semanticModel
    )
    {
        if (!ReferenceEquals(GetContainingFunction(invocation), method))
        {
            return false;
        }

        return IsReturnedExpression(method.ExpressionBody?.Expression, invocation, semanticModel)
            || IsReturnedFromBlock(invocation, method, semanticModel);
    }

    private static bool IsReturnedFromAnonymousFunction(
        InvocationExpressionSyntax invocation,
        AnonymousFunctionExpressionSyntax function,
        SemanticModel semanticModel
    ) =>
        IsReturnedExpression(function.ExpressionBody, invocation, semanticModel)
        || IsReturnedFromBlock(invocation, function, semanticModel);

    private static bool IsReturnedFromBlock(
        InvocationExpressionSyntax invocation,
        SyntaxNode function,
        SemanticModel semanticModel
    )
    {
        var returnStatement = invocation
            .Ancestors()
            .OfType<ReturnStatementSyntax>()
            .FirstOrDefault();
        return returnStatement is not null
            && IsReturnedExpression(returnStatement.Expression, invocation, semanticModel)
            && ReferenceEquals(GetContainingFunction(returnStatement), function)
            && semanticModel.AnalyzeControlFlow(returnStatement)
                is { Succeeded: true, StartPointIsReachable: true };
    }

    private static bool IsReturnedExpression(
        ExpressionSyntax? returned,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel
    )
    {
        if (returned is null)
        {
            return false;
        }

        returned = Unwrap(returned);
        if (IsSameExpression(returned, invocation))
        {
            return true;
        }

        if (returned is SwitchExpressionSyntax switchExpression)
        {
            return switchExpression.Arms.Any(arm =>
                IsSameExpression(Unwrap(arm.Expression), invocation)
            );
        }

        if (returned is ConditionalExpressionSyntax conditional)
        {
            return IsSameExpression(Unwrap(conditional.WhenTrue), invocation)
                || IsSameExpression(Unwrap(conditional.WhenFalse), invocation);
        }

        return semanticModel.GetSymbolInfo(returned).Symbol is ILocalSymbol local
            && TryGetProvenanceValue(local, returned, semanticModel, out var value)
            && IsReturnedExpression(value, invocation, semanticModel);
    }

    private static SyntaxNode? GetContainingFunction(SyntaxNode node) =>
        node.Ancestors()
            .FirstOrDefault(ancestor =>
                ancestor
                    is AnonymousFunctionExpressionSyntax
                        or LocalFunctionStatementSyntax
                        or BaseMethodDeclarationSyntax
            );

    private static IEnumerable<string> ExtractStrings(TypedConstant constant)
    {
        if (constant.Kind == TypedConstantKind.Array)
        {
            return constant.Values.SelectMany(ExtractStrings);
        }

        return constant.Value is string value ? [value] : [];
    }

    private static IReadOnlyList<string> ExtractConstantStrings(
        ExpressionSyntax expression,
        SemanticModel semanticModel
    )
    {
        var values = new List<string>();
        foreach (var candidate in expression.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
        {
            var constant = semanticModel.GetConstantValue(candidate);
            if (constant is { HasValue: true, Value: string value } && !values.Contains(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string? ExtractMinimalRoute(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        out string? routeError
    )
    {
        routeError = null;
        if (invocation.ArgumentList.Arguments.Count == 0)
        {
            routeError = "unresolved route: Map* call has no route argument";
            return null;
        }

        var route = semanticModel.GetConstantValue(invocation.ArgumentList.Arguments[0].Expression);
        if (route is { HasValue: true, Value: string value })
        {
            return NormalizeRoute(value);
        }

        routeError = "unresolved route: route template is not a compile-time constant";
        return null;
    }

    private static bool IsOneOf(INamedTypeSymbol actual, params INamedTypeSymbol?[] candidates) =>
        candidates.Any(candidate => SymbolEqualityComparer.Default.Equals(actual, candidate));

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    break;
                case PostfixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.SuppressNullableWarningExpression,
                } suppressed:
                    expression = suppressed.Operand;
                    break;
                default:
                    return expression;
            }
        }
    }

    private static bool RoutesMatch(string contractRoute, string implRoute) =>
        string.Equals(
            NormalizeRoute(contractRoute),
            NormalizeRoute(implRoute),
            StringComparison.OrdinalIgnoreCase
        );

    private static string NormalizeRoute(string route)
    {
        route = RouteParser.StripRouteConstraints(route);
        return "/" + route.Trim('/');
    }

    private sealed record TerminalImplementation(
        InvocationExpressionSyntax Invocation,
        EndpointContext Context
    );

    private sealed record ContractBinding(
        IFieldSymbol Field,
        InvocationExpressionSyntax Invocation
    );

    private sealed record ContractReference(
        IFieldSymbol Field,
        InvocationExpressionSyntax? Binding
    );

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
