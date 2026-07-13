namespace Rivet;

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
/// Shared builder state and fluent methods for all RouteDefinition variants.
/// Uses CRTP so each builder method returns the concrete type for chaining.
/// </summary>
public abstract class RouteDefinitionBase<TSelf>
    where TSelf : RouteDefinitionBase<TSelf>
{
    private int _successStatus;
    private bool _statusSet;
    private string? _summary;
    private string? _description;
    private bool _anonymous;
    private string? _securityScheme;
    private string? _fileContentType;
    private bool _acceptsFile;
    private bool _formEncoded;
    private string? _binaryRequestContentType;
    private string? _requestContentType;
    private string? _responseContentType;
    private string? _queryAuthParameterName;
    private List<RouteErrorResponse>? _errorResponses;
    private List<RouteResponseHeader>? _responseHeaders;
    private List<RouteResponseContent>? _responseContents;
    private string? _successStatusKey;
    private bool _suppressImplicitResponse;

    // R3: contract definitions are stored in shared static readonly fields; once a
    // definition has been published by a terminal any builder mutation would silently
    // change global state for all requests. Published definitions throw instead.
    private bool _published;
    private readonly object _publicationLock = new();
    private EndpointContract? _publishedContract;

    /// <summary>The HTTP method (GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS).</summary>
    public string Method { get; }

    /// <summary>The route template from the contract definition.</summary>
    public string Route { get; }

    public string? EndpointSummary => _summary;
    public string? EndpointDescription => _description;
    public bool IsAnonymous => _anonymous;
    public string? SecurityScheme => _securityScheme;
    public string? FileContentType => _fileContentType;
    public bool IsFileUpload => _acceptsFile;
    public bool IsFormEncoded => _formEncoded;
    public string? BinaryRequestContentType => _binaryRequestContentType;
    public string? RequestContentType => _requestContentType;
    public string? ResponseContentType => _responseContentType;
    public bool IsQueryAuth => _queryAuthParameterName is not null;
    public string? QueryAuthParameterName => _queryAuthParameterName;
    public IReadOnlyList<RouteErrorResponse>? RouteErrorResponses => _errorResponses;
    public IReadOnlyList<RouteResponseHeader>? ResponseHeaders => _responseHeaders;

    /// <summary>The resolved success status code for this endpoint.</summary>
    public int SuccessStatusCode => _successStatus;

    /// <summary>The resolved success status code used during publication.</summary>
    protected int SuccessStatus => _successStatus;

    protected RouteDefinitionBase(string method, string route, int defaultStatus)
    {
        Method = method;
        Route = route;
        _successStatus = defaultStatus;
    }

    /// <summary>
    /// Copy all builder state from this instance to another RouteDefinitionBase.
    /// Used by RouteDefinition.Accepts&lt;T&gt;() to transfer state during type conversion.
    /// </summary>
    protected void CopyStateTo<TOther>(RouteDefinitionBase<TOther> target)
        where TOther : RouteDefinitionBase<TOther>
    {
        using var mutation = target.BeginMutation();
        target._summary = _summary;
        target._description = _description;
        target._anonymous = _anonymous;
        target._securityScheme = _securityScheme;
        target._fileContentType = _fileContentType;
        target._acceptsFile = _acceptsFile;
        target._formEncoded = _formEncoded;
        target._binaryRequestContentType = _binaryRequestContentType;
        target._requestContentType = _requestContentType;
        target._responseContentType = _responseContentType;
        target._queryAuthParameterName = _queryAuthParameterName;
        target._errorResponses = _errorResponses?.ToList();
        target._responseHeaders = _responseHeaders?.ToList();
        target._responseContents = _responseContents?.ToList();
        target._successStatusKey = _successStatusKey;
        target._suppressImplicitResponse = _suppressImplicitResponse;
        target._statusSet = _statusSet;
    }

    internal EndpointContract Publish(Type? successPayloadType)
    {
        lock (_publicationLock)
        {
            if (_publishedContract is not null)
            {
                return _publishedContract;
            }

            if (_successStatus is < 100 or > 599)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: success status {_successStatus} is not a valid HTTP status code."
                );
            }

            if (
                _errorResponses?.Any(response => GetExactStatus(response) == _successStatus) is true
            )
            {
                throw new InvalidOperationException(
                    $"Status {_successStatus} is declared as both the success status and via .Returns() — "
                        + "success and error responses cannot share a status."
                );
            }

            var success = _suppressImplicitResponse
                ? null
                : BuildResponse(
                    _successStatusKey ?? _successStatus.ToString(),
                    _successStatus,
                    successPayloadType,
                    isSuccess: true
                );
            var exact = new Dictionary<int, ResponseContract>();
            var ranges = new Dictionary<int, ResponseContract>();
            ResponseContract? fallback = null;

            foreach (var response in _errorResponses ?? [])
            {
                var statusKey = response.EffectiveStatusKey;
                var exactStatus = GetExactStatus(response);
                if (exactStatus is not null)
                {
                    exact.Add(
                        exactStatus.Value,
                        BuildResponse(
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
                        BuildResponse(statusKey, null, response.ResponseType, isSuccess: false)
                    );
                    continue;
                }

                throw new InvalidOperationException(
                    $"{Method} {Route}: response status key '{statusKey}' is not an exact status, nXX range, or default."
                );
            }

            _publishedContract = new EndpointContract(
                Method,
                Route,
                success,
                new ResponseSet(exact, ranges, fallback)
            );
            _published = true;
            return _publishedContract;
        }
    }

    private ResponseContract BuildResponse(
        string statusKey,
        int? statusCode,
        Type? payloadType,
        bool isSuccess
    )
    {
        var matchingContents = (_responseContents ?? [])
            .Where(content =>
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

        if (_fileContentType is not null && isSuccess)
        {
            representations[_fileContentType] = new ResponseRepresentation(_fileContentType, true);
        }
        else if (_responseContentType is not null && isSuccess)
        {
            representations[_responseContentType] = new ResponseRepresentation(
                _responseContentType,
                false
            );
        }
        else if (payloadType is not null && representations.Count == 0)
        {
            var mediaType =
                isSuccess && _responseContentType is not null
                    ? _responseContentType
                    : "application/json";
            representations[mediaType] = new ResponseRepresentation(mediaType, false);
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
            isSuccess ? _responseContentType : null,
            representations
        );
    }

    protected IDisposable BeginMutation()
    {
        Monitor.Enter(_publicationLock);
        if (_published)
        {
            Monitor.Exit(_publicationLock);
            throw new InvalidOperationException(
                $"{Method} {Route}: contract definitions are immutable once published — "
                    + "builder methods cannot be called after a terminal publishes the endpoint. "
                    + "Configure the definition fully in its static readonly initializer."
            );
        }

        return new MutationLease(_publicationLock);
    }

    private sealed class MutationLease(object syncRoot) : IDisposable
    {
        private object? _syncRoot = syncRoot;

        public void Dispose()
        {
            var syncRoot = Interlocked.Exchange(ref _syncRoot, null);
            if (syncRoot is not null)
            {
                Monitor.Exit(syncRoot);
            }
        }
    }

    public TSelf Summary(string summary)
    {
        using var mutation = BeginMutation();
        _summary = summary;
        return (TSelf)this;
    }

    public TSelf Description(string description)
    {
        using var mutation = BeginMutation();
        _description = description;
        return (TSelf)this;
    }

    public TSelf Status(int statusCode)
    {
        using var mutation = BeginMutation();
        if (statusCode is < 100 or > 599)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: success status {statusCode} is not a valid HTTP status code."
            );
        }

        if (_statusSet)
        {
            throw new InvalidOperationException(
                $"Status already set to {_successStatus} — cannot set to {statusCode}. Call .Status() only once."
            );
        }

        if (_errorResponses?.Any(response => GetExactStatus(response) == statusCode) is true)
        {
            throw new InvalidOperationException(
                $"Status {statusCode} is already declared via .Returns() — success and error responses cannot share a status."
            );
        }

        _successStatus = statusCode;
        _statusSet = true;
        return (TSelf)this;
    }

    /// <summary>
    /// Carries the source OpenAPI response key and primary response description through
    /// generated C#. Concrete runtime status behavior remains controlled by <see cref="Status"/>.
    /// </summary>
    public TSelf StatusKey(string statusKey, string? description = null)
    {
        using var mutation = BeginMutation();
        _successStatusKey = statusKey;
        _ = description;
        return (TSelf)this;
    }

    /// <summary>
    /// Suppresses Rivet's authored method-default response. Intended for imported
    /// operations whose source response set contains no concrete success response.
    /// </summary>
    public TSelf SuppressImplicitResponse()
    {
        using var mutation = BeginMutation();
        _suppressImplicitResponse = true;
        return (TSelf)this;
    }

    public TSelf FormEncoded()
    {
        using var mutation = BeginMutation();
        if (_binaryRequestContentType is not null)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .FormEncoded() cannot be combined with .AcceptsBinary() — "
                    + "a request body is either raw binary or form-encoded, not both."
            );
        }

        _formEncoded = true;
        return (TSelf)this;
    }

    /// <summary>
    /// Declares the request body's media type when it is not application/json
    /// (e.g. "text/plain" for a string body). The body SCHEMA is unchanged —
    /// this overrides only the content-type key the spec declares. For raw
    /// binary bodies use .AcceptsBinary(); for forms use .FormEncoded().
    /// </summary>
    public TSelf AcceptsContentType(string contentType)
    {
        using var mutation = BeginMutation();
        if (_formEncoded || _binaryRequestContentType is not null)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .AcceptsContentType() cannot be combined with "
                    + ".FormEncoded() or .AcceptsBinary() — those already declare the body media type."
            );
        }

        _requestContentType = contentType;
        return (TSelf)this;
    }

    /// <summary>
    /// Declares the success response's media type when it is not
    /// application/json (e.g. "text/html" for a string response). The response
    /// SCHEMA is unchanged — this overrides only the content-type key the spec
    /// declares. For binary/file responses use .ProducesFile().
    /// </summary>
    public TSelf ProducesContentType(string contentType)
    {
        using var mutation = BeginMutation();
        if (_fileContentType is not null)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .ProducesContentType() cannot be combined with .ProducesFile() — "
                    + "the file content type already declares the response media type."
            );
        }

        _responseContentType = contentType;
        return (TSelf)this;
    }

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

    private TSelf AddErrorResponse(RouteErrorResponse response)
    {
        using var mutation = BeginMutation();
        _errorResponses ??= [];
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

        if (_statusSet && exactStatus == _successStatus)
        {
            throw new InvalidOperationException(
                $"Status {exactStatus} is already declared as the success status — success and error responses cannot share a status."
            );
        }

        if (
            _errorResponses.Any(existing =>
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

        _errorResponses.Add(response);
        return (TSelf)this;
    }

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

    private TSelf AddResponseHeader(RouteResponseHeader header)
    {
        using var mutation = BeginMutation();
        _responseHeaders ??= [];

        if (
            _responseHeaders.Any(existing =>
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

        _responseHeaders.Add(header);
        return (TSelf)this;
    }

    public TSelf RequestExampleJson(
        string json,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        using var mutation = BeginMutation();
        // Example metadata is consumed by the Roslyn analyzer, not at runtime.
        _ = json;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return (TSelf)this;
    }

    public TSelf RequestExampleRef(
        string componentExampleId,
        string resolvedJson,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        using var mutation = BeginMutation();
        _ = componentExampleId;
        _ = resolvedJson;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return (TSelf)this;
    }

    public TSelf ResponseExampleJson(
        int statusCode,
        string json,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        using var mutation = BeginMutation();
        _ = statusCode;
        _ = json;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return (TSelf)this;
    }

    public TSelf ResponseExampleJson(
        string statusKey,
        string json,
        string? name = null,
        string? mediaType = null,
        string? referencedComponentsJson = null
    )
    {
        using var mutation = BeginMutation();
        _ = statusKey;
        _ = json;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return (TSelf)this;
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
        using var mutation = BeginMutation();
        _ = statusCode;
        _ = componentExampleId;
        _ = resolvedJson;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return (TSelf)this;
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
        using var mutation = BeginMutation();
        _ = statusKey;
        _ = componentExampleId;
        _ = resolvedJson;
        _ = name;
        _ = mediaType;
        _ = referencedComponentsJson;
        return (TSelf)this;
    }

    public TSelf Anonymous()
    {
        using var mutation = BeginMutation();
        _anonymous = true;
        return (TSelf)this;
    }

    public TSelf Secure(string scheme)
    {
        using var mutation = BeginMutation();
        _securityScheme = scheme;
        return (TSelf)this;
    }

    public TSelf SecurityRequirements()
    {
        using var mutation = BeginMutation();
        return (TSelf)this;
    }

    public TSelf SecurityRequirement(int requirementOrder)
    {
        using var mutation = BeginMutation();
        _ = requirementOrder;
        return (TSelf)this;
    }

    public TSelf SecurityRequirement(int requirementOrder, string scheme, string? scope = null)
    {
        using var mutation = BeginMutation();
        _ = requirementOrder;
        _ = scheme;
        _ = scope;
        return (TSelf)this;
    }

    public TSelf RequestContent<T>(
        string mediaType,
        string? schemaRef = null,
        string? schemaType = null,
        string? format = null
    )
    {
        using var mutation = BeginMutation();
        _ = mediaType;
        _ = schemaRef;
        _ = schemaType;
        _ = format;
        return (TSelf)this;
    }

    public TSelf RequestContent(string mediaType)
    {
        using var mutation = BeginMutation();
        _ = mediaType;
        return (TSelf)this;
    }

    public TSelf RequestBinaryContent(string mediaType)
    {
        using var mutation = BeginMutation();
        _ = mediaType;
        return (TSelf)this;
    }

    public TSelf RequestBodyRequired(bool required)
    {
        using var mutation = BeginMutation();
        _ = required;
        return (TSelf)this;
    }

    public TSelf RequestBody()
    {
        using var mutation = BeginMutation();
        return (TSelf)this;
    }

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
        using var mutation = BeginMutation();
        _ = name;
        _ = location;
        _ = required;
        _ = schemaType;
        _ = format;
        _ = metadataJson;
        _ = schemaRef;
        return (TSelf)this;
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
        using var mutation = BeginMutation();
        AddResponseContent(statusCode.ToString(), mediaType, typeof(T), isBinary: false);
        _ = schemaRef;
        _ = schemaType;
        _ = format;
        _ = schemaDescription;
        return (TSelf)this;
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
        using var mutation = BeginMutation();
        AddResponseContent(statusKey, mediaType, typeof(T), isBinary: false);
        _ = schemaRef;
        _ = schemaType;
        _ = format;
        _ = schemaDescription;
        return (TSelf)this;
    }

    public TSelf ResponseContent(int statusCode, string mediaType)
    {
        using var mutation = BeginMutation();
        AddResponseContent(statusCode.ToString(), mediaType, null, isBinary: false);
        return (TSelf)this;
    }

    public TSelf ResponseContent(string statusKey, string mediaType)
    {
        using var mutation = BeginMutation();
        AddResponseContent(statusKey, mediaType, null, isBinary: false);
        return (TSelf)this;
    }

    public TSelf ResponseBinaryContent(int statusCode, string mediaType)
    {
        using var mutation = BeginMutation();
        AddResponseContent(statusCode.ToString(), mediaType, null, isBinary: true);
        return (TSelf)this;
    }

    public TSelf ResponseBinaryContent(string statusKey, string mediaType)
    {
        using var mutation = BeginMutation();
        AddResponseContent(statusKey, mediaType, null, isBinary: true);
        return (TSelf)this;
    }

    private void AddResponseContent(
        string statusKey,
        string mediaType,
        Type? payloadType,
        bool isBinary
    )
    {
        _responseContents ??= [];
        _responseContents.Add(
            new RouteResponseContent(statusKey, mediaType, payloadType, isBinary)
        );
    }

    /// <summary>
    /// Opts this endpoint into query-based authentication, where the auth token is passed
    /// as a query parameter instead of a header. Primarily intended for media players
    /// (ExoPlayer, HLS.js) that cannot inject custom headers on segment requests.
    /// </summary>
    public TSelf QueryAuth(string parameterName = "token")
    {
        using var mutation = BeginMutation();
        _queryAuthParameterName = parameterName;
        return (TSelf)this;
    }

    /// <summary>
    /// Marks this endpoint as returning a file download instead of JSON.
    /// The generated TS client returns Blob; the OpenAPI spec emits the given content type with format: binary.
    /// </summary>
    public TSelf ProducesFile(string contentType = "application/octet-stream")
    {
        using var mutation = BeginMutation();
        if (
            string.IsNullOrWhiteSpace(contentType)
            || !MediaTypeHeaderValue.TryParse(contentType, out _)
        )
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .ProducesFile() requires a valid content type."
            );
        }

        _fileContentType = contentType;
        return (TSelf)this;
    }

    /// <summary>
    /// Marks this endpoint as accepting a file upload (multipart/form-data).
    /// The generated TS client will accept a File parameter.
    /// </summary>
    public TSelf AcceptsFile()
    {
        using var mutation = BeginMutation();
        if (_binaryRequestContentType is not null)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .AcceptsFile() cannot be combined with .AcceptsBinary() — "
                    + "a request body is either raw binary or multipart/form-data, not both."
            );
        }

        _acceptsFile = true;
        return (TSelf)this;
    }

    /// <summary>
    /// Marks this endpoint as accepting a raw binary request body (application/octet-stream
    /// unless overridden). The body is the raw bytes; binding/reading the request stream is
    /// host code — Rivet never touches it at runtime. Rivet emits the binary requestBody
    /// into the OpenAPI spec, and on contract definitions the TInput properties lower to
    /// route/query parameters instead of a JSON body.
    /// </summary>
    public TSelf AcceptsBinary(string contentType = "application/octet-stream")
    {
        using var mutation = BeginMutation();
        if (_acceptsFile)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .AcceptsBinary() cannot be combined with .AcceptsFile() — "
                    + "a request body is either raw binary or multipart/form-data, not both."
            );
        }

        if (_formEncoded)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .AcceptsBinary() cannot be combined with .FormEncoded() — "
                    + "a request body is either raw binary or form-encoded, not both."
            );
        }

        _binaryRequestContentType = contentType;
        return (TSelf)this;
    }
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

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
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
        string? entityTag = null
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
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(typeof(TOutput)),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public static implicit operator Define(RouteDefinition<TOutput> _) => default!;
}

/// <summary>
/// Route definition for endpoints with input only (no typed output — e.g. PUT/PATCH returning 204).
/// Chain from void definition via .Accepts&lt;T&gt;().
/// </summary>
public sealed class InputRouteDefinition<TInput> : RouteDefinitionBase<InputRouteDefinition<TInput>>
{
    internal InputRouteDefinition(string method = "GET", string route = "", int defaultStatus = 200)
        : base(method, route, defaultStatus) { }

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

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
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
        string? entityTag = null
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
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(null),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    /// <summary>
    /// Convert to an input-only endpoint (accepts a body, returns void).
    /// </summary>
    public InputRouteDefinition<TInput> Accepts<TInput>()
    {
        using var mutation = BeginMutation();
        var def = new InputRouteDefinition<TInput>(Method, Route, SuccessStatus);
        CopyStateTo(def);
        return def;
    }

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

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
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
        string? entityTag = null
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
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(null),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
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
