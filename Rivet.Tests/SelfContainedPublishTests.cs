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
    public async Task SelfContained_Binary_EmitsFromProject()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        var sample = Path.Combine(
            CliRunner.RepoRoot,
            "samples",
            "ContractApi",
            "ContractApi.csproj"
        );
        using var outputDir = new TempDir();

        var (exitCode, output) = await CliRunner.RunAsync(
            _fixture.BinaryPath,
            $"--project \"{sample}\" --security bearer --output \"{outputDir.FullName}\""
        );

        Assert.True(exitCode == 0, $"--project failed (exit {exitCode}):\n{output}");
        Assert.True(File.Exists(Path.Combine(outputDir.FullName, "openapi.json")));
    }

    [Fact]
    public async Task SelfContained_Binary_EmitsFromLooseSourceFiles()
    {
        Assert.True(_fixture.PublishExitCode == 0, "Publish must succeed first");

        using var sourceDir = new TempDir();
        using var outputDir = new TempDir();
        var source = Path.Combine(sourceDir.FullName, "Contract.cs");
        await File.WriteAllTextAsync(
            source,
            """
            using System;
            using Rivet;

            namespace Smoke;

            public sealed record TaskDto(Guid Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> Get = Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """
        );

        var (exitCode, output) = await CliRunner.RunAsync(
            _fixture.BinaryPath,
            $"\"{source}\" --output \"{outputDir.FullName}\""
        );

        Assert.True(exitCode == 0, $"loose-file mode failed (exit {exitCode}):\n{output}");
        Assert.Contains(
            "TaskDto",
            await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "openapi.json"))
        );
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
    public async Task DotnetPack_Succeeds()
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

    /// <summary>
    /// MSBuildWorkspace launches its build host from BuildHost-netcore/ beside
    /// the binary; a single-file publish bundles the DLLs away and --project
    /// crashes.
    /// </summary>
    [Fact]
    public async Task CrossCompile_ForRid_ShipsMSBuildBuildHost()
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
        Assert.True(
            File.Exists(
                Path.Combine(
                    outDir.FullName,
                    "BuildHost-netcore",
                    "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll"
                )
            )
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
            $"publish \"{csproj}\" -c Release -r {rid} --self-contained -o \"{_publishDir.FullName}\"",
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
