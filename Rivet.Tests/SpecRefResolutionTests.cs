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

        var refs = new List<(string Pointer, string Ref)>();
        CollectRefs(doc.RootElement, "#", refs);

        var dangling = refs
            .Where(r => !ResolvesInDocument(doc.RootElement, r.Ref))
            .ToList();

        Assert.True(dangling.Count == 0,
            $"'{fixtureName}' emitted {dangling.Count} dangling $ref(s) — every consumer rejects these:\n"
            + string.Join("\n", dangling.Select(d => $"  at {d.Pointer}: $ref → {d.Ref}")));
    }

    private static void CollectRefs(JsonElement element, string pointer, List<(string, string)> refs)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "$ref" && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        refs.Add((pointer, prop.Value.GetString()!));
                    }
                    else
                    {
                        CollectRefs(prop.Value, $"{pointer}/{prop.Name}", refs);
                    }
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectRefs(item, $"{pointer}/{index}", refs);
                    index++;
                }
                break;
        }
    }

    /// <summary>
    /// Resolves a JSON-pointer-style "#/a/b/c" reference against the document root.
    /// Rivet emits only internal refs — anything that is not "#/…" is dangling by definition.
    /// </summary>
    private static bool ResolvesInDocument(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        var current = root;
        foreach (var rawSegment in reference[2..].Split('/'))
        {
            // RFC 6901 unescaping (~1 before ~0)
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    return false;
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, out var idx)
                    || idx < 0
                    || idx >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[idx];
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
