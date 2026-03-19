using System;

namespace Rivet;

/// <summary>
/// Static factory for defining contract endpoints. Methods are never executed —
/// Roslyn reads the invocation chain at compile time.
/// </summary>
public sealed class Endpoint
{
    internal Endpoint() { }

    // GET
    public static EndpointBuilder<TInput, TOutput> Get<TInput, TOutput>(string route) => default!;
    public static EndpointBuilder<TOutput> Get<TOutput>(string route) => default!;
    public static EndpointBuilder Get(string route) => default!;

    // POST
    public static EndpointBuilder<TInput, TOutput> Post<TInput, TOutput>(string route) => default!;
    public static EndpointBuilder<TOutput> Post<TOutput>(string route) => default!;
    public static EndpointBuilder Post(string route) => default!;

    // PUT
    public static EndpointBuilder<TInput, TOutput> Put<TInput, TOutput>(string route) => default!;
    public static EndpointBuilder<TOutput> Put<TOutput>(string route) => default!;
    public static EndpointBuilder Put(string route) => default!;

    // PATCH
    public static EndpointBuilder<TInput, TOutput> Patch<TInput, TOutput>(string route) => default!;
    public static EndpointBuilder<TOutput> Patch<TOutput>(string route) => default!;
    public static EndpointBuilder Patch(string route) => default!;

    // DELETE
    public static EndpointBuilder<TInput, TOutput> Delete<TInput, TOutput>(string route) => default!;
    public static EndpointBuilder<TOutput> Delete<TOutput>(string route) => default!;
    public static EndpointBuilder Delete(string route) => default!;
}
