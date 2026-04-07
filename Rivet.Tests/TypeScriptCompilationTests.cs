using System.Diagnostics;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

/// <summary>
/// Integration tests that generate full TypeScript output and run tsc --noEmit
/// to verify all imports resolve and types are valid.
/// </summary>
public sealed class TypeScriptCompilationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"rivet-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedOutput_PassesTscNoEmit()
    {
        var source = """
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

        var (exitCode, output) = await GenerateAndTypeCheck(source);

        Assert.True(exitCode == 0, $"tsc --noEmit failed:\n{output}");
    }

    [Fact]
    public async Task ValidatorsImports_ResolveCorrectly()
    {
        // Validators reference types from multiple namespace groups —
        // this is the exact scenario that was broken when validators
        // imported from types/index.js (which only exports namespaces).
        var source = """
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

                public sealed record CreateTaskCommand(string Title, Priority Priority, Email Author);
                public sealed record CreateTaskResult(Guid Id, DateTime CreatedAt);
                public sealed record TaskDetailDto(Guid Id, string Title, Priority Priority);

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
                    [ProducesResponseType(typeof(PagedResult<TaskDetailDto>), StatusCodes.Status200OK)]
                    public async Task<IActionResult> List(CancellationToken ct)
                        => throw new NotImplementedException();

                    [HttpPost]
                    [ProducesResponseType(typeof(CreateTaskResult), StatusCodes.Status201Created)]
                    public async Task<IActionResult> Create(
                        [FromBody] CreateTaskCommand command,
                        CancellationToken ct)
                        => throw new NotImplementedException();
                }
            }
            """;

        var (exitCode, output) = await GenerateAndTypeCheck(source);

        Assert.True(exitCode == 0, $"tsc --noEmit failed:\n{output}");
    }

    [Fact]
    public async Task MultiResponse_OverloadsCompile()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace MyApp.Contracts
            {
                public sealed record TaskDetailDto(Guid Id, string Title);
                public sealed record NotFoundDto(string Message);
                public sealed record ValidationErrorDto(string[] Errors);
                public sealed record CreateTaskCommand(string Title);
                public sealed record CreateTaskResult(Guid Id);
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
                    [ProducesResponseType(typeof(ValidationErrorDto), StatusCodes.Status400BadRequest)]
                    [ProducesResponseType(typeof(NotFoundDto), StatusCodes.Status404NotFound)]
                    public async Task<IActionResult> Create(
                        [FromBody] CreateTaskCommand command,
                        CancellationToken ct)
                        => throw new NotImplementedException();

                    [HttpGet]
                    [ProducesResponseType(typeof(List<TaskDetailDto>), StatusCodes.Status200OK)]
                    public async Task<IActionResult> List(CancellationToken ct)
                        => throw new NotImplementedException();

                    [HttpDelete("{id:guid}")]
                    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
                        => throw new NotImplementedException();
                }
            }
            """;

        var (exitCode, output) = await GenerateAndTypeCheck(source);

        Assert.True(exitCode == 0, $"tsc --noEmit failed:\n{output}");
    }

    [Fact]
    public async Task ContractEndpoints_MixedWithControllers_Compile()
    {
        var source = """
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

                // Controller-sourced endpoints (attribute-based)
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

                // Contract-sourced endpoints
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

        var (exitCode, output) = await GenerateAndTypeCheck(source);

        Assert.True(exitCode == 0, $"tsc --noEmit failed:\n{output}");
    }

    [Fact]
    public async Task FileEndpoint_ByteArray_CompilesTs()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace MyApp.Contracts
            {
                public sealed record ErrorDto(string Code, string Message);
                public sealed record NotFoundDto(string Message);
            }

            namespace MyApp.Api
            {
                using MyApp.Contracts;

                [RivetContract]
                public static class FilesContract
                {
                    // byte[] TOutput → inferred file endpoint → Promise<Blob>
                    public static readonly RouteDefinition<byte[]> Download =
                        Define.Get<byte[]>("/api/files/{id}")
                            .Description("Download a file")
                            .Returns<NotFoundDto>(404, "File not found");

                    // Explicit .ProducesFile() on void definition
                    public static readonly RouteDefinition Preview =
                        Define.Get("/api/files/{id}/preview")
                            .ProducesFile("image/png")
                            .Returns<ErrorDto>(400, "Bad request");
                }
            }
            """;

        var (exitCode, output) = await GenerateAndTypeCheck(source);

        Assert.True(exitCode == 0, $"tsc --noEmit failed:\n{output}");
    }

    [Fact]
    public async Task TypedResults_Endpoints_CompileTs()
    {
        var source = """
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
                    // Ok<T> + NotFound → typed success + void error
                    [RivetEndpoint]
                    [HttpGet("/api/items/{id}")]
                    public static Task<Results<Ok<ItemDto>, NotFound>> Get([FromRoute] Guid id)
                        => throw new NotImplementedException();

                    // Created<T> + Conflict<T> → typed success + typed error
                    [RivetEndpoint]
                    [HttpPost("/api/items")]
                    public static Task<Results<Created<ItemDto>, Conflict<ErrorDto>>> Create(
                        [FromBody] CreateItemRequest body)
                        => throw new NotImplementedException();

                    // NoContent + NotFound → void success + void error
                    [RivetEndpoint]
                    [HttpDelete("/api/items/{id}")]
                    public static Task<Results<NoContent, NotFound>> Delete([FromRoute] Guid id)
                        => throw new NotImplementedException();

                    // Single typed result (no Results<> wrapper)
                    [RivetEndpoint]
                    [HttpGet("/api/items")]
                    public static Task<Ok<List<ItemDto>>> List()
                        => throw new NotImplementedException();
                }
            }
            """;

        var (exitCode, output) = await GenerateAndTypeCheck(source);

        Assert.True(exitCode == 0, $"tsc --noEmit failed:\n{output}");
    }

    [Fact]
    public async Task FileEndpoint_WithQueryAuth_FullPipeline()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace MyApp.Contracts
            {
                public sealed record ErrorDto(string Code, string Message);
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

                    public static readonly RouteDefinition<List<ErrorDto>> List =
                        Define.Get<List<ErrorDto>>("/api/streams");
                }
            }
            """;

        // 1. Compile and walk
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contractEndpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        // 2. Assert walker output for the QueryAuth file endpoint
        var streamEp = contractEndpoints.Single(e => e.Name == "stream");
        Assert.True(streamEp.IsFileEndpoint);
        Assert.Equal("video/mp4", streamEp.FileContentType);
        Assert.NotNull(streamEp.QueryAuth);
        Assert.Equal("token", streamEp.QueryAuth!.ParameterName);

        // Custom parameter name
        var previewEp = contractEndpoints.Single(e => e.Name == "preview");
        Assert.True(previewEp.IsFileEndpoint);
        Assert.Equal("image/jpeg", previewEp.FileContentType);
        Assert.NotNull(previewEp.QueryAuth);
        Assert.Equal("key", previewEp.QueryAuth!.ParameterName);

        // Standard endpoint is unaffected
        var listEp = contractEndpoints.Single(e => e.Name == "list");
        Assert.False(listEp.IsFileEndpoint);
        Assert.Null(listEp.QueryAuth);

        // 3. Contract JSON emission includes queryAuth and isFileEndpoint
        var contractJson = ContractEmitter.Emit(
            new Dictionary<string, Rivet.Tool.Model.TsTypeDefinition>(walker.Definitions),
            new Dictionary<string, Rivet.Tool.Model.TsType>(walker.Enums),
            contractEndpoints.ToList());
        Assert.Contains("\"queryAuth\"", contractJson);
        Assert.Contains("\"parameterName\": \"token\"", contractJson);
        Assert.Contains("\"isFileEndpoint\": true", contractJson);

        // 4. OpenAPI emission includes x-rivet-query-auth extension
        var openApiJson = OpenApiEmitter.Emit(
            contractEndpoints.ToList(),
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            security: null);
        Assert.Contains("\"x-rivet-query-auth\"", openApiJson);

        // 5. TypeScript client includes *Url() function with route params and getBaseUrl
        var typeFileMap = CompilationHelper.BuildTypeFileMap(walker);
        var groups = ClientEmitter.GroupByController(contractEndpoints.ToList());
        var clientTs = string.Concat(
            groups.Select(g => ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
        Assert.Contains("streamUrl(id: string, token: string): string", clientTs);
        Assert.Contains("previewUrl(id: string, key: string): string", clientTs);
        Assert.Contains("getBaseUrl()", clientTs);
        Assert.DoesNotContain("listUrl(", clientTs);

        // 6. Full TS compilation check — the generated code must type-check
        var (exitCode, output) = await GenerateAndTypeCheck(source);
        Assert.True(exitCode == 0, $"tsc --noEmit failed:\n{output}");
    }

    [Fact]
    public async Task FileEndpointWithInputType_FullRoundTrip()
    {
        // A richly annotated file endpoint: typed input (route + query params),
        // custom content type, QueryAuth with custom param name, and error responses.
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace MyApp.Contracts
            {
                [RivetType]
                public sealed record StreamInput(string Id, string Quality);

                public sealed record ErrorDto(string Code, string Message);
            }

            namespace MyApp.Api
            {
                using MyApp.Contracts;

                [RivetContract]
                public static class MediaContract
                {
                    public static readonly FileRouteDefinition<StreamInput> Stream =
                        Define.File<StreamInput>("/api/media/{id}/stream")
                            .ContentType("video/mp4")
                            .QueryAuth("secret")
                            .Returns<ErrorDto>(404, "Not found")
                            .Description("Stream a media file");
                }
            }
            """;

        // ── Stage 1: C# → Walk ──
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var ep = Assert.Single(endpoints);
        Assert.Equal("stream", ep.Name);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Equal("/api/media/{id}/stream", ep.RouteTemplate);
        Assert.True(ep.IsFileEndpoint);
        Assert.Equal("video/mp4", ep.FileContentType);
        Assert.NotNull(ep.QueryAuth);
        Assert.Equal("secret", ep.QueryAuth!.ParameterName);
        Assert.Equal("Stream a media file", ep.Description);

        // Input type produces route + query params
        Assert.Equal(2, ep.Params.Count);
        var idParam = Assert.Single(ep.Params, p => p.Name == "id");
        Assert.Equal(Rivet.Tool.Model.ParamSource.Route, idParam.Source);
        var qualityParam = Assert.Single(ep.Params, p => p.Name == "quality");
        Assert.Equal(Rivet.Tool.Model.ParamSource.Query, qualityParam.Source);

        // ── Stage 2: Walk → Contract JSON ──
        var contractJson = ContractEmitter.Emit(
            new Dictionary<string, Rivet.Tool.Model.TsTypeDefinition>(walker.Definitions),
            new Dictionary<string, Rivet.Tool.Model.TsType>(walker.Enums),
            endpoints.ToList());
        Assert.Contains("\"isFileEndpoint\": true", contractJson);
        Assert.Contains("\"fileContentType\": \"video/mp4\"", contractJson);
        Assert.Contains("\"queryAuth\"", contractJson);
        Assert.Contains("\"parameterName\": \"secret\"", contractJson);

        // ── Stage 3: Walk → OpenAPI ──
        var openApiJson = OpenApiEmitter.Emit(
            endpoints.ToList(), walker.Definitions, walker.Brands, walker.Enums, security: null);
        Assert.Contains("\"x-rivet-query-auth\"", openApiJson);
        Assert.Contains("\"parameterName\": \"secret\"", openApiJson);
        // Route param, query param, and auth param all present
        Assert.Contains("\"name\": \"id\"", openApiJson);
        Assert.Contains("\"name\": \"quality\"", openApiJson);
        Assert.Contains("\"name\": \"secret\"", openApiJson);
        // Binary response with correct content type
        Assert.Contains("\"video/mp4\"", openApiJson);

        // ── Stage 4: OpenAPI → Import → C# ──
        var importResult = CompilationHelper.Import(openApiJson);
        var contractFile = CompilationHelper.FindFile(importResult, "MediaContract.cs");
        Assert.Contains("Define.File<", contractFile);
        Assert.Contains(".ContentType(\"video/mp4\")", contractFile);
        Assert.Contains(".QueryAuth(\"secret\")", contractFile);
        Assert.Contains("FileRouteDefinition<", contractFile);

        // ── Stage 5: Imported C# → Compile → Walk again ──
        var importedCompilation = CompilationHelper.CompileImportResult(importResult);
        var (importedDiscovered, importedWalker) = CompilationHelper.DiscoverAndWalk(importedCompilation);
        var importedEndpoints = CompilationHelper.WalkContracts(
            importedCompilation, importedDiscovered, importedWalker);

        var importedEp = Assert.Single(importedEndpoints);
        Assert.Equal("GET", importedEp.HttpMethod);
        Assert.True(importedEp.IsFileEndpoint);
        Assert.Equal("video/mp4", importedEp.FileContentType);
        Assert.NotNull(importedEp.QueryAuth);
        Assert.Equal("secret", importedEp.QueryAuth!.ParameterName);
        // Input params survive the round-trip
        var importedIdParam = Assert.Single(importedEp.Params, p => p.Name == "id");
        Assert.Equal(Rivet.Tool.Model.ParamSource.Route, importedIdParam.Source);
        var importedQualityParam = Assert.Single(importedEp.Params, p => p.Name == "quality");
        Assert.Equal(Rivet.Tool.Model.ParamSource.Query, importedQualityParam.Source);

        // ── Stage 6: Walk → TypeScript client ──
        var typeFileMap = CompilationHelper.BuildTypeFileMap(walker);
        var groups = ClientEmitter.GroupByController(endpoints.ToList());
        var clientTs = string.Concat(
            groups.Select(g => ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
        // *Url() function has all params: route param, query param, and auth token
        Assert.Contains("streamUrl(id: string, quality: string, secret: string): string", clientTs);
        Assert.Contains("getBaseUrl()", clientTs);
        // URL includes both query param and auth token
        Assert.Contains("quality=", clientTs);
        Assert.Contains("secret=", clientTs);

        // ── Stage 7: Full TS compilation ──
        var (exitCode, output) = await GenerateAndTypeCheck(source);
        Assert.True(exitCode == 0, $"tsc --noEmit failed:\n{output}");
    }

    private async Task<(int ExitCode, string Output)> GenerateAndTypeCheck(string csharpSource)
    {
        // 1. Compile C# and run Rivet analysis
        var compilation = CompilationHelper.CreateCompilation(csharpSource);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var controllerEndpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var contractEndpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        // Merge: contract wins on (ControllerName, Name) collision
        var seen = new HashSet<(string, string)>(
            contractEndpoints.Select(e => (e.ControllerName, e.Name)));
        var endpoints = new List<Rivet.Tool.Model.TsEndpointDefinition>(contractEndpoints);
        foreach (var ep in controllerEndpoints)
        {
            if (seen.Add((ep.ControllerName, ep.Name)))
            {
                endpoints.Add(ep);
            }
        }

        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(
            definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();

        // 2. Write all generated files to temp directory
        Directory.CreateDirectory(_tempDir);

        var typesDir = Path.Combine(_tempDir, "types");
        Directory.CreateDirectory(typesDir);

        var typeFileNames = new List<string>();
        foreach (var group in typeGrouping.Groups)
        {
            var content = TypeEmitter.EmitGroupFile(group);
            await File.WriteAllTextAsync(Path.Combine(typesDir, $"{group.FileName}.ts"), content);
            typeFileNames.Add(group.FileName);
        }

        var typesBarrel = TypeEmitter.EmitNamespacedBarrel(typeFileNames);
        await File.WriteAllTextAsync(Path.Combine(typesDir, "index.ts"), typesBarrel);

        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "rivet.ts"), ClientEmitter.EmitRivetBase());

        var clientDir = Path.Combine(_tempDir, "client");
        Directory.CreateDirectory(clientDir);

        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        var clientFileNames = new List<string>();
        foreach (var (controllerName, groupEndpoints) in controllerGroups)
        {
            var clientContent = ClientEmitter.EmitControllerClient(
                controllerName, groupEndpoints, typeFileMap);
            await File.WriteAllTextAsync(
                Path.Combine(clientDir, $"{controllerName}.ts"), clientContent);
            clientFileNames.Add(controllerName);
        }

        var clientBarrel = TypeEmitter.EmitNamespacedBarrel(clientFileNames);
        await File.WriteAllTextAsync(Path.Combine(clientDir, "index.ts"), clientBarrel);

        // 3. Write tsconfig.json
        var tsconfig = """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "ES2022",
                "moduleResolution": "bundler",
                "strict": true,
                "skipLibCheck": true,
                "noEmit": true
              },
              "include": ["./**/*.ts"],
              "exclude": ["node_modules"]
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "tsconfig.json"), tsconfig);

        // 4. Run tsc --noEmit
        return await RunTscAsync();
    }

    private async Task<(int ExitCode, string Output)> RunTscAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "npx",
            Arguments = $"--yes tsc --noEmit --project \"{Path.Combine(_tempDir, "tsconfig.json")}\"",
            WorkingDirectory = _tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start tsc");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = string.Join("\n",
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return (process.ExitCode, output);
    }
}
