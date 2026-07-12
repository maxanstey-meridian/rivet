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
            .GroupBy(response => response.StatusCode)
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
        var seen = new HashSet<int>();
        var normalized = new List<TsResponseType>();

        foreach (var response in responses)
        {
            if (seen.Add(response.StatusCode))
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

        normalized.Sort((a, b) => a.StatusCode.CompareTo(b.StatusCode));
        return normalized;
    }
}
