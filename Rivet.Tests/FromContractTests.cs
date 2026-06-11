namespace Rivet.Tests;

/// <summary>
/// Process-level harness for the `rivet --from &lt;contract.json&gt;` pipeline — the
/// invocation shape the rivet-ts vite plugin and rivet-php use. Post-Phase-3 the
/// pipeline's only output is the OpenAPI 3.1 spec.
/// </summary>
public sealed class FromContractTests
{
    [Fact]
    public async Task FromContract_PreviewToStdout_EmitsOpenApi()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\"",
            repoRoot);

        Assert.True(exitCode == 0, $"--from failed (exit {exitCode}):\n{output}");
        Assert.Contains("\"openapi\"", output);
        Assert.Contains("3.1.0", output);
        Assert.Contains("ProductDto", output);
        Assert.Contains("ProductStatus", output);
    }

    [Fact]
    public async Task FromContract_WithOutput_WritesOpenApiJson()
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

            // OpenAPI is the default output: --output <dir> writes <dir>/openapi.json
            var specPath = Path.Combine(outputDir, "openapi.json");
            Assert.True(File.Exists(specPath), $"expected OpenAPI spec at {specPath}");
            var spec = await File.ReadAllTextAsync(specPath);
            Assert.Contains("\"openapi\": \"3.1.0\"", spec);
            Assert.Contains("ProductDto", spec);

            // The TS outputs are gone — nothing else is written
            Assert.Empty(Directory.GetFiles(outputDir, "*.ts", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task FromContract_WithRelativeOpenApiPath_WritesSpecUnderOutputDirectory()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");
        var outputDir = Path.Combine(Path.GetTempPath(), $"rivet-from-openapi-test-{Guid.NewGuid():N}");
        var openApiFileName = "openapi.json";

        try
        {
            var (exitCode, output) = await PublishFixture.RunProcessAsync(
                "dotnet",
                $"run --project \"{csproj}\" -- --from \"{fixture}\" --openapi \"{openApiFileName}\" --output \"{outputDir}\"",
                repoRoot);

            Assert.True(exitCode == 0, $"--from --openapi --output failed (exit {exitCode}):\n{output}");

            var expectedOpenApiPath = Path.Combine(outputDir, openApiFileName);
            Assert.True(File.Exists(expectedOpenApiPath), $"expected OpenAPI file at {expectedOpenApiPath}");
            Assert.Contains("\"openapi\": \"3.1.0\"", await File.ReadAllTextAsync(expectedOpenApiPath));
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
    }

    [Fact]
    public async Task FromContract_RemovedCompileFlag_FailsLoudly()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\" --compile",
            repoRoot);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("removed in v2", output);
        Assert.Contains("openapi-typescript", output);
    }

    [Fact]
    public async Task FromContract_TaggedUnion_OpenApi_Has_Discriminator()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-tagged-union.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");
        var outputDir = Path.Combine(Path.GetTempPath(), $"rivet-from-tagged-union-{Guid.NewGuid():N}");

        try
        {
            var (exitCode, output) = await PublishFixture.RunProcessAsync(
                "dotnet",
                $"run --project \"{csproj}\" -- --from \"{fixture}\" --output \"{outputDir}\" --openapi openapi.json",
                repoRoot);

            Assert.True(exitCode == 0, $"--from tagged union failed (exit {exitCode}):\n{output}");

            var openApiPath = Path.Combine(outputDir, "openapi.json");
            Assert.True(File.Exists(openApiPath));
            var openApiJson = await File.ReadAllTextAsync(openApiPath);
            Assert.Contains("\"discriminator\"", openApiJson);
            Assert.Contains("\"propertyName\": \"kind\"", openApiJson);
            Assert.Contains("\"oneOf\"", openApiJson);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
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
