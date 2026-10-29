using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

public sealed record ContractSecurityMetadata(
    IReadOnlyDictionary<string, SecuritySchemeDefinition> Schemes,
    SecurityRequirements? GlobalRequirements = null
);

public abstract record SecuritySchemeDefinition(string? Description);

public sealed record ApiKeySecurityScheme(
    string Name,
    SecurityApiKeyLocation Location,
    string? Description = null
) : SecuritySchemeDefinition(Description);

public sealed record HttpSecurityScheme(
    string Scheme,
    string? BearerFormat = null,
    string? Description = null
) : SecuritySchemeDefinition(Description);

public sealed record OAuth2SecurityScheme(
    IReadOnlyList<OAuth2Flow> Flows,
    string? Description = null
) : SecuritySchemeDefinition(Description);

public sealed record OpenIdConnectSecurityScheme(
    string OpenIdConnectUrl,
    string? Description = null
) : SecuritySchemeDefinition(Description);

public sealed record MutualTlsSecurityScheme(string? Description = null)
    : SecuritySchemeDefinition(Description);

public sealed record OAuth2Flow(
    OAuth2FlowType Type,
    string? AuthorizationUrl,
    string? TokenUrl,
    string? RefreshUrl,
    IReadOnlyDictionary<string, string> Scopes
);

/// <summary>
/// An OpenAPI security requirement list: the caller satisfies any one alternative, and an
/// alternative only by satisfying every scheme in it (with each scheme's scopes). No
/// alternatives is the explicit <c>security: []</c>; an alternative with no schemes (<c>{}</c>)
/// makes credentials optional.
/// </summary>
public sealed record SecurityRequirements(
    [property: JsonRequired] IReadOnlyList<SecurityRequirement> Alternatives
);

public sealed record SecurityRequirement(
    [property: JsonRequired] IReadOnlyList<SecurityRequirementScheme> Schemes
);

public sealed record SecurityRequirementScheme(
    [property: JsonRequired] string Name,
    [property: JsonRequired] IReadOnlyList<string> Scopes
);

public enum SecurityApiKeyLocation
{
    Query,
    Header,
    Cookie,
}

internal static class SecurityApiKeyLocations
{
    /// <summary>
    /// The three locations OpenAPI gives an apiKey, by name and ignoring case, else null.
    /// <c>Enum.TryParse</c> would also take a number ("1" is header, "7" is nothing) and a
    /// comma-joined list. <c>[RivetSecurityScheme]</c> and <c>--security</c> both read it; each
    /// words its own error (RIV1121, RIV2018).
    /// </summary>
    internal static SecurityApiKeyLocation? Parse(string value) =>
        value.ToLowerInvariant() switch
        {
            "query" => SecurityApiKeyLocation.Query,
            "header" => SecurityApiKeyLocation.Header,
            "cookie" => SecurityApiKeyLocation.Cookie,
            _ => null,
        };
}

public enum OAuth2FlowType
{
    Implicit,
    Password,
    ClientCredentials,
    AuthorizationCode,
}

internal static class OAuth2FlowTypes
{
    /// <summary>
    /// A flow's OpenAPI name: its key under <c>flows</c>, and the name <c>[RivetOAuthFlow]</c>
    /// takes. The emitter and the importer's C# writer both write it.
    /// </summary>
    internal static string Name(OAuth2FlowType type) =>
        type switch
        {
            OAuth2FlowType.Implicit => "implicit",
            OAuth2FlowType.Password => "password",
            OAuth2FlowType.ClientCredentials => "clientCredentials",
            OAuth2FlowType.AuthorizationCode => "authorizationCode",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    /// <summary>
    /// The flow an OpenAPI name spells, exactly as <see cref="Name"/> writes it (OpenAPI's
    /// keys are case-sensitive), else null. <c>[RivetOAuthFlow]</c> reads it and words its own
    /// error (RIV1121).
    /// </summary>
    internal static OAuth2FlowType? Parse(string value)
    {
        foreach (var type in Enum.GetValues<OAuth2FlowType>())
        {
            if (string.Equals(Name(type), value, StringComparison.Ordinal))
            {
                return type;
            }
        }
        return null;
    }
}
