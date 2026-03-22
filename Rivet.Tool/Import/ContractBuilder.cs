using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

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
                var field = BuildEndpointField(
                    httpMethod, route, operation, tag, globalSecurityScheme, mapper);

                if (!groups.TryGetValue(tag, out var list))
                {
                    list = [];
                    groups[tag] = list;
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
        var fieldName = DeriveFieldName(operationId, httpMethod, route, tag);
        var method = Naming.ToPascalCaseFromSegments(httpMethod);
        var description = operation.Summary;
        var unsupported = new List<string>();

        // Resolve input type (requestBody — $ref resolved by library)
        var inputType = ResolveInputType(operation, mapper, fieldName, unsupported);

        // If no body input, synthesize an input record from path/query parameters
        if (inputType is null)
        {
            inputType = ResolveParamInputType(operation, mapper, fieldName);
        }

        // Resolve output type (lowest 2xx response with JSON content)
        var (outputType, successStatus, fileContentType) = ResolveOutputType(operation, mapper, fieldName, unsupported);

        // Error responses
        var errorResponses = ResolveErrorResponses(operation, mapper, fieldName, unsupported);

        // Security
        var (isAnonymous, securityScheme) = ResolveSecurity(operation, globalSecurityScheme);

        return new GeneratedEndpointField(
            fieldName, method, route, inputType, outputType,
            description, successStatus, errorResponses,
            isAnonymous, securityScheme, unsupported, fileContentType);
    }

    private static string? ResolveInputType(
        OpenApiOperation operation, SchemaMapper mapper, string fieldName, List<string> unsupported)
    {
        var content = operation.RequestBody?.Content;
        if (content is null)
        {
            return null;
        }

        // Try content types in priority order
        if (TryGetSchemaForContentType(content, "application/json", out var schema)
            || TryGetSchemaForContentType(content, "application/x-www-form-urlencoded", out schema)
            || TryGetSchemaForContentType(content, "multipart/form-data", out schema)
            || TryGetSchemaForContentType(content, "*/*", out schema))
        {
            // x-rivet-input-type preserves the original record name through round-trips
            var context = GetExtensionString(schema!, "x-rivet-input-type") ?? $"{fieldName}Request";
            return mapper.ResolveCSharpType(schema!, context);
        }

        // Fallback: try binary or text content types with a schema
        var fallbackType = content.Keys.FirstOrDefault(k =>
            IsBinaryContentType(k)
            || k.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("application/x-", StringComparison.OrdinalIgnoreCase));
        if (fallbackType is not null && TryGetSchemaForContentType(content, fallbackType, out schema))
        {
            return mapper.ResolveCSharpType(schema!, $"{fieldName}Request");
        }

        // Request body exists but uses unsupported content type(s)
        var contentTypes = string.Join(", ", content.Keys);
        unsupported.Add($"body content-type={contentTypes}");
        return null;
    }

    private static string? ResolveParamInputType(
        OpenApiOperation operation, SchemaMapper mapper, string fieldName)
    {
        if (operation.Parameters is null or { Count: 0 })
        {
            return null;
        }

        var properties = new List<RecordProperty>();

        foreach (var param in operation.Parameters)
        {
            if (param.In is not (ParameterLocation.Path or ParameterLocation.Query))
            {
                continue;
            }

            if (param.Schema is null || param.Name is null)
            {
                continue;
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

        if (properties.Count == 0)
        {
            return null;
        }

        var recordName = $"{fieldName}Input";
        mapper.AddExtraRecord(new GeneratedRecord(recordName, SchemaMapper.DeduplicateProperties(properties)));
        return recordName;
    }

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

        foreach (var (statusStr, response) in operation.Responses)
        {
            if (!int.TryParse(statusStr, out var code) || code < 200 || code >= 300)
            {
                continue;
            }

            if (successCode.HasValue && code >= successCode.Value)
            {
                continue;
            }

            successCode = code;

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
                            var contentTypes = string.Join(", ", response.Content.Keys);
                            unsupported.Add($"response status={code} content-type={contentTypes}");
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

        // Only emit .Status() if non-200
        var statusOverride = successCode != 200 ? successCode : null;

        return (outputType, statusOverride, fileContentType);
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
                    var contentTypes = string.Join(", ", response.Content.Keys);
                    unsupported.Add($"error status={code} content-type={contentTypes}");
                }
            }
            // Responses with no content block at all are intentionally void — not unsupported
        }

        return errors;
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
        string? globalSecurityScheme)
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

        // First security requirement object
        foreach (var req in operation.Security)
        {
            foreach (var (scheme, _) in req)
            {
                return (false, scheme.Reference?.Id);
            }
        }

        return (false, null);
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
