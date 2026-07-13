using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Tests;

public sealed class RoundTripGateHardeningTests
{
    [Fact]
    public void Only_The_Exact_Profiled_Source_Defect_Is_Exempt()
    {
        var profile = RoundTripCorpusGateTests.LoadVerifiedProfile();
        var policy = profile.SourceDefects.Single(defect => defect.CorpusId == "notion");
        Assert.True(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                profile,
                policy.CorpusId,
                policy.SourceSha256,
                policy.Diagnostic,
                existingSourceDefectCount: 0
            )
        );

        Assert.False(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                profile,
                "square",
                policy.SourceSha256,
                policy.Diagnostic,
                existingSourceDefectCount: 0
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                profile,
                policy.CorpusId,
                new string('0', 64),
                policy.Diagnostic,
                existingSourceDefectCount: 0
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                profile,
                policy.CorpusId,
                policy.SourceSha256,
                policy.Diagnostic.Replace(
                    "/v1/pages/{id}",
                    "/v1/other/{id}",
                    StringComparison.Ordinal
                ),
                existingSourceDefectCount: 0
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsSourceDefectDiagnostic(
                profile,
                policy.CorpusId,
                policy.SourceSha256,
                policy.Diagnostic,
                existingSourceDefectCount: 1
            )
        );

        var exactDefect = new RoundTripCorpusGateTests.SourceDefect(policy.Pointer, policy.Reason);
        Assert.True(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                profile,
                policy.CorpusId,
                policy.SourceSha256,
                [exactDefect]
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                profile,
                policy.CorpusId,
                policy.SourceSha256,
                [exactDefect, exactDefect]
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                profile,
                policy.CorpusId,
                policy.SourceSha256,
                [exactDefect with { Path = "#/changed" }]
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                profile,
                policy.CorpusId,
                policy.SourceSha256,
                [exactDefect with { Reason = "changed reason" }]
            )
        );
        Assert.False(
            RoundTripCorpusGateTests.IsAllowedComparatorSourceDefects(
                profile,
                policy.CorpusId,
                new string('0', 64),
                [exactDefect]
            )
        );
    }

    [Fact]
    public void Profile_Roster_Rejects_Manifest_Auto_Admission_And_Roster_Drift()
    {
        var profile = RoundTripCorpusGateTests.LoadVerifiedProfile();
        Assert.Equal(
            [
                "okta",
                "petstore-v2",
                "petstore-v3",
                "twilio",
                "square",
                "docusign",
                "notion",
                "circleci",
                "firebase",
                "docker",
                "sendgrid",
                "spotify",
                "asana",
            ],
            profile.VerifiedCorpusIds
        );
        Assert.Equal(25, profile.ManifestCorpusCount);

        var profileNode = JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("corpus", "verified-profile.json")))!
            .AsObject();
        profileNode["verifiedCorpusIds"]!.AsArray().Add("bitbucket");
        using var mutation = TemporaryJson.Write(profileNode);

        Assert.Throws<InvalidDataException>(() =>
            RoundTripCorpusGateTests.LoadVerifiedProfile(mutation.Path)
        );
    }

    [Theory]
    [InlineData("notion")]
    [InlineData("circleci")]
    [InlineData("docker")]
    [InlineData("sendgrid")]
    public void Profile_Rejects_Source_Defect_Policy_Broadening(string corpusId)
    {
        var profileNode = JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("corpus", "verified-profile.json")))!
            .AsObject();
        var sourceDefects = profileNode["sourceDefects"]!.AsArray();
        var defect = sourceDefects.First(item => item!["corpusId"]!.GetValue<string>() == corpusId);
        sourceDefects.Add(defect!.DeepClone());
        using var mutation = TemporaryJson.Write(profileNode);

        Assert.Throws<InvalidDataException>(() =>
            RoundTripCorpusGateTests.LoadVerifiedProfile(mutation.Path)
        );
    }

    [Fact]
    public void Component_Metrics_Include_Parameters_And_Responses()
    {
        using var source = TemporaryJson.Write(
            JsonNode.Parse("""{"components":{"parameters":{"p":{}},"responses":{"r":{}}}}""")!
        );
        using var reemitted = TemporaryJson.Write(
            JsonNode.Parse("""{"components":{"parameters":{"p":{}},"responses":{"r":{}}}}""")!
        );

        var metrics = RoundTripCorpusGateTests.CompareComponents(source.Path, reemitted.Path);

        Assert.Equal(
            ["parameters", "requestBodies", "responses", "schemas", "securitySchemes"],
            metrics.Keys.Order(StringComparer.Ordinal).ToArray()
        );
        Assert.Equal(1, metrics["parameters"].Matched);
        Assert.Equal(1, metrics["responses"].Matched);
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
            "notion",
            RoundTripCorpusGateTests
                .LoadVerifiedProfile()
                .SourceDefects.Single(defect => defect.CorpusId == "notion")
                .SourceSha256
        );
        report.Add("sourceDefects", "pinned defect");
        var summary = JsonSerializer.Deserialize<RoundTripCorpusGateTests.Summary>(
            """
            {
              "originalOps": 13,
              "reemittedOps": 13,
              "sharedOps": 13,
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
                ["schemas"] = new(0, 0, 0, 0, 0),
                ["requestBodies"] = new(0, 0, 0, 0, 0),
                ["parameters"] = new(0, 0, 0, 0, 0),
                ["responses"] = new(0, 0, 0, 0, 0),
                ["securitySchemes"] = new(0, 0, 0, 0, 0),
            }
        );

        using var result = JsonDocument.Parse(RoundTripCorpusGateTests.SerializeReport(report));
        var metrics = result.RootElement.GetProperty("metrics");
        Assert.Equal(13, metrics.GetProperty("operations").GetProperty("source").GetInt32());
        Assert.Equal(
            0,
            metrics
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("matched")
                .GetInt32()
        );
        Assert.Equal(
            0,
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

    private sealed class TemporaryJson(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TemporaryJson Write(JsonNode value)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"rivet-gate-profile-{Guid.NewGuid():N}.json"
            );
            File.WriteAllText(path, value.ToJsonString());
            return new TemporaryJson(path);
        }

        public void Dispose() => File.Delete(Path);
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
