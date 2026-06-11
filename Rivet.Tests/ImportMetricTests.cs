using Rivet.Tool.Import;

namespace Rivet.Tests;

/// <summary>
/// Metric assertions against real-world OpenAPI specs.
/// These catch regressions in import coverage without snapshot maintenance.
/// Requires local spec files in /openapi (gitignored) — skipped in CI via trait filter.
/// </summary>
[Trait("Category", "Local")]
public sealed class ImportMetricTests
{
    private static string SpecPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "openapi", $"{name}.json");

    private static ImportResult Import(string name)
    {
        var json = File.ReadAllText(SpecPath(name));
        return OpenApiImporter.Import(json, new ImportOptions("Test"));
    }

    private static int CountPattern(ImportResult result, string dir, string pattern)
    {
        return result.Files
            .Where(f => f.FileName.StartsWith(dir))
            .Sum(f => CountOccurrences(f.Content, pattern));
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }

        return count;
    }

    // ===== Warning ratchet =====
    // Warnings are allowed but must belong to a known category. New warning categories
    // (e.g. when a silent skip is converted into a diagnostic) must be consciously added
    // here and to the per-corpus allowed sets — never silently absorbed.
    private static string CategorizeWarning(string warning) => warning switch
    {
        _ when warning.StartsWith("Schema could not be resolved", StringComparison.Ordinal) => "unresolved-schema",
        _ when warning.StartsWith("Unsupported JSON Schema type", StringComparison.Ordinal) => "unsupported-schema-type",
        _ when warning.StartsWith("Array schema missing 'items'", StringComparison.Ordinal) => "array-missing-items",
        // Added with I.A-15: enum constraints that can't become a C# enum (single-value,
        // mixed/float, out-of-int32-range) degrade to a primitive WITH a named warning.
        _ when warning.StartsWith("Enum constraint dropped", StringComparison.Ordinal) => "enum-constraint-dropped",
        // Added with I.A-17: discriminator on a plain object record — dispatch semantics dropped.
        _ when warning.StartsWith("Discriminator dropped", StringComparison.Ordinal) => "discriminator-dropped",
        _ => $"UNCATEGORIZED: {warning}",
    };

    /// <summary>
    /// Ratchet: every warning must fall into one of the explicitly allowed categories.
    /// Unexpected categories fail loudly with the offending messages.
    /// </summary>
    private static void AssertWarningCategoriesSubsetOf(ImportResult r, params string[] allowedCategories)
    {
        var allowed = allowedCategories.ToHashSet(StringComparer.Ordinal);
        var unexpected = r.Warnings
            .Select(CategorizeWarning)
            .Where(category => !allowed.Contains(category))
            .Distinct()
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Unexpected warning categories (add deliberately or fix the importer):\n  {string.Join("\n  ", unexpected)}");
    }

    private static int TypeFiles(ImportResult r) => r.Files.Count(f => f.FileName.StartsWith("Types/"));
    private static int ContractFiles(ImportResult r) => r.Files.Count(f => f.FileName.StartsWith("Contracts/"));
    private static int TypedInputs(ImportResult r) => CountPattern(r, "Contracts/", "RouteDefinition<") - CountPattern(r, "Contracts/", "RouteDefinition<>") + CountPattern(r, "Contracts/", "InputRouteDefinition<");
    private static int UnsupportedBody(ImportResult r) => CountPattern(r, "Contracts/", "[rivet:unsupported body");
    private static int UnsupportedResponse(ImportResult r) => CountPattern(r, "Contracts/", "[rivet:unsupported response");
    private static int UnsupportedError(ImportResult r) => CountPattern(r, "Contracts/", "[rivet:unsupported error");

    // Typed inputs = RouteDefinition<A, B> (two type args) + InputRouteDefinition<A>
    // We need to count two-arg RouteDefinition separately from one-arg
    private static int TypedInputCount(ImportResult r)
    {
        var count = 0;
        foreach (var f in r.Files.Where(f => f.FileName.StartsWith("Contracts/")))
        {
            // Count lines with RouteDefinition<X, Y> (comma = two type args = has input)
            count += f.Content.Split('\n')
                .Count(line => line.Contains("RouteDefinition<") && line.Contains(",") && line.Contains("> "));
            // Count InputRouteDefinition<X>
            count += f.Content.Split('\n')
                .Count(line => line.Contains("InputRouteDefinition<"));
        }

        return count;
    }

    // ========== Stripe — largest spec, form-encoded heavy ==========

    [Fact]
    public void Stripe_Metrics()
    {
        var r = Import("stripe");

        Assert.True(TypeFiles(r) >= 3060, $"Expected ≥3060 types, got {TypeFiles(r)}");
        Assert.Equal(1, ContractFiles(r)); // single-tag API
        Assert.True(TypedInputCount(r) >= 580, $"Expected ≥580 typed inputs, got {TypedInputCount(r)}");
        Assert.Equal(0, UnsupportedBody(r));
        // Ratchet: warnings allowed but must be categorized — unexpected categories fail.
        // "enum-constraint-dropped" added deliberately with I.A-15: Stripe's `object:
        // {"enum": ["account"]}` discriminator constants are single-value enums that degrade
        // to string — previously silent, now each emits a named warning.
        AssertWarningCategoriesSubsetOf(r, "unresolved-schema", "unsupported-schema-type", "array-missing-items", "enum-constraint-dropped");
    }

    // ========== GitHub — large, well-structured, many tags ==========

    [Fact]
    public void GitHub_Metrics()
    {
        var r = Import("github");

        Assert.True(TypeFiles(r) >= 1800, $"Expected ≥1800 types, got {TypeFiles(r)}");
        Assert.True(ContractFiles(r) >= 40, $"Expected ≥40 contracts, got {ContractFiles(r)}");
        Assert.True(TypedInputCount(r) >= 300, $"Expected ≥300 typed inputs, got {TypedInputCount(r)}");
        Assert.True(UnsupportedBody(r) <= 5, $"Expected ≤5 unsupported bodies, got {UnsupportedBody(r)}");
        // Ratchet: warnings allowed but must be categorized — unexpected categories fail.
        // "enum-constraint-dropped" added deliberately with I.A-15: GitHub's single-value
        // permission enums ({"enum": ["read"]} etc.) degrade to string — previously silent,
        // now each emits a named warning.
        AssertWarningCategoriesSubsetOf(r, "unresolved-schema", "unsupported-schema-type", "array-missing-items", "enum-constraint-dropped");
    }

    // ========== Kubernetes — */* content type, PATCH-heavy ==========

    [Fact]
    public void Kubernetes_Metrics()
    {
        var r = Import("kubernetes");

        Assert.True(TypeFiles(r) >= 240, $"Expected ≥240 types, got {TypeFiles(r)}");
        Assert.True(TypedInputCount(r) >= 70, $"Expected ≥70 typed inputs, got {TypedInputCount(r)}");
        // Ratchet (counts may only go DOWN): CBOR/YAML/json-patch operations.
        // 26 at last audit — if a new content type becomes supported this shrinks; it must not grow.
        Assert.True(UnsupportedBody(r) <= 26, $"Expected ≤26 unsupported bodies (ratchet, was 26), got {UnsupportedBody(r)}");
        Assert.Equal(0, UnsupportedError(r));
        // Ratchet: warnings allowed but must be categorized — unexpected categories fail.
        AssertWarningCategoriesSubsetOf(r, "unresolved-schema", "unsupported-schema-type", "array-missing-items");
    }

    // ========== Cloudflare — largest contract count, hyphenated schema names ==========

    [Fact]
    public void Cloudflare_Metrics()
    {
        var r = Import("cloudflare");

        Assert.True(TypeFiles(r) >= 7000, $"Expected ≥7000 types, got {TypeFiles(r)}");
        Assert.True(ContractFiles(r) >= 400, $"Expected ≥400 contracts, got {ContractFiles(r)}");
        Assert.True(TypedInputCount(r) >= 1000, $"Expected ≥1000 typed inputs, got {TypedInputCount(r)}");

        // No invalid identifiers (hyphens) in type files
        var hasHyphens = r.Files
            .Where(f => f.FileName.StartsWith("Types/"))
            .Any(f => f.FileName.Contains('-'));
        Assert.False(hasHyphens, "Type filenames should not contain hyphens");
    }

    // ========== DocuSign — */* responses, $ref requestBodies ==========

    [Fact]
    public void DocuSign_Metrics()
    {
        var r = Import("docusign");

        Assert.True(TypeFiles(r) >= 500, $"Expected ≥500 types, got {TypeFiles(r)}");
        Assert.True(ContractFiles(r) >= 80, $"Expected ≥80 contracts, got {ContractFiles(r)}");
        Assert.True(TypedInputCount(r) >= 170, $"Expected ≥170 typed inputs, got {TypedInputCount(r)}");

        // DocuSign's */* responses should be typed now
        var typedOutputs = CountPattern(r, "Contracts/", "RouteDefinition<");
        Assert.True(typedOutputs >= 330, $"Expected ≥330 typed outputs, got {typedOutputs}");

        Assert.Equal(0, UnsupportedBody(r));
        // Image responses are now file endpoints, not unsupported
        Assert.True(UnsupportedResponse(r) <= 1, $"Expected ≤1 unsupported response, got {UnsupportedResponse(r)}");
        // Ratchet (counts may only go DOWN): 12 at last audit — must not grow.
        Assert.True(UnsupportedError(r) <= 12, $"Expected ≤12 unsupported errors (ratchet, was 12), got {UnsupportedError(r)}");
        // Image endpoints should generate Define.File()
        var fileEndpoints = CountPattern(r, "Contracts/", "Define.File");
        Assert.True(fileEndpoints >= 11, $"Expected ≥11 file endpoints, got {fileEndpoints}");
    }

    // ========== Jira — schemaless error responses ==========

    [Fact]
    public void Jira_Metrics()
    {
        var r = Import("jira");

        Assert.True(TypeFiles(r) >= 500, $"Expected ≥500 types, got {TypeFiles(r)}");
        Assert.True(ContractFiles(r) >= 80, $"Expected ≥80 contracts, got {ContractFiles(r)}");

        // Jira has 142 schemaless error responses (content but no schema) —
        // these have no content type to mark as unsupported, they're just empty.
        // Ratchet (counts may only go DOWN): 142 at last audit — must not grow.
        Assert.True(UnsupportedError(r) <= 142, $"Expected ≤142 unsupported errors (ratchet, was 142), got {UnsupportedError(r)}");
        Assert.Equal(0, UnsupportedBody(r));
    }

    // ========== Docker — mix of $ref responses and non-JSON ==========

    [Fact]
    public void Docker_Metrics()
    {
        var r = Import("docker");

        Assert.True(TypeFiles(r) >= 170, $"Expected ≥170 types, got {TypeFiles(r)}");
        Assert.True(TypedInputCount(r) >= 22, $"Expected ≥22 typed inputs, got {TypedInputCount(r)}");
        Assert.Equal(0, UnsupportedBody(r));
        // Ratchet (counts may only go DOWN): 17 at last audit — must not grow.
        Assert.True(UnsupportedError(r) <= 17, $"Expected ≤17 unsupported errors (ratchet, was 17), got {UnsupportedError(r)}");
    }

    // ========== Slack — warnings from genuinely untyped schemas ==========

    [Fact]
    public void Slack_Metrics()
    {
        var r = Import("slack");

        Assert.True(TypeFiles(r) >= 220, $"Expected ≥220 types, got {TypeFiles(r)}");
        Assert.True(ContractFiles(r) >= 50, $"Expected ≥50 contracts, got {ContractFiles(r)}");

        // Slack has genuinely untyped schemas — warnings are expected
        Assert.True(r.Warnings.Count <= 25, $"Expected ≤25 warnings, got {r.Warnings.Count}");
        Assert.True(r.Warnings.Count > 0, "Slack should have some warnings for untyped schemas");
    }
}
