using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Parses the --security CLI values into document security metadata. The first value is the
/// document-wide requirement; later values only add scheme definitions.
/// </summary>
public static class SecurityParser
{
    /// <summary>Null when no values were given.</summary>
    public static ContractSecurityMetadata? ParseMany(IReadOnlyList<string> specs)
    {
        if (specs.Count == 0)
        {
            return null;
        }

        var schemes = new Dictionary<string, SecuritySchemeDefinition>(StringComparer.Ordinal);
        string? primary = null;
        foreach (var spec in specs)
        {
            var (name, definition) =
                Parse(spec)
                ?? throw new RivetUserException(
                    $"error: invalid --security value '{spec}'; expected bearer[:format], cookie:name, apikey:location:name, or name=<value>"
                );
            if (!schemes.TryAdd(name, definition))
            {
                throw new RivetUserException(
                    $"error {Diagnostics.DuplicateSecuritySchemeDefinition}: duplicate --security scheme name '{name}'"
                );
            }
            primary ??= name;
        }

        return new ContractSecurityMetadata(
            schemes,
            new SecurityRequirements([
                new SecurityRequirement([new SecurityRequirementScheme(primary!, [])]),
            ])
        );
    }

    private static (string Name, SecuritySchemeDefinition Definition)? Parse(string spec)
    {
        var separator = spec.IndexOf('=');
        if (separator >= 0)
        {
            var schemeName = spec[..separator];
            var definitionSpec = spec[(separator + 1)..];
            return
                !definitionSpec.Contains('=')
                && IsValidSchemeName(schemeName)
                && Parse(definitionSpec) is { } parsed
                ? (schemeName, parsed.Definition)
                : null;
        }

        var parts = spec.Split(':');
        return parts[0].ToLowerInvariant() switch
        {
            "bearer" when parts.Length == 1 => ("bearer", new HttpSecurityScheme("bearer")),
            "bearer" when parts.Length == 2 && parts[1].Length > 0 => (
                "bearer",
                new HttpSecurityScheme("bearer", parts[1].ToUpperInvariant())
            ),
            "cookie" when parts.Length == 2 && parts[1].Length > 0 => (
                "cookieAuth",
                new ApiKeySecurityScheme(parts[1], SecurityApiKeyLocation.Cookie)
            ),
            "apikey"
                when parts.Length == 3
                    && parts[2].Length > 0
                    && Enum.TryParse(
                        parts[1],
                        ignoreCase: true,
                        out SecurityApiKeyLocation location
                    ) => ("apiKeyAuth", new ApiKeySecurityScheme(parts[2], location)),
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
