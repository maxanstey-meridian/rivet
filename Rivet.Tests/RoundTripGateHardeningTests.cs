using System.Text.Json;

namespace Rivet.Tests;

public sealed class RoundTripGateHardeningTests
{
    [Fact]
    public void Only_The_Exact_Pinned_Docusign_Defect_Is_Exempt()
    {
        Assert.True(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                "docusign",
                RoundTripCorpusGateTests.DocusignSha256,
                RoundTripCorpusGateTests.DocusignSourceDefectDiagnostic,
                existingSourceDefectCount: 0
            )
        );

        Assert.False(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                "square",
                RoundTripCorpusGateTests.DocusignSha256,
                RoundTripCorpusGateTests.DocusignSourceDefectDiagnostic,
                existingSourceDefectCount: 0
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                "docusign",
                new string('0', 64),
                RoundTripCorpusGateTests.DocusignSourceDefectDiagnostic,
                existingSourceDefectCount: 0
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                "docusign",
                RoundTripCorpusGateTests.DocusignSha256,
                RoundTripCorpusGateTests.DocusignSourceDefectDiagnostic.Replace(
                    "customParameters",
                    "otherParameters",
                    StringComparison.Ordinal
                ),
                existingSourceDefectCount: 0
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                "docusign",
                RoundTripCorpusGateTests.DocusignSha256,
                RoundTripCorpusGateTests.DocusignSourceDefectDiagnostic,
                existingSourceDefectCount: 1
            )
        );

        var exactDefect = new RoundTripCorpusGateTests.SourceDefect(
            RoundTripCorpusGateTests.DocusignSourceDefectPointer,
            RoundTripCorpusGateTests.DocusignSourceDefectReason
        );
        Assert.True(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                "docusign",
                RoundTripCorpusGateTests.DocusignSha256,
                [exactDefect]
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                "docusign",
                RoundTripCorpusGateTests.DocusignSha256,
                [exactDefect, exactDefect]
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                "docusign",
                RoundTripCorpusGateTests.DocusignSha256,
                [exactDefect with { Path = "#/changed" }]
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                "docusign",
                RoundTripCorpusGateTests.DocusignSha256,
                [exactDefect with { Reason = "changed reason" }]
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                "docusign",
                new string('0', 64),
                [exactDefect]
            )
        );
    }

    [Fact]
    public void Artifact_Layout_Requires_All_Auditable_Evidence()
    {
        using var artifacts = TemporaryDirectory.Create();
        foreach (var name in RoundTripCorpusGateTests.RequiredArtifactFiles)
        {
            File.WriteAllText(Path.Combine(artifacts.Path, name), name);
        }
        Directory.CreateDirectory(Path.Combine(artifacts.Path, "first-generated"));
        Directory.CreateDirectory(Path.Combine(artifacts.Path, "second-generated"));
        File.WriteAllText(Path.Combine(artifacts.Path, "first-generated", "First.cs"), "first");
        File.WriteAllText(Path.Combine(artifacts.Path, "second-generated", "Second.cs"), "second");

        Assert.Empty(RoundTripCorpusGateTests.ValidateArtifactShape(artifacts.Path));

        File.Delete(Path.Combine(artifacts.Path, "first-details.json"));
        Directory.CreateDirectory(Path.Combine(artifacts.Path, "first-generated", "obj"));
        var findings = RoundTripCorpusGateTests.ValidateArtifactShape(artifacts.Path);
        Assert.Contains("retained artifact is missing: artifacts/first-details.json", findings);
        Assert.Contains("cache directory was retained: artifacts/first-generated/obj", findings);
    }

    [Fact]
    public void Generated_Artifact_Tree_Is_Exact_And_Excludes_Caches()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        Directory.CreateDirectory(Path.Combine(source.Path, "Models"));
        Directory.CreateDirectory(Path.Combine(source.Path, "obj"));
        Directory.CreateDirectory(Path.Combine(source.Path, "node_modules", "package"));
        File.WriteAllText(Path.Combine(source.Path, "Root.cs"), "public sealed class Root;");
        File.WriteAllText(
            Path.Combine(source.Path, "Models", "Nested.cs"),
            "public sealed class Nested;"
        );
        File.WriteAllText(Path.Combine(source.Path, "obj", "Cached.cs"), "cached");
        File.WriteAllText(
            Path.Combine(source.Path, "node_modules", "package", "Cached.cs"),
            "cached"
        );
        File.WriteAllText(Path.Combine(source.Path, "notes.txt"), "not generated C#");

        RoundTripCorpusGateTests.CopyGeneratedSourceTree(source.Path, destination.Path);

        Assert.Equal(
            ["Models/Nested.cs", "Root.cs"],
            Directory
                .EnumerateFiles(destination.Path, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(destination.Path, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
        Assert.Equal(
            "public sealed class Nested;",
            File.ReadAllText(Path.Combine(destination.Path, "Models", "Nested.cs"))
        );
    }

    [Fact]
    public void Result_Report_Contains_Exact_Gate_Metric_Shape()
    {
        var report = new RoundTripCorpusGateTests.GateReport(
            "docusign",
            RoundTripCorpusGateTests.DocusignSha256
        );
        report.Add("sourceDefects", "pinned defect");
        var summary = JsonSerializer.Deserialize<RoundTripCorpusGateTests.Summary>(
            """
            {
              "originalOps": 393,
              "reemittedOps": 393,
              "sharedOps": 393,
              "operationsWithFindings": 0,
              "integrityFindings": {
                "unresolved-reference": 2,
                "undefined-security-scheme": 1
              }
            }
            """,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!;
        report.SetMetrics(
            summary,
            new Dictionary<string, RoundTripCorpusGateTests.ComponentMetric>
            {
                ["schemas"] = new(565, 565, 565, 0, 0),
                ["requestBodies"] = new(52, 52, 52, 0, 0),
                ["securitySchemes"] = new(0, 0, 0, 0, 0),
            }
        );

        using var result = JsonDocument.Parse(RoundTripCorpusGateTests.SerializeReport(report));
        var metrics = result.RootElement.GetProperty("metrics");
        Assert.Equal(393, metrics.GetProperty("operations").GetProperty("source").GetInt32());
        Assert.Equal(
            565,
            metrics
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("matched")
                .GetInt32()
        );
        Assert.Equal(
            52,
            metrics
                .GetProperty("components")
                .GetProperty("requestBodies")
                .GetProperty("source")
                .GetInt32()
        );
        Assert.True(metrics.GetProperty("components").TryGetProperty("securitySchemes", out _));
        Assert.Equal(1, metrics.GetProperty("sourceDefects").GetInt32());
        Assert.Equal(3, metrics.GetProperty("comparatorIntegrityFindings").GetInt32());
    }

    private sealed class TemporaryDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"rivet-gate-hardening-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
