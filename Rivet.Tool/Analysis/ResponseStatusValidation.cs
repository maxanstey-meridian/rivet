using System.Text.RegularExpressions;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// Enforces authored-contract response invariants and normalizes external IR
/// (controller metadata and <c>--from</c> contract JSON), refusing a repeated status in both.
/// </summary>
internal static partial class ResponseStatusValidation
{
    /// <summary>
    /// A response key: <c>default</c> (lowercase, as OpenAPI names the field), a range
    /// (<c>4XX</c> or <c>4xx</c>; imported keys keep the source's spelling) or a three-digit
    /// code. rivet-contract-schema.json's statusKey pattern is this text.
    /// </summary>
    internal const string StatusKeyPattern = "^(default|[1-5](?:[0-9]{2}|[Xx]{2}))$";

    internal static bool IsStatusKey(string key) => StatusKeyRegex().IsMatch(key);

    [GeneratedRegex(StatusKeyPattern)]
    private static partial Regex StatusKeyRegex();

    /// <summary>
    /// A status key authored in C# (<c>.StatusKey(...)</c>, or the statusKey form of
    /// <c>.Returns(...)</c>, <c>.ResponseContent(...)</c> and the rest), as the spec writes it.
    /// The runtime matches <c>default</c> ignoring case, by design, but OpenAPI only knows the
    /// lowercase field, so any casing of it is emitted as <c>default</c>. Anything else
    /// <see cref="IsStatusKey"/> refuses would reach the spec as written, so it stops
    /// generation (RIV1120). A range keeps its spelling, as an imported one does.
    /// </summary>
    internal static string AuthoredStatusKey(string key, string where)
    {
        if (key.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        return IsStatusKey(key) ? key : throw InvalidStatusKey($"'{key}'", where, "a status key");
    }

    /// <summary>
    /// A status code authored as an int (<c>.Status(...)</c>, <c>.Returns(...)</c>,
    /// <c>[ProducesResponseType]</c>): it must be one OpenAPI can key a response by, 100 to 599.
    /// </summary>
    internal static int AuthoredStatusCode(int code, string where) =>
        IsStatusKey(code.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ? code
            : throw InvalidStatusKey(code.ToString(), where, "status code");

    private static RivetUserException InvalidStatusKey(string key, string where, string what) =>
        Diagnostics.Error(
            Diagnostics.InvalidStatusKey,
            $"{where} declares {what} {key}, which OpenAPI can't key a response by, so the spec "
                + "would carry a response no client can match. Use a status code from 100 to 599 "
                + "(\"404\"), a range (\"4XX\") or \"default\""
        );

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

        throw Diagnostics.Error(
            Diagnostics.DuplicateResponseStatus,
            $"endpoint '{endpointName}' declares response status "
                + $"{duplicate.Key} more than once; authored contracts must declare exactly one response shape per status"
        );
    }

    internal static List<TsResponseType> NormalizeIr(
        IEnumerable<TsResponseType> responses,
        string endpointName
    )
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<TsResponseType>();

        foreach (var response in responses)
        {
            if (!seen.Add(response.EffectiveStatusKey))
            {
                // Keeping either declaration would silently drop the other's shape and metadata.
                throw Diagnostics.Error(
                    Diagnostics.DuplicateResponseStatusInIr,
                    $"endpoint '{endpointName}' declares response status "
                        + $"{response.EffectiveStatusKey} more than once; a status carries a single response shape. "
                        + "Remove the duplicate (often one declaration has the wrong status), or, if the status genuinely "
                        + "returns different shapes, declare one union type ([RivetUnion] in C#) and declare it once"
                );
            }

            normalized.Add(response);
        }

        normalized.Sort(
            (a, b) =>
                StringComparer.OrdinalIgnoreCase.Compare(a.EffectiveStatusKey, b.EffectiveStatusKey)
        );
        return normalized;
    }

    internal static List<TsResponseType> NormalizeIrAndEnsureResponse(
        IEnumerable<TsResponseType> responses,
        string endpointName,
        string httpMethod,
        TsType? returnType
    )
    {
        var normalized = NormalizeIr(responses, endpointName);
        // RIV1102 must run on every parse regardless of list state: authored
        // content always implies a non-empty list, so a post-early-return
        // placement would leave the guard inert for exactly its target cases.
        RejectBodyForbiddenContent(normalized, endpointName);
        if (normalized.Count > 0)
        {
            return normalized;
        }

        normalized.Add(
            new TsResponseType(DefaultSuccessCode(httpMethod, returnType is not null), returnType)
        );
        return normalized;
    }

    /// <summary>
    /// Default success status for an endpoint with no explicit .Status(...) call.
    /// Must agree with the runtime defaults in Rivet.Define (Endpoint.cs):
    /// POST → 201; DELETE without an output type → 204; DELETE with an output type → 200
    /// (204-with-body is invalid HTTP); everything else → 200.
    /// </summary>
    internal static int DefaultSuccessCode(string httpMethod, bool hasOutput) =>
        httpMethod.ToUpperInvariant() switch
        {
            "POST" => 201,
            "DELETE" when !hasOutput => 204,
            _ => 200,
        };

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
                throw Diagnostics.Error(
                    Diagnostics.BodyForbiddenStatusExample,
                    $"endpoint '{endpointName}' "
                        + $"authors response content on body-forbidden status {response.EffectiveStatusKey} — "
                        + "HTTP forbids a message body on 1xx/204/205/304, so the authored example/content "
                        + "could never reach the wire; move it to a status that allows a body or remove it"
                );
            }
        }
    }
}
