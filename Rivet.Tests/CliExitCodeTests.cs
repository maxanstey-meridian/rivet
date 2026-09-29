namespace Rivet.Tests;

/// <summary>
/// Public-CLI regression evidence for outcome:cli-checks — real process exit codes
/// and file effects, never merely parsed options:
/// - --routes combined with --verify is rejected before either mode can report success.
/// - A valid normal verification passes; a stale one fails without touching the file.
/// - A failed --check exits nonzero with and without --output, including the
///   supported --routes combination; successful file writing cannot clear the failure.
/// - Valid coverage exits 0 in the corresponding modes.
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
        Assert.Contains("is stale", verify.StdErr);
        // The committed spec is left untouched by the failed verification.
        Assert.Equal(committed, await File.ReadAllTextAsync(specPath));
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
}
