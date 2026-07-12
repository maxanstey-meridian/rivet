using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;

namespace Rivet.Tests;

internal static partial class RoundTripGateValidator
{
    private static readonly HashSet<string> _methods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get",
        "put",
        "post",
        "delete",
        "patch",
        "head",
        "options",
        "trace",
    };

    public static IReadOnlyList<string> Validate(string sourcePath, string emittedPath)
    {
        var findings = new List<string>();
        string emittedJson;
        try
        {
            emittedJson = File.ReadAllText(emittedPath);
        }
        catch (Exception exception)
        {
            findings.Add($"cannot read emitted document: {exception.Message}");
            return findings;
        }

        try
        {
            var readResult = OpenApiDocument.Parse(emittedJson, "json");
            if (readResult.Document is null)
            {
                findings.Add("OpenAPI parser returned no document");
            }

            foreach (var error in readResult.Diagnostic?.Errors ?? [])
            {
                findings.Add($"OpenAPI validation: {error.Message}");
            }
        }
        catch (Exception exception)
        {
            findings.Add($"OpenAPI parser failed: {exception.Message}");
        }

        JsonDocument source;
        JsonDocument emitted;
        try
        {
            source = JsonDocument.Parse(File.ReadAllText(sourcePath));
            emitted = JsonDocument.Parse(emittedJson);
        }
        catch (Exception exception)
        {
            findings.Add($"JSON parsing failed: {exception.Message}");
            return findings;
        }

        using (source)
        using (emitted)
        {
            var root = emitted.RootElement;
            ValidateRoot(root, findings);
            CollectUnresolvedLocalReferences(root, root, "$", findings);
            ValidateSecurityRequirements(root, findings);
            ValidateOperations(source.RootElement, root, findings);
            ValidateComponentIdentity(source.RootElement, root, findings);
        }

        return findings;
    }

    private static void ValidateRoot(JsonElement root, List<string> findings)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            findings.Add("document root is not an object");
            return;
        }

        if (
            !root.TryGetProperty("openapi", out var version)
            || version.ValueKind != JsonValueKind.String
            || version.GetString() is not { } versionText
            || !versionText.StartsWith("3.1.", StringComparison.Ordinal)
        )
        {
            findings.Add("emitted document does not declare OpenAPI 3.1.x");
        }

        RequireObject(root, "info", findings);
        RequireObject(root, "paths", findings);
    }

    private static void RequireObject(JsonElement root, string name, List<string> findings)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            findings.Add($"document '{name}' is missing or is not an object");
        }
    }

    private static void CollectUnresolvedLocalReferences(
        JsonElement node,
        JsonElement root,
        string pointer,
        List<string> findings
    )
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                var childPointer = $"{pointer}/{EscapePointerSegment(property.Name)}";
                if (
                    property.NameEquals("$ref")
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { } reference
                    && reference.StartsWith('#')
                    && !ResolvesLocalReference(reference, root)
                )
                {
                    findings.Add($"unresolved local reference at {childPointer}: {reference}");
                }

                CollectUnresolvedLocalReferences(property.Value, root, childPointer, findings);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
            {
                CollectUnresolvedLocalReferences(item, root, $"{pointer}/{index}", findings);
                index++;
            }
        }
    }

    private static bool ResolvesLocalReference(string reference, JsonElement root)
    {
        if (reference == "#")
        {
            return true;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        string fragment;
        try
        {
            fragment = Uri.UnescapeDataString(reference[2..]);
        }
        catch (UriFormatException)
        {
            return false;
        }

        var current = root;
        foreach (var rawSegment in fragment.Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    return false;
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (
                    !int.TryParse(segment, out var index)
                    || index < 0
                    || index >= current.GetArrayLength()
                )
                {
                    return false;
                }

                current = current[index];
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateSecurityRequirements(JsonElement root, List<string> findings)
    {
        var schemes = new HashSet<string>(StringComparer.Ordinal);
        if (
            root.TryGetProperty("components", out var components)
            && components.ValueKind == JsonValueKind.Object
            && components.TryGetProperty("securitySchemes", out var securitySchemes)
            && securitySchemes.ValueKind == JsonValueKind.Object
        )
        {
            schemes.UnionWith(securitySchemes.EnumerateObject().Select(property => property.Name));
        }

        CollectUnresolvedSecurityRequirements(root, "$", schemes, findings);
    }

    private static void CollectUnresolvedSecurityRequirements(
        JsonElement node,
        string pointer,
        IReadOnlySet<string> schemes,
        List<string> findings
    )
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                var childPointer = $"{pointer}/{EscapePointerSegment(property.Name)}";
                if (
                    property.NameEquals("security")
                    && property.Value.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var requirement in property.Value.EnumerateArray())
                    {
                        if (requirement.ValueKind != JsonValueKind.Object)
                        {
                            findings.Add(
                                $"security requirement at {childPointer} is not an object"
                            );
                            continue;
                        }

                        foreach (var scheme in requirement.EnumerateObject())
                        {
                            if (!schemes.Contains(scheme.Name))
                            {
                                findings.Add(
                                    $"undefined security scheme at {childPointer}: {scheme.Name}"
                                );
                            }
                        }
                    }
                }

                CollectUnresolvedSecurityRequirements(
                    property.Value,
                    childPointer,
                    schemes,
                    findings
                );
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
            {
                CollectUnresolvedSecurityRequirements(
                    item,
                    $"{pointer}/{index}",
                    schemes,
                    findings
                );
                index++;
            }
        }
    }

    private static void ValidateOperations(
        JsonElement sourceRoot,
        JsonElement emittedRoot,
        List<string> findings
    )
    {
        if (
            !emittedRoot.TryGetProperty("paths", out var paths)
            || paths.ValueKind != JsonValueKind.Object
        )
        {
            return;
        }

        var operationIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var pathParameters = ReadParameters(path.Value, emittedRoot);
            foreach (
                var method in path.Value.EnumerateObject().Where(p => _methods.Contains(p.Name))
            )
            {
                if (method.Value.ValueKind != JsonValueKind.Object)
                {
                    findings.Add(
                        $"operation {method.Name.ToUpperInvariant()} {path.Name} is not an object"
                    );
                    continue;
                }

                var operationPointer = $"{method.Name.ToUpperInvariant()} {path.Name}";
                var parameters = new Dictionary<(string Name, string Location), JsonElement>(
                    pathParameters
                );
                foreach (var parameter in ReadParameters(method.Value, emittedRoot))
                {
                    parameters[parameter.Key] = parameter.Value;
                }

                var tokens = RouteTokenRegex()
                    .Matches(path.Name)
                    .Select(match => match.Groups[1].Value)
                    .ToHashSet(StringComparer.Ordinal);
                var pathParameterNames = parameters
                    .Keys.Where(key => key.Location == "path")
                    .Select(key => key.Name)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (
                    var token in tokens.Except(pathParameterNames).Order(StringComparer.Ordinal)
                )
                {
                    findings.Add($"{operationPointer} route token has no path parameter: {token}");
                }
                foreach (
                    var parameter in pathParameterNames.Except(tokens).Order(StringComparer.Ordinal)
                )
                {
                    findings.Add(
                        $"{operationPointer} path parameter has no route token: {parameter}"
                    );
                }

                if (
                    method.Value.TryGetProperty("operationId", out var operationId)
                    && operationId.ValueKind == JsonValueKind.String
                    && operationId.GetString() is { Length: > 0 } id
                )
                {
                    if (!operationIds.TryGetValue(id, out var locations))
                    {
                        locations = [];
                        operationIds[id] = locations;
                    }

                    locations.Add(operationPointer);
                }
            }
        }

        var authoredIds = CollectAuthoredOperationIds(sourceRoot);
        foreach (var duplicate in operationIds.Where(pair => pair.Value.Count > 1))
        {
            var provenance = authoredIds.ContainsKey(duplicate.Key) ? "authored " : string.Empty;
            findings.Add(
                $"duplicate {provenance}emitted operationId '{duplicate.Key}': {string.Join(", ", duplicate.Value)}"
            );
        }
    }

    private static Dictionary<(string Name, string Location), JsonElement> ReadParameters(
        JsonElement owner,
        JsonElement root
    )
    {
        var result = new Dictionary<(string Name, string Location), JsonElement>();
        if (
            !owner.TryGetProperty("parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.Array
        )
        {
            return result;
        }

        foreach (var candidate in parameters.EnumerateArray())
        {
            var parameter = ResolveReference(candidate, root);
            if (
                parameter.ValueKind == JsonValueKind.Object
                && parameter.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && parameter.TryGetProperty("in", out var location)
                && location.ValueKind == JsonValueKind.String
                && name.GetString() is { } parameterName
                && location.GetString() is { } parameterLocation
            )
            {
                result[(parameterName, parameterLocation)] = parameter;
            }
        }

        return result;
    }

    private static JsonElement ResolveReference(JsonElement candidate, JsonElement root)
    {
        if (
            candidate.ValueKind != JsonValueKind.Object
            || !candidate.TryGetProperty("$ref", out var referenceNode)
            || referenceNode.ValueKind != JsonValueKind.String
            || referenceNode.GetString() is not { } reference
            || !reference.StartsWith("#/", StringComparison.Ordinal)
        )
        {
            return candidate;
        }

        var current = root;
        try
        {
            foreach (var rawSegment in Uri.UnescapeDataString(reference[2..]).Split('/'))
            {
                var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
                if (!current.TryGetProperty(segment, out current))
                {
                    return candidate;
                }
            }
        }
        catch (Exception exception)
            when (exception is UriFormatException or InvalidOperationException)
        {
            return candidate;
        }

        return current;
    }

    private static IReadOnlyDictionary<string, int> CollectAuthoredOperationIds(JsonElement root)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var path in paths.EnumerateObject())
        {
            if (path.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (
                var method in path.Value.EnumerateObject().Where(p => _methods.Contains(p.Name))
            )
            {
                if (
                    method.Value.ValueKind == JsonValueKind.Object
                    && method.Value.TryGetProperty("operationId", out var operationId)
                    && operationId.ValueKind == JsonValueKind.String
                    && operationId.GetString() is { Length: > 0 } id
                )
                {
                    result[id] = result.GetValueOrDefault(id) + 1;
                }
            }
        }

        return result;
    }

    private static void ValidateComponentIdentity(
        JsonElement sourceRoot,
        JsonElement emittedRoot,
        List<string> findings
    )
    {
        var sourceSchemas = SchemaNames(sourceRoot);
        var emittedSchemas = SchemaNames(emittedRoot);
        foreach (var leaked in emittedSchemas.Except(sourceSchemas).Order(StringComparer.Ordinal))
        {
            findings.Add($"synthetic/helper component leaked into public schemas: {leaked}");
        }
    }

    private static HashSet<string> SchemaNames(JsonElement root)
    {
        JsonElement schemas;
        if (
            root.TryGetProperty("components", out var components)
            && components.ValueKind == JsonValueKind.Object
            && components.TryGetProperty("schemas", out schemas)
            && schemas.ValueKind == JsonValueKind.Object
        )
        {
            return schemas
                .EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
        }

        if (
            root.TryGetProperty("definitions", out schemas)
            && schemas.ValueKind == JsonValueKind.Object
        )
        {
            return schemas
                .EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    private static string EscapePointerSegment(string value) =>
        value.Replace("~", "~0").Replace("/", "~1");

    [GeneratedRegex("\\{([^{}]+)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex RouteTokenRegex();
}
