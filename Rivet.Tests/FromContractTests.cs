namespace Rivet.Tests;

public sealed class FromContractTests
{
    [Fact]
    public async Task FromContract_PreviewToStdout_EmitsTypeScript()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\"",
            repoRoot);

        Assert.True(exitCode == 0, $"--from failed (exit {exitCode}):\n{output}");
        Assert.Contains("ProductDto", output);
        Assert.Contains("ProductStatus", output);
        Assert.Contains("getProduct", output);
    }

    [Fact]
    public async Task FromContract_WithOutput_WritesFiles()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");
        var outputDir = Path.Combine(Path.GetTempPath(), $"rivet-from-test-{Guid.NewGuid():N}");

        try
        {
            var (exitCode, output) = await PublishFixture.RunProcessAsync(
                "dotnet",
                $"run --project \"{csproj}\" -- --from \"{fixture}\" --output \"{outputDir}\"",
                repoRoot);

            Assert.True(exitCode == 0, $"--from --output failed (exit {exitCode}):\n{output}");

            var tsFiles = Directory.GetFiles(outputDir, "*.ts", SearchOption.AllDirectories);
            Assert.NotEmpty(tsFiles);

            // Should have types/ dir with type files
            var typesDir = Path.Combine(outputDir, "types");
            Assert.True(Directory.Exists(typesDir), "types/ directory should exist");

            // Should have client/ dir with client files
            var clientDir = Path.Combine(outputDir, "client");
            Assert.True(Directory.Exists(clientDir), "client/ directory should exist");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task FromContract_QuietFlag_SuppressesStdout()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\" --quiet",
            repoRoot);

        Assert.True(exitCode == 0, $"--from --quiet failed (exit {exitCode}):\n{output}");
        Assert.DoesNotContain("ProductDto", output);
        Assert.DoesNotContain("===", output);
    }

    [Fact]
    public async Task FromContract_JsonSchemaFlag_EmitsSchemaInPreview()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\" --jsonschema",
            repoRoot);

        Assert.True(exitCode == 0, $"--from --jsonschema failed (exit {exitCode}):\n{output}");
        Assert.Contains("=== schemas.ts ===", output);
        Assert.Contains("ProductDto", output);
    }

    [Fact]
    public async Task FromContract_InvalidPath_FailsGracefully()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from /nonexistent/contract.json",
            repoRoot);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("Unhandled exception", output);
    }
}
