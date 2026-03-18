using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Discovers [RivetEndpoint]-attributed methods and extracts HTTP method, route,
/// parameter bindings, and return type. Supports both minimal API (typed return)
/// and controller (ProducesResponseType + IActionResult) patterns.
/// </summary>
public static partial class EndpointWalker
{
    private static readonly HashSet<string> HttpMethodAttributes = new()
    {
        "Microsoft.AspNetCore.Mvc.HttpGetAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPostAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPutAttribute",
        "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPatchAttribute",
    };

    public static IReadOnlyList<TsEndpointDefinition> Walk(Compilation compilation, TypeWalker typeWalker)
    {
        var rivetEndpointAttr = compilation.GetTypeByMetadataName("Rivet.RivetEndpointAttribute");
        if (rivetEndpointAttr is null)
        {
            return [];
        }

        var endpoints = new List<TsEndpointDefinition>();

        foreach (var method in FindAttributedMethods(compilation, rivetEndpointAttr))
        {
            var endpoint = BuildEndpoint(method, typeWalker);
            if (endpoint is not null)
            {
                endpoints.Add(endpoint);
            }
        }

        return endpoints;
    }

    private static TsEndpointDefinition? BuildEndpoint(IMethodSymbol method, TypeWalker typeWalker)
    {
        var (httpMethod, methodRoute) = ExtractHttpMethodAndRoute(method);
        if (httpMethod is null)
        {
            return null;
        }

        // Combine controller [Route] prefix with method route
        var controllerRoute = ExtractControllerRoute(method.ContainingType);
        var fullRoute = CombineRoutes(controllerRoute, methodRoute);

        if (fullRoute is null)
        {
            return null;
        }

        // Strip route constraints: {id:guid} → {id}
        fullRoute = StripRouteConstraints(fullRoute);

        var parameters = ExtractParams(method, typeWalker, fullRoute);
        var returnType = ExtractReturnType(method, typeWalker);
        var name = ToCamelCase(method.Name);
        var controllerName = DeriveControllerFileName(method.ContainingType);

        return new TsEndpointDefinition(name, httpMethod, fullRoute, parameters, returnType, controllerName);
    }

    /// <summary>
    /// Derives a camelCase file name from the controller class.
    /// CaseStatusesController → caseStatuses, PublicFormsController → publicForms,
    /// Static class Endpoints → endpoints.
    /// </summary>
    private static string DeriveControllerFileName(INamedTypeSymbol? containingType)
    {
        if (containingType is null)
        {
            return "client";
        }

        var name = containingType.Name;

        // Strip "Controller" suffix
        if (name.EndsWith("Controller", StringComparison.Ordinal))
        {
            name = name[..^"Controller".Length];
        }

        return ToCamelCase(name);
    }

    private static (string? HttpMethod, string? Route) ExtractHttpMethodAndRoute(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            var attrName = attr.AttributeClass?.ToDisplayString();
            if (attrName is null || !HttpMethodAttributes.Contains(attrName))
            {
                continue;
            }

            var httpMethod = attrName switch
            {
                "Microsoft.AspNetCore.Mvc.HttpGetAttribute" => "GET",
                "Microsoft.AspNetCore.Mvc.HttpPostAttribute" => "POST",
                "Microsoft.AspNetCore.Mvc.HttpPutAttribute" => "PUT",
                "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute" => "DELETE",
                "Microsoft.AspNetCore.Mvc.HttpPatchAttribute" => "PATCH",
                _ => null,
            };

            // Route template is the first constructor argument (if any)
            var route = attr.ConstructorArguments.Length > 0
                ? attr.ConstructorArguments[0].Value as string
                : null;

            if (httpMethod is not null)
            {
                return (httpMethod, route);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Reads [Route("...")] from the containing controller class.
    /// </summary>
    private static string? ExtractControllerRoute(INamedTypeSymbol? containingType)
    {
        if (containingType is null)
        {
            return null;
        }

        foreach (var attr in containingType.GetAttributes())
        {
            var attrName = attr.AttributeClass?.ToDisplayString();
            if (attrName is "Microsoft.AspNetCore.Mvc.RouteAttribute" && attr.ConstructorArguments.Length > 0)
            {
                return attr.ConstructorArguments[0].Value as string;
            }
        }

        return null;
    }

    /// <summary>
    /// Combines controller route prefix with method route segment.
    /// e.g. "api/case-statuses" + "{id:guid}" → "/api/case-statuses/{id:guid}"
    /// </summary>
    private static string? CombineRoutes(string? controllerRoute, string? methodRoute)
    {
        // If method route starts with / it's absolute — use as-is
        if (methodRoute is not null && methodRoute.StartsWith('/'))
        {
            return methodRoute;
        }

        if (controllerRoute is null && methodRoute is null)
        {
            return null;
        }

        var prefix = string.IsNullOrEmpty(controllerRoute) ? null : controllerRoute.TrimStart('/').TrimEnd('/');
        var suffix = string.IsNullOrEmpty(methodRoute) ? null : methodRoute.TrimStart('/').TrimEnd('/');

        var combined = (prefix, suffix) switch
        {
            (not null, not null) => $"/{prefix}/{suffix}",
            (not null, null) => $"/{prefix}",
            (null, not null) => $"/{suffix}",
            _ => null,
        };

        return combined;
    }

    /// <summary>
    /// Strips route constraints: {id:guid} → {id}, {slug:minlength(3)} → {slug}
    /// </summary>
    private static string StripRouteConstraints(string route)
    {
        return RouteConstraintRegex().Replace(route, "{$1}");
    }

    private static IReadOnlyList<TsEndpointParam> ExtractParams(
        IMethodSymbol method,
        TypeWalker typeWalker,
        string? routeTemplate)
    {
        // Extract route param names from the template for implicit classification
        var routeParamNames = routeTemplate is not null
            ? RouteParamRegex().Matches(routeTemplate)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var parameters = new List<TsEndpointParam>();

        foreach (var param in method.Parameters)
        {
            var source = ClassifyParam(param, routeParamNames);
            if (source is null)
            {
                continue;
            }

            var tsType = typeWalker.MapTypePublic(param.Type);
            parameters.Add(new TsEndpointParam(param.Name, tsType, source.Value));
        }

        return parameters;
    }

    private static ParamSource? ClassifyParam(IParameterSymbol param, HashSet<string> routeParamNames)
    {
        // Explicit attribute takes precedence
        foreach (var attr in param.GetAttributes())
        {
            var name = attr.AttributeClass?.ToDisplayString();
            switch (name)
            {
                case "Microsoft.AspNetCore.Mvc.FromBodyAttribute":
                    return ParamSource.Body;
                case "Microsoft.AspNetCore.Mvc.FromQueryAttribute":
                    return ParamSource.Query;
                case "Microsoft.AspNetCore.Mvc.FromRouteAttribute":
                    return ParamSource.Route;
            }
        }

        // Implicit: param name matches a route template segment
        if (routeParamNames.Contains(param.Name))
        {
            return ParamSource.Route;
        }

        return null;
    }

    /// <summary>
    /// Extracts return type. Tries ProducesResponseType(typeof(T), 200) first (controllers),
    /// then falls back to method return type (minimal API).
    /// </summary>
    private static TsType? ExtractReturnType(IMethodSymbol method, TypeWalker typeWalker)
    {
        // Try ProducesResponseType first (controller pattern)
        var producesType = ExtractProducesResponseType(method);
        if (producesType is not null)
        {
            return typeWalker.MapTypePublic(producesType);
        }

        // Fall back to method return type (minimal API pattern)
        var returnType = method.ReturnType;

        // Unwrap Task<T> / ValueTask<T>
        if (returnType is INamedTypeSymbol namedReturn)
        {
            var displayName = namedReturn.OriginalDefinition.ToDisplayString();
            if (displayName is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                returnType = namedReturn.TypeArguments[0];
            }
            else if (displayName is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
            {
                return null;
            }
        }

        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return null;
        }

        // If it's IActionResult, we can't infer the type — skip
        var returnName = returnType.ToDisplayString();
        if (returnName is "Microsoft.AspNetCore.Mvc.IActionResult"
            or "Microsoft.AspNetCore.Mvc.ActionResult")
        {
            return null;
        }

        return typeWalker.MapTypePublic(returnType);
    }

    /// <summary>
    /// Finds [ProducesResponseType(typeof(T), 200)] on the method and returns T.
    /// Only considers 2xx status codes as the success response type.
    /// </summary>
    private static ITypeSymbol? ExtractProducesResponseType(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            var attrName = attr.AttributeClass?.ToDisplayString();
            if (attrName is not "Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute")
            {
                continue;
            }

            // ProducesResponseType(typeof(T), statusCode) — two constructor args
            if (attr.ConstructorArguments.Length >= 2
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol typeArg
                && attr.ConstructorArguments[1].Value is int statusCode
                && statusCode >= 200 && statusCode < 300)
            {
                return typeArg;
            }
        }

        return null;
    }

    private static IEnumerable<IMethodSymbol> FindAttributedMethods(
        Compilation compilation,
        INamedTypeSymbol attributeSymbol)
    {
        return GetAllTypes(compilation.GlobalNamespace)
            .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>())
            .Where(m => m.GetAttributes().Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol)));
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
        }

        foreach (var nested in ns.GetNamespaceMembers())
        {
            foreach (var type in GetAllTypes(nested))
            {
                yield return type;
            }
        }
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
        {
            return name;
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Matches route constraints like {id:guid}, {slug:minlength(3)}, {page:int}
    /// </summary>
    [GeneratedRegex(@"\{(\w+):[^}]+\}")]
    private static partial Regex RouteConstraintRegex();

    /// <summary>
    /// Matches route params: {id}, {id:guid}, {slug:minlength(3)}
    /// </summary>
    [GeneratedRegex(@"\{(\w+)(?::[^}]+)?\}")]
    private static partial Regex RouteParamRegex();
}
