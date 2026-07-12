using Rivet.Tool;
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
        return result
            .Files.Where(f => f.FileName.StartsWith(dir))
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
    // Warnings are allowed but must belong to a known category, keyed by their stable
    // RIV3xxx diagnostic ID (every import warning carries an "RIV3001: " prefix via
    // Diagnostics.Prefix). New warning categories (e.g. when a silent skip is converted
    // into a diagnostic) must be consciously added here and to the per-corpus allowed
    // sets — never silently absorbed. ID-less or unknown-ID warnings are UNCATEGORIZED.
    private static string CategorizeWarning(string warning) =>
        DiagnosticId(warning) switch
        {
            Diagnostics.ImportUnresolvedSchema => "unresolved-schema",
            Diagnostics.ImportUnsupportedSchemaType => "unsupported-schema-type",
            Diagnostics.ImportArrayMissingItems => "array-missing-items",
            // Added with I.A-15: enum constraints that can't become a C# enum (single-value,
            // mixed/float, out-of-int32-range) degrade to a primitive WITH a named warning.
            Diagnostics.ImportEnumConstraintDropped => "enum-constraint-dropped",
            // Added with I.A-17: discriminator on a plain object record — dispatch semantics dropped.
            Diagnostics.ImportDiscriminatorDropped => "discriminator-dropped",
            // Added with WP-1.2 I1: component $ref aliases that cannot resolve (cycle or
            // missing target) — consumers fall back to an untyped object, loudly.
            Diagnostics.ImportAliasCycleBroken => "alias-unresolvable",
            Diagnostics.ImportAliasTargetMissing => "alias-unresolvable",
            Diagnostics.ImportAliasRefCycle => "alias-unresolvable",
            Diagnostics.ImportUnresolvableAliasReference => "alias-unresolvable",
            // Added with Phase 4 / I5: a schema declaring BOTH `properties` and
            // `additionalProperties` keeps one side and drops the other — previously silent
            // in both directions, now each drop emits a named warning.
            Diagnostics.ImportDeclaredPropertiesDropped => "properties-dropped",
            Diagnostics.ImportAdditionalPropertiesDropped => "additional-properties-dropped",
            // Added with Phase 4 / I12: multi-scheme document security collapses to the first
            // scheme — previously silent, now a named warning.
            Diagnostics.ImportSecuritySchemesDropped => "security-schemes-dropped",
            // TRACE has no contract representation; each dropped operation emits RIV3003.
            Diagnostics.ImportOperationMethodDropped => "operation-method-dropped",
            // Added with P2 wave 3 (dictionary key types): a propertyNames key schema the
            // importer cannot represent as a C# dictionary key degrades to string keys
            // WITH a named warning.
            Diagnostics.ImportDictionaryKeyDropped => "dictionary-key-dropped",
            _ => $"UNCATEGORIZED: {warning}",
        };

    /// <summary>Extracts the leading "RIVnnnn" ID from a prefixed warning, or "" if absent.</summary>
    private static string DiagnosticId(string warning)
    {
        var colon = warning.IndexOf(':');
        return colon > 0 ? warning[..colon] : "";
    }

    /// <summary>
    /// Ratchet: every warning must fall into one of the explicitly allowed categories.
    /// Unexpected categories fail loudly with the offending messages.
    /// </summary>
    private static void AssertWarningCategoriesSubsetOf(
        ImportResult r,
        params string[] allowedCategories
    )
    {
        var allowed = allowedCategories.ToHashSet(StringComparer.Ordinal);
        var unexpected = r
            .Warnings.Select(CategorizeWarning)
            .Where(category => !allowed.Contains(category))
            .Distinct()
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Unexpected warning categories (add deliberately or fix the importer):\n  {string.Join("\n  ", unexpected)}"
        );
    }

    private static int TypeFiles(ImportResult r) =>
        r.Files.Count(f => f.FileName.StartsWith("Types/"));

    private static int ContractFiles(ImportResult r) =>
        r.Files.Count(f => f.FileName.StartsWith("Contracts/"));

    // Trailing space matters: "body " markers mean the body was DROPPED;
    // "body-location"/"body-optionality" markers (FABLE_ROUNDTRIP #5/#7)
    // describe bodies that imported WITH the body intact but a caveat about
    // how it re-emits — a different severity, ratcheted separately.
    private static int UnsupportedBody(ImportResult r) =>
        CountPattern(r, "Contracts/", "[rivet:unsupported body ");

    private static int BodyCaveats(ImportResult r) =>
        CountPattern(r, "Contracts/", "[rivet:unsupported body-");

    private static int UnsupportedResponse(ImportResult r) =>
        CountPattern(r, "Contracts/", "[rivet:unsupported response");

    private static int UnsupportedError(ImportResult r) =>
        CountPattern(r, "Contracts/", "[rivet:unsupported error");

    // Typed inputs = RouteDefinition<A, B> (two type args) + InputRouteDefinition<A>
    // We need to count two-arg RouteDefinition separately from one-arg
    private static int TypedInputCount(ImportResult r)
    {
        var count = 0;
        foreach (var f in r.Files.Where(f => f.FileName.StartsWith("Contracts/")))
        {
            // Count lines with RouteDefinition<X, Y> (comma = two type args = has input)
            count += f
                .Content.Split('\n')
                .Count(line =>
                    line.Contains("RouteDefinition<") && line.Contains(",") && line.Contains("> ")
                );
            // Count InputRouteDefinition<X>
            count += f.Content.Split('\n').Count(line => line.Contains("InputRouteDefinition<"));
        }

        return count;
    }

    // ===== Conformance check #4: scaffolded C# compiles =====
    // Every corpus entry exercised by this class must also prove the import's primary
    // promise — the scaffolded C# compiles. (Importer stability metrics alone would let
    // a compile-breaking regression through.) Uses RealWorldImportTests' compile helper
    // (identical-content dedupe + ASP.NET stubs).

    [Theory]
    [InlineData("stripe")]
    [InlineData("github")]
    [InlineData("kubernetes")]
    [InlineData("cloudflare")]
    [InlineData("docusign")]
    [InlineData("jira")]
    [InlineData("docker")]
    [InlineData("slack")]
    public void Scaffolded_CSharp_Compiles(string name)
    {
        var r = Import(name);
        var errors = RealWorldImportTests.GetCompilationErrors(r);
        Assert.True(
            errors.Count == 0,
            $"{name}: scaffolded C# has {errors.Count} compile error(s):\n"
                + string.Join("\n", errors.Take(10).Select(e => e.ToString()))
        );
    }

    // ========== Stripe — largest spec, form-encoded heavy ==========

    [Fact]
    public void Stripe_Metrics()
    {
        var r = Import("stripe");

        Assert.True(TypeFiles(r) >= 3060, $"Expected ≥3060 types, got {TypeFiles(r)}");
        Assert.Equal(1, ContractFiles(r)); // single-tag API
        Assert.True(
            TypedInputCount(r) >= 580,
            $"Expected ≥580 typed inputs, got {TypedInputCount(r)}"
        );
        // Ratchet: FABLE_ROUNDTRIP cross-corpus #1 — 287 GET/DELETE ops declare
        // form-encoded additionalProperties bodies (Stripe's expand-map idiom) that
        // imported as a Dictionary TInput, which leaked CLR members as invented query
        // params on emit. The op's real params now win; each unexpressable body is
        // dropped with an opaque-body marker. May only go down.
        Assert.True(
            UnsupportedBody(r) <= 287,
            $"Expected ≤287 unsupported bodies (ratchet), got {UnsupportedBody(r)}"
        );
        // Ratchet: DELETE-body + optional-body-merged caveats (FABLE_ROUNDTRIP #5/#7),
        // 32 + 168 at introduction — may only go down.
        Assert.True(
            BodyCaveats(r) <= 200,
            $"Expected ≤200 body caveats (ratchet), got {BodyCaveats(r)}"
        );
        // Ratchet: warnings allowed but must be categorized — unexpected categories fail.
        // "enum-constraint-dropped" added deliberately with I.A-15: Stripe's `object:
        // {"enum": ["account"]}` discriminator constants are single-value enums that degrade
        // to string — previously silent, now each emits a named warning.
        // "security-schemes-dropped" added deliberately with Phase 4 / I12: Stripe declares
        // two global schemes (basic + bearer); only the first is imported — previously
        // silent, now a named warning.
        AssertWarningCategoriesSubsetOf(
            r,
            "unresolved-schema",
            "unsupported-schema-type",
            "array-missing-items",
            "enum-constraint-dropped",
            "security-schemes-dropped"
        );
    }

    // ========== GitHub — large, well-structured, many tags ==========

    [Fact]
    public void GitHub_Metrics()
    {
        var r = Import("github");

        Assert.True(TypeFiles(r) >= 1800, $"Expected ≥1800 types, got {TypeFiles(r)}");
        Assert.True(ContractFiles(r) >= 40, $"Expected ≥40 contracts, got {ContractFiles(r)}");
        Assert.True(
            TypedInputCount(r) >= 300,
            $"Expected ≥300 typed inputs, got {TypedInputCount(r)}"
        );
        // Down from 5: text/* bodies now import via .AcceptsContentType (FABLE_ROUNDTRIP #10).
        Assert.True(
            UnsupportedBody(r) <= 1,
            $"Expected ≤1 unsupported bodies (ratchet, was 5), got {UnsupportedBody(r)}"
        );
        // Ratchet: DELETE-body + optional-body-merged caveats (FABLE_ROUNDTRIP #5/#7),
        // 16 + 44 at introduction — may only go down.
        Assert.True(
            BodyCaveats(r) <= 60,
            $"Expected ≤60 body caveats (ratchet), got {BodyCaveats(r)}"
        );
        // Ratchet: warnings allowed but must be categorized — unexpected categories fail.
        // "enum-constraint-dropped" added deliberately with I.A-15: GitHub's single-value
        // permission enums ({"enum": ["read"]} etc.) degrade to string — previously silent,
        // now each emits a named warning.
        // "properties-dropped" added deliberately with Phase 4 / I5: GitHub declares inline
        // objects with both `properties` and a typed `additionalProperties`; the dictionary
        // side wins and the declared properties are dropped — previously silent, now named.
        AssertWarningCategoriesSubsetOf(
            r,
            "unresolved-schema",
            "unsupported-schema-type",
            "array-missing-items",
            "enum-constraint-dropped",
            "properties-dropped"
        );
    }

    // ========== Kubernetes — */* content type, PATCH-heavy ==========

    [Fact]
    public void Kubernetes_Metrics()
    {
        var r = Import("kubernetes");

        Assert.True(TypeFiles(r) >= 240, $"Expected ≥240 types, got {TypeFiles(r)}");
        Assert.True(
            TypedInputCount(r) >= 70,
            $"Expected ≥70 typed inputs, got {TypedInputCount(r)}"
        );
        // Ratchet (counts may only go DOWN): CBOR/YAML/json-patch operations.
        // 26 at last audit — if a new content type becomes supported this shrinks; it must not grow.
        Assert.True(
            UnsupportedBody(r) <= 26,
            $"Expected ≤26 unsupported bodies (ratchet, was 26), got {UnsupportedBody(r)}"
        );
        // Ratchet: every kubernetes DELETE carries an options body (FABLE_ROUNDTRIP #5),
        // 29 at introduction — may only go down.
        Assert.True(
            BodyCaveats(r) <= 29,
            $"Expected ≤29 body caveats (ratchet), got {BodyCaveats(r)}"
        );
        Assert.Equal(0, UnsupportedError(r));
        // Kubernetes declares six HEAD and six OPTIONS operations. They must survive import.
        Assert.Equal(6, CountPattern(r, "Contracts/", "Define.Head"));
        Assert.Equal(6, CountPattern(r, "Contracts/", "Define.Options"));

        // Ratchet: warnings allowed but must be categorized — unexpected categories fail.
        AssertWarningCategoriesSubsetOf(
            r,
            "unresolved-schema",
            "unsupported-schema-type",
            "array-missing-items"
        );
        Assert.DoesNotContain(
            r.Warnings,
            warning => DiagnosticId(warning) == Diagnostics.ImportOperationMethodDropped
        );
    }

    // ========== Cloudflare — largest contract count, hyphenated schema names ==========

    [Fact]
    public void Cloudflare_Metrics()
    {
        var r = Import("cloudflare");

        Assert.True(TypeFiles(r) >= 7000, $"Expected ≥7000 types, got {TypeFiles(r)}");
        Assert.True(ContractFiles(r) >= 400, $"Expected ≥400 contracts, got {ContractFiles(r)}");
        Assert.True(
            TypedInputCount(r) >= 1000,
            $"Expected ≥1000 typed inputs, got {TypedInputCount(r)}"
        );

        // No invalid identifiers (hyphens) in type files
        var hasHyphens = r
            .Files.Where(f => f.FileName.StartsWith("Types/"))
            .Any(f => f.FileName.Contains('-'));
        Assert.False(hasHyphens, "Type filenames should not contain hyphens");

        // P2 wave 4: oneOf + discriminator + usable mapping now reverses to a
        // [JsonPolymorphic] hierarchy (4 bases at last audit — must not shrink),
        // and unusable mappings drop loudly with a reason (RIV3005) instead of
        // silently. Ratchet (count may only go DOWN): 15 drops at last audit.
        var polymorphicBases = CountPattern(r, "Types/", "[JsonPolymorphic(");
        Assert.True(
            polymorphicBases >= 4,
            $"Expected ≥4 polymorphic bases, got {polymorphicBases}"
        );
        var discriminatorDrops = r.Warnings.Count(w =>
            DiagnosticId(w) == Diagnostics.ImportDiscriminatorDropped
        );
        Assert.True(
            discriminatorDrops <= 15,
            $"Expected ≤15 discriminator-dropped warnings (ratchet, was 15), got {discriminatorDrops}"
        );
    }

    // ========== DocuSign — */* responses, $ref requestBodies ==========

    [Fact]
    public void DocuSign_Metrics()
    {
        var r = Import("docusign");

        Assert.True(TypeFiles(r) >= 500, $"Expected ≥500 types, got {TypeFiles(r)}");
        Assert.True(ContractFiles(r) >= 80, $"Expected ≥80 contracts, got {ContractFiles(r)}");
        Assert.True(
            TypedInputCount(r) >= 170,
            $"Expected ≥170 typed inputs, got {TypedInputCount(r)}"
        );

        // DocuSign's */* responses should be typed now
        var typedOutputs = CountPattern(r, "Contracts/", "RouteDefinition<");
        Assert.True(typedOutputs >= 330, $"Expected ≥330 typed outputs, got {typedOutputs}");

        Assert.Equal(0, UnsupportedBody(r));
        // Ratchet: DELETE-body + optional-body-merged caveats (FABLE_ROUNDTRIP #5/#7),
        // 32 + 140 at introduction — may only go down.
        Assert.True(
            BodyCaveats(r) <= 172,
            $"Expected ≤172 body caveats (ratchet), got {BodyCaveats(r)}"
        );
        // Image responses are now file endpoints, not unsupported
        Assert.True(
            UnsupportedResponse(r) <= 1,
            $"Expected ≤1 unsupported response, got {UnsupportedResponse(r)}"
        );
        // Ratchet (counts may only go DOWN): 12 at last audit — must not grow.
        Assert.True(
            UnsupportedError(r) <= 12,
            $"Expected ≤12 unsupported errors (ratchet, was 12), got {UnsupportedError(r)}"
        );
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
        Assert.True(
            UnsupportedError(r) <= 142,
            $"Expected ≤142 unsupported errors (ratchet, was 142), got {UnsupportedError(r)}"
        );
        Assert.Equal(0, UnsupportedBody(r));
        // Ratchet: DELETE-body + optional-body-merged caveats (FABLE_ROUNDTRIP #5/#7),
        // 2 + 3 at introduction — may only go down.
        Assert.True(
            BodyCaveats(r) <= 5,
            $"Expected ≤5 body caveats (ratchet), got {BodyCaveats(r)}"
        );

        // P2 wave 4: Jira's CustomFieldContextDefaultValue + WorkflowCondition unions
        // carry complete discriminator mappings — now reversed to [JsonPolymorphic]
        // hierarchies (2 bases at last audit — must not shrink). CustomContextVariable
        // declares sibling properties alongside its oneOf and falls back loudly.
        var polymorphicBases = CountPattern(r, "Types/", "[JsonPolymorphic(");
        Assert.True(
            polymorphicBases >= 2,
            $"Expected ≥2 polymorphic bases, got {polymorphicBases}"
        );
        var discriminatorDrops = r.Warnings.Count(w =>
            DiagnosticId(w) == Diagnostics.ImportDiscriminatorDropped
        );
        Assert.True(
            discriminatorDrops <= 1,
            $"Expected ≤1 discriminator-dropped warning (ratchet, was 1), got {discriminatorDrops}"
        );
    }

    // ========== Docker — mix of $ref responses and non-JSON ==========

    [Fact]
    public void Docker_Metrics()
    {
        var r = Import("docker");

        Assert.True(TypeFiles(r) >= 170, $"Expected ≥170 types, got {TypeFiles(r)}");
        Assert.True(
            TypedInputCount(r) >= 22,
            $"Expected ≥22 typed inputs, got {TypedInputCount(r)}"
        );
        Assert.Equal(0, UnsupportedBody(r));
        // Ratchet: optional-body-merged caveats (FABLE_ROUNDTRIP #7),
        // 9 at introduction — may only go down.
        Assert.True(
            BodyCaveats(r) <= 9,
            $"Expected ≤9 body caveats (ratchet), got {BodyCaveats(r)}"
        );
        // Ratchet (counts may only go DOWN): 17 at last audit — must not grow.
        Assert.True(
            UnsupportedError(r) <= 17,
            $"Expected ≤17 unsupported errors (ratchet, was 17), got {UnsupportedError(r)}"
        );
    }

    // ========== Slack — warnings from genuinely untyped schemas ==========

    [Fact]
    public void Slack_Metrics()
    {
        var r = Import("slack");

        Assert.True(TypeFiles(r) >= 220, $"Expected ≥220 types, got {TypeFiles(r)}");
        Assert.True(ContractFiles(r) >= 50, $"Expected ≥50 contracts, got {ContractFiles(r)}");

        // Slack has genuinely untyped schemas — warnings are expected. The exact
        // response-content pass now visits every status/media schema rather than only
        // the primary response, exposing 14 additional unresolved-schema sites.
        Assert.True(r.Warnings.Count <= 40, $"Expected ≤40 warnings, got {r.Warnings.Count}");
        Assert.True(r.Warnings.Count > 0, "Slack should have some warnings for untyped schemas");
    }
}
