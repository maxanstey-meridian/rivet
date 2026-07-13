using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// Verified-corpus gate over the real, on-disk CLI pipeline. The local corpus is
/// deliberately mandatory: a missing or changed artifact is a gate failure.
/// </summary>
public sealed class RoundTripCorpusGateTests
{
    private static readonly Lazy<VerifiedProfile> _profile = new(
        () => LoadVerifiedProfile(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private static readonly string[] _componentNamespaces =
    [
        "schemas",
        "requestBodies",
        "parameters",
        "responses",
        "securitySchemes",
    ];

    internal static readonly string[] RequiredArtifactFiles =
    [
        "source.json",
        "inventory.stdout.log",
        "inventory.stderr.log",
        "first-import.stdout.log",
        "first-import.stderr.log",
        "first-compile.stdout.log",
        "first-compile.stderr.log",
        "first-emit.stdout.log",
        "first-emit.stderr.log",
        "first-openapi.json",
        "first-summary.json",
        "first-details.json",
        "first-diff.stdout.log",
        "first-diff.stderr.log",
        "second-import.stdout.log",
        "second-import.stderr.log",
        "second-compile.stdout.log",
        "second-compile.stderr.log",
        "second-emit.stdout.log",
        "second-emit.stderr.log",
        "second-openapi.json",
        "fixed-point-summary.json",
        "fixed-point-details.json",
        "fixed-point-diff.stdout.log",
        "fixed-point-diff.stderr.log",
    ];

    private static readonly Lazy<ProcessResult> _inventory = new(
        () =>
            ProcessResult.From(
                CliRunner.Run(
                    CliRunner.RepoPath(),
                    "python3",
                    [CliRunner.RepoPath("tools", "roundtrip-inventory.py")]
                )
            ),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    public static IEnumerable<object[]> Corpora()
    {
        var profile = _profile.Value;
        var manifest = JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(CliRunner.RepoPath("corpus", "openapi-manifest.json")),
            _jsonOptions
        )!;

        foreach (var corpusId in profile.VerifiedCorpusIds)
        {
            var corpus = manifest.Corpora.SingleOrDefault(candidate => candidate.Id == corpusId);
            if (corpus is null)
            {
                throw new InvalidDataException(
                    $"Verified corpus '{corpusId}' is absent from the manifest."
                );
            }

            yield return [corpus.Id, corpus.File, corpus.Sha256];
        }
    }

    [Theory]
    [MemberData(nameof(Corpora))]
    public void Corpus_Satisfies_The_Verified_Gate(
        string corpusId,
        string corpusFile,
        string sha256
    )
    {
        var workDir = Directory.CreateTempSubdirectory($"rivet-corpus-{corpusId}-");
        var reportDirectory = CliRunner.RepoPath("TestResults", "roundtrip", corpusId);
        if (Directory.Exists(reportDirectory))
        {
            Directory.Delete(reportDirectory, recursive: true);
        }

        var artifactDirectory = Path.Combine(reportDirectory, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var report = new GateReport(corpusId, sha256);

        try
        {
            var inventory = _inventory.Value;
            WriteProcessLog(artifactDirectory, "inventory", inventory);
            RecordInventory(report, inventory);
            if (report.HasFailures("inventory"))
            {
                Fail(reportDirectory, report);
                return;
            }

            var originalPath = CliRunner.RepoPath("openapi", corpusFile);
            VerifyArtifact(originalPath, sha256, report);
            CopyFileIfPresent(originalPath, Path.Combine(artifactDirectory, "source.json"));
            if (report.HasFailures("artifact"))
            {
                Fail(reportDirectory, report);
                return;
            }

            var firstSourceDirectory = Path.Combine(workDir.FullName, "first-src");
            var firstImport = Import(workDir.FullName, originalPath, firstSourceDirectory);
            WriteProcessLog(artifactDirectory, "first-import", firstImport);
            RecordDiagnostics(report, "first import", firstImport, allowSourceDefect: true);
            CopyGeneratedSourceTree(
                firstSourceDirectory,
                Path.Combine(artifactDirectory, "first-generated")
            );
            ValidateSourceDefectDiagnosticCount(report);
            if (firstImport.ExitCode != 0 || !Directory.Exists(firstSourceDirectory))
            {
                Fail(reportDirectory, report);
                return;
            }

            RecordMarkers(report, "markers", "first import", firstSourceDirectory);

            var firstCompile = Compile(workDir.FullName, firstSourceDirectory);
            WriteProcessLog(artifactDirectory, "first-compile", firstCompile);
            RecordCompilation(report, "first compilation", firstCompile);

            var firstOutputDirectory = Path.Combine(workDir.FullName, "first-out");
            var firstEmit = Emit(workDir.FullName, firstSourceDirectory, firstOutputDirectory);
            WriteProcessLog(artifactDirectory, "first-emit", firstEmit);
            RecordDiagnostics(report, "first emission", firstEmit, allowSourceDefect: false);
            var firstEmittedPath = Path.Combine(firstOutputDirectory, "openapi.json");
            CopyFileIfPresent(
                firstEmittedPath,
                Path.Combine(artifactDirectory, "first-openapi.json")
            );
            if (firstEmit.ExitCode != 0 || !File.Exists(firstEmittedPath))
            {
                if (!File.Exists(firstEmittedPath))
                {
                    report.Add("diagnostics", "first emission did not produce openapi.json");
                }

                Fail(reportDirectory, report);
                return;
            }

            foreach (var finding in RoundTripGateValidator.Validate(originalPath, firstEmittedPath))
            {
                report.Add("integrity", finding);
            }

            var firstDiff = RunDiff(
                workDir.FullName,
                artifactDirectory,
                "first",
                originalPath,
                firstEmittedPath
            );
            RecordSemanticFindings(report, firstDiff);
            ValidateComparatorSourceDefects(report, firstDiff);
            report.SetMetrics(firstDiff.Summary, CompareComponents(originalPath, firstEmittedPath));

            RunFixedPoint(workDir.FullName, artifactDirectory, firstEmittedPath, report);
            foreach (var finding in ValidateArtifactShape(artifactDirectory))
            {
                report.Add("artifact", finding);
            }

            WriteReport(reportDirectory, report);
            Assert.True(report.Passed, report.DescribeFailure(reportDirectory));
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    private static void RunFixedPoint(
        string workingDirectory,
        string artifactDirectory,
        string firstEmittedPath,
        GateReport report
    )
    {
        var secondSourceDirectory = Path.Combine(workingDirectory, "second-src");
        var secondImport = Import(workingDirectory, firstEmittedPath, secondSourceDirectory);
        WriteProcessLog(artifactDirectory, "second-import", secondImport);
        RecordFixedPointProcess(report, "second import", secondImport);
        CopyGeneratedSourceTree(
            secondSourceDirectory,
            Path.Combine(artifactDirectory, "second-generated")
        );
        if (secondImport.ExitCode != 0 || !Directory.Exists(secondSourceDirectory))
        {
            return;
        }

        RecordMarkers(report, "fixedPoint", "second import", secondSourceDirectory);

        var secondCompile = Compile(workingDirectory, secondSourceDirectory);
        WriteProcessLog(artifactDirectory, "second-compile", secondCompile);
        RecordFixedPointProcess(report, "second compilation", secondCompile, isCompilation: true);
        if (secondCompile.ExitCode != 0)
        {
            return;
        }

        var secondOutputDirectory = Path.Combine(workingDirectory, "second-out");
        var secondEmit = Emit(workingDirectory, secondSourceDirectory, secondOutputDirectory);
        WriteProcessLog(artifactDirectory, "second-emit", secondEmit);
        RecordFixedPointProcess(report, "second emission", secondEmit);
        var secondEmittedPath = Path.Combine(secondOutputDirectory, "openapi.json");
        CopyFileIfPresent(
            secondEmittedPath,
            Path.Combine(artifactDirectory, "second-openapi.json")
        );
        if (secondEmit.ExitCode != 0 || !File.Exists(secondEmittedPath))
        {
            if (!File.Exists(secondEmittedPath))
            {
                report.Add("fixedPoint", "second emission did not produce openapi.json");
            }

            return;
        }

        foreach (
            var finding in RoundTripGateValidator.Validate(firstEmittedPath, secondEmittedPath)
        )
        {
            report.Add("fixedPoint", $"second document: {finding}");
        }

        var fixedPointDiff = RunDiff(
            workingDirectory,
            artifactDirectory,
            "fixed-point",
            firstEmittedPath,
            secondEmittedPath
        );
        foreach (var finding in DescribeSemanticFindings(fixedPointDiff))
        {
            report.Add("fixedPoint", finding.Message, finding.Count);
        }
    }

    private static void VerifyArtifact(string path, string expectedSha256, GateReport report)
    {
        if (!File.Exists(path))
        {
            report.Add("artifact", $"artifact is missing: {path}");
            return;
        }

        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (!string.Equals(expectedSha256, actualSha256, StringComparison.Ordinal))
        {
            report.Add(
                "artifact",
                $"SHA-256 mismatch: expected {expectedSha256}, actual {actualSha256}"
            );
        }
    }

    private static void RecordInventory(GateReport report, ProcessResult process)
    {
        if (process.ExitCode != 0)
        {
            report.Add(
                "inventory",
                $"roundtrip-inventory.py exited {process.ExitCode}: {string.Join(" | ", Lines(process.StdErr))}"
            );
            return;
        }

        try
        {
            using var result = JsonDocument.Parse(process.StdOut);
            if (
                !result.RootElement.TryGetProperty("passed", out var passed)
                || passed.ValueKind != JsonValueKind.True
            )
            {
                report.Add("inventory", "roundtrip-inventory.py did not report passed=true");
            }
        }
        catch (JsonException exception)
        {
            report.Add(
                "inventory",
                $"roundtrip-inventory.py returned invalid JSON: {exception.Message}"
            );
        }
    }

    private static ProcessResult Import(
        string workingDirectory,
        string openApiPath,
        string sourceDirectory
    ) =>
        ProcessResult.From(
            CliRunner.RunCli(
                workingDirectory,
                [
                    "--from-openapi",
                    openApiPath,
                    "--output",
                    sourceDirectory,
                    "--namespace",
                    "Generated",
                ]
            )
        );

    private static ProcessResult Compile(string workingDirectory, string sourceDirectory) =>
        ProcessResult.From(CliRunner.RunCli(workingDirectory, [sourceDirectory, "--routes"]));

    private static ProcessResult Emit(
        string workingDirectory,
        string sourceDirectory,
        string outputDirectory
    ) =>
        ProcessResult.From(
            CliRunner.RunCli(
                workingDirectory,
                [sourceDirectory, "--openapi", "--output", outputDirectory]
            )
        );

    private static DiffResult RunDiff(
        string workingDirectory,
        string reportDirectory,
        string name,
        string originalPath,
        string reemittedPath
    )
    {
        var summaryPath = Path.Combine(reportDirectory, $"{name}-summary.json");
        var detailsPath = Path.Combine(reportDirectory, $"{name}-details.json");
        var process = ProcessResult.From(
            CliRunner.Run(
                workingDirectory,
                "python3",
                [
                    CliRunner.RepoPath("tools", "roundtrip-diff.py"),
                    originalPath,
                    reemittedPath,
                    "--summary-json",
                    summaryPath,
                    "--details-json",
                    detailsPath,
                ]
            )
        );
        WriteProcessLog(reportDirectory, $"{name}-diff", process);

        if (
            process.ExitCode is not (0 or 1)
            || !File.Exists(summaryPath)
            || !File.Exists(detailsPath)
        )
        {
            return new DiffResult(process, null, null);
        }

        return new DiffResult(
            process,
            JsonSerializer.Deserialize<Summary>(File.ReadAllText(summaryPath), _jsonOptions),
            JsonSerializer.Deserialize<Details>(File.ReadAllText(detailsPath), _jsonOptions)
        );
    }

    private static void RecordDiagnostics(
        GateReport report,
        string stage,
        ProcessResult process,
        bool allowSourceDefect
    )
    {
        if (process.ExitCode != 0)
        {
            report.Add("diagnostics", $"{stage} exited {process.ExitCode}");
        }

        foreach (var line in Lines(process.StdErr))
        {
            if (
                allowSourceDefect
                && IsSourceDefectDiagnostic(
                    _profile.Value,
                    report.CorpusId,
                    report.SourceSha256,
                    line,
                    report.SourceDefectCount
                )
            )
            {
                report.Add("sourceDefects", $"{stage}: {line}");
            }
            else
            {
                report.Add("diagnostics", $"{stage}: {line}");
            }
        }
    }

    internal static bool IsSourceDefectDiagnostic(
        VerifiedProfile profile,
        string corpusId,
        string sourceSha256,
        string line,
        int existingSourceDefectCount
    )
    {
        var expected = ExpectedSourceDefects(profile, corpusId, sourceSha256)
            .SelectMany(defect => Enumerable.Repeat(defect.Diagnostic, defect.Cardinality))
            .ToArray();
        return existingSourceDefectCount < expected.Length
            && line == expected[existingSourceDefectCount];
    }

    private static void ValidateSourceDefectDiagnosticCount(GateReport report)
    {
        var expected = ExpectedSourceDefects(_profile.Value, report.CorpusId, report.SourceSha256)
            .Sum(defect => defect.Cardinality);
        if (report.SourceDefectCount != expected)
        {
            report.Add(
                "diagnostics",
                $"source-defect diagnostic count mismatch: expected {expected}, actual {report.SourceDefectCount}"
            );
        }
    }

    private static void ValidateComparatorSourceDefects(GateReport report, DiffResult diff)
    {
        if (diff.Details is null)
        {
            return;
        }

        if (
            !IsAllowedComparatorSourceDefects(
                _profile.Value,
                report.CorpusId,
                report.SourceSha256,
                diff.Details.SourceDefects
            )
        )
        {
            report.Add(
                "integrity",
                $"comparator source defects did not match the pinned allowlist: {JsonSerializer.Serialize(diff.Details.SourceDefects)}"
            );
        }
    }

    internal static bool IsAllowedComparatorSourceDefects(
        VerifiedProfile profile,
        string corpusId,
        string sourceSha256,
        IReadOnlyList<SourceDefect> sourceDefects
    )
    {
        var expected = ExpectedSourceDefects(profile, corpusId, sourceSha256)
            .SelectMany(defect =>
                Enumerable.Repeat(
                    new SourceDefect(defect.Pointer, defect.Reason),
                    defect.Cardinality
                )
            );
        return sourceDefects.SequenceEqual(expected);
    }

    private static void RecordCompilation(GateReport report, string stage, ProcessResult process)
    {
        if (process.ExitCode != 0)
        {
            report.Add("compilation", $"{stage} exited {process.ExitCode}");
        }

        foreach (var line in Lines(process.StdErr))
        {
            if (IsRouteSummary(line))
            {
                continue;
            }

            report.Add("compilation", $"{stage}: {line}");
        }
    }

    private static void RecordFixedPointProcess(
        GateReport report,
        string stage,
        ProcessResult process,
        bool isCompilation = false
    )
    {
        if (process.ExitCode != 0)
        {
            report.Add("fixedPoint", $"{stage} exited {process.ExitCode}");
        }

        foreach (var line in Lines(process.StdErr))
        {
            if (isCompilation && IsRouteSummary(line))
            {
                continue;
            }

            report.Add("fixedPoint", $"{stage}: {line}");
        }
    }

    private static void RecordMarkers(
        GateReport report,
        string category,
        string stage,
        string sourceDirectory
    )
    {
        foreach (
            var file in Directory
                .EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
        )
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (line.Contains("[rivet:unsupported", StringComparison.Ordinal))
                {
                    report.Add(
                        category,
                        $"{stage}: {Path.GetRelativePath(sourceDirectory, file)}:{lineNumber}: {line.Trim()}"
                    );
                }
            }
        }
    }

    private static void RecordSemanticFindings(GateReport report, DiffResult diff)
    {
        if (diff.Summary is null)
        {
            report.Add(
                "document",
                $"semantic comparator exited {diff.Process.ExitCode}: {string.Join(" | ", Lines(diff.Process.StdErr))}"
            );
            return;
        }

        var summary = diff.Summary;
        AddCount(report, "operation", "missing operations", summary.MissingOperations);
        AddCount(report, "operation", "invented operations", summary.InventedOperations);
        AddCount(report, "schema", "unmatched original schemas", summary.UnmatchedOriginalSchemas);
        AddCount(
            report,
            "schema",
            "unmatched re-emitted schemas",
            summary.UnmatchedReemittedSchemas
        );
        AddFindings(report, "document", summary.DocumentFindings);
        AddFindings(report, "operation", summary.OpFindings);
        AddFindings(report, "schema", summary.SchemaFindings);
        AddFindings(report, "integrity", summary.IntegrityFindings);
    }

    private static IEnumerable<(string Message, int Count)> DescribeSemanticFindings(
        DiffResult diff
    )
    {
        if (diff.Summary is null)
        {
            yield return (
                $"semantic comparator exited {diff.Process.ExitCode}: {string.Join(" | ", Lines(diff.Process.StdErr))}",
                1
            );
            yield break;
        }

        var summary = diff.Summary;
        foreach (
            var finding in new (string Name, int Count)[]
            {
                ("missing operations", summary.MissingOperations),
                ("invented operations", summary.InventedOperations),
                ("unmatched original schemas", summary.UnmatchedOriginalSchemas),
                ("unmatched re-emitted schemas", summary.UnmatchedReemittedSchemas),
            }
        )
        {
            if (finding.Count > 0)
            {
                yield return ($"{finding.Name}: {finding.Count}", finding.Count);
            }
        }

        foreach (var finding in Flatten("document", summary.DocumentFindings))
        {
            yield return finding;
        }
        foreach (var finding in Flatten("operation", summary.OpFindings))
        {
            yield return finding;
        }
        foreach (var finding in Flatten("schema", summary.SchemaFindings))
        {
            yield return finding;
        }
        foreach (var finding in Flatten("integrity", summary.IntegrityFindings))
        {
            yield return finding;
        }
    }

    private static IEnumerable<(string Message, int Count)> Flatten(
        string scope,
        IReadOnlyDictionary<string, int> findings
    ) =>
        findings
            .Where(pair => pair.Value > 0)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => ($"{scope}/{pair.Key}: {pair.Value}", pair.Value));

    private static void AddFindings(
        GateReport report,
        string category,
        IReadOnlyDictionary<string, int> findings
    )
    {
        foreach (var finding in findings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AddCount(report, category, finding.Key, finding.Value);
        }
    }

    private static void AddCount(GateReport report, string category, string name, int count)
    {
        if (count > 0)
        {
            report.Add(category, $"{name}: {count}", count);
        }
    }

    private static bool IsRouteSummary(string line)
    {
        const string suffix = " route(s).";
        return line.EndsWith(suffix, StringComparison.Ordinal)
            && int.TryParse(line[..^suffix.Length], out _);
    }

    private static IEnumerable<string> Lines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void WriteProcessLog(string reportDirectory, string name, ProcessResult process)
    {
        File.WriteAllText(Path.Combine(reportDirectory, $"{name}.stdout.log"), process.StdOut);
        File.WriteAllText(Path.Combine(reportDirectory, $"{name}.stderr.log"), process.StdErr);
    }

    internal static void CopyGeneratedSourceTree(
        string sourceDirectory,
        string destinationDirectory
    )
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (
            var sourcePath in Directory
                .EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
        )
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            if (
                relativePath
                    .Split(Path.DirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj" or "node_modules" or "packages")
            )
            {
                continue;
            }

            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    internal static IReadOnlyList<string> ValidateArtifactShape(string artifactDirectory)
    {
        var findings = RequiredArtifactFiles
            .Where(name => !File.Exists(Path.Combine(artifactDirectory, name)))
            .Select(name => $"retained artifact is missing: artifacts/{name}")
            .ToList();

        foreach (var tree in new[] { "first-generated", "second-generated" })
        {
            var path = Path.Combine(artifactDirectory, tree);
            if (
                !Directory.Exists(path)
                || !Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories).Any()
            )
            {
                findings.Add($"retained generated C# tree is missing or empty: artifacts/{tree}");
            }
        }

        var cacheNames = new HashSet<string>(
            ["bin", "obj", "node_modules", "packages"],
            StringComparer.Ordinal
        );
        foreach (
            var directory in Directory
                .EnumerateDirectories(artifactDirectory, "*", SearchOption.AllDirectories)
                .Where(path => cacheNames.Contains(Path.GetFileName(path)))
        )
        {
            findings.Add(
                $"cache directory was retained: artifacts/{Path.GetRelativePath(artifactDirectory, directory).Replace('\\', '/')}"
            );
        }

        return findings;
    }

    internal static VerifiedProfile LoadVerifiedProfile(
        string? profilePath = null,
        string? manifestPath = null
    )
    {
        profilePath ??= CliRunner.RepoPath("corpus", "verified-profile.json");
        manifestPath ??= CliRunner.RepoPath("corpus", "openapi-manifest.json");
        var profileJson = File.ReadAllText(profilePath);
        var profile =
            JsonSerializer.Deserialize<VerifiedProfile>(profileJson, _jsonOptions)
            ?? throw new InvalidDataException("Verified profile is empty.");
        var manifest =
            JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), _jsonOptions)
            ?? throw new InvalidDataException("OpenAPI manifest is empty.");

        if (profile.ManifestCorpusCount != manifest.Corpora.Length)
        {
            throw new InvalidDataException(
                $"Manifest corpus denominator changed: expected {profile.ManifestCorpusCount}, actual {manifest.Corpora.Length}."
            );
        }

        var factIds = profile.Facts.Corpora.Select(corpus => corpus.Id).ToArray();
        if (
            profile.VerifiedCorpusIds.Length != profile.VerifiedCorpusIds.Distinct().Count()
            || !profile.VerifiedCorpusIds.SequenceEqual(factIds)
        )
        {
            throw new InvalidDataException("Verified roster does not match profile facts.");
        }

        foreach (var fact in profile.Facts.Corpora)
        {
            var manifestEntry = manifest.Corpora.SingleOrDefault(corpus => corpus.Id == fact.Id);
            if (manifestEntry is null || manifestEntry.Sha256 != fact.Sha256)
            {
                throw new InvalidDataException(
                    $"Manifest/profile hash mismatch for verified corpus '{fact.Id}'."
                );
            }
        }

        using var document = JsonDocument.Parse(profileJson);
        var sourceDefectJson = CanonicalJson(document.RootElement.GetProperty("sourceDefects"));
        var sourceDefectSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceDefectJson));
        if (sourceDefectSha256 != profile.ReviewedSourceDefectsSha256)
        {
            throw new InvalidDataException("Reviewed source-defect policy changed.");
        }

        foreach (var defect in profile.SourceDefects)
        {
            var fact = profile.Facts.Corpora.SingleOrDefault(corpus =>
                corpus.Id == defect.CorpusId
            );
            if (
                fact is null
                || fact.Sha256 != defect.SourceSha256
                || defect.Cardinality < 1
                || string.IsNullOrEmpty(defect.Pointer)
                || string.IsNullOrEmpty(defect.Reason)
                || string.IsNullOrEmpty(defect.Diagnostic)
            )
            {
                throw new InvalidDataException("Source-defect policy entry is invalid.");
            }
        }

        return profile;
    }

    private static byte[] CanonicalJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (
            var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
            )
        )
        {
            WriteCanonicalJson(writer, element);
        }
        return stream.ToArray();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (
                    var property in element
                        .EnumerateObject()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                )
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("Profile contains an unsupported JSON token.");
        }
    }

    private static IEnumerable<SourceDefectPolicy> ExpectedSourceDefects(
        VerifiedProfile profile,
        string corpusId,
        string sourceSha256
    ) =>
        profile.SourceDefects.Where(defect =>
            defect.CorpusId == corpusId && defect.SourceSha256 == sourceSha256
        );

    private static void CopyFileIfPresent(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    internal static Dictionary<string, ComponentMetric> CompareComponents(
        string sourcePath,
        string reemittedPath
    )
    {
        var source = ReadComponentIdentities(sourcePath);
        var reemitted = ReadComponentIdentities(reemittedPath);
        return _componentNamespaces.ToDictionary(
            namespaceName => namespaceName,
            namespaceName =>
            {
                var sourceNames = source[namespaceName];
                var reemittedNames = reemitted[namespaceName];
                return new ComponentMetric(
                    sourceNames.Count,
                    reemittedNames.Count,
                    sourceNames.Intersect(reemittedNames).Count(),
                    sourceNames.Except(reemittedNames).Count(),
                    reemittedNames.Except(sourceNames).Count()
                );
            },
            StringComparer.Ordinal
        );
    }

    private static Dictionary<string, HashSet<string>> ReadComponentIdentities(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var result = _componentNamespaces.ToDictionary(
            name => name,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal
        );

        if (root.TryGetProperty("components", out var components))
        {
            foreach (var namespaceName in result.Keys)
            {
                if (
                    components.TryGetProperty(namespaceName, out var values)
                    && values.ValueKind == JsonValueKind.Object
                )
                {
                    result[namespaceName]
                        .UnionWith(values.EnumerateObject().Select(property => property.Name));
                }
            }
        }

        AddLegacyComponents(root, "definitions", result["schemas"]);
        AddLegacyComponents(root, "securityDefinitions", result["securitySchemes"]);
        return result;
    }

    private static void AddLegacyComponents(
        JsonElement root,
        string propertyName,
        HashSet<string> destination
    )
    {
        if (
            root.TryGetProperty(propertyName, out var values)
            && values.ValueKind == JsonValueKind.Object
        )
        {
            destination.UnionWith(values.EnumerateObject().Select(property => property.Name));
        }
    }

    private static void Fail(string reportDirectory, GateReport report)
    {
        WriteReport(reportDirectory, report);
        Assert.Fail(report.DescribeFailure(reportDirectory));
    }

    private static void WriteReport(string reportDirectory, GateReport report) =>
        File.WriteAllText(Path.Combine(reportDirectory, "result.json"), SerializeReport(report));

    internal static string SerializeReport(GateReport report) =>
        JsonSerializer.Serialize(report.ToSerializable(), _reportJsonOptions);

    internal sealed class GateReport(string corpusId, string sourceSha256)
    {
        private static readonly string[] _categoryNames =
        [
            "inventory",
            "artifact",
            "sourceDefects",
            "diagnostics",
            "markers",
            "compilation",
            "document",
            "operation",
            "schema",
            "integrity",
            "fixedPoint",
        ];

        private readonly Dictionary<string, List<GateFinding>> _findings =
            _categoryNames.ToDictionary(
                name => name,
                _ => new List<GateFinding>(),
                StringComparer.Ordinal
            );

        private Summary? _summary;
        private Dictionary<string, ComponentMetric>? _components;

        public string CorpusId { get; } = corpusId;

        public string SourceSha256 { get; } = sourceSha256;

        public int SourceDefectCount => _findings["sourceDefects"].Sum(finding => finding.Count);

        public bool Passed =>
            _findings.Where(pair => pair.Key != "sourceDefects").All(pair => pair.Value.Count == 0);

        public void Add(string category, string finding, int count = 1) =>
            _findings[category].Add(new GateFinding(count, finding));

        public bool HasFailures(string category) => _findings[category].Count > 0;

        public void SetMetrics(Summary? summary, Dictionary<string, ComponentMetric> components)
        {
            _summary = summary;
            _components = components;
        }

        internal object ToSerializable() =>
            new
            {
                corpusId = CorpusId,
                passed = Passed,
                metrics = new
                {
                    operations = _summary is null
                        ? null
                        : new OperationMetric(
                            _summary.OriginalOps,
                            _summary.ReemittedOps,
                            _summary.SharedOps,
                            _summary.MissingOperations,
                            _summary.InventedOperations,
                            _summary.OperationsWithFindings
                        ),
                    components = _components,
                    sourceDefects = SourceDefectCount,
                    comparatorIntegrityFindings = _summary?.IntegrityFindings.Values.Sum() ?? 0,
                },
                categories = _findings.ToDictionary(
                    pair => pair.Key,
                    pair => new
                    {
                        count = pair.Value.Sum(finding => finding.Count),
                        findings = pair.Value.ToArray(),
                    },
                    StringComparer.Ordinal
                ),
            };

        public string DescribeFailure(string reportDirectory)
        {
            var counts = _findings.Select(pair =>
                $"{pair.Key}={pair.Value.Sum(finding => finding.Count)}"
            );
            return $"{CorpusId}: verified-corpus gate failed ({string.Join(", ", counts)}). Full report: {reportDirectory}";
        }
    }

    internal sealed record GateFinding(int Count, string Message);

    private sealed record Manifest(int SchemaVersion, Corpus[] Corpora);

    private sealed record Corpus(string Id, string File, string Sha256);

    internal sealed record Summary
    {
        public int OriginalOps { get; init; }
        public int ReemittedOps { get; init; }
        public int SharedOps { get; init; }
        public int MissingOperations { get; init; }
        public int InventedOperations { get; init; }
        public int OperationsWithFindings { get; init; }
        public int UnmatchedOriginalSchemas { get; init; }
        public int UnmatchedReemittedSchemas { get; init; }
        public Dictionary<string, int> DocumentFindings { get; init; } = [];
        public Dictionary<string, int> OpFindings { get; init; } = [];
        public Dictionary<string, int> SchemaFindings { get; init; } = [];
        public Dictionary<string, int> IntegrityFindings { get; init; } = [];
        public int SourceDefects { get; init; }
    }

    internal sealed record Details
    {
        public SourceDefect[] SourceDefects { get; init; } = [];
    }

    internal sealed record SourceDefect(string Path, string Reason);

    internal sealed record SourceDefectPolicy(
        string CorpusId,
        string SourceSha256,
        string Pointer,
        string Reason,
        string Diagnostic,
        int Cardinality
    );

    internal sealed record VerifiedProfile(
        int SchemaVersion,
        int ManifestCorpusCount,
        string[] VerifiedCorpusIds,
        string ReviewedSourceDefectsSha256,
        SourceDefectPolicy[] SourceDefects,
        ProfileFacts Facts
    );

    internal sealed record ProfileFacts(ProfileCorpus[] Corpora);

    internal sealed record ProfileCorpus(string Id, string Sha256);

    internal sealed record OperationMetric(
        int Source,
        int Reemitted,
        int Shared,
        int Missing,
        int Invented,
        int WithFindings
    );

    internal sealed record ComponentMetric(
        int Source,
        int Reemitted,
        int Matched,
        int Missing,
        int Invented
    );

    private sealed record DiffResult(ProcessResult Process, Summary? Summary, Details? Details);

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
    {
        public static ProcessResult From((int ExitCode, string StdOut, string StdErr) value) =>
            new(value.ExitCode, value.StdOut, value.StdErr);
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions _reportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
