namespace Rivet.Tool.Emit;

/// <summary>
/// Parsed representation of the --security CLI flag.
/// </summary>
public sealed record SecurityConfig(
    string SchemeName,
    Dictionary<string, object> SchemeDefinition,
    IReadOnlyDictionary<string, Dictionary<string, object>>? AdditionalSchemeDefinitions = null
);

public static class SecurityParser
{
    public static SecurityConfig? ParseMany(IEnumerable<string> specs)
    {
        var parsed = new List<SecurityConfig>();
        foreach (var spec in specs)
        {
            var config =
                Parse(spec)
                ?? throw new SecurityConfigurationException(
                    $"error: invalid --security value '{spec}'; expected bearer[:format], cookie:name, apikey:location:name, or name=<value>"
                );
            parsed.Add(config);
        }

        if (parsed.Count == 0)
        {
            return null;
        }

        var definitions = new Dictionary<string, Dictionary<string, object>>(
            StringComparer.Ordinal
        );
        foreach (var config in parsed)
        {
            if (!definitions.TryAdd(config.SchemeName, config.SchemeDefinition))
            {
                throw new SecurityConfigurationException(
                    $"error: duplicate --security scheme name '{config.SchemeName}'"
                );
            }
        }

        var primary = parsed[0];
        definitions.Remove(primary.SchemeName);
        var additional = definitions;
        return primary with { AdditionalSchemeDefinitions = additional };
    }

    public static SecurityConfig? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var separator = spec.IndexOf('=');
        if (separator >= 0)
        {
            var schemeName = spec[..separator];
            var definitionSpec = spec[(separator + 1)..];
            if (definitionSpec.Contains('='))
            {
                return null;
            }

            var definition = Parse(definitionSpec);
            return !IsValidSchemeName(schemeName) || definition is null
                ? null
                : definition with
                {
                    SchemeName = schemeName,
                };
        }

        var parts = spec.Split(':');
        var kind = parts[0].ToLowerInvariant();

        return kind switch
        {
            "bearer" when parts.Length == 1 => new SecurityConfig(
                "bearer",
                new Dictionary<string, object> { ["type"] = "http", ["scheme"] = "bearer" }
            ),

            "bearer" when parts.Length == 2 && parts[1].Length > 0 => new SecurityConfig(
                "bearer",
                new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["scheme"] = "bearer",
                    ["bearerFormat"] = parts[1].ToUpperInvariant(),
                }
            ),

            "cookie" when parts.Length == 2 && parts[1].Length > 0 => new SecurityConfig(
                "cookieAuth",
                new Dictionary<string, object>
                {
                    ["type"] = "apiKey",
                    ["in"] = "cookie",
                    ["name"] = parts[1],
                }
            ),

            "apikey"
                when parts.Length == 3
                    && parts[2].Length > 0
                    && (
                        parts[1].Equals("query", StringComparison.OrdinalIgnoreCase)
                        || parts[1].Equals("header", StringComparison.OrdinalIgnoreCase)
                        || parts[1].Equals("cookie", StringComparison.OrdinalIgnoreCase)
                    ) => new SecurityConfig(
                "apiKeyAuth",
                new Dictionary<string, object>
                {
                    ["type"] = "apiKey",
                    ["in"] = parts[1].ToLowerInvariant(),
                    ["name"] = parts[2],
                }
            ),

            _ => null,
        };
    }

    internal static bool IsValidSchemeName(string name) =>
        name.Length > 0
        && name.All(character =>
            character
                is >= 'a'
                    and <= 'z'
                    or >= 'A'
                    and <= 'Z'
                    or >= '0'
                    and <= '9'
                    or '.'
                    or '_'
                    or '-'
        );
}

internal sealed class SecurityConfigurationException(string message)
    : InvalidOperationException(message);
