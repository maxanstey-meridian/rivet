using System.Text.Json.Serialization;

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
    string? Summary = null,
    string? Description = null,
    EndpointSecurity? Security = null,
    string? FileContentType = null,
    string? InputTypeName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsFormEncoded = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        TsType? RequestType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<TsEndpointExample>? RequestExamples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsFileEndpoint = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        QueryAuthMetadata? QueryAuth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? BinaryRequestContentType = null,
    // .AcceptsContentType()/.ProducesContentType(): non-JSON media types for
    // JSON-schema'd bodies (text/plain string body, text/html string response).
    // Schema is unchanged — only the declared content-type key (FABLE_ROUNDTRIP #10).
    [property: JsonIgnore(
        Condition = JsonIgnoreCondition.WhenWritingNull
    )] string? RequestContentTypeOverride = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ResponseContentTypeOverride = null
);

/// <summary>
/// Security metadata for an endpoint. null = inherit CLI default.
/// </summary>
public sealed record EndpointSecurity(bool IsAnonymous, string? Scheme = null);

/// <summary>
/// A typed response for a given status code.
/// </summary>
public sealed record TsResponseType(
    int StatusCode,
    TsType? DataType,
    string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<TsEndpointExample>? Examples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<TsResponseHeader>? Headers = null
);

/// <summary>
/// A declared response header (string-typed in v1). Spec-only: Rivet never sets or
/// validates response headers at runtime. Required is an explicit opt-in promise.
/// </summary>
public sealed record TsResponseHeader(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Required = false
);

/// <summary>
/// A parameter to a client function.
/// </summary>
public sealed record TsEndpointParam(
    string Name,
    TsType Type,
    ParamSource Source,
    bool IsOptional = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? BodyPropertyName = null
);

public enum ParamSource
{
    Route,
    Body,
    Query,
    File,
    FormField,
    Header,
}

/// <summary>
/// Query-based auth metadata: the auth token is passed as a query parameter
/// instead of a header, for clients (media players) that cannot set headers.
/// </summary>
public sealed record QueryAuthMetadata(string ParameterName);
