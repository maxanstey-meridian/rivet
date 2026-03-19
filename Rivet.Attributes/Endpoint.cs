namespace Rivet;

/// <summary>
/// Static factory for defining contract endpoints. Factory methods create typed route definitions
/// that Roslyn reads at generation time and that provide type-safe Invoke at runtime.
/// </summary>
public sealed class Define
{
    internal Define() { }

    // GET
    public static RouteDefinition<TInput, TOutput> Get<TInput, TOutput>(string route) => new("GET", route);
    public static RouteDefinition<TOutput> Get<TOutput>(string route) => new("GET", route);
    public static RouteDefinition Get(string route) => new("GET", route);

    // POST
    public static RouteDefinition<TInput, TOutput> Post<TInput, TOutput>(string route) => new("POST", route, 201);
    public static RouteDefinition<TOutput> Post<TOutput>(string route) => new("POST", route, 201);
    public static RouteDefinition Post(string route) => new("POST", route, 201);

    // PUT
    public static RouteDefinition<TInput, TOutput> Put<TInput, TOutput>(string route) => new("PUT", route);
    public static RouteDefinition<TOutput> Put<TOutput>(string route) => new("PUT", route);
    public static RouteDefinition Put(string route) => new("PUT", route);

    // PATCH
    public static RouteDefinition<TInput, TOutput> Patch<TInput, TOutput>(string route) => new("PATCH", route);
    public static RouteDefinition<TOutput> Patch<TOutput>(string route) => new("PATCH", route);
    public static RouteDefinition Patch(string route) => new("PATCH", route);

    // DELETE
    public static RouteDefinition<TInput, TOutput> Delete<TInput, TOutput>(string route) => new("DELETE", route);
    public static RouteDefinition<TOutput> Delete<TOutput>(string route) => new("DELETE", route);
    public static RouteDefinition Delete(string route) => new("DELETE", route);
}

// Backwards compatibility — existing code using Endpoint.Get etc. continues to work
// TODO: Remove in next major version
/// <summary>Deprecated: use Define instead.</summary>
public sealed class Endpoint
{
    internal Endpoint() { }

    public static RouteDefinition<TInput, TOutput> Get<TInput, TOutput>(string route) => Define.Get<TInput, TOutput>(route);
    public static RouteDefinition<TOutput> Get<TOutput>(string route) => Define.Get<TOutput>(route);
    public static RouteDefinition Get(string route) => Define.Get(route);

    public static RouteDefinition<TInput, TOutput> Post<TInput, TOutput>(string route) => Define.Post<TInput, TOutput>(route);
    public static RouteDefinition<TOutput> Post<TOutput>(string route) => Define.Post<TOutput>(route);
    public static RouteDefinition Post(string route) => Define.Post(route);

    public static RouteDefinition<TInput, TOutput> Put<TInput, TOutput>(string route) => Define.Put<TInput, TOutput>(route);
    public static RouteDefinition<TOutput> Put<TOutput>(string route) => Define.Put<TOutput>(route);
    public static RouteDefinition Put(string route) => Define.Put(route);

    public static RouteDefinition<TInput, TOutput> Patch<TInput, TOutput>(string route) => Define.Patch<TInput, TOutput>(route);
    public static RouteDefinition<TOutput> Patch<TOutput>(string route) => Define.Patch<TOutput>(route);
    public static RouteDefinition Patch(string route) => Define.Patch(route);

    public static RouteDefinition<TInput, TOutput> Delete<TInput, TOutput>(string route) => Define.Delete<TInput, TOutput>(route);
    public static RouteDefinition<TOutput> Delete<TOutput>(string route) => Define.Delete<TOutput>(route);
    public static RouteDefinition Delete(string route) => Define.Delete(route);
}
