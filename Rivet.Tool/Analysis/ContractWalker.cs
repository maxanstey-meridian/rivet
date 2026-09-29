using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>A contract endpoint and the field it was read from (null for an abstract method).</summary>
public sealed record ContractEndpoint(TsEndpointDefinition Endpoint, IFieldSymbol? Field);

/// <summary>
/// Discovers [RivetContract]-attributed static classes and extracts endpoint definitions
/// from their static readonly RouteDefinition fields by reading the builder chain via Roslyn operations.
/// </summary>
public static class ContractWalker
{
    /// <summary>
    /// Discovers endpoints from [RivetContract] classes, each with the endpoint field it
    /// was read from (null for an abstract-method contract endpoint).
    /// Use SymbolDiscovery.Discover() to obtain the contract type list.
    /// </summary>
    public static IReadOnlyList<ContractEndpoint> Walk(
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

        var endpoints = new List<ContractEndpoint>();

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

                    var endpoint = EndpointWalker.BuildControllerEndpoint(
                        method,
                        controllerName,
                        isContract: true,
                        wkt,
                        typeWalker
                    );
                    if (endpoint is not null)
                    {
                        endpoints.Add(new ContractEndpoint(endpoint, null));
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
                    endpoints.Add(new ContractEndpoint(endpoint, field));
                }
            }
        }

        return endpoints;
    }

    private static TsEndpointDefinition? BuildEndpointFromField(
        IFieldSymbol field,
        string controllerName,
        Compilation compilation,
        WellKnownTypes wkt,
        TypeWalker typeWalker
    )
    {
        if (ReadBuilderChain(field, compilation, wkt) is not [var root, .. var builderCalls])
        {
            return null;
        }

        var route = root.StringArg("route");
        if (route is null)
        {
            return null;
        }

        var isFileEndpoint = root.TargetMethod.Name == "File";
        var httpMethod = isFileEndpoint ? "GET" : root.TargetMethod.Name.ToUpperInvariant();
        route = RouteParser.StripRouteConstraints(route);

        var name = Naming.ToCamelCase(field.Name);
        var provenance = OpenApiProvenanceWalker.ReadOperation(compilation, field);

        // Define.Get<TInput, TOutput> carries both types; a single type argument is the
        // output, except on Define.File<TInput>, where it is the input.
        var rootTypeArgs = root.TargetMethod.TypeArguments;
        ITypeSymbol? tInput = rootTypeArgs switch
        {
            [var input, _] => input,
            [var input] when isFileEndpoint => input,
            _ => null,
        };
        ITypeSymbol? tOutput = rootTypeArgs switch
        {
            [_, var output] => output,
            [var output] when !isFileEndpoint => output,
            _ => null,
        };

        var responses = new List<TsResponseType>();
        var requestExampleCalls = new List<PendingEndpointExampleCall>();
        var responseExampleCalls = new List<PendingEndpointExampleCall>();
        var responseHeaderCalls = new List<PendingResponseHeaderCall>();
        var requestContents = new List<TsMediaTypeContent>();
        var requestContentsAuthoritative = false;
        var responseContents = new List<(string StatusKey, TsMediaTypeContent Content)>();
        var declaredParameters = new List<TsEndpointParam>();
        int? successStatusOverride = null;
        string? successStatusKey = null;
        string? successResponseDescription = null;
        var suppressImplicitResponse = false;
        string? endpointSummary = null;
        string? endpointDescription = null;
        EndpointSecurity? security = null;
        SecurityRequirements? securityRequirements = null;
        var securityRequirementSchemes =
            new SortedDictionary<int, Dictionary<string, List<string>>>();
        var securityRequirementOrders = new HashSet<int>();
        bool? requestBodyRequired = null;
        var requestBodyPresent = false;
        var acceptsFile = false;
        var isFormEncoded = false;
        string? fileContentType = null;
        string? binaryRequestContentType = null;
        string? requestContentTypeOverride = null;
        string? responseContentTypeOverride = null;
        QueryAuthMetadata? queryAuth = null;

        TsMediaTypeContent SchemaContent(IInvocationOperation call, string mediaType, string label)
        {
            var schemaType = call.StringArg("schemaType");
            var format = call.StringArg("format");
            return new TsMediaTypeContent(
                mediaType,
                typeWalker.ApplyGeneratedSchemaRef(
                    typeWalker
                        .MapType(call.TargetMethod.TypeArguments[0])
                        .WithLeaf(schemaType, format),
                    call.StringArg("schemaRef"),
                    $"{label} '{mediaType}' on endpoint '{name}'"
                ),
                SchemaType: schemaType,
                Format: format == "" ? null : format,
                IsFormatSpecified: format is not null,
                SchemaDescription: call.StringArg("schemaDescription")
            );
        }

        foreach (var call in builderCalls)
        {
            var typeArgs = call.TargetMethod.TypeArguments;
            switch (call.TargetMethod.Name)
            {
                case "Accepts" when typeArgs.Length == 1:
                    tInput = typeArgs[0];
                    break;
                case "AcceptsFile":
                    acceptsFile = true;
                    requestBodyPresent = true;
                    break;
                case "FormEncoded":
                    isFormEncoded = true;
                    requestBodyPresent = true;
                    break;
                case "AcceptsBinary":
                    binaryRequestContentType =
                        call.StringArg("contentType") ?? "application/octet-stream";
                    requestBodyPresent = true;
                    requestContentsAuthoritative = true;
                    requestContents.Add(
                        new TsMediaTypeContent(binaryRequestContentType, null, IsBinary: true)
                    );
                    break;
                case "AcceptsContentType" when call.StringArg("contentType") is { } contentType:
                    requestContentTypeOverride = contentType;
                    break;
                case "ProducesContentType" when call.StringArg("contentType") is { } contentType:
                    responseContentTypeOverride = contentType;
                    break;
                case "RequestExampleJson" or "RequestExampleRef" when IsCompleteExample(call):
                    requestExampleCalls.Add(ToExampleCall(call, statusKey: null));
                    break;
                case "ResponseExampleJson"
                or "ResponseExampleRef" when call.Status() is { } status && IsCompleteExample(call):
                    responseExampleCalls.Add(ToExampleCall(call, status.Key));
                    break;
                case "Returns" when call.Status() is { } status:
                    responses.Add(
                        new TsResponseType(
                            status.Code,
                            typeArgs.Length == 1 ? typeWalker.MapType(typeArgs[0]) : null,
                            call.StringArg("description"),
                            StatusKey: status.DeclaredKey
                        )
                    );
                    break;
                case "WithResponseHeader"
                or "WithResponseHeaderKey" when call.StringArg("name") is { } headerName:
                    // The convenience overload has no status — null targets the success
                    // response, resolved after the responses list is built.
                    responseHeaderCalls.Add(
                        new PendingResponseHeaderCall(
                            call.Status()?.Key,
                            headerName,
                            typeArgs.Length == 1
                                ? typeWalker
                                    .MapType(typeArgs[0])
                                    .WithLeaf(
                                        call.StringArg("schemaType"),
                                        call.StringArg("format")
                                    )
                                : new TsType.Primitive("string"),
                            call.StringArg("description"),
                            call.BoolArg("required") ?? false,
                            ParseJsonArgument(call.StringArg("schemaExamplesJson")),
                            ParseJsonArgument(call.StringArg("exampleJson")),
                            ParseJsonArgument(call.StringArg("examplesJson")),
                            call.BoolArg("deprecated") ?? false,
                            call.StringArg("style"),
                            call.BoolArg("explode"),
                            call.BoolArg("allowReserved") ?? false,
                            call.BoolArg("allowEmptyValue") ?? false,
                            call.StringArg("contentType")
                        )
                    );
                    break;
                case "Status" when call.IntArg("statusCode") is int statusCode:
                    if (successStatusOverride is not null)
                    {
                        throw new RivetUserException(
                            $"error {Diagnostics.DuplicateResponseStatus}: endpoint '{name}' calls .Status() more than once"
                        );
                    }
                    successStatusOverride = statusCode;
                    break;
                case "SuppressImplicitResponse":
                    suppressImplicitResponse = true;
                    break;
                case "StatusKey" when call.StringArg("statusKey") is { } statusKey:
                    successStatusKey = statusKey;
                    successResponseDescription = call.StringArg("description");
                    break;
                case "Summary" when call.StringArg("summary") is { } summary:
                    endpointSummary = summary;
                    break;
                case "Description" when call.StringArg("description") is { } description:
                    endpointDescription = description;
                    break;
                case "Anonymous":
                    security = new EndpointSecurity(true);
                    break;
                case "Secure" when call.StringArg("scheme") is { } scheme:
                    security = new EndpointSecurity(false, scheme);
                    break;
                case "SecurityRequirements":
                    securityRequirements = new SecurityRequirements([]);
                    break;
                case "SecurityRequirement" when call.IntArg("requirementOrder") is int order:
                    securityRequirementOrders.Add(order);
                    if (call.StringArg("scheme") is { } requirementScheme)
                    {
                        if (!securityRequirementSchemes.TryGetValue(order, out var schemes))
                        {
                            schemes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                            securityRequirementSchemes.Add(order, schemes);
                        }
                        if (!schemes.TryGetValue(requirementScheme, out var scopes))
                        {
                            scopes = [];
                            schemes.Add(requirementScheme, scopes);
                        }
                        if (call.StringArg("scope") is { } scope)
                        {
                            scopes.Add(scope);
                        }
                    }
                    break;
                case "RequestContent" when call.StringArg("mediaType") is { } mediaType:
                    requestContents.Add(
                        typeArgs.Length == 1
                            ? SchemaContent(call, mediaType, "Request content")
                            : new TsMediaTypeContent(mediaType, null)
                    );
                    requestBodyPresent = true;
                    requestContentsAuthoritative = true;
                    break;
                case "RequestBinaryContent" when call.StringArg("mediaType") is { } mediaType:
                    if (
                        !requestContents.Any(content =>
                            content.MediaType == mediaType && content.IsBinary
                        )
                    )
                    {
                        requestContents.Add(
                            new TsMediaTypeContent(mediaType, null, IsBinary: true)
                        );
                    }
                    requestBodyPresent = true;
                    requestContentsAuthoritative = true;
                    break;
                case "RequestBodyRequired":
                    requestBodyRequired = call.BoolArg("required");
                    requestBodyPresent = true;
                    break;
                case "RequestBody":
                    requestBodyPresent = true;
                    requestContentsAuthoritative = true;
                    break;
                case "Parameter"
                    when typeArgs.Length == 1
                        && call.StringArg("name") is { } parameterName
                        && call.StringArg("location") is { } parameterLocation
                        && call.BoolArg("required") is { } parameterRequired:
                    declaredParameters.Add(
                        ToDeclaredParameter(
                            call,
                            parameterName,
                            parameterLocation,
                            parameterRequired,
                            name,
                            typeWalker
                        )
                    );
                    break;
                case "ResponseContent"
                    when call.Status() is { } status
                        && call.StringArg("mediaType") is { } mediaType:
                    responseContents.Add(
                        (
                            status.Key,
                            typeArgs.Length == 1
                                ? SchemaContent(call, mediaType, "Response content")
                                : new TsMediaTypeContent(mediaType, null)
                        )
                    );
                    break;
                case "ResponseBinaryContent"
                    when call.Status() is { } status
                        && call.StringArg("mediaType") is { } mediaType:
                    responseContents.Add(
                        (status.Key, new TsMediaTypeContent(mediaType, null, IsBinary: true))
                    );
                    break;
                case "ProducesFile":
                    fileContentType = call.StringArg("contentType") ?? "application/octet-stream";
                    break;
                case "ContentType":
                    fileContentType = call.StringArg("mediaType") ?? "application/octet-stream";
                    break;
                case "QueryAuth":
                    queryAuth = new QueryAuthMetadata(call.StringArg("parameterName") ?? "token");
                    break;
            }
        }

        // The builder throws on this combination at runtime, but a static readonly field
        // initializer never runs at generation time — refuse loudly here too instead of
        // emitting a spec with two competing request bodies.
        if (binaryRequestContentType is not null && (acceptsFile || isFormEncoded))
        {
            var conflictingCall = acceptsFile ? ".AcceptsFile()" : ".FormEncoded()";
            throw new RivetUserException(
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
        if (field.HasAttribute(wkt.ProducesFile))
        {
            fileContentType ??= "application/octet-stream";
        }

        // Ordinary byte[] is a JSON value: System.Text.Json serializes byte[] as a
        // base64 JSON string, so an ordinary Define.Get<byte[]> response emits the
        // base64 string schema (TypeWalker's mapping) — NOT a binary file. Only an
        // explicit file declaration ([ProducesFile]/.ProducesFile/Define.File) makes
        // it a file: then a plain byte[] (or a (byte[], string) named tuple) maps to
        // no TS type and the client gets Blob.
        if (fileContentType is not null)
        {
            if (IsByteArrayStringTuple(tOutput) || IsPlainByteArray(tOutput))
            {
                tOutput = null; // Explicit file payload — don't map to TS, client gets Blob
            }
        }

        // No implicit file contract: a Stream/FileResult output type does not make an
        // ordinary Define.* endpoint a binary file endpoint. File behavior requires an
        // explicit declaration — Define.File(), .ProducesFile(...) or [ProducesFile]
        // through the contract surface above (acceptance:no-implicit-contract-file-by-
        // output-type). An undeclared Stream output is an unresolved contract; the
        // return-type mapping below surfaces it.

        // Build return type from TOutput
        TsType? returnType = tOutput is not null ? typeWalker.MapType(tOutput) : null;

        var declaredRequestBody = ReadDeclaredRequestBody(field, wkt);
        var (builtParameters, inputTypeName) = BuildParams(
            wkt,
            httpMethod,
            route,
            tInput,
            field,
            declaredRequestBody,
            typeWalker,
            acceptsFile,
            binaryRequestContentType,
            declaredParameters,
            requestBodyPresent,
            requestContents.Count > 0 && requestContents.All(content => content.IsBinary)
        );
        var parameters = builtParameters.ToList();
        requestBodyPresent |= parameters.Any(parameter =>
            parameter.Source is ParamSource.Body or ParamSource.File or ParamSource.FormField
        );

        requestBodyRequired ??= declaredRequestBody?.Required;

        foreach (var declaredParameter in declaredParameters)
        {
            var hasWireNameCollision =
                declaredParameters.Count(parameter => parameter.Name == declaredParameter.Name) > 1;
            parameters.RemoveAll(parameter =>
                parameter.Source == declaredParameter.Source
                && (
                    parameter.Name == declaredParameter.Name
                    || hasWireNameCollision
                        && IsGeneratedCollisionName(parameter.Name, declaredParameter.Name)
                )
            );
            parameters.Add(declaredParameter);
        }

        // Add success response to responses list
        // Void endpoints with typed error responses also need a success entry
        // so the client emitter generates a discriminated union (not RivetResult<void>)
        if (returnType is not null)
        {
            var successCode =
                successStatusOverride ?? DefaultSuccessCode(httpMethod, hasOutput: true);
            responses.Insert(
                0,
                new TsResponseType(
                    successCode,
                    returnType,
                    successResponseDescription,
                    StatusKey: successStatusKey
                )
            );
        }
        else if (!suppressImplicitResponse)
        {
            var successCode =
                successStatusOverride ?? DefaultSuccessCode(httpMethod, hasOutput: false);
            responses.Insert(
                0,
                new TsResponseType(
                    successCode,
                    null,
                    successResponseDescription,
                    StatusKey: successStatusKey
                )
            );
        }

        ResponseStatusValidation.RejectContractDuplicates(responses, name);
        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));
        EndpointWalker.ApplyResponseExamples(
            responses,
            responseExampleCalls
                .Select(call =>
                    (
                        call.StatusKey!,
                        ToEndpointExample(
                            call,
                            DefaultResponseExampleMediaType(
                                ParseStatusCode(call.StatusKey!),
                                fileContentType
                            )
                        )
                    )
                )
                .ToList(),
            Diagnostics.ContractExampleUndeclaredStatus,
            $"contract endpoint '{name}'"
        );
        ApplyResponseHeaders(
            responses,
            responseHeaderCalls,
            successStatusOverride
                ?? DefaultSuccessCode(httpMethod, hasOutput: returnType is not null),
            successStatusKey,
            name
        );
        // Explicit additional representations must not erase the file factory's
        // success representation: runtime always includes the configured file format.
        var fileSuccessKey =
            successStatusKey
            ?? (
                successStatusOverride
                ?? DefaultSuccessCode(httpMethod, hasOutput: returnType is not null)
            ).ToString();
        if (
            fileContentType is not null
            && !suppressImplicitResponse
            && responseContents.Any(item => item.StatusKey == fileSuccessKey)
            && !responseContents.Any(item =>
                item.StatusKey == fileSuccessKey
                && string.Equals(
                    item.Content.MediaType,
                    fileContentType,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            responseContents.Insert(
                0,
                (fileSuccessKey, new TsMediaTypeContent(fileContentType, null, IsBinary: true))
            );
        }
        ApplyResponseContents(responses, responseContents);

        var requestExamples =
            requestExampleCalls.Count == 0
                ? null
                : requestExampleCalls
                    .Select(call =>
                        ToEndpointExample(
                            call,
                            EndpointWalker.DefaultRequestExampleMediaType(parameters, isFormEncoded)
                        )
                    )
                    .ToList();

        if (securityRequirementOrders.Count > 0)
        {
            securityRequirements = new SecurityRequirements(
                securityRequirementOrders
                    .Order()
                    .Select(order => new SecurityRequirement(
                        (securityRequirementSchemes.GetValueOrDefault(order) ?? [])
                            .Select(pair => new SecurityRequirementScheme(pair.Key, pair.Value))
                            .ToList()
                    ))
                    .ToList()
            );
        }

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
            ResponseContentTypeOverride: responseContentTypeOverride,
            SecurityRequirements: securityRequirements,
            RequestContents: requestContentsAuthoritative ? requestContents : null,
            RequestBodyRequired: requestBodyRequired,
            RequestBodyPresent: requestBodyPresent,
            Provenance: provenance
        );
    }

    private static void ApplyResponseContents(
        List<TsResponseType> responses,
        IReadOnlyList<(string StatusKey, TsMediaTypeContent Content)> contents
    )
    {
        foreach (
            var group in contents.GroupBy(item => item.StatusKey, StringComparer.OrdinalIgnoreCase)
        )
        {
            var index = responses.FindIndex(response =>
                response.EffectiveStatusKey.Equals(group.Key, StringComparison.OrdinalIgnoreCase)
            );
            var mapped = group.Select(item => item.Content).ToList();
            if (index < 0)
            {
                responses.Add(
                    new TsResponseType(
                        ParseStatusCode(group.Key),
                        null,
                        Contents: mapped,
                        StatusKey: group.Key
                    )
                );
                continue;
            }

            responses[index] = responses[index] with { Contents = mapped };
        }

        responses.Sort((left, right) => left.StatusCode.CompareTo(right.StatusCode));
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

    private static int ParseStatusCode(string statusKey) =>
        int.TryParse(statusKey, out var statusCode) ? statusCode : 0;

    private static bool IsGeneratedCollisionName(string candidate, string wireName)
    {
        if (
            !candidate.StartsWith(wireName + "_", StringComparison.Ordinal)
            || candidate.Length == wireName.Length + 1
        )
        {
            return false;
        }

        return candidate[(wireName.Length + 1)..].All(char.IsDigit);
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
            call.ResolvedJson,
            call.ReferencedComponents
        );
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
        string? successStatusKey,
        string endpointName
    )
    {
        if (responseHeaderCalls.Count == 0)
        {
            return;
        }

        foreach (
            var group in responseHeaderCalls.GroupBy(
                call => call.StatusKey ?? successStatusKey ?? successStatusCode.ToString(),
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            var headers = group
                .Select(call => new TsResponseHeader(
                    call.Name,
                    call.Type,
                    call.Description,
                    call.Required,
                    call.Deprecated,
                    call.SchemaExamples,
                    call.Example,
                    call.Examples,
                    call.Style,
                    call.Explode,
                    call.AllowReserved,
                    call.AllowEmptyValue,
                    call.ContentType
                ))
                .ToList();

            var responseIndex = responses.FindIndex(response =>
                response.EffectiveStatusKey.Equals(group.Key, StringComparison.OrdinalIgnoreCase)
            );
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
        DeclaredRequestBody? declaredRequestBody,
        TypeWalker typeWalker,
        bool acceptsFile = false,
        string? binaryContentType = null,
        IReadOnlyList<TsEndpointParam>? declaredParameters = null,
        bool hasDeclaredRequestBody = false,
        bool hasOnlyBinaryRequestBody = false
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
        var hasBody =
            hasDeclaredRequestBody
            || (
                httpMethod is "POST" or "PUT" or "PATCH" && declaredParameters is not { Count: > 0 }
            );
        // .AcceptsBinary(): the request body is the raw bytes (host code reads the
        // stream), so TInput never lowers to a JSON body — its properties become
        // route/query params exactly like a GET/DELETE input.
        var lowersBody = hasBody && binaryContentType is null && !hasOnlyBinaryRequestBody;
        string? inputTypeName = null;

        // P2 wave 5: [RivetHeader] properties are header params on every HTTP method —
        // classified BEFORE the route/query/body split so a header never leaks into the
        // body schema (TypeWalker skips them) or the query string.
        if (tInput is not null)
        {
            foreach (var prop in typeWalker.GetEffectiveProperties(tInput))
            {
                if (prop is not IPropertySymbol headerProp)
                {
                    continue;
                }

                if (
                    typeWalker.IsJsonIgnored(headerProp)
                    || typeWalker.GetHeaderName(headerProp) is not { } headerName
                )
                {
                    continue;
                }

                parameters.Add(
                    new TsEndpointParam(
                        headerName,
                        typeWalker.MapPropertyType(headerProp),
                        ParamSource.Header,
                        IsOptional: typeWalker.IsOptional(headerProp)
                    )
                );
            }
        }

        if (lowersBody)
        {
            var requestBodyType = GetRequestBodyType(
                field,
                declaredRequestBody,
                tInput,
                route,
                typeWalker
            );

            // Route params from template — try to match types from TInput properties
            var routeMatchedProps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var paramName in routeParamNames)
            {
                TsType paramType = new TsType.Primitive("string");
                string? bodyPropertyName = null;
                if (tInput is not null)
                {
                    // A3: match against the flattened member surface (incl. inherited)
                    var normalized = RouteParser.NormalizeForMatching(paramName);
                    var matchingProp = typeWalker
                        .GetEffectiveProperties(tInput)
                        .FirstOrDefault(p =>
                            RouteParser.NormalizeForMatching(p.Name) == normalized
                        );
                    if (matchingProp is not null)
                    {
                        paramType = matchingProp is IPropertySymbol matchedProperty
                            ? typeWalker.MapPropertyType(matchedProperty)
                            : typeWalker.MapType(TypeWalker.GetMemberType(matchingProp));
                        routeMatchedProps.Add(matchingProp.Name);
                        if (requestBodyType is null)
                        {
                            bodyPropertyName =
                                typeWalker.GetJsonMemberName(matchingProp)
                                ?? Naming.ToCamelCase(matchingProp.Name);
                        }
                    }
                    else
                    {
                        var declared = declaredParameters?.FirstOrDefault(parameter =>
                            parameter.Source == ParamSource.Route && parameter.Name == paramName
                        );
                        if (declared is not null)
                        {
                            paramType = declared.Type;
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
                    // A route-split input cannot safely reference its complete definition:
                    // that would put route properties back into the multipart body. Inputs
                    // used wholly as multipart can be registered and referenced normally.
                    var mappedInput =
                        routeMatchedProps.Count == 0
                            ? typeWalker.MapType(tInput, $"multipart input '{tInput.Name}'")
                            : null;
                    inputTypeName = mappedInput is TsType.TypeRef typeRef ? typeRef.Name : null;
                    // A3: walk the flattened property surface (incl. inherited).
                    // Form fields are request surface: request-only properties lower,
                    // response-only properties are absent from the multipart body
                    // (planner-constraint:component-schema-directionality).
                    foreach (var formProp in typeWalker.GetRequestProperties(tInput))
                    {
                        // Skip properties already emitted as route params
                        if (routeMatchedProps.Contains(formProp.Name))
                        {
                            continue;
                        }

                        var tsName =
                            typeWalker.GetJsonMemberName(formProp)
                            ?? Naming.ToCamelCase(formProp.Name);

                        if (IsFormFileType(wkt, formProp.Type))
                        {
                            parameters.Add(
                                new TsEndpointParam(
                                    tsName,
                                    new TsType.Primitive("File"),
                                    ParamSource.File,
                                    IsOptional: typeWalker.IsOptional(formProp)
                                )
                            );
                        }
                        else if (typeWalker.IsCollectionOf(formProp.Type, wkt.IFormFile))
                        {
                            // FABLE_GAPS §7 item 12: List<IFormFile>/IFormFile[] →
                            // multipart array-of-binary part, consistent with single files
                            parameters.Add(
                                new TsEndpointParam(
                                    tsName,
                                    new TsType.Array(new TsType.Primitive("File")),
                                    ParamSource.File,
                                    IsOptional: typeWalker.IsOptional(formProp)
                                )
                            );
                        }
                        else
                        {
                            // Non-file properties on a mixed upload record → form fields
                            parameters.Add(
                                new TsEndpointParam(
                                    tsName,
                                    typeWalker.MapPropertyType(formProp),
                                    ParamSource.FormField,
                                    IsOptional: typeWalker.IsOptional(formProp)
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
                    // Request-surface comparison: route-binding equivalence is judged on
                    // properties the request surface actually carries.
                    var bodyProps = typeWalker.GetRequestProperties(tInput).ToList();
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

                // A3: walk the flattened property surface (incl. inherited).
                // Query/route lowering is request surface: response-only properties
                // (serialized but never deserializable) cannot carry request values,
                // so they are absent from the param list
                // (planner-constraint:component-schema-directionality).
                foreach (var queryProp in typeWalker.GetRequestProperties(tInput))
                {
                    var jsonName = typeWalker.GetJsonMemberName(queryProp);
                    var tsName = jsonName ?? Naming.ToCamelCase(queryProp.Name);

                    var isFormFile = SymbolEqualityComparer.Default.Equals(
                        queryProp.Type,
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

                    var tsType = typeWalker.MapPropertyType(queryProp);
                    // Route matching uses the normalized C# property name ({thing_id}
                    // matches ThingId), never the JSON name — the token is wire truth
                    if (
                        normalizedRouteTokens.TryGetValue(
                            RouteParser.NormalizeForMatching(queryProp.Name),
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
                                $"[JsonPropertyName(\"{jsonName}\")] on route-bound property '{queryProp.Name}' "
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
                            IsOptional: typeWalker.IsOptional(queryProp)
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
                // No TInput: the route template itself declares each placeholder's
                // existence and transport location (the narrow documented exception),
                // so the param emits as an untyped string — but its schema type is
                // unknown and must warn exactly like the missing-property paths above
                // (acceptance:no-input-route-token-warns).
                foreach (var paramName in routeParamNames)
                {
                    Diagnostics.Warn(
                        Diagnostics.RouteTokenWithoutInputProperty,
                        $"route token '{{{paramName}}}' on {httpMethod} {route} has no input type — "
                            + "emitted as an untyped string path param."
                    );
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

    private static TsEndpointParam ToDeclaredParameter(
        IInvocationOperation call,
        string parameterName,
        string parameterLocation,
        bool parameterRequired,
        string endpointName,
        TypeWalker typeWalker
    )
    {
        var source = parameterLocation.ToLowerInvariant() switch
        {
            "path" => ParamSource.Route,
            "query" => ParamSource.Query,
            "header" => ParamSource.Header,
            "cookie" => ParamSource.Cookie,
            _ => throw new RivetUserException(
                $"Endpoint '{endpointName}' declares unsupported parameter location '{parameterLocation}'."
            ),
        };
        var metadata = ParseParameterMetadata(call.StringArg("metadataJson"));
        var schemaType = call.StringArg("schemaType");
        var format = call.StringArg("format");
        var parameterType = typeWalker.ApplyGeneratedSchemaRef(
            typeWalker.MapType(call.TargetMethod.TypeArguments[0]).WithLeaf(schemaType, format),
            call.StringArg("schemaRef"),
            $"Parameter '{parameterName}' on endpoint '{endpointName}'"
        );
        if (parameterType is TsType.Array array && metadata.ItemMetadata is not null)
        {
            parameterType = array with { ElementMetadata = metadata.ItemMetadata };
        }
        return new TsEndpointParam(
            parameterName,
            parameterType,
            source,
            IsOptional: !parameterRequired,
            Description: metadata.Description,
            IsDeprecated: metadata.IsDeprecated,
            DefaultValue: metadata.DefaultValue,
            Constraints: metadata.Constraints,
            SchemaExamples: metadata.SchemaExamples,
            Example: metadata.Example,
            Examples: metadata.Examples,
            Style: metadata.Style,
            Explode: metadata.Explode,
            SchemaType: schemaType,
            Format: format == "" ? null : format,
            IsFormatSpecified: format is not null,
            AllowEmptyValue: metadata.AllowEmptyValue
        );
    }

    private static ParameterMetadata ParseParameterMetadata(string? json)
    {
        if (json is null)
        {
            return new ParameterMetadata();
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new ParameterMetadata(
            root.TryGetProperty("description", out var description)
                ? description.GetString()
                : null,
            root.TryGetProperty("deprecated", out var deprecated) && deprecated.GetBoolean(),
            root.TryGetProperty("default", out var defaultValue) ? defaultValue.GetRawText() : null,
            root.TryGetProperty("constraints", out var constraints)
                ? constraints.Deserialize<TsPropertyConstraints>()
                : null,
            CloneProperty(root, "schemaExamples"),
            CloneProperty(root, "example"),
            CloneProperty(root, "examples"),
            root.TryGetProperty("style", out var style) ? style.GetString() : null,
            root.TryGetProperty("explode", out var explode) ? explode.GetBoolean() : null,
            root.TryGetProperty("itemMetadata", out var itemMetadata)
                ? itemMetadata.Deserialize<TsScalarMetadata>()
                : null,
            root.TryGetProperty("allowEmptyValue", out var allowEmptyValue)
                && allowEmptyValue.GetBoolean()
        );
    }

    private static IReadOnlyDictionary<string, string>? ParseReferencedComponents(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);

    private static JsonElement? CloneProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.Clone() : null;

    private static JsonElement? ParseJsonArgument(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>[RivetRequestBody(typeof(T), required)] on a contract field.</summary>
    private sealed record DeclaredRequestBody(ITypeSymbol? BodyType, bool Required);

    private static DeclaredRequestBody? ReadDeclaredRequestBody(
        IFieldSymbol field,
        WellKnownTypes wkt
    ) =>
        field.GetAttribute(wkt.RivetRequestBody) is { } attribute
            ? new DeclaredRequestBody(
                attribute.ConstructorArguments is [{ Value: ITypeSymbol bodyType }, ..]
                    ? bodyType
                    : null,
                attribute.ConstructorArguments is not [_, { Value: false }, ..]
            )
            : null;

    private static TsType? GetRequestBodyType(
        IFieldSymbol field,
        DeclaredRequestBody? declaredRequestBody,
        ITypeSymbol? inputType,
        string route,
        TypeWalker typeWalker
    )
    {
        if (declaredRequestBody?.BodyType is not { } bodyType)
        {
            return null;
        }

        if (
            inputType is null
            || !IsCompatibleRequestBodyType(bodyType, inputType, route, typeWalker)
        )
        {
            throw new RivetUserException(
                $"error {Diagnostics.InvalidRequestBodyProvenance}: endpoint '{field.ContainingType.Name}.{field.Name}' "
                    + $"declares request body type '{bodyType.Name}', which is not represented independently by its input type '{inputType?.Name}'"
            );
        }

        var mappedType = typeWalker.MapType(bodyType);
        return declaredRequestBody.Required || mappedType is TsType.Nullable
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
        // Like-surface comparison (planner-constraint:compatible-body-surface-comparison):
        // both sides are request surfaces — a response-only accessibility exclusion on
        // one side must not by itself make equivalent request declarations incompatible.
        var routeNames = RouteParser
            .ParseRouteParamNames(route)
            .Select(RouteParser.NormalizeForMatching)
            .ToHashSet(StringComparer.Ordinal);
        if (
            typeWalker
                .GetRequestProperties(inputType)
                .Any(property =>
                    !routeNames.Contains(RouteParser.NormalizeForMatching(property.Name))
                    && SymbolEqualityComparer.Default.Equals(property.Type, bodyType)
                )
        )
        {
            return true;
        }

        var inputProperties = typeWalker
            .GetRequestProperties(inputType)
            .GroupBy(
                property =>
                    typeWalker.GetJsonMemberName(property) ?? Naming.ToCamelCase(property.Name),
                StringComparer.Ordinal
            )
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var bodyProperties = typeWalker.GetRequestProperties(bodyType).ToList();
        if (bodyProperties.Count == 0)
        {
            return false;
        }

        foreach (var bodyProperty in bodyProperties)
        {
            var wireName =
                typeWalker.GetJsonMemberName(bodyProperty) ?? Naming.ToCamelCase(bodyProperty.Name);
            if (!inputProperties.TryGetValue(wireName, out var matches) || matches.Count != 1)
            {
                return false;
            }

            var inputProperty = matches[0];
            if (
                routeNames.Contains(RouteParser.NormalizeForMatching(inputProperty.Name))
                || !SymbolEqualityComparer.Default.Equals(bodyProperty.Type, inputProperty.Type)
                || typeWalker.IsOptional(bodyProperty) != typeWalker.IsOptional(inputProperty)
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
            .OfType<IPropertySymbol>()
            .Any(p =>
                IsFormFileType(wkt, p.Type) || typeWalker.IsCollectionOf(p.Type, wkt.IFormFile)
            );

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

    /// <summary>
    /// Checks if the type is a plain byte[] — an explicit file declaration on it
    /// selects Blob semantics (ReturnType null) instead of the base64 JSON string.
    /// </summary>
    private static bool IsPlainByteArray(ITypeSymbol? type)
    {
        return type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte };
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

    private sealed record PendingEndpointExampleCall(
        string? StatusKey,
        string? Name,
        string? MediaType,
        string? Json,
        string? ComponentExampleId,
        string? ResolvedJson,
        IReadOnlyDictionary<string, string>? ReferencedComponents
    );

    private sealed record ParameterMetadata(
        string? Description = null,
        bool IsDeprecated = false,
        string? DefaultValue = null,
        TsPropertyConstraints? Constraints = null,
        JsonElement? SchemaExamples = null,
        JsonElement? Example = null,
        JsonElement? Examples = null,
        string? Style = null,
        bool? Explode = null,
        TsScalarMetadata? ItemMetadata = null,
        bool AllowEmptyValue = false
    );

    /// <summary>A .WithResponseHeader(...) call; null StatusKey = the success response.</summary>
    private sealed record PendingResponseHeaderCall(
        string? StatusKey,
        string Name,
        TsType Type,
        string? Description,
        bool Required,
        JsonElement? SchemaExamples,
        JsonElement? Example,
        JsonElement? Examples,
        bool Deprecated,
        string? Style,
        bool? Explode,
        bool AllowReserved,
        bool AllowEmptyValue,
        string? ContentType
    );

    /// <summary>
    /// Reads a contract field's initializer as a Rivet builder chain: the Define factory
    /// call first, then each builder call in source order. Null when the initializer is
    /// not a chain rooted at a Define member, so the field is not read. A call in the
    /// chain that is not a Rivet builder method (a user extension, say) is refused: its
    /// effect on the contract cannot be read statically.
    /// </summary>
    private static List<IInvocationOperation>? ReadBuilderChain(
        IFieldSymbol field,
        Compilation compilation,
        WellKnownTypes wkt
    )
    {
        if (
            field.DeclaringSyntaxReferences is not [var reference, ..]
            || reference.GetSyntax()
                is not VariableDeclaratorSyntax { Initializer.Value: var value }
        )
        {
            return null;
        }

        var calls = new List<IInvocationOperation>();
        var operation = compilation.GetSemanticModel(value.SyntaxTree).GetOperation(value);
        while (WithoutConversions(operation) is IInvocationOperation invocation)
        {
            calls.Add(invocation);
            operation =
                invocation.Instance
                ?? (
                    invocation.TargetMethod.IsExtensionMethod
                    && invocation.Arguments is [var receiver, ..]
                        ? receiver.Value
                        : null
                );
        }
        calls.Reverse();

        if (
            calls is not [var root, ..]
            || !SymbolEqualityComparer.Default.Equals(root.TargetMethod.ContainingType, wkt.Define)
        )
        {
            return null;
        }

        foreach (var call in calls.Skip(1))
        {
            var owner = call.TargetMethod.ContainingType.OriginalDefinition;
            INamedTypeSymbol?[] builderTypes =
            [
                wkt.RouteDefinitionBase,
                wkt.RouteDefinition,
                wkt.FileRouteDefinition,
                wkt.FileRouteDefinitionOfT,
            ];
            if (!builderTypes.Any(type => SymbolEqualityComparer.Default.Equals(owner, type)))
            {
                throw new RivetUserException(
                    $"Contract endpoint '{field.ContainingType.Name}.{field.Name}' calls "
                        + $"'{call.TargetMethod.ToDisplayString()}', which is not a Rivet builder method. "
                        + "Rivet reads the builder chain statically and cannot interpret it; "
                        + "declare the endpoint with Rivet builder calls only."
                );
            }
        }

        return calls;
    }

    private static IOperation? WithoutConversions(IOperation? operation) =>
        operation is IConversionOperation conversion
            ? WithoutConversions(conversion.Operand)
            : operation;

    private static object? ConstantArg(this IInvocationOperation call, string parameter) =>
        call.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == parameter)
            is { } argument
        && WithoutConversions(argument.Value)?.ConstantValue is { HasValue: true } constant
            ? constant.Value
            : null;

    private static string? StringArg(this IInvocationOperation call, string parameter) =>
        call.ConstantArg(parameter) as string;

    private static int? IntArg(this IInvocationOperation call, string parameter) =>
        call.ConstantArg(parameter) is int value ? value : null;

    private static bool? BoolArg(this IInvocationOperation call, string parameter) =>
        call.ConstantArg(parameter) is bool value ? value : null;

    /// <summary>
    /// The response status a builder call targets, from either its statusCode or its
    /// statusKey parameter. DeclaredKey is set only for the statusKey form.
    /// </summary>
    private static ResponseStatus? Status(this IInvocationOperation call) =>
        call.IntArg("statusCode") is int code ? new ResponseStatus(code, null)
        : call.StringArg("statusKey") is { } key ? new ResponseStatus(ParseStatusCode(key), key)
        : null;

    private readonly record struct ResponseStatus(int Code, string? DeclaredKey)
    {
        public string Key => DeclaredKey ?? Code.ToString();
    }

    private static bool IsCompleteExample(IInvocationOperation call) =>
        call.StringArg("json") is not null
        || call.StringArg("componentExampleId") is not null
            && call.StringArg("resolvedJson") is not null;

    private static PendingEndpointExampleCall ToExampleCall(
        IInvocationOperation call,
        string? statusKey
    ) =>
        new(
            statusKey,
            call.StringArg("name"),
            call.StringArg("mediaType"),
            call.StringArg("json"),
            call.StringArg("componentExampleId"),
            call.StringArg("resolvedJson"),
            ParseReferencedComponents(call.StringArg("referencedComponentsJson"))
        );
}
