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
    /// <summary>
    /// Discovers endpoints from [RivetEndpoint] methods and [RivetClient] classes.
    /// Use SymbolDiscovery.Discover() to obtain the method/type lists.
    /// </summary>
    public static IReadOnlyList<TsEndpointDefinition> Walk(
        WellKnownTypes wkt,
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
                var endpoint = BuildEndpoint(method, wkt, typeWalker);
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
                && HasHttpMethodAttribute(wkt, m));

        foreach (var method in clientMethods)
        {
            if (seen.Add(method))
            {
                var endpoint = BuildEndpoint(method, wkt, typeWalker);
                if (endpoint is not null)
                {
                    endpoints.Add(endpoint);
                }
            }
        }

        return endpoints;
    }

    private static TsEndpointDefinition? BuildEndpoint(IMethodSymbol method, WellKnownTypes wkt, TypeWalker typeWalker)
    {
        var (httpMethod, methodRoute) = ExtractHttpMethodAndRoute(wkt, method);
        if (httpMethod is null)
        {
            return null;
        }

        // Combine controller [Route] prefix with method route
        var controllerRoute = ExtractControllerRoute(wkt, method.ContainingType);
        var fullRoute = CombineRoutes(controllerRoute, methodRoute);

        if (fullRoute is null)
        {
            return null;
        }

        // Strip route constraints: {id:guid} → {id}
        fullRoute = RouteParser.StripRouteConstraints(fullRoute);

        var parameters = ExtractParams(wkt, method, typeWalker, fullRoute);
        var responses = ExtractAllResponseTypes(wkt, method, typeWalker);
        var returnType = ExtractReturnType(wkt, method, typeWalker);
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

    internal static (string? HttpMethod, string? Route) ExtractHttpMethodAndRoute(WellKnownTypes wkt, IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass is not { } attrClass)
                continue;

            if (!wkt.HttpMethodAttributes.TryGetValue(attrClass, out var httpMethod))
                continue;

            // Route template is the first constructor argument (if any)
            var route = attr.ConstructorArguments.Length > 0
                ? attr.ConstructorArguments[0].Value as string
                : null;

            return (httpMethod, route);
        }

        return (null, null);
    }

    /// <summary>
    /// Reads [Route("...")] from the containing controller class.
    /// </summary>
    internal static string? ExtractControllerRoute(WellKnownTypes wkt, INamedTypeSymbol? containingType)
    {
        if (containingType is null)
        {
            return null;
        }

        foreach (var attr in containingType.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, wkt.Route)
                && attr.ConstructorArguments.Length > 0)
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
        WellKnownTypes wkt,
        IMethodSymbol method,
        TypeWalker typeWalker,
        string? routeTemplate)
    {
        // Extract route param names from the template for implicit classification
        var routeParamNames = routeTemplate is not null
            ? RouteParser.ParseRouteParamNames(routeTemplate)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var parameters = new List<TsEndpointParam>();

        // Pre-scan: if any parameter is IFormFile, non-route/non-file params become FormField
        var hasFileParam = wkt.IFormFile is not null
            && method.Parameters.Any(p => SymbolEqualityComparer.Default.Equals(p.Type, wkt.IFormFile));

        foreach (var param in method.Parameters)
        {
            var source = ClassifyParam(wkt, param, routeParamNames);
            if (source is null)
            {
                // Skip infrastructure types (CancellationToken, DI services, etc.)
                if (IsInfrastructureType(wkt, param.Type))
                {
                    continue;
                }

                // In mixed upload methods, unclassified params are form fields
                if (hasFileParam)
                {
                    parameters.Add(new TsEndpointParam(param.Name, typeWalker.MapType(param.Type), ParamSource.FormField));
                }

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

    private static bool IsInfrastructureType(WellKnownTypes wkt, ITypeSymbol type)
    {
        if (SymbolEqualityComparer.Default.Equals(type, wkt.CancellationToken))
        {
            return true;
        }

        // Interface types without [From*] attributes are DI services (but not IFormFile)
        return type.TypeKind == TypeKind.Interface
            && !SymbolEqualityComparer.Default.Equals(type, wkt.IFormFile);
    }

    private static ParamSource? ClassifyParam(WellKnownTypes wkt, IParameterSymbol param, HashSet<string> routeParamNames)
    {
        // IFormFile parameter → File source (before attribute check — IFormFile is the signal)
        if (SymbolEqualityComparer.Default.Equals(param.Type, wkt.IFormFile))
        {
            return ParamSource.File;
        }

        // Explicit attribute takes precedence
        foreach (var attr in param.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null) continue;

            if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromBody))
                return ParamSource.Body;
            if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromQuery))
                return ParamSource.Query;
            if (SymbolEqualityComparer.Default.Equals(attrClass, wkt.FromRoute))
                return ParamSource.Route;
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
    private static (int StatusCode, ITypeSymbol? BodyType)? MapTypedResult(WellKnownTypes wkt, INamedTypeSymbol type)
    {
        if (!wkt.TypedResultStatusCodes.TryGetValue(type.OriginalDefinition, out var statusCode))
        {
            return null;
        }

        var bodyType = type.TypeArguments.Length > 0 ? type.TypeArguments[0] : null;
        return (statusCode, bodyType);
    }

    /// <summary>
    /// Unwraps Task&lt;T&gt; / ValueTask&lt;T&gt; from a return type.
    /// Returns the inner type, or null if it's a non-generic Task/ValueTask/void.
    /// Returns the type unchanged if it's not a task wrapper.
    /// The out parameter isVoidTask is true when the return type is Task or ValueTask (no result).
    /// </summary>
    private static ITypeSymbol? UnwrapTask(WellKnownTypes wkt, ITypeSymbol returnType, out bool isVoidTask)
    {
        isVoidTask = false;

        if (returnType is INamedTypeSymbol namedReturn)
        {
            var original = namedReturn.OriginalDefinition;
            if (SymbolEqualityComparer.Default.Equals(original, wkt.TaskOfT)
                || SymbolEqualityComparer.Default.Equals(original, wkt.ValueTaskOfT))
            {
                return namedReturn.TypeArguments[0];
            }

            if (SymbolEqualityComparer.Default.Equals(original, wkt.Task)
                || SymbolEqualityComparer.Default.Equals(original, wkt.ValueTask))
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
    private static bool IsTypedResults(WellKnownTypes wkt, INamedTypeSymbol type)
    {
        return type.TypeArguments.Length >= 2
            && wkt.ResultsArities.Contains(type.OriginalDefinition);
    }

    /// <summary>
    /// Collects typed result mappings from a type that is either Results&lt;T1, T2, ...&gt;
    /// or a single typed result (e.g. Ok&lt;T&gt;). Returns an empty list if the type is neither.
    /// </summary>
    private static List<(int StatusCode, ITypeSymbol? BodyType)> CollectTypedResultMappings(WellKnownTypes wkt, INamedTypeSymbol type)
    {
        var results = new List<(int StatusCode, ITypeSymbol? BodyType)>();

        if (IsTypedResults(wkt, type))
        {
            foreach (var arg in type.TypeArguments)
            {
                if (arg is INamedTypeSymbol resultArg)
                {
                    var mapped = MapTypedResult(wkt, resultArg);
                    if (mapped is not null)
                    {
                        results.Add(mapped.Value);
                    }
                }
            }
        }
        else
        {
            var mapped = MapTypedResult(wkt, type);
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
    internal static TsType? ExtractReturnType(WellKnownTypes wkt, IMethodSymbol method, TypeWalker typeWalker)
    {
        // Try ProducesResponseType first (controller pattern)
        var producesType = ExtractProducesResponseType(wkt, method);
        if (producesType is not null)
        {
            return typeWalker.MapType(producesType);
        }

        // Fall back to method return type (minimal API pattern)
        var unwrapped = UnwrapTask(wkt, method.ReturnType, out var isVoidTask);
        if (isVoidTask || unwrapped is null)
        {
            return null;
        }

        // Check for typed results (Results<T1, T2, ...> or single e.g. Ok<T>)
        if (unwrapped is INamedTypeSymbol namedUnwrapped)
        {
            var resultMappings = CollectTypedResultMappings(wkt, namedUnwrapped);
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
            && SymbolEqualityComparer.Default.Equals(actionResult.OriginalDefinition, wkt.ActionResultOfT))
        {
            return typeWalker.MapType(actionResult.TypeArguments[0]);
        }

        // If it's IActionResult or non-generic ActionResult, we can't infer the type — skip
        if (SymbolEqualityComparer.Default.Equals(unwrapped, wkt.IActionResult)
            || SymbolEqualityComparer.Default.Equals(unwrapped, wkt.ActionResult))
        {
            return null;
        }

        return typeWalker.MapType(unwrapped);
    }

    /// <summary>
    /// Finds [ProducesResponseType(typeof(T), 200)] on the method and returns T.
    /// Only considers 2xx status codes as the success response type.
    /// </summary>
    private static ITypeSymbol? ExtractProducesResponseType(WellKnownTypes wkt, IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, wkt.ProducesResponseType))
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
        WellKnownTypes wkt,
        IMethodSymbol method,
        TypeWalker typeWalker)
    {
        var responses = new List<TsResponseType>();

        foreach (var attr in method.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, wkt.ProducesResponseType))
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
            var unwrapped = UnwrapTask(wkt, method.ReturnType, out _);
            if (unwrapped is INamedTypeSymbol actionResult
                && SymbolEqualityComparer.Default.Equals(actionResult.OriginalDefinition, wkt.ActionResultOfT))
            {
                var tsType = typeWalker.MapType(actionResult.TypeArguments[0]);
                responses.Insert(0, new TsResponseType(200, tsType));
            }
        }

        // If no [ProducesResponseType] found, try typed results from return type
        if (responses.Count == 0)
        {
            var unwrapped = UnwrapTask(wkt, method.ReturnType, out _);
            if (unwrapped is INamedTypeSymbol namedType)
            {
                foreach (var mapping in CollectTypedResultMappings(wkt, namedType))
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

    internal static bool HasHttpMethodAttribute(WellKnownTypes wkt, IMethodSymbol method) =>
        method.GetAttributes().Any(a =>
            a.AttributeClass is not null && wkt.HttpMethodAttributes.ContainsKey(a.AttributeClass));

}
