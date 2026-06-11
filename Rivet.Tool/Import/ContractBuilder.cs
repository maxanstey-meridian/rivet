using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

/// <summary>
/// Maps OpenAPI paths to v1 static contract class intermediates grouped by tag.
/// </summary>
internal static class ContractBuilder
{
    private static readonly HashSet<HttpMethod> SupportedMethods =
        [HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete];

    /// <summary>
    /// Group operations by tag, return one contract per tag.
    /// </summary>
    public static IReadOnlyList<GeneratedContract> BuildContracts(
        OpenApiPaths paths,
        SchemaMapper mapper,
        string? globalSecurityScheme)
    {
        var groups = new Dictionary<string, List<GeneratedEndpointField>>();

        foreach (var (route, pathItem) in paths)
        {
            foreach (var (method, operation) in pathItem.Operations ?? [])
            {
                if (!SupportedMethods.Contains(method))
                {
                    continue;
                }

                var httpMethod = method.Method.ToLowerInvariant();
                var tag = ExtractTag(operation) ?? "Default";

                // WP-1.1: prefer the explicit x-rivet-contract extension — the tag
                // convention is lossy for unusual casing (underscores, acronyms) and
                // breaks under hand-edits. Convention stays as the fallback.
                var contractKey = GetOperationExtensionString(operation, "x-rivet-contract") is { } contractExt
                    ? Naming.StripInvalidIdentifierChars(contractExt)
                    : tag;

                var field = BuildEndpointField(
                    httpMethod, route, operation, tag, globalSecurityScheme, mapper);

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
            .Select(g => new GeneratedContract($"{g.Key}Contract", DeduplicateFields(g.Value)))
            .ToList();
    }

    private static GeneratedEndpointField BuildEndpointField(
        string httpMethod,
        string route,
        OpenApiOperation operation,
        string tag,
        string? globalSecurityScheme,
        SchemaMapper mapper)
    {
        var operationId = operation.OperationId;

        // WP-1.1: prefer the explicit x-rivet-endpoint extension over the
        // operationId/tag-prefix convention (lossy for unusual casing).
        var fieldName = GetOperationExtensionString(operation, "x-rivet-endpoint") is { } endpointExt
            ? Naming.StripInvalidIdentifierChars(endpointExt)
            : DeriveFieldName(operationId, httpMethod, route, tag);
        var method = Naming.ToPascalCaseFromSegments(httpMethod);
        var summary = string.IsNullOrEmpty(operation.Summary) ? null : operation.Summary;
        var description = string.IsNullOrEmpty(operation.Description) ? null : operation.Description;
        var unsupported = new List<string>();

        // QueryAuth: read x-rivet-query-auth extension
        var queryAuthParameterName = ResolveQueryAuth(operation);

        // Resolve input type (requestBody — $ref resolved by library)
        var (inputType, isFormEncoded) = ResolveInputType(operation, mapper, fieldName, unsupported);

        // If no body input, synthesize an input record from path/query parameters
        if (inputType is null)
        {
            inputType = ResolveParamInputType(operation, mapper, fieldName, unsupported, queryAuthParameterName);
        }

        // Resolve output type (lowest 2xx response with JSON content)
        var (outputType, successStatus, fileContentType) = ResolveOutputType(operation, mapper, fieldName, unsupported);

        // File endpoint: binary content type on a GET endpoint → Define.File()
        // Non-GET binary endpoints (e.g. POST → PDF) keep Define.{Method}().ProducesFile()
        var isFileEndpoint = fileContentType is not null
            && httpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);

        // Error responses
        var errorResponses = ResolveErrorResponses(operation, mapper, fieldName, unsupported);
        var requestExamples = ResolveRequestExamples(operation, unsupported);
        var responseExamples = ResolveResponseExamples(operation, unsupported);
        errorResponses = EnsureExampleStatusesAreDeclared(
            operation,
            successStatus,
            errorResponses,
            responseExamples);

        // Security
        var (isAnonymous, securityScheme) = ResolveSecurity(operation, globalSecurityScheme, unsupported);

        return new GeneratedEndpointField(
            fieldName, method, route, inputType, outputType,
            summary, description, successStatus, errorResponses,
            isAnonymous, securityScheme, unsupported, fileContentType, isFormEncoded,
            requestExamples, responseExamples, isFileEndpoint, queryAuthParameterName);
    }

    private static (string? InputType, bool IsFormEncoded) ResolveInputType(
        OpenApiOperation operation, SchemaMapper mapper, string fieldName, List<string> unsupported)
    {
        var requestBody = operation.RequestBody;
        if (requestBody is null)
        {
            return (null, false);
        }

        // A $ref request body that the library could not resolve has no content —
        // never drop it silently (I11 class): leave a loud marker on the endpoint.
        if (requestBody is OpenApiRequestBodyReference { Target: null } unresolvedRef)
        {
            var refId = unresolvedRef.Reference?.Id ?? "unknown";
            unsupported.Add($"body $ref={refId} reason=unresolved-ref");
            return (null, false);
        }

        var content = requestBody.Content;
        if (content is null)
        {
            return (null, false);
        }

        // Try content types in priority order, tracking which one matched
        IOpenApiSchema? schema = null;
        var isFormEncoded = false;

        if (TryGetSchemaForContentType(content, "application/json", out schema))
        {
            // JSON — default
        }
        else if (TryGetSchemaForContentType(content, "application/x-www-form-urlencoded", out schema))
        {
            isFormEncoded = true;
        }
        else if (TryGetSchemaForContentType(content, "multipart/form-data", out schema)
            || TryGetSchemaForContentType(content, "*/*", out schema))
        {
            // multipart or wildcard — not form-encoded
        }

        if (schema is not null)
        {
            // x-rivet-input-type preserves the original record name through round-trips.
            // The convention fallback is segment-pascalized: underscores in a component
            // name are treated as delimiters on the next import, so a synthesized name
            // containing them would mutate every loop.
            var context = GetExtensionString(schema, "x-rivet-input-type")
                ?? $"{Naming.ToPascalCaseFromSegments(fieldName)}Request";
            return (mapper.ResolveCSharpType(schema, context), isFormEncoded);
        }

        // Fallback: try binary or text content types with a schema
        var fallbackType = content.Keys.FirstOrDefault(k =>
            IsBinaryContentType(k)
            || k.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("application/x-", StringComparison.OrdinalIgnoreCase));
        if (fallbackType is not null && TryGetSchemaForContentType(content, fallbackType, out schema))
        {
            return (mapper.ResolveCSharpType(schema!, $"{fieldName}Request"), false);
        }

        // Request body exists but uses unsupported content type(s)
        unsupported.Add($"body {DescribeUnsupportedContent(content)}");
        return (null, false);
    }

    private static string? ResolveParamInputType(
        OpenApiOperation operation, SchemaMapper mapper, string fieldName, List<string> unsupported, string? queryAuthParameterName = null)
    {
        if (operation.Parameters is null or { Count: 0 })
        {
            return null;
        }

        var properties = new List<RecordProperty>();
        var metadataDropped = new List<string>();

        foreach (var param in operation.Parameters)
        {
            if (param.In is not (ParameterLocation.Path or ParameterLocation.Query
                or ParameterLocation.Header or ParameterLocation.Cookie))
            {
                continue;
            }

            if (param.Schema is null || param.Name is null)
            {
                continue;
            }

            // Skip QueryAuth token parameter — it's not an input field
            if (queryAuthParameterName is not null
                && param.In is ParameterLocation.Query
                && string.Equals(param.Name, queryAuthParameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // I13: the synthesized input record erases the in-location — header/cookie
            // params re-emit as query params. Loud per-param marker.
            if (param.In is ParameterLocation.Header or ParameterLocation.Cookie)
            {
                var location = param.In is ParameterLocation.Header ? "header" : "cookie";
                unsupported.Add($"param name={param.Name} in={location} reason=location-erased-to-query");
            }

            // I13: description/deprecated/constraints don't survive into the synthesized
            // input record — aggregated marker below names the affected params.
            if (!string.IsNullOrEmpty(param.Description)
                || param.Deprecated
                || HasParamConstraints(param.Schema))
            {
                metadataDropped.Add(param.Name);
            }

            var csharpType = mapper.ResolveCSharpType(param.Schema, $"{fieldName}{Naming.ToPascalCaseFromSegments(param.Name)}");
            if (!param.Required && !csharpType.EndsWith("?"))
            {
                csharpType += "?";
            }

            properties.Add(new RecordProperty(
                Naming.ToPascalCaseFromSegments(param.Name),
                csharpType,
                param.Required));
        }

        if (metadataDropped.Count > 0)
        {
            unsupported.Add($"param-metadata params={string.Join(", ", metadataDropped)} reason=metadata-dropped");
        }

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

        // Dedup-with-shape-check (I3): a same-named synthetic input with a different shape
        // (e.g. two tags both synthesizing GetByIdInput, or a name-only collision with a
        // component schema) gets a disambiguated name.
        return mapper.AddExtraRecord(new GeneratedRecord(recordName, deduped));
    }

    /// <summary>
    /// I13: does the parameter schema carry validation constraints that the synthesized
    /// input record drops?
    /// </summary>
    private static bool HasParamConstraints(IOpenApiSchema schema) =>
        schema.MinLength.HasValue || schema.MaxLength.HasValue || schema.Pattern is not null
        || schema.Minimum is not null || schema.Maximum is not null
        || schema.ExclusiveMinimum is not null || schema.ExclusiveMaximum is not null
        || schema.MultipleOf is not null || schema.MinItems.HasValue || schema.MaxItems.HasValue
        || schema.UniqueItems == true;

    private static readonly HashSet<string> BinaryContentTypes = new(StringComparer.OrdinalIgnoreCase)
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
        BinaryContentTypes.Contains(contentType)
        || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static (string? OutputType, int? SuccessStatus, string? FileContentType) ResolveOutputType(
        OpenApiOperation operation,
        SchemaMapper mapper,
        string fieldName,
        List<string> unsupported)
    {
        if (operation.Responses is null)
        {
            return (null, null, null);
        }

        string? outputType = null;
        int? successCode = null;
        string? fileContentType = null;

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

            if (response.Content is { Count: > 0 })
            {
                if (TryGetSchemaForContentType(response.Content, "application/json", out var schema)
                    || TryGetSchemaForContentType(response.Content, "*/*", out schema))
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
                            k.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
                        if (textType is not null
                            && TryGetSchemaForContentType(response.Content, textType, out schema))
                        {
                            outputType = mapper.ResolveCSharpType(schema!, $"{fieldName}Response");
                        }
                        else
                        {
                            unsupported.Add($"response status={code} {DescribeUnsupportedContent(response.Content)}");
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

        return (outputType, successCode, fileContentType);
    }

    private static IReadOnlyList<GeneratedErrorResponse> ResolveErrorResponses(
        OpenApiOperation operation,
        SchemaMapper mapper,
        string fieldName,
        List<string> unsupported)
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
                code = 400;
            }
            else if (statusStr is "5XX" or "5xx")
            {
                code = 500;
            }
            else if (!int.TryParse(statusStr, out code) || code < 300)
            {
                continue;
            }

            if (response.Content is { Count: > 0 })
            {
                if (TryGetSchemaForContentType(response.Content, "application/json", out var schema)
                    || TryGetSchemaForContentType(response.Content, "*/*", out schema))
                {
                    var typeName = mapper.ResolveCSharpType(schema!, $"{fieldName}Error{code}");
                    var description = string.IsNullOrEmpty(response.Description) ? null : response.Description;

                    if (!errors.Any(e => e.StatusCode == code))
                    {
                        errors.Add(new GeneratedErrorResponse(code, typeName, description));
                    }
                }
                else
                {
                    // Error response has content but no supported schema
                    unsupported.Add($"error status={code} {DescribeUnsupportedContent(response.Content)}");
                }
            }
            else if (!errors.Any(e => e.StatusCode == code))
            {
                // Void error response (no content) — preserve the status code and description
                var description = string.IsNullOrEmpty(response.Description) ? null : response.Description;
                errors.Add(new GeneratedErrorResponse(code, null, description));
            }
        }

        return errors;
    }

    private static IReadOnlyList<TsEndpointExample> ResolveRequestExamples(
        OpenApiOperation operation,
        List<string> unsupported)
    {
        if (operation.RequestBody?.Content is not { Count: > 0 } content)
        {
            return [];
        }

        return ResolveMediaExamples(content, unsupported, "request-example");
    }

    private static IReadOnlyList<GeneratedEndpointResponseExample> ResolveResponseExamples(
        OpenApiOperation operation,
        List<string> unsupported)
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

            foreach (var example in ResolveMediaExamples(
                content,
                unsupported,
                $"response-example status={statusCode.Value}"))
            {
                responseExamples.Add(new GeneratedEndpointResponseExample(statusCode.Value, example));
            }
        }

        return responseExamples;
    }

    private static IReadOnlyList<GeneratedErrorResponse> EnsureExampleStatusesAreDeclared(
        OpenApiOperation operation,
        int? successStatus,
        IReadOnlyList<GeneratedErrorResponse> errorResponses,
        IReadOnlyList<GeneratedEndpointResponseExample> responseExamples)
    {
        if (responseExamples.Count == 0)
        {
            return errorResponses;
        }

        var declaredStatuses = errorResponses
            .Select(response => response.StatusCode)
            .ToHashSet();
        var descriptionsByStatus = (operation.Responses ?? [])
            .Select(entry => new
            {
                StatusCode = NormalizeStatusCode(entry.Key),
                Description = string.IsNullOrEmpty(entry.Value.Description) ? null : entry.Value.Description,
            })
            .Where(entry => entry.StatusCode is not null)
            .GroupBy(entry => entry.StatusCode!.Value)
            .ToDictionary(group => group.Key, group => group.First().Description);

        var augmentedResponses = errorResponses.ToList();

        foreach (var statusCode in responseExamples
                     .Select(example => example.StatusCode)
                     .Distinct()
                     .OrderBy(code => code))
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
        string markerPrefix)
    {
        var examples = new List<TsEndpointExample>();

        foreach (var (mediaType, media) in content)
        {
            if (media.Example is not null)
            {
                examples.Add(new TsEndpointExample(
                    mediaType,
                    Json: media.Example.ToJsonString()));
            }

            if (media.Examples is null)
            {
                continue;
            }

            foreach (var (name, example) in media.Examples)
            {
                var endpointExample = ResolveExample(mediaType, name, example, out var reason);
                if (endpointExample is not null)
                {
                    examples.Add(endpointExample);
                    continue;
                }

                if (reason is not null)
                {
                    unsupported.Add(BuildExampleUnsupportedMarker(
                        markerPrefix,
                        mediaType,
                        name,
                        TryGetComponentExampleId(example),
                        reason));
                }
            }
        }

        return examples;
    }

    private static TsEndpointExample? ResolveExample(
        string mediaType,
        string? name,
        IOpenApiExample example,
        out string? reason)
    {
        var componentExampleId = TryGetComponentExampleId(example);
        var resolvedJson = TryGetExampleJson(example);

        if (componentExampleId is not null)
        {
            reason = resolvedJson is null ? "unresolved-ref" : null;
            return resolvedJson is not null
                ? new TsEndpointExample(
                    mediaType,
                    name,
                    ComponentExampleId: componentExampleId,
                    ResolvedJson: resolvedJson)
                : null;
        }

        reason = resolvedJson is null ? "missing-value" : null;
        return resolvedJson is not null
            ? new TsEndpointExample(mediaType, name, Json: resolvedJson)
            : null;
    }

    private static string BuildExampleUnsupportedMarker(
        string markerPrefix,
        string mediaType,
        string? name,
        string? componentExampleId,
        string reason)
    {
        var parts = new List<string>
        {
            markerPrefix,
            $"media-type={mediaType}",
        };

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
            OpenApiExampleReference { RecursiveTarget.Value: not null } exampleReference
                => exampleReference.RecursiveTarget.Value.ToJsonString(),
            OpenApiExampleReference { Target.Value: not null } exampleReference
                => exampleReference.Target.Value.ToJsonString(),
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
        out IOpenApiSchema? schema)
    {
        if (content.TryGetValue(contentType, out var mediaType) && mediaType.Schema is not null)
        {
            schema = mediaType.Schema;
            return true;
        }

        schema = null;
        return false;
    }

    private static (bool IsAnonymous, string? Scheme) ResolveSecurity(
        OpenApiOperation operation,
        string? globalSecurityScheme,
        List<string> unsupported)
    {
        if (operation.Security is null)
        {
            // No operation-level security — use global default
            return globalSecurityScheme is not null
                ? (false, globalSecurityScheme)
                : (false, null);
        }

        // Empty list → anonymous
        if (operation.Security.Count == 0)
        {
            return (true, null);
        }

        // I12: the contract model carries a single scheme, so OR alternatives, AND
        // combinations and scopes collapse to the first resolvable scheme — with a loud
        // marker instead of a silent drop.
        var schemeIds = operation.Security
            .SelectMany(req => req.Keys)
            .Select(scheme => scheme.Reference?.Id)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();

        if (schemeIds.Count > 1)
        {
            unsupported.Add($"security schemes={string.Join(", ", schemeIds)} reason=multi-scheme-first-only");
        }

        return (false, schemeIds.FirstOrDefault());
    }

    private static IReadOnlyList<GeneratedEndpointField> DeduplicateFields(
        List<GeneratedEndpointField> fields)
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
        string tag)
    {
        if (operationId is not null)
        {
            return StripTagPrefix(operationId, tag);
        }

        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.StartsWith('{') && s.EndsWith('}')
                ? "By" + Naming.ToPascalCaseFromSegments(s[1..^1])
                : Naming.ToPascalCaseFromSegments(s));

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
        if (operation.Extensions is null
            || !operation.Extensions.TryGetValue("x-rivet-query-auth", out var ext))
        {
            return null;
        }

        if (ext is JsonNodeExtension { Node: JsonObject obj }
            && obj.TryGetPropertyValue("parameterName", out var nameNode))
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
        return firstTag?.Name is not null
            ? Naming.ToPascalCaseFromSegments(firstTag.Name)
            : null;
    }
}
