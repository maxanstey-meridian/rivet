namespace Rivet;

/// <summary>
/// Static factory for defining contract endpoints. Factory methods create typed builders
/// that Roslyn reads at generation time and that provide type-safe Invoke at runtime.
/// </summary>
public sealed class Endpoint
{
    internal Endpoint() { }

    // GET
    public static EndpointBuilder<TInput, TOutput> Get<TInput, TOutput>(string route) => new EndpointBuilder<TInput, TOutput>();
    public static EndpointBuilder<TOutput> Get<TOutput>(string route) => new EndpointBuilder<TOutput>();
    public static EndpointBuilder Get(string route) => new EndpointBuilder();

    // POST
    public static EndpointBuilder<TInput, TOutput> Post<TInput, TOutput>(string route) => new EndpointBuilder<TInput, TOutput>(201);
    public static EndpointBuilder<TOutput> Post<TOutput>(string route) => new EndpointBuilder<TOutput>(201);
    public static EndpointBuilder Post(string route) => new EndpointBuilder(201);

    // PUT
    public static EndpointBuilder<TInput, TOutput> Put<TInput, TOutput>(string route) => new EndpointBuilder<TInput, TOutput>();
    public static EndpointBuilder<TOutput> Put<TOutput>(string route) => new EndpointBuilder<TOutput>();
    public static EndpointBuilder Put(string route) => new EndpointBuilder();

    // PATCH
    public static EndpointBuilder<TInput, TOutput> Patch<TInput, TOutput>(string route) => new EndpointBuilder<TInput, TOutput>();
    public static EndpointBuilder<TOutput> Patch<TOutput>(string route) => new EndpointBuilder<TOutput>();
    public static EndpointBuilder Patch(string route) => new EndpointBuilder();

    // DELETE
    public static EndpointBuilder<TInput, TOutput> Delete<TInput, TOutput>(string route) => new EndpointBuilder<TInput, TOutput>();
    public static EndpointBuilder<TOutput> Delete<TOutput>(string route) => new EndpointBuilder<TOutput>();
    public static EndpointBuilder Delete(string route) => new EndpointBuilder();
}
