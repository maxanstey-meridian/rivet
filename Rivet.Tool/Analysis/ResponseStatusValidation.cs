using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Enforces authored-contract response invariants and normalizes external IR.
/// </summary>
internal static class ResponseStatusValidation
{
    internal static void RejectContractDuplicates(
        IEnumerable<TsResponseType> responses,
        string endpointName
    )
    {
        var duplicate = responses
            .GroupBy(response => response.EffectiveStatusKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is null)
        {
            return;
        }

        throw new ContractAnalysisException(
            $"error {Diagnostics.DuplicateResponseStatus}: endpoint '{endpointName}' declares response status "
                + $"{duplicate.Key} more than once; authored contracts must declare exactly one response shape per status"
        );
    }

    internal static List<TsResponseType> NormalizeIrKeepingFirst(
        IEnumerable<TsResponseType> responses,
        string endpointName
    )
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<TsResponseType>();

        foreach (var response in responses)
        {
            if (seen.Add(response.EffectiveStatusKey))
            {
                normalized.Add(response);
                continue;
            }

            Diagnostics.Warn(
                Diagnostics.DuplicateResponseStatusInIr,
                $"endpoint '{endpointName}' declares response status {response.StatusCode} more than once — "
                    + "a status carries a single response shape. If it genuinely returns different shapes, declare a "
                    + "[RivetUnion] type and return that once; otherwise remove the duplicate (often one .Returns is mistyped). "
                    + "Keeping the first declaration."
            );
        }

        normalized.Sort(
            (a, b) =>
                StringComparer.OrdinalIgnoreCase.Compare(a.EffectiveStatusKey, b.EffectiveStatusKey)
        );
        return normalized;
    }

    internal static List<TsResponseType> NormalizeIrAndEnsureResponse(
        IEnumerable<TsResponseType> responses,
        TsEndpointDefinition endpoint
    ) =>
        NormalizeIrAndEnsureResponse(
            responses,
            endpoint.Name,
            endpoint.HttpMethod,
            endpoint.ReturnType
        );

    internal static List<TsResponseType> NormalizeIrAndEnsureResponse(
        IEnumerable<TsResponseType> responses,
        string endpointName,
        string httpMethod,
        TsType? returnType
    )
    {
        var normalized = NormalizeIrKeepingFirst(responses, endpointName);
        // RIV1102 must run on every parse regardless of list state: authored
        // content always implies a non-empty list, so a post-early-return
        // placement would leave the guard inert for exactly its target cases.
        RejectBodyForbiddenContent(normalized, endpointName);
        if (normalized.Count > 0)
        {
            return normalized;
        }

        var statusCode =
            httpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) ? 201
            : httpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase) && returnType is null
                ? 204
            : 200;
        normalized.Add(new TsResponseType(statusCode, returnType));
        return normalized;
    }

    /// <summary>
    /// HTTP forbids a message body on 1xx, 204, 205 and 304 — the same statuses
    /// <c>EndpointRuntime.AllowsBody</c> rejects at runtime. Authored examples or
    /// contents on such statuses could never reach the wire, so they fail
    /// generation instead of being emitted.
    /// </summary>
    internal static bool IsBodyForbiddenStatus(int statusCode) =>
        statusCode is >= 100 and (< 200 or 204 or 205 or 304);

    internal static bool IsBodyForbiddenStatusKey(string statusKey)
    {
        if (statusKey.Length == 3 && int.TryParse(statusKey, out var statusCode))
        {
            return IsBodyForbiddenStatus(statusCode);
        }

        // "1XX"-style informational wildcard: every status it covers forbids a body.
        return statusKey.Length == 3
            && statusKey[0] == '1'
            && statusKey[1] is 'X' or 'x'
            && statusKey[2] is 'X' or 'x';
    }

    /// <summary>
    /// Frontend-parsing guard for RIV1102: authored examples/contents on
    /// body-forbidden statuses (1xx/204/205/304) are an actionable generation
    /// failure, not silent content. Called from every frontend parse site; the
    /// emitter's BuildResponses path re-checks as defense in depth.
    /// </summary>
    internal static void RejectBodyForbiddenContent(
        IReadOnlyList<TsResponseType> responses,
        string endpointName
    )
    {
        foreach (var response in responses)
        {
            var hasAuthoredContent =
                response.Examples is { Count: > 0 } || response.Contents is { Count: > 0 };
            if (!hasAuthoredContent)
            {
                continue;
            }

            if (IsBodyForbiddenStatusKey(response.EffectiveStatusKey))
            {
                throw new ContractAnalysisException(
                    $"error {Diagnostics.BodyForbiddenStatusExample}: endpoint '{endpointName}' "
                        + $"authors response content on body-forbidden status {response.EffectiveStatusKey} — "
                        + "HTTP forbids a message body on 1xx/204/205/304, so the authored example/content "
                        + "could never reach the wire; move it to a status that allows a body or remove it"
                );
            }
        }
    }
}
