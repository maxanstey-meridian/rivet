using System.Diagnostics;
using System.Text.Json.Nodes;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Xunit.Abstractions;

namespace Rivet.Tests;

/// <summary>
/// The conformance gate (FABLE_REWRITE.md "The conformance gate", FABLE_TEST_FIXES.md SYS-2).
///
/// For every contract fixture in the corpus, the emitted OpenAPI must:
///   1. Lint clean — <c>spectral lint</c> (spectral:oas ruleset) with zero errors
///      (warnings are reported in test output but do not fail);
///   2. Be consumable — <c>openapi-typescript</c> over the spec, then
///      <c>tsc --strict</c> over its output, zero errors;
///   3. Self-loop stably — emit → import → emit is idempotent
///      (check 3's semantic-fidelity half lives in OpenApiRoundTripTests; this
///      adds loop stability across the whole corpus).
///
/// Check 4 (foreign-spec importer stability) lives in RealWorldImportTests.
///
/// Tooling is vendored in Rivet.Tests/js (see its README) — runs offline once
/// <c>npm install</c> has been done there.
///
/// Phase 0 marked failing rows <c>Skip="CONFORMANCE-GAP: …"</c> (catalogued in
/// FABLE_PHASE0.md). As of Phase 1 (WP-1.1) all gaps are fixed and every row runs
/// un-skipped — any future failure here is a regression, not a known gap.
/// </summary>
public sealed class OpenApiConformanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"rivet-conformance-{Guid.NewGuid():N}");

    public OpenApiConformanceTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ════════════════════════════════ Corpus ════════════════════════════════
    //
    // Every distinct contract-fixture source the suite already exercises for
    // OpenAPI emission, plus the ContractApi sample project and the contract-JSON
    // fixtures. One name → one emitted spec.

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string JsDir = Path.Combine(RepoRoot, "Rivet.Tests", "js");

    // Conformance fixtures are emitted the way a real deployment would invoke the
    // tool: --title/--version/--server set. This is what lets the spectral gate
    // assert oas3-api-servers absent (see DeniedWarningCodes).
    private static readonly OpenApiDocumentInfo FixtureDocumentInfo = new(
        "Rivet Conformance Fixture", "1.2.3", ["https://api.example.com"]);

    public static string EmitSpec(string fixtureName) => fixtureName switch
    {
        "maximal-contract" => EmitFromSources([MaximalContractSource], "bearer"),
        "controller-annotations" => EmitFromSources([ControllerAnnotationsSource], null),
        "typed-results" => EmitFromSources([TypedResultsSource], null),
        "mixed-contracts-controllers" => EmitFromSources([MixedContractsControllersSource], null),
        "file-endpoints-query-auth" => EmitFromSources([FileEndpointsQueryAuthSource], "bearer"),
        "validation-metadata" => EmitFromSources([ValidationMetadataSource], null),
        "polymorphic-shapes" => EmitFromSources([PolymorphicShapesSource], "bearer"),
        "header-contracts" => EmitFromSources([HeaderContractsSource], "bearer"),
        "contractapi-sample" => EmitFromSources(LoadContractApiSampleSources(), "bearer"),
        "contract-sample-json" => CompilationHelper.EmitOpenApiFromJson(LoadFixture("contract-sample.json"), FixtureDocumentInfo),
        "contract-tagged-union-json" => CompilationHelper.EmitOpenApiFromJson(LoadFixture("contract-tagged-union.json"), FixtureDocumentInfo),
        "php-golden-contract-json" => CompilationHelper.EmitOpenApiFromJson(LoadFixture("php-golden-contract.json"), FixtureDocumentInfo),
        // TS-lowerer-shaped contract JSON: brands appear only as inline kind:"brand"
        // nodes, and multipart inputs are decomposed into params with an inputTypeName
        // that has NO matching entry in types[] (mirrors rivet-ts output; BUG-1/BUG-2).
        "contract-ts-brands-json" => CompilationHelper.EmitOpenApiFromJson(LoadFixture("contract-ts-brands.json"), FixtureDocumentInfo),
        "contract-ts-multipart-json" => CompilationHelper.EmitOpenApiFromJson(LoadFixture("contract-ts-multipart.json"), FixtureDocumentInfo),
        _ => throw new ArgumentException($"Unknown conformance fixture '{fixtureName}'"),
    };

    private static string EmitFromSources(string[] sources, string? security)
    {
        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contractEndpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var annotationEndpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var merged = EndpointMerger.Merge(contractEndpoints, annotationEndpoints);
        var securityConfig = security is null ? null : SecurityParser.Parse(security);
        return OpenApiEmitter.Emit(merged, walker.Definitions, walker.Brands, walker.Enums, securityConfig, FixtureDocumentInfo);
    }

    private static string LoadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string[] LoadContractApiSampleSources()
    {
        var sampleDir = Path.Combine(RepoRoot, "samples", "ContractApi");
        // Contracts + Models + Domain are the contract surface; Program.cs and
        // Controllers pull in the full ASP.NET hosting model the in-memory
        // compilation doesn't reference (and contribute no endpoints).
        // The sample project builds with ImplicitUsings; the in-memory
        // compilation does not, so supply the equivalent global usings.
        const string implicitUsings = """
            global using System;
            global using System.Collections.Generic;
            global using System.Linq;
            global using System.Threading;
            global using System.Threading.Tasks;
            """;

        return new[] { "Contracts", "Models", "Domain" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(sampleDir, d), "*.cs"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .Prepend(implicitUsings)
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rivet.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Rivet.slnx)");
    }

    private string WriteSpec(string fixtureName)
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, $"{fixtureName}.openapi.json");
        File.WriteAllText(path, EmitSpec(fixtureName));

        // Triage aid: RIVET_CONFORMANCE_DUMP=<dir> keeps a copy of every emitted spec.
        var dumpDir = Environment.GetEnvironmentVariable("RIVET_CONFORMANCE_DUMP");
        if (!string.IsNullOrEmpty(dumpDir))
        {
            Directory.CreateDirectory(dumpDir);
            File.Copy(path, Path.Combine(dumpDir, Path.GetFileName(path)), overwrite: true);
        }

        return path;
    }

    // ═══════════════════════ Check 1 — spectral lint ════════════════════════

    // Warning codes the --title/--version/--server flags make fixable, promoted to
    // asserted-absent now that the corpus is emitted with those flags (a named-codes
    // denylist, NOT fail-on-all-warnings). Only oas3-api-servers qualifies:
    // info.title/info.version are schema-required (missing ones are already
    // severity-error), and info-contact / info-description need contact/description
    // data no flag provides — those stay reported-but-not-failing.
    private static readonly string[] DeniedWarningCodes = ["oas3-api-servers"];

    [Theory]
    [InlineData("maximal-contract")]
    [InlineData("controller-annotations")]
    [InlineData("typed-results")]
    [InlineData("mixed-contracts-controllers")]
    [InlineData("file-endpoints-query-auth")]
    [InlineData("validation-metadata")]
    [InlineData("polymorphic-shapes")]
    [InlineData("header-contracts")]
    [InlineData("contractapi-sample")]
    [InlineData("contract-sample-json")]
    [InlineData("contract-tagged-union-json")]
    [InlineData("php-golden-contract-json")]
    [InlineData("contract-ts-brands-json")]
    [InlineData("contract-ts-multipart-json")]
    public void Spectral_Lint_Has_Zero_Errors(string fixtureName)
    {
        var specPath = WriteSpec(fixtureName);
        var ruleset = Path.Combine(JsDir, ".spectral.yaml");
        var spectral = Path.Combine(JsDir, "node_modules", ".bin", "spectral");

        var (exitCode, stdout, stderr) = RunNode(
            spectral, ["lint", "--ruleset", ruleset, "--format", "json", specPath]);

        // --format json prints a findings array on stdout regardless of exit code.
        JsonArray findings;
        try
        {
            findings = JsonNode.Parse(string.IsNullOrWhiteSpace(stdout) ? "[]" : stdout)!.AsArray();
        }
        catch (Exception)
        {
            Assert.Fail($"spectral did not produce JSON output (exit {exitCode}):\n{stdout}\n{stderr}");
            return;
        }

        static string Describe(JsonNode? f) =>
            $"{f?["code"]} at {string.Join('.', f?["path"]?.AsArray().Select(p => p?.ToString()) ?? [])}: {f?["message"]}";

        // Severity 0 = error (the gate); 1+ = warning/info/hint (reported, not
        // failing) — except the denylisted codes, which fail at any severity.
        var errors = findings
            .Where(f => f?["severity"]?.GetValue<int>() == 0
                || DeniedWarningCodes.Contains(f?["code"]?.GetValue<string>()))
            .ToList();
        var warnings = findings.Except(errors).ToList();

        foreach (var warning in warnings)
        {
            _output.WriteLine($"spectral warning [{fixtureName}]: {Describe(warning)}");
        }

        Assert.True(errors.Count == 0,
            $"spectral found {errors.Count} error(s) in '{fixtureName}':\n"
            + string.Join("\n", errors.Select(Describe)));
    }

    // ════════ Check 2 — openapi-typescript + tsc --strict consumption ═══════

    [Theory]
    [InlineData("maximal-contract")]
    [InlineData("controller-annotations")]
    [InlineData("typed-results")]
    [InlineData("mixed-contracts-controllers")]
    [InlineData("file-endpoints-query-auth")]
    [InlineData("validation-metadata")]
    [InlineData("polymorphic-shapes")]
    [InlineData("header-contracts")]
    [InlineData("contractapi-sample")]
    [InlineData("contract-sample-json")]
    [InlineData("contract-tagged-union-json")]
    [InlineData("php-golden-contract-json")]
    [InlineData("contract-ts-brands-json")]
    [InlineData("contract-ts-multipart-json")]
    public void OpenApiTypescript_Output_Compiles_Under_TscStrict(string fixtureName)
    {
        var specPath = WriteSpec(fixtureName);
        var typesPath = Path.Combine(_tempDir, $"{fixtureName}.ts");

        var openapiTs = Path.Combine(JsDir, "node_modules", ".bin", "openapi-typescript");
        var (genExit, genOut, genErr) = RunNode(openapiTs, [specPath, "-o", typesPath]);
        Assert.True(genExit == 0 && File.Exists(typesPath),
            $"openapi-typescript failed for '{fixtureName}' (exit {genExit}):\n{genOut}\n{genErr}");

        var tsc = Path.Combine(JsDir, "node_modules", ".bin", "tsc");
        var (tscExit, tscOut, tscErr) = RunNode(tsc,
        [
            "--noEmit", "--strict",
            "--target", "es2022", "--module", "es2022",
            "--moduleResolution", "bundler", "--skipLibCheck",
            typesPath,
        ]);

        Assert.True(tscExit == 0,
            $"tsc --strict rejected openapi-typescript output for '{fixtureName}':\n{tscOut}\n{tscErr}");
    }

    // ═════════════════ Check 3 — self-loop emit/import stability ════════════
    //
    // Semantic first-loop fidelity (import(emit(contract)) ≡ contract) is pinned
    // per-construct by OpenApiRoundTripTests (incl. the maximal contract). This
    // theory extends loop coverage across the conformance corpus: after one
    // normalizing hop, emit∘import must be a fixed point (json1 ≡ json2).
    // Contract-JSON fixtures are excluded: they enter via JsonContractReader,
    // not via the importer, so the loop's first emit is already the same path.

    [Theory]
    [InlineData("maximal-contract")]
    [InlineData("controller-annotations")]
    [InlineData("typed-results")]
    [InlineData("mixed-contracts-controllers")]
    [InlineData("file-endpoints-query-auth")]
    [InlineData("validation-metadata")]
    [InlineData("polymorphic-shapes")]
    [InlineData("header-contracts")]
    [InlineData("contractapi-sample")]
    public void SelfLoop_Emit_Import_Emit_Is_Stable(string fixtureName)
    {
        const string security = "bearer";

        var json0 = EmitSpec(fixtureName);
        var json1 = ReEmitThroughImporter(json0, security);
        var json2 = ReEmitThroughImporter(json1, security);

        var node1 = JsonNode.Parse(json1);
        var node2 = JsonNode.Parse(json2);

        Assert.True(JsonNode.DeepEquals(node1, node2),
            $"emit∘import is not a fixed point for '{fixtureName}'.\n"
            + $"--- after one import ---\n{json1}\n--- after two imports ---\n{json2}");
    }

    private static string ReEmitThroughImporter(string openApiJson, string security)
    {
        var result = CompilationHelper.Import(openApiJson, ns: "ConformanceLoop", security);
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        return OpenApiEmitter.Emit(
            endpoints, walker.Definitions, walker.Brands, walker.Enums, SecurityParser.Parse(security));
    }

    // ═══════════════════════════ Process plumbing ═══════════════════════════

    private static (int ExitCode, string StdOut, string StdErr) RunNode(string script, string[] args)
    {
        if (!File.Exists(script))
        {
            throw new InvalidOperationException(
                $"Node tool not found: {script}. Run 'npm install' in {JsDir} (see its README).");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = JsDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(script);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start node {script}");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }

    // ════════════════════════════ Fixture sources ═══════════════════════════

    /// <summary>
    /// The maximal contract — same source as
    /// OpenApiRoundTripTests.MaximalContract_DoublRoundTrip_IsLossless: every
    /// primitive/nullable/collection/brand/enum/generic/nested shape, all five
    /// HTTP verbs, multi-response, file upload, security overrides.
    /// </summary>
    private const string MaximalContractSource = """
        using System;
        using System.Collections.Generic;
        using Microsoft.AspNetCore.Http;
        using Rivet;

        namespace Test;

        public enum Priority { Low, Medium, High, Critical }

        [RivetType]
        public sealed record Email(string Value);

        [RivetType]
        public sealed record Uprn(string Value);

        [RivetType]
        public sealed record Quantity(int Value);

        [RivetType]
        public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

        [RivetType]
        public sealed record KitchenSinkDto(
            string Name,
            int IntVal,
            uint UintVal,
            long LongVal,
            ulong UlongVal,
            short ShortVal,
            ushort UshortVal,
            byte ByteVal,
            sbyte SbyteVal,
            float FloatVal,
            double DoubleVal,
            decimal DecimalVal,
            bool BoolVal,
            DateTime DateTimeVal,
            DateTimeOffset DateTimeOffsetVal,
            DateOnly DateOnlyVal,
            Guid GuidVal,
            string? NullableString,
            int? NullableInt,
            bool? NullableBool,
            Guid? NullableGuid,
            DateTime? NullableDateTime,
            DateTimeOffset? NullableDateTimeOffset,
            List<string> Tags,
            List<int> Scores,
            Dictionary<string, string> Metadata,
            Dictionary<string, int> Counts,
            Dictionary<Priority, int> PriorityTallies,
            List<Guid> IdList,
            Email AuthorEmail,
            Quantity ItemQuantity,
            Priority CurrentPriority,
            AddressDto HomeAddress,
            AddressDto? WorkAddress,
            [property: Obsolete] string LegacyField);

        [RivetType]
        public sealed record AddressDto(string Line1, string? Line2, string City, string PostCode);

        [RivetType]
        public sealed record NotFoundError(string Message);

        [RivetType]
        public sealed record ValidationError(string Message, Dictionary<string, string> Errors);

        [RivetType]
        public sealed record CreateItemInput(
            string Name, Email AuthorEmail, Priority CurrentPriority, AddressDto HomeAddress);

        [RivetType]
        public sealed record SearchInput(string Query, int Limit, int? Offset);

        [RivetType]
        public sealed record UploadInput(IFormFile Document, string Title, int? PageCount);

        [RivetType]
        public sealed record UploadResult(string Url, Guid FileId);

        [RivetType]
        public sealed record UserDto(string Id, string Name, Email Email, Uprn? Uprn);

        [RivetContract]
        public static class ItemsContract
        {
            public static readonly Define GetItem =
                Define.Get<KitchenSinkDto>("/api/items/{id}")
                    .Description("Retrieve a single item by its unique ID");

            public static readonly Define SearchItems =
                Define.Get<SearchInput, PagedResult<KitchenSinkDto>>("/api/items");

            public static readonly Define CreateItem =
                Define.Post<CreateItemInput, KitchenSinkDto>("/api/items")
                    .Status(201)
                    .Returns<ValidationError>(422, "Validation failed");

            public static readonly Define UpdateItem =
                Define.Put<CreateItemInput, KitchenSinkDto>("/api/items/{id}");

            public static readonly Define DeleteItem =
                Define.Delete("/api/items/{id}")
                    .Status(204)
                    .Returns<NotFoundError>(404, "Item not found");

            public static readonly Define PatchItem =
                Define.Patch<CreateItemInput>("/api/items/{id}")
                    .Status(204);
        }

        [RivetContract]
        public static class UsersContract
        {
            public static readonly Define ListUsers =
                Define.Get<PagedResult<UserDto>>("/api/users")
                    .Description("List all users with pagination");

            public static readonly Define GetUser =
                Define.Get<UserDto>("/api/users/{userId}")
                    .Returns<NotFoundError>(404, "User not found");
        }

        [RivetContract]
        public static class FilesContract
        {
            public static readonly Define Upload =
                Define.Post<UploadInput, UploadResult>("/api/files")
                    .Status(201);
        }

        [RivetContract]
        public static class HealthContract
        {
            public static readonly Define Check =
                Define.Get("/api/health")
                    .Anonymous()
                    .Description("Health check endpoint");
        }

        [RivetContract]
        public static class AdminContract
        {
            public static readonly Define Purge =
                Define.Delete("/api/admin/cache")
                    .Status(204)
                    .Secure("admin");
        }
        """;

    /// <summary>
    /// Annotation-driven controllers — same source as
    /// TypeScriptCompilationTests.GeneratedOutput_PassesTscNoEmit (generics,
    /// brands, enums, IFormFile upload, ProducesResponseType).
    /// </summary>
    private const string ControllerAnnotationsSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace MyApp.Domain
        {
            public enum Priority { Low, Medium, High, Critical }
            public enum WorkItemStatus { Open, InProgress, Done }
            public sealed record Email(string Value);
            public sealed record TaskId(Guid Value);

            [RivetType]
            public sealed record Label(string Name, string Color);
        }

        namespace MyApp.Contracts
        {
            using MyApp.Domain;

            public sealed record CreateTaskCommand(string Title, Priority Priority, Email Author);
            public sealed record CreateTaskResult(Guid Id, DateTime CreatedAt);
            public sealed record TaskListItemDto(Guid Id, string Title, Priority Priority, Email Author);
            public sealed record TaskDetailDto(Guid Id, string Title, Priority Priority, Email Author, List<Label> Labels);

            [RivetType]
            public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);
        }

        namespace MyApp.Api
        {
            using MyApp.Domain;
            using MyApp.Contracts;

            [RivetClient]
            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                [ProducesResponseType(typeof(PagedResult<TaskListItemDto>), StatusCodes.Status200OK)]
                public async Task<IActionResult> List(
                    [FromQuery] int? page,
                    [FromQuery] int? pageSize,
                    CancellationToken ct)
                    => throw new NotImplementedException();

                [HttpGet("{id:guid}")]
                public async Task<ActionResult<TaskDetailDto>> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();

                [HttpPost]
                [ProducesResponseType(typeof(CreateTaskResult), StatusCodes.Status201Created)]
                public async Task<IActionResult> Create(
                    [FromBody] CreateTaskCommand command,
                    CancellationToken ct)
                    => throw new NotImplementedException();

                [HttpDelete("{id:guid}")]
                public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();

                [HttpPost("{id:guid}/attachments")]
                [ProducesResponseType(typeof(CreateTaskResult), StatusCodes.Status201Created)]
                public async Task<IActionResult> Attach(
                    Guid id,
                    IFormFile file,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }

            [RivetClient]
            [Route("api/members")]
            public sealed class MembersController : ControllerBase
            {
                [HttpGet]
                [ProducesResponseType(typeof(List<TaskListItemDto>), StatusCodes.Status200OK)]
                public async Task<IActionResult> List(CancellationToken ct)
                    => throw new NotImplementedException();
            }
        }
        """;

    /// <summary>
    /// TypedResults endpoints — same source as
    /// TypeScriptCompilationTests.TypedResults_Endpoints_CompileTs.
    /// </summary>
    private const string TypedResultsSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Http.HttpResults;
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace MyApp.Contracts
        {
            public sealed record ItemDto(Guid Id, string Name);
            public sealed record ErrorDto(string Code, string Message);
            public sealed record CreateItemRequest(string Name);
        }

        namespace MyApp.Api
        {
            using MyApp.Contracts;

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<Results<Ok<ItemDto>, NotFound>> Get([FromRoute] Guid id)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpPost("/api/items")]
                public static Task<Results<Created<ItemDto>, Conflict<ErrorDto>>> Create(
                    [FromBody] CreateItemRequest body)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpDelete("/api/items/{id}")]
                public static Task<Results<NoContent, NotFound>> Delete([FromRoute] Guid id)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpGet("/api/items")]
                public static Task<Ok<List<ItemDto>>> List()
                    => throw new NotImplementedException();
            }
        }
        """;

    /// <summary>
    /// Contract + controller endpoints merged — same source as
    /// TypeScriptCompilationTests.ContractEndpoints_MixedWithControllers_Compile.
    /// </summary>
    private const string MixedContractsControllersSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace MyApp.Domain
        {
            public enum Priority { Low, Medium, High }
            public sealed record Email(string Value);
        }

        namespace MyApp.Contracts
        {
            using MyApp.Domain;

            public sealed record TaskDetailDto(Guid Id, string Title, Priority Priority, Email Author);
            public sealed record NotFoundDto(string Message);
            public sealed record CreateTaskCommand(string Title, Priority Priority);
            public sealed record CreateTaskResult(Guid Id, DateTime CreatedAt);

            public sealed record MemberDto(Guid Id, string Name, Email Email);
            public sealed record InviteMemberRequest(Email Email, string Role);
            public sealed record InviteMemberResponse(Guid Id);
            public sealed record ValidationErrorDto(string Message);
        }

        namespace MyApp.Api
        {
            using MyApp.Contracts;

            [RivetClient]
            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
                [ProducesResponseType(typeof(NotFoundDto), StatusCodes.Status404NotFound)]
                public async Task<IActionResult> Get(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();

                [HttpPost]
                [ProducesResponseType(typeof(CreateTaskResult), StatusCodes.Status201Created)]
                public async Task<IActionResult> Create(
                    [FromBody] CreateTaskCommand command,
                    CancellationToken ct)
                    => throw new NotImplementedException();

                [HttpDelete("{id:guid}")]
                public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
                    => throw new NotImplementedException();
            }

            [RivetContract]
            public static class MembersContract
            {
                public static readonly Define List =
                    Define.Get<MemberDto>("/api/members");

                public static readonly Define Invite =
                    Define.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
                        .Status(201)
                        .Returns<ValidationErrorDto>(422);

                public static readonly Define Remove =
                    Define.Delete("/api/members/{id}")
                        .Returns<NotFoundDto>(404);
            }
        }
        """;

    /// <summary>
    /// File endpoints with QueryAuth and byte[] outputs — merged from
    /// TypeScriptCompilationTests.FileEndpoint_WithQueryAuth_FullPipeline and
    /// FileEndpoint_ByteArray_CompilesTs.
    /// </summary>
    private const string FileEndpointsQueryAuthSource = """
        using System;
        using System.Collections.Generic;
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace MyApp.Contracts
        {
            public sealed record ErrorDto(string Code, string Message);
            public sealed record NotFoundDto(string Message);

            [RivetType]
            public sealed record StreamInput(string Id, string Quality);
        }

        namespace MyApp.Api
        {
            using MyApp.Contracts;

            [RivetContract]
            public static class StreamingContract
            {
                public static readonly FileRouteDefinition Stream =
                    Define.File("/api/streams/{id}")
                        .ContentType("video/mp4")
                        .QueryAuth()
                        .Description("Stream a video file");

                public static readonly FileRouteDefinition Preview =
                    Define.File("/api/streams/{id}/preview")
                        .ContentType("image/jpeg")
                        .QueryAuth("key")
                        .Returns<ErrorDto>(404, "Not found");

                public static readonly FileRouteDefinition<StreamInput> Media =
                    Define.File<StreamInput>("/api/media/{id}/stream")
                        .ContentType("video/mp4")
                        .QueryAuth("secret")
                        .Returns<ErrorDto>(404, "Not found")
                        .Description("Stream a media file");
            }

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<byte[]> Download =
                    Define.Get<byte[]>("/api/files/{id}")
                        .Description("Download a file")
                        .Returns<NotFoundDto>(404, "File not found");

                public static readonly RouteDefinition Preview =
                    Define.Get("/api/files/{id}/preview")
                        .ProducesFile("image/png")
                        .Returns<ErrorDto>(400, "Bad request");
            }
        }
        """;

    /// <summary>
    /// Validation/metadata attributes — distilled from MetadataAttributeTests
    /// fixtures: DataAnnotations constraints, RivetConstraints (MultipleOf,
    /// ExclusiveMinimum), descriptions/examples/defaults, read/write-only,
    /// deprecation, and a form-encoded endpoint.
    /// </summary>
    private const string ValidationMetadataSource = """
        using System;
        using System.ComponentModel.DataAnnotations;
        using Rivet;

        namespace Test;

        [RivetDescription("A product listing")]
        [RivetType]
        public sealed record ProductDto(
            [property: RivetReadOnly]
            [property: RivetDescription("Unique identifier")]
            string Id,

            [property: MinLength(1), MaxLength(200)]
            [property: RivetDescription("Product name")]
            [property: RivetExample("\"Widget Pro\"")] string Name,

            [property: RivetDefault("9.99")]
            [property: Range(0, double.MaxValue)]
            double Price,

            [property: Range(5, double.MaxValue), RivetConstraints(ExclusiveMinimum = 0)]
            double Weight,

            [property: MinLength(1), MaxLength(100), RegularExpression("^[a-z]+$")]
            string Slug,

            [property: Range(0, 999.5), RivetConstraints(MultipleOf = 0.5)]
            double Score,

            [property: RivetOptional]
            [property: RivetWriteOnly]
            string? InternalNotes,

            [property: Obsolete]
            string LegacySku,

            [property: RivetDescription("Primary product category")]
            CategoryDto Category);

        [RivetType]
        public sealed record CategoryDto(string Name);

        [RivetType]
        public sealed record LoginInput(string Username, string Password);

        [RivetType]
        public sealed record LoginResult(string Token);

        [RivetContract]
        public static class ProductContract
        {
            public static readonly RouteDefinition<ProductDto> Get =
                Define.Get<ProductDto>("/api/products/{id}");

            public static readonly Define Login =
                Define.Post<LoginInput, LoginResult>("/api/auth/login")
                    .FormEncoded();
        }
        """;

    /// <summary>
    /// P2 wave 5 — headers as contract concepts: [RivetHeader] request headers on a
    /// GET (query siblings), a POST (body sibling — the header must stay OUT of the
    /// body schema) and a route-param endpoint, plus .WithResponseHeader response
    /// headers on success and error statuses (required-on-opt-in, descriptions).
    /// </summary>
    private const string HeaderContractsSource = """
        using System;
        using Rivet;

        namespace Test;

        public sealed record ListPagesInput(
            [property: RivetHeader("Notion-Version")] string Version,
            string? Cursor,
            int? PageSize);

        [RivetType]
        public sealed record PageDto(string Id, string Title);

        [RivetType]
        public sealed record CreatePageRequest(
            [property: RivetHeader("X-Request-Id")] string RequestId,
            string Title,
            string? Icon);

        [RivetType]
        public sealed record ErrorDto(string Message);

        public sealed record GetPageInput(
            string Id,
            [property: RivetHeader("If-None-Match")] string? ETag);

        [RivetContract]
        public static class PagesContract
        {
            public static readonly Define ListPages =
                Define.Get<ListPagesInput, PageDto>("/api/pages")
                    .WithResponseHeader("X-Request-Cost", "Units consumed by this call");

            public static readonly Define CreatePage =
                Define.Post<CreatePageRequest, PageDto>("/api/pages")
                    .WithResponseHeader("Location", "URL of the created page", required: true)
                    .WithResponseHeader(201, "ETag")
                    .Returns<ErrorDto>(429, "Rate limited")
                    .WithResponseHeader(429, "Retry-After", "Seconds to wait before retrying");

            public static readonly Define GetPage =
                Define.Get<GetPageInput, PageDto>("/api/pages/{Id}")
                    .Returns(304, "Not modified");
        }
        """;

    /// <summary>
    /// P2 wave 4 — STJ polymorphism: a default-<c>$type</c> hierarchy, a
    /// custom-discriminator hierarchy, nested type references inside variants,
    /// and a derived type referenced directly (whose standalone schema stays
    /// untagged, matching System.Text.Json's wire semantics).
    /// </summary>
    private const string PolymorphicShapesSource = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json.Serialization;
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record PointDto(double X, double Y);

        [RivetType]
        [JsonPolymorphic]
        [JsonDerivedType(typeof(Circle), "circle")]
        [JsonDerivedType(typeof(Square), "square")]
        public abstract record Shape(string Id, PointDto Origin);

        public sealed record Circle(string Id, PointDto Origin, double Radius) : Shape(Id, Origin);
        public sealed record Square(string Id, PointDto Origin, double Side, string? Label) : Shape(Id, Origin);

        [RivetType]
        [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
        [JsonDerivedType(typeof(EmailChannel), "email")]
        [JsonDerivedType(typeof(SmsChannel), "sms")]
        public abstract record Channel;

        public sealed record EmailChannel(string Address) : Channel;
        public sealed record SmsChannel(string Number, string? Carrier) : Channel;

        [RivetContract]
        public static class ShapesContract
        {
            public static readonly Define GetShape =
                Define.Get<Shape>("/api/shapes/{id}");

            public static readonly Define CreateShape =
                Define.Post<Shape, Shape>("/api/shapes")
                    .Status(201);

            // Derived type referenced directly — its standalone schema stays untagged
            public static readonly Define GetCircle =
                Define.Get<Circle>("/api/circles/{id}");
        }

        [RivetContract]
        public static class ChannelsContract
        {
            public static readonly Define GetChannel =
                Define.Get<Channel>("/api/channels/{id}");
        }
        """;
}
