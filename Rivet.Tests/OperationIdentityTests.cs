using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// Regression evidence for outcome:operation-identity:
/// - Overloaded same-name actions at distinct routes both survive the merge and get
///   distinct deterministic operationIds, in either discovery order.
/// - Cross-frontend same-name collisions survive (transport identity, not source
///   identity, decides survival).
/// - Contradictory declarations of the same normalized method+route fail through the
///   public CLI with exit 1, a diagnostic naming both sources, and no partial
///   replacement of previously written output.
/// - Reported operation counts equal the emitted operation count.
/// </summary>
public sealed class OperationIdentityTests
{
    private const string OverloadsSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record ItemDto(string Id, string Title);

        [RivetClient]
        [ApiController]
        [Route("api")]
        public sealed class ItemsController : ControllerBase
        {
            [HttpGet("items")]
            [ProducesResponseType(typeof(ItemDto[]), 200)]
            public Task<IActionResult> Get(CancellationToken ct)
                => throw new NotImplementedException();

            [HttpGet("items/{id}")]
            [ProducesResponseType(typeof(ItemDto), 200)]
            public Task<IActionResult> Get(int id, CancellationToken ct)
                => throw new NotImplementedException();
        }
        """;

    /// <summary>Same controller with the two actions declared in the opposite order.</summary>
    private const string OverloadsSourceReversed = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record ItemDto(string Id, string Title);

        [RivetClient]
        [ApiController]
        [Route("api")]
        public sealed class ItemsController : ControllerBase
        {
            [HttpGet("items/{id}")]
            [ProducesResponseType(typeof(ItemDto), 200)]
            public Task<IActionResult> Get(int id, CancellationToken ct)
                => throw new NotImplementedException();

            [HttpGet("items")]
            [ProducesResponseType(typeof(ItemDto[]), 200)]
            public Task<IActionResult> Get(CancellationToken ct)
                => throw new NotImplementedException();
        }
        """;

    [Fact]
    public void Overloaded_Same_Name_Actions_At_Distinct_Routes_Both_Survive()
    {
        var (endpoints, _) = CompilationHelper.WalkMerged(OverloadsSource);

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, ep => Assert.Equal("GET", ep.HttpMethod));
        Assert.Equal(
            new[] { "/api/items", "/api/items/{id}" },
            endpoints
                .Select(ep => ep.RouteTemplate)
                .OrderBy(route => route, StringComparer.Ordinal)
                .ToArray()
        );
        // Name equality alone did not remove an operation.
        Assert.All(endpoints, ep => Assert.Equal("get", ep.Name));
    }

    [Fact]
    public void Overloaded_Actions_Emit_Distinct_Deterministic_OperationIds()
    {
        var document = CompilationHelper.EmitOpenApi(OverloadsSource).RootElement;

        var paths = document.GetProperty("paths");
        var listOp = paths.GetProperty("/api/items").GetProperty("get");
        var itemOp = paths.GetProperty("/api/items/{id}").GetProperty("get");

        var listId = listOp.GetProperty("operationId").GetString()!;
        var itemId = itemOp.GetProperty("operationId").GetString()!;

        Assert.NotEqual(listId, itemId);
        // Deterministic route-derived disambiguator on ALL members of the colliding
        // name group: /api/items → api_items, /api/items/{id} → api_items_id.
        Assert.Equal("items_get_api_items", listId);
        Assert.Equal("items_get_api_items_id", itemId);
    }

    [Fact]
    public void Overloaded_OperationIds_Are_Independent_Of_Discovery_Order()
    {
        // The same two actions declared in the opposite source order must produce the
        // identical operationIds — discovery order never picks the ids.
        var forwardDocument = CompilationHelper.EmitOpenApi(OverloadsSource).RootElement;
        var reversedDocument = CompilationHelper.EmitOpenApi(OverloadsSourceReversed).RootElement;

        foreach (var document in new[] { forwardDocument, reversedDocument })
        {
            var paths = document.GetProperty("paths");
            Assert.Equal(
                "items_get_api_items",
                paths
                    .GetProperty("/api/items")
                    .GetProperty("get")
                    .GetProperty("operationId")
                    .GetString()
            );
            Assert.Equal(
                "items_get_api_items_id",
                paths
                    .GetProperty("/api/items/{id}")
                    .GetProperty("get")
                    .GetProperty("operationId")
                    .GetString()
            );
        }
    }

    [Fact]
    public void Unique_Names_Keep_The_Baseline_OperationId_Shape()
    {
        var document = CompilationHelper
            .EmitOpenApi(
                """
                using System;
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.AspNetCore.Mvc;
                using Rivet;

                namespace Test;

                [RivetType]
                public sealed record ItemDto(string Id);

                [RivetClient]
                [ApiController]
                [Route("api")]
                public sealed class ItemsController : ControllerBase
                {
                    [HttpGet("items")]
                    [ProducesResponseType(typeof(ItemDto[]), 200)]
                    public Task<IActionResult> List(CancellationToken ct)
                        => throw new NotImplementedException();
                }
                """
            )
            .RootElement;

        Assert.Equal(
            "items_list",
            document
                .GetProperty("paths")
                .GetProperty("/api/items")
                .GetProperty("get")
                .GetProperty("operationId")
                .GetString()
        );
    }

    [Fact]
    public void Cross_Frontend_Same_Name_Collision_Survives_Both_Operations()
    {
        // Contract-first and annotation frontends declare two same-named endpoints at
        // distinct routes — transport identity keeps both alive.
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItem =
                    Define.Get<ItemDto>("/api/contract-items/{id}");
            }

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpGet("annotation-items/{id}")]
                [ProducesResponseType(typeof(ItemDto), 200)]
                public Task<IActionResult> Get(int id, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        Assert.Equal(2, endpoints.Count);
        // Same source name from both frontends (GetItem → getItem, Get → get) —
        // survival is decided by transport identity, not by the shared name.
        Assert.Contains(endpoints, ep => ep.Name == "getItem" && ep.ControllerName == "items");
        Assert.Contains(endpoints, ep => ep.Name == "get" && ep.ControllerName == "items");
        Assert.Equal(
            new[] { "/api/annotation-items/{id}", "/api/contract-items/{id}" },
            endpoints
                .Select(ep => ep.RouteTemplate)
                .OrderBy(route => route, StringComparer.Ordinal)
                .ToArray()
        );
    }

    [Fact]
    public void Equivalent_Cross_Frontend_Declarations_Of_One_Operation_Collapse()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id, string Title);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItems =
                    Define.Get<ItemDto[]>("/api/items");
            }

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpGet("items")]
                [ProducesResponseType(typeof(ItemDto[]), 200)]
                public Task<IActionResult> Get(CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        // Equivalent representations of one transport identity collapse to one
        // emitted operation — contract-first representative.
        var endpoint = Assert.Single(endpoints);
        Assert.Equal("GET", endpoint.HttpMethod);
        Assert.Equal("/api/items", endpoint.RouteTemplate);
        Assert.Equal("items", endpoint.ControllerName);
        Assert.Equal("getItems", endpoint.Name);
    }

    [Fact]
    public void Contradictory_Same_Transport_Identity_Declarations_Fail_At_Merge()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id, string Title);

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItems =
                    Define.Get<ItemDto>("/api/items");
            }

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpGet("items")]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public Task<IActionResult> Get(CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        Assert.Contains("error RIV2012:", exception.Message);
        Assert.Contains("GET /api/items", exception.Message);
        // Both conflicting sources are named.
        Assert.Contains("'items.getItems'", exception.Message);
        Assert.Contains("'items.get'", exception.Message);
    }

    [Fact]
    public async Task Public_Cli_Fails_On_Contradictory_Operations_Without_Partial_Replacement()
    {
        using var work = OperationIdentityWork.Create();
        var outputDir = Path.Combine(work.Path, "output");
        Directory.CreateDirectory(outputDir);

        // Seed a previously written openapi.json — a failed re-emission must leave it
        // byte-identical (no successful partial replacement).
        var specPath = Path.Combine(outputDir, "openapi.json");
        const string previousSpec = "{ \"previous\": true }";
        await File.WriteAllTextAsync(specPath, previousSpec);

        var sourcePath = Path.Combine(work.Path, "Conflicting.cs");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id, string Title);

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItems =
                    Define.Get<ItemDto>("/api/items");
            }

            [RivetClient]
            [ApiController]
            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpGet("items")]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public Task<IActionResult> Get(CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """
        );

        var emission = CliRunner.RunCli(work.Path, [sourcePath, "--output", outputDir]);

        Assert.Equal(1, emission.ExitCode);
        Assert.Contains("error RIV2012:", emission.StdErr);
        // Both conflicting sources are named (controller/contract name is source-derived:
        // ItemsContract → items, ItemsController → items).
        Assert.Contains("getItems", emission.StdErr);
        Assert.Contains("'items.get'", emission.StdErr);
        Assert.DoesNotContain("Unhandled exception", emission.StdErr);

        // The previously committed spec is untouched.
        Assert.Equal(previousSpec, await File.ReadAllTextAsync(specPath));
    }

    [Fact]
    public void Reported_Operation_Count_Equals_Emitted_Operation_Count()
    {
        var document = CompilationHelper.EmitOpenApi(OverloadsSource).RootElement;

        var emitted = 0;
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (
                    operation.Name
                    is "get"
                        or "put"
                        or "post"
                        or "delete"
                        or "options"
                        or "head"
                        or "patch"
                        or "trace"
                )
                {
                    emitted++;
                }
            }
        }

        // The two overloaded operations survived: the emitted operation count equals
        // the declared operations in paths, and nothing was silently dropped.
        Assert.Equal(2, emitted);
    }

    [Fact]
    public async Task Public_Cli_Emits_Both_Overloads_With_Reported_Count_Matching()
    {
        using var work = OperationIdentityWork.Create();
        var outputDir = Path.Combine(work.Path, "output");

        var sourcePath = Path.Combine(work.Path, "Overloads.cs");
        await File.WriteAllTextAsync(sourcePath, OverloadsSource);

        var emission = CliRunner.RunCli(work.Path, [sourcePath, "--output", outputDir]);

        Assert.Equal(0, emission.ExitCode);
        Assert.Contains("2 endpoints.", emission.StdOut);

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(outputDir, "openapi.json"))
        );
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/items", out var listPath));
        Assert.True(listPath.TryGetProperty("get", out _));
        Assert.True(paths.TryGetProperty("/api/items/{id}", out var itemPath));
        Assert.True(itemPath.TryGetProperty("get", out _));
    }
}

/// <summary>Self-contained temp workspace for CLI regression runs.</summary>
internal sealed class OperationIdentityWork(string path) : IDisposable
{
    public string Path { get; } = path;

    public static OperationIdentityWork Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"rivet-operation-identity-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return new OperationIdentityWork(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
