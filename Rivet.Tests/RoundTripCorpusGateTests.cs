using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// Strict source-to-source corpus gate. A corpus passes only when the real CLI
/// imports it, compiles the generated C#, re-emits it without lossy diagnostics,
/// and the semantic comparator reports no structural or semantic findings.
/// </summary>
public sealed class RoundTripCorpusGateTests
{
    public static IEnumerable<object[]> Corpora()
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(CliRunner.RepoPath("corpus", "openapi-manifest.json")),
            _jsonOptions
        )!;
        return manifest.Corpora.Select(corpus => new object[] { corpus.Id, corpus.File });
    }

    [Theory]
    [MemberData(nameof(Corpora))]
    public void Corpus_Round_Trips_Without_Loss(string corpusId, string corpusFile)
    {
        var workDir = Directory.CreateTempSubdirectory($"rivet-roundtrip-{corpusId}-");
        try
        {
            var reportDirectory = CliRunner.RepoPath("TestResults", "roundtrip", corpusId);
            Directory.CreateDirectory(reportDirectory);
            var originalPath = CliRunner.RepoPath("openapi", corpusFile);
            var srcDir = Path.Combine(workDir.FullName, "src");
            var import = CliRunner.RunCli(
                workDir.FullName,
                [
                    "--from-openapi",
                    originalPath,
                    "--output",
                    srcDir,
                    "--namespace",
                    "Generated",
                ]
            );
            File.WriteAllText(Path.Combine(reportDirectory, "import.stderr.log"), import.StdErr);
            Assert.True(
                import.ExitCode == 0,
                WriteStageFailure(
                    reportDirectory,
                    corpusId,
                    "import",
                    import.ExitCode,
                    import.StdErr
                )
            );

            var outDir = Path.Combine(workDir.FullName, "out");
            var emit = CliRunner.RunCli(
                workDir.FullName,
                [srcDir, "--openapi", "--output", outDir]
            );
            File.WriteAllText(Path.Combine(reportDirectory, "emit.stderr.log"), emit.StdErr);
            Assert.True(
                emit.ExitCode == 0,
                WriteStageFailure(
                    reportDirectory,
                    corpusId,
                    "compile-reemit",
                    emit.ExitCode,
                    emit.StdErr
                )
            );

            var summaryPath = Path.Combine(workDir.FullName, "summary.json");
            var detailsPath = Path.Combine(workDir.FullName, "details.json");
            var diff = CliRunner.Run(
                workDir.FullName,
                "python3",
                [
                    CliRunner.RepoPath("tools", "roundtrip-diff.py"),
                    originalPath,
                    Path.Combine(outDir, "openapi.json"),
                    "--summary-json",
                    summaryPath,
                    "--details-json",
                    detailsPath,
                ]
            );
            File.WriteAllText(Path.Combine(reportDirectory, "diff.stderr.log"), diff.StdErr);
            Assert.True(
                diff.ExitCode is 0 or 1,
                WriteStageFailure(
                    reportDirectory,
                    corpusId,
                    "semantic-comparator",
                    diff.ExitCode,
                    diff.StdErr
                )
            );

            var summary = JsonSerializer.Deserialize<Summary>(
                File.ReadAllText(summaryPath),
                _jsonOptions
            )!;
            File.Copy(summaryPath, Path.Combine(reportDirectory, "summary.json"), overwrite: true);
            File.Copy(detailsPath, Path.Combine(reportDirectory, "details.json"), overwrite: true);

            var failures = DescribeFailures(import.StdErr, emit.StdErr, summary);
            WriteResult(reportDirectory, corpusId, import, emit, diff, summary, failures);
            Assert.True(
                diff.ExitCode == 0 && failures.Count == 0,
                $"{corpusId}: round-trip is lossy:\n  {string.Join("\n  ", failures)}\n"
                    + $"Full report: {reportDirectory}"
            );
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    private static string WriteStageFailure(
        string reportDirectory,
        string corpusId,
        string stage,
        int exitCode,
        string stderr
    )
    {
        File.WriteAllText(
            Path.Combine(reportDirectory, "result.json"),
            JsonSerializer.Serialize(
                new
                {
                    corpusId,
                    passed = false,
                    failedStage = stage,
                    exitCode,
                    diagnosticCount = CountLines(stderr),
                },
                _reportJsonOptions
            )
        );
        return $"{corpusId}: {stage} failed ({exitCode}); diagnostics: {CountLines(stderr)}; "
            + $"full report: {reportDirectory}";
    }

    private static void WriteResult(
        string reportDirectory,
        string corpusId,
        (int ExitCode, string StdOut, string StdErr) import,
        (int ExitCode, string StdOut, string StdErr) emit,
        (int ExitCode, string StdOut, string StdErr) diff,
        Summary summary,
        IReadOnlyCollection<string> failures
    )
    {
        File.WriteAllText(
            Path.Combine(reportDirectory, "result.json"),
            JsonSerializer.Serialize(
                new
                {
                    corpusId,
                    passed = diff.ExitCode == 0 && failures.Count == 0,
                    failedStage = failures.Count == 0 ? null : "semantic-diff",
                    importExitCode = import.ExitCode,
                    emitExitCode = emit.ExitCode,
                    diffExitCode = diff.ExitCode,
                    importDiagnosticCount = CountLines(import.StdErr),
                    emitDiagnosticCount = CountLines(emit.StdErr),
                    summary,
                },
                _reportJsonOptions
            )
        );
    }

    private static List<string> DescribeFailures(
        string importStdErr,
        string emitStdErr,
        Summary summary
    )
    {
        var failures = new List<string>();
        if (!string.IsNullOrWhiteSpace(importStdErr))
        {
            failures.Add($"lossy import diagnostics: {CountLines(importStdErr)}");
        }
        if (!string.IsNullOrWhiteSpace(emitStdErr))
        {
            failures.Add($"lossy emit diagnostics: {CountLines(emitStdErr)}");
        }

        AddCount(failures, "missing operations", summary.MissingOperations);
        AddCount(failures, "invented operations", summary.InventedOperations);
        AddCount(failures, "unmatched original schemas", summary.UnmatchedOriginalSchemas);
        AddCount(failures, "unmatched re-emitted schemas", summary.UnmatchedReemittedSchemas);
        AddCount(
            failures,
            "original schema-name collisions",
            summary.OriginalSchemaNameCollisions
        );
        AddCount(
            failures,
            "re-emitted schema-name collisions",
            summary.ReemittedSchemaNameCollisions
        );
        foreach (var (category, count) in summary.OpFindings.OrderBy(pair => pair.Key))
        {
            AddCount(failures, $"operation/{category}", count);
        }
        foreach (var (category, count) in summary.SchemaFindings.OrderBy(pair => pair.Key))
        {
            AddCount(failures, $"schema/{category}", count);
        }

        return failures;
    }

    private static int CountLines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;

    private static void AddCount(List<string> failures, string category, int count)
    {
        if (count > 0)
        {
            failures.Add($"{category}: {count}");
        }
    }

    private sealed record Manifest(int SchemaVersion, Corpus[] Corpora);

    private sealed record Corpus(string Id, string File);

    private sealed record Summary(
        int OriginalOps,
        int ReemittedOps,
        int SharedOps,
        int MissingOperations,
        int InventedOperations,
        int OriginalSchemas,
        int ReemittedSchemas,
        int MatchedSchemas,
        int UnmatchedOriginalSchemas,
        int UnmatchedReemittedSchemas,
        int OriginalSchemaNameCollisions,
        int ReemittedSchemaNameCollisions,
        int CleanOps,
        Dictionary<string, int> OpFindings,
        Dictionary<string, int> SchemaFindings
    );

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions _reportJsonOptions = new()
    {
        WriteIndented = true,
    };
}
