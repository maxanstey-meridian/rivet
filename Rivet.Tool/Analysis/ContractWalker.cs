using System.Text.Json;
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
        var provenance = OpenApiProvenanceWalker.ReadOperation(field);

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
                requestBodyPresent = true;
            }
            else if (call.MethodName == "FormEncoded")
            {
                isFormEncoded = true;
                requestBodyPresent = true;
            }
            else if (call.MethodName == "AcceptsBinary")
            {
                binaryRequestContentType = call.StringArg ?? "application/octet-stream";
                requestBodyPresent = true;
                requestContentsAuthoritative = true;
                requestContents.Add(
                    new TsMediaTypeContent(binaryRequestContentType, null, IsBinary: true)
                );
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
                        StatusKey: null,
                        Name: call.GetStringArg("name"),
                        MediaType: call.GetStringArg("mediaType"),
                        Json: call.GetStringArg("json"),
                        ComponentExampleId: null,
                        ResolvedJson: null,
                        ReferencedComponents: ParseReferencedComponents(
                            call.GetStringArg("referencedComponentsJson")
                        )
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
                        StatusKey: null,
                        Name: call.GetStringArg("name"),
                        MediaType: call.GetStringArg("mediaType"),
                        Json: null,
                        ComponentExampleId: call.GetStringArg("componentExampleId"),
                        ResolvedJson: call.GetStringArg("resolvedJson"),
                        ReferencedComponents: ParseReferencedComponents(
                            call.GetStringArg("referencedComponentsJson")
                        )
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
                responses.Add(
                    new TsResponseType(
                        call.StatusCodeArg.Value,
                        tsType,
                        call.GetStringArg("description")
                    )
                );
            }
            else if (
                call.MethodName == "Returns"
                && call.TypeArgs.Count == 1
                && call.GetStringArg("statusKey") is { } typedStatusKey
            )
            {
                responses.Add(
                    new TsResponseType(
                        ParseStatusCode(typedStatusKey),
                        typeWalker.MapType(call.TypeArgs[0]),
                        call.GetStringArg("description"),
                        StatusKey: typedStatusKey
                    )
                );
            }
            else if (
                call.MethodName == "Returns"
                && call.TypeArgs.Count == 0
                && call.StatusCodeArg is not null
            )
            {
                responses.Add(
                    new TsResponseType(
                        call.StatusCodeArg.Value,
                        null,
                        call.GetStringArg("description")
                    )
                );
            }
            else if (
                call.MethodName == "Returns"
                && call.TypeArgs.Count == 0
                && call.GetStringArg("statusKey") is { } statusKey
            )
            {
                responses.Add(
                    new TsResponseType(
                        ParseStatusCode(statusKey),
                        null,
                        call.GetStringArg("description"),
                        StatusKey: statusKey
                    )
                );
            }
            else if (
                call.MethodName is "WithResponseHeader" or "WithResponseHeaderKey"
                && call.GetStringArg("name") is { } responseHeaderName
            )
            {
                // The convenience overload has no statusCode arg — null targets the
                // success response, resolved after the responses list is built.
                responseHeaderCalls.Add(
                    new PendingResponseHeaderCall(
                        call.GetIntArg("statusCode")?.ToString() ?? call.GetStringArg("statusKey"),
                        responseHeaderName,
                        call.TypeArgs.Count == 1
                            ? ApplyParameterMetadata(
                                typeWalker.MapType(call.TypeArgs[0]),
                                call.GetStringArg("schemaType"),
                                call.GetStringArg("format")
                            )
                            : new TsType.Primitive("string"),
                        call.GetStringArg("description"),
                        call.GetBoolArg("required") ?? false,
                        ParseJsonArgument(call.GetStringArg("schemaExamplesJson")),
                        ParseJsonArgument(call.GetStringArg("exampleJson")),
                        ParseJsonArgument(call.GetStringArg("examplesJson")),
                        call.GetBoolArg("deprecated") ?? false,
                        call.GetStringArg("style"),
                        call.GetBoolArg("explode"),
                        call.GetBoolArg("allowReserved") ?? false,
                        call.GetBoolArg("allowEmptyValue") ?? false,
                        call.GetStringArg("contentType")
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
            else if (call.MethodName == "SuppressImplicitResponse")
            {
                suppressImplicitResponse = true;
            }
            else if (
                call.MethodName == "StatusKey"
                && call.GetStringArg("statusKey") is { } primaryStatusKey
            )
            {
                successStatusKey = primaryStatusKey;
                successResponseDescription = call.GetStringArg("description");
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
            else if (call.MethodName == "SecurityRequirements")
            {
                securityRequirements = new SecurityRequirements([]);
            }
            else if (
                call.MethodName == "SecurityRequirement"
                && call.GetIntArg("requirementOrder") is int requirementOrder
            )
            {
                securityRequirementOrders.Add(requirementOrder);
                if (call.GetStringArg("scheme") is not { } requirementScheme)
                {
                    continue;
                }

                if (!securityRequirementSchemes.TryGetValue(requirementOrder, out var schemes))
                {
                    schemes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    securityRequirementSchemes.Add(requirementOrder, schemes);
                }
                if (!schemes.TryGetValue(requirementScheme, out var scopes))
                {
                    scopes = [];
                    schemes.Add(requirementScheme, scopes);
                }
                if (call.GetStringArg("scope") is { } scope)
                {
                    scopes.Add(scope);
                }
            }
            else if (
                call.MethodName == "RequestContent"
                && call.TypeArgs.Count == 1
                && call.GetStringArg("mediaType") is { } requestMediaType
            )
            {
                var schemaType = call.GetStringArg("schemaType");
                var format = call.GetStringArg("format");
                requestContents.Add(
                    new TsMediaTypeContent(
                        requestMediaType,
                        typeWalker.ApplyGeneratedSchemaRef(
                            ApplyParameterMetadata(
                                typeWalker.MapType(call.TypeArgs[0]),
                                schemaType,
                                format
                            ),
                            call.GetStringArg("schemaRef"),
                            $"Request content '{requestMediaType}' on endpoint '{name}'"
                        ),
                        SchemaType: schemaType,
                        Format: format == "" ? null : format,
                        IsFormatSpecified: format is not null
                    )
                );
                requestBodyPresent = true;
                requestContentsAuthoritative = true;
            }
            else if (
                call.MethodName == "RequestContent"
                && call.TypeArgs.Count == 0
                && call.GetStringArg("mediaType") is { } schemaLessRequestMediaType
            )
            {
                requestContents.Add(new TsMediaTypeContent(schemaLessRequestMediaType, null));
                requestBodyPresent = true;
                requestContentsAuthoritative = true;
            }
            else if (
                call.MethodName == "RequestBinaryContent"
                && call.GetStringArg("mediaType") is { } binaryRequestMediaType
            )
            {
                if (
                    !requestContents.Any(content =>
                        content.MediaType == binaryRequestMediaType && content.IsBinary
                    )
                )
                {
                    requestContents.Add(
                        new TsMediaTypeContent(binaryRequestMediaType, null, IsBinary: true)
                    );
                }
                requestBodyPresent = true;
                requestContentsAuthoritative = true;
            }
            else if (call.MethodName == "RequestBodyRequired")
            {
                requestBodyRequired = call.GetBoolArg("required");
                requestBodyPresent = true;
            }
            else if (call.MethodName == "RequestBody")
            {
                requestBodyPresent = true;
                requestContentsAuthoritative = true;
            }
            else if (
                call.MethodName == "Parameter"
                && call.TypeArgs.Count == 1
                && call.GetStringArg("name") is { } parameterName
                && call.GetStringArg("location") is { } parameterLocation
                && call.GetBoolArg("required") is { } parameterRequired
            )
            {
                var source = parameterLocation.ToLowerInvariant() switch
                {
                    "path" => ParamSource.Route,
                    "query" => ParamSource.Query,
                    "header" => ParamSource.Header,
                    "cookie" => ParamSource.Cookie,
                    _ => throw new ContractAnalysisException(
                        $"Endpoint '{name}' declares unsupported parameter location '{parameterLocation}'."
                    ),
                };
                var metadata = ParseParameterMetadata(call.GetStringArg("metadataJson"));
                var parameterType = ApplyParameterMetadata(
                    typeWalker.MapType(call.TypeArgs[0]),
                    call.GetStringArg("schemaType"),
                    call.GetStringArg("format")
                );
                parameterType = typeWalker.ApplyGeneratedSchemaRef(
                    parameterType,
                    call.GetStringArg("schemaRef"),
                    $"Parameter '{parameterName}' on endpoint '{name}'"
                );
                if (parameterType is TsType.Array array && metadata.ItemMetadata is not null)
                {
                    parameterType = array with { ElementMetadata = metadata.ItemMetadata };
                }
                declaredParameters.Add(
                    new TsEndpointParam(
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
                        SchemaType: call.GetStringArg("schemaType"),
                        Format: call.GetStringArg("format") is ""
                            ? null
                            : call.GetStringArg("format"),
                        IsFormatSpecified: call.GetStringArg("format") is not null,
                        AllowEmptyValue: metadata.AllowEmptyValue
                    )
                );
            }
            else if (
                call.MethodName == "ResponseContent"
                && call.TypeArgs.Count == 1
                && call.GetIntArg("statusCode") is int contentStatusCode
                && call.GetStringArg("mediaType") is { } responseMediaType
            )
            {
                var schemaType = call.GetStringArg("schemaType");
                var format = call.GetStringArg("format");
                responseContents.Add(
                    (
                        contentStatusCode.ToString(),
                        new TsMediaTypeContent(
                            responseMediaType,
                            typeWalker.ApplyGeneratedSchemaRef(
                                ApplyParameterMetadata(
                                    typeWalker.MapType(call.TypeArgs[0]),
                                    schemaType,
                                    format
                                ),
                                call.GetStringArg("schemaRef"),
                                $"Response content '{responseMediaType}' on endpoint '{name}'"
                            ),
                            SchemaType: schemaType,
                            Format: format == "" ? null : format,
                            IsFormatSpecified: format is not null,
                            SchemaDescription: call.GetStringArg("schemaDescription")
                        )
                    )
                );
            }
            else if (
                call.MethodName == "ResponseContent"
                && call.TypeArgs.Count == 0
                && call.GetIntArg("statusCode") is int schemaLessContentStatusCode
                && call.GetStringArg("mediaType") is { } schemaLessResponseMediaType
            )
            {
                responseContents.Add(
                    (
                        schemaLessContentStatusCode.ToString(),
                        new TsMediaTypeContent(schemaLessResponseMediaType, null)
                    )
                );
            }
            else if (
                call.MethodName == "ResponseBinaryContent"
                && call.GetIntArg("statusCode") is int binaryContentStatusCode
                && call.GetStringArg("mediaType") is { } binaryResponseMediaType
            )
            {
                responseContents.Add(
                    (
                        binaryContentStatusCode.ToString(),
                        new TsMediaTypeContent(binaryResponseMediaType, null, IsBinary: true)
                    )
                );
            }
            else if (
                call.MethodName == "ResponseContent"
                && call.TypeArgs.Count == 1
                && call.GetStringArg("statusKey") is { } typedContentStatusKey
                && call.GetStringArg("mediaType") is { } typedResponseMediaType
            )
            {
                var schemaType = call.GetStringArg("schemaType");
                var format = call.GetStringArg("format");
                responseContents.Add(
                    (
                        typedContentStatusKey,
                        new TsMediaTypeContent(
                            typedResponseMediaType,
                            typeWalker.ApplyGeneratedSchemaRef(
                                ApplyParameterMetadata(
                                    typeWalker.MapType(call.TypeArgs[0]),
                                    schemaType,
                                    format
                                ),
                                call.GetStringArg("schemaRef"),
                                $"Response content '{typedResponseMediaType}' on endpoint '{name}'"
                            ),
                            SchemaType: schemaType,
                            Format: format == "" ? null : format,
                            IsFormatSpecified: format is not null,
                            SchemaDescription: call.GetStringArg("schemaDescription")
                        )
                    )
                );
            }
            else if (
                call.MethodName == "ResponseContent"
                && call.TypeArgs.Count == 0
                && call.GetStringArg("statusKey") is { } contentStatusKey
                && call.GetStringArg("mediaType") is { } schemaLessResponseMediaTypeByKey
            )
            {
                responseContents.Add(
                    (
                        contentStatusKey,
                        new TsMediaTypeContent(schemaLessResponseMediaTypeByKey, null)
                    )
                );
            }
            else if (
                call.MethodName == "ResponseBinaryContent"
                && call.GetStringArg("statusKey") is { } binaryStatusKey
                && call.GetStringArg("mediaType") is { } binaryResponseMediaTypeByKey
            )
            {
                responseContents.Add(
                    (
                        binaryStatusKey,
                        new TsMediaTypeContent(binaryResponseMediaTypeByKey, null, IsBinary: true)
                    )
                );
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
                        responseStatusCode.ToString(),
                        call.GetStringArg("name"),
                        call.GetStringArg("mediaType"),
                        call.GetStringArg("json"),
                        null,
                        null,
                        ParseReferencedComponents(call.GetStringArg("referencedComponentsJson"))
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
                        refStatusCode.ToString(),
                        call.GetStringArg("name"),
                        call.GetStringArg("mediaType"),
                        null,
                        call.GetStringArg("componentExampleId"),
                        call.GetStringArg("resolvedJson"),
                        ParseReferencedComponents(call.GetStringArg("referencedComponentsJson"))
                    )
                );
            }
            else if (
                call.MethodName == "ResponseExampleJson"
                && call.GetStringArg("statusKey") is { } exampleStatusKey
                && call.GetStringArg("json") is not null
            )
            {
                responseExampleCalls.Add(
                    new PendingEndpointExampleCall(
                        exampleStatusKey,
                        call.GetStringArg("name"),
                        call.GetStringArg("mediaType"),
                        call.GetStringArg("json"),
                        null,
                        null,
                        ParseReferencedComponents(call.GetStringArg("referencedComponentsJson"))
                    )
                );
            }
            else if (
                call.MethodName == "ResponseExampleRef"
                && call.GetStringArg("statusKey") is { } refStatusKey
                && call.GetStringArg("componentExampleId") is not null
                && call.GetStringArg("resolvedJson") is not null
            )
            {
                responseExampleCalls.Add(
                    new PendingEndpointExampleCall(
                        refStatusKey,
                        call.GetStringArg("name"),
                        call.GetStringArg("mediaType"),
                        null,
                        call.GetStringArg("componentExampleId"),
                        call.GetStringArg("resolvedJson"),
                        ParseReferencedComponents(call.GetStringArg("referencedComponentsJson"))
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
        // The runtime contract keeps the original type for response validation, but the TS client gets Blob
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
        var (builtParameters, inputTypeName) = BuildParams(
            wkt,
            httpMethod,
            route,
            tInput,
            field,
            compilation,
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

        requestBodyRequired ??= GetRequestBodyRequired(field, compilation);

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
        ApplyResponseExamples(responses, responseExampleCalls, fileContentType, name);
        ApplyResponseHeaders(
            responses,
            responseHeaderCalls,
            successStatusOverride
                ?? DefaultSuccessCode(httpMethod, hasOutput: returnType is not null),
            successStatusKey,
            name
        );
        ApplyResponseContents(responses, responseContents);

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
            call.ResolvedJson,
            call.ReferencedComponents
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

        foreach (
            var group in responseExampleCalls.GroupBy(
                call => call.StatusKey!,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            var mappedExamples = group
                .Select(call =>
                    ToEndpointExample(
                        call,
                        DefaultResponseExampleMediaType(ParseStatusCode(group.Key), fileContentType)
                    )
                )
                .ToList();

            var responseIndex = responses.FindIndex(response =>
                response.EffectiveStatusKey.Equals(group.Key, StringComparison.OrdinalIgnoreCase)
            );
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
        Compilation compilation,
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
                        typeWalker.MapPropertyType(prop),
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
                        paramType = typeWalker.MapPropertyType(matchingProp);
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
                                    ParamSource.File,
                                    IsOptional: TypeWalker.IsOptionalProperty(prop)
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
                                    ParamSource.File,
                                    IsOptional: TypeWalker.IsOptionalProperty(prop)
                                )
                            );
                        }
                        else
                        {
                            // Non-file properties on a mixed upload record → form fields
                            parameters.Add(
                                new TsEndpointParam(
                                    tsName,
                                    typeWalker.MapPropertyType(prop),
                                    ParamSource.FormField,
                                    IsOptional: TypeWalker.IsOptionalProperty(prop)
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

                    var tsType = typeWalker.MapPropertyType(prop);
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

    private static TsType ApplyParameterMetadata(TsType type, string? schemaType, string? format)
    {
        var explicitFormat = format == "" ? null : format;
        return type switch
        {
            TsType.Primitive primitive => primitive with
            {
                Name = schemaType ?? primitive.Name,
                Format = format is null ? primitive.Format : explicitFormat,
            },
            TsType.Nullable { Inner: TsType.Primitive primitive } => new TsType.Nullable(
                primitive with
                {
                    Name = schemaType ?? primitive.Name,
                    Format = format is null ? primitive.Format : explicitFormat,
                }
            ),
            _ => type,
        };
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

    private static bool? GetRequestBodyRequired(IFieldSymbol field, Compilation compilation)
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
        if (attribute is null)
        {
            return null;
        }

        return attribute.ConstructorArguments.Length < 2
            || attribute.ConstructorArguments[1].Value is not false;
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
