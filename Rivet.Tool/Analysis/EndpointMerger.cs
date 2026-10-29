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
    public static IReadOnlyList<TsEndpointDefinition> Merge(
        IReadOnlyList<TsEndpointDefinition> contractEndpoints,
        IReadOnlyList<TsEndpointDefinition> annotationEndpoints
    )
    {
        var merged = new List<TsEndpointDefinition>(
            contractEndpoints.Count + annotationEndpoints.Count
        );
        var byTransportIdentity =
            new Dictionary<
                (string Method, string Route),
                (TsEndpointDefinition Endpoint, bool FromContract)
            >();

        // Contract-first ordering is the deterministic representative choice for
        // equivalent declarations (same precedence as the previous (ControllerName,
        // Name) merge): contract endpoints are seeded first, annotation endpoints
        // only fill transport identities no contract endpoint declared.
        foreach (
            var (endpoint, fromContract) in contractEndpoints
                .Select(endpoint => (endpoint, true))
                .Concat(annotationEndpoints.Select(endpoint => (endpoint, false)))
        )
        {
            var key = TransportIdentity.Key(endpoint.HttpMethod, endpoint.RouteTemplate);
            if (byTransportIdentity.TryGetValue(key, out var existing))
            {
                if (EndpointSurfaceEquivalent(existing.Endpoint, endpoint))
                {
                    // Equivalent representations of one operation collapse; the first
                    // (contract-first) representative stays.
                    continue;
                }

                throw Conflict(
                    key.Method,
                    key.Route,
                    existing.Endpoint,
                    endpoint,
                    SecurityRemedy(existing.FromContract, fromContract)
                );
            }

            byTransportIdentity[key] = (endpoint, fromContract);
            merged.Add(endpoint);
        }

        return merged;
    }

    /// <summary>
    /// RIV2012 for two endpoints at one transport identity that aren't
    /// <see cref="EndpointSurfaceEquivalent"/>. When security is all that differs, it says
    /// what each one's security is and how to align them, in the caller's terms
    /// (<paramref name="securityRemedy"/>: what the pair's sources declare security with);
    /// otherwise it names both.
    /// </summary>
    internal static RivetUserException Conflict(
        string method,
        string route,
        TsEndpointDefinition existing,
        TsEndpointDefinition endpoint,
        string securityRemedy
    ) =>
        EndpointSurfaceEquivalent(existing, endpoint with { Security = existing.Security })
            ? Diagnostics.Error(
                Diagnostics.ConflictingOperations,
                $"transport identity {method} {route} is declared by two endpoints that agree on everything but security: "
                    + $"'{existing.ControllerName}.{existing.Name}' {DescribeSecurity(existing.Security)}, and "
                    + $"'{endpoint.ControllerName}.{endpoint.Name}' {DescribeSecurity(endpoint.Security)}. "
                    + $"Declare the same security on both: {securityRemedy}."
            )
            : Diagnostics.Error(
                Diagnostics.ConflictingOperations,
                $"transport identity {method} {route} is declared by two incompatible endpoints: "
                    + $"'{existing.ControllerName}.{existing.Name}' and '{endpoint.ControllerName}.{endpoint.Name}'. "
                    + "Resolve the contradiction at the source — first-wins/last-wins cannot resolve conflicting declarations."
            );

    /// <summary>
    /// How a merged pair aligns its security, by what each side declares it with: a contract
    /// definition with builder calls, an annotated action (or abstract contract method) with
    /// attributes (<see cref="SecurityDeclaration"/>'s two spellings).
    /// </summary>
    private static string SecurityRemedy(bool existingFromContract, bool endpointFromContract) =>
        (existingFromContract, endpointFromContract) switch
        {
            (true, true) =>
                $"give both definitions the same {Steps(SecurityDeclaration.BuilderCalls)} calls",
            (false, false) =>
                $"give both actions the same {Steps(SecurityDeclaration.Attributes)} attributes",
            _ =>
                $"repeat the contract's {Steps(SecurityDeclaration.BuilderCalls)} calls on the action as "
                    + Steps(SecurityDeclaration.Attributes),
        };

    private static string Steps(SecurityDeclaration.Spelling spelling) =>
        $"{spelling.Secure}, {spelling.AndSecure}, {spelling.OrAnonymous}, or {spelling.Anonymous}";

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
            && SecurityEquivalent(left.Security, right.Security)
            && ParamsEquivalent(left.Params, right.Params)
            && DeclaredMediaTypeEquivalent(left.FileContentType, right.FileContentType)
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
    /// Security equivalence: the same alternatives, each needing the same schemes with the
    /// same scopes, in any order (OpenAPI ORs alternatives and ANDs schemes). Inheriting the
    /// document's security (null) is a declaration of its own: a controller action and a
    /// contract that disagree on it would publish whichever came first.
    /// </summary>
    private static bool SecurityEquivalent(
        SecurityRequirements? left,
        SecurityRequirements? right
    ) =>
        left is null || right is null
            ? left is null && right is null
            : CanonicalSecurity(left).SetEquals(CanonicalSecurity(right));

    // Each alternative as JSON, its schemes and their scopes sorted: JSON quotes every name,
    // so no two different alternatives share a key.
    private static HashSet<string> CanonicalSecurity(SecurityRequirements security) =>
        security
            .Alternatives.Select(alternative =>
                JsonSerializer.Serialize(
                    alternative
                        .Schemes.Select(scheme =>
                            (string[])[scheme.Name, .. scheme.Scopes.Order(StringComparer.Ordinal)]
                        )
                        .OrderBy(scheme => JsonSerializer.Serialize(scheme), StringComparer.Ordinal)
                )
            )
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>What an endpoint's security says, for a diagnostic.</summary>
    private static string DescribeSecurity(SecurityRequirements? security) =>
        security switch
        {
            null => "declares none, so it inherits the document's",
            { Alternatives: [] } => "is anonymous",
            _ => "requires "
                + string.Join(
                    " or ",
                    security.Alternatives.Select(alternative =>
                        alternative.Schemes.Count == 0
                            ? "nothing (anonymous)"
                            : string.Join(
                                " and ",
                                alternative.Schemes.Select(scheme =>
                                    scheme.Scopes.Count == 0
                                        ? $"'{scheme.Name}'"
                                        : $"'{scheme.Name}' with scopes "
                                            + string.Join(", ", scheme.Scopes.Select(s => $"'{s}'"))
                                )
                            )
                    )
                ),
        };

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
        var consumed = new bool[right.Count];
        foreach (var param in left)
        {
            var matched = false;
            for (var index = 0; index < right.Count; index++)
            {
                var candidate = right[index];
                if (
                    consumed[index]
                    || (candidate.Name, candidate.Source, candidate.IsOptional)
                        != (param.Name, param.Source, param.IsOptional)
                    || !TsTypeEquivalent(param.Type, candidate.Type)
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

        return ListEquivalentBy(left.Contents, right.Contents, content => content.MediaType)
            && ListEquivalentBy(left.Headers, right.Headers, header => header.Name);
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

        return ListEquivalentBy(
            left.RequestContents,
            right.RequestContents,
            content => content.MediaType
        );
    }

    /// <summary>
    /// Two optional lists are equivalent when both are absent, or both hold the same
    /// serialized entries once ordered by <paramref name="key"/>.
    /// </summary>
    private static bool ListEquivalentBy<T>(
        IReadOnlyList<T>? left,
        IReadOnlyList<T>? right,
        Func<T, string> key
    ) =>
        left is null || right is null
            ? left is null && right is null
            : left.Count == right.Count
                && left.OrderBy(key, StringComparer.Ordinal)
                    .Zip(
                        right.OrderBy(key, StringComparer.Ordinal),
                        (leftItem, rightItem) =>
                            JsonSerializer.Serialize(leftItem)
                            == JsonSerializer.Serialize(rightItem)
                    )
                    .All(equal => equal);

    private static bool TsTypeEquivalent(TsType? left, TsType? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    }
}
