namespace Rivet.Tool.Emit;

/// <summary>
/// Parsed representation of the --security CLI flag.
/// </summary>
public sealed record SecurityConfig(string SchemeName, Dictionary<string, object> SchemeDefinition);

public static class SecurityParser
{
    public static SecurityConfig? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        var parts = spec.Split(':');

        return parts[0] switch
        {
            "bearer" when parts.Length == 1 =>
                new SecurityConfig("bearer", new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["scheme"] = "bearer",
                }),

            "bearer" when parts.Length == 2 =>
                new SecurityConfig("bearer", new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["scheme"] = "bearer",
                    ["bearerFormat"] = parts[1].ToUpperInvariant(),
                }),

            "cookie" when parts.Length == 2 =>
                new SecurityConfig("cookieAuth", new Dictionary<string, object>
                {
                    ["type"] = "apiKey",
                    ["in"] = "cookie",
                    ["name"] = parts[1],
                }),

            "apikey" when parts.Length == 3 =>
                new SecurityConfig("apiKeyAuth", new Dictionary<string, object>
                {
                    ["type"] = "apiKey",
                    ["in"] = parts[1],
                    ["name"] = parts[2],
                }),

            _ => null,
        };
    }
}
