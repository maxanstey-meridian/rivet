namespace Rivet;

/// <summary>
/// Builder for endpoints with both input and output types.
/// Never executed at runtime — Roslyn reads the chain.
/// </summary>
public sealed class EndpointBuilder<TInput, TOutput>
{
    internal EndpointBuilder() { }

    public EndpointBuilder<TInput, TOutput> Returns<TResponse>(int statusCode) => this;
    public EndpointBuilder<TInput, TOutput> Returns<TResponse>(int statusCode, string description) => this;
    public EndpointBuilder<TInput, TOutput> Status(int statusCode) => this;
    public EndpointBuilder<TInput, TOutput> Description(string description) => this;
    public EndpointBuilder<TInput, TOutput> Anonymous() => this;
    public EndpointBuilder<TInput, TOutput> Secure(string scheme) => this;

    public static implicit operator Endpoint(EndpointBuilder<TInput, TOutput> _) => default!;
}

/// <summary>
/// Builder for endpoints with output only (no input type).
/// </summary>
public sealed class EndpointBuilder<TOutput>
{
    internal EndpointBuilder() { }

    public EndpointBuilder<TOutput> Returns<TResponse>(int statusCode) => this;
    public EndpointBuilder<TOutput> Returns<TResponse>(int statusCode, string description) => this;
    public EndpointBuilder<TOutput> Status(int statusCode) => this;
    public EndpointBuilder<TOutput> Description(string description) => this;
    public EndpointBuilder<TOutput> Anonymous() => this;
    public EndpointBuilder<TOutput> Secure(string scheme) => this;

    public static implicit operator Endpoint(EndpointBuilder<TOutput> _) => default!;
}

/// <summary>
/// Builder for endpoints with no typed input or output.
/// </summary>
public sealed class EndpointBuilder
{
    internal EndpointBuilder() { }

    public EndpointBuilder Returns<TResponse>(int statusCode) => this;
    public EndpointBuilder Returns<TResponse>(int statusCode, string description) => this;
    public EndpointBuilder Status(int statusCode) => this;
    public EndpointBuilder Description(string description) => this;
    public EndpointBuilder Anonymous() => this;
    public EndpointBuilder Secure(string scheme) => this;

    public static implicit operator Endpoint(EndpointBuilder _) => default!;
}
