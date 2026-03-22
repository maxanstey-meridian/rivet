using System.Text.RegularExpressions;
using Rivet.Tool.Model;

namespace Rivet.Tool;

internal static class RoutePrinter
{
    public static void Print(IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        if (endpoints.Count == 0)
        {
            Console.Error.WriteLine("0 route(s).");
            return;
        }

        var sorted = endpoints
            .OrderBy(e => e.RouteTemplate)
            .ThenBy(e => e.HttpMethod)
            .ToList();

        // Column widths
        var methodLabel = "Method";
        var routeLabel = "Route";
        var handlerLabel = "Handler";
        var methodWidth = Math.Max(methodLabel.Length, sorted.Max(e => e.HttpMethod.Length));
        var routeWidth = Math.Max(routeLabel.Length, sorted.Max(e => e.RouteTemplate.Length));

        Console.WriteLine($"  {methodLabel.PadRight(methodWidth)}  {routeLabel.PadRight(routeWidth)}  {handlerLabel}");
        Console.WriteLine($"  {"".PadRight(methodWidth, '─')}  {"".PadRight(routeWidth, '─')}  {"".PadRight(handlerLabel.Length, '─')}");

        foreach (var ep in sorted)
        {
            var method = ep.HttpMethod.PadRight(methodWidth);
            var route = ep.RouteTemplate.PadRight(routeWidth);
            var methodColor = ep.HttpMethod switch
            {
                "GET" => "\x1b[32m",     // green
                "POST" => "\x1b[33m",    // yellow
                "PUT" => "\x1b[34m",     // blue
                "PATCH" => "\x1b[36m",   // cyan
                "DELETE" => "\x1b[31m",  // red
                _ => "",
            };
            var coloredRoute = Regex.Replace(
                route, @"\{[^}]+\}", m => $"\x1b[33m{m.Value}\x1b[0m");
            Console.WriteLine($"  {methodColor}{method}\x1b[0m  {coloredRoute}  \x1b[90m{ep.ControllerName}.{ep.Name}\x1b[0m");
        }

        Console.Error.WriteLine($"{sorted.Count} route(s).");
    }
}
