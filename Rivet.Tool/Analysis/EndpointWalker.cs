using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Discovers [RivetEndpoint]-attributed methods and extracts HTTP method, route,
/// parameter bindings, and return type.
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
        var (httpMethod, route) = ExtractHttpMethodAndRoute(method);
        if (httpMethod is null || route is null)
        {
            return null;
        }

        var parameters = ExtractParams(method, typeWalker);
        var returnType = ExtractReturnType(method, typeWalker);
        var name = ToCamelCase(method.Name);

        return new TsEndpointDefinition(name, httpMethod, route, parameters, returnType);
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

            // Route is the first constructor argument (if any)
            var route = attr.ConstructorArguments.Length > 0
                ? attr.ConstructorArguments[0].Value as string
                : null;

            if (httpMethod is not null && route is not null)
            {
                return (httpMethod, route);
            }
        }

        return (null, null);
    }

    private static IReadOnlyList<TsEndpointParam> ExtractParams(IMethodSymbol method, TypeWalker typeWalker)
    {
        var parameters = new List<TsEndpointParam>();

        foreach (var param in method.Parameters)
        {
            var source = ClassifyParam(param);
            if (source is null)
            {
                continue;
            }

            var tsType = typeWalker.MapTypePublic(param.Type);
            parameters.Add(new TsEndpointParam(param.Name, tsType, source.Value));
        }

        return parameters;
    }

    private static ParamSource? ClassifyParam(IParameterSymbol param)
    {
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

        return null;
    }

    private static TsType? ExtractReturnType(IMethodSymbol method, TypeWalker typeWalker)
    {
        var returnType = method.ReturnType;

        // Unwrap Task<T> / ValueTask<T>
        if (returnType is INamedTypeSymbol namedReturn)
        {
            var displayName = namedReturn.OriginalDefinition.ToDisplayString();
            if (displayName is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
            {
                returnType = namedReturn.TypeArguments[0];
            }
            // Task (no T) or void → null (no return body)
            else if (displayName is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
            {
                return null;
            }
        }

        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return null;
        }

        return typeWalker.MapTypePublic(returnType);
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
}
