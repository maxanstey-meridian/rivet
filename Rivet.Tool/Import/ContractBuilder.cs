using System.Text.Json;

namespace Rivet.Tool.Import;

/// <summary>
/// Maps OpenAPI paths to v2 abstract contract class intermediates grouped by tag.
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
        // Collect all operations grouped by tag
        var groups = new Dictionary<string, List<(string Route, string HttpMethod, JsonElement Operation)>>();

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
                var tag = ExtractTag(operation) ?? "Default";

                if (!groups.TryGetValue(tag, out var list))
                {
                    list = [];
                    groups[tag] = list;
                }

                list.Add((route, method.Name, operation));
            }
        }

        var contracts = new List<GeneratedContract>();

        foreach (var (tag, operations) in groups.OrderBy(g => g.Key))
        {
            var routes = operations.Select(o => o.Route).ToList();
            var routePrefix = ComputeCommonRoutePrefix(routes);

            var fields = operations
                .Select(o => BuildEndpointField(
                    o.HttpMethod, o.Route, routePrefix, o.Operation, tag, warnings))
                .ToList();

            contracts.Add(new GeneratedContract($"{tag}Contract", routePrefix, fields));
        }

        return contracts;
    }

    private static GeneratedEndpointField BuildEndpointField(
        string httpMethod,
        string route,
        string routePrefix,
        JsonElement operation,
        string tag,
        List<string> warnings)
    {
        var operationId = operation.TryGetProperty("operationId", out var opId)
            ? opId.GetString()!
            : null;

        var fieldName = DeriveFieldName(operationId, httpMethod, route, tag);
        var method = SchemaMapper.ToPascalCase(httpMethod);
        var description = operation.TryGetProperty("summary", out var summary)
            ? summary.GetString()
            : null;

        // Compute method-level route suffix
        var methodRoute = ComputeMethodRoute(route, routePrefix);

        // Resolve input type (requestBody)
        var inputType = ResolveInputType(operation, warnings);

        // Resolve output type (lowest 2xx response with JSON content)
        var (outputType, successStatus) = ResolveOutputType(operation, warnings);

        // Error responses (include void errors too for [ProducesResponseType(statusCode)])
        var errorResponses = ResolveErrorResponses(operation, warnings);

        // Build method parameters: route params + body param
        var methodParams = BuildMethodParams(httpMethod, route, routePrefix, inputType, operation, warnings);

        return new GeneratedEndpointField(
            fieldName, method, route, methodRoute, inputType, outputType,
            description, successStatus, errorResponses, methodParams);
    }

    private static IReadOnlyList<GeneratedMethodParam> BuildMethodParams(
        string httpMethod,
        string route,
        string routePrefix,
        string? inputType,
        JsonElement operation,
        List<string> warnings)
    {
        var parameters = new List<GeneratedMethodParam>();

        // Extract path parameters from OpenAPI operation or route template
        var pathParams = ExtractPathParams(operation, route, warnings);
        foreach (var (name, type) in pathParams)
        {
            parameters.Add(new GeneratedMethodParam(name, type, false));
        }

        // Body parameter
        if (inputType is not null)
        {
            parameters.Add(new GeneratedMethodParam("body", inputType, true));
        }

        return parameters;
    }

    private static List<(string Name, string CSharpType)> ExtractPathParams(
        JsonElement operation,
        string route,
        List<string> warnings)
    {
        var result = new List<(string, string)>();

        // Try to get typed params from the OpenAPI parameters array
        if (operation.TryGetProperty("parameters", out var paramsArray))
        {
            foreach (var param in paramsArray.EnumerateArray())
            {
                if (param.TryGetProperty("in", out var inEl) && inEl.GetString() == "path")
                {
                    var name = param.GetProperty("name").GetString()!;
                    var type = "string";

                    if (param.TryGetProperty("schema", out var schema))
                    {
                        type = SchemaMapper.ResolveCSharpType(schema, warnings);
                    }

                    result.Add((name, type));
                }
            }
        }

        // If no parameters array, extract from route template
        if (result.Count == 0)
        {
            var segments = route.Split('/');
            foreach (var segment in segments)
            {
                if (segment.StartsWith('{') && segment.EndsWith('}'))
                {
                    var name = segment[1..^1];
                    // Strip route constraints like {id:guid}
                    var colonIdx = name.IndexOf(':');
                    if (colonIdx >= 0)
                    {
                        name = name[..colonIdx];
                    }

                    result.Add((name, "string"));
                }
            }
        }

        return result;
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

        if (!content.TryGetProperty("application/json", out var json))
        {
            return null;
        }

        if (!json.TryGetProperty("schema", out var schema))
        {
            return null;
        }

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

        return (outputType, successCode);
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

            string? typeName = null;
            var description = resp.Value.TryGetProperty("description", out var desc)
                ? desc.GetString()
                : null;

            if (resp.Value.TryGetProperty("content", out var content)
                && content.TryGetProperty("application/json", out var json)
                && json.TryGetProperty("schema", out var schema))
            {
                typeName = SchemaMapper.ResolveCSharpType(schema, warnings);
            }

            errors.Add(new GeneratedErrorResponse(code, typeName, description));
        }

        return errors;
    }

    /// <summary>
    /// Compute the longest common route prefix across all routes in a tag group.
    /// </summary>
    internal static string ComputeCommonRoutePrefix(IReadOnlyList<string> routes)
    {
        if (routes.Count == 0)
        {
            return "";
        }

        var segmentSets = routes
            .Select(r => r.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !s.StartsWith('{'))
                .ToArray())
            .ToList();

        var minLen = segmentSets.Min(s => s.Length);
        var commonCount = 0;

        for (var i = 0; i < minLen; i++)
        {
            var segment = segmentSets[0][i];
            if (segmentSets.All(s => s[i] == segment))
            {
                commonCount++;
            }
            else
            {
                break;
            }
        }

        if (commonCount == 0)
        {
            return "";
        }

        return string.Join("/", segmentSets[0].Take(commonCount));
    }

    /// <summary>
    /// Compute the method-level route suffix by stripping the common prefix.
    /// Returns null if the route exactly matches the prefix.
    /// </summary>
    private static string? ComputeMethodRoute(string fullRoute, string routePrefix)
    {
        var prefixWithSlash = "/" + routePrefix;

        if (fullRoute == prefixWithSlash || fullRoute == routePrefix)
        {
            return null;
        }

        // Strip prefix
        var suffix = fullRoute;
        if (suffix.StartsWith(prefixWithSlash))
        {
            suffix = suffix[(prefixWithSlash.Length + 1)..]; // +1 for trailing /
        }
        else if (suffix.StartsWith(routePrefix))
        {
            suffix = suffix[(routePrefix.Length + 1)..];
        }

        return suffix.Length > 0 ? suffix : null;
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
            .Select(SchemaMapper.ToPascalCase);

        return SchemaMapper.ToPascalCase(httpMethod) + string.Concat(segments);
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
                return SchemaMapper.ToPascalCase(stripped);
            }
        }

        return SchemaMapper.ToPascalCase(operationId);
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

        return SchemaMapper.ToPascalCase(tags[0].GetString()!);
    }
}
