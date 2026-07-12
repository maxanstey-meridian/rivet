using System.Text.Json.Nodes;
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
        HttpMethod.Post,
        HttpMethod.Put,
        HttpMethod.Patch,
        HttpMethod.Delete,
    ];

    /// <summary>
    /// Group operations by tag, return one contract per tag.
    /// </summary>
    public static IReadOnlyList<GeneratedContract> BuildContracts(
        OpenApiPaths paths,
        SchemaMapper mapper,
        string? globalSecurityScheme,
        List<string> warnings,
        IDictionary<string, IOpenApiExample>? componentExamples = null
    )
    {
        var groups = new Dictionary<string, List<GeneratedEndpointField>>();

        foreach (var (route, pathItem) in paths)
        {
            foreach (var (method, operation) in pathItem.Operations ?? [])
            {
                if (!_supportedMethods.Contains(method))
                {
                    // I15: HEAD/OPTIONS/TRACE used to be skipped with zero diagnostics —
                    // "nothing is dropped silently" demands a named warning per dropped op.
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
                    mapper
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
        SchemaMapper mapper
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
        var requestContents = ResolveRequestContents(operation, mapper, fieldName);
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
            queryAuthParameterName
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
            fieldName,
            unsupported
        );
        var responseContents = ResolveResponseContents(operation, mapper, fieldName);

        // File endpoint: binary content type on a GET endpoint → Define.File()
        // Non-GET binary endpoints (e.g. POST → PDF) keep Define.{Method}().ProducesFile()
        var isFileEndpoint =
            fileContentType is not null
            && httpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);

        // Error responses
        var errorResponses = ResolveErrorResponses(
            operation,
            mapper,
            fieldName,
            unsupported,
            successStatus
        );
        var requestExamples = ResolveRequestExamples(operation, unsupported, componentExamples);
        var responseExamples = ResolveResponseExamples(operation, unsupported, componentExamples);
        errorResponses = EnsureExampleStatusesAreDeclared(
            operation,
            successStatus,
            errorResponses,
            responseExamples
        );

        // P2 wave 5: response headers re-emit as .WithResponseHeader(...) chain calls —
        // resolved AFTER the declared-status set is final (success + error responses).
        var responseHeaders = ResolveResponseHeaders(
            operation,
            successStatus,
            errorResponses,
            unsupported
        );

        // Security
        var (isAnonymous, securityScheme, securityRequirementsJson) = ResolveSecurity(
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
            securityRequirementsJson,
            requestContents,
            responseContents,
            explicitParameters
        );
    }

    private static IReadOnlyList<IOpenApiParameter> MergeParameters(
        IList<IOpenApiParameter>? pathParameters,
        IList<IOpenApiParameter>? operationParameters
    )
    {
        var merged = new Dictionary<(string Name, ParameterLocation? Location), IOpenApiParameter>();
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
                    && location.ToString().Equals(
                        parameter.Location,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
                var sourceSchema = sourceParameter.Schema!;
                var typeName = mapper.ResolveCSharpType(
                    sourceSchema,
                    $"{fieldName}{Naming.ToPascalCaseFromSegments(parameter.OriginalName)}Parameter"
                );
                var format = mapper.ResolveFormat(sourceSchema);
                return new GeneratedEndpointParameter(
                    parameter.OriginalName,
                    parameter.Location,
                    typeName,
                    parameter.Property.IsRequired,
                    mapper.ResolveSchemaType(sourceSchema),
                    format,
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
                                or "decimal"
                );
            })
            .ToList();

    private static IReadOnlyList<GeneratedMediaTypeContent> ResolveRequestContents(
        OpenApiOperation operation,
        SchemaMapper mapper,
        string fieldName
    )
    {
        if (operation.RequestBody?.Content is not { Count: > 0 } content)
        {
            return [];
        }

        var result = new List<GeneratedMediaTypeContent>();
        var index = 0;
        foreach (var (mediaType, media) in content)
        {
            if (media.Schema is null)
            {
                continue;
            }
            if (
                IsRawBinarySchema(media.Schema)
                && !mediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var typeName = mapper.ResolveCSharpType(
                media.Schema,
                $"{Naming.ToPascalCaseFromSegments(fieldName)}RequestContent{index++}"
            );
            result.Add(new GeneratedMediaTypeContent(mediaType, typeName));
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
            var statusCode = NormalizeStatusCode(status);
            if (statusCode is null || response.Content is not { Count: > 0 } content)
            {
                continue;
            }

            foreach (var (mediaType, media) in content)
            {
                if (media.Schema is null)
                {
                    continue;
                }
                if (IsRawBinarySchema(media.Schema))
                {
                    continue;
                }

                var typeName = mapper.ResolveCSharpType(
                    media.Schema,
                    $"{Naming.ToPascalCaseFromSegments(fieldName)}Response{statusCode}Content{index++}"
                );
                result.Add(
                    new GeneratedResponseMediaTypeContent(statusCode.Value, mediaType, typeName)
                );
            }
        }

        return result;
    }

    /// <summary>
    /// P2 wave 5: response headers (previously out-of-scope) become .WithResponseHeader()
    /// calls — name, description and required survive; the schema is string-typed in v1,
    /// so a non-string header schema degrades LOUDLY. Headers on a status the contract
    /// cannot declare (e.g. a second 2xx) are dropped LOUDLY.
    /// </summary>
    private static IReadOnlyList<GeneratedResponseHeader> ResolveResponseHeaders(
        OpenApiOperation operation,
        int? successStatus,
        IReadOnlyList<GeneratedErrorResponse> errorResponses,
        List<string> unsupported
    )
    {
        if (operation.Responses is null)
        {
            return [];
        }

        var declaredStatuses = errorResponses.Select(error => error.StatusCode).ToHashSet();
        if (successStatus is not null)
        {
            declaredStatuses.Add(successStatus.Value);
        }

        var headers = new List<GeneratedResponseHeader>();

        foreach (var (statusStr, response) in operation.Responses)
        {
            if (
                response.Headers is not { Count: > 0 }
                || NormalizeStatusCode(statusStr) is not { } statusCode
            )
            {
                continue;
            }

            foreach (var (name, header) in response.Headers)
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!declaredStatuses.Contains(statusCode))
                {
                    unsupported.Add(
                        $"header name={name} status={statusCode} reason=undeclared-status"
                    );
                    continue;
                }

                if (
                    header.Schema is { } schema
                    && schema.Type.HasValue
                    && !schema.Type.Value.HasFlag(JsonSchemaType.String)
                )
                {
                    unsupported.Add(
                        $"header name={name} status={statusCode} reason=schema-degraded-to-string"
                    );
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

                headers.Add(
                    new GeneratedResponseHeader(
                        statusCode,
                        name,
                        string.IsNullOrEmpty(header.Description) ? null : header.Description,
                        header.Required
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

        // Request body exists but uses unsupported content type(s)
        unsupported.Add($"body {DescribeUnsupportedContent(content)}");
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
        string? queryAuthParameterName = null
    )
    {
        if (sourceParameters.Count == 0)
        {
            return [];
        }

        var properties = new List<ParamProperty>();
        var metadataDropped = new List<string>();

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

            // Empty names occur in the wild (Notion declares a nameless header param) —
            // they cannot become a C# property.
            if (param.Schema is null || string.IsNullOrEmpty(param.Name))
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

            var location = param.In switch
            {
                ParameterLocation.Path => "path",
                ParameterLocation.Query => "query",
                ParameterLocation.Header => "header",
                _ => "cookie",
            };

            // Accept/Content-Type/Authorization are not legal OpenAPI header params —
            // the emitter would refuse to re-emit them (RIV2009), so importing them
            // would break the round-trip promise. Dropped loudly instead (mirrors
            // OpenApiEmitter.IsReservedHeaderName).
            if (param.In is ParameterLocation.Header && IsReservedHeaderName(param.Name))
            {
                unsupported.Add(
                    $"param name={param.Name} in=header reason=reserved-header-dropped"
                );
                continue;
            }

            // I13: description/deprecated/constraints don't survive into the synthesized
            // input record — aggregated marker below names the affected params.
            if (
                !string.IsNullOrEmpty(param.Description)
                || param.Deprecated
                || HasParamConstraints(param.Schema)
            )
            {
                metadataDropped.Add(param.Name);
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

        if (metadataDropped.Count > 0)
        {
            unsupported.Add(
                $"param-metadata params={string.Join(", ", metadataDropped)} reason=metadata-dropped"
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

    /// <summary>
    /// I13: does the parameter schema carry validation constraints that the synthesized
    /// input record drops?
    /// </summary>
    private static bool HasParamConstraints(IOpenApiSchema schema) =>
        schema.MinLength.HasValue
        || schema.MaxLength.HasValue
        || schema.Pattern is not null
        || schema.Minimum is not null
        || schema.Maximum is not null
        || schema.ExclusiveMinimum is not null
        || schema.ExclusiveMaximum is not null
        || schema.MultipleOf is not null
        || schema.MinItems.HasValue
        || schema.MaxItems.HasValue
        || schema.UniqueItems == true;

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
    ) ResolveOutputType(
        OpenApiOperation operation,
        SchemaMapper mapper,
        string fieldName,
        List<string> unsupported
    )
    {
        if (operation.Responses is null)
        {
            return (null, null, null, null);
        }

        string? outputType = null;
        int? successCode = null;
        string? fileContentType = null;
        string? responseContentType = null;

        // Collect concrete 2xx responses; a "2XX" range wildcard (I9) maps to 200 but only
        // when no concrete 2xx status is declared — concrete statuses always win.
        var successResponses = new List<(int Code, IOpenApiResponse Response)>();
        (int Code, IOpenApiResponse Response)? rangeWildcard = null;

        foreach (var (statusStr, response) in operation.Responses)
        {
            if (statusStr is "2XX" or "2xx")
            {
                rangeWildcard ??= (200, response);
                continue;
            }

            if (int.TryParse(statusStr, out var parsed) && parsed is >= 200 and < 300)
            {
                successResponses.Add((parsed, response));
            }
        }

        if (successResponses.Count == 0 && rangeWildcard is not null)
        {
            // Cross-corpus #2 (FABLE_ROUNDTRIP): the projection re-emits a literal
            // status the API never promised — keep it, but loudly.
            unsupported.Add("response status-range=2XX projected=200");
            successResponses.Add(rangeWildcard.Value);
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
                            unsupported.Add(
                                $"response status={code} {DescribeUnsupportedContent(response.Content)}"
                            );
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
        List<string> unsupported,
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
            int code;
            // NOTE: "default" responses are projected to 500 — OpenAPI's catch-all has no
            // C# contract equivalent, and real-world specs overwhelmingly use it for errors.
            if (statusStr == "default")
            {
                code = 500;
            }
            else if (statusStr is "4XX" or "4xx")
            {
                // Cross-corpus #2: the range projects to a literal status the API
                // never promised — kept (dropping loses the error type), but loudly.
                unsupported.Add($"error status-range={statusStr} projected=400");
                code = 400;
            }
            else if (statusStr is "5XX" or "5xx")
            {
                unsupported.Add($"error status-range={statusStr} projected=500");
                code = 500;
            }
            else if (!int.TryParse(statusStr, out code) || code < 300)
            {
                // Cross-corpus #3: a 1xx that is not the promoted success status has
                // no contract axis — dropped, but never silently.
                if (code is >= 100 and < 200 && code != successStatus)
                {
                    unsupported.Add($"response status={code} reason=informational-status-dropped");
                }

                continue;
            }

            // A 3xx promoted to the declared success status (redirect-only op)
            // is already carried by .Status(...) — don't double-declare it.
            if (code == successStatus)
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
                    var typeName = mapper.ResolveCSharpType(schema!, $"{fieldName}Error{code}");
                    var description = string.IsNullOrEmpty(response.Description)
                        ? null
                        : response.Description;

                    if (!errors.Any(e => e.StatusCode == code))
                    {
                        errors.Add(new GeneratedErrorResponse(code, typeName, description));
                    }
                }
                else
                {
                    // Error response has content but no supported schema
                    unsupported.Add(
                        $"error status={code} {DescribeUnsupportedContent(response.Content)}"
                    );
                }
            }
            else if (!errors.Any(e => e.StatusCode == code))
            {
                // Void error response (no content) — preserve the status code and description
                var description = string.IsNullOrEmpty(response.Description)
                    ? null
                    : response.Description;
                errors.Add(new GeneratedErrorResponse(code, null, description));
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
            var statusCode = NormalizeStatusCode(statusStr);
            if (statusCode is null || response.Content is not { Count: > 0 } content)
            {
                continue;
            }

            foreach (
                var example in ResolveMediaExamples(
                    content,
                    unsupported,
                    $"response-example status={statusCode.Value}",
                    componentExamples
                )
            )
            {
                responseExamples.Add(
                    new GeneratedEndpointResponseExample(statusCode.Value, example)
                );
            }
        }

        return responseExamples;
    }

    private static IReadOnlyList<GeneratedErrorResponse> EnsureExampleStatusesAreDeclared(
        OpenApiOperation operation,
        int? successStatus,
        IReadOnlyList<GeneratedErrorResponse> errorResponses,
        IReadOnlyList<GeneratedEndpointResponseExample> responseExamples
    )
    {
        if (responseExamples.Count == 0)
        {
            return errorResponses;
        }

        var declaredStatuses = errorResponses.Select(response => response.StatusCode).ToHashSet();
        var descriptionsByStatus = (operation.Responses ?? [])
            .Select(entry => new
            {
                StatusCode = NormalizeStatusCode(entry.Key),
                Description = string.IsNullOrEmpty(entry.Value.Description)
                    ? null
                    : entry.Value.Description,
            })
            .Where(entry => entry.StatusCode is not null)
            .GroupBy(entry => entry.StatusCode!.Value)
            .ToDictionary(group => group.Key, group => group.First().Description);

        var augmentedResponses = errorResponses.ToList();

        foreach (
            var statusCode in responseExamples
                .Select(example => example.StatusCode)
                .Distinct()
                .OrderBy(code => code)
        )
        {
            if (statusCode == successStatus || declaredStatuses.Contains(statusCode))
            {
                continue;
            }

            descriptionsByStatus.TryGetValue(statusCode, out var description);
            augmentedResponses.Add(new GeneratedErrorResponse(statusCode, null, description));
            declaredStatuses.Add(statusCode);
        }

        return augmentedResponses;
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
                examples.Add(
                    new TsEndpointExample(
                        mediaType,
                        Json: InlineEmbeddedExampleRefs(
                            media.Example.ToJsonString(),
                            componentExamples,
                            unsupported,
                            markerPrefix,
                            mediaType,
                            null
                        )
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
        if (resolvedJson is not null)
        {
            resolvedJson = InlineEmbeddedExampleRefs(
                resolvedJson,
                componentExamples,
                unsupported,
                markerPrefix,
                mediaType,
                name
            );
        }

        if (componentExampleId is not null)
        {
            reason = resolvedJson is null ? "unresolved-ref" : null;
            return resolvedJson is not null
                ? new TsEndpointExample(
                    mediaType,
                    name,
                    ComponentExampleId: componentExampleId,
                    ResolvedJson: resolvedJson
                )
                : null;
        }

        reason = resolvedJson is null ? "missing-value" : null;
        return resolvedJson is not null
            ? new TsEndpointExample(mediaType, name, Json: resolvedJson)
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
    /// <summary>
    /// Microsoft.OpenApi cannot hold a JSON null in its JsonNode-based example
    /// model — the library substitutes this sentinel string (a fixed constant,
    /// verified against 2.7.0). Leaking it emitted a GUID string everywhere
    /// the spec said <c>null</c> (FABLE_ROUNDTRIP #9, 45 corpus occurrences).
    /// </summary>
    private const string OpenApiNullSentinel =
        "\"openapi-json-null-sentinel-value-2BF93600-0FE4-4250-987A-E5DDB203E464\"";

    private static string InlineEmbeddedExampleRefs(
        string json,
        IDictionary<string, IOpenApiExample>? componentExamples,
        List<string> unsupported,
        string markerPrefix,
        string mediaType,
        string? name
    )
    {
        json = json.Replace(OpenApiNullSentinel, "null", StringComparison.Ordinal);

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
            return example.Value.ToJsonString();
        }

        return example switch
        {
            OpenApiExampleReference { RecursiveTarget.Value: not null } exampleReference =>
                exampleReference.RecursiveTarget.Value.ToJsonString(),
            OpenApiExampleReference { Target.Value: not null } exampleReference =>
                exampleReference.Target.Value.ToJsonString(),
            _ => null,
        };
    }

    private static int? NormalizeStatusCode(string statusStr)
    {
        if (statusStr == "default")
        {
            return 500;
        }

        if (statusStr is "2XX" or "2xx")
        {
            return 200;
        }

        if (statusStr is "4XX" or "4xx")
        {
            return 400;
        }

        if (statusStr is "5XX" or "5xx")
        {
            return 500;
        }

        return int.TryParse(statusStr, out var code) ? code : null;
    }

    /// <summary>
    /// I10: content-type matching is an exact dictionary lookup, so media-type parameters
    /// (e.g. <c>application/json; charset=utf-8</c>) defeat it. The resulting unsupported
    /// marker names the cause explicitly instead of looking like an exotic content type.
    /// </summary>
    private static string DescribeUnsupportedContent(IDictionary<string, OpenApiMediaType> content)
    {
        var contentTypes = string.Join(", ", content.Keys);
        return content.Keys.Any(k => k.Contains(';'))
            ? $"content-type={contentTypes} reason=media-type-parameters"
            : $"content-type={contentTypes}";
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

    private static (bool IsAnonymous, string? Scheme, string? RequirementsJson) ResolveSecurity(
        OpenApiOperation operation,
        string? globalSecurityScheme
    )
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
            return (false, null, "[]");
        }

        var requirements = new JsonArray();
        foreach (var requirement in operation.Security)
        {
            var requirementJson = new JsonObject();
            foreach (var (scheme, scopes) in requirement)
            {
                var name = scheme.Reference?.Id;
                if (name is null)
                {
                    continue;
                }

                requirementJson[name] = new JsonArray(
                    scopes.Select(scope => JsonValue.Create(scope)).ToArray()
                );
            }

            requirements.Add(requirementJson);
        }

        return (false, null, requirements.ToJsonString());
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
