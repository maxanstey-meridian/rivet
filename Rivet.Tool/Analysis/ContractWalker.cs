using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Discovers [RivetContract]-attributed static classes and extracts endpoint definitions
/// from their static readonly Endpoint fields by reading the builder chain via Roslyn operations.
/// </summary>
public static class ContractWalker
{
    public static IReadOnlyList<TsEndpointDefinition> Walk(Compilation compilation, TypeWalker typeWalker)
    {
        var contractAttr = compilation.GetTypeByMetadataName("Rivet.RivetContractAttribute");
        if (contractAttr is null)
        {
            return [];
        }

        var endpointType = compilation.GetTypeByMetadataName("Rivet.Endpoint");
        if (endpointType is null)
        {
            return [];
        }

        var endpoints = new List<TsEndpointDefinition>();

        foreach (var type in RoslynExtensions.GetAllTypes(compilation.GlobalNamespace))
        {
            if (!type.GetAttributes().Any(a =>
                SymbolEqualityComparer.Default.Equals(a.AttributeClass, contractAttr)))
            {
                continue;
            }

            var controllerName = DeriveControllerName(type);

            // Abstract class contract: read HTTP attributes from abstract methods
            if (type.IsAbstract && !type.IsStatic)
            {
                foreach (var member in type.GetMembers())
                {
                    if (member is not IMethodSymbol method || !method.IsAbstract)
                    {
                        continue;
                    }

                    if (!EndpointWalker.HasHttpMethodAttribute(method))
                    {
                        continue;
                    }

                    var endpoint = BuildEndpointFromMethod(method, controllerName, typeWalker);
                    if (endpoint is not null)
                    {
                        endpoints.Add(endpoint);
                    }
                }

                continue;
            }

            // Static class contract: read Endpoint fields from builder chain
            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol field)
                {
                    continue;
                }

                if (!IsRivetEndpointField(field.Type, endpointType))
                {
                    continue;
                }

                var endpoint = BuildEndpointFromField(field, controllerName, compilation, typeWalker);
                if (endpoint is not null)
                {
                    endpoints.Add(endpoint);
                }
            }
        }

        return endpoints;
    }

    /// <summary>
    /// Builds an endpoint definition from an abstract method with HTTP attributes.
    /// Uses EndpointWalker's extraction logic with contract-style controller name derivation.
    /// </summary>
    private static TsEndpointDefinition? BuildEndpointFromMethod(
        IMethodSymbol method,
        string controllerName,
        TypeWalker typeWalker)
    {
        var (httpMethod, methodRoute) = EndpointWalker.ExtractHttpMethodAndRoute(method);
        if (httpMethod is null)
        {
            return null;
        }

        var classRoute = EndpointWalker.ExtractControllerRoute(method.ContainingType);
        var fullRoute = EndpointWalker.CombineRoutes(classRoute, methodRoute);

        if (fullRoute is null)
        {
            return null;
        }

        fullRoute = RouteParser.StripRouteConstraints(fullRoute);

        var parameters = EndpointWalker.ExtractParams(method, typeWalker, fullRoute);
        var responses = EndpointWalker.ExtractAllResponseTypes(method, typeWalker);
        var returnType = EndpointWalker.ExtractReturnType(method, typeWalker);
        var name = Naming.ToCamelCase(method.Name);

        return new TsEndpointDefinition(name, httpMethod, fullRoute, parameters, returnType, controllerName, responses);
    }

    private static TsEndpointDefinition? BuildEndpointFromField(
        IFieldSymbol field,
        string controllerName,
        Compilation compilation,
        TypeWalker typeWalker)
    {
        // Get the syntax for the field initializer
        if (field.DeclaringSyntaxReferences.Length == 0)
        {
            return null;
        }

        var syntaxRef = field.DeclaringSyntaxReferences[0];
        var syntaxNode = syntaxRef.GetSyntax();

        if (syntaxNode is not VariableDeclaratorSyntax declarator || declarator.Initializer is null)
        {
            return null;
        }

        var initializerExpr = declarator.Initializer.Value;
        var semanticModel = compilation.GetSemanticModel(syntaxNode.SyntaxTree);

        // Walk the invocation chain via syntax + GetSymbolInfo (more reliable than operations API
        // for field initializers with implicit conversions)
        var chain = CollectInvocationChain(initializerExpr, semanticModel);
        if (chain.Count == 0)
        {
            return null;
        }

        // The root call is the factory method: Endpoint.Get<TInput, TOutput>("/route")
        var root = chain[0];
        var httpMethod = root.MethodName.ToUpperInvariant();
        var route = root.RouteArg;

        if (route is null)
        {
            return null;
        }

        route = RouteParser.StripRouteConstraints(route);

        var name = Naming.ToCamelCase(field.Name);

        // Determine TInput / TOutput from type arguments on the root factory call
        ITypeSymbol? tInput = null;
        ITypeSymbol? tOutput = null;

        if (root.TypeArgs.Count == 2)
        {
            tInput = root.TypeArgs[0];
            tOutput = root.TypeArgs[1];
        }
        else if (root.TypeArgs.Count == 1)
        {
            tOutput = root.TypeArgs[0];
        }

        // Process chained calls: .Returns<T>(statusCode[, description]), .Status(statusCode), .Description(desc),
        // .Anonymous(), .Secure(scheme)
        var responses = new List<TsResponseType>();
        int? successStatusOverride = null;
        string? endpointDescription = null;
        EndpointSecurity? security = null;

        for (var i = 1; i < chain.Count; i++)
        {
            var call = chain[i];
            if (call.MethodName == "Returns" && call.TypeArgs.Count == 1 && call.StatusCodeArg is not null)
            {
                var tsType = typeWalker.MapType(call.TypeArgs[0]);
                responses.Add(new TsResponseType(call.StatusCodeArg.Value, tsType, call.DescriptionArg));
            }
            else if (call.MethodName == "Status" && call.StatusCodeArg is not null)
            {
                successStatusOverride = call.StatusCodeArg.Value;
            }
            else if (call.MethodName == "Description" && call.DescriptionArg is not null)
            {
                endpointDescription = call.DescriptionArg;
            }
            else if (call.MethodName == "Anonymous")
            {
                security = new EndpointSecurity(true);
            }
            else if (call.MethodName == "Secure" && call.DescriptionArg is not null)
            {
                security = new EndpointSecurity(false, call.DescriptionArg);
            }
        }

        // Build return type from TOutput
        TsType? returnType = tOutput is not null ? typeWalker.MapType(tOutput) : null;

        // Build params based on HTTP method and TInput
        var parameters = BuildParams(httpMethod, route, tInput, typeWalker);

        // Add success response to responses list if we have TOutput
        if (returnType is not null)
        {
            var successCode = successStatusOverride ?? DefaultSuccessCode(httpMethod);
            responses.Insert(0, new TsResponseType(successCode, returnType));
        }
        else if (successStatusOverride is not null)
        {
            responses.Insert(0, new TsResponseType(successStatusOverride.Value, null));
        }

        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));

        return new TsEndpointDefinition(name, httpMethod, route, parameters, returnType, controllerName, responses, endpointDescription, security);
    }

    private static int DefaultSuccessCode(string httpMethod) =>
        httpMethod is "POST" ? 201 : 200;

    private static IReadOnlyList<TsEndpointParam> BuildParams(
        string httpMethod,
        string route,
        ITypeSymbol? tInput,
        TypeWalker typeWalker)
    {
        var routeParamNames = RouteParser.ParseRouteParamNames(route);
        var parameters = new List<TsEndpointParam>();
        var hasBody = httpMethod is "POST" or "PUT" or "PATCH";

        if (hasBody)
        {
            // Route params from template as standalone string params (not from TInput)
            foreach (var paramName in routeParamNames)
            {
                parameters.Add(new TsEndpointParam(paramName, new TsType.Primitive("string"), ParamSource.Route));
            }

            if (tInput is not null)
            {
                // Check if TInput itself is IFormFile
                if (IsFormFileType(tInput))
                {
                    parameters.Add(new TsEndpointParam("file", new TsType.Primitive("File"), ParamSource.File));
                }
                // Check if TInput is a record containing IFormFile properties
                else if (HasFormFileProperty(tInput))
                {
                    foreach (var member in tInput.GetMembers())
                    {
                        if (member is not IPropertySymbol prop || prop.IsImplicitlyDeclared)
                        {
                            continue;
                        }

                        if (IsFormFileType(prop.Type))
                        {
                            parameters.Add(new TsEndpointParam(
                                Naming.ToCamelCase(prop.Name),
                                new TsType.Primitive("File"),
                                ParamSource.File));
                        }
                        // Non-file properties on a file upload record are skipped —
                        // FormData doesn't carry typed JSON alongside the file
                    }
                }
                else
                {
                    // Normal body param
                    var tsType = typeWalker.MapType(tInput);
                    parameters.Add(new TsEndpointParam("body", tsType, ParamSource.Body));
                }
            }
        }
        else
        {
            // GET/DELETE: TInput properties matched by name to route → Route, remaining → Query
            if (tInput is not null)
            {
                foreach (var member in tInput.GetMembers())
                {
                    if (member is not IPropertySymbol prop || prop.IsImplicitlyDeclared)
                    {
                        continue;
                    }

                    var isFormFile = prop.Type.ToDisplayString() is "Microsoft.AspNetCore.Http.IFormFile";
                    if (isFormFile)
                    {
                        parameters.Add(new TsEndpointParam(
                            Naming.ToCamelCase(prop.Name),
                            new TsType.Primitive("File"),
                            ParamSource.File));
                        continue;
                    }

                    var tsType = typeWalker.MapType(prop.Type);
                    var source = routeParamNames.Contains(prop.Name)
                        ? ParamSource.Route
                        : ParamSource.Query;

                    parameters.Add(new TsEndpointParam(Naming.ToCamelCase(prop.Name), tsType, source));
                }
            }
            else
            {
                // No TInput but might have route params (e.g. DELETE with route params)
                foreach (var paramName in routeParamNames)
                {
                    parameters.Add(new TsEndpointParam(paramName, new TsType.Primitive("string"), ParamSource.Route));
                }
            }
        }

        return parameters;
    }

    private static bool IsFormFileType(ITypeSymbol type) =>
        type.ToDisplayString() is "Microsoft.AspNetCore.Http.IFormFile";

    private static bool HasFormFileProperty(ITypeSymbol type) =>
        type.GetMembers().OfType<IPropertySymbol>()
            .Any(p => !p.IsImplicitlyDeclared && IsFormFileType(p.Type));

    /// <summary>
    /// Accepts Endpoint fields (v1 legacy) and EndpointBuilder fields (v1 typed).
    /// </summary>
    private static bool IsRivetEndpointField(ITypeSymbol fieldType, INamedTypeSymbol? endpointType)
    {
        if (endpointType is not null && SymbolEqualityComparer.Default.Equals(fieldType, endpointType))
        {
            return true;
        }

        // EndpointBuilder, EndpointBuilder<TOutput>, EndpointBuilder<TInput, TOutput>
        if (fieldType is INamedTypeSymbol named
            && named.Name == "EndpointBuilder"
            && named.ContainingNamespace?.ToDisplayString() == "Rivet")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Strips "Contract" suffix and camelCases. TasksContract → tasks.
    /// </summary>
    private static string DeriveControllerName(INamedTypeSymbol type)
    {
        var name = type.Name;

        if (name.EndsWith("Contract", StringComparison.Ordinal))
        {
            name = name[..^"Contract".Length];
        }

        return Naming.ToCamelCase(name);
    }

    /// <summary>
    /// Represents a single method call in the builder chain.
    /// </summary>
    private sealed record ChainedCall(
        string MethodName,
        IReadOnlyList<ITypeSymbol> TypeArgs,
        string? RouteArg,
        int? StatusCodeArg,
        string? DescriptionArg);

    /// <summary>
    /// Walks the invocation chain from the initializer expression using syntax + GetSymbolInfo.
    /// Returns calls in order: root factory call first, then chained builder calls.
    /// </summary>
    private static List<ChainedCall> CollectInvocationChain(
        ExpressionSyntax expression,
        SemanticModel semanticModel)
    {
        var calls = new List<ChainedCall>();
        CollectInvocationsRecursive(expression, semanticModel, calls);
        calls.Reverse(); // Root call first
        return calls;
    }

    private static void CollectInvocationsRecursive(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        List<ChainedCall> calls)
    {
        // Unwrap parentheses
        while (expression is ParenthesizedExpressionSyntax parens)
        {
            expression = parens.Expression;
        }

        if (expression is not InvocationExpressionSyntax invocation)
        {
            return;
        }

        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol method)
        {
            return;
        }

        // Extract type arguments
        var typeArgs = method.TypeArguments;

        // Extract arguments — collect all string and int constants
        var stringArgs = new List<string>();
        int? statusCodeArg = null;

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var constValue = semanticModel.GetConstantValue(arg.Expression);
            if (constValue.HasValue)
            {
                if (constValue.Value is string s)
                {
                    stringArgs.Add(s);
                }
                else if (constValue.Value is int i)
                {
                    statusCodeArg = i;
                }
            }
        }

        // For factory calls (Get/Post/etc): first string = route, no description
        // For .Returns(statusCode, description): first string = description
        // For .Description(desc): first string = description
        var isFactoryCall = method.ContainingType?.Name == "Endpoint";
        string? routeArg = isFactoryCall ? stringArgs.FirstOrDefault() : null;
        string? descriptionArg = isFactoryCall ? null : stringArgs.FirstOrDefault();

        calls.Add(new ChainedCall(
            method.Name,
            typeArgs,
            routeArg,
            statusCodeArg,
            descriptionArg));

        // Recurse into the receiver (the expression the method is called on)
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            CollectInvocationsRecursive(memberAccess.Expression, semanticModel, calls);
        }
    }
}
