using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

/// <summary>
/// One operation: its route, parameters, request body and responses.
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
    // Schema is unchanged — only the declared content-type key.
    [property: JsonIgnore(
        Condition = JsonIgnoreCondition.WhenWritingNull
    )] string? RequestContentTypeOverride = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ResponseContentTypeOverride = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SecurityRequirements? SecurityRequirements = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<TsMediaTypeContent>? RequestContents = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? RequestBodyRequired = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool RequestBodyPresent = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OpenApiOperationProvenance? Provenance = null
)
{
    /// <summary>
    /// Every type on the endpoint surface, each with its site relative to the endpoint
    /// ("return", "response.200", "param.id", ...): the return type, each response's data
    /// type, content schemas and headers, then the params, request type and request contents.
    /// </summary>
    public IEnumerable<(string Site, TsType Type)> AllTypes()
    {
        if (ReturnType is not null)
        {
            yield return ("return", ReturnType);
        }

        foreach (var response in Responses)
        {
            if (response.DataType is not null)
            {
                yield return ($"response.{response.StatusCode}", response.DataType);
            }
            foreach (var content in response.Contents ?? [])
            {
                if (content.Schema is not null)
                {
                    yield return (
                        $"response.{response.EffectiveStatusKey}.content.{content.MediaType}",
                        content.Schema
                    );
                }
            }
            foreach (var header in response.Headers ?? [])
            {
                yield return (
                    $"response.{response.EffectiveStatusKey}.header.{header.Name}",
                    header.Type
                );
            }
        }

        foreach (var param in Params)
        {
            yield return ($"param.{param.Name}", param.Type);
        }

        if (RequestType is not null)
        {
            yield return ("requestType", RequestType);
        }

        foreach (var content in RequestContents ?? [])
        {
            if (content.Schema is not null)
            {
                yield return ($"requestContent.{content.MediaType}", content.Schema);
            }
        }
    }
}

/// <summary>
/// Security metadata for an endpoint. null = inherit CLI default.
/// </summary>
public sealed record EndpointSecurity(bool IsAnonymous, string? Scheme = null);

/// <summary>
/// A typed response for a given status code.
/// </summary>
public sealed record TsResponseType(
    // 0 means "no numeric status" (default/range keys) and is absent on the wire.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int StatusCode,
    TsType? DataType,
    string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<TsEndpointExample>? Examples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<TsResponseHeader>? Headers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<TsMediaTypeContent>? Contents = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StatusKey = null
)
{
    [JsonIgnore]
    public string EffectiveStatusKey => StatusKey ?? StatusCode.ToString();
}

public sealed record TsMediaTypeContent(
    string MediaType,
    TsType? Schema,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsBinary = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SchemaType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsFormatSpecified = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SchemaDescription = null
);

/// <summary>
/// A declared response header. Spec-only: Rivet never sets or
/// validates response headers at runtime. Required is an explicit opt-in promise.
/// </summary>
public sealed record TsResponseHeader(
    string Name,
    [property: JsonRequired] TsType Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool Required = false,
    [property: JsonPropertyName("deprecated")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsDeprecated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? SchemaExamples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? Example = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? Examples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Style = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Explode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool AllowReserved = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool AllowEmptyValue = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ContentType = null
);

/// <summary>
/// A parameter to a client function.
/// </summary>
public sealed record TsEndpointParam(
    string Name,
    [property: JsonRequired] TsType Type,
    ParamSource Source,
    bool IsOptional = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? BodyPropertyName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsDeprecated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DefaultValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        TsPropertyConstraints? Constraints = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? SchemaExamples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? Example = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? Examples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Style = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Explode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SchemaType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool IsFormatSpecified = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool AllowEmptyValue = false
);

public enum ParamSource
{
    Route,
    Body,
    Query,
    File,
    FormField,
    Header,
    Cookie,
}

/// <summary>
/// Query-based auth metadata: the auth token is passed as a query parameter
/// instead of a header, for clients (media players) that cannot set headers.
/// </summary>
public sealed record QueryAuthMetadata(string ParameterName);
