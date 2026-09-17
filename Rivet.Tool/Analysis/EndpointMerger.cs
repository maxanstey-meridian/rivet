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
    /// identical declared responses (status, payload shape, headers, contents), an
    /// equivalent request-body declaration, the same set of (wire name, source,
    /// mapped type) parameters with per-name requiredness, and the same resolved
    /// declared media types on the file/content-type axes. Source identity (names)
    /// is deliberately excluded — it is diagnostic context, not part of the surface.
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

        return RequestSurfaceEquivalent(left, right)
            && ParamsEquivalent(left.Params, right.Params)
            && FileContentTypeEquivalent(left.FileContentType, right.FileContentType)
            && ContentTypeOverrideEquivalent(
                left.RequestContentTypeOverride,
                right.RequestContentTypeOverride
            )
            && ContentTypeOverrideEquivalent(
                left.ResponseContentTypeOverride,
                right.ResponseContentTypeOverride
            );
    }

    /// <summary>
    /// Parameter-surface equivalence: the same set of (wire name, source, mapped type)
    /// entries with per-name requiredness agreement, compared order-independently.
    /// Wire names (not source identifiers) are what an OpenAPI consumer observes, so a
    /// [FromQuery(Name=)] rename or an MVC-inferred source is part of the surface.
    /// </summary>
    private static bool ParamsEquivalent(
        IReadOnlyList<TsEndpointParam> left,
        IReadOnlyList<TsEndpointParam> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        // Strict multiset pairing: each left entry is matched with at most one
        // unconsumed right entry on the full observable key (wire name, source,
        // mapped type, requiredness). A duplicate key on one side cannot
        // double-match a single right entry, so duplicate-vs-distinct
        // contradictions fail instead of collapsing silently.
        var rightCandidates = right.Select(param => (Param: param, Key: ParamKey(param))).ToList();
        var consumed = new bool[rightCandidates.Count];
        foreach (var param in left)
        {
            var key = ParamKey(param);
            var matched = false;
            for (var index = 0; index < rightCandidates.Count; index++)
            {
                var candidate = rightCandidates[index];
                if (
                    consumed[index]
                    || !string.Equals(candidate.Key.Name, key.Name, StringComparison.Ordinal)
                    || candidate.Key.Source != key.Source
                    || !TsTypeEquivalent(param.Type, candidate.Param.Type)
                    || param.IsOptional != candidate.Param.IsOptional
                )
                {
                    continue;
                }

                consumed[index] = true;
                matched = true;
                break;
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static (string Name, ParamSource Source) ParamKey(TsEndpointParam param) =>
        (param.Name, param.Source);

    /// <summary>
    /// File-axis media-type equivalence. Only an observable declaration
    /// difference is a conflict: when exactly one declaration is null (no
    /// explicit declaration) the two are treated as equivalent when the
    /// non-null value is one of the file/JSON defaults, keeping cross-frontend
    /// pairs that carry their file media type on different representation axes
    /// (one in FileContentType, the other in response contents, or the default
    /// omitted on one side) collapsing, while any explicitly differing file
    /// media type fails the merge.
    /// </summary>
    private static bool FileContentTypeEquivalent(string? left, string? right) =>
        DeclaredMediaTypeEquivalent(left, right);

    /// <summary>
    /// Content-type-override equivalence on the request and response axes.
    /// The allowance for a null-vs-populated representation difference does not
    /// apply here: the emitter resolves a null override to application/json
    /// (OpenApiEmitter BuildOperation requestBody / success-response content),
    /// so two declarations whose resolved media types observably differ —
    /// including one side null and the other a non-null declared override —
    /// produce different artifacts and conflict.
    /// </summary>
    private static bool ContentTypeOverrideEquivalent(string? left, string? right)
    {
        const string jsonDefault = "application/json";
        return string.Equals(
            left ?? jsonDefault,
            right ?? jsonDefault,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static bool DeclaredMediaTypeEquivalent(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (left is null || right is null)
        {
            // One side omitted the declaration. The comparison stays on the observable
            // surface: null is equivalent to the JSON default and to an octet-stream
            // file default only when the other side carries exactly that default.
            const string jsonDefault = "application/json";
            const string fileDefault = "application/octet-stream";
            var populated = left ?? right;
            return string.Equals(populated, jsonDefault, StringComparison.OrdinalIgnoreCase)
                || string.Equals(populated, fileDefault, StringComparison.OrdinalIgnoreCase);
        }

        return false;
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
