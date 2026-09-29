namespace Rivet.Tests;

/// <summary>
/// Process-level harness for the `rivet --from &lt;contract.json&gt;` pipeline — the
/// invocation shape the rivet-ts vite plugin and rivet-php use. Post-Phase-3 the
/// pipeline's only output is the OpenAPI 3.1 spec.
/// </summary>
[Collection("Repository builds")]
public sealed class FromContractTests
{
    [Fact]
    public async Task FromContract_HeterogeneousScalarUnion_EmitsOneOfWithConst()
    {
        var repoRoot = CliRunner.RepoRoot;
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");
        using var tempDir = new TempDir();
        var contractPath = Path.Combine(tempDir.FullName, "contract.json");
        var outputDir = Path.Combine(tempDir.FullName, "generated");
        await File.WriteAllTextAsync(
            contractPath,
            """
            {
              "types": [{
                "name": "SettingsDto",
                "typeParameters": [],
                "properties": [{
                  "name": "idleTimeoutMs",
                  "type": {
                    "kind": "union",
                    "variants": [
                      { "kind": "primitive", "type": "number" },
                      { "kind": "literal", "value": false }
                    ]
                  },
                  "optional": false
                }]
              }],
              "enums": [],
              "endpoints": []
            }
            """
        );

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{contractPath}\" --output \"{outputDir}\"",
            repoRoot
        );
        Assert.True(exitCode == 0, $"--from failed (exit {exitCode}):\n{output}");

        using var document = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(outputDir, "openapi.json"))
        );
        var property = document
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("SettingsDto")
            .GetProperty("properties")
            .GetProperty("idleTimeoutMs");
        var oneOf = property.GetProperty("oneOf");
        Assert.Equal("number", oneOf[0].GetProperty("type").GetString());
        Assert.False(oneOf[1].GetProperty("const").GetBoolean());
    }

    [Fact]
    public async Task FromContract_PreviewToStdout_EmitsOpenApi()
    {
        var repoRoot = CliRunner.RepoRoot;
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\"",
            repoRoot
        );

        Assert.True(exitCode == 0, $"--from failed (exit {exitCode}):\n{output}");
        Assert.Contains("\"openapi\"", output);
        Assert.Contains("3.1.0", output);
        Assert.Contains("ProductDto", output);
        Assert.Contains("ProductStatus", output);
    }

    [Fact]
    public async Task FromContract_WithOutput_WritesOpenApiJson()
    {
        var repoRoot = CliRunner.RepoRoot;
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");
        using var outputDir = new TempDir();

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\" --output \"{outputDir.FullName}\"",
            repoRoot
        );

        Assert.True(exitCode == 0, $"--from --output failed (exit {exitCode}):\n{output}");

        // OpenAPI is the default output: --output <dir> writes <dir>/openapi.json
        var specPath = Path.Combine(outputDir.FullName, "openapi.json");
        Assert.True(File.Exists(specPath), $"expected OpenAPI spec at {specPath}");
        var spec = await File.ReadAllTextAsync(specPath);
        Assert.Contains("\"openapi\": \"3.1.0\"", spec);
        Assert.Contains("ProductDto", spec);

        // The TS outputs are gone — nothing else is written
        Assert.Empty(Directory.GetFiles(outputDir.FullName, "*.ts", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FromContract_WithRelativeOpenApiPath_WritesSpecUnderOutputDirectory()
    {
        var repoRoot = CliRunner.RepoRoot;
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");
        using var outputDir = new TempDir();
        var openApiFileName = "openapi.json";

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\" --openapi \"{openApiFileName}\" --output \"{outputDir.FullName}\"",
            repoRoot
        );

        Assert.True(
            exitCode == 0,
            $"--from --openapi --output failed (exit {exitCode}):\n{output}"
        );

        var expectedOpenApiPath = Path.Combine(outputDir.FullName, openApiFileName);
        Assert.True(
            File.Exists(expectedOpenApiPath),
            $"expected OpenAPI file at {expectedOpenApiPath}"
        );
        Assert.Contains("\"openapi\": \"3.1.0\"", await File.ReadAllTextAsync(expectedOpenApiPath));
    }

    [Fact]
    public async Task FromContract_QuietFlag_SuppressesStdout()
    {
        var repoRoot = CliRunner.RepoRoot;
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\" --quiet",
            repoRoot
        );

        Assert.True(exitCode == 0, $"--from --quiet failed (exit {exitCode}):\n{output}");
        Assert.DoesNotContain("ProductDto", output);
    }

    [Fact]
    public async Task FromContract_RemovedCompileFlag_FailsLoudly()
    {
        var repoRoot = CliRunner.RepoRoot;
        var fixture = Path.Combine(repoRoot, "Rivet.Tests", "Fixtures", "contract-sample.json");
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\" --compile",
            repoRoot
        );

        Assert.NotEqual(0, exitCode);
        Assert.Contains("removed in v2", output);
        Assert.Contains("openapi-typescript", output);
    }

    [Fact]
    public async Task FromContract_TaggedUnion_OpenApi_Has_Discriminator()
    {
        var repoRoot = CliRunner.RepoRoot;
        var fixture = Path.Combine(
            repoRoot,
            "Rivet.Tests",
            "Fixtures",
            "contract-tagged-union.json"
        );
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");
        using var outputDir = new TempDir();

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from \"{fixture}\" --output \"{outputDir.FullName}\" --openapi openapi.json",
            repoRoot
        );

        Assert.True(exitCode == 0, $"--from tagged union failed (exit {exitCode}):\n{output}");

        var openApiPath = Path.Combine(outputDir.FullName, "openapi.json");
        Assert.True(File.Exists(openApiPath));
        var openApiJson = await File.ReadAllTextAsync(openApiPath);
        Assert.Contains("\"discriminator\"", openApiJson);
        Assert.Contains("\"propertyName\": \"kind\"", openApiJson);
        Assert.Contains("\"oneOf\"", openApiJson);
    }

    [Fact]
    public async Task FromContract_InvalidPath_FailsGracefully()
    {
        var repoRoot = CliRunner.RepoRoot;
        var csproj = Path.Combine(repoRoot, "Rivet.Tool", "Rivet.Tool.csproj");

        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"run --project \"{csproj}\" -- --from /nonexistent/contract.json",
            repoRoot
        );

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("Unhandled exception", output);
    }
}
