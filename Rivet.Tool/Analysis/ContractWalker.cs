using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Discovers [RivetContract]-attributed static classes and extracts endpoint definitions
/// from their static readonly RouteDefinition fields by reading the builder chain via Roslyn operations.
/// </summary>
public static class ContractWalker
{
    /// <summary>
    /// Discovers endpoints from [RivetContract] static classes.
    /// Use SymbolDiscovery.Discover() to obtain the contract type list.
    /// </summary>
    public static IReadOnlyList<TsEndpointDefinition> Walk(
        Compilation compilation,
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        IReadOnlyList<INamedTypeSymbol> contractTypes)
    {
        var defineType = compilation.GetTypeByMetadataName("Rivet.Define");
        if (defineType is null)
        {
            return [];
        }

        var endpoints = new List<TsEndpointDefinition>();

        foreach (var type in contractTypes)
        {

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

                    if (!EndpointWalker.HasHttpMethodAttribute(wkt, method))
                    {
                        continue;
                    }

                    var endpoint = BuildEndpointFromMethod(wkt, method, controllerName, typeWalker);
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

                if (!IsRivetEndpointField(field.Type, defineType))
                {
                    continue;
                }

                if (!field.IsStatic || !field.IsReadOnly)
                {
                    Console.Error.WriteLine(
                        $"warning: {type.Name}.{field.Name} should be 'static readonly' — it may not be read correctly at generation time");
                }

                var endpoint = BuildEndpointFromField(field, controllerName, compilation, wkt, typeWalker);
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
        WellKnownTypes wkt,
        IMethodSymbol method,
        string controllerName,
        TypeWalker typeWalker)
    {
        var (httpMethod, methodRoute) = EndpointWalker.ExtractHttpMethodAndRoute(wkt, method);
        if (httpMethod is null)
        {
            return null;
        }

        var classRoute = EndpointWalker.ExtractControllerRoute(wkt, method.ContainingType);
        var fullRoute = EndpointWalker.CombineRoutes(classRoute, methodRoute);

        if (fullRoute is null)
        {
            return null;
        }

        fullRoute = RouteParser.StripRouteConstraints(fullRoute);

        var parameters = EndpointWalker.ExtractParams(wkt, method, typeWalker, fullRoute);
        var responses = EndpointWalker.ExtractAllResponseTypes(wkt, method, typeWalker);
        var returnType = EndpointWalker.ExtractReturnType(wkt, method, typeWalker);
        var name = Naming.ToCamelCase(method.Name);

        return new TsEndpointDefinition(name, httpMethod, fullRoute, parameters, returnType, controllerName, responses);
    }

    private static TsEndpointDefinition? BuildEndpointFromField(
        IFieldSymbol field,
        string controllerName,
        Compilation compilation,
        WellKnownTypes wkt,
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

        // The root call is the factory method: Define.Get<TInput, TOutput>("/route") or Define.File("/route")
        var root = chain[0];
        var isFileEndpoint = root.MethodName == "File";
        var httpMethod = isFileEndpoint ? "GET" : root.MethodName.ToUpperInvariant();
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

        // Process chained calls: .Accepts<T>(), .Returns<T>(statusCode[, description]),
        // .Status(statusCode), .Description(desc), .Anonymous(), .Secure(scheme), .ProducesFile(contentType)
        var responses = new List<TsResponseType>();
        var requestExampleCalls = new List<PendingEndpointExampleCall>();
        var responseExampleCalls = new List<PendingEndpointExampleCall>();
        int? successStatusOverride = null;
        string? endpointSummary = null;
        string? endpointDescription = null;
        EndpointSecurity? security = null;
        var acceptsFile = false;
        var isFormEncoded = false;
        string? fileContentType = null;
        QueryAuthMetadata? queryAuth = null;

        for (var i = 1; i < chain.Count; i++)
        {
            var call = chain[i];
            if (call.MethodName == "Accepts" && call.TypeArgs.Count == 1)
            {
                tInput = call.TypeArgs[0];
            }
            else if (call.MethodName == "AcceptsFile")
            {
                acceptsFile = true;
            }
            else if (call.MethodName == "FormEncoded")
            {
                isFormEncoded = true;
            }
            else if (call.MethodName == "RequestExampleJson" && call.GetStringArg("json") is not null)
            {
                requestExampleCalls.Add(new PendingEndpointExampleCall(
                    StatusCode: null,
                    Name: call.GetStringArg("name"),
                    MediaType: call.GetStringArg("mediaType"),
                    Json: call.GetStringArg("json"),
                    ComponentExampleId: null,
                    ResolvedJson: null));
            }
            else if (call.MethodName == "RequestExampleRef"
                && call.GetStringArg("componentExampleId") is not null
                && call.GetStringArg("resolvedJson") is not null)
            {
                requestExampleCalls.Add(new PendingEndpointExampleCall(
                    StatusCode: null,
                    Name: call.GetStringArg("name"),
                    MediaType: call.GetStringArg("mediaType"),
                    Json: null,
                    ComponentExampleId: call.GetStringArg("componentExampleId"),
                    ResolvedJson: call.GetStringArg("resolvedJson")));
            }
            else if (call.MethodName == "Returns" && call.TypeArgs.Count == 1 && call.StatusCodeArg is not null)
            {
                var tsType = typeWalker.MapType(call.TypeArgs[0]);
                responses.Add(new TsResponseType(call.StatusCodeArg.Value, tsType, call.StringArg));
            }
            else if (call.MethodName == "Returns" && call.TypeArgs.Count == 0 && call.StatusCodeArg is not null)
            {
                responses.Add(new TsResponseType(call.StatusCodeArg.Value, null, call.StringArg));
            }
            else if (call.MethodName == "Status" && call.StatusCodeArg is not null)
            {
                successStatusOverride = call.StatusCodeArg.Value;
            }
            else if (call.MethodName == "Summary" && call.StringArg is not null)
            {
                endpointSummary = call.StringArg;
            }
            else if (call.MethodName == "Description" && call.StringArg is not null)
            {
                endpointDescription = call.StringArg;
            }
            else if (call.MethodName == "Anonymous")
            {
                security = new EndpointSecurity(true);
            }
            else if (call.MethodName == "Secure" && call.StringArg is not null)
            {
                security = new EndpointSecurity(false, call.StringArg);
            }
            else if (call.MethodName == "ProducesFile")
            {
                fileContentType = call.StringArg ?? "application/octet-stream";
            }
            else if (call.MethodName == "ContentType")
            {
                fileContentType = call.StringArg ?? "application/octet-stream";
            }
            else if (call.MethodName == "QueryAuth")
            {
                queryAuth = new QueryAuthMetadata(call.GetStringArg("parameterName") ?? "token");
            }
            else if (call.MethodName == "ResponseExampleJson"
                && call.GetIntArg("statusCode") is int responseStatusCode
                && call.GetStringArg("json") is not null)
            {
                responseExampleCalls.Add(new PendingEndpointExampleCall(
                    responseStatusCode,
                    call.GetStringArg("name"),
                    call.GetStringArg("mediaType"),
                    call.GetStringArg("json"),
                    null,
                    null));
            }
            else if (call.MethodName == "ResponseExampleRef"
                && call.GetIntArg("statusCode") is int refStatusCode
                && call.GetStringArg("componentExampleId") is not null
                && call.GetStringArg("resolvedJson") is not null)
            {
                responseExampleCalls.Add(new PendingEndpointExampleCall(
                    refStatusCode,
                    call.GetStringArg("name"),
                    call.GetStringArg("mediaType"),
                    null,
                    call.GetStringArg("componentExampleId"),
                    call.GetStringArg("resolvedJson")));
            }
        }

        // Define.File() defaults to application/octet-stream (constructor calls ProducesFile()
        // at runtime, but the syntax walker only sees the source-level chain)
        if (isFileEndpoint)
        {
            fileContentType ??= "application/octet-stream";
        }

        // [ProducesFile] attribute on the field → file endpoint
        if (field.GetAttributes().Any(a => a.AttributeClass?.Name is "ProducesFileAttribute" or "ProducesFile"))
        {
            fileContentType ??= "application/octet-stream";
        }

        // byte[] or (byte[], string) as TOutput → file endpoint
        // The runtime contract keeps the original type for .Invoke(), but the TS client gets Blob
        if (tOutput is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            fileContentType ??= "application/octet-stream";
            tOutput = null; // Don't map byte[] → number[] in TS
        }
        else if (fileContentType is not null && IsByteArrayStringTuple(tOutput))
        {
            tOutput = null; // Named file tuple — don't map to TS, client gets Blob
        }

        // Build return type from TOutput
        TsType? returnType = tOutput is not null ? typeWalker.MapType(tOutput) : null;

        // Build params based on HTTP method and TInput
        var (parameters, inputTypeName) = BuildParams(wkt, httpMethod, route, tInput, typeWalker, acceptsFile);

        // Add success response to responses list
        // Void endpoints with typed error responses also need a success entry
        // so the client emitter generates a discriminated union (not RivetResult<void>)
        if (returnType is not null)
        {
            var successCode = successStatusOverride ?? DefaultSuccessCode(httpMethod);
            responses.Insert(0, new TsResponseType(successCode, returnType));
        }
        else if (fileContentType is not null || successStatusOverride is not null || responses.Count > 0
            || DefaultSuccessCode(httpMethod) != 200)
        {
            var successCode = successStatusOverride ?? DefaultSuccessCode(httpMethod);
            responses.Insert(0, new TsResponseType(successCode, null));
        }

        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));
        ApplyResponseExamples(responses, responseExampleCalls, fileContentType, name);

        var requestExamples = requestExampleCalls.Count == 0
            ? null
            : requestExampleCalls
                .Select(call => ToEndpointExample(call, DefaultRequestExampleMediaType(isFormEncoded, parameters)))
                .ToList();

        return new TsEndpointDefinition(
            name,
            httpMethod,
            route,
            parameters,
            returnType,
            controllerName,
            responses,
            endpointSummary,
            endpointDescription,
            security,
            fileContentType,
            inputTypeName,
            isFormEncoded,
            RequestExamples: requestExamples,
            IsFileEndpoint: isFileEndpoint,
            QueryAuth: queryAuth);
    }

    private static int DefaultSuccessCode(string httpMethod) =>
        httpMethod switch { "POST" => 201, "DELETE" => 204, _ => 200 };

    private static string DefaultRequestExampleMediaType(
        bool isFormEncoded,
        IReadOnlyList<TsEndpointParam> parameters)
    {
        if (parameters.Any(parameter => parameter.Source is ParamSource.File or ParamSource.FormField))
        {
            return "multipart/form-data";
        }

        return isFormEncoded
            ? "application/x-www-form-urlencoded"
            : "application/json";
    }

    private static string DefaultResponseExampleMediaType(int statusCode, string? fileContentType)
    {
        if (fileContentType is not null && statusCode is >= 200 and < 300)
        {
            return fileContentType;
        }

        return "application/json";
    }

    private static TsEndpointExample ToEndpointExample(PendingEndpointExampleCall call, string defaultMediaType)
    {
        return new TsEndpointExample(
            call.MediaType ?? defaultMediaType,
            call.Name,
            call.Json,
            call.ComponentExampleId,
            call.ResolvedJson);
    }

    private static void ApplyResponseExamples(
        List<TsResponseType> responses,
        IReadOnlyList<PendingEndpointExampleCall> responseExampleCalls,
        string? fileContentType,
        string endpointName)
    {
        if (responseExampleCalls.Count == 0)
        {
            return;
        }

        foreach (var group in responseExampleCalls.GroupBy(call => call.StatusCode!.Value))
        {
            var mappedExamples = group
                .Select(call => ToEndpointExample(call, DefaultResponseExampleMediaType(group.Key, fileContentType)))
                .ToList();

            var responseIndex = responses.FindIndex(response => response.StatusCode == group.Key);
            if (responseIndex >= 0)
            {
                var response = responses[responseIndex];
                var mergedExamples = response.Examples is null
                    ? mappedExamples
                    : response.Examples.Concat(mappedExamples).ToList();
                responses[responseIndex] = response with { Examples = mergedExamples };
                continue;
            }

            Console.Error.WriteLine(
                $"warning: ignoring response example for undeclared status {group.Key} on contract endpoint '{endpointName}'");
        }

        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));
    }

    private static (IReadOnlyList<TsEndpointParam> Params, string? InputTypeName) BuildParams(
        WellKnownTypes wkt,
        string httpMethod,
        string route,
        ITypeSymbol? tInput,
        TypeWalker typeWalker,
        bool acceptsFile = false)
    {
        var routeParamNames = RouteParser.ParseRouteParamNames(route);
        var parameters = new List<TsEndpointParam>();
        var hasBody = httpMethod is "POST" or "PUT" or "PATCH";
        string? inputTypeName = null;

        if (hasBody)
        {
            // Route params from template — try to match types from TInput properties
            foreach (var paramName in routeParamNames)
            {
                TsType paramType = new TsType.Primitive("string");
                if (tInput is not null)
                {
                    var matchingProp = tInput.GetMembers().OfType<IPropertySymbol>()
                        .FirstOrDefault(p => !p.IsImplicitlyDeclared
                            && string.Equals(p.Name, paramName, StringComparison.OrdinalIgnoreCase));
                    if (matchingProp is not null)
                    {
                        paramType = typeWalker.MapType(matchingProp.Type);
                    }
                }
                parameters.Add(new TsEndpointParam(paramName, paramType, ParamSource.Route));
            }

            // .AcceptsFile() on the contract — add a File param
            if (acceptsFile)
            {
                parameters.Add(new TsEndpointParam("file", new TsType.Primitive("File"), ParamSource.File));
            }

            if (tInput is not null)
            {
                // Check if TInput itself is IFormFile
                if (IsFormFileType(wkt, tInput))
                {
                    parameters.Add(new TsEndpointParam("file", new TsType.Primitive("File"), ParamSource.File));
                }
                // Check if TInput is a record containing IFormFile properties
                else if (HasFormFileProperty(wkt, tInput))
                {
                    inputTypeName = tInput.Name;
                    foreach (var member in tInput.GetMembers())
                    {
                        if (member is not IPropertySymbol prop || prop.IsImplicitlyDeclared)
                        {
                            continue;
                        }

                        if (typeWalker.IsJsonIgnored(prop))
                        {
                            continue;
                        }

                        // Skip properties already emitted as route params
                        if (routeParamNames.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var tsName = typeWalker.GetJsonPropertyName(prop) ?? Naming.ToCamelCase(prop.Name);

                        if (IsFormFileType(wkt, prop.Type))
                        {
                            parameters.Add(new TsEndpointParam(
                                tsName,
                                new TsType.Primitive("File"),
                                ParamSource.File));
                        }
                        else
                        {
                            // Non-file properties on a mixed upload record → form fields
                            parameters.Add(new TsEndpointParam(
                                tsName,
                                typeWalker.MapType(prop.Type),
                                ParamSource.FormField));
                        }
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

                    if (typeWalker.IsJsonIgnored(prop))
                    {
                        continue;
                    }

                    var tsName = typeWalker.GetJsonPropertyName(prop) ?? Naming.ToCamelCase(prop.Name);

                    var isFormFile = SymbolEqualityComparer.Default.Equals(prop.Type, wkt.IFormFile);
                    if (isFormFile)
                    {
                        parameters.Add(new TsEndpointParam(
                            tsName,
                            new TsType.Primitive("File"),
                            ParamSource.File));
                        continue;
                    }

                    var tsType = typeWalker.MapType(prop.Type);
                    // Route matching uses C# property name (matches template {Id}), not JSON name
                    var source = routeParamNames.Contains(prop.Name)
                        ? ParamSource.Route
                        : ParamSource.Query;

                    parameters.Add(new TsEndpointParam(tsName, tsType, source));
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

        return (parameters, inputTypeName);
    }

    private static bool IsFormFileType(WellKnownTypes wkt, ITypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type, wkt.IFormFile);

    private static bool HasFormFileProperty(WellKnownTypes wkt, ITypeSymbol type) =>
        type.GetMembers().OfType<IPropertySymbol>()
            .Any(p => !p.IsImplicitlyDeclared && IsFormFileType(wkt, p.Type));

    /// <summary>
    /// Checks if the type is a (byte[], string) tuple — used for named file downloads
    /// when the field is marked with [ProducesFile].
    /// </summary>
    private static bool IsByteArrayStringTuple(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || !named.IsTupleType || named.TupleElements.Length != 2)
        {
            return false;
        }

        var first = named.TupleElements[0].Type;
        var second = named.TupleElements[1].Type;

        return first is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }
            && second.SpecialType == SpecialType.System_String;
    }

    internal static bool IsRivetEndpointField(ITypeSymbol fieldType, INamedTypeSymbol? defineType)
    {
        if (defineType is not null && SymbolEqualityComparer.Default.Equals(fieldType, defineType))
        {
            return true;
        }

        if (fieldType is INamedTypeSymbol named
            && named.Name is "RouteDefinition" or "InputRouteDefinition"
            && named.ContainingNamespace?.ToDisplayString() == "Rivet")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Strips "Contract" suffix and camelCases. TasksContract → tasks.
    /// </summary>
    internal static string DeriveControllerName(INamedTypeSymbol type)
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
    private sealed class ChainedCall
    {
        public ChainedCall(
            string methodName,
            IReadOnlyList<ITypeSymbol> typeArgs,
            IReadOnlyDictionary<string, object> constantArgs)
        {
            MethodName = methodName;
            TypeArgs = typeArgs;
            ConstantArgs = constantArgs;
        }

        public string MethodName { get; }
        public IReadOnlyList<ITypeSymbol> TypeArgs { get; }
        public IReadOnlyDictionary<string, object> ConstantArgs { get; }

        public string? RouteArg => GetStringArg("route");
        public int? StatusCodeArg => GetIntArg("statusCode");
        public string? StringArg => GetFirstStringArg();

        public string? GetStringArg(string parameterName) =>
            ConstantArgs.TryGetValue(parameterName, out var value) && value is string text
                ? text
                : null;

        public int? GetIntArg(string parameterName) =>
            ConstantArgs.TryGetValue(parameterName, out var value) && value is int number
                ? number
                : null;

        private string? GetFirstStringArg() =>
            ConstantArgs.Values.OfType<string>().FirstOrDefault();
    }

    private sealed record PendingEndpointExampleCall(
        int? StatusCode,
        string? Name,
        string? MediaType,
        string? Json,
        string? ComponentExampleId,
        string? ResolvedJson);

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

        var constantArgs = new Dictionary<string, object>(StringComparer.Ordinal);

        for (var i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var arg = invocation.ArgumentList.Arguments[i];
            var parameter = ResolveParameter(method, arg, i);
            if (parameter is null)
            {
                continue;
            }

            var constValue = semanticModel.GetConstantValue(arg.Expression);
            if (!constValue.HasValue || constValue.Value is null)
            {
                continue;
            }

            constantArgs[parameter.Name] = constValue.Value;
        }

        calls.Add(new ChainedCall(
            method.Name,
            typeArgs,
            constantArgs));

        // Recurse into the receiver (the expression the method is called on)
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            CollectInvocationsRecursive(memberAccess.Expression, semanticModel, calls);
        }
    }

    private static IParameterSymbol? ResolveParameter(IMethodSymbol method, ArgumentSyntax argument, int ordinal)
    {
        if (argument.NameColon is not null)
        {
            var name = argument.NameColon.Name.Identifier.ValueText;
            return method.Parameters.FirstOrDefault(parameter => parameter.Name == name);
        }

        return ordinal < method.Parameters.Length
            ? method.Parameters[ordinal]
            : null;
    }
}
