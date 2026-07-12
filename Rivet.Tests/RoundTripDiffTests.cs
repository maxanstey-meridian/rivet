using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Tests;

public sealed class RoundTripDiffTests
{
    [Fact]
    public void Missing_Operation_Is_Reported_And_Fails_The_Diff()
    {
        var workDir = Directory.CreateTempSubdirectory("rivet-roundtrip-diff-");
        try
        {
            var originalPath = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "roundtrip-probe.json"
            );
            var reemitted = JsonNode.Parse(File.ReadAllText(originalPath))!.AsObject();
            reemitted["paths"]!["/things/{thing_id}"]!.AsObject().Remove("put");
            var reemittedPath = Path.Combine(workDir.FullName, "reemitted.json");
            File.WriteAllText(reemittedPath, reemitted.ToJsonString());

            var summaryPath = Path.Combine(workDir.FullName, "summary.json");
            var detailsPath = Path.Combine(workDir.FullName, "details.json");
            var result = CliRunner.Run(
                workDir.FullName,
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
            );

            Assert.Equal(1, result.ExitCode);
            using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
            Assert.Equal(8, summary.RootElement.GetProperty("originalOps").GetInt32());
            Assert.Equal(7, summary.RootElement.GetProperty("reemittedOps").GetInt32());
            Assert.Equal(1, summary.RootElement.GetProperty("missingOperations").GetInt32());

            using var details = JsonDocument.Parse(File.ReadAllText(detailsPath));
            var missing = Assert.Single(
                details.RootElement.GetProperty("missingOperations").EnumerateArray()
            );
            Assert.Equal("/things/{thing_id}", missing[0].GetString());
            Assert.Equal("put", missing[1].GetString());
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Missing_Schema_Is_Reported_And_Fails_The_Diff()
    {
        var workDir = Directory.CreateTempSubdirectory("rivet-roundtrip-diff-");
        try
        {
            var originalPath = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "roundtrip-probe.json"
            );
            var reemitted = JsonNode.Parse(File.ReadAllText(originalPath))!.AsObject();
            reemitted["components"]!["schemas"]!.AsObject().Remove("nullable-owner");
            var reemittedPath = Path.Combine(workDir.FullName, "reemitted.json");
            File.WriteAllText(reemittedPath, reemitted.ToJsonString());

            var summaryPath = Path.Combine(workDir.FullName, "summary.json");
            var detailsPath = Path.Combine(workDir.FullName, "details.json");
            var result = CliRunner.Run(
                workDir.FullName,
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
            );

            Assert.Equal(1, result.ExitCode);
            using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
            Assert.Equal(2, summary.RootElement.GetProperty("originalSchemas").GetInt32());
            Assert.Equal(1, summary.RootElement.GetProperty("reemittedSchemas").GetInt32());
            Assert.Equal(1, summary.RootElement.GetProperty("unmatchedOriginalSchemas").GetInt32());

            using var details = JsonDocument.Parse(File.ReadAllText(detailsPath));
            var missing = Assert.Single(
                details.RootElement.GetProperty("unmatchedOriginalSchemas").EnumerateArray()
            );
            Assert.Equal("nullable-owner", missing.GetString());
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }
}
