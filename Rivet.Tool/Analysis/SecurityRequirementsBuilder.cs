using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Accumulates ordered security requirements, from the builder chain or from assembly
/// attributes. Each declared order is one requirement (an alternative); its schemes keep
/// declaration order, and scopes added for the same scheme accumulate.
/// </summary>
internal sealed class SecurityRequirementsBuilder
{
    private readonly HashSet<int> _orders = [];
    private readonly Dictionary<int, Dictionary<string, List<string>>> _schemes = [];

    public void AddRequirement(int order) => _orders.Add(order);

    public void AddScheme(int order, string scheme, IEnumerable<string> scopes)
    {
        if (!_schemes.TryGetValue(order, out var schemes))
        {
            schemes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            _schemes.Add(order, schemes);
        }
        if (!schemes.TryGetValue(scheme, out var schemeScopes))
        {
            schemeScopes = [];
            schemes.Add(scheme, schemeScopes);
        }
        schemeScopes.AddRange(scopes);
    }

    /// <summary>The declared requirements in order, or null when none was declared.</summary>
    public SecurityRequirements? Build() =>
        _orders.Count == 0
            ? null
            : new SecurityRequirements(
                _orders
                    .Order()
                    .Select(order => new SecurityRequirement(
                        (_schemes.GetValueOrDefault(order) ?? [])
                            .Select(pair => new SecurityRequirementScheme(pair.Key, pair.Value))
                            .ToList()
                    ))
                    .ToList()
            );
}
