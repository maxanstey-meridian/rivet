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
        var summary = string.IsNullOrEmpty(operation.Summary) ? null : operation.Summary;
        var description = string.IsNullOrEmpty(operation.Description) ? null : operation.Description;
        var unsupported = new List<string>();

        // Resolve input type (requestBody — $ref resolved by library)
        var (inputType, isFormEncoded) = ResolveInputType(operation, mapper, fieldName, unsupported);

        // If no body input, synthesize an input record from path/query parameters
        if (inputType is null)
        {
            inputType = ResolveParamInputType(operation, mapper, fieldName);
        }

        // Resolve output type (lowest 2xx response with JSON content)
        var (outputType, successStatus, fileContentType) = ResolveOutputType(operation, mapper, fieldName, unsupported);

        // Error responses
        var errorResponses = ResolveErrorResponses(operation, mapper, fieldName, unsupported);
        var requestExamples = ResolveRequestExamples(operation);
        var responseExamples = ResolveResponseExamples(operation);

        // Security
        var (isAnonymous, securityScheme) = ResolveSecurity(operation, globalSecurityScheme);

        return new GeneratedEndpointField(
            fieldName, method, route, inputType, outputType,
            summary, description, successStatus, errorResponses,
            isAnonymous, securityScheme, unsupported, fileContentType, isFormEncoded,
            requestExamples, responseExamples);
    }

    private static (string? InputType, bool IsFormEncoded) ResolveInputType(
        OpenApiOperation operation, SchemaMapper mapper, string fieldName, List<string> unsupported)
    {
        var content = operation.RequestBody?.Content;
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
            // x-rivet-input-type preserves the original record name through round-trips
            var context = GetExtensionString(schema, "x-rivet-input-type") ?? $"{fieldName}Request";
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
        var contentTypes = string.Join(", ", content.Keys);
        unsupported.Add($"body content-type={contentTypes}");
        return (null, false);
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
            if (param.In is not (ParameterLocation.Path or ParameterLocation.Query
                or ParameterLocation.Header or ParameterLocation.Cookie))
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

        // If a schema with this name was already mapped from components/schemas, reuse it
        if (!mapper.HasMappedSchema(recordName))
        {
            mapper.AddExtraRecord(new GeneratedRecord(recordName, SchemaClassifier.DeduplicateProperties(properties)));
        }

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
            else if (!errors.Any(e => e.StatusCode == code))
            {
                // Void error response (no content) — preserve the status code and description
                var description = string.IsNullOrEmpty(response.Description) ? null : response.Description;
                errors.Add(new GeneratedErrorResponse(code, null, description));
            }
        }

        return errors;
    }

    private static IReadOnlyList<TsEndpointExample> ResolveRequestExamples(OpenApiOperation operation)
    {
        if (operation.RequestBody?.Content is not { Count: > 0 } content)
        {
            return [];
        }

        return ResolveMediaExamples(content);
    }

    private static IReadOnlyList<GeneratedEndpointResponseExample> ResolveResponseExamples(OpenApiOperation operation)
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

            foreach (var example in ResolveMediaExamples(content))
            {
                responseExamples.Add(new GeneratedEndpointResponseExample(statusCode.Value, example));
            }
        }

        return responseExamples;
    }

    private static IReadOnlyList<TsEndpointExample> ResolveMediaExamples(
        IDictionary<string, OpenApiMediaType> content)
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
                var endpointExample = ResolveExample(mediaType, name, example);
                if (endpointExample is not null)
                {
                    examples.Add(endpointExample);
                }
            }
        }

        return examples;
    }

    private static TsEndpointExample? ResolveExample(
        string mediaType,
        string? name,
        IOpenApiExample example)
    {
        var componentExampleId = TryGetComponentExampleId(example);
        var resolvedJson = TryGetExampleJson(example);

        if (componentExampleId is not null)
        {
            return resolvedJson is not null
                ? new TsEndpointExample(
                    mediaType,
                    name,
                    ComponentExampleId: componentExampleId,
                    ResolvedJson: resolvedJson)
                : new TsEndpointExample(
                    mediaType,
                    name,
                    ComponentExampleId: componentExampleId);
        }

        return resolvedJson is not null
            ? new TsEndpointExample(mediaType, name, Json: resolvedJson)
            : null;
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
