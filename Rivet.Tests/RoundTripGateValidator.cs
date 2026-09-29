using Microsoft.OpenApi;

namespace Rivet.Tests;

/// <summary>
/// Microsoft.OpenApi's reading of an emitted document: parser and rule-set errors, and
/// the 3.1 dialect Rivet emits. Reference, security-scheme, path-parameter, operationId
/// and component-identity integrity belong to <c>tools/roundtrip-diff.py</c>, which the
/// gate runs over the same documents.
/// </summary>
internal static class RoundTripGateValidator
{
    public static IReadOnlyList<string> Validate(string emittedPath)
    {
        var readResult = OpenApiDocument.Parse(File.ReadAllText(emittedPath), "json");
        var findings = readResult
            .Diagnostic.Errors.Select(error => $"OpenAPI validation: {error.Message}")
            .ToList();
        if (readResult.Document is null)
        {
            findings.Add("OpenAPI parser returned no document");
        }

        if (readResult.Diagnostic.SpecificationVersion != OpenApiSpecVersion.OpenApi3_1)
        {
            findings.Add("emitted document does not declare OpenAPI 3.1.x");
        }

        return findings;
    }
}
