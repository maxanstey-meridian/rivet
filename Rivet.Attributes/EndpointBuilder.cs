using System;
using System.Threading.Tasks;

namespace Rivet;

/// <summary>
/// Builder for endpoints with both input and output types.
/// Roslyn reads the chain at generation time. Invoke provides type-safe runtime execution.
/// </summary>
public sealed class EndpointBuilder<TInput, TOutput>
{
    private int _successStatus;

    internal EndpointBuilder(int defaultStatus = 200)
    {
        _successStatus = defaultStatus;
    }

    public EndpointBuilder<TInput, TOutput> Returns<TResponse>(int statusCode) => this;
    public EndpointBuilder<TInput, TOutput> Returns<TResponse>(int statusCode, string description) => this;

    public EndpointBuilder<TInput, TOutput> Status(int statusCode)
    {
        _successStatus = statusCode;
        return this;
    }

    public EndpointBuilder<TInput, TOutput> Description(string description) => this;
    public EndpointBuilder<TInput, TOutput> Anonymous() => this;
    public EndpointBuilder<TInput, TOutput> Secure(string scheme) => this;

    /// <summary>
    /// Execute the endpoint handler with type-safe input and output.
    /// </summary>
    public async Task<RivetResult<TOutput>> Invoke(TInput input, Func<TInput, Task<TOutput>> handler)
    {
        var result = await handler(input);
        return new RivetResult<TOutput>(_successStatus, result);
    }

    public static implicit operator Endpoint(EndpointBuilder<TInput, TOutput> _) => default!;
}

/// <summary>
/// Builder for endpoints with output only (no input type).
/// </summary>
public sealed class EndpointBuilder<TOutput>
{
    private int _successStatus;

    internal EndpointBuilder(int defaultStatus = 200)
    {
        _successStatus = defaultStatus;
    }

    public EndpointBuilder<TOutput> Returns<TResponse>(int statusCode) => this;
    public EndpointBuilder<TOutput> Returns<TResponse>(int statusCode, string description) => this;

    public EndpointBuilder<TOutput> Status(int statusCode)
    {
        _successStatus = statusCode;
        return this;
    }

    public EndpointBuilder<TOutput> Description(string description) => this;
    public EndpointBuilder<TOutput> Anonymous() => this;
    public EndpointBuilder<TOutput> Secure(string scheme) => this;

    /// <summary>
    /// Execute the endpoint handler with type-safe output.
    /// </summary>
    public async Task<RivetResult<TOutput>> Invoke(Func<Task<TOutput>> handler)
    {
        var result = await handler();
        return new RivetResult<TOutput>(_successStatus, result);
    }

    public static implicit operator Endpoint(EndpointBuilder<TOutput> _) => default!;
}

/// <summary>
/// Builder for endpoints with no typed input or output.
/// </summary>
public sealed class EndpointBuilder
{
    private int _successStatus;

    internal EndpointBuilder(int defaultStatus = 200)
    {
        _successStatus = defaultStatus;
    }

    public EndpointBuilder Returns<TResponse>(int statusCode) => this;
    public EndpointBuilder Returns<TResponse>(int statusCode, string description) => this;

    public EndpointBuilder Status(int statusCode)
    {
        _successStatus = statusCode;
        return this;
    }

    public EndpointBuilder Description(string description) => this;
    public EndpointBuilder Anonymous() => this;
    public EndpointBuilder Secure(string scheme) => this;

    /// <summary>
    /// Execute the endpoint handler (void — no typed output).
    /// </summary>
    public async Task<RivetResult> Invoke(Func<Task> handler)
    {
        await handler();
        return new RivetResult(_successStatus);
    }

    public static implicit operator Endpoint(EndpointBuilder _) => default!;
}
