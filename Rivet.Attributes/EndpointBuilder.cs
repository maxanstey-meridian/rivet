namespace Rivet;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

/// <summary>
/// Describes an additional (non-success) response declared via .Returns&lt;T&gt;().
/// </summary>
public sealed record RouteErrorResponse(int StatusCode, Type? ResponseType, string? Description);

/// <summary>
/// Describes a response header declared via .WithResponseHeader(). A null StatusCode
/// targets the endpoint's success status. Spec-only: Rivet never sets or validates
/// response headers at runtime — emitting them is handler code.
/// </summary>
public sealed record RouteResponseHeader(int? StatusCode, string Name, string? Description, bool Required);

/// <summary>
/// Shared builder state and fluent methods for all RouteDefinition variants.
/// Uses CRTP so each builder method returns the concrete type for chaining.
/// </summary>
public abstract class RouteDefinitionBase<TSelf> where TSelf : RouteDefinitionBase<TSelf>
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
    private bool _skipValidation;

    // R3: contract definitions are stored in shared static readonly fields; once a
    // definition has been published (first Invoke) any builder mutation would silently
    // change global state for all requests. Frozen definitions throw instead.
    private bool _published;

    /// <summary>The HTTP method (GET, POST, PUT, PATCH, DELETE).</summary>
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
    public bool ShouldSkipValidation => _skipValidation;

    /// <summary>The resolved success status code for this endpoint.</summary>
    public int SuccessStatusCode => _successStatus;

    /// <summary>The resolved success status code (for use in Invoke).</summary>
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
    protected void CopyStateTo<TOther>(RouteDefinitionBase<TOther> target) where TOther : RouteDefinitionBase<TOther>
    {
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
        target._skipValidation = _skipValidation;
    }

    /// <summary>
    /// R3: marks this definition as published. Called by every Invoke overload —
    /// after this, all builder mutators throw.
    /// </summary>
    protected void MarkPublished() => _published = true;

    private void EnsureMutable()
    {
        if (_published)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: contract definitions are immutable once published — " +
                "builder methods cannot be called after the endpoint has been invoked. " +
                "Configure the definition fully in its static readonly initializer.");
        }
    }

    public TSelf Summary(string summary)
    {
        EnsureMutable();
        _summary = summary;
        return (TSelf)this;
    }

    public TSelf Description(string description)
    {
        EnsureMutable();
        _description = description;
        return (TSelf)this;
    }

    public TSelf Status(int statusCode)
    {
        EnsureMutable();
        if (_statusSet)
        {
            throw new InvalidOperationException($"Status already set to {_successStatus} — cannot set to {statusCode}. Call .Status() only once.");
        }

        _successStatus = statusCode;
        _statusSet = true;
        return (TSelf)this;
    }

    public TSelf FormEncoded()
    {
        EnsureMutable();
        if (_binaryRequestContentType is not null)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .FormEncoded() cannot be combined with .AcceptsBinary() — " +
                "a request body is either raw binary or form-encoded, not both.");
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
        EnsureMutable();
        if (_formEncoded || _binaryRequestContentType is not null)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .AcceptsContentType() cannot be combined with " +
                ".FormEncoded() or .AcceptsBinary() — those already declare the body media type.");
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
        EnsureMutable();
        if (_fileContentType is not null)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .ProducesContentType() cannot be combined with .ProducesFile() — " +
                "the file content type already declares the response media type.");
        }

        _responseContentType = contentType;
        return (TSelf)this;
    }

    /// <summary>
    /// Skip typed-result validation for endpoints that return framework result types
    /// (e.g. ChallengeHttpResult, SignOutHttpResult) which do not implement
    /// IStatusCodeHttpResult and therefore cannot be validated by Rivet's validator.
    /// </summary>
    public TSelf SkipValidation()
    {
        EnsureMutable();
        _skipValidation = true;
        return (TSelf)this;
    }

    public TSelf Returns<TResponse>(int statusCode)
        => Returns<TResponse>(statusCode, null);

    public TSelf Returns<TResponse>(int statusCode, string? description)
        => AddErrorResponse(new RouteErrorResponse(statusCode, typeof(TResponse), description));

    public TSelf Returns(int statusCode)
        => Returns(statusCode, null);

    public TSelf Returns(int statusCode, string? description)
        => AddErrorResponse(new RouteErrorResponse(statusCode, null, description));

    private TSelf AddErrorResponse(RouteErrorResponse response)
    {
        EnsureMutable();
        _errorResponses ??= [];

        if (_errorResponses.Any(existing => existing.StatusCode == response.StatusCode))
        {
            throw new InvalidOperationException(
                $"Status {response.StatusCode} is already declared via .Returns() — declare each status only once.");
        }

        _errorResponses.Add(response);
        return (TSelf)this;
    }

    /// <summary>
    /// Declares a response header on the given status code (contract concept).
    /// Spec-only: Rivet never sets or validates response headers at runtime —
    /// emitting Location/ETag/... is handler code. <paramref name="required"/> is an
    /// explicit opt-in promise that the header is always present.
    /// </summary>
    public TSelf WithResponseHeader(int statusCode, string name, string? description = null, bool required = false)
        => AddResponseHeader(new RouteResponseHeader(statusCode, name, description, required));

    /// <summary>
    /// Declares a response header on the endpoint's success status (contract concept).
    /// See <see cref="WithResponseHeader(int, string, string?, bool)"/>.
    /// </summary>
    public TSelf WithResponseHeader(string name, string? description = null, bool required = false)
        => AddResponseHeader(new RouteResponseHeader(null, name, description, required));

    private TSelf AddResponseHeader(RouteResponseHeader header)
    {
        EnsureMutable();
        _responseHeaders ??= [];

        if (_responseHeaders.Any(existing => existing.StatusCode == header.StatusCode
            && string.Equals(existing.Name, header.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Response header '{header.Name}' is already declared for this status via .WithResponseHeader() — declare each header only once per status.");
        }

        _responseHeaders.Add(header);
        return (TSelf)this;
    }

    public TSelf RequestExampleJson(string json, string? name = null, string? mediaType = null)
    {
        return (TSelf)this;
    }

    public TSelf RequestExampleRef(string componentExampleId, string resolvedJson, string? name = null, string? mediaType = null)
    {
        return (TSelf)this;
    }

    public TSelf ResponseExampleJson(int statusCode, string json, string? name = null, string? mediaType = null)
    {
        return (TSelf)this;
    }

    public TSelf ResponseExampleRef(
        int statusCode,
        string componentExampleId,
        string resolvedJson,
        string? name = null,
        string? mediaType = null)
    {
        return (TSelf)this;
    }

    public TSelf Anonymous()
    {
        EnsureMutable();
        _anonymous = true;
        return (TSelf)this;
    }

    public TSelf Secure(string scheme)
    {
        EnsureMutable();
        _securityScheme = scheme;
        return (TSelf)this;
    }

    /// <summary>
    /// Opts this endpoint into query-based authentication, where the auth token is passed
    /// as a query parameter instead of a header. Primarily intended for media players
    /// (ExoPlayer, HLS.js) that cannot inject custom headers on segment requests.
    /// </summary>
    public TSelf QueryAuth(string parameterName = "token")
    {
        EnsureMutable();
        _queryAuthParameterName = parameterName;
        return (TSelf)this;
    }

    /// <summary>
    /// Marks this endpoint as returning a file download instead of JSON.
    /// The generated TS client returns Blob; the OpenAPI spec emits the given content type with format: binary.
    /// </summary>
    public TSelf ProducesFile(string contentType = "application/octet-stream")
    {
        EnsureMutable();
        _fileContentType = contentType;
        return (TSelf)this;
    }

    /// <summary>
    /// Marks this endpoint as accepting a file upload (multipart/form-data).
    /// The generated TS client will accept a File parameter.
    /// </summary>
    public TSelf AcceptsFile()
    {
        EnsureMutable();
        if (_binaryRequestContentType is not null)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .AcceptsFile() cannot be combined with .AcceptsBinary() — " +
                "a request body is either raw binary or multipart/form-data, not both.");
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
        EnsureMutable();
        if (_acceptsFile)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .AcceptsBinary() cannot be combined with .AcceptsFile() — " +
                "a request body is either raw binary or multipart/form-data, not both.");
        }

        if (_formEncoded)
        {
            throw new InvalidOperationException(
                $"{Method} {Route}: .AcceptsBinary() cannot be combined with .FormEncoded() — " +
                "a request body is either raw binary or form-encoded, not both.");
        }

        _binaryRequestContentType = contentType;
        return (TSelf)this;
    }
}

/// <summary>
/// Route definition for endpoints with both input and output types.
/// Roslyn reads the chain at generation time. Invoke provides type-safe runtime execution.
/// </summary>
public sealed class RouteDefinition<TInput, TOutput> : RouteDefinitionBase<RouteDefinition<TInput, TOutput>>
{
    internal RouteDefinition(string method = "GET", string route = "", int defaultStatus = 200)
        : base(method, route, defaultStatus) { }

    /// <summary>
    /// Execute the endpoint handler with type-safe input and output.
    /// </summary>
    public async Task<RivetResult<TOutput>> Invoke(TInput input, Func<TInput, Task<TOutput>> handler)
    {
        MarkPublished();
        var result = await handler(input);
        return new RivetResult<TOutput>(SuccessStatus, result);
    }

    public async Task<Results<T1, T2>> Invoke<T1, T2>(
        TInput input,
        Func<TInput, Task<Results<T1, T2>>> handler)
        where T1 : IResult
        where T2 : IResult
        => await InvokeTypedResult(
            input,
            typeof(TOutput),
            handler);

    public async Task<Results<T1, T2, T3>> Invoke<T1, T2, T3>(
        TInput input,
        Func<TInput, Task<Results<T1, T2, T3>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        => await InvokeTypedResult(
            input,
            typeof(TOutput),
            handler);

    public async Task<Results<T1, T2, T3, T4>> Invoke<T1, T2, T3, T4>(
        TInput input,
        Func<TInput, Task<Results<T1, T2, T3, T4>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        => await InvokeTypedResult(
            input,
            typeof(TOutput),
            handler);

    public async Task<Results<T1, T2, T3, T4, T5>> Invoke<T1, T2, T3, T4, T5>(
        TInput input,
        Func<TInput, Task<Results<T1, T2, T3, T4, T5>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        where T5 : IResult
        => await InvokeTypedResult(
            input,
            typeof(TOutput),
            handler);

    public async Task<Results<T1, T2, T3, T4, T5, T6>> Invoke<T1, T2, T3, T4, T5, T6>(
        TInput input,
        Func<TInput, Task<Results<T1, T2, T3, T4, T5, T6>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        where T5 : IResult
        where T6 : IResult
        => await InvokeTypedResult(
            input,
            typeof(TOutput),
            handler);

    private async Task<TResult> InvokeTypedResult<TResult>(
        TInput input,
        Type successResponseType,
        Func<TInput, Task<TResult>> handler)
        where TResult : IResult
    {
        MarkPublished();
        var result = await handler(input);
        TypedResultValidator.Validate(Route, SuccessStatus, successResponseType, RouteErrorResponses, result, ShouldSkipValidation);
        return result;
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

    /// <summary>
    /// Execute the endpoint handler with type-safe output.
    /// </summary>
    public async Task<RivetResult<TOutput>> Invoke(Func<Task<TOutput>> handler)
    {
        MarkPublished();
        var result = await handler();
        return new RivetResult<TOutput>(SuccessStatus, result);
    }

    public async Task<T1> Invoke<T1>(
        Func<Task<T1>> handler)
        where T1 : IResult
        => await InvokeTypedResult(typeof(TOutput), handler);

    public async Task<Results<T1, T2>> Invoke<T1, T2>(
        Func<Task<Results<T1, T2>>> handler)
        where T1 : IResult
        where T2 : IResult
        => await InvokeTypedResult(typeof(TOutput), handler);

    public async Task<Results<T1, T2, T3>> Invoke<T1, T2, T3>(
        Func<Task<Results<T1, T2, T3>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        => await InvokeTypedResult(typeof(TOutput), handler);

    public async Task<Results<T1, T2, T3, T4>> Invoke<T1, T2, T3, T4>(
        Func<Task<Results<T1, T2, T3, T4>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        => await InvokeTypedResult(typeof(TOutput), handler);

    public async Task<Results<T1, T2, T3, T4, T5>> Invoke<T1, T2, T3, T4, T5>(
        Func<Task<Results<T1, T2, T3, T4, T5>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        where T5 : IResult
        => await InvokeTypedResult(typeof(TOutput), handler);

    public async Task<Results<T1, T2, T3, T4, T5, T6>> Invoke<T1, T2, T3, T4, T5, T6>(
        Func<Task<Results<T1, T2, T3, T4, T5, T6>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        where T5 : IResult
        where T6 : IResult
        => await InvokeTypedResult(typeof(TOutput), handler);

    private async Task<TResult> InvokeTypedResult<TResult>(
        Type successResponseType,
        Func<Task<TResult>> handler)
        where TResult : IResult
    {
        MarkPublished();
        var result = await handler();
        TypedResultValidator.Validate(Route, SuccessStatus, successResponseType, RouteErrorResponses, result, ShouldSkipValidation);
        return result;
    }

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

    /// <summary>
    /// Execute the endpoint handler with type-safe input (void output).
    /// </summary>
    public async Task<RivetResult> Invoke(TInput input, Func<TInput, Task> handler)
    {
        MarkPublished();
        await handler(input);
        return new RivetResult(SuccessStatus);
    }

    public async Task<T1> Invoke<T1>(
        TInput input,
        Func<TInput, Task<T1>> handler)
        where T1 : IResult
        => await InvokeTypedResult(input, handler);

    public async Task<Results<T1, T2>> Invoke<T1, T2>(
        TInput input,
        Func<TInput, Task<Results<T1, T2>>> handler)
        where T1 : IResult
        where T2 : IResult
        => await InvokeTypedResult(input, handler);

    public async Task<Results<T1, T2, T3>> Invoke<T1, T2, T3>(
        TInput input,
        Func<TInput, Task<Results<T1, T2, T3>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        => await InvokeTypedResult(input, handler);

    public async Task<Results<T1, T2, T3, T4>> Invoke<T1, T2, T3, T4>(
        TInput input,
        Func<TInput, Task<Results<T1, T2, T3, T4>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        => await InvokeTypedResult(input, handler);

    public async Task<Results<T1, T2, T3, T4, T5>> Invoke<T1, T2, T3, T4, T5>(
        TInput input,
        Func<TInput, Task<Results<T1, T2, T3, T4, T5>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        where T5 : IResult
        => await InvokeTypedResult(input, handler);

    public async Task<Results<T1, T2, T3, T4, T5, T6>> Invoke<T1, T2, T3, T4, T5, T6>(
        TInput input,
        Func<TInput, Task<Results<T1, T2, T3, T4, T5, T6>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        where T5 : IResult
        where T6 : IResult
        => await InvokeTypedResult(input, handler);

    private async Task<TResult> InvokeTypedResult<TResult>(
        TInput input,
        Func<TInput, Task<TResult>> handler)
        where TResult : IResult
    {
        MarkPublished();
        var result = await handler(input);
        TypedResultValidator.Validate(Route, SuccessStatus, null, RouteErrorResponses, result, ShouldSkipValidation);
        return result;
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

    /// <summary>
    /// Convert to an input-only endpoint (accepts a body, returns void).
    /// </summary>
    public InputRouteDefinition<TInput> Accepts<TInput>()
    {
        var def = new InputRouteDefinition<TInput>(Method, Route, SuccessStatus);
        CopyStateTo(def);
        return def;
    }

    /// <summary>
    /// Execute the endpoint handler (void — no typed output).
    /// </summary>
    public async Task<RivetResult> Invoke(Func<Task> handler)
    {
        MarkPublished();
        await handler();
        return new RivetResult(SuccessStatus);
    }

    public async Task<T1> Invoke<T1>(Func<Task<T1>> handler)
        where T1 : IResult
        => await InvokeTypedResult(handler);

    public async Task<Results<T1, T2>> Invoke<T1, T2>(Func<Task<Results<T1, T2>>> handler)
        where T1 : IResult
        where T2 : IResult
        => await InvokeTypedResult(handler);

    public async Task<Results<T1, T2, T3>> Invoke<T1, T2, T3>(Func<Task<Results<T1, T2, T3>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        => await InvokeTypedResult(handler);

    public async Task<Results<T1, T2, T3, T4>> Invoke<T1, T2, T3, T4>(Func<Task<Results<T1, T2, T3, T4>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        => await InvokeTypedResult(handler);

    public async Task<Results<T1, T2, T3, T4, T5>> Invoke<T1, T2, T3, T4, T5>(Func<Task<Results<T1, T2, T3, T4, T5>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        where T5 : IResult
        => await InvokeTypedResult(handler);

    public async Task<Results<T1, T2, T3, T4, T5, T6>> Invoke<T1, T2, T3, T4, T5, T6>(Func<Task<Results<T1, T2, T3, T4, T5, T6>>> handler)
        where T1 : IResult
        where T2 : IResult
        where T3 : IResult
        where T4 : IResult
        where T5 : IResult
        where T6 : IResult
        => await InvokeTypedResult(handler);

    private async Task<TResult> InvokeTypedResult<TResult>(Func<Task<TResult>> handler)
        where TResult : IResult
    {
        MarkPublished();
        var result = await handler();
        TypedResultValidator.Validate(Route, SuccessStatus, null, RouteErrorResponses, result, ShouldSkipValidation);
        return result;
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

    /// <summary>
    /// Sets the response content type for this file endpoint.
    /// Alias for ProducesFile — preferred on FileRouteDefinition for readability.
    /// </summary>
    public FileRouteDefinition ContentType(string mediaType)
        => ProducesFile(mediaType);

    /// <summary>
    /// Execute the endpoint handler with runtime contract validation: the success
    /// branch must carry file content matching the declared content type, error
    /// statuses must be declared. File results write their own status (200, or 206
    /// under range processing), so only their content type is checked.
    /// </summary>
    public async Task<TResult> Invoke<TResult>(Func<Task<TResult>> handler)
        where TResult : IResult
    {
        MarkPublished();
        var result = await handler();
        TypedResultValidator.ValidateFile(
            Route, SuccessStatus, FileContentType, RouteErrorResponses, result, ShouldSkipValidation);
        return result;
    }

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

    /// <summary>
    /// Sets the response content type for this file endpoint.
    /// Alias for ProducesFile — preferred on FileRouteDefinition for readability.
    /// </summary>
    public FileRouteDefinition<TInput> ContentType(string mediaType)
        => ProducesFile(mediaType);

    /// <summary>
    /// Execute the endpoint handler with runtime contract validation: the success
    /// branch must carry file content matching the declared content type, error
    /// statuses must be declared. File results write their own status (200, or 206
    /// under range processing), so only their content type is checked.
    /// </summary>
    public async Task<TResult> Invoke<TResult>(TInput input, Func<TInput, Task<TResult>> handler)
        where TResult : IResult
    {
        MarkPublished();
        var result = await handler(input);
        TypedResultValidator.ValidateFile(
            Route, SuccessStatus, FileContentType, RouteErrorResponses, result, ShouldSkipValidation);
        return result;
    }

    public static implicit operator Define(FileRouteDefinition<TInput> _) => default!;
}
