using System.Runtime.InteropServices;

namespace Rivet.Tests;

[Trait("Category", "Local")]
[Collection("Repository builds")]
public sealed class SelfContainedPublishTests : IClassFixture<PublishFixture>
{
    private readonly PublishFixture _fixture;

    public SelfContainedPublishTests(PublishFixture fixture) => _fixture = fixture;

    [Fact]
    public void SelfContained_Publish_Succeeds()
    {
        Assert.True(
            _fixture.PublishExitCode == 0,
            $"dotnet publish failed (exit {_fixture.PublishExitCode}):\n{_fixture.PublishOutput}"
        );
    }

    [Fact]
    public void SelfContained_Binary_Exists_After_Publish()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");
        Assert.True(
            File.Exists(_fixture.BinaryPath),
            $"Expected binary at {_fixture.BinaryPath} but it does not exist"
        );
    }

    [Fact]
    public async Task SelfContained_Binary_ShowsUsage()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var (exitCode, output) = await CliRunner.RunAsync(_fixture.BinaryPath, "");

        Assert.Equal(1, exitCode);
        Assert.Contains("--from-openapi", output);
    }

    [Fact]
    public async Task SelfContained_Binary_ImportsOpenApiSpec()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var repoRoot = CliRunner.RepoRoot;
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "openapi-petstore-v3.json");

        var (exitCode, output) = await CliRunner.RunAsync(
            _fixture.BinaryPath,
            $"--from-openapi \"{fixture}\" --namespace PetStore"
        );

        Assert.True(exitCode == 0, $"Import failed (exit {exitCode}):\n{output}");
        Assert.Contains("// ===", output);
        Assert.Contains("Pet", output);
    }

    [Fact]
    public async Task SelfContained_Binary_WritesOutputToDirectory()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var repoRoot = CliRunner.RepoRoot;
        var fixtureFile = Path.Combine(
            repoRoot,
            "Rivet.Tests",
            "Fixtures",
            "openapi-petstore-v3.json"
        );
        using var outputDir = new TempDir();

        var (exitCode, output) = await CliRunner.RunAsync(
            _fixture.BinaryPath,
            $"--from-openapi \"{fixtureFile}\" --namespace PetStore --output \"{outputDir.FullName}\""
        );

        Assert.True(exitCode == 0, $"Import with --output failed (exit {exitCode}):\n{output}");

        var generatedFiles = Directory.GetFiles(
            outputDir.FullName,
            "*.cs",
            SearchOption.AllDirectories
        );
        Assert.NotEmpty(generatedFiles);
        Assert.Contains("Generated", output);
    }

    [Fact]
    public async Task SelfContained_Binary_EmitsFromContractJson()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var repoRoot = CliRunner.RepoRoot;
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");

        var (exitCode, output) = await CliRunner.RunAsync(
            _fixture.BinaryPath,
            $"--from \"{fixture}\""
        );

        Assert.True(exitCode == 0, $"--from failed (exit {exitCode}):\n{output}");
        Assert.Contains("ProductDto", output);
        Assert.Contains("getProduct", output);
    }

    [Fact]
    public async Task SelfContained_Binary_FromContract_WritesOutputToDirectory()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var repoRoot = CliRunner.RepoRoot;
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        using var outputDir = new TempDir();

        var (exitCode, output) = await CliRunner.RunAsync(
            _fixture.BinaryPath,
            $"--from \"{fixture}\" --output \"{outputDir.FullName}\""
        );

        Assert.True(exitCode == 0, $"--from --output failed (exit {exitCode}):\n{output}");

        var specPath = Path.Combine(outputDir.FullName, "openapi.json");
        Assert.True(File.Exists(specPath), $"expected OpenAPI spec at {specPath}");
        Assert.Contains("\"openapi\": \"3.1.0\"", await File.ReadAllTextAsync(specPath));
        Assert.Contains("Generated", output);
    }

    [Fact]
    public async Task SelfContained_Binary_InvalidFilePath_FailsGracefully()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var (exitCode, output) = await CliRunner.RunAsync(
            _fixture.BinaryPath,
            "--from-openapi /nonexistent/path/spec.json --namespace Ns"
        );

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("Unhandled exception", output);
    }

    [Fact]
    public async Task DotnetPack_StillSucceeds_WithSingleFileConditional()
    {
        var repoRoot = CliRunner.RepoRoot;
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"pack \"{csproj}\" -c Release --no-restore",
            repoRoot
        );

        Assert.True(exitCode == 0, $"dotnet pack failed (exit {exitCode}):\n{output}");
    }

    [Fact]
    public async Task CrossCompile_ForRid_ProducesSingleFile()
    {
        var repoRoot = CliRunner.RepoRoot;
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");
        using var outDir = new TempDir();

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"publish \"{csproj}\" -c Release -r linux-x64 --self-contained -o \"{outDir.FullName}\"",
            repoRoot
        );

        Assert.True(exitCode == 0, $"Cross-compile failed (exit {exitCode}):\n{output}");

        var files = Directory
            .GetFiles(outDir.FullName)
            .Where(f => !f.EndsWith(".pdb") && !f.EndsWith(".json"))
            .ToArray();

        Assert.True(
            files.Length <= 3,
            $"Expected single-file output (≤3 non-pdb/json files) but found {files.Length}:\n"
                + string.Join("\n", files.Select(Path.GetFileName))
        );
    }
}

public sealed class PublishFixture : IAsyncLifetime
{
    private readonly TempDir _publishDir = new();

    public int PublishExitCode { get; private set; } = -1;
    public string PublishOutput { get; private set; } = "";
    public string BinaryPath { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var repoRoot = CliRunner.RepoRoot;
        var rid = RuntimeInformation.RuntimeIdentifier;
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"publish \"{csproj}\" -c Release -r {rid} --self-contained -p:PublishSingleFile=true -o \"{_publishDir.FullName}\"",
            repoRoot
        );

        PublishExitCode = exitCode;
        PublishOutput = output;

        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Rivet.Tool.exe"
            : "Rivet.Tool";
        BinaryPath = Path.Combine(_publishDir.FullName, binaryName);
    }

    public Task DisposeAsync()
    {
        _publishDir.Dispose();
        return Task.CompletedTask;
    }
}
