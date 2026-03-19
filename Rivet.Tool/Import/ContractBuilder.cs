using System.Text.Json;

namespace Rivet.Tool.Import;

/// <summary>
/// Maps OpenAPI paths to v1 static contract class intermediates grouped by tag.
/// </summary>
internal static class ContractBuilder
{
    private static readonly HashSet<string> HttpMethods =
        ["get", "post", "put", "patch", "delete"];

    /// <summary>
    /// Group operations by tag, return one contract per tag.
    /// </summary>
    public static IReadOnlyList<GeneratedContract> BuildContracts(
        JsonElement paths,
        SchemaMapResult schemas,
        string? globalSecurityScheme,
        List<string> warnings)
    {
        var groups = new Dictionary<string, List<GeneratedEndpointField>>();

        foreach (var path in paths.EnumerateObject())
        {
            var route = path.Name;

            foreach (var method in path.Value.EnumerateObject())
            {
                if (!HttpMethods.Contains(method.Name))
                {
                    continue;
                }

                var operation = method.Value;
                var httpMethod = method.Name;
                var tag = ExtractTag(operation) ?? "Default";
                var field = BuildEndpointField(
                    httpMethod, route, operation, tag, globalSecurityScheme, warnings);

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
            .Select(g => new GeneratedContract($"{g.Key}Contract", g.Value))
            .ToList();
    }

    private static GeneratedEndpointField BuildEndpointField(
        string httpMethod,
        string route,
        JsonElement operation,
        string tag,
        string? globalSecurityScheme,
        List<string> warnings)
    {
        var operationId = operation.TryGetProperty("operationId", out var opId)
            ? opId.GetString()!
            : null;

        var fieldName = DeriveFieldName(operationId, httpMethod, route, tag);
        var method = Naming.ToPascalCaseFromSegments(httpMethod);
        var description = operation.TryGetProperty("summary", out var summary)
            ? summary.GetString()
            : null;

        // Resolve input type (requestBody)
        var inputType = ResolveInputType(operation, warnings);

        // Resolve output type (lowest 2xx response with JSON content)
        var (outputType, successStatus) = ResolveOutputType(operation, warnings);

        // Error responses
        var errorResponses = ResolveErrorResponses(operation, warnings);

        // Security
        var (isAnonymous, securityScheme) = ResolveSecurity(operation, globalSecurityScheme);

        return new GeneratedEndpointField(
            fieldName, method, route, inputType, outputType,
            description, successStatus, errorResponses,
            isAnonymous, securityScheme);
    }

    private static string? ResolveInputType(JsonElement operation, List<string> warnings)
    {
        if (!operation.TryGetProperty("requestBody", out var requestBody))
        {
            return null;
        }

        if (!requestBody.TryGetProperty("content", out var content))
        {
            return null;
        }

        // JSON body — standard path
        if (content.TryGetProperty("application/json", out var json)
            && json.TryGetProperty("schema", out var jsonSchema))
        {
            return SchemaMapper.ResolveCSharpType(jsonSchema, warnings);
        }

        // multipart/form-data — file upload path
        if (content.TryGetProperty("multipart/form-data", out var multipart)
            && multipart.TryGetProperty("schema", out var multipartSchema))
        {
            return ResolveMultipartInputType(multipartSchema, warnings);
        }

        return null;
    }

    /// <summary>
    /// Resolves a multipart/form-data schema to an input type name.
    /// The schema is typically a $ref to a named schema whose binary properties
    /// are already mapped to IFormFile by SchemaMapper.
    /// </summary>
    private static string? ResolveMultipartInputType(JsonElement schema, List<string> warnings)
    {
        return SchemaMapper.ResolveCSharpType(schema, warnings);
    }

    private static (string? OutputType, int? SuccessStatus) ResolveOutputType(
        JsonElement operation,
        List<string> warnings)
    {
        if (!operation.TryGetProperty("responses", out var responses))
        {
            return (null, null);
        }

        string? outputType = null;
        int? successCode = null;

        foreach (var resp in responses.EnumerateObject())
        {
            if (!int.TryParse(resp.Name, out var code) || code < 200 || code >= 300)
            {
                continue;
            }

            if (successCode.HasValue && code >= successCode.Value)
            {
                continue;
            }

            successCode = code;

            if (resp.Value.TryGetProperty("content", out var content)
                && content.TryGetProperty("application/json", out var json)
                && json.TryGetProperty("schema", out var schema))
            {
                outputType = SchemaMapper.ResolveCSharpType(schema, warnings);
            }
            else
            {
                outputType = null;
            }
        }

        // Only emit .Status() if non-200
        var statusOverride = successCode != 200 ? successCode : null;

        return (outputType, statusOverride);
    }

    private static IReadOnlyList<GeneratedErrorResponse> ResolveErrorResponses(
        JsonElement operation,
        List<string> warnings)
    {
        if (!operation.TryGetProperty("responses", out var responses))
        {
            return [];
        }

        var errors = new List<GeneratedErrorResponse>();

        foreach (var resp in responses.EnumerateObject())
        {
            if (!int.TryParse(resp.Name, out var code) || code < 300)
            {
                continue;
            }

            // Only include if the response has a typed schema
            if (resp.Value.TryGetProperty("content", out var content)
                && content.TryGetProperty("application/json", out var json)
                && json.TryGetProperty("schema", out var schema))
            {
                var typeName = SchemaMapper.ResolveCSharpType(schema, warnings);
                var description = resp.Value.TryGetProperty("description", out var desc)
                    ? desc.GetString()
                    : null;

                errors.Add(new GeneratedErrorResponse(code, typeName, description));
            }
        }

        return errors;
    }

    private static (bool IsAnonymous, string? Scheme) ResolveSecurity(
        JsonElement operation,
        string? globalSecurityScheme)
    {
        if (!operation.TryGetProperty("security", out var security))
        {
            // No operation-level security — use global default
            return globalSecurityScheme is not null
                ? (false, globalSecurityScheme)
                : (false, null);
        }

        // Empty array → anonymous
        if (security.GetArrayLength() == 0)
        {
            return (true, null);
        }

        // First security requirement object
        foreach (var req in security.EnumerateArray())
        {
            foreach (var scheme in req.EnumerateObject())
            {
                return (false, scheme.Name);
            }
        }

        return (false, null);
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
            .Where(s => !s.StartsWith('{'))
            .Select(Naming.ToPascalCaseFromSegments);

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

    private static string? ExtractTag(JsonElement operation)
    {
        if (!operation.TryGetProperty("tags", out var tags))
        {
            return null;
        }

        if (tags.GetArrayLength() == 0)
        {
            return null;
        }

        return Naming.ToPascalCaseFromSegments(tags[0].GetString()!);
    }
}
