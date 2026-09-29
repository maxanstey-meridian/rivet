using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// $ref-resolves conformance lint (FABLE_GAPS §3, BUG-1/BUG-2 class).
///
/// Rivet emits only document-internal <c>$ref</c>s. A <c>$ref</c> whose target does
/// not exist in the emitted document is fatal for every real consumer (Prism won't
/// boot, openapi-zod-client/openapi-typescript fail, Spectral crashes), so for EVERY
/// spec the suite already produces — both Roslyn-path fixtures and the TS/PHP
/// <c>--from</c> contract-JSON fixtures — this walks the entire JSON document and
/// asserts each <c>$ref</c> value resolves to an existing location in the document.
///
/// The corpus is OpenApiConformanceTests.EmitSpec; it includes a <c>--from</c>
/// contract using brands (contract-ts-brands-json) and a <c>--from</c> contract with
/// a multipart file-upload endpoint (contract-ts-multipart-json) — the two shapes the
/// TS lowerer produces that previously emitted dangling refs.
/// </summary>
public sealed class SpecRefResolutionTests
{
    [Theory]
    [InlineData("maximal-contract")]
    [InlineData("controller-annotations")]
    [InlineData("typed-results")]
    [InlineData("mixed-contracts-controllers")]
    [InlineData("file-endpoints-query-auth")]
    [InlineData("validation-metadata")]
    [InlineData("contractapi-sample")]
    [InlineData("contract-sample-json")]
    [InlineData("contract-tagged-union-json")]
    [InlineData("php-golden-contract-json")]
    [InlineData("contract-ts-brands-json")]
    [InlineData("contract-ts-multipart-json")]
    public void Every_Ref_Resolves_Within_The_Document(string fixtureName)
    {
        var json = OpenApiConformanceTests.EmitSpec(fixtureName);
        using var doc = JsonDocument.Parse(json);

        var dangling = JsonRefs
            .Collect(doc.RootElement)
            .Where(r => !JsonRefs.Resolves(doc.RootElement, r.Reference))
            .ToList();

        Assert.True(
            dangling.Count == 0,
            $"'{fixtureName}' emitted {dangling.Count} dangling $ref(s) — every consumer rejects these:\n"
                + string.Join("\n", dangling.Select(d => $"  at {d.Pointer}: $ref → {d.Reference}"))
        );
    }
}
