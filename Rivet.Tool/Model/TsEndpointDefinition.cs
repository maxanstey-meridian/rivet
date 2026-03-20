namespace Rivet.Tool.Model;

/// <summary>
/// A typed fetch function: export const foo = (...) => rivetFetch(...)
/// </summary>
public sealed record TsEndpointDefinition(
    string Name,
    string HttpMethod,
    string RouteTemplate,
    IReadOnlyList<TsEndpointParam> Params,
    TsType? ReturnType,
    string ControllerName,
    IReadOnlyList<TsResponseType> Responses,
    string? Description = null,
    EndpointSecurity? Security = null,
    string? FileContentType = null,
    string? InputTypeName = null);

/// <summary>
/// Security metadata for an endpoint. null = inherit CLI default.
/// </summary>
public sealed record EndpointSecurity(bool IsAnonymous, string? Scheme = null);

/// <summary>
/// A typed response for a given status code.
/// </summary>
public sealed record TsResponseType(int StatusCode, TsType? DataType, string? Description = null);

/// <summary>
/// A parameter to a client function.
/// </summary>
public sealed record TsEndpointParam(string Name, TsType Type, ParamSource Source);

public enum ParamSource
{
    Route,
    Body,
    Query,
    File,
    FormField,
}
