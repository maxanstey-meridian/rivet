using Rivet.Tool;

namespace Rivet.Tests;

/// <summary>
/// Public-CLI regression evidence for outcome:cli-checks — real process exit codes
/// and file effects, never merely parsed options:
/// - --routes combined with --verify is rejected before either mode can report success.
/// - A valid normal verification passes; a stale one fails without touching the file.
/// - A failed --check exits nonzero with and without --output, including the
///   supported --routes combination; successful file writing cannot clear the failure.
/// - Valid coverage exits 0 in the corresponding modes.
/// - --help/-h prints usage and exits 0; no arguments is a usage error.
/// </summary>
public sealed class CliExitCodeTests
{
    private const string CoveredSource = """
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record ItemDto(string Id);

        [RivetContract]
        public static class ItemsContract
        {
            public static readonly RouteDefinition<ItemDto> GetItems =
                Define.Get<ItemDto>("/api/items");
        }

        [ApiController]
        [Route("api")]
        public sealed class ItemsController : ControllerBase
        {
            [HttpGet("items")]
            public IActionResult Get()
                => ItemsContract.GetItems.Success(new ItemDto("1")).ToActionResult();
        }
        """;

    /// <summary>The handler route does not match the contract's declared route.</summary>
    private const string RouteMismatchSource = """
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record ItemDto(string Id);

        [RivetContract]
        public static class ItemsContract
        {
            public static readonly RouteDefinition<ItemDto> GetItems =
                Define.Get<ItemDto>("/api/items");
        }

        [ApiController]
        [Route("api")]
        public sealed class ItemsController : ControllerBase
        {
            [HttpGet("other-items")]
            public IActionResult Get()
                => ItemsContract.GetItems.Success(new ItemDto("1")).ToActionResult();
        }
        """;

    private static async Task<string> WriteSourceAsync(TempDir work, string name, string source)
    {
        var sourcePath = Path.Combine(work.FullName, name);
        await File.WriteAllTextAsync(sourcePath, source);
        return sourcePath;
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--project", "Api.csproj", "--help")]
    [InlineData("--tool-version", "--help")]
    public void Help_Prints_Usage_To_Stdout_And_Exits_Zero(params string[] args)
    {
        using var work = new TempDir();

        var result = CliRunner.RunCli(work.FullName, args);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StdOut);
        Assert.Equal("", result.StdErr);
    }

    // rivet-ts runs --tool-version to check a binary it didn't download itself, so it prints
    // the bare release to stdout and exits 0 whatever else is on the command line.
    [Theory]
    [InlineData("--tool-version")]
    [InlineData("--tool-version", "--bogus")]
    [InlineData("--from", "missing.json", "--tool-version")]
    public void Tool_Version_Prints_The_Release_To_Stdout_And_Exits_Zero(params string[] args)
    {
        using var work = new TempDir();

        var result = CliRunner.RunCli(work.FullName, args);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CliParser.ToolVersion, result.StdOut.TrimEnd('\r', '\n'));
        Assert.Equal("", result.StdErr);
    }

    [Fact]
    public void No_Arguments_Prints_Usage_And_Exits_One()
    {
        using var work = new TempDir();

        var result = CliRunner.RunCli(work.FullName, []);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Usage:", result.StdErr);
    }

    [Fact]
    public async Task Routes_Combined_With_Verify_Is_Rejected_Before_Either_Mode_Succeeds()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", CoveredSource);
        var outputDir = Path.Combine(work.FullName, "output");

        var result = CliRunner.RunCli(
            work.FullName,
            [sourcePath, "--routes", "--verify", "--output", outputDir]
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--routes", result.StdErr);
        Assert.Contains("--verify", result.StdErr);
        Assert.Contains("cannot be combined", result.StdErr);
        // Neither mode may treat the invocation as successful: no route listing,
        // no spec written.
        Assert.DoesNotContain("route(s)", result.StdOut);
        Assert.DoesNotContain("route(s)", result.StdErr);
        Assert.False(Directory.Exists(outputDir), "no output directory should be created");
    }

    [Fact]
    public async Task Valid_Normal_Verification_Passes_Without_Rewriting_The_Spec()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", CoveredSource);
        var outputDir = Path.Combine(work.FullName, "output");

        var emit = CliRunner.RunCli(work.FullName, [sourcePath, "--output", outputDir]);
        Assert.Equal(0, emit.ExitCode);

        var specPath = Path.Combine(outputDir, "openapi.json");
        var committed = await File.ReadAllTextAsync(specPath);

        var verify = CliRunner.RunCli(
            work.FullName,
            [sourcePath, "--verify", "--output", outputDir]
        );

        Assert.Equal(0, verify.ExitCode);
        Assert.Contains("Spec is up to date", verify.StdOut);
        // --verify never writes.
        Assert.Equal(committed, await File.ReadAllTextAsync(specPath));
    }

    [Fact]
    public async Task Stale_Normal_Verification_Fails_Without_Touching_The_Committed_File()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", CoveredSource);
        var outputDir = Path.Combine(work.FullName, "output");

        var emit = CliRunner.RunCli(work.FullName, [sourcePath, "--output", outputDir]);
        Assert.Equal(0, emit.ExitCode);

        var specPath = Path.Combine(outputDir, "openapi.json");
        var committed = await File.ReadAllTextAsync(specPath);

        // Edit the source so the would-be spec no longer matches the committed one.
        var driftedSource = CoveredSource.Replace(
            "/api/items",
            "/api/renamed-items",
            StringComparison.Ordinal
        );
        Assert.NotEqual(CoveredSource, driftedSource);
        await File.WriteAllTextAsync(sourcePath, driftedSource);

        var verify = CliRunner.RunCli(
            work.FullName,
            [sourcePath, "--verify", "--output", outputDir]
        );

        Assert.Equal(1, verify.ExitCode);
        Assert.Contains("error RIV2020: ", verify.StdErr);
        Assert.Contains("is stale", verify.StdErr);
        // The committed spec is left untouched by the failed verification.
        Assert.Equal(committed, await File.ReadAllTextAsync(specPath));
    }

    [Fact]
    public async Task Verification_Without_A_Committed_Spec_Fails_Without_Writing_One()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", CoveredSource);
        var outputDir = Path.Combine(work.FullName, "output");

        var verify = CliRunner.RunCli(
            work.FullName,
            [sourcePath, "--verify", "--output", outputDir]
        );

        Assert.Equal(1, verify.ExitCode);
        Assert.Contains("error RIV2020: ", verify.StdErr);
        Assert.Contains("does not exist", verify.StdErr);
        Assert.False(File.Exists(Path.Combine(outputDir, "openapi.json")));
    }

    [Theory]
    [InlineData("--from", "contract.json")]
    [InlineData("--from-openapi", "openapi.json")]
    [InlineData("", "Api.cs")]
    [InlineData("--project", "Api.csproj")]
    public void Missing_Input_Path_Exits_1_Naming_The_Flag_And_The_Path(
        string flag,
        string fileName
    )
    {
        using var work = new TempDir();
        var missingPath = Path.Combine(work.FullName, fileName);
        var outputDir = Path.Combine(work.FullName, "output");

        var result = CliRunner.RunCli(
            work.FullName,
            [.. flag == "" ? new[] { missingPath } : [flag, missingPath], "--output", outputDir]
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            $"error RIV2021: {(flag == "" ? "source file" : flag)}: no file at {missingPath}",
            result.StdErr
        );
        Assert.DoesNotContain("   at ", result.StdErr);
        Assert.False(Directory.Exists(outputDir), "nothing should be written");
    }

    // MSBuildWorkspace opens only a project file of a language it has loaded, so a solution,
    // another language's project, or a stray file is C# that can't be loaded.
    [Theory]
    [InlineData("Api.sln")]
    [InlineData("Api.fsproj")]
    [InlineData("notes.txt")]
    public async Task Project_MSBuild_Cannot_Open_Exits_1_Without_Stack_Trace(string fileName)
    {
        using var work = new TempDir();
        var projectPath = await WriteSourceAsync(work, fileName, "<Project />");
        var outputDir = Path.Combine(work.FullName, "output");

        var result = CliRunner.RunCli(
            work.FullName,
            ["--project", projectPath, "--output", outputDir]
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            $"error RIV1126: --project: MSBuild can't open {projectPath}",
            result.StdErr
        );
        Assert.DoesNotContain("   at ", result.StdErr);
        Assert.False(Directory.Exists(outputDir), "nothing should be written");
    }

    [Fact]
    public async Task Source_That_Does_Not_Compile_Exits_1_Listing_The_Compiler_Errors()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(
            work,
            "Api.cs",
            CoveredSource + "\npublic sealed class Broken { private int _x = ; }\n"
        );
        var outputDir = Path.Combine(work.FullName, "output");

        var result = CliRunner.RunCli(work.FullName, [sourcePath, "--output", outputDir]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error RIV1126: ", result.StdErr);
        Assert.Contains("error CS1525", result.StdErr);
        Assert.DoesNotContain("   at ", result.StdErr);
        Assert.False(Directory.Exists(outputDir), "nothing should be written");
    }

    [Fact]
    public async Task Failed_Check_Without_Output_Exits_Nonzero()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", RouteMismatchSource);
        var result = CliRunner.RunCli(work.FullName, [sourcePath, "--check"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("warning RIV4003:", result.StdErr);
        Assert.Contains("[RouteMismatch]", result.StdErr);
        Assert.Contains("Coverage:", result.StdErr);
    }

    [Fact]
    public async Task Failed_Check_With_Output_Exits_Nonzero_And_Writes_Nothing()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", RouteMismatchSource);
        var outputDir = Path.Combine(work.FullName, "output");
        Directory.CreateDirectory(outputDir);

        var result = CliRunner.RunCli(
            work.FullName,
            [sourcePath, "--check", "--output", outputDir]
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[RouteMismatch]", result.StdErr);
        // A successful write must not clear the coverage failure: nothing is
        // emitted and the output directory stays empty.
        Assert.Empty(Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Failed_Check_With_Routes_Exits_Nonzero_And_Prints_No_Routes()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", RouteMismatchSource);
        var result = CliRunner.RunCli(work.FullName, [sourcePath, "--check", "--routes"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[RouteMismatch]", result.StdErr);
        // The route listing must not run: the check failed and the process exits
        // before RoutePrinter prints anything.
        Assert.DoesNotContain("route(s)", result.StdOut);
        Assert.DoesNotContain("route(s)", result.StdErr);
    }

    [Fact]
    public async Task Valid_Check_Exits_Zero_With_And_Without_Output()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", CoveredSource);
        var withoutOutput = CliRunner.RunCli(work.FullName, [sourcePath, "--check"]);
        Assert.Equal(0, withoutOutput.ExitCode);
        Assert.Contains("All OK.", withoutOutput.StdErr);

        var outputDir = Path.Combine(work.FullName, "output");
        var withOutput = CliRunner.RunCli(
            work.FullName,
            [sourcePath, "--check", "--output", outputDir]
        );
        Assert.Equal(0, withOutput.ExitCode);
        Assert.Contains("All OK.", withOutput.StdErr);
        Assert.True(
            File.Exists(Path.Combine(outputDir, "openapi.json")),
            "valid coverage in --output mode should proceed to emission"
        );
    }

    // --security replaces the source's security for --check as it does for emission: the
    // apiKey header it names is the credential, not an undeclared input, and the authorized
    // controller agrees with the secured contract (RIV4008).
    [Fact]
    public async Task Check_Reads_Security_From_The_Command_Line()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<ItemDto> GetItems =
                    Define.Get<ItemDto>("/api/items");
            }

            [Microsoft.AspNetCore.Authorization.Authorize]
            [ApiController]
            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpGet("items")]
                public IActionResult Get([FromHeader(Name = "X-Api-Key")] string key)
                    => ItemsContract.GetItems.Success(new ItemDto(key)).ToActionResult();
            }
            """;
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", source);

        var withoutSecurity = CliRunner.RunCli(work.FullName, [sourcePath, "--check"]);
        Assert.Equal(1, withoutSecurity.ExitCode);
        Assert.Contains("error RIV4006:", withoutSecurity.StdErr);

        var withSecurity = CliRunner.RunCli(
            work.FullName,
            [sourcePath, "--check", "--security", "apikey:header:X-Api-Key"]
        );
        Assert.Equal(0, withSecurity.ExitCode);
        Assert.Contains("All OK.", withSecurity.StdErr);
    }

    [Theory]
    [InlineData("oauth2", "error RIV2018: invalid --security value")]
    // Enum.TryParse would read a number as a location ("1" is header) and write "in": "7".
    [InlineData("apikey:7:X-Key", "error RIV2018: invalid --security value")]
    [InlineData("apikey:1:X-Key", "error RIV2018: invalid --security value")]
    [InlineData("admin=bearer,admin=cookie:sid", "error RIV2011: duplicate --security scheme name")]
    public async Task Invalid_Security_Exits_1_Without_Stack_Trace(
        string securitySchemes,
        string expectedError
    )
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", CoveredSource);
        var result = CliRunner.RunCli(
            work.FullName,
            [
                sourcePath,
                .. securitySchemes.Split(',').SelectMany(scheme => new[] { "--security", scheme }),
            ]
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedError, result.StdErr);
        Assert.DoesNotContain("   at ", result.StdErr);
    }

    // The import-side refusal is the emit-side one (RIV2018), not a usage error: it prints
    // its one line, without the usage text after it.
    [Fact]
    public async Task Invalid_Import_Security_Exits_1_Without_Usage()
    {
        using var work = new TempDir();
        var specPath = await WriteSourceAsync(
            work,
            "openapi.json",
            """{ "openapi": "3.1.0", "info": { "title": "t", "version": "1" }, "paths": {} }"""
        );
        var outputDir = Path.Combine(work.FullName, "output");

        var result = CliRunner.RunCli(
            work.FullName,
            ["--from-openapi", specPath, "--security", "admin=bearer", "--output", outputDir]
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error RIV2018: ", result.StdErr);
        Assert.DoesNotContain("Usage", result.StdErr);
        Assert.DoesNotContain("Usage", result.StdOut);
        Assert.DoesNotContain("   at ", result.StdErr);
        Assert.False(Directory.Exists(outputDir), "nothing should be written");
    }

    [Fact]
    public async Task Valid_Check_With_Routes_Prints_Routes_And_Exits_Zero()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", CoveredSource);
        var result = CliRunner.RunCli(work.FullName, [sourcePath, "--check", "--routes"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("All OK.", result.StdErr);
        Assert.Contains("1 route(s).", result.StdErr);
        Assert.Contains("/api/items", result.StdOut);
    }

    private const string UnroutableMvcSource = """
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record ItemDto(string Id);

        [RivetContract]
        public static class ItemsContract
        {
            public static readonly RouteDefinition<ItemDto> GetItems =
                Define.Get<ItemDto>("/api/items");
        }

        // [HttpGet] with no template on a controller without [Route]: the route
        // template cannot be statically resolved, so the requested coverage check
        // must report the implementation unresolved and exit nonzero — the
        // unroutable action cannot disappear into an All OK result.
        public sealed class ItemsController : ControllerBase
        {
            [HttpGet]
            public IActionResult Get() =>
                ItemsContract.GetItems.Success(new ItemDto("1")).ToActionResult();
        }
        """;

    [Fact]
    public async Task Failed_Check_On_Unroutable_Mvc_Action_Exits_Nonzero_Without_Output()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", UnroutableMvcSource);
        var result = CliRunner.RunCli(work.FullName, [sourcePath, "--check"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("warning RIV4003:", result.StdErr);
        Assert.Contains("[RouteMismatch]", result.StdErr);
        // The unresolved cause is stated, not a fabricated resolved route.
        Assert.Contains("unresolved route", result.StdErr);
        Assert.Contains("Coverage:", result.StdErr);
    }

    [Fact]
    public async Task Failed_Check_On_Unroutable_Mvc_Action_Exits_Nonzero_With_Output()
    {
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", UnroutableMvcSource);
        var outputDir = Path.Combine(work.FullName, "output");
        Directory.CreateDirectory(outputDir);

        var result = CliRunner.RunCli(
            work.FullName,
            [sourcePath, "--check", "--output", outputDir]
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[RouteMismatch]", result.StdErr);
        Assert.Contains("unresolved route", result.StdErr);
        // A successful write must not clear the coverage failure: nothing is
        // emitted and the output directory stays empty.
        Assert.Empty(Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Form_Endpoint_With_Non_Form_Content_Type_Exits_1_Without_Stack_Trace()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed class ItemsController : ControllerBase
            {
                [Consumes("text/plain"), RivetEndpoint, HttpPost("/items")]
                public string Post([FromForm] string title) => "ok";
            }
            """;
        using var work = new TempDir();
        var sourcePath = await WriteSourceAsync(work, "Api.cs", source);
        var result = CliRunner.RunCli(work.FullName, [sourcePath]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("RIV1100", result.StdErr);
        Assert.DoesNotContain("Unhandled exception", result.StdErr);
        Assert.DoesNotContain("   at ", result.StdErr);
    }

    [Fact]
    public async Task Malformed_Contract_Json_Exits_1_Without_Stack_Trace()
    {
        using var work = new TempDir();
        var contractPath = await WriteSourceAsync(work, "contract.json", "{");
        var result = CliRunner.RunCli(
            work.FullName,
            ["--from", contractPath, "--output", Path.Combine(work.FullName, "out")]
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("contract.json", result.StdErr);
        Assert.DoesNotContain("   at ", result.StdErr);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{ "openapi": "3.1.0", "info": { "version": "1" }, "paths": {} }""")]
    public async Task Malformed_OpenApi_Spec_Exits_1_Without_Stack_Trace(string spec)
    {
        using var work = new TempDir();
        var specPath = await WriteSourceAsync(work, "openapi.json", spec);
        var result = CliRunner.RunCli(
            work.FullName,
            ["--from-openapi", specPath, "--output", Path.Combine(work.FullName, "out")]
        );

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error", result.StdErr);
        Assert.DoesNotContain("   at ", result.StdErr);
    }
}
