using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

public enum CoverageWarningKind
{
    MissingImplementation,
    HttpMethodMismatch,
    RouteMismatch,
}

public sealed record CoverageWarning(
    CoverageWarningKind Kind,
    string ContractName,
    string FieldName,
    string? Expected,
    string? Actual,
    Location? Location);

public static class CoverageChecker
{
    private static readonly Dictionary<string, string> MinimalApiMethodMap = new(StringComparer.Ordinal)
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
        IReadOnlyList<TsEndpointDefinition> contractEndpoints)
    {
        // Step 1 — Build contract field symbol → endpoint map
        var contractAttr = compilation.GetTypeByMetadataName("Rivet.RivetContractAttribute");
        var defineType = compilation.GetTypeByMetadataName("Rivet.Define");

        if (contractAttr is null || defineType is null)
        {
            return [];
        }

        var fieldMap = new Dictionary<IFieldSymbol, TsEndpointDefinition>(SymbolEqualityComparer.Default);

        // Scope to source assembly — Rivet attributes only exist on user code
        foreach (var type in RoslynExtensions.GetAllTypes(compilation.Assembly.GlobalNamespace))
        {
            if (!type.GetAttributes().Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, contractAttr)))
            {
                continue;
            }

            if (type.IsAbstract && !type.IsStatic)
            {
                continue; // Abstract class contracts don't use RouteDefinition fields
            }

            var controllerName = ContractWalker.DeriveControllerName(type);

            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol field)
                {
                    continue;
                }

                if (!ContractWalker.IsRivetEndpointField(field.Type, defineType))
                {
                    continue;
                }

                var fieldName = Naming.ToCamelCase(field.Name);

                // Join to TsEndpointDefinition by controllerName + fieldName
                var endpoint = contractEndpoints.FirstOrDefault(e =>
                    e.ControllerName == controllerName && e.Name == fieldName);

                if (endpoint is not null)
                {
                    fieldMap[field] = endpoint;
                }
            }
        }

        if (fieldMap.Count == 0)
        {
            return [];
        }

        // Step 2 — Find all .Invoke() call sites
        var covered = new Dictionary<IFieldSymbol, List<InvocationExpressionSyntax>>(SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                {
                    continue;
                }

                if (memberAccess.Name.Identifier.Text != "Invoke")
                {
                    continue;
                }

                var symbolInfo = semanticModel.GetSymbolInfo(memberAccess.Expression);
                var receiverSymbol = symbolInfo.Symbol;

                if (receiverSymbol is IFieldSymbol receiverField && fieldMap.ContainsKey(receiverField))
                {
                    if (!covered.TryGetValue(receiverField, out var list))
                    {
                        list = [];
                        covered[receiverField] = list;
                    }

                    list.Add(invocation);
                }
            }
        }

        // Step 3 + 4 — Validate and produce warnings
        var warnings = new List<CoverageWarning>();

        foreach (var (field, endpoint) in fieldMap)
        {
            if (!covered.TryGetValue(field, out var invocations))
            {
                warnings.Add(new CoverageWarning(
                    CoverageWarningKind.MissingImplementation,
                    field.ContainingType.Name,
                    field.Name,
                    Expected: $"{endpoint.HttpMethod} {endpoint.RouteTemplate}",
                    Actual: "(none)",
                    Location: field.Locations.FirstOrDefault()));
                continue;
            }

            foreach (var invocation in invocations)
            {
                var semanticModel = compilation.GetSemanticModel(invocation.SyntaxTree);
                var (httpMethod, route) = ResolveImplementation(wkt, invocation, semanticModel);

                if (httpMethod is null && route is null)
                {
                    continue; // Can't determine context — skip validation
                }

                if (httpMethod is not null && !string.Equals(httpMethod, endpoint.HttpMethod, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(new CoverageWarning(
                        CoverageWarningKind.HttpMethodMismatch,
                        field.ContainingType.Name,
                        field.Name,
                        Expected: endpoint.HttpMethod,
                        Actual: httpMethod,
                        Location: invocation.GetLocation()));
                }

                if (route is not null && !RoutesMatch(endpoint.RouteTemplate, route))
                {
                    warnings.Add(new CoverageWarning(
                        CoverageWarningKind.RouteMismatch,
                        field.ContainingType.Name,
                        field.Name,
                        Expected: endpoint.RouteTemplate,
                        Actual: route,
                        Location: invocation.GetLocation()));
                }
            }
        }

        return warnings;
    }

    private static (string? HttpMethod, string? Route) ResolveImplementation(
        WellKnownTypes wkt,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        // Try controller path: walk up to containing method with HTTP attributes
        var controllerResult = TryResolveController(wkt, invocation, semanticModel);
        if (controllerResult.HttpMethod is not null)
        {
            return controllerResult;
        }

        // Try minimal API path: walk up to MapGet/MapPost/etc.
        return TryResolveMinimalApi(invocation, semanticModel);
    }

    private static (string? HttpMethod, string? Route) TryResolveController(
        WellKnownTypes wkt,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        var method = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method is null)
        {
            return (null, null);
        }

        if (semanticModel.GetDeclaredSymbol(method) is not IMethodSymbol methodSymbol)
        {
            return (null, null);
        }

        var (httpMethod, methodRoute) = EndpointWalker.ExtractHttpMethodAndRoute(wkt, methodSymbol);
        if (httpMethod is null)
        {
            return (null, null);
        }

        var controllerRoute = EndpointWalker.ExtractControllerRoute(wkt, methodSymbol.ContainingType);
        var fullRoute = EndpointWalker.CombineRoutes(controllerRoute, methodRoute);

        if (fullRoute is not null)
        {
            fullRoute = RouteParser.StripRouteConstraints(fullRoute);
        }

        return (httpMethod, fullRoute);
    }

    private static (string? HttpMethod, string? Route) TryResolveMinimalApi(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        // Walk up: Invoke() is inside a lambda → lambda is an argument → argument is in MapGet(...) call
        SyntaxNode? current = invocation;

        while (current is not null)
        {
            current = current.Parent;

            if (current is InvocationExpressionSyntax parentInvocation
                && parentInvocation.Expression is MemberAccessExpressionSyntax parentMemberAccess)
            {
                var methodName = parentMemberAccess.Name.Identifier.Text;

                if (MinimalApiMethodMap.TryGetValue(methodName, out var httpMethod))
                {
                    // Extract route from first argument
                    string? route = null;
                    if (parentInvocation.ArgumentList.Arguments.Count > 0)
                    {
                        var firstArg = parentInvocation.ArgumentList.Arguments[0].Expression;
                        var constValue = semanticModel.GetConstantValue(firstArg);
                        if (constValue is { HasValue: true, Value: string s })
                        {
                            route = NormalizeRoute(s);
                        }
                    }

                    return (httpMethod, route);
                }
            }
        }

        return (null, null);
    }

    private static bool RoutesMatch(string contractRoute, string implRoute)
    {
        return string.Equals(
            NormalizeRoute(contractRoute),
            NormalizeRoute(implRoute),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoute(string route)
    {
        route = RouteParser.StripRouteConstraints(route);
        return "/" + route.Trim('/');
    }

}
