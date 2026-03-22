namespace Rivet;

/// <summary>
/// Describes an additional (non-success) response declared via .Returns&lt;T&gt;().
/// </summary>
public sealed record ErrorResponse(int StatusCode, Type ResponseType, string? Description);

/// <summary>
/// Route definition for endpoints with both input and output types.
/// Roslyn reads the chain at generation time. Invoke provides type-safe runtime execution.
/// </summary>
public sealed class RouteDefinition<TInput, TOutput>
{
    private int _successStatus;
    private bool _statusSet;
    private string? _description;
    private bool _anonymous;
    private string? _securityScheme;
    private string? _fileContentType;
    private bool _acceptsFile;
    private List<ErrorResponse>? _errorResponses;

    /// <summary>The HTTP method (GET, POST, PUT, PATCH, DELETE).</summary>
    public string Method { get; }

    /// <summary>The route template from the contract definition.</summary>
    public string Route { get; }

    public string? EndpointDescription => _description;
    public bool IsAnonymous => _anonymous;
    public string? SecurityScheme => _securityScheme;
    public string? FileContentType => _fileContentType;
    public bool IsFileUpload => _acceptsFile;
    public IReadOnlyList<ErrorResponse>? ErrorResponses => _errorResponses;

    internal RouteDefinition(string method = "GET", string route = "", int defaultStatus = 200)
    {
        Method = method;
        Route = route;
        _successStatus = defaultStatus;
    }

    public RouteDefinition<TInput, TOutput> Returns<TResponse>(int statusCode)
        => Returns<TResponse>(statusCode, null);

    public RouteDefinition<TInput, TOutput> Returns<TResponse>(int statusCode, string? description)
    {
        _errorResponses ??= [];
        _errorResponses.Add(new ErrorResponse(statusCode, typeof(TResponse), description));
        return this;
    }

    public RouteDefinition<TInput, TOutput> Status(int statusCode)
    {
        if (_statusSet)
        {
            throw new InvalidOperationException($"Status already set to {_successStatus} — cannot set to {statusCode}. Call .Status() only once.");
        }

        _successStatus = statusCode;
        _statusSet = true;
        return this;
    }

    public RouteDefinition<TInput, TOutput> Description(string description)
    {
        _description = description;
        return this;
    }

    public RouteDefinition<TInput, TOutput> Anonymous()
    {
        _anonymous = true;
        return this;
    }

    public RouteDefinition<TInput, TOutput> Secure(string scheme)
    {
        _securityScheme = scheme;
        return this;
    }

    public RouteDefinition<TInput, TOutput> ProducesFile(string contentType = "application/octet-stream")
    {
        _fileContentType = contentType;
        return this;
    }

    /// <summary>
    /// Marks this endpoint as accepting a file upload (multipart/form-data).
    /// The generated TS client will accept a File parameter.
    /// </summary>
    public RouteDefinition<TInput, TOutput> AcceptsFile()
    {
        _acceptsFile = true;
        return this;
    }

    /// <summary>
    /// Execute the endpoint handler with type-safe input and output.
    /// </summary>
    public async Task<RivetResult<TOutput>> Invoke(TInput input, Func<TInput, Task<TOutput>> handler)
    {
        var result = await handler(input);
        return new RivetResult<TOutput>(_successStatus, result);
    }

    public static implicit operator Define(RouteDefinition<TInput, TOutput> _) => default!;
}

/// <summary>
/// Route definition for endpoints with output only (no input type).
/// </summary>
public sealed class RouteDefinition<TOutput>
{
    private int _successStatus;
    private bool _statusSet;
    private string? _description;
    private bool _anonymous;
    private string? _securityScheme;
    private string? _fileContentType;
    private bool _acceptsFile;
    private List<ErrorResponse>? _errorResponses;

    /// <summary>The HTTP method (GET, POST, PUT, PATCH, DELETE).</summary>
    public string Method { get; }

    /// <summary>The route template from the contract definition.</summary>
    public string Route { get; }

    public string? EndpointDescription => _description;
    public bool IsAnonymous => _anonymous;
    public string? SecurityScheme => _securityScheme;
    public string? FileContentType => _fileContentType;
    public bool IsFileUpload => _acceptsFile;
    public IReadOnlyList<ErrorResponse>? ErrorResponses => _errorResponses;

    internal RouteDefinition(string method = "GET", string route = "", int defaultStatus = 200)
    {
        Method = method;
        Route = route;
        _successStatus = defaultStatus;
    }

    public RouteDefinition<TOutput> Returns<TResponse>(int statusCode)
        => Returns<TResponse>(statusCode, null);

    public RouteDefinition<TOutput> Returns<TResponse>(int statusCode, string? description)
    {
        _errorResponses ??= [];
        _errorResponses.Add(new ErrorResponse(statusCode, typeof(TResponse), description));
        return this;
    }

    public RouteDefinition<TOutput> Status(int statusCode)
    {
        if (_statusSet)
        {
            throw new InvalidOperationException($"Status already set to {_successStatus} — cannot set to {statusCode}. Call .Status() only once.");
        }

        _successStatus = statusCode;
        _statusSet = true;
        return this;
    }

    public RouteDefinition<TOutput> Description(string description)
    {
        _description = description;
        return this;
    }

    public RouteDefinition<TOutput> Anonymous()
    {
        _anonymous = true;
        return this;
    }

    public RouteDefinition<TOutput> Secure(string scheme)
    {
        _securityScheme = scheme;
        return this;
    }

    public RouteDefinition<TOutput> ProducesFile(string contentType = "application/octet-stream")
    {
        _fileContentType = contentType;
        return this;
    }

    /// <summary>
    /// Marks this endpoint as accepting a file upload (multipart/form-data).
    /// The generated TS client will accept a File parameter.
    /// </summary>
    public RouteDefinition<TOutput> AcceptsFile()
    {
        _acceptsFile = true;
        return this;
    }

    /// <summary>
    /// Execute the endpoint handler with type-safe output.
    /// </summary>
    public async Task<RivetResult<TOutput>> Invoke(Func<Task<TOutput>> handler)
    {
        var result = await handler();
        return new RivetResult<TOutput>(_successStatus, result);
    }

    public static implicit operator Define(RouteDefinition<TOutput> _) => default!;
}

/// <summary>
/// Route definition for endpoints with input only (no typed output — e.g. PUT/PATCH returning 204).
/// Chain from void definition via .Accepts&lt;T&gt;().
/// </summary>
public sealed class InputRouteDefinition<TInput>
{
    private int _successStatus;
    private bool _statusSet;
    internal string? _description;
    internal bool _anonymous;
    internal string? _securityScheme;
    internal string? _fileContentType;
    internal bool _acceptsFile;
    internal List<ErrorResponse>? _errorResponses;

    /// <summary>The HTTP method (GET, POST, PUT, PATCH, DELETE).</summary>
    public string Method { get; }

    /// <summary>The route template from the contract definition.</summary>
    public string Route { get; }

    public string? EndpointDescription => _description;
    public bool IsAnonymous => _anonymous;
    public string? SecurityScheme => _securityScheme;
    public string? FileContentType => _fileContentType;
    public bool IsFileUpload => _acceptsFile;
    public IReadOnlyList<ErrorResponse>? ErrorResponses => _errorResponses;

    internal InputRouteDefinition(string method = "GET", string route = "", int defaultStatus = 200)
    {
        Method = method;
        Route = route;
        _successStatus = defaultStatus;
    }

    public InputRouteDefinition<TInput> Returns<TResponse>(int statusCode)
        => Returns<TResponse>(statusCode, null);

    public InputRouteDefinition<TInput> Returns<TResponse>(int statusCode, string? description)
    {
        _errorResponses ??= [];
        _errorResponses.Add(new ErrorResponse(statusCode, typeof(TResponse), description));
        return this;
    }

    public InputRouteDefinition<TInput> Status(int statusCode)
    {
        if (_statusSet)
        {
            throw new InvalidOperationException($"Status already set to {_successStatus} — cannot set to {statusCode}. Call .Status() only once.");
        }

        _successStatus = statusCode;
        _statusSet = true;
        return this;
    }

    public InputRouteDefinition<TInput> Description(string description)
    {
        _description = description;
        return this;
    }

    public InputRouteDefinition<TInput> Anonymous()
    {
        _anonymous = true;
        return this;
    }

    public InputRouteDefinition<TInput> Secure(string scheme)
    {
        _securityScheme = scheme;
        return this;
    }

    public InputRouteDefinition<TInput> ProducesFile(string contentType = "application/octet-stream")
    {
        _fileContentType = contentType;
        return this;
    }

    /// <summary>
    /// Marks this endpoint as accepting a file upload (multipart/form-data).
    /// The generated TS client will accept a File parameter.
    /// </summary>
    public InputRouteDefinition<TInput> AcceptsFile()
    {
        _acceptsFile = true;
        return this;
    }

    /// <summary>
    /// Execute the endpoint handler with type-safe input (void output).
    /// </summary>
    public async Task<RivetResult> Invoke(TInput input, Func<TInput, Task> handler)
    {
        await handler(input);
        return new RivetResult(_successStatus);
    }

    public static implicit operator Define(InputRouteDefinition<TInput> _) => default!;
}

/// <summary>
/// Route definition for endpoints with no typed input or output.
/// </summary>
public sealed class RouteDefinition
{
    private int _successStatus;
    private bool _statusSet;
    private string? _description;
    private bool _anonymous;
    private string? _securityScheme;
    private string? _fileContentType;
    private bool _acceptsFile;
    private List<ErrorResponse>? _errorResponses;

    /// <summary>The HTTP method (GET, POST, PUT, PATCH, DELETE).</summary>
    public string Method { get; }

    /// <summary>The route template from the contract definition.</summary>
    public string Route { get; }

    public string? EndpointDescription => _description;
    public bool IsAnonymous => _anonymous;
    public string? SecurityScheme => _securityScheme;
    public string? FileContentType => _fileContentType;
    public bool IsFileUpload => _acceptsFile;
    public IReadOnlyList<ErrorResponse>? ErrorResponses => _errorResponses;

    internal RouteDefinition(string method = "GET", string route = "", int defaultStatus = 200)
    {
        Method = method;
        Route = route;
        _successStatus = defaultStatus;
    }

    public RouteDefinition Returns<TResponse>(int statusCode)
        => Returns<TResponse>(statusCode, null);

    public RouteDefinition Returns<TResponse>(int statusCode, string? description)
    {
        _errorResponses ??= [];
        _errorResponses.Add(new ErrorResponse(statusCode, typeof(TResponse), description));
        return this;
    }

    public RouteDefinition Status(int statusCode)
    {
        if (_statusSet)
        {
            throw new InvalidOperationException($"Status already set to {_successStatus} — cannot set to {statusCode}. Call .Status() only once.");
        }

        _successStatus = statusCode;
        _statusSet = true;
        return this;
    }

    public RouteDefinition Description(string description)
    {
        _description = description;
        return this;
    }

    public RouteDefinition Anonymous()
    {
        _anonymous = true;
        return this;
    }

    public RouteDefinition Secure(string scheme)
    {
        _securityScheme = scheme;
        return this;
    }

    /// <summary>
    /// Marks this endpoint as returning a file download instead of JSON.
    /// The generated TS client returns Blob; the OpenAPI spec emits the given content type with format: binary.
    /// </summary>
    public RouteDefinition ProducesFile(string contentType = "application/octet-stream")
    {
        _fileContentType = contentType;
        return this;
    }

    /// <summary>
    /// Marks this endpoint as accepting a file upload (multipart/form-data).
    /// The generated TS client will accept a File parameter.
    /// </summary>
    public RouteDefinition AcceptsFile()
    {
        _acceptsFile = true;
        return this;
    }

    /// <summary>
    /// Convert to an input-only endpoint (accepts a body, returns void).
    /// </summary>
    public InputRouteDefinition<TInput> Accepts<TInput>()
    {
        var def = new InputRouteDefinition<TInput>(Method, Route, _successStatus);
        def._description = _description;
        def._anonymous = _anonymous;
        def._securityScheme = _securityScheme;
        def._fileContentType = _fileContentType;
        def._acceptsFile = _acceptsFile;
        def._errorResponses = _errorResponses?.ToList();
        return def;
    }

    /// <summary>
    /// Execute the endpoint handler (void — no typed output).
    /// </summary>
    public async Task<RivetResult> Invoke(Func<Task> handler)
    {
        await handler();
        return new RivetResult(_successStatus);
    }

    public static implicit operator Define(RouteDefinition _) => default!;
}
