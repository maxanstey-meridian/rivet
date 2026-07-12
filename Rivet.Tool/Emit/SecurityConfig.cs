using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Parsed representation of the --security CLI flag.
/// </summary>
public sealed record SecurityConfig(
    string SchemeName,
    SecuritySchemeDefinition SchemeDefinition,
    IReadOnlyDictionary<string, SecuritySchemeDefinition>? AdditionalSchemeDefinitions = null
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

        var definitions = new Dictionary<string, SecuritySchemeDefinition>(StringComparer.Ordinal);
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
        return primary with { AdditionalSchemeDefinitions = definitions };
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
                new HttpSecurityScheme("bearer")
            ),
            "bearer" when parts.Length == 2 && parts[1].Length > 0 => new SecurityConfig(
                "bearer",
                new HttpSecurityScheme("bearer", parts[1].ToUpperInvariant())
            ),
            "cookie" when parts.Length == 2 && parts[1].Length > 0 => new SecurityConfig(
                "cookieAuth",
                new ApiKeySecurityScheme(parts[1], SecurityApiKeyLocation.Cookie)
            ),
            "apikey"
                when parts.Length == 3
                    && parts[2].Length > 0
                    && TryParseLocation(parts[1], out var location) => new SecurityConfig(
                "apiKeyAuth",
                new ApiKeySecurityScheme(parts[2], location)
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

    private static bool TryParseLocation(string value, out SecurityApiKeyLocation location) =>
        Enum.TryParse(value, ignoreCase: true, out location);
}

internal sealed class SecurityConfigurationException(string message)
    : InvalidOperationException(message);
