using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>A contract endpoint and the field it was read from (null for an abstract method).</summary>
public sealed record ContractEndpoint(TsEndpointDefinition Endpoint, IFieldSymbol? Field)
{
    /// <summary>
    /// What the endpoint declares its security with: a static contract's field takes builder
    /// calls, and an abstract contract's method (no field) takes attributes, as an action does.
    /// </summary>
    internal SecurityDeclaration.Spelling Spelling =>
        Field is null ? SecurityDeclaration.Attributes : SecurityDeclaration.BuilderCalls;
}

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
        if (compilation.GetTypeByMetadataName("Rivet.Define") is null)
        {
            return [];
        }

        var endpoints = new List<ContractEndpoint>();

        foreach (var type in contractTypes)
        {
            var controllerName = DeriveControllerName(type);

            // Abstract class contract: read HTTP attributes from abstract methods
            if (IsMethodContract(type))
            {
                foreach (var method in AbstractOperations(type))
                {
                    var endpoint = EndpointWalker.BuildControllerEndpoint(
                        method,
                        controllerName,
                        isContract: true,
                        wkt,
                        typeWalker
                    );
                    endpoints.Add(new ContractEndpoint(endpoint, null));
                }

                continue;
            }

            // Static class contract: read Endpoint fields from builder chain
            foreach (var member in type.GetMembers())
            {
                if (member is IPropertySymbol property && IsRivetEndpointType(property.Type))
                {
                    throw Diagnostics.Error(
                        Diagnostics.UnreadableContractEndpoint,
                        "contract endpoint "
                            + $"'{type.Name}.{property.Name}' is a property. Rivet reads the spec from a "
                            + "field's initializer, so it can't read a property and would drop the operation. "
                            + "Declare it as a 'public static readonly' field."
                    );
                }

                // An auto-property's backing field belongs to the property refused above.
                if (
                    member is not IFieldSymbol { AssociatedSymbol: null } field
                    || !IsRivetEndpointType(field.Type)
                )
                {
                    continue;
                }

                if (!field.IsStatic || !field.IsReadOnly)
                {
                    throw Diagnostics.Error(
                        Diagnostics.EndpointFieldNotStaticReadonly,
                        $"contract endpoint '{type.Name}.{field.Name}' "
                            + "is not 'static readonly'. Rivet reads the spec from the field's initializer, so a field "
                            + "that can be reassigned, or exists once per instance, can serve a different definition "
                            + "at runtime than the one in the spec. Declare it 'public static readonly'."
                    );
                }

                var endpoint = BuildEndpointFromField(
                    field,
                    controllerName,
                    compilation,
                    wkt,
                    typeWalker
                );
                endpoints.Add(new ContractEndpoint(endpoint, field));
            }
        }

        return endpoints;
    }

    private static TsEndpointDefinition BuildEndpointFromField(
        IFieldSymbol field,
        string controllerName,
        Compilation compilation,
        WellKnownTypes wkt,
        TypeWalker typeWalker
    )
    {
        var chain = ReadBuilderChain(field, compilation, wkt);
        var root = chain[0];
        var builderCalls = chain[1..];

        var routeTemplate =
            root.StringArg("route")
            ?? throw Diagnostics.Error(
                Diagnostics.UnreadableContractEndpoint,
                "contract endpoint "
                    + $"'{field.ContainingType.Name}.{field.Name}' has a null route, so the operation "
                    + "has no path in the spec. Pass a route template such as \"/api/items\"."
            );

        var isFileEndpoint = root.TargetMethod.Name == "File";
        var httpMethod = isFileEndpoint ? "GET" : root.TargetMethod.Name.ToUpperInvariant();
        var route = RouteParser.StripRouteConstraints(routeTemplate);
        var routeTokens = RouteConstraints.Tokens(
            routeTemplate,
            RouteLabel(httpMethod, route, field)
        );

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
        var securityDeclaration = new SecurityDeclaration(
            SecurityDeclaration.BuilderCalls,
            reason => InvalidSecurity(field, reason)
        );
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
                case "RequestExampleJson" or "RequestExampleRef":
                    RequireCompleteExample(call, field);
                    requestExampleCalls.Add(ToExampleCall(call, statusKey: null));
                    break;
                case "ResponseExampleJson"
                or "ResponseExampleRef" when call.Status(name) is { } status:
                    RequireCompleteExample(call, field);
                    responseExampleCalls.Add(ToExampleCall(call, status.Key));
                    break;
                case "Returns" when call.Status(name) is { } status:
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
                    var headerType =
                        typeArgs.Length == 1
                            ? typeWalker.MapType(typeArgs[0])
                            : new TsType.Primitive("string");
                    // Unlike a param's or content's, a header's schemaType: only re-types the
                    // leaf, so on anything but a primitive it would vanish.
                    headerType.RequirePrimitiveLeaf(
                        call.StringArg("schemaType"),
                        $"response header '{headerName}' ({(typeArgs.Length == 1 ? typeArgs[0].ToDisplayString() : "string")}) on endpoint '{name}'",
                        fromBuilderArgument: true
                    );
                    responseHeaderCalls.Add(
                        new PendingResponseHeaderCall(
                            call.Status(name)?.Key,
                            headerName,
                            headerType.WithLeaf(
                                call.StringArg("schemaType"),
                                call.StringArg("format")
                            ),
                            call.StringArg("description"),
                            call.BoolArg("required") ?? false,
                            ParseJsonArgument(
                                call.StringArg("schemaExamplesJson"),
                                $"schemaExamplesJson of response header '{headerName}' on endpoint '{name}'"
                            ),
                            ParseJsonArgument(
                                call.StringArg("exampleJson"),
                                $"exampleJson of response header '{headerName}' on endpoint '{name}'"
                            ),
                            ParseJsonArgument(
                                call.StringArg("examplesJson"),
                                $"examplesJson of response header '{headerName}' on endpoint '{name}'"
                            ),
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
                        throw Diagnostics.Error(
                            Diagnostics.DuplicateResponseStatus,
                            $"endpoint '{name}' calls .Status() more than once"
                        );
                    }
                    successStatusOverride = ResponseStatusValidation.AuthoredStatusCode(
                        statusCode,
                        StatusSite(call, name)
                    );
                    break;
                case "SuppressImplicitResponse":
                    suppressImplicitResponse = true;
                    break;
                case "StatusKey" when call.StringArg("statusKey") is { } statusKey:
                    successStatusKey = ResponseStatusValidation.AuthoredStatusKey(
                        statusKey,
                        StatusSite(call, name)
                    );
                    successResponseDescription = call.StringArg("description");
                    break;
                case "Summary" when call.StringArg("summary") is { } summary:
                    endpointSummary = summary;
                    break;
                case "Description" when call.StringArg("description") is { } description:
                    endpointDescription = description;
                    break;
                case "Anonymous":
                    securityDeclaration.Anonymous();
                    break;
                case "Secure":
                    securityDeclaration.Secure(
                        SecuritySchemeArg(call, field),
                        SecurityScopesArg(call, field)
                    );
                    break;
                case "AndSecure":
                    securityDeclaration.AndSecure(
                        SecuritySchemeArg(call, field),
                        SecurityScopesArg(call, field)
                    );
                    break;
                case "OrAnonymous":
                    securityDeclaration.OrAnonymous();
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
                    when call.Status(name) is { } status
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
                    when call.Status(name) is { } status
                        && call.StringArg("mediaType") is { } mediaType:
                    responseContents.Add(
                        (status.Key, new TsMediaTypeContent(mediaType, null, IsBinary: true))
                    );
                    break;
                case "ProducesFile":
                    fileContentType = call.StringArg("contentType") ?? "application/octet-stream";
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
            throw Diagnostics.Error(
                Diagnostics.MixedFormFileParameters,
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
        // it a file: then a plain byte[] (or a (byte[], string) named tuple) has no
        // schema of its own and the body is binary.
        if (fileContentType is not null)
        {
            if (IsByteArrayStringTuple(tOutput) || IsPlainByteArray(tOutput))
            {
                tOutput = null; // Explicit file payload: binary body, no schema
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

        // An OpenAPI path parameter's name is its token's, exactly: a declaration that matches
        // a token only ignoring case would emit a parameter no template token names.
        foreach (var declaredParameter in declaredParameters)
        {
            if (
                declaredParameter.Source == ParamSource.Route
                && !routeTokens.Any(token => token.Name == declaredParameter.Name)
                && routeTokens.FirstOrDefault(token =>
                    string.Equals(
                        token.Name,
                        declaredParameter.Name,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    is { } caseOnly
            )
            {
                throw Diagnostics.Error(
                    Diagnostics.InvalidBuilderArgument,
                    $"{RouteLabel(httpMethod, route, field)} declares .Parameter<T>(\"{declaredParameter.Name}\", \"path\", ...), "
                        + $"but the route's token is '{{{caseOnly.Name}}}'. A path parameter's name must equal "
                        + $"its token exactly; spell it \"{caseOnly.Name}\"."
                );
            }
        }

        var declaredRequestBody = ReadDeclaredRequestBody(field, wkt);
        var (builtParameters, inputTypeName, bodyFieldNames) = BuildParams(
            compilation,
            wkt,
            httpMethod,
            route,
            routeTokens,
            tInput,
            field,
            declaredRequestBody,
            typeWalker,
            acceptsFile,
            binaryRequestContentType,
            declaredParameters,
            requestBodyPresent,
            requestContents,
            isFormEncoded
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
            parameters.Add(
                declaredParameter.Source == ParamSource.Route
                && routeTokens.FirstOrDefault(token => token.Name == declaredParameter.Name)
                    is { } token
                    ? RouteConstraints.Apply(
                        declaredParameter,
                        token,
                        RouteLabel(httpMethod, route, field),
                        $"its .Parameter<T>(\"{declaredParameter.Name}\", \"path\", ...) declaration"
                    )
                    : declaredParameter
            );
        }

        // The success response. Void endpoints with typed error responses also need one
        // so the client emitter generates a discriminated union (not RivetResult<void>).
        var successCode =
            successStatusOverride
            ?? ResponseStatusValidation.DefaultSuccessCode(httpMethod, returnType is not null);
        if (returnType is not null || !suppressImplicitResponse)
        {
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

        ResponseStatusValidation.RejectContractDuplicates(responses, name);
        responses.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));
        // Explicit additional representations must not erase the file factory's
        // success representation: runtime always includes the configured file format.
        var fileSuccessKey = successStatusKey ?? successCode.ToString();
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
        // Contents first: .ResponseContent(...) declares its status, so examples and
        // headers may target a status declared only that way.
        ApplyResponseContents(responses, responseContents);
        EndpointWalker.ApplyResponseExamples(
            responses,
            responseExampleCalls
                .Select(call =>
                    (
                        call.StatusKey!,
                        ToEndpointExample(
                            call,
                            ResponseExampleMediaType(
                                responses,
                                call,
                                fileContentType,
                                responseContentTypeOverride,
                                name
                            )
                        )
                    )
                )
                .ToList(),
            Diagnostics.ContractExampleUndeclaredStatus,
            $"contract endpoint '{name}'",
            "declare the status with .Returns(...), .Status(...) or .ResponseContent(...)"
        );
        ApplyResponseHeaders(responses, responseHeaderCalls, successCode, successStatusKey, name);

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

        var security = securityDeclaration.Build();

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
            BodyFieldNames: bodyFieldNames,
            IsFileEndpoint: isFileEndpoint,
            QueryAuth: queryAuth,
            BinaryRequestContentType: binaryRequestContentType,
            RequestContentTypeOverride: requestContentTypeOverride,
            ResponseContentTypeOverride: responseContentTypeOverride,
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

    /// <summary>
    /// The media type a response example lands under. A status declared with
    /// .ResponseContent(...) carries exactly those media types, so its example defaults to
    /// the single declared one and must name one of them (RIV1002); anything else would
    /// add a media type the response never declared. Other statuses default to where the
    /// spec puts their body: a success under the file format or .ProducesContentType(...),
    /// anything else under application/json.
    /// </summary>
    private static string ResponseExampleMediaType(
        IReadOnlyList<TsResponseType> responses,
        PendingEndpointExampleCall call,
        string? fileContentType,
        string? successContentType,
        string endpointName
    )
    {
        var statusKey = call.StatusKey!;
        var response = responses.FirstOrDefault(candidate =>
            candidate.EffectiveStatusKey.Equals(statusKey, StringComparison.OrdinalIgnoreCase)
        );
        var contents = response?.Contents;
        if (contents is not { Count: > 0 })
        {
            if (call.MediaType is not null)
            {
                return call.MediaType;
            }

            // Mirrors the emitter: a typed body takes the success media type, and the file's
            // media type goes only to the first 2xx response, and only when it has no type.
            if (
                fileContentType is not null
                && response is { DataType: null }
                && ReferenceEquals(
                    response,
                    responses.FirstOrDefault(candidate => candidate.StatusCode is >= 200 and < 300)
                )
            )
            {
                return fileContentType;
            }

            return ParseStatusCode(statusKey) is >= 200 and < 300
                ? successContentType ?? "application/json"
                : "application/json";
        }

        var declared = string.Join(", ", contents.Select(content => $"'{content.MediaType}'"));
        if (call.MediaType is null)
        {
            if (contents.Count == 1)
            {
                return contents[0].MediaType;
            }

            throw Diagnostics.Error(
                Diagnostics.ContractExampleUndeclaredStatus,
                $"contract endpoint '{endpointName}' "
                    + $"has a response example for status {statusKey} without a media type, and that status "
                    + $"declares several ({declared}), so Rivet can't tell which one the example shows. "
                    + "Pass the example's mediaType."
            );
        }

        if (
            !contents.Any(content =>
                string.Equals(content.MediaType, call.MediaType, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            throw Diagnostics.Error(
                Diagnostics.ContractExampleUndeclaredStatus,
                $"contract endpoint '{endpointName}' "
                    + $"has a response example for status {statusKey} under '{call.MediaType}', which that "
                    + $"status does not declare (it declares {declared}), so the spec would gain a media type "
                    + "the response never serves. Use one of the declared media types, or declare this one "
                    + "with .ResponseContent(...)."
            );
        }

        return call.MediaType;
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
            call.ReferencedComponents,
            call.Summary,
            call.Description
        );
    }

    /// <summary>
    /// Attaches .WithResponseHeader(...) declarations to their responses.
    /// A null status (convenience overload) targets the success status. Headers on a
    /// status no .Returns(), .Status() or .ResponseContent() declared fail generation (RIV1017), mirroring
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

                // HTTP header names are case-insensitive, so ETag and etag are one header.
                if (
                    mergedHeaders
                        .GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault(names => names.Count() > 1) is
                    { } duplicate
                )
                {
                    throw Diagnostics.Error(
                        Diagnostics.DuplicateResponseHeader,
                        $"endpoint '{endpointName}' declares response header '{duplicate.Key}' more than once for status {group.Key}"
                    );
                }

                responses[responseIndex] = response with { Headers = mergedHeaders };
                continue;
            }

            throw Diagnostics.Error(
                Diagnostics.ResponseHeaderUndeclaredStatus,
                $"contract endpoint '{endpointName}' declares "
                    + $"response header(s) {string.Join(", ", headers.Select(h => $"'{h.Name}'"))} for status "
                    + $"{group.Key}, which it does not declare, so the spec has nowhere to put them. Declare the "
                    + "status with .Returns(...), .Status(...) or .ResponseContent(...), or move the header to a declared status."
            );
        }
    }

    /// <summary>
    /// Lowers the input type into route, query, header and body params. BodyFieldNames are
    /// the body's top-level names as the spec sends them (route-bound and read-only members
    /// dropped), when it has a closed list: a JSON body, lowered from the input or declared
    /// by <c>.RequestContent&lt;T&gt;()</c>, and a form body left as one <c>body</c> param (a
    /// <c>[RivetRequestBody]</c> type, or an input that doesn't lower to parts). A form body
    /// lowered to parts lists them as its params instead.
    /// </summary>
    private static (
        IReadOnlyList<TsEndpointParam> Params,
        string? InputTypeName,
        IReadOnlyList<string>? BodyFieldNames
    ) BuildParams(
        Compilation compilation,
        WellKnownTypes wkt,
        string httpMethod,
        string route,
        IReadOnlyList<RouteToken> routeTokens,
        ITypeSymbol? tInput,
        IFieldSymbol field,
        DeclaredRequestBody? declaredRequestBody,
        TypeWalker typeWalker,
        bool acceptsFile,
        string? binaryContentType,
        IReadOnlyList<TsEndpointParam> declaredParameters,
        bool hasDeclaredRequestBody,
        IReadOnlyList<TsMediaTypeContent> requestContents,
        bool isFormEncoded
    )
    {
        // The tokens routing reads, so a literal {{ in the template is no parameter.
        var routeParamNames = routeTokens
            .Select(token => token.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Wire-name pinning for params: a route token and a
        // C# property are the same param when they match under normalization
        // ({thing_id} ↔ ThingId, {enterprise-team} ↔ EnterpriseTeam). The param
        // always keeps the TOKEN's spelling — the route template is wire truth.
        var normalizedRouteTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in routeParamNames)
        {
            normalizedRouteTokens.TryAdd(RouteParser.NormalizeForMatching(token), token);
        }

        var tokensByName = new Dictionary<string, RouteToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in routeTokens)
        {
            tokensByName.TryAdd(token.Name, token);
        }
        var where = RouteLabel(httpMethod, route, field);

        TsEndpointParam Constrained(TsEndpointParam param, string declaredBy) =>
            tokensByName.TryGetValue(param.Name, out var token)
                ? RouteConstraints.Apply(param, token, where, declaredBy)
                : param;

        TsEndpointParam RouteParamFromMember(ISymbol member, string token, string? bodyName) =>
            Constrained(
                LoweredParam(typeWalker, member, token, ParamSource.Route, false, bodyName),
                $"its input property '{member.Name}'"
            );

        // A token no input member binds is declared by a .Parameter<T>(name, "path", ...)
        // (which replaces this param, constraints applied there) or by a type constraint.
        // With neither, its type is unknown: RIV1019 stops generation.
        TsEndpointParam UnboundRouteParam(string paramName)
        {
            if (
                declaredParameters.FirstOrDefault(parameter =>
                    parameter.Source == ParamSource.Route && parameter.Name == paramName
                ) is
                { } declared
            )
            {
                return new TsEndpointParam(paramName, declared.Type, ParamSource.Route);
            }

            if (RouteConstraints.DeclaredType(tokensByName[paramName], where) is { } constrained)
            {
                return Constrained(
                    new TsEndpointParam(paramName, constrained, ParamSource.Route),
                    "its type constraint"
                );
            }

            throw Diagnostics.Error(
                Diagnostics.RouteTokenUndeclared,
                $"route token '{{{paramName}}}' on {httpMethod} {route} "
                    + (
                        tInput is null
                            ? "has no input type"
                            : $"has no matching property on input type '{tInput.Name}'"
                    )
                    + ", no type constraint and no .Parameter<T>() declaration, so its type is unknown "
                    + "and the spec can't say what the handler binds. Declare it: "
                    + (tInput is null ? "give the endpoint an input type with" : "add")
                    + $" a '{Naming.ToPascalCaseFromSegments(paramName)}' property, a type constraint "
                    + $"('{{{paramName}:guid}}', '{{{paramName}:int}}', ...), or "
                    + $".Parameter<T>(\"{paramName}\", \"path\", ...)."
            );
        }

        var parameters = new List<TsEndpointParam>();
        var isFormFileInput = tInput is not null && IsFormFileType(wkt, tInput);
        // What the form binder binds decides whether the input is multipart and, when it is,
        // lists its parts: one list, so a file that makes the body multipart is always a part.
        var formBound =
            tInput is null || isFormFileInput
                ? []
                : FormBinding.FormBoundProperties(compilation, tInput);
        var hasFormFileProperty = formBound.Any(property =>
            FormBinding.CarriesFiles(wkt, typeWalker, property.Type)
        );
        // The runtime binds TInput by the same rule. With no body, or a raw binary one
        // (.AcceptsBinary(): host code reads the stream), TInput's properties become route/query
        // params exactly like a GET/DELETE input.
        var lowersBody =
            RequestInputLowering.Of(
                httpMethod,
                bodyDeclared: hasDeclaredRequestBody,
                parametersDeclared: declaredParameters.Count > 0,
                acceptsBinary: binaryContentType is not null,
                contentTypes: requestContents
                    .Where(content => !content.IsBinary)
                    .Select(content => content.MediaType)
                    .ToList(),
                binaryContentDeclared: requestContents.Any(content => content.IsBinary),
                formDeclared: isFormEncoded || acceptsFile,
                formFileInput: isFormFileInput || hasFormFileProperty
            ) != InputBinding.RouteAndQuery;
        string? inputTypeName = null;
        IReadOnlyList<string>? bodyFieldNames = null;

        // [RivetHeader] properties are header params on every HTTP method —
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
                    LoweredParam(
                        typeWalker,
                        headerProp,
                        headerName,
                        ParamSource.Header,
                        typeWalker.IsOptional(headerProp)
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
                routeParamNames,
                typeWalker
            );

            // A form record (one with files, or a .FormEncoded() one, or one whose declared
            // request content is a form, as import writes a multipart body with no file in it,
            // and the runtime binds it; with no declared body type) lowers to parts; a JSON
            // one to a body param.
            var formBody =
                tInput is not null
                && (
                    hasFormFileProperty
                    || (
                        isFormEncoded
                        || requestContents.Any(content =>
                            !content.IsBinary && RequestInputLowering.IsForm(content.MediaType)
                        )
                    )
                        && requestBodyType is null
                        && typeWalker.IsParamLowerable(tInput)
                );

            // Route params from template — try to match types from TInput properties: a form
            // body's from the one member list its parts come from (a package base's included),
            // a JSON body's from the flattened member surface (incl. inherited).
            var routeMatchedProps = new HashSet<string>(StringComparer.Ordinal);
            var routeCandidates =
                tInput is null ? []
                : formBody ? formBound.Cast<ISymbol>().ToList()
                : typeWalker.GetEffectiveProperties(tInput).ToList();
            foreach (var paramName in routeParamNames)
            {
                var normalized = RouteParser.NormalizeForMatching(paramName);
                var matchingProp = routeCandidates.FirstOrDefault(p =>
                    RouteParser.NormalizeForMatching(p.Name) == normalized
                );
                if (matchingProp is null)
                {
                    parameters.Add(UnboundRouteParam(paramName));
                    continue;
                }

                routeMatchedProps.Add(matchingProp.Name);
                parameters.Add(
                    RouteParamFromMember(
                        matchingProp,
                        paramName,
                        requestBodyType is null
                            ? typeWalker.GetJsonMemberName(matchingProp)
                                ?? Naming.ToCamelCase(matchingProp.Name)
                            : null
                    )
                );
            }

            // The parts of a form record, or null for a JSON one. Both hosts can serve a
            // contract.
            IReadOnlyList<FormBinding.FormPart>? FormParts() =>
                formBody
                    ? FormBinding.Parts(
                        wkt,
                        typeWalker,
                        tInput!,
                        routeMatchedProps,
                        FormBinding.FormHosts.Any,
                        where
                    )
                    : null;

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
                if (isFormFileInput)
                {
                    parameters.Add(
                        new TsEndpointParam("file", new TsType.Primitive("File"), ParamSource.File)
                    );
                }
                // A record that carries files, or a .FormEncoded() one: its parts are what the
                // form binders bind, under the names they read. An empty form record keeps
                // its (empty) body below, as a JSON one does.
                else if (
                    FormParts() is { } parts
                    && (parts.Count > 0 || routeMatchedProps.Count > 0)
                )
                {
                    // The body is described from the input's definition: the emitter $refs
                    // its component when that lists exactly the parts under the same names,
                    // and otherwise spells the parts out inline, each carrying its property's
                    // schema. BodyPropertyName says which property a part is.
                    inputTypeName = typeWalker.MapType(tInput, $"form input '{tInput.Name}'")
                        is TsType.TypeRef typeRef
                        ? typeRef.Name
                        : null;
                    parameters.AddRange(
                        FormPartParams(wkt, typeWalker, parts, inputTypeName, namePrefix: null)
                    );
                }
                else
                {
                    // An input whose every property is route-bound
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
                        bodyFieldNames = ClosedBodyFieldNames(typeWalker, tsType, parameters);
                    }
                }
            }
        }
        else
        {
            // GET/DELETE (and .AcceptsBinary() bodies): TInput properties matched by name
            // to route → Route, remaining → Query — never a JSON body param. A body declared
            // here can only be the binary one.
            if (tInput is not null && !typeWalker.IsParamLowerable(tInput))
            {
                // A dictionary/collection/scalar has no property surface to lower to
                // route/query params and there is no JSON body to carry it, so any spec
                // either invents params from CLR members (Count, Keys, …) or drops the input.
                throw Diagnostics.Error(
                    Diagnostics.InputTypeNotParamLowerable,
                    $"input type '{tInput.ToDisplayString()}' on "
                        + $"{httpMethod} {route} is a dictionary, collection or scalar, but "
                        + (
                            hasDeclaredRequestBody
                                ? "the request body carries the binary payload"
                                : "this request has no JSON body to carry it (as on GET or DELETE)"
                        )
                        + ", so the input must lower to route and query params, and it has no properties to "
                        + "lower. Model the input as a record with one property per route or query parameter"
                        + (
                            hasDeclaredRequestBody
                                ? "."
                                : ", or send the values as a JSON body with Define.Post/Put/Patch."
                        )
                );
            }

            if (tInput is not null)
            {
                inputTypeName = tInput.Name;
                var matchedRouteParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Walk the flattened property surface (incl. inherited).
                // Query/route lowering is request surface: response-only properties
                // (serialized but never deserializable) cannot carry request values,
                // so they are absent from the param list.
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

                        // A route-bound param must keep the ROUTE name — runtime route
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

                        parameters.Add(RouteParamFromMember(queryProp, routeName, tsName));
                        continue;
                    }

                    // Surface property-level optionality ([RivetOptional], nullability)
                    // on the param so emitters mark non-nullable optionals required: false
                    parameters.Add(
                        LoweredParam(
                            typeWalker,
                            queryProp,
                            tsName,
                            ParamSource.Query,
                            typeWalker.IsOptional(queryProp)
                        )
                    );
                }

                // Route tokens no TInput property binds
                foreach (var paramName in routeParamNames)
                {
                    if (!matchedRouteParams.Contains(paramName))
                    {
                        parameters.Insert(0, UnboundRouteParam(paramName));
                    }
                }
            }
            else
            {
                // No TInput: the route template declares each placeholder's existence and
                // transport location; its type comes from a declaration or a type
                // constraint, or generation stops with RIV1019 like the missing-property
                // paths above.
                foreach (var paramName in routeParamNames)
                {
                    parameters.Add(UnboundRouteParam(paramName));
                }
            }
        }

        // A JSON body declared only by .RequestContent<T>(json) lowers no param, but its
        // top-level names are as closed as a lowered body's, so --check holds a Functions
        // parameter to them the same way. The emitter writes such a body as T's whole schema,
        // whatever the route binds from TInput, so only T's read-only members drop.
        if (
            !parameters.Any(parameter =>
                parameter.Source is ParamSource.Body or ParamSource.File or ParamSource.FormField
            )
            && requestContents
                .Where(content =>
                    !content.IsBinary && !RequestInputLowering.IsForm(content.MediaType)
                )
                .ToList()
                is { Count: > 0 } jsonContents
        )
        {
            var names = jsonContents
                .Select(content =>
                    content.Schema is null
                        ? null
                        : ClosedBodyFieldNames(typeWalker, content.Schema, [])
                )
                .ToList();
            bodyFieldNames = names.Contains(null)
                ? null
                : [.. names.SelectMany(list => list!).Distinct(StringComparer.Ordinal)];
        }

        return (parameters, inputTypeName, bodyFieldNames);
    }

    /// <summary>
    /// The top-level names a JSON body of <paramref name="bodyType"/> is sent with, as the
    /// emitter lowers it (route-bound and read-only members dropped), or null when the body
    /// has no closed list of names (a dictionary, extension data, a non-object).
    /// </summary>
    private static IReadOnlyList<string>? ClosedBodyFieldNames(
        TypeWalker typeWalker,
        TsType bodyType,
        IReadOnlyList<TsEndpointParam> parameters
    )
    {
        var definitionName = (
            bodyType is TsType.Nullable nullable ? nullable.Inner : bodyType
        ) switch
        {
            TsType.TypeRef typeRef => typeRef.Name,
            TsType.Generic generic => generic.Name,
            _ => null,
        };
        return
            definitionName is not null
            && typeWalker.Definitions.GetValueOrDefault(definitionName)
                is { Type: null, AdditionalProperties: not true } definition
            ? TsEndpointDefinition
                .RequestBodyProperties(parameters, definition.Properties)
                .Select(property => property.Name)
                .ToList()
            : null;
    }

    /// <summary>
    /// The params a form input's parts become: a file or a list of files, or a form field
    /// carrying its member's annotations as a body property would. Each names the property
    /// it is (<see cref="TsEndpointParam.BodyPropertyName"/>) so the emitter can describe the
    /// body from the input's definition, <paramref name="inputTypeName"/>. A member that
    /// definition marks readOnly is never sent in a request (OpenAPI), so it's no form field,
    /// as it's no JSON body property. <paramref name="namePrefix"/> is an MVC model name the
    /// binder reads every part under (<c>[FromForm(Name = "p")] Dto dto</c> reads <c>p.Title</c>).
    /// </summary>
    internal static IEnumerable<TsEndpointParam> FormPartParams(
        WellKnownTypes wkt,
        TypeWalker typeWalker,
        IReadOnlyList<FormBinding.FormPart> parts,
        string? inputTypeName,
        string? namePrefix
    )
    {
        var readOnly = (
            inputTypeName is null ? null : typeWalker.Definitions.GetValueOrDefault(inputTypeName)
        )
            ?.Properties.Where(property => property.IsReadOnly)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            var property = part.Property;
            var propertyName =
                typeWalker.GetJsonMemberName(property) ?? Naming.ToCamelCase(property.Name);
            if (!part.IsFile && readOnly?.Contains(propertyName) == true)
            {
                continue;
            }

            var name = namePrefix is null ? part.Name : $"{namePrefix}.{part.Name}";
            yield return part.IsFile
                ? new TsEndpointParam(
                    name,
                    FormBinding.FilePartType(wkt, property.Type),
                    ParamSource.File,
                    IsOptional: !part.Required && typeWalker.IsOptional(property),
                    BodyPropertyName: propertyName
                )
                : LoweredParam(
                    typeWalker,
                    property,
                    name,
                    ParamSource.FormField,
                    !part.Required && typeWalker.IsOptional(property),
                    propertyName
                );
        }
    }

    private static string RouteLabel(string httpMethod, string route, IFieldSymbol field) =>
        $"{httpMethod} {route} (contract '{field.ContainingType.Name}.{field.Name}')";

    /// <summary>
    /// A route, query, header or form-field param lowered from an input member, carrying what
    /// the member's schema would if it were a body property (<see cref="TypeWalker.AnnotatedParam"/>:
    /// annotations, schema refs and generated item metadata included). The leaf already holds
    /// [RivetSchemaType] and the format, so the param doesn't pin SchemaType: the emitter's
    /// pinned-leaf path would drop the inferred format the body keeps.
    /// </summary>
    private static TsEndpointParam LoweredParam(
        TypeWalker typeWalker,
        ISymbol member,
        string name,
        ParamSource source,
        bool isOptional,
        string? bodyPropertyName = null
    ) =>
        typeWalker.AnnotatedParam(
            new TsEndpointParam(
                name,
                typeWalker.MapType(TypeWalker.GetMemberType(member)),
                source,
                IsOptional: isOptional,
                BodyPropertyName: bodyPropertyName
            ),
            member
        );

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
            _ => throw Diagnostics.Error(
                Diagnostics.InvalidBuilderArgument,
                $"endpoint '{endpointName}' declares parameter '{parameterName}' in unsupported location "
                    + $"'{parameterLocation}'. Use \"path\", \"query\", \"header\", or \"cookie\"."
            ),
        };
        var metadata = ParseParameterMetadata(
            call.StringArg("metadataJson"),
            $"metadataJson of parameter '{parameterName}' on endpoint '{endpointName}'"
        );
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
            AllowEmptyValue: metadata.AllowEmptyValue,
            AllowReserved: metadata.AllowReserved
        );
    }

    private static ParameterMetadata ParseParameterMetadata(string? json, string what)
    {
        if (json is null)
        {
            return new ParameterMetadata();
        }

        try
        {
            return ReadParameterMetadata(json);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidBuilderArgument,
                $"{what} must be a JSON object of parameter metadata, got '{json}' ({exception.Message})"
            );
        }
    }

    private static ParameterMetadata ReadParameterMetadata(string json)
    {
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
                && allowEmptyValue.GetBoolean(),
            root.TryGetProperty("allowReserved", out var allowReserved)
                && allowReserved.GetBoolean()
        );
    }

    private static IReadOnlyDictionary<string, string>? ParseReferencedComponents(
        string? json,
        string method
    )
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException exception)
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidBuilderArgument,
                $"referencedComponentsJson of .{method}() must be a JSON object of strings, got "
                    + $"'{json}' ({exception.Message})"
            );
        }
    }

    private static JsonElement? CloneProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.Clone() : null;

    private static JsonElement? ParseJsonArgument(string? json, string what) =>
        json is null ? null : AuthoredJson.Parse(json, what);

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
        IReadOnlySet<string> routeParamNames,
        TypeWalker typeWalker
    )
    {
        if (declaredRequestBody?.BodyType is not { } bodyType)
        {
            return null;
        }

        if (
            inputType is null
            || !IsCompatibleRequestBodyType(bodyType, inputType, routeParamNames, typeWalker)
        )
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidRequestBodyProvenance,
                $"endpoint '{field.ContainingType.Name}.{field.Name}' "
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
        IReadOnlySet<string> routeParamNames,
        TypeWalker typeWalker
    )
    {
        // Like-surface comparison:
        // both sides are request surfaces — a response-only accessibility exclusion on
        // one side must not by itself make equivalent request declarations incompatible.
        // The route's names are the tokens routing reads, so a literal brace is no parameter.
        var routeNames = routeParamNames
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
                || !SymbolEqualityComparer.IncludeNullability.Equals(
                    bodyProperty.Type,
                    inputProperty.Type
                )
                || typeWalker.CanOmitOnWire(bodyProperty) != typeWalker.CanOmitOnWire(inputProperty)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFormFileType(WellKnownTypes wkt, ITypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type, wkt.IFormFile);

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

    private static bool IsRivetEndpointType(ITypeSymbol type) =>
        type
            is INamedTypeSymbol
            {
                Name: "RouteDefinition" or "InputRouteDefinition" or "FileRouteDefinition",
                ContainingNamespace: { Name: "Rivet", ContainingNamespace.IsGlobalNamespace: true },
            };

    /// <summary>
    /// A <c>[RivetContract]</c> class that declares its operations as abstract methods: an
    /// abstract, non-static class. A static or concrete contract declares them as fields, and
    /// a method contract with no operations still has no fields to read.
    /// </summary>
    internal static bool IsMethodContract(INamedTypeSymbol type) =>
        type is { IsAbstract: true, IsStatic: false };

    /// <summary>
    /// The operations a method contract (<see cref="IsMethodContract"/>) declares: its ordinary
    /// abstract methods. An abstract property's or event's accessors are abstract methods too,
    /// but never operations. Any other contract has none here. <see cref="Walk"/> emits them and
    /// <c>--check</c> compares their overrides' security.
    /// </summary>
    internal static IEnumerable<IMethodSymbol> AbstractOperations(INamedTypeSymbol type) =>
        IsMethodContract(type)
            ? type.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(method => method is { MethodKind: MethodKind.Ordinary, IsAbstract: true })
            : [];

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
        IReadOnlyDictionary<string, string>? ReferencedComponents,
        string? Summary,
        string? Description
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
        bool AllowEmptyValue = false,
        bool AllowReserved = false
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
    /// call first, then each builder call in source order. A field whose initializer is
    /// missing or isn't a chain rooted at a Define member is refused, and so is a call in
    /// the chain that is not a Rivet builder method (a user extension, say): either way the
    /// contract can't be read statically, and skipping the field would drop the operation
    /// from the spec without a word.
    /// </summary>
    private static List<IInvocationOperation> ReadBuilderChain(
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
            throw Unreadable(field, "has no initializer", BuilderChainRemedy);
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
            throw Unreadable(
                field,
                "isn't initialised with a Define.* builder chain (a helper call or another field, say)",
                BuilderChainRemedy
            );
        }

        foreach (var call in calls)
        {
            RequireConstantArguments(call, field);
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
                throw Unreadable(
                    field,
                    $"calls '{call.TargetMethod.ToDisplayString()}', which is not a Rivet builder method",
                    BuilderChainRemedy
                );
            }
        }

        return calls;
    }

    private static string SecuritySchemeArg(IInvocationOperation call, IFieldSymbol field) =>
        call.StringArg("scheme")
        ?? throw Unreadable(
            field,
            $"passes .{call.TargetMethod.Name}() a scheme name that isn't a compile-time constant",
            ConstantRemedy
        );

    private static IReadOnlyList<string> SecurityScopesArg(
        IInvocationOperation call,
        IFieldSymbol field
    )
    {
        var value = WithoutConversions(
            call.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "scopes")?.Value
        );
        IEnumerable<IOperation>? elements = value switch
        {
            IArrayCreationOperation { Initializer: { } initializer } => initializer.ElementValues,
            IArrayCreationOperation { DimensionSizes: [{ ConstantValue.Value: 0 }] } => [],
            ICollectionExpressionOperation collection => collection.Elements,
            _ => null,
        };
        var scopes = elements
            ?.Select(element => WithoutConversions(element)?.ConstantValue)
            .Select(constant => constant is { HasValue: true, Value: string scope } ? scope : null)
            .ToList();
        return scopes is not null && scopes.All(scope => scope is not null)
            ? scopes.OfType<string>().ToList()
            : throw Unreadable(
                field,
                $"passes .{call.TargetMethod.Name}() scopes that aren't compile-time constant strings",
                "Pass an array or collection expression of literal or const strings."
            );
    }

    private static RivetUserException InvalidSecurity(IFieldSymbol field, string reason) =>
        Diagnostics.Error(
            Diagnostics.InvalidSecurityDeclaration,
            "contract endpoint " + $"'{field.ContainingType.Name}.{field.Name}': {reason}."
        );

    /// <summary>
    /// Every string, int or bool a builder call is given must be a compile-time constant:
    /// the argument readers can't tell a variable from an omitted argument, so a variable
    /// would silently fall back to the default (or skip the call) instead of reaching the spec.
    /// </summary>
    private static void RequireConstantArguments(IInvocationOperation call, IFieldSymbol field)
    {
        foreach (var argument in call.Arguments)
        {
            if (
                argument.ArgumentKind is ArgumentKind.DefaultValue
                || argument.Parameter is not { } parameter
                || !IsConstantReadParameter(parameter.Type)
                || WithoutConversions(argument.Value)?.ConstantValue is { HasValue: true }
            )
            {
                continue;
            }

            throw Unreadable(
                field,
                $"passes .{call.TargetMethod.Name}() a '{parameter.Name}' that isn't a compile-time constant",
                ConstantRemedy
            );
        }
    }

    private static bool IsConstantReadParameter(ITypeSymbol type) =>
        (
            type
                is INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                } nullable
                ? nullable.TypeArguments[0]
                : type
        ).SpecialType
            is SpecialType.System_String
                or SpecialType.System_Int32
                or SpecialType.System_Boolean;

    private const string BuilderChainRemedy =
        "Initialise the field with Define.* followed by Rivet builder calls only.";

    private const string ConstantRemedy =
        "Use a literal or a const (const strings can be concatenated or interpolated).";

    private static RivetUserException Unreadable(
        IFieldSymbol field,
        string reason,
        string remedy
    ) =>
        Diagnostics.Error(
            Diagnostics.UnreadableContractEndpoint,
            "contract endpoint "
                + $"'{field.ContainingType.Name}.{field.Name}' {reason}. Rivet reads the spec from the "
                + "field's initializer statically and can't interpret this, so it would otherwise drop "
                + $"the operation. {remedy}"
        );

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
    private static ResponseStatus? Status(this IInvocationOperation call, string endpointName) =>
        call.IntArg("statusCode") is int code
            ? new ResponseStatus(
                ResponseStatusValidation.AuthoredStatusCode(code, StatusSite(call, endpointName)),
                null
            )
        : call.StringArg("statusKey") is { } key
            ? AuthoredStatus(key, StatusSite(call, endpointName))
        : null;

    private static ResponseStatus AuthoredStatus(string key, string where)
    {
        var authored = ResponseStatusValidation.AuthoredStatusKey(key, where);
        return new ResponseStatus(ParseStatusCode(authored), authored);
    }

    private static string StatusSite(IInvocationOperation call, string endpointName) =>
        $"contract endpoint '{endpointName}' (.{call.TargetMethod.Name}())";

    private readonly record struct ResponseStatus(int Code, string? DeclaredKey)
    {
        public string Key => DeclaredKey ?? Code.ToString();
    }

    /// <summary>
    /// An example needs a JSON value, a resolved component example, a summary or a
    /// description: one with none of them has nothing to emit, and skipping it would drop
    /// a declared example without a word.
    /// </summary>
    private static void RequireCompleteExample(IInvocationOperation call, IFieldSymbol field)
    {
        if (
            call.StringArg("json") is not null
            || call.StringArg("componentExampleId") is not null
                && call.StringArg("resolvedJson") is not null
            || call.StringArg("summary") is not null
            || call.StringArg("description") is not null
        )
        {
            return;
        }

        throw Diagnostics.Error(
            Diagnostics.UnreadableContractEndpoint,
            "contract endpoint "
                + $"'{field.ContainingType.Name}.{field.Name}' passes .{call.TargetMethod.Name}() an "
                + "example with nothing in it: no JSON value, summary or description. Give the example "
                + "at least one of them, or remove the call."
        );
    }

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
            ParseReferencedComponents(
                call.StringArg("referencedComponentsJson"),
                call.TargetMethod.Name
            ),
            call.StringArg("summary"),
            call.StringArg("description")
        );
}
