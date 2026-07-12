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
}
