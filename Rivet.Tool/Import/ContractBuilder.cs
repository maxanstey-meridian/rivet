using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

/// <summary>
/// Maps OpenAPI paths to v1 static contract class intermediates grouped by tag.
/// </summary>
internal static class ContractBuilder
{
    private static readonly HashSet<HttpMethod> _supportedMethods =
    [
        HttpMethod.Get,
        HttpMethod.Head,
        HttpMethod.Post,
        HttpMethod.Put,
        HttpMethod.Patch,
        HttpMethod.Delete,
        HttpMethod.Options,
    ];

    /// <summary>
    /// Group operations by tag, return one contract per tag.
    /// </summary>
    public static IReadOnlyList<GeneratedContract> BuildContracts(
        OpenApiPaths paths,
        SchemaMapper mapper,
        string? globalSecurityScheme,
        List<string> warnings,
        IDictionary<string, IOpenApiExample>? componentExamples = null,
        IReadOnlyDictionary<
            (string Path, string Method),
            OpenApiOperationProvenance
        >? operationProvenance = null
    )
    {
        var groups = new Dictionary<string, List<GeneratedEndpointField>>();

        foreach (var (route, pathItem) in paths)
        {
            foreach (var (method, operation) in pathItem.Operations ?? [])
            {
                if (!_supportedMethods.Contains(method))
                {
                    // TRACE has no contract representation; never drop it silently.
                    warnings.Add(
                        Diagnostics.Prefix(
                            Diagnostics.ImportOperationMethodDropped,
                            $"Operation dropped: {method.Method.ToUpperInvariant()} {route} — HTTP method has no contract representation."
                        )
                    );
                    continue;
                }

                var httpMethod = method.Method.ToLowerInvariant();
                var tag = ExtractTag(operation) ?? "Default";

                // WP-1.1: prefer the explicit x-rivet-contract extension — the tag
                // convention is lossy for unusual casing (underscores, acronyms) and
                // breaks under hand-edits. Convention stays as the fallback.
                var contractKey = GetOperationExtensionString(operation, "x-rivet-contract")
                    is { } contractExt
                    ? Naming.StripInvalidIdentifierChars(contractExt)
                    : tag;

                var field = BuildEndpointField(
                    componentExamples,
                    httpMethod,
                    route,
                    operation,
                    MergeParameters(pathItem.Parameters, operation.Parameters),
                    tag,
                    globalSecurityScheme,
                    mapper,
                    warnings,
                    operationProvenance is not null
                    && operationProvenance.TryGetValue((route, httpMethod), out var provenance)
                        ? provenance
                        : null
                );

                if (!groups.TryGetValue(contractKey, out var list))
                {
                    list = [];
                    groups[contractKey] = list;
                }

                list.Add(field);
            }
        }

        return groups
            .OrderBy(g => g.Key)
            .Select(g => new GeneratedContract(
                g.Key,
                $"{g.Key}Contract",
                DeduplicateFields(g.Value)
            ))
            .ToList();
    }

    private static GeneratedEndpointField BuildEndpointField(
        IDictionary<string, IOpenApiExample>? componentExamples,
        string httpMethod,
        string route,
        OpenApiOperation operation,
        IReadOnlyList<IOpenApiParameter> parameters,
        string tag,
        string? globalSecurityScheme,
        SchemaMapper mapper,
        List<string> warnings,
        OpenApiOperationProvenance? provenance
    )
    {
        var operationId = operation.OperationId;

        // WP-1.1: prefer the explicit x-rivet-endpoint extension over the
        // operationId/tag-prefix convention (lossy for unusual casing).
        var fieldName = GetOperationExtensionString(operation, "x-rivet-endpoint")
            is { } endpointExt
            ? Naming.StripInvalidIdentifierChars(endpointExt)
            : DeriveFieldName(operationId, httpMethod, route, tag);
        var method = Naming.ToPascalCaseFromSegments(httpMethod);
        var summary = string.IsNullOrEmpty(operation.Summary) ? null : operation.Summary;
        var description = string.IsNullOrEmpty(operation.Description)
            ? null
            : operation.Description;
        var unsupported = new List<string>();

        // QueryAuth: read x-rivet-query-auth extension
        var queryAuthParameterName = ResolveQueryAuth(operation);

        // Resolve input type (requestBody — $ref resolved by library)
        var (inputType, isFormEncoded, binaryRequestContentType, requestContentType) =
            ResolveInputType(operation, mapper, fieldName, unsupported);
        var requestContents = ResolveRequestContents(operation.RequestBody, mapper, fieldName);
        var inputTypeFromBody = inputType is not null;

        // I14: parameters must be resolved regardless of body presence — they used to be
        // silently discarded whenever the operation had a request body (262 Stripe GETs
        // lost every path+query param). Path/query params merge with the body-derived
        // input record; when a true merge is structurally impossible (opaque body type)
        // the loser is dropped LOUDLY via a named marker — never silently.
        var paramProperties = CollectParamProperties(
            parameters,
            mapper,
            fieldName,
            unsupported,
            queryAuthParameterName,
            warnings,
            httpMethod,
            route
        );

        var explicitParameters = BuildExplicitParameters(
            parameters,
            paramProperties,
            mapper,
            fieldName
        );
        if (inputType is null)
        {
            inputType = SynthesizeParamInputType(paramProperties, mapper, fieldName);
        }
        string? requestBodyType = null;

        // FABLE_ROUNDTRIP #7: an optional request body (required:false — the
        // OpenAPI default) is modeled by a nullable TInput; the emitter's E11
        // rule re-emits it as required:false. Only for pure-body inputs on
        // body-carrying methods: a record that merged required path/query
        // params cannot be nullable as a whole, so that case stays loud.
        var bodyIsOptional =
            inputTypeFromBody
            && httpMethod is "post" or "put" or "patch"
            && operation.RequestBody is { Required: false };
        if (bodyIsOptional && inputType is not null && !inputType.EndsWith('?'))
        {
            inputType += "?";
        }

        // Resolve output type (lowest 2xx response with JSON content)
        var (outputType, successStatus, fileContentType, responseContentType) = ResolveOutputType(
            operation,
            mapper,
            fieldName
        );
        var successStatusKey = successStatus?.ToString();
        var successResponseDescription =
            successStatusKey is not null
            && operation.Responses?.TryGetValue(successStatusKey, out var primaryResponse) is true
                ? primaryResponse.Description
                : null;
        var responseContents = ResolveResponseContents(operation, mapper, fieldName);

        // File endpoint: binary content type on a GET endpoint → Define.File()
        // Non-GET binary endpoints (e.g. POST → PDF) keep Define.{Method}().ProducesFile()
        var isFileEndpoint =
            fileContentType is not null
            && httpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);

        // Error responses
        var errorResponses = ResolveErrorResponses(operation, mapper, fieldName, successStatus);
        var requestExamples = ResolveRequestExamples(operation, unsupported, componentExamples);
        var responseExamples = ResolveResponseExamples(operation, unsupported, componentExamples);
        // P2 wave 5: response headers re-emit as .WithResponseHeader(...) chain calls —
        // resolved AFTER the declared-status set is final (success + error responses).
        var responseHeaders = ResolveResponseHeaders(
            operation,
            mapper,
            successStatus,
            errorResponses,
            unsupported
        );

        // Security
        var (isAnonymous, securityScheme, securityRequirements) = ResolveSecurity(
            operation,
            globalSecurityScheme
        );

        return new GeneratedEndpointField(
            fieldName,
            method,
            route,
            inputType,
            outputType,
            summary,
            description,
            successStatus,
            successStatusKey,
            successResponseDescription,
            errorResponses,
            isAnonymous,
            securityScheme,
            unsupported,
            fileContentType,
            isFormEncoded,
            requestExamples,
            responseExamples,
            isFileEndpoint,
            queryAuthParameterName,
            responseHeaders,
            binaryRequestContentType,
            requestContentType,
            responseContentType,
            requestBodyType,
            operation.RequestBody is null ? null : operation.RequestBody.Required,
            operation.RequestBody is not null,
            securityRequirements,
            requestContents,
            responseContents,
            explicitParameters,
            SuppressImplicitResponse: successStatus is null,
            Provenance: provenance
        );
    }

    private static IReadOnlyList<IOpenApiParameter> MergeParameters(
        IList<IOpenApiParameter>? pathParameters,
        IList<IOpenApiParameter>? operationParameters
    )
    {
        var merged =
            new Dictionary<(string Name, ParameterLocation? Location), IOpenApiParameter>();
        foreach (var parameter in pathParameters ?? [])
        {
            merged[(parameter.Name ?? "", parameter.In)] = parameter;
        }
        foreach (var parameter in operationParameters ?? [])
        {
            merged[(parameter.Name ?? "", parameter.In)] = parameter;
        }

        return merged.Values.ToList();
    }

    private static IReadOnlyList<GeneratedEndpointParameter> BuildExplicitParameters(
        IReadOnlyList<IOpenApiParameter> sourceParameters,
        IReadOnlyList<ParamProperty> parameters,
        SchemaMapper mapper,
        string fieldName
    ) =>
        parameters
            .Select(parameter =>
            {
                var sourceParameter = sourceParameters.First(candidate =>
                    candidate.Name == parameter.OriginalName
                    && candidate.In is { } location
                    && location
                        .ToString()
                        .Equals(parameter.Location, StringComparison.OrdinalIgnoreCase)
                );
                var sourceSchema = sourceParameter.Schema!;
                var schemaRef = mapper.ResolveScalarReferenceName(sourceSchema);
                var leaf = schemaRef is null ? ResolveInlineScalarLeaf(sourceSchema) : null;
                var typeName = mapper.ResolveCSharpType(
                    sourceSchema,
                    $"{fieldName}{Naming.ToPascalCaseFromSegments(parameter.OriginalName)}Parameter"
                );
                return new GeneratedEndpointParameter(
                    parameter.OriginalName,
                    parameter.Location,
                    typeName,
                    parameter.Property.IsRequired,
                    leaf?.SchemaType,
                    leaf?.Format,
                    leaf is not null,
                    BuildParameterMetadataJson(
                        sourceParameter,
                        sourceSchema,
                        includeSchemaMetadata: schemaRef is null
                    ),
                    schemaRef
                );
            })
            .ToList();

    internal static IReadOnlyList<OpenApiComponentRequestBodyProvenance> BuildRequestBodyComponents(
        IDictionary<string, IOpenApiRequestBody>? requestBodies,
        SchemaMapper mapper,
        List<string> warnings,
        IDictionary<string, IOpenApiExample>? componentExamples
    )
    {
        if (requestBodies is not { Count: > 0 })
        {
            return [];
        }

        return requestBodies
            .Select(pair => new OpenApiComponentRequestBodyProvenance(
                pair.Key,
                pair.Value.Description,
                pair.Value.Required,
                ResolveRequestContents(pair.Value, mapper, $"{pair.Key}Component")
                    .Select(content => new OpenApiRequestBodyContentProvenance(
                        content.MediaType,
                        content.TypeName,
                        null,
                        content.IsBinary,
                        content.SchemaRef,
                        content.SchemaType,
                        content.Format,
                        content.IsFormatSpecified
                    ))
                    .ToList(),
                pair.Value.Content is { Count: > 0 } content
                    ? ResolveMediaExamples(
                        content,
                        warnings,
                        $"request-body-component name={pair.Key}",
                        componentExamples
                    )
                    : []
            ))
            .ToList();
    }

    private static IReadOnlyList<GeneratedMediaTypeContent> ResolveRequestContents(
        IOpenApiRequestBody? requestBody,
        SchemaMapper mapper,
        string fieldName
    )
    {
        if (requestBody?.Content is not { Count: > 0 } content)
        {
            return [];
        }

        var result = new List<GeneratedMediaTypeContent>();
        var index = 0;
        foreach (var (mediaType, media) in content)
        {
            if (media.Schema is null)
            {
                result.Add(new GeneratedMediaTypeContent(mediaType, null));
                continue;
            }
            if (
                IsRawBinarySchema(media.Schema)
                && !mediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            )
            {
                result.Add(new GeneratedMediaTypeContent(mediaType, null, IsBinary: true));
                continue;
            }

            var typeName = mapper.ResolveCSharpType(
                media.Schema,
                $"{Naming.ToPascalCaseFromSegments(fieldName)}RequestContent{index++}"
            );
            var schemaRef = mapper.ResolveScalarReferenceName(media.Schema);
            var leaf = schemaRef is null ? ResolveInlineScalarLeaf(media.Schema) : null;
            result.Add(
                new GeneratedMediaTypeContent(
                    mediaType,
                    typeName,
                    SchemaRef: schemaRef,
                    SchemaType: leaf?.SchemaType,
                    Format: leaf?.Format,
                    IsFormatSpecified: leaf is not null
                )
            );
        }

        return result;
    }

    private static IReadOnlyList<GeneratedResponseMediaTypeContent> ResolveResponseContents(
        OpenApiOperation operation,
        SchemaMapper mapper,
        string fieldName
    )
    {
        var result = new List<GeneratedResponseMediaTypeContent>();
        var index = 0;
        foreach (var (status, response) in operation.Responses ?? [])
        {
            var statusCode = int.TryParse(status, out var parsed) ? parsed : 0;
            if (response.Content is not { Count: > 0 } content)
            {
                continue;
            }

            foreach (var (mediaType, media) in content)
            {
                if (media.Schema is null)
                {
                    result.Add(
                        new GeneratedResponseMediaTypeContent(statusCode, status, mediaType, null)
                    );
                    continue;
                }
                if (IsRawBinarySchema(media.Schema))
                {
                    result.Add(
                        new GeneratedResponseMediaTypeContent(
                            statusCode,
                            status,
                            mediaType,
                            null,
                            IsBinary: true
                        )
                    );
                    continue;
                }

                var typeName = mapper.ResolveCSharpType(
                    media.Schema,
                    $"{Naming.ToPascalCaseFromSegments(fieldName)}Response{Naming.ToPascalCaseFromSegments(status)}Content{index++}"
                );
                var schemaRef = mapper.ResolveScalarReferenceName(media.Schema);
                var leaf = schemaRef is null ? ResolveInlineScalarLeaf(media.Schema) : null;
                result.Add(
                    new GeneratedResponseMediaTypeContent(
                        statusCode,
                        status,
                        mediaType,
                        typeName,
                        SchemaRef: schemaRef,
                        SchemaType: leaf?.SchemaType,
                        Format: leaf?.Format,
                        IsFormatSpecified: leaf is not null,
                        SchemaDescription: media.Schema is OpenApiSchemaReference
                            ? null
                            : media.Schema.Description
                    )
                );
            }
        }

        return result;
    }

    private static ScalarLeafProvenance? ResolveInlineScalarLeaf(IOpenApiSchema schema)
    {
        if (
            schema is OpenApiSchemaReference
            || schema.AllOf is { Count: > 0 }
            || schema.OneOf is { Count: > 0 }
            || schema.AnyOf is { Count: > 0 }
            || schema.Not is not null
            || schema.Const is not null
            || schema.Enum is { Count: > 0 }
            || schema.Type is not { } declared
        )
        {
            return null;
        }

        var schemaType = (declared & ~JsonSchemaType.Null) switch
        {
            JsonSchemaType.String => "string",
            JsonSchemaType.Integer => "integer",
            JsonSchemaType.Number => "number",
            JsonSchemaType.Boolean => "boolean",
            _ => null,
        };
        return schemaType is null ? null : new ScalarLeafProvenance(schemaType, schema.Format);
    }

    private sealed record ScalarLeafProvenance(string SchemaType, string? Format);

    /// <summary>
    /// P2 wave 5: response headers (previously out-of-scope) become .WithResponseHeader()
    /// calls. Headers on a status the contract cannot declare are dropped loudly.
    /// </summary>
    private static IReadOnlyList<GeneratedResponseHeader> ResolveResponseHeaders(
        OpenApiOperation operation,
        SchemaMapper mapper,
        int? successStatus,
        IReadOnlyList<GeneratedErrorResponse> errorResponses,
        List<string> unsupported
    )
    {
        if (operation.Responses is null)
        {
            return [];
        }

        var declaredStatuses = errorResponses
            .Select(error => error.StatusKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (successStatus is not null)
        {
            declaredStatuses.Add(successStatus.Value.ToString());
        }

        var headers = new List<GeneratedResponseHeader>();

        foreach (var (statusStr, response) in operation.Responses)
        {
            if (response.Headers is not { Count: > 0 })
            {
                continue;
            }
            var statusCode = int.TryParse(statusStr, out var parsed) ? parsed : 0;

            foreach (var (name, header) in response.Headers)
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!declaredStatuses.Contains(statusStr))
                {
                    unsupported.Add(
                        $"header name={name} status={statusStr} reason=undeclared-status"
                    );
                    continue;
                }

                if (
                    headers.Any(existing =>
                        existing.StatusCode == statusCode
                        && string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    continue;
                }

                var contentEntry = header.Content is { Count: > 0 }
                    ? header.Content.First()
                    : default(KeyValuePair<string, OpenApiMediaType>);
                var contentType = contentEntry.Key;
                var schema = contentType is null ? header.Schema : contentEntry.Value.Schema;
                var typeName = schema is null
                    ? "string"
                    : mapper.ResolveCSharpType(schema, $"response header {name}");
                var format = schema is null ? null : mapper.ResolveFormat(schema);
                var isFormatSpecified =
                    format is not null
                    || typeName.TrimEnd('?')
                        is "sbyte"
                            or "byte"
                            or "short"
                            or "ushort"
                            or "int"
                            or "uint"
                            or "long"
                            or "ulong"
                            or "float"
                            or "double"
                            or "decimal";

                headers.Add(
                    new GeneratedResponseHeader(
                        statusCode,
                        statusStr,
                        name,
                        typeName,
                        schema is null ? null : mapper.ResolveSchemaType(schema),
                        format,
                        isFormatSpecified,
                        header.Description,
                        header.Required,
                        SchemaExamplesJson: BuildSchemaExamplesNode(schema)?.ToJsonString(),
                        ExampleJson: header.Example is null
                            ? null
                            : OpenApiJsonNodeSerializer.Serialize(header.Example),
                        ExamplesJson: BuildExamplesNode(header.Examples)?.ToJsonString(),
                        Deprecated: header.Deprecated,
                        Style: header.Style is { } style && style != ParameterStyle.Simple
                            ? ParameterStyleName(style)
                            : null,
                        Explode: header.Explode is true ? true : null,
                        AllowReserved: header.AllowReserved,
                        AllowEmptyValue: header.AllowEmptyValue,
                        ContentType: contentType
                    )
                );
            }
        }

        return headers;
    }

    private static (
        string? InputType,
        bool IsFormEncoded,
        string? BinaryRequestContentType,
        string? RequestContentType
    ) ResolveInputType(
        OpenApiOperation operation,
        SchemaMapper mapper,
        string fieldName,
        List<string> unsupported
    )
    {
        var requestBody = operation.RequestBody;
        if (requestBody is null)
        {
            return (null, false, null, null);
        }

        // A $ref request body that the library could not resolve has no content —
        // never drop it silently (I11 class): leave a loud marker on the endpoint.
        if (requestBody is OpenApiRequestBodyReference { Target: null } unresolvedRef)
        {
            var refId = unresolvedRef.Reference?.Id ?? "unknown";
            unsupported.Add($"body $ref={refId} reason=unresolved-ref");
            return (null, false, null, null);
        }

        var content = requestBody.Content;
        if (content is null)
        {
            return (null, false, null, null);
        }

        // Try content types in priority order, tracking which one matched
        IOpenApiSchema? schema = null;
        var isFormEncoded = false;

        if (TryGetSchemaForContentType(content, "application/json", out schema))
        {
            // JSON — default
        }
        else if (
            TryGetSchemaForContentType(content, "application/x-www-form-urlencoded", out schema)
        )
        {
            isFormEncoded = true;
        }
        else if (
            TryGetSchemaForContentType(content, "multipart/form-data", out schema)
            || TryGetSchemaForContentType(content, "*/*", out schema)
        )
        {
            // multipart or wildcard — not form-encoded
        }

        if (schema is not null)
        {
            // x-rivet-input-type preserves the original record name through round-trips.
            // The convention fallback is segment-pascalized: underscores in a component
            // name are treated as delimiters on the next import, so a synthesized name
            // containing them would mutate every loop.
            var context =
                GetExtensionString(schema, "x-rivet-input-type")
                ?? $"{Naming.ToPascalCaseFromSegments(fieldName)}Request";
            return (mapper.ResolveCSharpType(schema, context), isFormEncoded, null, null);
        }

        // Raw binary body: a non-multipart content entry whose schema is
        // { type: string, format: binary } imports as .AcceptsBinary("<ct>") — the
        // body never becomes a TInput; path/query params import as normal.
        var binaryEntry = content.FirstOrDefault(entry =>
            !entry.Key.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            && IsRawBinarySchema(entry.Value.Schema)
        );
        if (binaryEntry.Key is not null)
        {
            return (null, false, binaryEntry.Key, null);
        }

        // Fallback: try binary or text content types with a schema
        var fallbackType = content.Keys.FirstOrDefault(k =>
            IsBinaryContentType(k)
            || k.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("application/x-", StringComparison.OrdinalIgnoreCase)
        );
        if (
            fallbackType is not null
            && TryGetSchemaForContentType(content, fallbackType, out schema)
        )
        {
            // FABLE_ROUNDTRIP #10: a text/* body keeps its media type via
            // .AcceptsContentType(...) — re-emitting it as application/json
            // was a silent wire change (the octet-stream bug's sibling).
            var requestContentType = fallbackType.StartsWith(
                "text/",
                StringComparison.OrdinalIgnoreCase
            )
                ? fallbackType
                : null;
            return (
                mapper.ResolveCSharpType(schema!, $"{fieldName}Request"),
                false,
                null,
                requestContentType
            );
        }

        // The complete content map is persisted independently of the primary runtime
        // input type. Schema-less and non-standard media types therefore need no
        // synthetic TInput and are not degraded here.
        return (null, false, null, null);
    }

    /// <summary>
    /// A schema of exactly { type: string, format: binary } — OpenAPI 3.x's encoding
    /// for a raw binary request body.
    /// </summary>
    private static bool IsRawBinarySchema(IOpenApiSchema? schema) =>
        schema is not null
        && schema.Type.HasValue
        && schema.Type.Value.HasFlag(JsonSchemaType.String)
        && string.Equals(schema.Format, "binary", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A path/query/header/cookie parameter resolved to a record property, keeping the
    /// original spec name and location for diagnostics.
    /// </summary>
    private sealed record ParamProperty(
        RecordProperty Property,
        string OriginalName,
        string Location
    );

    private static List<ParamProperty> CollectParamProperties(
        IReadOnlyList<IOpenApiParameter> sourceParameters,
        SchemaMapper mapper,
        string fieldName,
        List<string> unsupported,
        string? queryAuthParameterName,
        List<string> warnings,
        string httpMethod,
        string route
    )
    {
        if (sourceParameters.Count == 0)
        {
            return [];
        }

        var properties = new List<ParamProperty>();

        foreach (var param in sourceParameters)
        {
            if (
                param.In
                is not (
                    ParameterLocation.Path
                    or ParameterLocation.Query
                    or ParameterLocation.Header
                    or ParameterLocation.Cookie
                )
            )
            {
                continue;
            }

            var location = param.In switch
            {
                ParameterLocation.Path => "path",
                ParameterLocation.Query => "query",
                ParameterLocation.Header => "header",
                _ => "cookie",
            };

            if (string.IsNullOrEmpty(param.Name))
            {
                warnings.Add(
                    Diagnostics.Prefix(
                        Diagnostics.ImportEmptyParameterNameDropped,
                        $"Parameter dropped: {httpMethod.ToUpperInvariant()} {route} has an empty name (in={location}); OpenAPI parameters require a non-empty name."
                    )
                );
                continue;
            }

            if (
                param.In is ParameterLocation.Header
                && param.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            )
            {
                warnings.Add(
                    Diagnostics.Prefix(
                        Diagnostics.ImportReservedContentTypeHeaderDropped,
                        $"Reserved header parameter dropped: {httpMethod.ToUpperInvariant()} {route} declares '{param.Name}'; request media types are represented by requestBody.content."
                    )
                );
                continue;
            }

            if (param.Schema is null)
            {
                continue;
            }

            // Skip QueryAuth token parameter — it's not an input field
            if (
                queryAuthParameterName is not null
                && param.In is ParameterLocation.Query
                && string.Equals(
                    param.Name,
                    queryAuthParameterName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            // Accept and Authorization are not legal OpenAPI header params. Keep their
            // existing loud fallback until their semantics have a dedicated source diagnosis.
            if (param.In is ParameterLocation.Header && IsReservedHeaderName(param.Name))
            {
                unsupported.Add(
                    $"param name={param.Name} in=header reason=reserved-header-dropped"
                );
                continue;
            }

            var csharpType = mapper.ResolveCSharpType(
                param.Schema,
                $"{fieldName}{Naming.ToPascalCaseFromSegments(param.Name)}"
            );
            if (!param.Required && !csharpType.EndsWith("?"))
            {
                csharpType += "?";
            }

            var paramPropertyName = Naming.ToPascalCaseFromSegments(param.Name);
            if (Naming.IsReservedRecordMemberName(paramPropertyName))
            {
                // Params bind by member name (not the serializer), so the rename
                // shifts the spec-visible name — loud marker, not a silent fix.
                paramPropertyName += "Value";
                unsupported.Add(
                    $"param name={param.Name} in={location} reason=reserved-member-renamed"
                );
            }

            // FABLE_ROUNDTRIP #1, the query half: pin the wire name whenever the
            // emitted name (camelCase of the property) differs from the original
            // — `per_page` no longer drifts to `perPage` (263 github query
            // params). Headers carry their original name via [RivetHeader]
            // instead; path params match route tokens by NAME (normalized), and
            // a pin equal to the token is inert by the A14 rule.
            var wireName =
                param.In is ParameterLocation.Header
                || string.Equals(
                    Naming.ToCamelCase(paramPropertyName),
                    param.Name,
                    StringComparison.Ordinal
                )
                    ? null
                    : param.Name;

            properties.Add(
                new ParamProperty(
                    new RecordProperty(
                        paramPropertyName,
                        csharpType,
                        param.Required,
                        HeaderName: param.In is ParameterLocation.Header ? param.Name : null,
                        WireName: wireName
                    ),
                    param.Name,
                    location
                )
            );
        }

        return properties;
    }

    private static string? SynthesizeParamInputType(
        List<ParamProperty> paramProperties,
        SchemaMapper mapper,
        string fieldName
    )
    {
        return paramProperties.Count == 0
            ? null
            : SynthesizeInputRecord(
                paramProperties.Select(p => p.Property).ToList(),
                mapper,
                fieldName
            );
    }

    private static string? SynthesizeInputRecord(
        List<RecordProperty> properties,
        SchemaMapper mapper,
        string fieldName
    )
    {
        if (properties.Count == 0)
        {
            return null;
        }

        // Segment-pascalize: a synthesized component name containing underscores would be
        // segment-split on the next import, mutating the name every emit∘import loop.
        var recordName = $"{Naming.ToPascalCaseFromSegments(fieldName)}Input";
        var deduped = SchemaClassifier.DeduplicateProperties(properties);

        // Reuse a components/schemas record only when its SHAPE matches the synthesized input —
        // name-only reuse silently hands the endpoint someone else's type (I3 residual).
        if (mapper.HasMappedSchemaWithShape(recordName, deduped))
        {
            return recordName;
        }

        // GAP-2 (emit∘import idempotency): a previous import loop may already have
        // disambiguated this synthesized input to a numbered variant (e.g. StreamInput2).
        // Reuse the identically-shaped numbered component instead of minting a fresh
        // suffix (StreamInput3, StreamInput4, …) on every loop.
        var numberedVariant = mapper.FindNumberedSchemaWithShape(recordName, deduped);
        if (numberedVariant is not null)
        {
            return numberedVariant;
        }

        // P2 wave 5: [RivetHeader] properties are never part of a JSON schema, so a
        // component emitted on a previous loop carries only the NON-header subset.
        // Re-attaching the header properties to that component (instead of minting a
        // numbered variant per loop) keeps emit∘import a fixed point for header-bearing
        // inputs — same GAP-2/I3-residual reasoning as the numbered-variant reuse above.
        var augmented = mapper.AugmentComponentWithHeaderShape(recordName, deduped);
        if (augmented is not null)
        {
            return augmented;
        }

        // Dedup-with-shape-check (I3): a same-named synthetic input with a different shape
        // (e.g. two tags both synthesizing GetByIdInput, or a name-only collision with a
        // component schema) gets a disambiguated name.
        return mapper.AddExtraRecord(new GeneratedRecord(recordName, deduped));
    }

    /// <summary>
    /// OpenAPI 3.x rule (kept in sync with OpenApiEmitter.IsReservedHeaderName):
    /// Accept, Content-Type and Authorization must not be declared as header parameters.
    /// </summary>
    private static bool IsReservedHeaderName(string name) =>
        name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase);

    private static string? BuildParameterMetadataJson(
        IOpenApiParameter parameter,
        IOpenApiSchema schema,
        bool includeSchemaMetadata
    )
    {
        var metadata = new JsonObject();
        if (!string.IsNullOrEmpty(parameter.Description))
        {
            metadata["description"] = parameter.Description;
        }
        if (parameter.Deprecated)
        {
            metadata["deprecated"] = true;
        }
        if (includeSchemaMetadata && schema.Default is not null)
        {
            metadata["default"] = JsonNode.Parse(schema.Default.ToJsonString());
        }
        if (
            includeSchemaMetadata && RecordSynthesizer.ExtractConstraints(schema) is { } constraints
        )
        {
            metadata["constraints"] = JsonSerializer.SerializeToNode(
                constraints,
                new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                }
            );
        }
        if (includeSchemaMetadata && BuildSchemaExamplesNode(schema) is { } schemaExamples)
        {
            metadata["schemaExamples"] = schemaExamples;
        }
        if (includeSchemaMetadata && schema.Items is not null)
        {
            var itemMetadata = SchemaMapper.BuildScalarMetadata(
                schema.Items,
                schema.Items.Type?.HasFlag(JsonSchemaType.Null) == true
            );
            metadata["itemMetadata"] = JsonSerializer.SerializeToNode(itemMetadata);
        }
        if (parameter.Example is not null)
        {
            metadata["example"] = OpenApiJsonNodeSerializer.Clone(parameter.Example);
        }
        if (BuildExamplesNode(parameter.Examples) is { } examples)
        {
            metadata["examples"] = examples;
        }
        if (parameter.Style is { } style && style != DefaultParameterStyle(parameter.In))
        {
            metadata["style"] = style switch
            {
                ParameterStyle.SpaceDelimited => "spaceDelimited",
                ParameterStyle.PipeDelimited => "pipeDelimited",
                ParameterStyle.DeepObject => "deepObject",
                _ => ParameterStyleName(style),
            };
        }
        if (parameter.Explode is { } explode && explode != (parameter.Style is ParameterStyle.Form))
        {
            metadata["explode"] = explode;
        }

        return metadata.Count == 0 ? null : metadata.ToJsonString();
    }

    private static ParameterStyle DefaultParameterStyle(ParameterLocation? location) =>
        location is ParameterLocation.Query or ParameterLocation.Cookie
            ? ParameterStyle.Form
            : ParameterStyle.Simple;

    private static string ParameterStyleName(ParameterStyle style) =>
        style switch
        {
            ParameterStyle.SpaceDelimited => "spaceDelimited",
            ParameterStyle.PipeDelimited => "pipeDelimited",
            ParameterStyle.DeepObject => "deepObject",
            _ => style.ToString().ToLowerInvariant(),
        };

    private static JsonArray? BuildSchemaExamplesNode(IOpenApiSchema? schema)
    {
        if (schema?.Examples is { Count: > 0 })
        {
            return new JsonArray(
                schema
                    .Examples.Select(example =>
                        example is null ? null : OpenApiJsonNodeSerializer.Clone(example)
                    )
                    .ToArray()
            );
        }

        return schema?.Example is null
            ? null
            : new JsonArray(OpenApiJsonNodeSerializer.Clone(schema.Example));
    }

    private static JsonObject? BuildExamplesNode(
        IDictionary<string, IOpenApiExample>? sourceExamples
    )
    {
        if (sourceExamples is not { Count: > 0 })
        {
            return null;
        }

        var examples = new JsonObject();
        foreach (var (name, example) in sourceExamples)
        {
            if (example is not OpenApiExample concrete)
            {
                continue;
            }

            var value = new JsonObject();
            if (concrete.Summary is not null)
            {
                value["summary"] = concrete.Summary;
            }
            if (concrete.Description is not null)
            {
                value["description"] = concrete.Description;
            }
            if (concrete.Value is not null)
            {
                value["value"] = OpenApiJsonNodeSerializer.Clone(concrete.Value);
            }
            if (concrete.ExternalValue is not null)
            {
                value["externalValue"] = concrete.ExternalValue.ToString();
            }
            examples[name] = value;
        }

        return examples.Count == 0 ? null : examples;
    }

    private static readonly HashSet<string> _binaryContentTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "application/octet-stream",
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "audio/mpeg",
        "video/mp4",
    };

    private static bool IsBinaryContentType(string contentType) =>
        _binaryContentTypes.Contains(contentType)
        || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static (
        string? OutputType,
        int? SuccessStatus,
        string? FileContentType,
        string? ResponseContentType
    ) ResolveOutputType(OpenApiOperation operation, SchemaMapper mapper, string fieldName)
    {
        if (operation.Responses is null)
        {
            return (null, null, null, null);
        }

        string? outputType = null;
        int? successCode = null;
        string? fileContentType = null;
        string? responseContentType = null;

        // A runtime success branch needs a concrete status. Wildcard response keys remain
        // exact secondary metadata and suppress the authored method default.
        var successResponses = new List<(int Code, IOpenApiResponse Response)>();

        foreach (var (statusStr, response) in operation.Responses)
        {
            if (statusStr is "2XX" or "2xx")
            {
                continue;
            }

            if (int.TryParse(statusStr, out var parsed) && parsed is >= 200 and < 300)
            {
                successResponses.Add((parsed, response));
            }
        }

        foreach (var (code, response) in successResponses)
        {
            if (successCode.HasValue && code >= successCode.Value)
            {
                continue;
            }

            successCode = code;

            // I7: a lower 2xx supersedes everything a higher one resolved — including a
            // binary branch's fileContentType, which previously leaked through and produced
            // a typed JSON output AND ProducesFile on the same endpoint.
            outputType = null;
            fileContentType = null;
            responseContentType = null;

            if (response.Content is { Count: > 0 })
            {
                if (
                    TryGetSchemaForContentType(response.Content, "application/json", out var schema)
                    || TryGetSchemaForContentType(response.Content, "*/*", out schema)
                )
                {
                    outputType = mapper.ResolveCSharpType(schema!, $"{fieldName}Response");
                }
                else
                {
                    // Check for binary/file content types
                    var binaryType = response.Content.Keys.FirstOrDefault(IsBinaryContentType);
                    if (binaryType is not null)
                    {
                        fileContentType = binaryType;
                        outputType = null;
                    }
                    else
                    {
                        // Try any text/* content type with a schema
                        var textType = response.Content.Keys.FirstOrDefault(k =>
                            k.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                        );
                        if (
                            textType is not null
                            && TryGetSchemaForContentType(response.Content, textType, out schema)
                        )
                        {
                            // FABLE_ROUNDTRIP #10: keep the text/* media type via
                            // .ProducesContentType(...) — re-emitting it as
                            // application/json was a silent wire change.
                            outputType = mapper.ResolveCSharpType(schema!, $"{fieldName}Response");
                            responseContentType = textType;
                        }
                        else
                        {
                            // The complete response content map carries this schema even
                            // when no media type can become the primary runtime return type.
                            outputType = null;
                        }
                    }
                }
            }
            else
            {
                outputType = null;
            }
        }

        // FABLE_ROUNDTRIP #8 + cross-corpus #3: operations that declare no 2xx at
        // all. The lowest concrete non-error status — 1xx informational (websocket
        // upgrades declare only 101) or 3xx redirect — becomes the declared
        // (bodyless) success status; without one the walker defaults a 200 the API
        // never returns.
        if (successCode is null && operation.Responses is not null)
        {
            successCode = operation
                .Responses.Keys.Select(status => int.TryParse(status, out var parsed) ? parsed : -1)
                .Where(parsed => parsed is (>= 100 and < 200) or (>= 300 and < 400))
                .OrderBy(parsed => parsed)
                .Cast<int?>()
                .FirstOrDefault();
        }

        return (outputType, successCode, fileContentType, responseContentType);
    }

    private static IReadOnlyList<GeneratedErrorResponse> ResolveErrorResponses(
        OpenApiOperation operation,
        SchemaMapper mapper,
        string fieldName,
        int? successStatus = null
    )
    {
        if (operation.Responses is null)
        {
            return [];
        }

        var errors = new List<GeneratedErrorResponse>();

        foreach (var (statusStr, response) in operation.Responses)
        {
            var code = int.TryParse(statusStr, out var parsed) ? parsed : 0;

            if (code != 0 && code == successStatus)
            {
                continue;
            }

            if (response.Content is { Count: > 0 })
            {
                if (
                    TryGetSchemaForContentType(response.Content, "application/json", out var schema)
                    || TryGetSchemaForContentType(response.Content, "*/*", out schema)
                )
                {
                    var suffix =
                        code == 0 ? Naming.ToPascalCaseFromSegments(statusStr) : code.ToString();
                    var typeName = mapper.ResolveCSharpType(
                        schema!,
                        $"{fieldName}Response{suffix}"
                    );
                    var description = response.Description;

                    if (
                        !errors.Any(e =>
                            e.StatusKey.Equals(statusStr, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        errors.Add(
                            new GeneratedErrorResponse(code, statusStr, typeName, description)
                        );
                    }
                }
                else
                {
                    if (
                        !errors.Any(e =>
                            e.StatusKey.Equals(statusStr, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        errors.Add(
                            new GeneratedErrorResponse(code, statusStr, null, response.Description)
                        );
                    }

                    // ResolveResponseContents persists every status/media/schema tuple.
                    // The untyped error declaration only supplies the status and
                    // description when the media type is not the primary JSON shape.
                }
            }
            else if (
                !errors.Any(e => e.StatusKey.Equals(statusStr, StringComparison.OrdinalIgnoreCase))
            )
            {
                errors.Add(new GeneratedErrorResponse(code, statusStr, null, response.Description));
            }
        }

        return errors;
    }

    private static IReadOnlyList<TsEndpointExample> ResolveRequestExamples(
        OpenApiOperation operation,
        List<string> unsupported,
        IDictionary<string, IOpenApiExample>? componentExamples
    )
    {
        if (operation.RequestBody?.Content is not { Count: > 0 } content)
        {
            return [];
        }

        return ResolveMediaExamples(content, unsupported, "request-example", componentExamples);
    }

    private static IReadOnlyList<GeneratedEndpointResponseExample> ResolveResponseExamples(
        OpenApiOperation operation,
        List<string> unsupported,
        IDictionary<string, IOpenApiExample>? componentExamples
    )
    {
        if (operation.Responses is null)
        {
            return [];
        }

        var responseExamples = new List<GeneratedEndpointResponseExample>();

        foreach (var (statusStr, response) in operation.Responses)
        {
            var statusCode = int.TryParse(statusStr, out var parsed) ? parsed : 0;
            if (response.Content is not { Count: > 0 } content)
            {
                continue;
            }

            foreach (
                var example in ResolveMediaExamples(
                    content,
                    unsupported,
                    $"response-example status={statusStr}",
                    componentExamples
                )
            )
            {
                responseExamples.Add(
                    new GeneratedEndpointResponseExample(statusCode, statusStr, example)
                );
            }
        }

        return responseExamples;
    }

    private static IReadOnlyList<TsEndpointExample> ResolveMediaExamples(
        IDictionary<string, OpenApiMediaType> content,
        List<string> unsupported,
        string markerPrefix,
        IDictionary<string, IOpenApiExample>? componentExamples = null
    )
    {
        var examples = new List<TsEndpointExample>();

        foreach (var (mediaType, media) in content)
        {
            if (media.Example is not null)
            {
                var resolved = PreserveEmbeddedExampleRefs(
                    OpenApiJsonNodeSerializer.Serialize(media.Example),
                    componentExamples,
                    unsupported,
                    markerPrefix,
                    mediaType,
                    null
                );
                examples.Add(
                    new TsEndpointExample(
                        mediaType,
                        Json: resolved.Json,
                        ReferencedComponents: resolved.ReferencedComponents
                    )
                );
            }

            if (media.Examples is null)
            {
                continue;
            }

            foreach (var (name, example) in media.Examples)
            {
                var endpointExample = ResolveExample(
                    mediaType,
                    name,
                    example,
                    componentExamples,
                    unsupported,
                    markerPrefix,
                    out var reason
                );
                if (endpointExample is not null)
                {
                    examples.Add(endpointExample);
                    continue;
                }

                if (reason is not null)
                {
                    unsupported.Add(
                        BuildExampleUnsupportedMarker(
                            markerPrefix,
                            mediaType,
                            name,
                            TryGetComponentExampleId(example),
                            reason
                        )
                    );
                }
            }
        }

        return examples;
    }

    private static TsEndpointExample? ResolveExample(
        string mediaType,
        string? name,
        IOpenApiExample example,
        IDictionary<string, IOpenApiExample>? componentExamples,
        List<string> unsupported,
        string markerPrefix,
        out string? reason
    )
    {
        var componentExampleId = TryGetComponentExampleId(example);
        var resolvedJson = TryGetExampleJson(example);
        IReadOnlyDictionary<string, string>? referencedComponents = null;
        if (resolvedJson is not null)
        {
            var resolved = PreserveEmbeddedExampleRefs(
                resolvedJson,
                componentExamples,
                unsupported,
                markerPrefix,
                mediaType,
                name
            );
            resolvedJson = resolved.Json;
            referencedComponents = resolved.ReferencedComponents;
        }

        if (componentExampleId is not null)
        {
            reason = resolvedJson is null ? "unresolved-ref" : null;
            return resolvedJson is not null
                ? new TsEndpointExample(
                    mediaType,
                    name,
                    ComponentExampleId: componentExampleId,
                    ResolvedJson: resolvedJson,
                    ReferencedComponents: referencedComponents
                )
                : null;
        }

        reason = resolvedJson is null
            ? example.ExternalValue is not null
                ? "external-value"
                : "missing-value"
            : null;
        return resolvedJson is not null
            ? new TsEndpointExample(
                mediaType,
                name,
                Json: resolvedJson,
                ReferencedComponents: referencedComponents
            )
            : null;
    }

    /// <summary>
    /// github anti-pattern: example VALUES containing
    /// {"$ref": "#/components/examples/X"}. In the source document X exists;
    /// after a round-trip only the examples attached to surviving operations
    /// are re-registered, so the embedded ref dangles and downstream
    /// generators (openapi-typescript) hard-fail on it. Inline the referenced
    /// value at import time, while the source components are still in hand.
    /// Unresolvable or cyclic refs degrade LOUDLY to null + marker.
    /// </summary>
    private static (
        string Json,
        IReadOnlyDictionary<string, string>? ReferencedComponents
    ) PreserveEmbeddedExampleRefs(
        string json,
        IDictionary<string, IOpenApiExample>? componentExamples,
        List<string> unsupported,
        string markerPrefix,
        string mediaType,
        string? name
    )
    {
        if (!json.Contains("#/components/examples/", StringComparison.Ordinal))
        {
            return (json, null);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return (json, null);
        }

        var referenced = new Dictionary<string, string>(StringComparer.Ordinal);
        if (
            CollectReferencedExampleComponents(
                parsed,
                componentExamples,
                referenced,
                new HashSet<string>(StringComparer.Ordinal)
            )
        )
        {
            return (parsed?.ToJsonString() ?? "null", referenced);
        }

        return (
            InlineEmbeddedExampleRefs(
                json,
                componentExamples,
                unsupported,
                markerPrefix,
                mediaType,
                name
            ),
            null
        );
    }

    private static bool CollectReferencedExampleComponents(
        JsonNode? node,
        IDictionary<string, IOpenApiExample>? componentExamples,
        Dictionary<string, string> referenced,
        HashSet<string> resolving
    )
    {
        const string examplesPrefix = "#/components/examples/";
        switch (node)
        {
            case JsonObject obj:
                if (
                    obj["$ref"]?.GetValue<string>() is { } reference
                    && reference.StartsWith(examplesPrefix, StringComparison.Ordinal)
                )
                {
                    var componentName = reference[examplesPrefix.Length..];
                    if (
                        componentExamples is null
                        || !componentExamples.TryGetValue(componentName, out var component)
                        || !resolving.Add(componentName)
                        || TryGetExampleJson(component) is not { } componentJson
                    )
                    {
                        resolving.Remove(componentName);
                        return false;
                    }

                    referenced.TryAdd(componentName, componentJson);
                    JsonNode? componentNode;
                    try
                    {
                        componentNode = JsonNode.Parse(componentJson);
                    }
                    catch (JsonException)
                    {
                        resolving.Remove(componentName);
                        return false;
                    }
                    var resolved = CollectReferencedExampleComponents(
                        componentNode,
                        componentExamples,
                        referenced,
                        resolving
                    );
                    resolving.Remove(componentName);
                    return resolved;
                }

                return obj.All(property =>
                    CollectReferencedExampleComponents(
                        property.Value,
                        componentExamples,
                        referenced,
                        resolving
                    )
                );

            case JsonArray array:
                return array.All(item =>
                    CollectReferencedExampleComponents(
                        item,
                        componentExamples,
                        referenced,
                        resolving
                    )
                );

            default:
                return true;
        }
    }

    private static string InlineEmbeddedExampleRefs(
        string json,
        IDictionary<string, IOpenApiExample>? componentExamples,
        List<string> unsupported,
        string markerPrefix,
        string mediaType,
        string? name
    )
    {
        if (!json.Contains("#/components/examples/", StringComparison.Ordinal))
        {
            return json;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }

        var changed = false;
        var inlined = InlineExampleRefNode(
            parsed,
            componentExamples,
            new HashSet<string>(StringComparer.Ordinal),
            () => changed = true,
            unresolved =>
                unsupported.Add(
                    BuildExampleUnsupportedMarker(
                        markerPrefix,
                        mediaType,
                        name,
                        unresolved,
                        "unresolvable-embedded-example-ref"
                    )
                )
        );

        return changed ? inlined?.ToJsonString() ?? "null" : json;
    }

    private static JsonNode? InlineExampleRefNode(
        JsonNode? node,
        IDictionary<string, IOpenApiExample>? componentExamples,
        HashSet<string> resolving,
        Action markChanged,
        Action<string> markUnresolved
    )
    {
        const string examplesPrefix = "#/components/examples/";

        switch (node)
        {
            case JsonObject obj:
                if (
                    obj.TryGetPropertyValue("$ref", out var refNode)
                    && refNode is JsonValue refValue
                    && refValue.TryGetValue<string>(out var reference)
                    && reference.StartsWith(examplesPrefix, StringComparison.Ordinal)
                )
                {
                    markChanged();
                    var componentName = reference[examplesPrefix.Length..];

                    if (
                        componentExamples is not null
                        && componentExamples.TryGetValue(componentName, out var component)
                        && resolving.Add(componentName)
                        && TryGetExampleJson(component) is { } componentJson
                    )
                    {
                        var componentNode = JsonNode.Parse(componentJson);
                        var resolved = InlineExampleRefNode(
                            componentNode,
                            componentExamples,
                            resolving,
                            markChanged,
                            markUnresolved
                        );
                        resolving.Remove(componentName);
                        return resolved;
                    }

                    resolving.Remove(componentName);
                    markUnresolved(componentName);
                    return null;
                }

                foreach (var key in obj.Select(property => property.Key).ToList())
                {
                    var child = obj[key];
                    var replaced = InlineExampleRefNode(
                        child,
                        componentExamples,
                        resolving,
                        markChanged,
                        markUnresolved
                    );
                    if (!ReferenceEquals(child, replaced))
                    {
                        obj[key] = replaced;
                    }
                }

                return obj;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var child = array[index];
                    var replaced = InlineExampleRefNode(
                        child,
                        componentExamples,
                        resolving,
                        markChanged,
                        markUnresolved
                    );
                    if (!ReferenceEquals(child, replaced))
                    {
                        array[index] = replaced;
                    }
                }

                return array;

            default:
                return node;
        }
    }

    private static string BuildExampleUnsupportedMarker(
        string markerPrefix,
        string mediaType,
        string? name,
        string? componentExampleId,
        string reason
    )
    {
        var parts = new List<string> { markerPrefix, $"media-type={mediaType}" };

        if (name is not null)
        {
            parts.Add($"name={name}");
        }

        if (componentExampleId is not null)
        {
            parts.Add($"component-example-id={componentExampleId}");
        }

        parts.Add($"reason={reason}");
        return string.Join(" ", parts);
    }

    private static string? TryGetComponentExampleId(IOpenApiExample example)
    {
        return example switch
        {
            OpenApiExampleReference exampleReference => exampleReference.Reference?.Id,
            _ => null,
        };
    }

    private static string? TryGetExampleJson(IOpenApiExample example)
    {
        if (example.Value is not null)
        {
            return OpenApiJsonNodeSerializer.Serialize(example.Value);
        }

        return example switch
        {
            OpenApiExampleReference { RecursiveTarget.Value: not null } exampleReference =>
                OpenApiJsonNodeSerializer.Serialize(exampleReference.RecursiveTarget.Value),
            OpenApiExampleReference { Target.Value: not null } exampleReference =>
                OpenApiJsonNodeSerializer.Serialize(exampleReference.Target.Value),
            _ => null,
        };
    }

    private static bool TryGetSchemaForContentType(
        IDictionary<string, OpenApiMediaType> content,
        string contentType,
        out IOpenApiSchema? schema
    )
    {
        if (content.TryGetValue(contentType, out var mediaType) && mediaType.Schema is not null)
        {
            schema = mediaType.Schema;
            return true;
        }

        schema = null;
        return false;
    }

    private static (
        bool IsAnonymous,
        string? Scheme,
        SecurityRequirements? Requirements
    ) ResolveSecurity(OpenApiOperation operation, string? globalSecurityScheme)
    {
        if (operation.Security is null)
        {
            // No operation-level security — use global default
            return globalSecurityScheme is not null
                ? (false, globalSecurityScheme, null)
                : (false, null, null);
        }

        // Empty list → anonymous
        if (operation.Security.Count == 0)
        {
            return (false, null, new SecurityRequirements([]));
        }

        var requirements = new List<SecurityRequirement>();
        foreach (var requirement in operation.Security)
        {
            var schemes = new List<SecurityRequirementScheme>();
            foreach (var (scheme, scopes) in requirement)
            {
                var name = scheme.Reference?.Id;
                if (name is null)
                {
                    continue;
                }

                schemes.Add(new SecurityRequirementScheme(name, scopes.ToList()));
            }

            requirements.Add(new SecurityRequirement(schemes));
        }

        return (false, null, new SecurityRequirements(requirements));
    }

    private static IReadOnlyList<GeneratedEndpointField> DeduplicateFields(
        List<GeneratedEndpointField> fields
    )
    {
        var seen = new Dictionary<string, int>();
        var result = new List<GeneratedEndpointField>(fields.Count);

        foreach (var field in fields)
        {
            var name = field.FieldName;
            if (seen.TryGetValue(name, out var count))
            {
                count++;
                seen[name] = count;
                var deduped = $"{name}_{count}";
                result.Add(field with { FieldName = deduped });
            }
            else
            {
                seen[name] = 1;
                result.Add(field);
            }
        }

        return result;
    }

    private static string DeriveFieldName(
        string? operationId,
        string httpMethod,
        string route,
        string tag
    )
    {
        if (operationId is not null)
        {
            return StripTagPrefix(operationId, tag);
        }

        var segments = route
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s =>
                s.StartsWith('{') && s.EndsWith('}')
                    ? "By" + Naming.ToPascalCaseFromSegments(s[1..^1])
                    : Naming.ToPascalCaseFromSegments(s)
            );

        return Naming.ToPascalCaseFromSegments(httpMethod) + string.Concat(segments);
    }

    private static string StripTagPrefix(string operationId, string tag)
    {
        var prefixes = new[]
        {
            tag.ToLowerInvariant() + "_",
            tag.ToLowerInvariant() + "-",
            tag + "_",
            tag + "-",
        };

        foreach (var prefix in prefixes)
        {
            if (operationId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = operationId[prefix.Length..];
                return Naming.ToPascalCaseFromSegments(stripped);
            }
        }

        return Naming.ToPascalCaseFromSegments(operationId);
    }

    private static string? ResolveQueryAuth(OpenApiOperation operation)
    {
        if (
            operation.Extensions is null
            || !operation.Extensions.TryGetValue("x-rivet-query-auth", out var ext)
        )
        {
            return null;
        }

        if (
            ext is JsonNodeExtension { Node: JsonObject obj }
            && obj.TryGetPropertyValue("parameterName", out var nameNode)
        )
        {
            return nameNode?.GetValue<string>();
        }

        return null;
    }

    private static string? GetOperationExtensionString(OpenApiOperation operation, string key)
    {
        if (operation.Extensions is null || !operation.Extensions.TryGetValue(key, out var ext))
        {
            return null;
        }

        if (ext is JsonNodeExtension jsonExt)
        {
            return jsonExt.Node?.GetValue<string>();
        }

        return null;
    }

    private static string? GetExtensionString(IOpenApiSchema schema, string key)
    {
        if (schema.Extensions is null || !schema.Extensions.TryGetValue(key, out var ext))
        {
            return null;
        }

        if (ext is JsonNodeExtension jsonExt)
        {
            return jsonExt.Node?.GetValue<string>();
        }

        return null;
    }

    private static string? ExtractTag(OpenApiOperation operation)
    {
        if (operation.Tags is null || operation.Tags.Count == 0)
        {
            return null;
        }

        var firstTag = operation.Tags.FirstOrDefault();
        return firstTag?.Name is not null ? Naming.ToPascalCaseFromSegments(firstTag.Name) : null;
    }
}
