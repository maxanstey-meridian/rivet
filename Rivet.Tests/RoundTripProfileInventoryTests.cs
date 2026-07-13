using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Tests;

[Trait("Category", "Local")]
public sealed class RoundTripProfileInventoryTests
{
    [Fact]
    public void Verified_Profile_Matches_The_Artifacts()
    {
        var result = RunInventory();

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StdOut);
        Assert.True(output.RootElement.GetProperty("passed").GetBoolean());
        var facts = output.RootElement.GetProperty("facts");
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
            facts
                .GetProperty("corpora")
                .EnumerateArray()
                .Select(corpus => corpus.GetProperty("id").GetString()!)
                .ToArray()
        );
        Assert.Empty(facts.GetProperty("unknownKeywords").EnumerateArray());
        Assert.Equal(
            2081,
            facts.GetProperty("normalizedComponentTotals").GetProperty("schemas").GetInt32()
        );
        Assert.Equal(
            69,
            facts.GetProperty("normalizedComponentTotals").GetProperty("requestBodies").GetInt32()
        );
        Assert.Equal(
            12,
            facts
                .GetProperty("sourceComponentTotals")
                .GetProperty("components.securitySchemes")
                .GetInt32()
        );
        Assert.Equal(
            14,
            facts.GetProperty("normalizedComponentTotals").GetProperty("securitySchemes").GetInt32()
        );
        Assert.Equal(
            107,
            facts.GetProperty("normalizedComponentTotals").GetProperty("parameters").GetInt32()
        );
        Assert.Equal(
            95,
            facts.GetProperty("normalizedComponentTotals").GetProperty("responses").GetInt32()
        );
    }

    [Fact]
    public void Unknown_Extension_Fails_The_Inventory()
    {
        using var mutation = MutateOkta(document => document["x-unreviewed"] = true);

        var result = RunInventory("--document", $"okta={mutation.Path}");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unknown extension: x-unreviewed", Errors(result.StdOut));
    }

    [Fact]
    public void Unknown_Standard_Keyword_Fails_The_Inventory()
    {
        using var mutation = MutateOkta(document => document["unreviewedKeyword"] = true);

        var result = RunInventory("--document", $"okta={mutation.Path}");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unknown keyword: /unreviewedKeyword", Errors(result.StdOut));
    }

    [Fact]
    public void Changed_Reviewed_Disposition_Fails_The_Inventory()
    {
        var profile = JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("corpus", "verified-profile.json")))!
            .AsObject();
        profile["vendorExtensionDispositions"]!["x-logo"]!["disposition"] = "preserve";
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("reviewed vendor-extension disposition changed", Errors(result.StdOut));
    }

    [Fact]
    public void Changed_Profile_Fact_Fails_The_Inventory()
    {
        var profile = JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("corpus", "verified-profile.json")))!
            .AsObject();
        profile["facts"]!["corpora"]![0]!["operationCount"] = 18;
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("profile facts changed", Errors(result.StdOut));
    }

    [Fact]
    public void Roster_Mismatch_Fails_The_Inventory()
    {
        var profile = ReadProfile();
        profile["verifiedCorpusIds"]!.AsArray().Add("asana");
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("verified roster does not match profile facts", Errors(result.StdOut));
    }

    [Theory]
    [InlineData("notion")]
    [InlineData("circleci")]
    [InlineData("docker")]
    [InlineData("sendgrid")]
    public void Source_Defect_Policy_Broadening_Fails_The_Inventory(string corpusId)
    {
        var profile = ReadProfile();
        var sourceDefects = profile["sourceDefects"]!.AsArray();
        var defect = sourceDefects.First(item => item!["corpusId"]!.GetValue<string>() == corpusId);
        sourceDefects.Add(defect!.DeepClone());
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("reviewed source-defect policy changed", Errors(result.StdOut));
    }

    [Fact]
    public void Profile_Update_Cannot_Approve_A_Source_Defect_Policy_Change()
    {
        var profile = ReadProfile();
        var sourceDefects = profile["sourceDefects"]!.AsArray();
        sourceDefects.Add(sourceDefects[0]!.DeepClone());
        using var mutation = TemporaryJson.Write(profile);

        var result = RunInventory("--profile", mutation.Path, "--update-profile");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("reviewed source-defect policy changed", result.StdErr);
    }

    private static JsonObject ReadProfile() =>
        JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("corpus", "verified-profile.json")))!
            .AsObject();

    private static TemporaryJson MutateOkta(Action<JsonObject> mutate)
    {
        var document = JsonNode
            .Parse(File.ReadAllText(CliRunner.RepoPath("openapi", "okta.json")))!
            .AsObject();
        mutate(document);
        return TemporaryJson.Write(document);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunInventory(
        params string[] arguments
    ) =>
        CliRunner.Run(
            CliRunner.RepoPath(),
            "python3",
            [CliRunner.RepoPath("tools", "roundtrip-inventory.py"), .. arguments]
        );

    private static string[] Errors(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document
            .RootElement.GetProperty("errors")
            .EnumerateArray()
            .Select(error => error.GetString()!)
            .ToArray();
    }

    private sealed class TemporaryJson(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TemporaryJson Write(JsonNode value)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"rivet-verified-profile-{Guid.NewGuid():N}.json"
            );
            File.WriteAllText(path, value.ToJsonString());
            return new TemporaryJson(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
