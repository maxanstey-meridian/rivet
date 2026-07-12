using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Merges contract-walker endpoints with annotation-walker (controller) endpoints.
/// Contract endpoints win on a (ControllerName, Name) collision.
/// This is the single production merge used by Program.cs — tests exercise the same code.
/// </summary>
public static class EndpointMerger
{
    public static IReadOnlyList<TsEndpointDefinition> Merge(
        IReadOnlyList<TsEndpointDefinition> contractEndpoints,
        IReadOnlyList<TsEndpointDefinition> annotationEndpoints
    )
    {
        var seen = new HashSet<(string, string)>(
            contractEndpoints.Select(e => (e.ControllerName, e.Name))
        );
        var merged = new List<TsEndpointDefinition>(contractEndpoints);
        foreach (var ep in annotationEndpoints)
        {
            if (seen.Add((ep.ControllerName, ep.Name)))
            {
                merged.Add(ep);
            }
        }

        return merged;
    }
}
