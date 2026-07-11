using Rivet.Tool;

namespace Rivet.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void ParseArgs_FromFlag_SetsFromContractPath()
    {
        var args = new[] { "--from", "contracts.json", "--output", "./generated" };

        var options = CliParser.ParseArgs(args);

        Assert.NotNull(options);
        Assert.Equal("contracts.json", options!.FromContractPath);
        Assert.Equal("./generated", options.OutputDir);
    }

    [Fact]
    public void ParseArgs_FromFlag_WithoutOutput_SetsStdoutMode()
    {
        var args = new[] { "--from", "contracts.json" };

        var options = CliParser.ParseArgs(args);

        Assert.NotNull(options);
        Assert.Equal("contracts.json", options!.FromContractPath);
        Assert.Null(options.OutputDir);
    }

    // ========== --verify: drift gate flag ==========

    [Theory]
    [InlineData("--project", "Api.csproj")]
    [InlineData("--from", "contracts.json")]
    public void ParseArgs_VerifyFlag_SetsVerify(string modeFlag, string modeValue)
    {
        var options = CliParser.ParseArgs([modeFlag, modeValue, "--output", "./generated", "--verify"]);

        Assert.NotNull(options);
        Assert.True(options!.Verify);
    }

    [Fact]
    public void ParseArgs_VerifyFlag_WithoutSpecTarget_Fails()
    {
        RivetOptions? options = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            options = CliParser.ParseArgs(["--project", "Api.csproj", "--verify"]));

        Assert.Null(options);
        Assert.Contains("'--verify' needs --output or --openapi", stderr);
    }

    [Fact]
    public void ParseArgs_VerifyFlag_WithOpenApiOverrideOnly_IsAccepted()
    {
        var options = CliParser.ParseArgs(["--project", "Api.csproj", "--openapi", "spec/openapi.json", "--verify"]);

        Assert.NotNull(options);
        Assert.True(options!.Verify);
    }

    [Fact]
    public void ParseArgs_VerifyFlag_InImportMode_Fails()
    {
        RivetOptions? options = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            options = CliParser.ParseArgs(["--from-openapi", "spec.json", "--output", "./out", "--verify"]));

        Assert.Null(options);
        Assert.Contains("does not apply to --from-openapi", stderr);
    }

    // ========== Removed-in-v2 flags: loud error, not "unknown flag" ==========

    [Theory]
    [InlineData("--compile")]
    [InlineData("--jsonschema")]
    public void ParseArgs_RemovedFlag_Fails_With_RemovedInV2_Error(string flag)
    {
        RivetOptions? options = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            options = CliParser.ParseArgs(["--from", "contracts.json", "--output", "./out", flag]));

        Assert.Null(options);
        Assert.Contains($"'{flag}' was removed in v2", stderr);
        Assert.Contains("openapi-typescript", stderr);
        Assert.DoesNotContain("unknown flag", stderr);
    }

    [Fact]
    public void ParseArgs_OpenApiFlag_WithoutValue_DefaultsFileName()
    {
        var options = CliParser.ParseArgs(["--from", "contracts.json", "--output", "./out", "--openapi"]);

        Assert.NotNull(options);
        Assert.Equal("openapi.json", options!.OpenApiPath);
    }

    [Fact]
    public void ParseArgs_OpenApiFlag_WithExplicitPath_SetsOverride()
    {
        var options = CliParser.ParseArgs(["--from", "contracts.json", "--output", "./out", "--openapi", "../spec/openapi.json"]);

        Assert.NotNull(options);
        Assert.Equal("../spec/openapi.json", options!.OpenApiPath);
    }

    [Fact]
    public void ParseArgs_FromFlag_ForwardsQuietFlag()
    {
        var args = new[] { "--from", "contracts.json", "--quiet" };

        var options = CliParser.ParseArgs(args);

        Assert.NotNull(options);
        Assert.True(options!.Quiet, "--quiet should be forwarded when using --from");
    }

    // ========== C3: dangling flag values ==========

    [Theory]
    [InlineData("--from")]
    [InlineData("--from-openapi")]
    [InlineData("--project")]
    [InlineData("-p")]
    [InlineData("--output")]
    [InlineData("-o")]
    [InlineData("--security")]
    [InlineData("--namespace")]
    [InlineData("--title")]
    [InlineData("--version")]
    [InlineData("--server")]
    public void ParseArgs_ValueTakingFlag_WithoutValue_Fails_With_Loud_Error(string flag)
    {
        RivetOptions? options = null;
        var stderr = CompilationHelper.CaptureStdErr(() => options = CliParser.ParseArgs([flag]));

        Assert.Null(options);
        Assert.Contains($"flag '{flag}' requires a value", stderr);
    }

    [Fact]
    public void ParseArgs_DanglingFlag_After_Valid_Args_Fails_With_Loud_Error()
    {
        RivetOptions? options = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            options = CliParser.ParseArgs(["--project", "app.csproj", "--output"]));

        Assert.Null(options);
        Assert.Contains("flag '--output' requires a value", stderr);
    }

    // ========== C7: unknown flags ==========

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-x")]
    [InlineData("--from-openapi-typo")]
    public void ParseArgs_UnknownFlag_Fails_With_Loud_Error(string flag)
    {
        RivetOptions? options = null;
        var stderr = CompilationHelper.CaptureStdErr(() => options = CliParser.ParseArgs([flag, "file.cs"]));

        Assert.Null(options);
        Assert.Contains($"unknown flag '{flag}'", stderr);
    }

    [Fact]
    public void ParseArgs_Plain_File_Arguments_Still_Accepted()
    {
        var options = CliParser.ParseArgs(["Contracts.cs", "Types.cs"]);

        Assert.NotNull(options);
        Assert.Equal(new[] { "Contracts.cs", "Types.cs" }, options!.Files);
        Assert.Equal("Contracts.cs", options.ProjectPath);
    }

    // ════════ --title / --version / --server (spec metadata plumbing) ════════

    [Fact]
    public void ParseArgs_TitleAndVersion_SetOptions()
    {
        var options = CliParser.ParseArgs(
            ["--project", "app.csproj", "--title", "Orders API", "--version", "2.3.0"]);

        Assert.NotNull(options);
        Assert.Equal("Orders API", options!.Title);
        Assert.Equal("2.3.0", options.Version);
    }

    [Fact]
    public void ParseArgs_Server_Repeatable_AccumulatesInOrder()
    {
        var options = CliParser.ParseArgs(
        [
            "--project", "app.csproj",
            "--server", "https://api.example.com",
            "--server", "https://staging.example.com",
            "--server", "/relative-base",
        ]);

        Assert.NotNull(options);
        Assert.Equal(
            new[] { "https://api.example.com", "https://staging.example.com", "/relative-base" },
            options!.Servers);
    }

    [Fact]
    public void ParseArgs_NoMetadataFlags_LeavesTitleVersionNull_And_ServersEmpty()
    {
        var options = CliParser.ParseArgs(["--project", "app.csproj"]);

        Assert.NotNull(options);
        Assert.Null(options!.Title);
        Assert.Null(options.Version);
        Assert.True(options.Servers is null or { Count: 0 });
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("example.com")]
    public void ParseArgs_Server_GarbageUrl_Fails_With_Loud_Error(string url)
    {
        RivetOptions? options = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            options = CliParser.ParseArgs(["--project", "app.csproj", "--server", url]));

        Assert.Null(options);
        Assert.Contains($"'--server' value '{url}' is not a valid URL", stderr);
    }

    [Fact]
    public void ParseArgs_MetadataFlags_FlowThrough_FromContractMode()
    {
        var options = CliParser.ParseArgs(
        [
            "--from", "contracts.json",
            "--title", "Orders API", "--version", "2.3.0",
            "--server", "https://api.example.com",
        ]);

        Assert.NotNull(options);
        Assert.Equal("contracts.json", options!.FromContractPath);
        Assert.Equal("Orders API", options.Title);
        Assert.Equal("2.3.0", options.Version);
        Assert.Equal(new[] { "https://api.example.com" }, options.Servers);
    }

    [Fact]
    public void PrintUsage_IncludesMetadataFlags()
    {
        var output = CompilationHelper.CaptureStdErr(CliParser.PrintUsage);

        Assert.Contains("--title", output);
        Assert.Contains("--version", output);
        Assert.Contains("--server", output);
    }

    [Fact]
    public void PrintUsage_IncludesFromFlag()
    {
        var output = CompilationHelper.CaptureStdErr(CliParser.PrintUsage);

        Assert.Contains("--from", output);
    }

    [Fact]
    public void ParseArgs_FromOpenApi_Rejects_Generation_Security_Configuration()
    {
        string[][] invalidArguments =
        [
            ["--from-openapi", "spec.json", "--security", "admin=bearer"],
            ["--from-openapi", "spec.json", "--security", "admin", "--security", "internal"],
        ];

        foreach (var args in invalidArguments)
        {
            RivetOptions? options = null;
            var stderr = CompilationHelper.CaptureStdErr(() => options = CliParser.ParseArgs(args));

            Assert.Null(options);
            Assert.Contains("--security with --from-openapi accepts one security scheme name", stderr);
        }
    }

    [Fact]
    public void ParseArgs_FromOpenApi_Accepts_Single_Security_Scheme_Name()
    {
        var options = CliParser.ParseArgs(
            ["--from-openapi", "spec.json", "--security", "admin"]);

        Assert.NotNull(options);
        Assert.Equal("admin", options!.DefaultSecurity);
    }
}
