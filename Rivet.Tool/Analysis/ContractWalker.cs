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
        IReadOnlyList<INamedTypeSymbol> contractTypes
    )
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
                    Diagnostics.Warn(
                        Diagnostics.EndpointFieldNotStaticReadonly,
                        $"{type.Name}.{field.Name} should be 'static readonly' — it may not be read correctly at generation time"
                    );
                }

                var endpoint = BuildEndpointFromField(
                    field,
                    controllerName,
                    compilation,
                    wkt,
                    typeWalker
                );
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
        TypeWalker typeWalker
    )
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

        // A6: substitute [controller]/[action] tokens before constraint stripping
        fullRoute = EndpointWalker.SubstituteRouteTokens(fullRoute, method.ContainingType, method);

        fullRoute = RouteParser.StripRouteConstraints(fullRoute);

        var parameters = EndpointWalker.ExtractParams(wkt, method, typeWalker, fullRoute);
        var responses = EndpointWalker
            .ExtractAllResponseTypes(wkt, method, typeWalker, normalize: false)
            .ToList();
        var name = Naming.ToCamelCase(method.Name);
        ResponseStatusValidation.RejectContractDuplicates(responses, name);
        responses.Sort((left, right) => left.StatusCode.CompareTo(right.StatusCode));
        var returnType =
            responses.FirstOrDefault(response => response.StatusCode is >= 200 and < 300)?.DataType
            ?? (
                responses.Count == 0
                    ? EndpointWalker.ExtractReturnType(wkt, method, typeWalker)
                    : null
            );

        return new TsEndpointDefinition(
            name,
            httpMethod,
            fullRoute,
            parameters,
            returnType,
            controllerName,
            responses
        );
    }

    private static TsEndpointDefinition? BuildEndpointFromField(
        IFieldSymbol field,
        string controllerName,
        Compilation compilation,
        WellKnownTypes wkt,
        TypeWalker typeWalker
    )
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
            // Define.File<TInput> has a single type arg that represents the input type,
            // while Define.Get<TOutput> has a single type arg that represents the output type.
            if (isFileEndpoint)
            {
                tInput = root.TypeArgs[0];
            }
            else
            {
                tOutput = root.TypeArgs[0];
            }
        }

        // Process chained calls: .Accepts<T>(), .Returns<T>(statusCode[, description]),
        // .Status(statusCode), .Description(desc), .Anonymous(), .Secure(scheme), .ProducesFile(contentType)
        var responses = new List<TsResponseType>();
        var requestExampleCalls = new List<PendingEndpointExampleCall>();
        var responseExampleCalls = new List<PendingEndpointExampleCall>();
        var responseHeaderCalls = new List<PendingResponseHeaderCall>();
        int? successStatusOverride = null;
        string? endpointSummary = null;
        string? endpointDescription = null;
        EndpointSecurity? security = null;
        var acceptsFile = false;
        var isFormEncoded = false;
        string? fileContentType = null;
        string? binaryRequestContentType = null;
        string? requestContentTypeOverride = null;
        string? responseContentTypeOverride = null;
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
            else if (call.MethodName == "AcceptsBinary")
            {
                binaryRequestContentType = call.StringArg ?? "application/octet-stream";
            }
            else if (call.MethodName == "AcceptsContentType" && call.StringArg is not null)
            {
                requestContentTypeOverride = call.StringArg;
            }
            else if (call.MethodName == "ProducesContentType" && call.StringArg is not null)
            {
                responseContentTypeOverride = call.StringArg;
            }
            else if (
                call.MethodName == "RequestExampleJson"
                && call.GetStringArg("json") is not null
            )
            {
                requestExampleCalls.Add(
                    new PendingEndpointExampleCall(
                        StatusCode: null,
                        Name: call.GetStringArg("name"),
                        MediaType: call.GetStringArg("mediaType"),
                        Json: call.GetStringArg("json"),
                        ComponentExampleId: null,
                        ResolvedJson: null
                    )
                );
            }
            else if (
                call.MethodName == "RequestExampleRef"
                && call.GetStringArg("componentExampleId") is not null
                && call.GetStringArg("resolvedJson") is not null
            )
            {
                requestExampleCalls.Add(
                    new PendingEndpointExampleCall(
                        StatusCode: null,
                        Name: call.GetStringArg("name"),
                        MediaType: call.GetStringArg("mediaType"),
                        Json: null,
                        ComponentExampleId: call.GetStringArg("componentExampleId"),
                        ResolvedJson: call.GetStringArg("resolvedJson")
                    )
                );
            }
            else if (
                call.MethodName == "Returns"
                && call.TypeArgs.Count == 1
                && call.StatusCodeArg is not null
            )
            {
                var tsType = typeWalker.MapType(call.TypeArgs[0]);
                responses.Add(new TsResponseType(call.StatusCodeArg.Value, tsType, call.StringArg));
            }
            else if (
                call.MethodName == "Returns"
                && call.TypeArgs.Count == 0
                && call.StatusCodeArg is not null
            )
            {
                responses.Add(new TsResponseType(call.StatusCodeArg.Value, null, call.StringArg));
            }
            else if (
                call.MethodName == "WithResponseHeader"
                && call.GetStringArg("name") is { } responseHeaderName
            )
            {
                // The convenience overload has no statusCode arg — null targets the
                // success response, resolved after the responses list is built.
                responseHeaderCalls.Add(
                    new PendingResponseHeaderCall(
                        call.GetIntArg("statusCode"),
                        responseHeaderName,
                        call.GetStringArg("description"),
                        call.GetBoolArg("required") ?? false
                    )
                );
            }
            else if (call.MethodName == "Status" && call.StatusCodeArg is not null)
            {
                if (successStatusOverride is not null)
                {
                    throw new ContractAnalysisException(
                        $"error {Diagnostics.DuplicateResponseStatus}: endpoint '{name}' calls .Status() more than once"
                    );
                }
                else
                {
                    successStatusOverride = call.StatusCodeArg.Value;
                }
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
            else if (
                call.MethodName == "ResponseExampleJson"
                && call.GetIntArg("statusCode") is int responseStatusCode
                && call.GetStringArg("json") is not null
            )
            {
                responseExampleCalls.Add(
                    new PendingEndpointExampleCall(
                        responseStatusCode,
                        call.GetStringArg("name"),
                        call.GetStringArg("mediaType"),
                        call.GetStringArg("json"),
                        null,
                        null
                    )
                );
            }
            else if (
                call.MethodName == "ResponseExampleRef"
                && call.GetIntArg("statusCode") is int refStatusCode
                && call.GetStringArg("componentExampleId") is not null
                && call.GetStringArg("resolvedJson") is not null
            )
            {
                responseExampleCalls.Add(
                    new PendingEndpointExampleCall(
                        refStatusCode,
                        call.GetStringArg("name"),
                        call.GetStringArg("mediaType"),
                        null,
                        call.GetStringArg("componentExampleId"),
                        call.GetStringArg("resolvedJson")
                    )
                );
            }
        }

        // The builder throws on this combination at runtime, but a static readonly field
        // initializer never runs at generation time — refuse loudly here too instead of
        // emitting a spec with two competing request bodies.
        if (binaryRequestContentType is not null && (acceptsFile || isFormEncoded))
        {
            var conflictingCall = acceptsFile ? ".AcceptsFile()" : ".FormEncoded()";
            throw new InvalidOperationException(
                $"{httpMethod} {route} ({controllerName}.{name}): .AcceptsBinary() cannot be combined "
                    + $"with {conflictingCall} — a request body is either raw binary or {(acceptsFile ? "multipart/form-data" : "form-encoded")}, not both."
            );
        }

        // Define.File() defaults to application/octet-stream (constructor calls ProducesFile()
        // at runtime, but the syntax walker only sees the source-level chain)
        if (isFileEndpoint)
        {
            fileContentType ??= "application/octet-stream";
        }

        // [ProducesFile] attribute on the field → file endpoint
        if (
            field
                .GetAttributes()
                .Any(a => a.AttributeClass?.Name is "ProducesFileAttribute" or "ProducesFile")
        )
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

        // Stream / FileResult return types → implicit file endpoint
        if (IsFileReturnType(tOutput))
        {
            isFileEndpoint = true;
            fileContentType ??= "application/octet-stream";
            tOutput = null; // Don't map Stream/FileResult to TS — client gets Blob
        }

        // Build return type from TOutput
        TsType? returnType = tOutput is not null ? typeWalker.MapType(tOutput) : null;

        // Build params based on HTTP method and TInput
        var (parameters, inputTypeName) = BuildParams(
            wkt,
            httpMethod,
            route,
            tInput,
            field,
            compilation,
            typeWalker,
            acceptsFile,
            binaryRequestContentType
        );

        // Add success response to responses list
        // Void endpoints with typed error responses also need a success entry
        // so the client emitter generates a discriminated union (not RivetResult<void>)
        if (returnType is not null)
        {
            var successCode =
                successStatusOverride ?? DefaultSuccessCode(httpMethod, hasOutput: true);
            responses.Insert(0, new TsResponseType(successCode, returnType));
        }
        else if (
            fileContentType is not null
            || successStatusOverride is not null
            || responses.Count > 0
            || responseHeaderCalls.Any(call => call.StatusCode is null)
            || DefaultSuccessCode(httpMethod, hasOutput: false) != 200
        )
        {
            var successCode =
                successStatusOverride ?? DefaultSuccessCode(httpMethod, hasOutput: false);
            responses.Insert(0, new TsResponseType(successCode, null));
        }

        ResponseStatusValidation.RejectContractDuplicates(responses, name);
        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));
        ApplyResponseExamples(responses, responseExampleCalls, fileContentType, name);
        ApplyResponseHeaders(
            responses,
            responseHeaderCalls,
            successStatusOverride
                ?? DefaultSuccessCode(httpMethod, hasOutput: returnType is not null),
            name
        );

        var requestExamples =
            requestExampleCalls.Count == 0
                ? null
                : requestExampleCalls
                    .Select(call =>
                        ToEndpointExample(
                            call,
                            DefaultRequestExampleMediaType(isFormEncoded, parameters)
                        )
                    )
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
            QueryAuth: queryAuth,
            BinaryRequestContentType: binaryRequestContentType,
            RequestContentTypeOverride: requestContentTypeOverride,
            ResponseContentTypeOverride: responseContentTypeOverride
        );
    }

    /// <summary>
    /// Default success status for an endpoint with no explicit .Status(...) call.
    /// Must agree with the runtime defaults in Rivet.Define (Endpoint.cs):
    /// POST → 201; DELETE without an output type → 204; DELETE with an output type → 200
    /// (204-with-body is invalid HTTP); everything else → 200.
    /// </summary>
    private static int DefaultSuccessCode(string httpMethod, bool hasOutput) =>
        httpMethod switch
        {
            "POST" => 201,
            "DELETE" when !hasOutput => 204,
            _ => 200,
        };

    private static string DefaultRequestExampleMediaType(
        bool isFormEncoded,
        IReadOnlyList<TsEndpointParam> parameters
    )
    {
        if (
            parameters.Any(parameter =>
                parameter.Source is ParamSource.File or ParamSource.FormField
            )
        )
        {
            return "multipart/form-data";
        }

        return isFormEncoded ? "application/x-www-form-urlencoded" : "application/json";
    }

    private static string DefaultResponseExampleMediaType(int statusCode, string? fileContentType)
    {
        if (fileContentType is not null && statusCode is >= 200 and < 300)
        {
            return fileContentType;
        }

        return "application/json";
    }

    private static TsEndpointExample ToEndpointExample(
        PendingEndpointExampleCall call,
        string defaultMediaType
    )
    {
        return new TsEndpointExample(
            call.MediaType ?? defaultMediaType,
            call.Name,
            call.Json,
            call.ComponentExampleId,
            call.ResolvedJson
        );
    }

    private static void ApplyResponseExamples(
        List<TsResponseType> responses,
        IReadOnlyList<PendingEndpointExampleCall> responseExampleCalls,
        string? fileContentType,
        string endpointName
    )
    {
        if (responseExampleCalls.Count == 0)
        {
            return;
        }

        foreach (var group in responseExampleCalls.GroupBy(call => call.StatusCode!.Value))
        {
            var mappedExamples = group
                .Select(call =>
                    ToEndpointExample(
                        call,
                        DefaultResponseExampleMediaType(group.Key, fileContentType)
                    )
                )
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

            Diagnostics.Warn(
                Diagnostics.ContractExampleUndeclaredStatus,
                $"ignoring response example for undeclared status {group.Key} on contract endpoint '{endpointName}'"
            );
        }

        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));
    }

    /// <summary>
    /// P2 wave 5: attaches .WithResponseHeader(...) declarations to their responses.
    /// A null status (convenience overload) targets the success status. Headers on a
    /// status no .Returns()/.Status() declared are ignored LOUDLY (RIV1017), mirroring
    /// the response-example policy.
    /// </summary>
    private static void ApplyResponseHeaders(
        List<TsResponseType> responses,
        IReadOnlyList<PendingResponseHeaderCall> responseHeaderCalls,
        int successStatusCode,
        string endpointName
    )
    {
        if (responseHeaderCalls.Count == 0)
        {
            return;
        }

        foreach (
            var group in responseHeaderCalls.GroupBy(call => call.StatusCode ?? successStatusCode)
        )
        {
            var headers = group
                .Select(call => new TsResponseHeader(call.Name, call.Description, call.Required))
                .ToList();

            var responseIndex = responses.FindIndex(response => response.StatusCode == group.Key);
            if (responseIndex >= 0)
            {
                var response = responses[responseIndex];
                var mergedHeaders = response.Headers is null
                    ? headers
                    : response.Headers.Concat(headers).ToList();
                responses[responseIndex] = response with { Headers = mergedHeaders };
                continue;
            }

            Diagnostics.Warn(
                Diagnostics.ResponseHeaderUndeclaredStatus,
                $"ignoring response header(s) {string.Join(", ", headers.Select(h => $"'{h.Name}'"))} "
                    + $"for undeclared status {group.Key} on contract endpoint '{endpointName}'"
            );
        }
    }

    private static (IReadOnlyList<TsEndpointParam> Params, string? InputTypeName) BuildParams(
        WellKnownTypes wkt,
        string httpMethod,
        string route,
        ITypeSymbol? tInput,
        IFieldSymbol field,
        Compilation compilation,
        TypeWalker typeWalker,
        bool acceptsFile = false,
        string? binaryContentType = null
    )
    {
        var routeParamNames = RouteParser.ParseRouteParamNames(route);
        // Wire-name pinning for params (FABLE_ROUNDTRIP #1/#4): a route token and a
        // C# property are the same param when they match under normalization
        // ({thing_id} ↔ ThingId, {enterprise-team} ↔ EnterpriseTeam). The param
        // always keeps the TOKEN's spelling — the route template is wire truth.
        var normalizedRouteTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in routeParamNames)
        {
            normalizedRouteTokens.TryAdd(RouteParser.NormalizeForMatching(token), token);
        }

        var parameters = new List<TsEndpointParam>();
        var hasBody = httpMethod is "POST" or "PUT" or "PATCH";
        // .AcceptsBinary(): the request body is the raw bytes (host code reads the
        // stream), so TInput never lowers to a JSON body — its properties become
        // route/query params exactly like a GET/DELETE input.
        var lowersBody = hasBody && binaryContentType is null;
        string? inputTypeName = null;

        // P2 wave 5: [RivetHeader] properties are header params on every HTTP method —
        // classified BEFORE the route/query/body split so a header never leaks into the
        // body schema (TypeWalker skips them) or the query string.
        if (tInput is not null)
        {
            foreach (var prop in typeWalker.GetEffectiveProperties(tInput))
            {
                if (
                    typeWalker.IsJsonIgnored(prop)
                    || TypeWalker.GetHeaderName(prop) is not { } headerName
                )
                {
                    continue;
                }

                parameters.Add(
                    new TsEndpointParam(
                        headerName,
                        typeWalker.MapType(prop.Type),
                        ParamSource.Header,
                        IsOptional: TypeWalker.IsOptionalProperty(prop)
                    )
                );
            }
        }

        if (lowersBody)
        {
            var requestBodyType = GetRequestBodyType(field, compilation, tInput, route, typeWalker);

            // Route params from template — try to match types from TInput properties
            var routeMatchedProps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var paramName in routeParamNames)
            {
                TsType paramType = new TsType.Primitive("string");
                string? bodyPropertyName = null;
                if (tInput is not null)
                {
                    // A3: match against the flattened property surface (incl. inherited)
                    var normalized = RouteParser.NormalizeForMatching(paramName);
                    var matchingProp = typeWalker
                        .GetEffectiveProperties(tInput)
                        .FirstOrDefault(p =>
                            RouteParser.NormalizeForMatching(p.Name) == normalized
                        );
                    if (matchingProp is not null)
                    {
                        paramType = typeWalker.MapType(matchingProp.Type);
                        routeMatchedProps.Add(matchingProp.Name);
                        if (requestBodyType is null)
                        {
                            bodyPropertyName =
                                typeWalker.GetJsonPropertyName(matchingProp)
                                ?? Naming.ToCamelCase(matchingProp.Name);
                        }
                    }
                    else
                    {
                        Diagnostics.Warn(
                            Diagnostics.RouteTokenWithoutInputProperty,
                            $"route token '{{{paramName}}}' on {httpMethod} {route} has no matching property "
                                + $"on input type '{tInput.Name}' — emitted as an untyped string path param."
                        );
                    }
                }
                parameters.Add(
                    new TsEndpointParam(
                        paramName,
                        paramType,
                        ParamSource.Route,
                        BodyPropertyName: bodyPropertyName
                    )
                );
            }

            // .AcceptsFile() on the contract — add a File param
            if (acceptsFile)
            {
                parameters.Add(
                    new TsEndpointParam("file", new TsType.Primitive("File"), ParamSource.File)
                );
            }

            if (tInput is not null)
            {
                // Check if TInput itself is IFormFile
                if (IsFormFileType(wkt, tInput))
                {
                    parameters.Add(
                        new TsEndpointParam("file", new TsType.Primitive("File"), ParamSource.File)
                    );
                }
                // Check if TInput is a record containing IFormFile properties
                else if (HasFormFileProperty(wkt, typeWalker, tInput))
                {
                    inputTypeName = tInput.Name;
                    // A3: walk the flattened property surface (incl. inherited)
                    foreach (var prop in typeWalker.GetEffectiveProperties(tInput))
                    {
                        if (typeWalker.IsJsonIgnored(prop))
                        {
                            continue;
                        }

                        // [RivetHeader] properties were already emitted as header params
                        if (TypeWalker.GetHeaderName(prop) is not null)
                        {
                            continue;
                        }

                        // Skip properties already emitted as route params
                        if (routeMatchedProps.Contains(prop.Name))
                        {
                            continue;
                        }

                        var tsName =
                            typeWalker.GetJsonPropertyName(prop) ?? Naming.ToCamelCase(prop.Name);

                        if (IsFormFileType(wkt, prop.Type))
                        {
                            parameters.Add(
                                new TsEndpointParam(
                                    tsName,
                                    new TsType.Primitive("File"),
                                    ParamSource.File
                                )
                            );
                        }
                        else if (typeWalker.IsCollectionOf(prop.Type, wkt.IFormFile))
                        {
                            // FABLE_GAPS §7 item 12: List<IFormFile>/IFormFile[] →
                            // multipart array-of-binary part, consistent with single files
                            parameters.Add(
                                new TsEndpointParam(
                                    tsName,
                                    new TsType.Array(new TsType.Primitive("File")),
                                    ParamSource.File
                                )
                            );
                        }
                        else
                        {
                            // Non-file properties on a mixed upload record → form fields
                            parameters.Add(
                                new TsEndpointParam(
                                    tsName,
                                    typeWalker.MapType(prop.Type),
                                    ParamSource.FormField
                                )
                            );
                        }
                    }
                }
                else
                {
                    // FABLE_ROUNDTRIP #4: an input whose every property is route-bound
                    // has no body left to carry — emitting one anyway fabricated a
                    // required JSON body on bodyless POST/PUTs (66 github-corpus ops).
                    var bodyProps = typeWalker
                        .GetEffectiveProperties(tInput)
                        .Where(p =>
                            !typeWalker.IsJsonIgnored(p) && TypeWalker.GetHeaderName(p) is null
                        )
                        .ToList();
                    if (
                        bodyProps.Count == 0
                        || bodyProps.Any(p => !routeMatchedProps.Contains(p.Name))
                    )
                    {
                        // Normal body param
                        var tsType = requestBodyType ?? typeWalker.MapType(tInput);
                        parameters.Add(new TsEndpointParam("body", tsType, ParamSource.Body));
                    }
                }
            }
        }
        else
        {
            // GET/DELETE (and .AcceptsBinary() bodies): TInput properties matched by name
            // to route → Route, remaining → Query — never a JSON body param
            if (tInput is not null && !typeWalker.IsParamLowerable(tInput))
            {
                // FABLE_ROUNDTRIP cross-corpus #1: walking a dictionary/collection/scalar
                // input here enumerated its CLR members (Count, Keys, Comparer, …) into
                // the emitted spec as invented query params. Drop the input LOUDLY and
                // keep the route tokens as untyped path params.
                Diagnostics.Warn(
                    Diagnostics.InputTypeNotParamLowerable,
                    $"input type '{tInput.ToDisplayString()}' on {httpMethod} {route} has no property surface "
                        + "to lower to query params (dictionary/collection/scalar) — input dropped; "
                        + "route tokens emitted as untyped string path params."
                );
                foreach (var paramName in routeParamNames)
                {
                    parameters.Add(
                        new TsEndpointParam(
                            paramName,
                            new TsType.Primitive("string"),
                            ParamSource.Route
                        )
                    );
                }
            }
            else if (tInput is not null)
            {
                inputTypeName = tInput.Name;
                var matchedRouteParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // A3: walk the flattened property surface (incl. inherited)
                foreach (var prop in typeWalker.GetEffectiveProperties(tInput))
                {
                    if (typeWalker.IsJsonIgnored(prop))
                    {
                        continue;
                    }

                    // [RivetHeader] properties were already emitted as header params
                    if (TypeWalker.GetHeaderName(prop) is not null)
                    {
                        continue;
                    }

                    var jsonName = typeWalker.GetJsonPropertyName(prop);
                    var tsName = jsonName ?? Naming.ToCamelCase(prop.Name);

                    var isFormFile = SymbolEqualityComparer.Default.Equals(
                        prop.Type,
                        wkt.IFormFile
                    );
                    if (isFormFile)
                    {
                        parameters.Add(
                            new TsEndpointParam(
                                tsName,
                                new TsType.Primitive("File"),
                                ParamSource.File
                            )
                        );
                        continue;
                    }

                    var tsType = typeWalker.MapType(prop.Type);
                    // Route matching uses the normalized C# property name ({thing_id}
                    // matches ThingId), never the JSON name — the token is wire truth
                    if (
                        normalizedRouteTokens.TryGetValue(
                            RouteParser.NormalizeForMatching(prop.Name),
                            out var routeName
                        )
                    )
                    {
                        matchedRouteParams.Add(routeName);

                        // A14: a route-bound param must keep the ROUTE name — runtime route
                        // binding uses the C# property name, so a [JsonPropertyName] rename
                        // would leave the {token} uninterpolated in every client.
                        if (jsonName is not null && jsonName != routeName)
                        {
                            Diagnostics.Warn(
                                Diagnostics.RouteBoundJsonPropertyNameIgnored,
                                $"[JsonPropertyName(\"{jsonName}\")] on route-bound property '{prop.Name}' "
                                    + $"is ignored for route interpolation — the contract param keeps the route name '{routeName}'."
                            );
                        }

                        parameters.Add(
                            new TsEndpointParam(
                                routeName,
                                tsType,
                                ParamSource.Route,
                                BodyPropertyName: tsName
                            )
                        );
                        continue;
                    }

                    // E8: surface property-level optionality ([RivetOptional], nullability)
                    // on the param so emitters mark non-nullable optionals required: false
                    parameters.Add(
                        new TsEndpointParam(
                            tsName,
                            tsType,
                            ParamSource.Query,
                            IsOptional: TypeWalker.IsOptionalProperty(prop)
                        )
                    );
                }

                // Add route params that have no matching TInput property (default to string)
                foreach (var paramName in routeParamNames)
                {
                    if (!matchedRouteParams.Contains(paramName))
                    {
                        Diagnostics.Warn(
                            Diagnostics.RouteTokenWithoutInputProperty,
                            $"route token '{{{paramName}}}' on {httpMethod} {route} has no matching property "
                                + $"on input type '{tInput.Name}' — emitted as an untyped string path param."
                        );
                        parameters.Insert(
                            0,
                            new TsEndpointParam(
                                paramName,
                                new TsType.Primitive("string"),
                                ParamSource.Route
                            )
                        );
                    }
                }
            }
            else
            {
                // No TInput but might have route params (e.g. DELETE with route params)
                foreach (var paramName in routeParamNames)
                {
                    parameters.Add(
                        new TsEndpointParam(
                            paramName,
                            new TsType.Primitive("string"),
                            ParamSource.Route
                        )
                    );
                }
            }
        }

        return (parameters, inputTypeName);
    }

    private static TsType? GetRequestBodyType(
        IFieldSymbol field,
        Compilation compilation,
        ITypeSymbol? inputType,
        string route,
        TypeWalker typeWalker
    )
    {
        var attributeType = compilation.GetTypeByMetadataName("Rivet.RivetRequestBodyAttribute");
        if (attributeType is null)
        {
            return null;
        }

        var attribute = field
            .GetAttributes()
            .FirstOrDefault(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attributeType)
            );
        if (attribute?.ConstructorArguments is not [{ Value: ITypeSymbol bodyType }, ..])
        {
            return null;
        }

        if (
            inputType is null
            || !IsCompatibleRequestBodyType(bodyType, inputType, route, typeWalker)
        )
        {
            throw new ContractAnalysisException(
                $"error {Diagnostics.InvalidRequestBodyProvenance}: endpoint '{field.ContainingType.Name}.{field.Name}' "
                    + $"declares request body type '{bodyType.Name}', which is not represented independently by its input type '{inputType?.Name}'"
            );
        }

        var mappedType = typeWalker.MapType(bodyType);
        var isRequired =
            attribute.ConstructorArguments.Length < 2
            || attribute.ConstructorArguments[1].Value is not false;
        return isRequired || mappedType is TsType.Nullable
            ? mappedType
            : new TsType.Nullable(mappedType);
    }

    private static bool IsCompatibleRequestBodyType(
        ITypeSymbol bodyType,
        ITypeSymbol inputType,
        string route,
        TypeWalker typeWalker
    )
    {
        var routeNames = RouteParser
            .ParseRouteParamNames(route)
            .Select(RouteParser.NormalizeForMatching)
            .ToHashSet(StringComparer.Ordinal);
        if (
            typeWalker
                .GetEffectiveProperties(inputType)
                .Any(property =>
                    !typeWalker.IsJsonIgnored(property)
                    && TypeWalker.GetHeaderName(property) is null
                    && !routeNames.Contains(RouteParser.NormalizeForMatching(property.Name))
                    && SymbolEqualityComparer.Default.Equals(property.Type, bodyType)
                )
        )
        {
            return true;
        }

        var inputProperties = typeWalker
            .GetEffectiveProperties(inputType)
            .Where(property =>
                !typeWalker.IsJsonIgnored(property) && TypeWalker.GetHeaderName(property) is null
            )
            .GroupBy(
                property =>
                    typeWalker.GetJsonPropertyName(property) ?? Naming.ToCamelCase(property.Name),
                StringComparer.Ordinal
            )
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var bodyProperties = typeWalker
            .GetEffectiveProperties(bodyType)
            .Where(property =>
                !typeWalker.IsJsonIgnored(property) && TypeWalker.GetHeaderName(property) is null
            )
            .ToList();
        if (bodyProperties.Count == 0)
        {
            return false;
        }

        foreach (var bodyProperty in bodyProperties)
        {
            var wireName =
                typeWalker.GetJsonPropertyName(bodyProperty)
                ?? Naming.ToCamelCase(bodyProperty.Name);
            if (!inputProperties.TryGetValue(wireName, out var matches) || matches.Count != 1)
            {
                return false;
            }

            var inputProperty = matches[0];
            if (
                routeNames.Contains(RouteParser.NormalizeForMatching(inputProperty.Name))
                || !SymbolEqualityComparer.Default.Equals(bodyProperty.Type, inputProperty.Type)
                || TypeWalker.IsOptionalProperty(bodyProperty)
                    != TypeWalker.IsOptionalProperty(inputProperty)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFormFileType(WellKnownTypes wkt, ITypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type, wkt.IFormFile);

    private static bool HasFormFileProperty(
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        ITypeSymbol type
    ) =>
        // A3: consider inherited properties too. Collections of IFormFile count —
        // a record whose ONLY files were List<IFormFile> used to emit as JSON with
        // format:binary strings, an unimplementable spec (FABLE_GAPS §7 item 12).
        typeWalker
            .GetEffectiveProperties(type)
            .Any(p =>
                IsFormFileType(wkt, p.Type) || typeWalker.IsCollectionOf(p.Type, wkt.IFormFile)
            );

    /// <summary>
    /// Checks if the return type is a known file/stream type that should be treated
    /// as a file endpoint (Stream, FileResult, FileStreamResult, etc.).
    /// </summary>
    private static bool IsFileReturnType(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        var ns = type.ContainingNamespace?.ToDisplayString();
        return type.Name switch
        {
            "Stream" when ns is "System.IO" => true,
            "FileResult"
            or "FileStreamResult"
            or "FileContentResult"
            or "PhysicalFileResult" when ns is "Microsoft.AspNetCore.Mvc" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Checks if the type is a (byte[], string) tuple — used for named file downloads
    /// when the field is marked with [ProducesFile].
    /// </summary>
    private static bool IsByteArrayStringTuple(ITypeSymbol? type)
    {
        if (
            type is not INamedTypeSymbol named
            || !named.IsTupleType
            || named.TupleElements.Length != 2
        )
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

        if (
            fieldType is INamedTypeSymbol named
            && named.Name is "RouteDefinition" or "InputRouteDefinition" or "FileRouteDefinition"
            && named.ContainingNamespace?.ToDisplayString() == "Rivet"
        )
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
            IReadOnlyDictionary<string, object> constantArgs
        )
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

        public bool? GetBoolArg(string parameterName) =>
            ConstantArgs.TryGetValue(parameterName, out var value) && value is bool flag
                ? flag
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
        string? ResolvedJson
    );

    /// <summary>A .WithResponseHeader(...) call; null StatusCode = the success status.</summary>
    private sealed record PendingResponseHeaderCall(
        int? StatusCode,
        string Name,
        string? Description,
        bool Required
    );

    /// <summary>
    /// Walks the invocation chain from the initializer expression using syntax + GetSymbolInfo.
    /// Returns calls in order: root factory call first, then chained builder calls.
    /// </summary>
    private static List<ChainedCall> CollectInvocationChain(
        ExpressionSyntax expression,
        SemanticModel semanticModel
    )
    {
        var calls = new List<ChainedCall>();
        CollectInvocationsRecursive(expression, semanticModel, calls);
        calls.Reverse(); // Root call first
        return calls;
    }

    private static void CollectInvocationsRecursive(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        List<ChainedCall> calls
    )
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

        calls.Add(new ChainedCall(method.Name, typeArgs, constantArgs));

        // Recurse into the receiver (the expression the method is called on)
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            CollectInvocationsRecursive(memberAccess.Expression, semanticModel, calls);
        }
    }

    private static IParameterSymbol? ResolveParameter(
        IMethodSymbol method,
        ArgumentSyntax argument,
        int ordinal
    )
    {
        if (argument.NameColon is not null)
        {
            var name = argument.NameColon.Name.Identifier.ValueText;
            return method.Parameters.FirstOrDefault(parameter => parameter.Name == name);
        }

        return ordinal < method.Parameters.Length ? method.Parameters[ordinal] : null;
    }
}
