using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Discovers [RivetEndpoint]-attributed methods and extracts HTTP method, route,
/// parameter bindings, and return type. Supports both minimal API (typed return)
/// and controller (ProducesResponseType + IActionResult) patterns.
/// </summary>
public static class EndpointWalker
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
        var endpoints = new List<TsEndpointDefinition>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        // [RivetEndpoint] on individual methods
        var rivetEndpointAttr = compilation.GetTypeByMetadataName("Rivet.RivetEndpointAttribute");
        if (rivetEndpointAttr is not null)
        {
            foreach (var method in FindAttributedMethods(compilation, rivetEndpointAttr))
            {
                if (seen.Add(method))
                {
                    var endpoint = BuildEndpoint(method, typeWalker);
                    if (endpoint is not null)
                    {
                        endpoints.Add(endpoint);
                    }
                }
            }
        }

        // [RivetClient] on classes — all public methods with HTTP attributes
        var rivetClientAttr = compilation.GetTypeByMetadataName("Rivet.RivetClientAttribute");
        if (rivetClientAttr is not null)
        {
            foreach (var method in FindClientMethods(compilation, rivetClientAttr))
            {
                if (seen.Add(method))
                {
                    var endpoint = BuildEndpoint(method, typeWalker);
                    if (endpoint is not null)
                    {
                        endpoints.Add(endpoint);
                    }
                }
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
        var responses = ExtractAllResponseTypes(method, typeWalker);
        var returnType = ExtractReturnType(method, typeWalker);
        var name = Naming.ToCamelCase(method.Name);
        var controllerName = DeriveControllerFileName(method.ContainingType);

        return new TsEndpointDefinition(name, httpMethod, fullRoute, parameters, returnType, controllerName, responses);
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

        return Naming.ToCamelCase(name);
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
        return RouteParser.StripRouteConstraints(route);
    }

    private static IReadOnlyList<TsEndpointParam> ExtractParams(
        IMethodSymbol method,
        TypeWalker typeWalker,
        string? routeTemplate)
    {
        // Extract route param names from the template for implicit classification
        var routeParamNames = routeTemplate is not null
            ? RouteParser.ParseRouteParamNames(routeTemplate)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var parameters = new List<TsEndpointParam>();

        foreach (var param in method.Parameters)
        {
            var source = ClassifyParam(param, routeParamNames);
            if (source is null)
            {
                continue;
            }

            // IFormFile maps to the Web API File type — don't walk it through Roslyn
            var tsType = source == ParamSource.File
                ? new TsType.Primitive("File")
                : typeWalker.MapTypePublic(param.Type);
            parameters.Add(new TsEndpointParam(param.Name, tsType, source.Value));
        }

        return parameters;
    }

    private static readonly HashSet<string> FormFileTypeNames = new()
    {
        "Microsoft.AspNetCore.Http.IFormFile",
    };

    private static ParamSource? ClassifyParam(IParameterSymbol param, HashSet<string> routeParamNames)
    {
        // IFormFile parameter → File source (before attribute check — IFormFile is the signal)
        var typeName = param.Type.ToDisplayString();
        if (FormFileTypeNames.Contains(typeName))
        {
            return ParamSource.File;
        }

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

        // Unwrap ActionResult<T> → T
        if (returnType is INamedTypeSymbol actionResult
            && actionResult.OriginalDefinition.ToDisplayString() is "Microsoft.AspNetCore.Mvc.ActionResult<TValue>")
        {
            return typeWalker.MapTypePublic(actionResult.TypeArguments[0]);
        }

        // If it's IActionResult or non-generic ActionResult, we can't infer the type — skip
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

    /// <summary>
    /// Extracts all [ProducesResponseType] attributes as typed responses.
    /// </summary>
    private static IReadOnlyList<TsResponseType> ExtractAllResponseTypes(
        IMethodSymbol method,
        TypeWalker typeWalker)
    {
        var responses = new List<TsResponseType>();

        foreach (var attr in method.GetAttributes())
        {
            var attrName = attr.AttributeClass?.ToDisplayString();
            if (attrName is not "Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute")
            {
                continue;
            }

            // ProducesResponseType(typeof(T), statusCode)
            if (attr.ConstructorArguments.Length >= 2
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol typeArg
                && attr.ConstructorArguments[1].Value is int statusCode)
            {
                var tsType = typeWalker.MapTypePublic(typeArg);
                responses.Add(new TsResponseType(statusCode, tsType));
            }
            // ProducesResponseType(statusCode) — no body
            else if (attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is int codeOnly)
            {
                responses.Add(new TsResponseType(codeOnly, null));
            }
        }

        // Sort by status code for consistent output
        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));

        return responses;
    }

    private static IEnumerable<IMethodSymbol> FindAttributedMethods(
        Compilation compilation,
        INamedTypeSymbol attributeSymbol)
    {
        return RoslynExtensions.GetAllTypes(compilation.GlobalNamespace)
            .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>())
            .Where(m => m.GetAttributes().Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol)));
    }

    /// <summary>
    /// Finds all public methods with HTTP method attributes on [RivetClient]-decorated classes.
    /// </summary>
    private static IEnumerable<IMethodSymbol> FindClientMethods(
        Compilation compilation,
        INamedTypeSymbol clientAttributeSymbol)
    {
        return RoslynExtensions.GetAllTypes(compilation.GlobalNamespace)
            .Where(t => t.GetAttributes().Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, clientAttributeSymbol)))
            .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>())
            .Where(m => m.DeclaredAccessibility == Accessibility.Public
                && !m.IsImplicitlyDeclared
                && HasHttpMethodAttribute(m));
    }

    private static bool HasHttpMethodAttribute(IMethodSymbol method) =>
        method.GetAttributes().Any(a =>
        {
            var name = a.AttributeClass?.ToDisplayString();
            return name is not null && HttpMethodAttributes.Contains(name);
        });

}
