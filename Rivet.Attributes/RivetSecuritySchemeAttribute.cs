namespace Rivet;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetSecuritySchemeAttribute(
    string name,
    string type,
    string? description = null,
    string? parameterName = null,
    string? location = null,
    string? scheme = null,
    string? bearerFormat = null,
    string? openIdConnectUrl = null
) : Attribute
{
    public string Name { get; } = name;
    public string Type { get; } = type;
    public string? Description { get; } = description;
    public string? ParameterName { get; } = parameterName;
    public string? Location { get; } = location;
    public string? Scheme { get; } = scheme;
    public string? BearerFormat { get; } = bearerFormat;
    public string? OpenIdConnectUrl { get; } = openIdConnectUrl;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetOAuthFlowAttribute(
    string schemeName,
    string flow,
    string? authorizationUrl,
    string? tokenUrl,
    string? refreshUrl,
    string[] scopeNames,
    string[] scopeDescriptions
) : Attribute
{
    public string SchemeName { get; } = schemeName;
    public string Flow { get; } = flow;
    public string? AuthorizationUrl { get; } = authorizationUrl;
    public string? TokenUrl { get; } = tokenUrl;
    public string? RefreshUrl { get; } = refreshUrl;
    public IReadOnlyList<string> ScopeNames { get; } = scopeNames;
    public IReadOnlyList<string> ScopeDescriptions { get; } = scopeDescriptions;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetGlobalSecurityAttribute(int requirementOrder) : Attribute
{
    public int RequirementOrder { get; } = requirementOrder;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RivetGlobalSecuritySchemeAttribute(
    int requirementOrder,
    string schemeName,
    string[] scopes
) : Attribute
{
    public int RequirementOrder { get; } = requirementOrder;
    public string SchemeName { get; } = schemeName;
    public IReadOnlyList<string> Scopes { get; } = scopes;
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RivetEmptyGlobalSecurityAttribute : Attribute;
