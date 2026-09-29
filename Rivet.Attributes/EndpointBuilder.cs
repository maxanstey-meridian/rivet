namespace Rivet;

using System.Collections.Immutable;
using Microsoft.Net.Http.Headers;

/// <summary>
/// Describes an additional (non-success) response declared via .Returns&lt;T&gt;().
/// </summary>
public sealed record RouteErrorResponse(
    int StatusCode,
    Type? ResponseType,
    string? Description,
    string? StatusKey = null
)
{
    public string EffectiveStatusKey => StatusKey ?? StatusCode.ToString();
}

/// <summary>
/// Describes a response header declared via .WithResponseHeader(). A null StatusCode
/// targets the endpoint's success status. Spec-only: Rivet never sets or validates
/// response headers at runtime — emitting them is handler code.
/// </summary>
public sealed record RouteResponseHeader(
    int? StatusCode,
    string Name,
    string? Description,
    bool Required,
    string? StatusKey = null,
    Type? HeaderType = null,
    string? SchemaType = null,
    string? Format = null,
    string? SchemaExamplesJson = null,
    string? ExampleJson = null,
    string? ExamplesJson = null,
    bool Deprecated = false,
    string? Style = null,
    bool? Explode = null,
    bool AllowReserved = false,
    bool AllowEmptyValue = false,
    string? ContentType = null
);

/// <summary>
/// Everything a route definition's builder methods record. Immutable, so a converted
/// definition (<c>.Accepts&lt;T&gt;()</c>) takes the whole state in one assignment.
/// </summary>
internal sealed record RouteState(int SuccessStatus)
{
    public bool StatusSet { get; init; }
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public bool Anonymous { get; init; }
    public string? SecurityScheme { get; init; }
    public string? FileContentType { get; init; }
    public bool AcceptsFile { get; init; }
    public bool FormEncoded { get; init; }
    public string? BinaryRequestContentType { get; init; }
    public string? RequestContentType { get; init; }
    public string? ResponseContentType { get; init; }
    public string? QueryAuthParameterName { get; init; }
    public ImmutableList<RouteErrorResponse> ErrorResponses { get; init; } = [];
    public ImmutableList<RouteResponseHeader> ResponseHeaders { get; init; } = [];
    public ImmutableList<RouteResponseContent> ResponseContents { get; init; } = [];
    public string? SuccessStatusKey { get; init; }
    public bool SuppressImplicitResponse { get; init; }
}

/// <summary>
/// Shared builder state and fluent methods for all RouteDefinition variants.
/// Uses CRTP so each builder method returns the concrete type for chaining.
/// </summary>
public abstract class RouteDefinitionBase<TSelf>
    where TSelf : RouteDefinitionBase<TSelf>
{
    // Definitions live in shared static readonly fields, so once a terminal has published
    // the contract a later builder call would silently change it for every request.
    // Mutation and publication share one gate, and mutation after publication throws.
    private readonly object _gate = new();
    private RouteState _state;
    private volatile EndpointContract? _publishedContract;

    /// <summary>The HTTP method (GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS).</summary>
    public string Method { get; }

    /// <summary>The route template from the contract definition.</summary>
    public string Route { get; }

    public string? EndpointSummary => _state.Summary;
    public string? EndpointDescription => _state.Description;
    public bool IsAnonymous => _state.Anonymous;
    public string? SecurityScheme => _state.SecurityScheme;
    public string? FileContentType => _state.FileContentType;
    public bool IsFormEncoded => _state.FormEncoded;
    public string? BinaryRequestContentType => _state.BinaryRequestContentType;
    public string? RequestContentType => _state.RequestContentType;
    public string? ResponseContentType => _state.ResponseContentType;
    public bool IsQueryAuth => _state.QueryAuthParameterName is not null;
    public string? QueryAuthParameterName => _state.QueryAuthParameterName;
    public IReadOnlyList<RouteErrorResponse>? RouteErrorResponses =>
        _state.ErrorResponses.IsEmpty ? null : _state.ErrorResponses;
    public IReadOnlyList<RouteResponseHeader>? ResponseHeaders =>
        _state.ResponseHeaders.IsEmpty ? null : _state.ResponseHeaders;

    /// <summary>The resolved success status code for this endpoint.</summary>
    public int SuccessStatusCode => _state.SuccessStatus;

    /// <summary>The resolved success status code used during publication.</summary>
    protected int SuccessStatus => _state.SuccessStatus;

    protected RouteDefinitionBase(string method, string route, int defaultStatus)
        : this(method, route, new RouteState(defaultStatus)) { }

    private protected RouteDefinitionBase(string method, string route, RouteState state)
    {
        Method = method;
        Route = route;
        _state = state;
    }

    /// <summary>The builder state, for handing to a converted definition. Throws once published.</summary>
    private protected RouteState CurrentState()
    {
        lock (_gate)
        {
            ThrowIfPublished();
            return _state;
        }
    }

    private TSelf Mutate(Func<RouteState, RouteState> change)
    {
        lock (_gate)
        {
            ThrowIfPublished();
            _state = change(_state);
        }

        return (TSelf)this;
    }

    private void ThrowIfPublished()
    {
        if (_publishedContract is not null)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: contract definitions are immutable once published — "
                    + "builder methods cannot be called after a terminal publishes the endpoint. "
                    + "Configure the definition fully in its static readonly initializer."
            );
        }
    }

    internal EndpointContract Publish(Type? successPayloadType)
    {
        if (_publishedContract is { } published)
        {
            return published;
        }

        lock (_gate)
        {
            return _publishedContract ??= BuildContract(_state, successPayloadType);
        }
    }

    private EndpointContract BuildContract(RouteState state, Type? successPayloadType)
    {
        if (state.SuccessStatus is < 100 or > 599)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: success status {state.SuccessStatus} is not a valid HTTP status code."
            );
        }

        if (state.ErrorResponses.Any(response => GetExactStatus(response) == state.SuccessStatus))
        {
            throw new InvalidOperationException(
                $"Status {state.SuccessStatus} is declared as both the success status and via .Returns() — "
                    + "success and error responses cannot share a status."
            );
        }

        var success = state.SuppressImplicitResponse
            ? null
            : BuildResponse(
                state,
                state.SuccessStatusKey ?? state.SuccessStatus.ToString(),
                state.SuccessStatus,
                successPayloadType,
                isSuccess: true
            );
        var exact = new Dictionary<int, ResponseContract>();
        var ranges = new Dictionary<int, ResponseContract>();
        ResponseContract? fallback = null;

        foreach (var response in state.ErrorResponses)
        {
            var statusKey = response.EffectiveStatusKey;
            var exactStatus = GetExactStatus(response);
            if (exactStatus is not null)
            {
                exact.Add(
                    exactStatus.Value,
                    BuildResponse(
                        state,
                        statusKey,
                        exactStatus.Value,
                        response.ResponseType,
                        isSuccess: false
                    )
                );
                continue;
            }

            if (statusKey.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                fallback = BuildResponse(
                    state,
                    statusKey,
                    null,
                    response.ResponseType,
                    isSuccess: false
                );
                continue;
            }

            if (
                statusKey.Length == 3
                && statusKey[0] is >= '1' and <= '5'
                && statusKey[1..].Equals("XX", StringComparison.OrdinalIgnoreCase)
            )
            {
                ranges.Add(
                    statusKey[0] - '0',
                    BuildResponse(state, statusKey, null, response.ResponseType, isSuccess: false)
                );
                continue;
            }

            throw new InvalidOperationException(
                $"{Method} {Route}: response status key '{statusKey}' is not an exact status, nXX range, or default."
            );
        }

        return new EndpointContract(
            Method,
            Route,
            success,
            new ResponseSet(exact, ranges, fallback)
        );
    }

    private ResponseContract BuildResponse(
        RouteState state,
        string statusKey,
        int? statusCode,
        Type? payloadType,
        bool isSuccess
    )
    {
        var matchingContents = state
            .ResponseContents.Where(content =>
                content.StatusKey.Equals(statusKey, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();
        var representations = matchingContents
            .GroupBy(content => content.MediaType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new ResponseRepresentation(group.Key, group.Last().IsBinary),
                StringComparer.OrdinalIgnoreCase
            );

        if (state.FileContentType is not null && isSuccess)
        {
            representations[state.FileContentType] = new ResponseRepresentation(
                state.FileContentType,
                true
            );
        }
        else if (state.ResponseContentType is not null && isSuccess)
        {
            representations[state.ResponseContentType] = new ResponseRepresentation(
                state.ResponseContentType,
                false
            );
        }
        else if (payloadType is not null && representations.Count == 0)
        {
            representations["application/json"] = new ResponseRepresentation(
                "application/json",
                false
            );
        }

        var contentPayloadTypes = matchingContents
            .Where(content => content.PayloadType is not null)
            .Select(content => content.PayloadType!)
            .Distinct()
            .ToArray();
        if (contentPayloadTypes.Length > 1)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: response status '{statusKey}' declares multiple content payload types: "
                    + $"{string.Join(", ", contentPayloadTypes.Select(type => $"'{type.FullName}'"))}."
            );
        }

        if (
            payloadType is not null
            && contentPayloadTypes is [var declaredContentType]
            && declaredContentType != payloadType
        )
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: response status '{statusKey}' declares payload type "
                    + $"'{payloadType.FullName}', but its content declares '{declaredContentType.FullName}'."
            );
        }

        if (payloadType is null && contentPayloadTypes is [var contentPayloadType])
        {
            payloadType = contentPayloadType;
        }

        return new ResponseContract(
            statusKey,
            statusCode,
            payloadType,
            isSuccess ? state.ResponseContentType : null,
            representations
        );
    }

    public TSelf Summary(string summary) => Mutate(state => state with { Summary = summary });

    public TSelf Description(string description) =>
        Mutate(state => state with { Description = description });

    public TSelf Status(int statusCode) =>
        Mutate(state =>
        {
            if (statusCode is < 100 or > 599)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: success status {statusCode} is not a valid HTTP status code."
                );
            }

            if (state.StatusSet)
            {
                throw new InvalidOperationException(
                    $"Status already set to {state.SuccessStatus} — cannot set to {statusCode}. Call .Status() only once."
                );
            }

            if (state.ErrorResponses.Any(response => GetExactStatus(response) == statusCode))
            {
                throw new InvalidOperationException(
                    $"Status {statusCode} is already declared via .Returns() — success and error responses cannot share a status."
                );
            }

            return state with
            {
                SuccessStatus = statusCode,
                StatusSet = true,
            };
        });

    /// <summary>
    /// Carries the source OpenAPI response key and primary response description through
    /// generated C#. Concrete runtime status behavior remains controlled by <see cref="Status"/>.
    /// </summary>
    public TSelf StatusKey(string statusKey, string? description = null)
    {
        _ = description;
        return Mutate(state => state with { SuccessStatusKey = statusKey });
    }

    /// <summary>
    /// Suppresses Rivet's authored method-default response. Intended for imported
    /// operations whose source response set contains no concrete success response.
    /// </summary>
    public TSelf SuppressImplicitResponse() =>
        Mutate(state => state with { SuppressImplicitResponse = true });

    public TSelf FormEncoded() =>
        Mutate(state =>
        {
            if (state.BinaryRequestContentType is not null)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: .FormEncoded() cannot be combined with .AcceptsBinary() — "
                        + "a request body is either raw binary or form-encoded, not both."
                );
            }

            return state with
            {
                FormEncoded = true,
            };
        });

    /// <summary>
    /// Declares the request body's media type when it is not application/json
    /// (e.g. "text/plain" for a string body). The body SCHEMA is unchanged —
    /// this overrides only the content-type key the spec declares. For raw
    /// binary bodies use .AcceptsBinary(); for forms use .FormEncoded().
    /// </summary>
    public TSelf AcceptsContentType(string contentType) =>
        Mutate(state =>
        {
            if (state.FormEncoded || state.BinaryRequestContentType is not null)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: .AcceptsContentType() cannot be combined with "
                        + ".FormEncoded() or .AcceptsBinary() — those already declare the body media type."
                );
            }

            return state with
            {
                RequestContentType = contentType,
            };
        });

    /// <summary>
    /// Declares the success response's media type when it is not
    /// application/json (e.g. "text/html" for a string response). The response
    /// SCHEMA is unchanged — this overrides only the content-type key the spec
    /// declares. For binary/file responses use .ProducesFile().
    /// </summary>
    public TSelf ProducesContentType(string contentType) =>
        Mutate(state =>
        {
            if (state.FileContentType is not null)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: .ProducesContentType() cannot be combined with .ProducesFile() — "
                        + "the file content type already declares the response media type."
                );
            }

            return state with
            {
                ResponseContentType = contentType,
            };
        });

    public TSelf Returns<TResponse>(int statusCode) => Returns<TResponse>(statusCode, null);

    public TSelf Returns<TResponse>(int statusCode, string? description) =>
        AddErrorResponse(new RouteErrorResponse(statusCode, typeof(TResponse), description));

    public TSelf Returns<TResponse>(string statusKey, string? description = null) =>
        AddErrorResponse(new RouteErrorResponse(0, typeof(TResponse), description, statusKey));

    public TSelf Returns(int statusCode) => Returns(statusCode, null);

    public TSelf Returns(int statusCode, string? description) =>
        AddErrorResponse(new RouteErrorResponse(statusCode, null, description));

    public TSelf Returns(string statusKey, string? description = null) =>
        AddErrorResponse(new RouteErrorResponse(0, null, description, statusKey));

    private TSelf AddErrorResponse(RouteErrorResponse response) =>
        Mutate(state =>
        {
            var exactStatus = GetExactStatus(response);

            if (
                response.StatusKey is { } statusKey
                && exactStatus is null
                && !statusKey.Equals("default", StringComparison.OrdinalIgnoreCase)
                && !IsRangeStatusKey(statusKey)
            )
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: response status key '{statusKey}' is not an exact status, nXX range, or default."
                );
            }

            if (exactStatus is < 100 or > 599)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: response status {exactStatus} is not a valid HTTP status code."
                );
            }

            if (state.StatusSet && exactStatus == state.SuccessStatus)
            {
                throw new InvalidOperationException(
                    $"Status {exactStatus} is already declared as the success status — success and error responses cannot share a status."
                );
            }

            if (
                state.ErrorResponses.Any(existing =>
                    exactStatus is not null
                        ? GetExactStatus(existing) == exactStatus
                        : GetExactStatus(existing) is null
                            && string.Equals(
                                existing.EffectiveStatusKey,
                                response.EffectiveStatusKey,
                                StringComparison.OrdinalIgnoreCase
                            )
                )
            )
            {
                throw new InvalidOperationException(
                    $"Status {response.EffectiveStatusKey} is already declared via .Returns() — a status carries a single response shape. "
                        + "For multiple shapes at one status, declare a [RivetUnion] type and return it once."
                );
            }

            return state with
            {
                ErrorResponses = state.ErrorResponses.Add(response),
            };
        });

    private static int? GetExactStatus(RouteErrorResponse response)
    {
        if (response.StatusKey is null)
        {
            return response.StatusCode;
        }

        return response.StatusKey is [>= '1' and <= '5', >= '0' and <= '9', >= '0' and <= '9']
            ? int.Parse(response.StatusKey)
            : null;
    }

    private static bool IsRangeStatusKey(string statusKey) =>
        statusKey is [>= '1' and <= '5', 'X' or 'x', 'X' or 'x'];

    /// <summary>
    /// Declares a response header on the given status code (contract concept).
    /// Spec-only: Rivet never sets or validates response headers at runtime —
    /// emitting Location/ETag/... is handler code. <paramref name="required"/> is an
    /// explicit opt-in promise that the header is always present.
    /// </summary>
    public TSelf WithResponseHeader(
        int statusCode,
        string name,
        string? description = null,
        bool required = false
    ) =>
        AddResponseHeader(
            new RouteResponseHeader(
                statusCode,
                name,
                description,
                required,
                HeaderType: typeof(string)
            )
        );

    public TSelf WithResponseHeader<THeader>(
        int statusCode,
        string name,
        string? description = null,
        bool required = false,
        string? schemaType = null,
        string? format = null,
        string? schemaExamplesJson = null,
        string? exampleJson = null,
        string? examplesJson = null,
        bool deprecated = false,
        string? style = null,
        bool? explode = null,
        bool allowReserved = false,
        bool allowEmptyValue = false,
        string? contentType = null
    ) =>
        AddResponseHeader(
            new RouteResponseHeader(
                statusCode,
                name,
                description,
                required,
                HeaderType: typeof(THeader),
                SchemaType: schemaType,
                Format: format,
                SchemaExamplesJson: schemaExamplesJson,
                ExampleJson: exampleJson,
                ExamplesJson: examplesJson,
                Deprecated: deprecated,
                Style: style,
                Explode: explode,
                AllowReserved: allowReserved,
                AllowEmptyValue: allowEmptyValue,
                ContentType: contentType
            )
        );

    public TSelf WithResponseHeaderKey(
        string statusKey,
        string name,
        string? description = null,
        bool required = false
    )
    {
        return AddResponseHeader(
            new RouteResponseHeader(null, name, description, required, statusKey, typeof(string))
        );
    }

    public TSelf WithResponseHeaderKey<THeader>(
        string statusKey,
        string name,
        string? description = null,
        bool required = false,
        string? schemaType = null,
        string? format = null,
        string? schemaExamplesJson = null,
        string? exampleJson = null,
        string? examplesJson = null,
        bool deprecated = false,
        string? style = null,
        bool? explode = null,
        bool allowReserved = false,
        bool allowEmptyValue = false,
        string? contentType = null
    ) =>
        AddResponseHeader(
            new RouteResponseHeader(
                null,
                name,
                description,
                required,
                statusKey,
                typeof(THeader),
                schemaType,
                format,
                schemaExamplesJson,
                exampleJson,
                examplesJson,
                deprecated,
                style,
                explode,
                allowReserved,
                allowEmptyValue,
                contentType
            )
        );

    /// <summary>
    /// Declares a response header on the endpoint's success status (contract concept).
    /// See <see cref="WithResponseHeader(int, string, string?, bool)"/>.
    /// </summary>
    public TSelf WithResponseHeader(
        string name,
        string? description = null,
        bool required = false
    ) =>
        AddResponseHeader(
            new RouteResponseHeader(null, name, description, required, HeaderType: typeof(string))
        );

    public TSelf WithResponseHeader<THeader>(
        string name,
        string? description = null,
        bool required = false,
        string? schemaType = null,
        string? format = null,
        string? schemaExamplesJson = null,
        string? exampleJson = null,
        string? examplesJson = null,
        bool deprecated = false,
        string? style = null,
        bool? explode = null,
        bool allowReserved = false,
        bool allowEmptyValue = false,
        string? contentType = null
    ) =>
        AddResponseHeader(
            new RouteResponseHeader(
                null,
                name,
                description,
                required,
                HeaderType: typeof(THeader),
                SchemaType: schemaType,
                Format: format,
                SchemaExamplesJson: schemaExamplesJson,
                ExampleJson: exampleJson,
                ExamplesJson: examplesJson,
                Deprecated: deprecated,
                Style: style,
                Explode: explode,
                AllowReserved: allowReserved,
                AllowEmptyValue: allowEmptyValue,
                ContentType: contentType
            )
        );

    private TSelf AddResponseHeader(RouteResponseHeader header) =>
        Mutate(state =>
        {
            if (
                state.ResponseHeaders.Any(existing =>
                    existing.StatusCode == header.StatusCode
                    && string.Equals(
                        existing.StatusKey,
                        header.StatusKey,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && string.Equals(existing.Name, header.Name, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                throw new InvalidOperationException(
                    $"Response header '{header.Name}' is already declared for this status via .WithResponseHeader() — declare each header only once per status."
                );
            }

            return state with
            {
                ResponseHeaders = state.ResponseHeaders.Add(header),
            };
        });

    public TSelf RequestExampleJson(
        string json,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        // Example metadata is consumed by the Roslyn analyzer, not at runtime.
        _ = json;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return Mutate(static state => state);
    }

    public TSelf RequestExampleRef(
        string componentExampleId,
        string resolvedJson,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        _ = componentExampleId;
        _ = resolvedJson;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return Mutate(static state => state);
    }

    public TSelf ResponseExampleJson(
        int statusCode,
        string json,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        _ = statusCode;
        _ = json;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return Mutate(static state => state);
    }

    public TSelf ResponseExampleJson(
        string statusKey,
        string json,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        _ = statusKey;
        _ = json;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return Mutate(static state => state);
    }

    public TSelf ResponseExampleRef(
        int statusCode,
        string componentExampleId,
        string resolvedJson,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        _ = statusCode;
        _ = componentExampleId;
        _ = resolvedJson;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return Mutate(static state => state);
    }

    public TSelf ResponseExampleRef(
        string statusKey,
        string componentExampleId,
        string resolvedJson,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        _ = statusKey;
        _ = componentExampleId;
        _ = resolvedJson;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return Mutate(static state => state);
    }

    public TSelf Anonymous() => Mutate(static state => state with { Anonymous = true });

    public TSelf Secure(string scheme) => Mutate(state => state with { SecurityScheme = scheme });

    public TSelf SecurityRequirements() => Mutate(static state => state);

    public TSelf SecurityRequirement(int requirementOrder)
    {
        _ = requirementOrder;
        return Mutate(static state => state);
    }

    public TSelf SecurityRequirement(int requirementOrder, string scheme, string? scope = null)
    {
        _ = requirementOrder;
        _ = scheme;
        _ = scope;
        return Mutate(static state => state);
    }

    public TSelf RequestContent<T>(
        string mediaType,
        string? schemaRef = null,
        string? schemaType = null,
        string? format = null
    )
    {
        _ = mediaType;
        _ = schemaRef;
        _ = schemaType;
        _ = format;
        return Mutate(static state => state);
    }

    public TSelf RequestContent(string mediaType)
    {
        _ = mediaType;
        return Mutate(static state => state);
    }

    public TSelf RequestBinaryContent(string mediaType)
    {
        _ = mediaType;
        return Mutate(static state => state);
    }

    public TSelf RequestBodyRequired(bool required)
    {
        _ = required;
        return Mutate(static state => state);
    }

    public TSelf RequestBody() => Mutate(static state => state);

    public TSelf Parameter<T>(
        string name,
        string location,
        bool required,
        string? schemaType = null,
        string? format = null,
        string? metadataJson = null,
        string? schemaRef = null
    )
    {
        _ = name;
        _ = location;
        _ = required;
        _ = schemaType;
        _ = format;
        _ = metadataJson;
        _ = schemaRef;
        return Mutate(static state => state);
    }

    public TSelf ResponseContent<T>(
        int statusCode,
        string mediaType,
        string? schemaRef = null,
        string? schemaType = null,
        string? format = null,
        string? schemaDescription = null
    )
    {
        _ = schemaRef;
        _ = schemaType;
        _ = format;
        _ = schemaDescription;
        return AddResponseContent(statusCode.ToString(), mediaType, typeof(T), isBinary: false);
    }

    public TSelf ResponseContent<T>(
        string statusKey,
        string mediaType,
        string? schemaRef = null,
        string? schemaType = null,
        string? format = null,
        string? schemaDescription = null
    )
    {
        _ = schemaRef;
        _ = schemaType;
        _ = format;
        _ = schemaDescription;
        return AddResponseContent(statusKey, mediaType, typeof(T), isBinary: false);
    }

    public TSelf ResponseContent(int statusCode, string mediaType) =>
        AddResponseContent(statusCode.ToString(), mediaType, null, isBinary: false);

    public TSelf ResponseContent(string statusKey, string mediaType) =>
        AddResponseContent(statusKey, mediaType, null, isBinary: false);

    public TSelf ResponseBinaryContent(int statusCode, string mediaType) =>
        AddResponseContent(statusCode.ToString(), mediaType, null, isBinary: true);

    public TSelf ResponseBinaryContent(string statusKey, string mediaType) =>
        AddResponseContent(statusKey, mediaType, null, isBinary: true);

    private TSelf AddResponseContent(
        string statusKey,
        string mediaType,
        Type? payloadType,
        bool isBinary
    ) =>
        Mutate(state =>
            state with
            {
                ResponseContents = state.ResponseContents.Add(
                    new RouteResponseContent(statusKey, mediaType, payloadType, isBinary)
                ),
            }
        );

    /// <summary>
    /// Opts this endpoint into query-based authentication, where the auth token is passed
    /// as a query parameter instead of a header. Primarily intended for media players
    /// (ExoPlayer, HLS.js) that cannot inject custom headers on segment requests.
    /// </summary>
    public TSelf QueryAuth(string parameterName = "token") =>
        Mutate(state => state with { QueryAuthParameterName = parameterName });

    /// <summary>
    /// Marks this endpoint as returning a file download instead of JSON.
    /// The generated TS client returns Blob; the OpenAPI spec emits the given content type with format: binary.
    /// </summary>
    public TSelf ProducesFile(string contentType = "application/octet-stream") =>
        Mutate(state =>
        {
            if (
                string.IsNullOrWhiteSpace(contentType)
                || !MediaTypeHeaderValue.TryParse(contentType, out _)
            )
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: .ProducesFile() requires a valid content type."
                );
            }

            return state with
            {
                FileContentType = contentType,
            };
        });

    /// <summary>
    /// Marks this endpoint as accepting a file upload (multipart/form-data).
    /// The generated TS client will accept a File parameter.
    /// </summary>
    public TSelf AcceptsFile() =>
        Mutate(state =>
        {
            if (state.BinaryRequestContentType is not null)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: .AcceptsFile() cannot be combined with .AcceptsBinary() — "
                        + "a request body is either raw binary or multipart/form-data, not both."
                );
            }

            return state with
            {
                AcceptsFile = true,
            };
        });

    /// <summary>
    /// Marks this endpoint as accepting a raw binary request body (application/octet-stream
    /// unless overridden). The body is the raw bytes; binding/reading the request stream is
    /// host code — Rivet never touches it at runtime. Rivet emits the binary requestBody
    /// into the OpenAPI spec, and on contract definitions the TInput properties lower to
    /// route/query parameters instead of a JSON body.
    /// </summary>
    public TSelf AcceptsBinary(string contentType = "application/octet-stream") =>
        Mutate(state =>
        {
            if (state.AcceptsFile)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: .AcceptsBinary() cannot be combined with .AcceptsFile() — "
                        + "a request body is either raw binary or multipart/form-data, not both."
                );
            }

            if (state.FormEncoded)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: .AcceptsBinary() cannot be combined with .FormEncoded() — "
                        + "a request body is either raw binary or form-encoded, not both."
                );
            }

            return state with
            {
                BinaryRequestContentType = contentType,
            };
        });
}

/// <summary>
/// Route definition for endpoints with both input and output types.
/// Roslyn reads the chain at generation time. Bind publishes the contract for terminal use.
/// </summary>
public sealed class RouteDefinition<TInput, TOutput>
    : RouteDefinitionBase<RouteDefinition<TInput, TOutput>>
{
    internal RouteDefinition(string method = "GET", string route = "", int defaultStatus = 200)
        : base(method, route, defaultStatus) { }

    public BoundRouteDefinition<TOutput> Bind(TInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new BoundRouteDefinition<TOutput>(Publish(typeof(TOutput)));
    }

    public static implicit operator Define(RouteDefinition<TInput, TOutput> _) => default!;
}

/// <summary>
/// Route definition for endpoints with output only (no input type).
/// </summary>
public sealed class RouteDefinition<TOutput> : RouteDefinitionBase<RouteDefinition<TOutput>>
{
    internal RouteDefinition(string method = "GET", string route = "", int defaultStatus = 200)
        : base(method, route, defaultStatus) { }

    public RivetResult Success(TOutput payload) =>
        RivetTerminal.Success(Publish(typeof(TOutput)), payload);

    public RivetResult Error(int statusCode) =>
        RivetTerminal.Error(Publish(typeof(TOutput)), statusCode);

    public RivetResult Error<TError>(int statusCode, TError payload) =>
        RivetTerminal.Error(Publish(typeof(TOutput)), statusCode, payload);

    // Retained for callers compiled against the original five-parameter signature.
    public RivetResult File(
        byte[] content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
    ) =>
        RivetTerminal.File(
            Publish(typeof(TOutput)),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null
    ) =>
        RivetTerminal.File(
            Publish(typeof(TOutput)),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType
        );

    // Retained for callers compiled against the original five-parameter signature.
    public RivetResult File(
        Stream content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
    ) =>
        RivetTerminal.File(
            Publish(typeof(TOutput)),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        Stream content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null
    ) =>
        RivetTerminal.File(
            Publish(typeof(TOutput)),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType
        );

    // Retained for callers compiled against the original five-parameter signature.
    public RivetResult File(
        string physicalPath,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(typeof(TOutput)),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(typeof(TOutput)),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType
        );

    public static implicit operator Define(RouteDefinition<TOutput> _) => default!;
}

/// <summary>
/// Route definition for endpoints with input only (no typed output — e.g. PUT/PATCH returning 204).
/// Chain from void definition via .Accepts&lt;T&gt;().
/// </summary>
public sealed class InputRouteDefinition<TInput> : RouteDefinitionBase<InputRouteDefinition<TInput>>
{
    internal InputRouteDefinition(string method, string route, RouteState state)
        : base(method, route, state) { }

    public BoundRouteDefinition Bind(TInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new BoundRouteDefinition(Publish(null));
    }

    public static implicit operator Define(InputRouteDefinition<TInput> _) => default!;
}

/// <summary>
/// Route definition for endpoints with no typed input or output.
/// </summary>
public sealed class RouteDefinition : RouteDefinitionBase<RouteDefinition>
{
    internal RouteDefinition(string method = "GET", string route = "", int defaultStatus = 200)
        : base(method, route, defaultStatus) { }

    public RivetResult Success() => RivetTerminal.Success(Publish(null));

    public RivetResult Error(int statusCode) => RivetTerminal.Error(Publish(null), statusCode);

    public RivetResult Error<TError>(int statusCode, TError payload) =>
        RivetTerminal.Error(Publish(null), statusCode, payload);

    // Retained for callers compiled against the original five-parameter signature.
    public RivetResult File(
        byte[] content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
    ) =>
        RivetTerminal.File(
            Publish(null),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null
    ) =>
        RivetTerminal.File(
            Publish(null),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType
        );

    // Retained for callers compiled against the original five-parameter signature.
    public RivetResult File(
        Stream content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
    ) =>
        RivetTerminal.File(
            Publish(null),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        Stream content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null
    ) =>
        RivetTerminal.File(
            Publish(null),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType
        );

    // Retained for callers compiled against the original five-parameter signature.
    public RivetResult File(
        string physicalPath,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(null),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(null),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType
        );

    /// <summary>
    /// Convert to an input-only endpoint (accepts a body, returns void).
    /// </summary>
    public InputRouteDefinition<TInput> Accepts<TInput>() => new(Method, Route, CurrentState());

    public static implicit operator Define(RouteDefinition _) => default!;
}

/// <summary>
/// Route definition for file/stream endpoints that return binary content rather than JSON.
/// Defaults to GET and sets a content type (application/octet-stream unless overridden).
/// </summary>
public sealed class FileRouteDefinition : RouteDefinitionBase<FileRouteDefinition>
{
    internal FileRouteDefinition(string route, int defaultStatus = 200)
        : base("GET", route, defaultStatus)
    {
        ProducesFile();
    }

    public RivetResult Error(int statusCode) => RivetTerminal.Error(Publish(null), statusCode);

    public RivetResult Error<TError>(int statusCode, TError payload) =>
        RivetTerminal.Error(Publish(null), statusCode, payload);

    // Retained for callers compiled against the original five-parameter signature.
    public RivetResult File(
        byte[] content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
    ) =>
        RivetTerminal.File(
            Publish(null),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null
    ) =>
        RivetTerminal.File(
            Publish(null),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType
        );

    // Retained for callers compiled against the original five-parameter signature.
    public RivetResult File(
        Stream content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
    ) =>
        RivetTerminal.File(
            Publish(null),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        Stream content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null
    ) =>
        RivetTerminal.File(
            Publish(null),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType
        );

    // Retained for callers compiled against the original five-parameter signature.
    public RivetResult File(
        string physicalPath,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(null),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(null),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType
        );

    /// <summary>
    /// Sets the response content type for this file endpoint.
    /// Alias for ProducesFile — preferred on FileRouteDefinition for readability.
    /// </summary>
    public FileRouteDefinition ContentType(string mediaType) => ProducesFile(mediaType);

    public static implicit operator Define(FileRouteDefinition _) => default!;
}

/// <summary>
/// Route definition for file/stream endpoints with an input type (e.g. route/query params).
/// Defaults to GET and sets a content type (application/octet-stream unless overridden).
/// </summary>
public sealed class FileRouteDefinition<TInput> : RouteDefinitionBase<FileRouteDefinition<TInput>>
{
    internal FileRouteDefinition(string route, int defaultStatus = 200)
        : base("GET", route, defaultStatus)
    {
        ProducesFile();
    }

    public BoundFileRouteDefinition Bind(TInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new BoundFileRouteDefinition(Publish(null));
    }

    /// <summary>
    /// Sets the response content type for this file endpoint.
    /// Alias for ProducesFile — preferred on FileRouteDefinition for readability.
    /// </summary>
    public FileRouteDefinition<TInput> ContentType(string mediaType) => ProducesFile(mediaType);

    public static implicit operator Define(FileRouteDefinition<TInput> _) => default!;
}
