namespace Rivet.Tool.Analysis;

/// <summary>
/// Canonical transport identity for endpoints: (HTTP method, normalized route).
/// The normalization is the single shared source used by both the endpoint merger
/// and the OpenAPI emitter, mirroring CoverageChecker's route normalization
/// (RouteParser.StripRouteConstraints plus slash trim) so the layers cannot drift.
/// (ControllerName, Name) is source identity, never transport identity —
/// overloaded actions and cross-frontend declarations must not collapse on it.
/// </summary>
public static class TransportIdentity
{
    /// <summary>
    /// Normalizes a route template to its canonical transport form: route constraints
    /// stripped ({id:guid} → {id}) and edge slashes trimmed with a leading slash
    /// restored — identical to CoverageChecker.NormalizeRoute.
    /// </summary>
    public static string NormalizeRoute(string route)
    {
        route = RouteParser.StripRouteConstraints(route);
        return "/" + route.Trim('/');
    }

    /// <summary>
    /// The canonical (method, route) identity key for an endpoint. HTTP method is
    /// upper-invariant; the route uses <see cref="NormalizeRoute"/>; the comparison
    /// is ordinal. Case-insensitive route folding (as in RoutesMatch) is not applied
    /// here: emitted path keys are case-sensitive wire artifacts.
    /// </summary>
    public static (string Method, string Route) Key(string httpMethod, string routeTemplate) =>
        (httpMethod.ToUpperInvariant(), NormalizeRoute(routeTemplate));
}
