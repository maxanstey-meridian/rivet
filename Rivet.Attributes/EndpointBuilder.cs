namespace Rivet;

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

    public static implicit operator Define(RouteDefinition _) => default!;
}
