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

public sealed record SecurityRequirements(IReadOnlyList<SecurityRequirement> Alternatives);

public sealed record SecurityRequirement(IReadOnlyList<SecurityRequirementScheme> Schemes);

public sealed record SecurityRequirementScheme(string Name, IReadOnlyList<string> Scopes);

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
