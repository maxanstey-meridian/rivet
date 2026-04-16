namespace Rivet;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

/// <summary>
/// Describes an additional (non-success) response declared via .Returns&lt;T&gt;().
/// </summary>
public sealed record RouteErrorResponse(int StatusCode, Type? ResponseType, string? Description);

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
    private string? _queryAuthParameterName;
    private List<RouteErrorResponse>? _errorResponses;

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
    public bool IsQueryAuth => _queryAuthParameterName is not null;
    public string? QueryAuthParameterName => _queryAuthParameterName;
    public IReadOnlyList<RouteErrorResponse>? RouteErrorResponses => _errorResponses;

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
        target._queryAuthParameterName = _queryAuthParameterName;
        target._errorResponses = _errorResponses?.ToList();
    }

    public TSelf Summary(string summary)
    {
        _summary = summary;
        return (TSelf)this;
    }

    public TSelf Description(string description)
    {
        _description = description;
        return (TSelf)this;
    }

    public TSelf Status(int statusCode)
    {
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
        _formEncoded = true;
        return (TSelf)this;
    }

    public TSelf Returns<TResponse>(int statusCode)
        => Returns<TResponse>(statusCode, null);

    public TSelf Returns<TResponse>(int statusCode, string? description)
    {
        _errorResponses ??= [];
        _errorResponses.Add(new RouteErrorResponse(statusCode, typeof(TResponse), description));
        return (TSelf)this;
    }

    public TSelf Returns(int statusCode)
        => Returns(statusCode, null);

    public TSelf Returns(int statusCode, string? description)
    {
        _errorResponses ??= [];
        _errorResponses.Add(new RouteErrorResponse(statusCode, null, description));
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
        _anonymous = true;
        return (TSelf)this;
    }

    public TSelf Secure(string scheme)
    {
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
        _queryAuthParameterName = parameterName;
        return (TSelf)this;
    }

    /// <summary>
    /// Marks this endpoint as returning a file download instead of JSON.
    /// The generated TS client returns Blob; the OpenAPI spec emits the given content type with format: binary.
    /// </summary>
    public TSelf ProducesFile(string contentType = "application/octet-stream")
    {
        _fileContentType = contentType;
        return (TSelf)this;
    }

    /// <summary>
    /// Marks this endpoint as accepting a file upload (multipart/form-data).
    /// The generated TS client will accept a File parameter.
    /// </summary>
    public TSelf AcceptsFile()
    {
        _acceptsFile = true;
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
        var result = await handler(input);
        TypedResultValidator.Validate(Route, SuccessStatus, successResponseType, RouteErrorResponses, result);
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
        var result = await handler();
        return new RivetResult<TOutput>(SuccessStatus, result);
    }

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
        var result = await handler();
        TypedResultValidator.Validate(Route, SuccessStatus, successResponseType, RouteErrorResponses, result);
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
        await handler(input);
        return new RivetResult(SuccessStatus);
    }

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
        var result = await handler(input);
        TypedResultValidator.Validate(Route, SuccessStatus, null, RouteErrorResponses, result);
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
        await handler();
        return new RivetResult(SuccessStatus);
    }

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
        var result = await handler();
        TypedResultValidator.Validate(Route, SuccessStatus, null, RouteErrorResponses, result);
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

    public static implicit operator Define(FileRouteDefinition<TInput> _) => default!;
}
