using System;
using System.Threading.Tasks;

namespace Rivet;

/// <summary>
/// Route definition for endpoints with both input and output types.
/// Roslyn reads the chain at generation time. Invoke provides type-safe runtime execution.
/// </summary>
public sealed class RouteDefinition<TInput, TOutput>
{
    private int _successStatus;

    internal RouteDefinition(int defaultStatus = 200)
    {
        _successStatus = defaultStatus;
    }

    public RouteDefinition<TInput, TOutput> Returns<TResponse>(int statusCode) => this;
    public RouteDefinition<TInput, TOutput> Returns<TResponse>(int statusCode, string description) => this;

    public RouteDefinition<TInput, TOutput> Status(int statusCode)
    {
        _successStatus = statusCode;
        return this;
    }

    public RouteDefinition<TInput, TOutput> Description(string description) => this;
    public RouteDefinition<TInput, TOutput> Anonymous() => this;
    public RouteDefinition<TInput, TOutput> Secure(string scheme) => this;

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

    internal RouteDefinition(int defaultStatus = 200)
    {
        _successStatus = defaultStatus;
    }

    public RouteDefinition<TOutput> Returns<TResponse>(int statusCode) => this;
    public RouteDefinition<TOutput> Returns<TResponse>(int statusCode, string description) => this;

    public RouteDefinition<TOutput> Status(int statusCode)
    {
        _successStatus = statusCode;
        return this;
    }

    public RouteDefinition<TOutput> Description(string description) => this;
    public RouteDefinition<TOutput> Anonymous() => this;
    public RouteDefinition<TOutput> Secure(string scheme) => this;

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

    internal InputRouteDefinition(int defaultStatus = 200)
    {
        _successStatus = defaultStatus;
    }

    public InputRouteDefinition<TInput> Returns<TResponse>(int statusCode) => this;
    public InputRouteDefinition<TInput> Returns<TResponse>(int statusCode, string description) => this;

    public InputRouteDefinition<TInput> Status(int statusCode)
    {
        _successStatus = statusCode;
        return this;
    }

    public InputRouteDefinition<TInput> Description(string description) => this;
    public InputRouteDefinition<TInput> Anonymous() => this;
    public InputRouteDefinition<TInput> Secure(string scheme) => this;

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

    internal RouteDefinition(int defaultStatus = 200)
    {
        _successStatus = defaultStatus;
    }

    public RouteDefinition Returns<TResponse>(int statusCode) => this;
    public RouteDefinition Returns<TResponse>(int statusCode, string description) => this;

    public RouteDefinition Status(int statusCode)
    {
        _successStatus = statusCode;
        return this;
    }

    public RouteDefinition Description(string description) => this;
    public RouteDefinition Anonymous() => this;
    public RouteDefinition Secure(string scheme) => this;

    /// <summary>
    /// Convert to an input-only endpoint (accepts a body, returns void).
    /// </summary>
    public InputRouteDefinition<TInput> Accepts<TInput>()
        => new InputRouteDefinition<TInput>(_successStatus);

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
