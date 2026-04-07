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

    // FILE (binary/stream endpoints — defaults to GET)
    public static FileRouteDefinition<TInput> File<TInput>(string route) => new(route);
    public static FileRouteDefinition File(string route) => new(route);
}
