namespace Rivet;

using System.Collections.Immutable;
using Microsoft.Net.Http.Headers;

/// <summary>An OpenAPI response key: an exact status, an nXX range (<see cref="RangeClass"/>), or default.</summary>
internal readonly record struct ResponseStatusKey(string Key, int? Exact, int? RangeClass)
{
    public static ResponseStatusKey FromStatus(int statusCode) =>
        new(statusCode.ToString(), statusCode, null);

    public static ResponseStatusKey? Parse(string key) =>
        key switch
        {
            [>= '1' and <= '5', >= '0' and <= '9', >= '0' and <= '9'] => new(
                key,
                int.Parse(key),
                null
            ),
            [>= '1' and <= '5', 'X' or 'x', 'X' or 'x'] => new(key, null, key[0] - '0'),
            _ when key.Equals("default", StringComparison.OrdinalIgnoreCase) => new(
                key,
                null,
                null
            ),
            _ => null,
        };
}

/// <summary>A non-success response declared via .Returns().</summary>
internal sealed record RouteErrorResponse(ResponseStatusKey Status, Type? ResponseType);

/// <summary>
/// Everything a route definition's builder methods record. Immutable, so a converted
/// definition (<c>.Accepts&lt;T&gt;()</c>) takes the whole state in one assignment.
/// </summary>
internal sealed record RouteState(int SuccessStatus)
{
    public bool StatusSet { get; init; }
    public string? FileContentType { get; init; }
    public bool AcceptsFile { get; init; }
    public bool FormEncoded { get; init; }
    public string? BinaryRequestContentType { get; init; }
    public string? ResponseContentType { get; init; }
    public ImmutableList<RouteErrorResponse> ErrorResponses { get; init; } = [];
    public ImmutableList<RouteResponseContent> ResponseContents { get; init; } = [];
    public string? SuccessStatusKey { get; init; }
    public bool SuppressImplicitResponse { get; init; }
}

/// <summary>
/// Shared builder state and fluent methods for all RouteDefinition variants.
/// Uses CRTP so each builder method returns the concrete type for chaining.
/// </summary>
public abstract partial class RouteDefinitionBase<TSelf>
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
        if (state.ErrorResponses.Any(response => response.Status.Exact == state.SuccessStatus))
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

        foreach (var (status, responseType) in state.ErrorResponses)
        {
            var response = BuildResponse(
                state,
                status.Key,
                status.Exact,
                responseType,
                isSuccess: false
            );
            if (status.Exact is { } exactStatus)
            {
                exact.Add(exactStatus, response);
            }
            else if (status.RangeClass is { } rangeClass)
            {
                ranges.Add(rangeClass, response);
            }
            else
            {
                fallback = response;
            }
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
        var representations = new Dictionary<string, ResponseRepresentation>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var content in matchingContents)
        {
            Add(ResponseRepresentation.Create(content.MediaType, content.IsBinary));
        }

        if (state.FileContentType is not null && isSuccess)
        {
            Add(ResponseRepresentation.Create(state.FileContentType, isBinary: true));
        }
        else if (state.ResponseContentType is not null && isSuccess)
        {
            Add(ResponseRepresentation.Create(state.ResponseContentType, isBinary: false));
        }
        else if (payloadType is not null && representations.Count == 0)
        {
            Add(ResponseRepresentation.Json);
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
            representations.Count,
            SelectBodyRepresentation(),
            representations.Values.Where(representation => representation.IsBinary).ToArray()
        );

        void Add(ResponseRepresentation representation) =>
            representations[representation.Key] = representation;

        // The representation Success/Error write a body with: the explicit primary content
        // type, else JSON, else the only one declared. Null when that is ambiguous.
        ResponseRepresentation? SelectBodyRepresentation()
        {
            if (
                isSuccess
                && state.ResponseContentType is { } preferred
                && representations.TryGetValue(
                    ResponseRepresentation.KeyOf(preferred),
                    out var preferredRepresentation
                )
            )
            {
                return preferredRepresentation;
            }

            if (representations.Count == 0)
            {
                return ResponseRepresentation.Json;
            }

            return representations.Values.FirstOrDefault(representation => representation.IsJson)
                ?? (representations.Count == 1 ? representations.Values.Single() : null);
        }
    }

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

            if (state.ErrorResponses.Any(response => response.Status.Exact == statusCode))
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

    private TSelf AddErrorResponse(int statusCode, Type? responseType) =>
        Mutate(state =>
        {
            if (statusCode is < 100 or > 599)
            {
                throw new InvalidOperationException(
                    $"{Method} {Route}: response status {statusCode} is not a valid HTTP status code."
                );
            }

            return AddErrorResponse(state, ResponseStatusKey.FromStatus(statusCode), responseType);
        });

    private TSelf AddErrorResponse(string statusKey, Type? responseType) =>
        Mutate(state =>
            AddErrorResponse(
                state,
                ResponseStatusKey.Parse(statusKey)
                    ?? throw new InvalidOperationException(
                        $"{Method} {Route}: response status key '{statusKey}' is not an exact status, nXX range, or default."
                    ),
                responseType
            )
        );

    private static RouteState AddErrorResponse(
        RouteState state,
        ResponseStatusKey status,
        Type? responseType
    )
    {
        if (state.StatusSet && status.Exact == state.SuccessStatus)
        {
            throw new InvalidOperationException(
                $"Status {status.Exact} is already declared as the success status — success and error responses cannot share a status."
            );
        }

        if (
            state.ErrorResponses.Any(existing =>
                status.Exact is not null
                    ? existing.Status.Exact == status.Exact
                    : existing.Status.Exact is null
                        && existing.Status.Key.Equals(
                            status.Key,
                            StringComparison.OrdinalIgnoreCase
                        )
            )
        )
        {
            throw new InvalidOperationException(
                $"Status {status.Key} is already declared via .Returns() — a status carries a single response shape. "
                    + "For multiple shapes at one status, declare a [RivetUnion] type and return it once."
            );
        }

        return state with
        {
            ErrorResponses = state.ErrorResponses.Add(new RouteErrorResponse(status, responseType)),
        };
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
/// The terminals an unbound definition shares: Error and File. Success differs by payload
/// shape, so each definition declares its own.
/// </summary>
public abstract class TerminalRouteDefinitionBase<TSelf> : RouteDefinitionBase<TSelf>
    where TSelf : TerminalRouteDefinitionBase<TSelf>
{
    private protected TerminalRouteDefinitionBase(string method, string route, int defaultStatus)
        : base(method, route, defaultStatus) { }

    private protected virtual Type? SuccessPayloadType => null;

    public RivetResult Error(int statusCode) =>
        RivetTerminal.Error(Publish(SuccessPayloadType), statusCode);

    public RivetResult Error<TError>(int statusCode, TError payload) =>
        RivetTerminal.Error(Publish(SuccessPayloadType), statusCode, payload);

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null,
        bool inline = false
    ) =>
        RivetTerminal.File(
            Publish(SuccessPayloadType),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType,
            inline
        );

    public RivetResult File(
        Stream content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null,
        bool inline = false
    ) =>
        RivetTerminal.File(
            Publish(SuccessPayloadType),
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType,
            inline
        );

    public RivetResult File(
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null,
        bool inline = false
    ) =>
        RivetTerminal.PhysicalFile(
            Publish(SuccessPayloadType),
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType,
            inline
        );
}

/// <summary>
/// Route definition for endpoints with both input and output types.
/// Roslyn reads the chain at generation time. Bind publishes the contract for terminal use.
/// </summary>
public sealed class RouteDefinition<TInput, TOutput>
    : RouteDefinitionBase<RouteDefinition<TInput, TOutput>>
{
    internal RouteDefinition(string method, string route, int defaultStatus = 200)
        : base(method, route, defaultStatus) { }

    public BoundRouteDefinition<TOutput> Bind(TInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new BoundRouteDefinition<TOutput>(Publish(typeof(TOutput)));
    }
}

/// <summary>
/// Route definition for endpoints with output only (no input type).
/// </summary>
public sealed class RouteDefinition<TOutput> : TerminalRouteDefinitionBase<RouteDefinition<TOutput>>
{
    internal RouteDefinition(string method, string route, int defaultStatus = 200)
        : base(method, route, defaultStatus) { }

    private protected override Type? SuccessPayloadType => typeof(TOutput);

    public RivetResult Success(TOutput payload) =>
        RivetTerminal.Success(Publish(typeof(TOutput)), payload);
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
}

/// <summary>
/// Route definition for endpoints with no typed input or output.
/// </summary>
public sealed class RouteDefinition : TerminalRouteDefinitionBase<RouteDefinition>
{
    internal RouteDefinition(string method, string route, int defaultStatus = 200)
        : base(method, route, defaultStatus) { }

    public RivetResult Success() => RivetTerminal.Success(Publish(null));

    /// <summary>
    /// Convert to an input-only endpoint (accepts a body, returns void).
    /// </summary>
    public InputRouteDefinition<TInput> Accepts<TInput>() => new(Method, Route, CurrentState());
}

/// <summary>
/// Route definition for file/stream endpoints that return binary content rather than JSON.
/// Defaults to GET and sets a content type (application/octet-stream unless overridden).
/// </summary>
public sealed class FileRouteDefinition : TerminalRouteDefinitionBase<FileRouteDefinition>
{
    internal FileRouteDefinition(string route, int defaultStatus = 200)
        : base("GET", route, defaultStatus)
    {
        ProducesFile();
    }
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
}
