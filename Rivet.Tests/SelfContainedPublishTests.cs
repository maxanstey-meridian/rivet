using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rivet.Tests;

[Trait("Category", "Local")]
public sealed class SelfContainedPublishTests : IClassFixture<PublishFixture>
{
    private readonly PublishFixture _fixture;

    public SelfContainedPublishTests(PublishFixture fixture) => _fixture = fixture;

    [Fact]
    public void SelfContained_Publish_Succeeds()
    {
        Assert.True(_fixture.PublishExitCode == 0,
            $"dotnet publish failed (exit {_fixture.PublishExitCode}):\n{_fixture.PublishOutput}");
    }

    [Fact]
    public void SelfContained_Binary_Exists_After_Publish()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");
        Assert.True(File.Exists(_fixture.BinaryPath),
            $"Expected binary at {_fixture.BinaryPath} but it does not exist");
    }

    [Fact]
    public async Task SelfContained_Binary_ShowsUsage()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(_fixture.BinaryPath, "");

        Assert.Equal(1, exitCode);
        Assert.Contains("--from-openapi", output);
    }

    [Fact]
    public async Task SelfContained_Binary_ImportsOpenApiSpec()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var repoRoot = PublishFixture.FindRepoRoot();
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "openapi-petstore-v3.json");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(
            _fixture.BinaryPath,
            $"--from-openapi \"{fixture}\" --namespace PetStore");

        Assert.True(exitCode == 0, $"Import failed (exit {exitCode}):\n{output}");
        Assert.Contains("// ===", output);
        Assert.Contains("Pet", output);
    }

    [Fact]
    public async Task SelfContained_Binary_WritesOutputToDirectory()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var repoRoot = PublishFixture.FindRepoRoot();
        var fixtureFile = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "openapi-petstore-v3.json");
        var outputDir = Path.Combine(Path.GetTempPath(), $"rivet-output-test-{Guid.NewGuid():N}");

        try
        {
            var (exitCode, output) = await PublishFixture.RunProcessAsync(
                _fixture.BinaryPath,
                $"--from-openapi \"{fixtureFile}\" --namespace PetStore --output \"{outputDir}\"");

            Assert.True(exitCode == 0, $"Import with --output failed (exit {exitCode}):\n{output}");

            var generatedFiles = Directory.GetFiles(outputDir, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(generatedFiles);
            Assert.Contains("Generated", output);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task SelfContained_Binary_InvalidFilePath_FailsGracefully()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(
            _fixture.BinaryPath,
            "--from-openapi /nonexistent/path/spec.json --namespace Ns");

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("Unhandled exception", output);
    }

    [Fact]
    public async Task DotnetPack_StillSucceeds_WithSingleFileConditional()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await PublishFixture.RunProcessAsync(
            "dotnet",
            $"pack \"{csproj}\" -c Release --no-restore",
            repoRoot);

        Assert.True(exitCode == 0, $"dotnet pack failed (exit {exitCode}):\n{output}");
    }

    [Fact]
    public async Task CrossCompile_ForRid_ProducesSingleFile()
    {
        var repoRoot = PublishFixture.FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");
        var outDir = Path.Combine(Path.GetTempPath(), $"rivet-cross-test-{Guid.NewGuid():N}");

        try
        {
            var (exitCode, output) = await PublishFixture.RunProcessAsync(
                "dotnet",
                $"publish \"{csproj}\" -c Release -r linux-x64 --self-contained -o \"{outDir}\"",
                repoRoot);

            Assert.True(exitCode == 0, $"Cross-compile failed (exit {exitCode}):\n{output}");

            var files = Directory.GetFiles(outDir)
                .Where(f => !f.EndsWith(".pdb") && !f.EndsWith(".json"))
                .ToArray();

            Assert.True(files.Length <= 3,
                $"Expected single-file output (≤3 non-pdb/json files) but found {files.Length}:\n"
                + string.Join("\n", files.Select(Path.GetFileName)));
        }
        finally
        {
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, recursive: true);
        }
    }
}

public sealed class PublishFixture : IAsyncLifetime
{
    private string _tempDir = null!;

    public int PublishExitCode { get; private set; } = -1;
    public string PublishOutput { get; private set; } = "";
    public string BinaryPath { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rivet-publish-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var repoRoot = FindRepoRoot();
        var rid = RuntimeInformation.RuntimeIdentifier;
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await RunProcessAsync(
            "dotnet",
            $"publish \"{csproj}\" -c Release -r {rid} --self-contained -p:PublishSingleFile=true -o \"{_tempDir}\"",
            repoRoot);

        PublishExitCode = exitCode;
        PublishOutput = output;

        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Rivet.Tool.exe"
            : "Rivet.Tool";
        BinaryPath = Path.Combine(_tempDir, binaryName);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        return Task.CompletedTask;
    }

    internal static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Rivet.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not find repo root (Rivet.slnx)");
    }

    internal static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName, string arguments, string? workingDir = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var output = string.Join("\n",
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return (process.ExitCode, output);
    }
}
