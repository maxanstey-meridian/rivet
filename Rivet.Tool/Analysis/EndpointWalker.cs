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

    /// <summary>
    /// Discovers endpoints from [RivetEndpoint] methods and [RivetClient] classes.
    /// Use SymbolDiscovery.Discover() to obtain the method/type lists.
    /// </summary>
    public static IReadOnlyList<TsEndpointDefinition> Walk(
        TypeWalker typeWalker,
        IReadOnlyList<IMethodSymbol> endpointMethods,
        IReadOnlyList<INamedTypeSymbol> clientTypes)
    {
        var endpoints = new List<TsEndpointDefinition>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        // [RivetEndpoint] on individual methods
        foreach (var method in endpointMethods)
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

        // [RivetClient] on classes — all public methods with HTTP attributes
        var clientMethods = clientTypes
            .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>())
            .Where(m => m.DeclaredAccessibility == Accessibility.Public
                && !m.IsImplicitlyDeclared
                && HasHttpMethodAttribute(m));

        foreach (var method in clientMethods)
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
        fullRoute = RouteParser.StripRouteConstraints(fullRoute);

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

    internal static (string? HttpMethod, string? Route) ExtractHttpMethodAndRoute(IMethodSymbol method)
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
    internal static string? ExtractControllerRoute(INamedTypeSymbol? containingType)
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
    internal static string? CombineRoutes(string? controllerRoute, string? methodRoute)
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

    internal static IReadOnlyList<TsEndpointParam> ExtractParams(
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
                : typeWalker.MapType(param.Type);
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
    /// Maps an ASP.NET typed result type (e.g. Ok&lt;T&gt;, NotFound) to its HTTP status code
    /// and optional body type.
    /// </summary>
    private static (int StatusCode, ITypeSymbol? BodyType)? MapTypedResult(INamedTypeSymbol type)
    {
        var display = type.OriginalDefinition.ToDisplayString();
        return display switch
        {
            "Microsoft.AspNetCore.Http.HttpResults.Ok<TValue>" => (200, type.TypeArguments[0]),
            "Microsoft.AspNetCore.Http.HttpResults.Ok" => (200, null),
            "Microsoft.AspNetCore.Http.HttpResults.Created<TValue>" => (201, type.TypeArguments[0]),
            "Microsoft.AspNetCore.Http.HttpResults.Created" => (201, null),
            "Microsoft.AspNetCore.Http.HttpResults.Accepted<TValue>" => (202, type.TypeArguments[0]),
            "Microsoft.AspNetCore.Http.HttpResults.Accepted" => (202, null),
            "Microsoft.AspNetCore.Http.HttpResults.NoContent" => (204, null),
            "Microsoft.AspNetCore.Http.HttpResults.BadRequest<TValue>" => (400, type.TypeArguments[0]),
            "Microsoft.AspNetCore.Http.HttpResults.BadRequest" => (400, null),
            "Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult" => (401, null),
            "Microsoft.AspNetCore.Http.HttpResults.NotFound<TValue>" => (404, type.TypeArguments[0]),
            "Microsoft.AspNetCore.Http.HttpResults.NotFound" => (404, null),
            "Microsoft.AspNetCore.Http.HttpResults.Conflict<TValue>" => (409, type.TypeArguments[0]),
            "Microsoft.AspNetCore.Http.HttpResults.Conflict" => (409, null),
            "Microsoft.AspNetCore.Http.HttpResults.UnprocessableEntity<TValue>" => (422, type.TypeArguments[0]),
            "Microsoft.AspNetCore.Http.HttpResults.UnprocessableEntity" => (422, null),
            _ => null,
        };
    }

    /// <summary>
    /// Unwraps Task&lt;T&gt; / ValueTask&lt;T&gt; from a return type.
    /// Returns the inner type, or null if it's a non-generic Task/ValueTask/void.
    /// Returns the type unchanged if it's not a task wrapper.
    /// The out parameter isVoidTask is true when the return type is Task or ValueTask (no result).
    /// </summary>
    private static ITypeSymbol? UnwrapTask(ITypeSymbol returnType, out bool isVoidTask)
    {
        isVoidTask = false;

        if (returnType is INamedTypeSymbol namedReturn)
        {
            var displayName = namedReturn.OriginalDefinition.ToDisplayString();
            if (displayName is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                return namedReturn.TypeArguments[0];
            }

            if (displayName is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
            {
                isVoidTask = true;
                return null;
            }
        }

        if (returnType.SpecialType == SpecialType.System_Void)
        {
            isVoidTask = true;
            return null;
        }

        return returnType;
    }

    /// <summary>
    /// Checks if a type is Results&lt;T1, T2, ...&gt; (arity 2-6).
    /// </summary>
    private static bool IsTypedResults(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        return ns is "Microsoft.AspNetCore.Http.HttpResults" && type.Name is "Results" && type.TypeArguments.Length >= 2;
    }

    /// <summary>
    /// Collects typed result mappings from a type that is either Results&lt;T1, T2, ...&gt;
    /// or a single typed result (e.g. Ok&lt;T&gt;). Returns an empty list if the type is neither.
    /// </summary>
    private static List<(int StatusCode, ITypeSymbol? BodyType)> CollectTypedResultMappings(INamedTypeSymbol type)
    {
        var results = new List<(int StatusCode, ITypeSymbol? BodyType)>();

        if (IsTypedResults(type))
        {
            foreach (var arg in type.TypeArguments)
            {
                if (arg is INamedTypeSymbol resultArg)
                {
                    var mapped = MapTypedResult(resultArg);
                    if (mapped is not null)
                    {
                        results.Add(mapped.Value);
                    }
                }
            }
        }
        else
        {
            var mapped = MapTypedResult(type);
            if (mapped is not null)
            {
                results.Add(mapped.Value);
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts return type. Tries ProducesResponseType(typeof(T), 200) first (controllers),
    /// then falls back to method return type (minimal API).
    /// </summary>
    internal static TsType? ExtractReturnType(IMethodSymbol method, TypeWalker typeWalker)
    {
        // Try ProducesResponseType first (controller pattern)
        var producesType = ExtractProducesResponseType(method);
        if (producesType is not null)
        {
            return typeWalker.MapType(producesType);
        }

        // Fall back to method return type (minimal API pattern)
        var unwrapped = UnwrapTask(method.ReturnType, out var isVoidTask);
        if (isVoidTask || unwrapped is null)
        {
            return null;
        }

        // Check for typed results (Results<T1, T2, ...> or single e.g. Ok<T>)
        if (unwrapped is INamedTypeSymbol namedUnwrapped)
        {
            var resultMappings = CollectTypedResultMappings(namedUnwrapped);
            if (resultMappings.Count > 0)
            {
                // Prefer 2xx with body over 2xx without — order in Results<> shouldn't matter
                var successWithBody = resultMappings.FirstOrDefault(
                    m => m.StatusCode is >= 200 and < 300 && m.BodyType is not null);

                return successWithBody.BodyType is not null
                    ? typeWalker.MapType(successWithBody.BodyType)
                    : null;
            }
        }

        // Unwrap ActionResult<T> → T
        if (unwrapped is INamedTypeSymbol actionResult
            && actionResult.OriginalDefinition.ToDisplayString() is "Microsoft.AspNetCore.Mvc.ActionResult<TValue>")
        {
            return typeWalker.MapType(actionResult.TypeArguments[0]);
        }

        // If it's IActionResult or non-generic ActionResult, we can't infer the type — skip
        var returnName = unwrapped.ToDisplayString();
        if (returnName is "Microsoft.AspNetCore.Mvc.IActionResult"
            or "Microsoft.AspNetCore.Mvc.ActionResult")
        {
            return null;
        }

        return typeWalker.MapType(unwrapped);
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
    /// Falls back to Results&lt;T1, T2, ...&gt; or single typed results when no attributes are present.
    /// </summary>
    internal static IReadOnlyList<TsResponseType> ExtractAllResponseTypes(
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
                var tsType = typeWalker.MapType(typeArg);
                responses.Add(new TsResponseType(statusCode, tsType));
            }
            // ProducesResponseType(statusCode) — no body
            else if (attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is int codeOnly)
            {
                responses.Add(new TsResponseType(codeOnly, null));
            }
        }

        // If [ProducesResponseType] attributes exist but none are 2xx, synthesize the
        // success response from ActionResult<T> so the discriminated union has a success branch.
        if (responses.Count > 0 && !responses.Any(r => r.StatusCode is >= 200 and < 300))
        {
            var unwrapped = UnwrapTask(method.ReturnType, out _);
            if (unwrapped is INamedTypeSymbol actionResult
                && actionResult.OriginalDefinition.ToDisplayString() is "Microsoft.AspNetCore.Mvc.ActionResult<TValue>")
            {
                var tsType = typeWalker.MapType(actionResult.TypeArguments[0]);
                responses.Insert(0, new TsResponseType(200, tsType));
            }
        }

        // If no [ProducesResponseType] found, try typed results from return type
        if (responses.Count == 0)
        {
            var unwrapped = UnwrapTask(method.ReturnType, out _);
            if (unwrapped is INamedTypeSymbol namedType)
            {
                foreach (var mapping in CollectTypedResultMappings(namedType))
                {
                    var tsType = mapping.BodyType is not null
                        ? typeWalker.MapType(mapping.BodyType)
                        : null;
                    responses.Add(new TsResponseType(mapping.StatusCode, tsType));
                }
            }
        }

        // Sort by status code for consistent output
        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));

        return responses;
    }

    internal static bool HasHttpMethodAttribute(IMethodSymbol method) =>
        method.GetAttributes().Any(a =>
        {
            var name = a.AttributeClass?.ToDisplayString();
            return name is not null && HttpMethodAttributes.Contains(name);
        });

}
