using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// The FABLE_ROUNDTRIP audit (2026-06-12) as a permanent ratchet: import the
/// GitHub corpus through the real CLI, re-emit, semantic-diff against the
/// original with tools/roundtrip-diff.py, and compare category counts to the
/// committed baseline. Counts may only go DOWN — a new finding category or a
/// regression in an existing one fails the gate; an improvement fails too,
/// with instructions to ratchet the baseline so the gain is locked in.
/// </summary>
[Trait("Category", "Local")] // needs the gitignored openapi/ corpus + python3
public sealed class RoundTripCorpusGateTests
{
    [Fact]
    public void GitHub_Corpus_RoundTrip_Drift_Never_Regresses()
    {
        var workDir = Directory.CreateTempSubdirectory("rivet-roundtrip-gate-");
        try
        {
            var srcDir = Path.Combine(workDir.FullName, "src");
            var import = CliRunner.RunCli(
                workDir.FullName,
                ["--from-openapi", CliRunner.RepoPath("openapi", "github.json"), "--output", srcDir, "--namespace", "Generated"]);
            Assert.True(import.ExitCode == 0, $"import failed:\n{import.StdErr}");

            var outDir = Path.Combine(workDir.FullName, "out");
            var emit = CliRunner.RunCli(workDir.FullName, [srcDir, "--openapi", "--output", outDir]);
            Assert.True(emit.ExitCode == 0, $"compile/emit failed:\n{emit.StdErr}");

            var summaryPath = Path.Combine(workDir.FullName, "summary.json");
            var diff = CliRunner.Run(
                workDir.FullName,
                "python3",
                [
                    CliRunner.RepoPath("tools", "roundtrip-diff.py"),
                    CliRunner.RepoPath("openapi", "github.json"),
                    Path.Combine(outDir, "openapi.json"),
                    "--summary-json", summaryPath,
                ]);
            Assert.True(diff.ExitCode == 0, $"roundtrip-diff.py failed:\n{diff.StdErr}");

            var current = JsonSerializer.Deserialize<Summary>(File.ReadAllText(summaryPath), JsonOptions)!;
            var baselinePath = CliRunner.RepoPath("Rivet.Tests", "Fixtures", "roundtrip-baseline.json");
            var baseline = JsonSerializer.Deserialize<Summary>(File.ReadAllText(baselinePath), JsonOptions)!;

            var failures = new List<string>();
            var improvements = new List<string>();
            CompareCategories("op", baseline.OpFindings, current.OpFindings, failures, improvements);
            CompareCategories("schema", baseline.SchemaFindings, current.SchemaFindings, failures, improvements);

            if (current.CleanOps < baseline.CleanOps)
            {
                failures.Add($"clean ops regressed: {baseline.CleanOps} -> {current.CleanOps} (of {current.TotalOps})");
            }
            else if (current.CleanOps > baseline.CleanOps)
            {
                improvements.Add($"clean ops improved: {baseline.CleanOps} -> {current.CleanOps} (of {current.TotalOps})");
            }

            Assert.True(failures.Count == 0,
                "round-trip drift REGRESSED vs Rivet.Tests/Fixtures/roundtrip-baseline.json:\n  "
                + string.Join("\n  ", failures));

            // An improvement is also a "failure" — of the baseline, not the code.
            // Ratchet it down so the gain can never silently regress.
            Assert.True(improvements.Count == 0,
                "round-trip drift IMPROVED — lock it in by replacing "
                + $"Rivet.Tests/Fixtures/roundtrip-baseline.json with {summaryPath} "
                + "(re-run the gate to confirm, then commit the new baseline):\n  "
                + string.Join("\n  ", improvements));
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    private static void CompareCategories(
        string level,
        Dictionary<string, int> baseline,
        Dictionary<string, int> current,
        List<string> failures,
        List<string> improvements)
    {
        foreach (var (category, count) in current)
        {
            var allowed = baseline.GetValueOrDefault(category);
            if (count > allowed)
            {
                failures.Add($"{level}/{category}: {allowed} -> {count}");
            }
            else if (count < allowed)
            {
                improvements.Add($"{level}/{category}: {allowed} -> {count}");
            }
        }

        foreach (var (category, count) in baseline)
        {
            if (count > 0 && !current.ContainsKey(category))
            {
                improvements.Add($"{level}/{category}: {count} -> 0");
            }
        }
    }

    private sealed record Summary(
        int TotalOps,
        int CleanOps,
        Dictionary<string, int> OpFindings,
        Dictionary<string, int> SchemaFindings);

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };
}
