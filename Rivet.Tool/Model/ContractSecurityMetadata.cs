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

public enum OAuth2FlowType
{
    Implicit,
    Password,
    ClientCredentials,
    AuthorizationCode,
}
