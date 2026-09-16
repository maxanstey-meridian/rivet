using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Merges contract-walker endpoints with annotation-walker (controller) endpoints.
/// Identity is the normalized transport identity (HTTP method + normalized route),
/// not the source (ControllerName, Name) — same-named overloads at distinct routes
/// survive, and a same (method, route) collision between frontends collapses only
/// when the two declarations are equivalent. Contradictory declarations of the same
/// transport identity are a hard error naming both sources; they can never be
/// resolved by first-wins/last-wins.
/// This is the single production merge used by Program.cs — tests exercise the same code.
/// </summary>
public static class EndpointMerger
{
    /// <summary>
    /// Thrown when two non-equivalent endpoint declarations share one transport
    /// identity. Program.cs routes this through the established stderr + exit 1
    /// pattern so --routes cannot print and return success.
    /// </summary>
    public sealed class TransportConflictException(string message)
        : InvalidOperationException(message);

    public static IReadOnlyList<TsEndpointDefinition> Merge(
        IReadOnlyList<TsEndpointDefinition> contractEndpoints,
        IReadOnlyList<TsEndpointDefinition> annotationEndpoints
    )
    {
        var merged = new List<TsEndpointDefinition>(
            contractEndpoints.Count + annotationEndpoints.Count
        );
        var byTransportIdentity =
            new Dictionary<(string Method, string Route), TsEndpointDefinition>();

        // Contract-first ordering is the deterministic representative choice for
        // equivalent declarations (same precedence as the previous (ControllerName,
        // Name) merge): contract endpoints are seeded first, annotation endpoints
        // only fill transport identities no contract endpoint declared.
        foreach (var endpoint in contractEndpoints.Concat(annotationEndpoints))
        {
            var key = TransportIdentity.Key(endpoint.HttpMethod, endpoint.RouteTemplate);
            if (byTransportIdentity.TryGetValue(key, out var existing))
            {
                if (EndpointSurfaceEquivalent(existing, endpoint))
                {
                    // Equivalent representations of one operation collapse; the first
                    // (contract-first) representative stays.
                    continue;
                }

                throw new TransportConflictException(
                    $"error {Diagnostics.ConflictingOperations}: transport identity {key.Method} {key.Route} is declared by two incompatible endpoints: "
                        + $"'{existing.ControllerName}.{existing.Name}' and '{endpoint.ControllerName}.{endpoint.Name}'. "
                        + "Resolve the contradiction at the source — first-wins/last-wins cannot resolve conflicting declarations."
                );
            }

            byTransportIdentity[key] = endpoint;
            merged.Add(endpoint);
        }

        return merged;
    }

    /// <summary>
    /// True when two endpoint definitions describe the same supported HTTP surface:
    /// identical declared responses (status, payload shape, headers, contents) and
    /// an equivalent request-body declaration. Source identity (names) is
    /// deliberately excluded — it is diagnostic context, not part of the surface.
    /// </summary>
    internal static bool EndpointSurfaceEquivalent(
        TsEndpointDefinition left,
        TsEndpointDefinition right
    )
    {
        if (left.Responses.Count != right.Responses.Count)
        {
            return false;
        }

        var leftResponses = left
            .Responses.OrderBy(
                response => response.EffectiveStatusKey,
                StringComparer.OrdinalIgnoreCase
            )
            .ToList();
        var rightResponses = right
            .Responses.OrderBy(
                response => response.EffectiveStatusKey,
                StringComparer.OrdinalIgnoreCase
            )
            .ToList();

        for (var index = 0; index < leftResponses.Count; index++)
        {
            if (!ResponseEquivalent(leftResponses[index], rightResponses[index]))
            {
                return false;
            }
        }

        return RequestSurfaceEquivalent(left, right);
    }

    private static bool ResponseEquivalent(TsResponseType left, TsResponseType right)
    {
        if (
            !string.Equals(
                left.EffectiveStatusKey,
                right.EffectiveStatusKey,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        if (left.StatusCode != right.StatusCode)
        {
            return false;
        }

        if (!TsTypeEquivalent(left.DataType, right.DataType))
        {
            return false;
        }

        if (!ContentEquivalent(left.Contents, right.Contents))
        {
            return false;
        }

        return HeadersEquivalent(left.Headers, right.Headers);
    }

    private static bool HeadersEquivalent(
        IReadOnlyList<TsResponseHeader>? left,
        IReadOnlyList<TsResponseHeader>? right
    )
    {
        if ((left is null) != (right is null))
        {
            return false;
        }

        if (left is null)
        {
            return true;
        }

        if (left.Count != right!.Count)
        {
            return false;
        }

        return left.OrderBy(header => header.Name, StringComparer.Ordinal)
            .Zip(
                right.OrderBy(header => header.Name, StringComparer.Ordinal),
                (leftHeader, rightHeader) =>
                    JsonSerializer.Serialize(leftHeader) == JsonSerializer.Serialize(rightHeader)
            )
            .All(equal => equal);
    }

    private static bool ContentEquivalent(
        IReadOnlyList<TsMediaTypeContent>? left,
        IReadOnlyList<TsMediaTypeContent>? right
    )
    {
        if ((left is null) != (right is null))
        {
            return false;
        }

        if (left is null)
        {
            return true;
        }

        if (left.Count != right!.Count)
        {
            return false;
        }

        return left.OrderBy(content => content.MediaType, StringComparer.Ordinal)
            .Zip(
                right.OrderBy(content => content.MediaType, StringComparer.Ordinal),
                (leftContent, rightContent) =>
                    JsonSerializer.Serialize(leftContent) == JsonSerializer.Serialize(rightContent)
            )
            .All(equal => equal);
    }

    private static bool RequestSurfaceEquivalent(
        TsEndpointDefinition left,
        TsEndpointDefinition right
    )
    {
        if (left.RequestBodyRequired != right.RequestBodyRequired)
        {
            return false;
        }

        if (left.RequestBodyPresent != right.RequestBodyPresent)
        {
            return false;
        }

        if (left.IsFormEncoded != right.IsFormEncoded)
        {
            return false;
        }

        return RequestContentsEquivalent(left.RequestContents, right.RequestContents);
    }

    private static bool RequestContentsEquivalent(
        IReadOnlyList<TsMediaTypeContent>? left,
        IReadOnlyList<TsMediaTypeContent>? right
    )
    {
        if ((left is null) != (right is null))
        {
            return false;
        }

        if (left is null)
        {
            return true;
        }

        if (left.Count != right!.Count)
        {
            return false;
        }

        return left.OrderBy(content => content.MediaType, StringComparer.Ordinal)
            .Zip(
                right.OrderBy(content => content.MediaType, StringComparer.Ordinal),
                (leftContent, rightContent) =>
                    JsonSerializer.Serialize(leftContent) == JsonSerializer.Serialize(rightContent)
            )
            .All(equal => equal);
    }

    private static bool TsTypeEquivalent(TsType? left, TsType? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    }
}
