using System.Diagnostics;
using System.Text;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

/// <summary>
/// Phase 2 (FABLE_REWRITE_PLAN.md WP-2.1 / WP-2.2): the openapi-fetch parallel run.
///
/// WP-2.1 — the openapi-typescript consumer path: emit the ContractApi sample's
/// OpenAPI 3.1, run the vendored <c>openapi-typescript</c> over it, and type-check a
/// small hand-written <c>openapi-fetch</c> consumer module (<c>createClient&lt;paths&gt;</c>)
/// under <c>tsc --strict</c>.
///
/// WP-2.2 — dual-run: boot the real ContractApi server and exercise a representative
/// endpoint set through BOTH clients — the existing generated rivet.ts client (the
/// comparison baseline that dies at Phase 3) and the new openapi-fetch consumer —
/// asserting identical status codes and response bodies.
///
/// Tooling is vendored in Rivet.Tests/js (openapi-fetch added as a devDependency
/// for this suite); runs offline after one `npm install` there.
/// </summary>
[Trait("Category", "Local")]
public sealed class SampleProjectOpenApiFetchTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SampleDir = Path.Combine(RepoRoot, "samples", "ContractApi");
    private static readonly string JsDir = Path.Combine(RepoRoot, "Rivet.Tests", "js");
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"rivet-oaf-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ════════════════ WP-2.1 — openapi-fetch consumer type-checks ════════════════

    [Fact]
    public async Task OpenApiFetch_Consumer_TypeChecks_Under_TscStrict()
    {
        Directory.CreateDirectory(_tempDir);
        await GenerateOpenApiFetchArtifacts(_tempDir);
        await WriteTsConfig(_tempDir);
        LinkNodeModules(_tempDir);

        var tsc = Path.Combine(JsDir, "node_modules", ".bin", "tsc");
        var (tscExit, tscOut, tscErr) = RunNode(tsc, ["--project", Path.Combine(_tempDir, "tsconfig.json")]);
        Assert.True(tscExit == 0,
            $"tsc --strict rejected the openapi-fetch consumer:\n{tscOut}\n{tscErr}");
    }

    // ═════════ WP-2.2 — dual-run: rivet.ts client vs openapi-fetch consumer ═════════
    //
    // Same booted ContractApi server, same requests through both clients, identical
    // statuses and bodies. Covered: GET (200 JSON), POST with body (201), DELETE with
    // route param (204 void), an error path (404), PUT with route param + body (204),
    // a bare GET void (health), and the queryAuth file endpoint (avatar) — exercised
    // via the same URL through both clients (the rivet client exposes queryAuth file
    // endpoints as URL builders; openapi-fetch demands the token per call).

    [Fact]
    public async Task DualRun_RivetClient_And_OpenApiFetch_Produce_Identical_Responses()
    {
        Directory.CreateDirectory(_tempDir);

        // 1. Generate both clients into one package
        await GenerateRivetTsClient(_tempDir);
        await GenerateOpenApiFetchArtifacts(_tempDir);
        await WriteTsConfig(_tempDir);
        LinkNodeModules(_tempDir);

        // 2. Compile TS → JS
        var tsc = Path.Combine(JsDir, "node_modules", ".bin", "tsc");
        var (tscExit, tscOut, tscErr) = RunNode(tsc, ["--project", Path.Combine(_tempDir, "tsconfig.json")]);
        Assert.True(tscExit == 0, $"tsc failed:\n{tscOut}\n{tscErr}");

        // 3. Boot the real ContractApi server
        var port = Random.Shared.Next(49152, 65000);
        var url = $"http://localhost:{port}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var server = await StartSampleServer(url, cts.Token);

        // 4. Dual-run script: same requests through both clients, compare
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "dual-run.mjs"), DualRunScript);

        var (nodeExit, nodeOutput) = await RunProcessAsync(
            "node", $"\"{Path.Combine(_tempDir, "dual-run.mjs")}\" {url}",
            workingDir: _tempDir);

        Assert.True(nodeExit == 0, $"Dual-run found divergences:\n{nodeOutput}");
    }

    // ═══════════════════════════ Generation helpers ═══════════════════════════

    /// <summary>
    /// The openapi-typescript consumer path: emitted spec → schema.d.ts →
    /// hand-written openapi-fetch consumer module.
    /// </summary>
    private async Task GenerateOpenApiFetchArtifacts(string dir)
    {
        Assert.True(
            File.Exists(Path.Combine(JsDir, "node_modules", "openapi-fetch", "package.json")),
            "openapi-fetch not installed — run 'npm install' in Rivet.Tests/js/");

        // Emit the sample's OpenAPI 3.1 (same emission path as `rivet --openapi`).
        var specPath = Path.Combine(dir, "openapi.json");
        await File.WriteAllTextAsync(specPath, OpenApiConformanceTests.EmitSpec("contractapi-sample"));

        // openapi-typescript over it (vendored, offline).
        var openapiTs = Path.Combine(JsDir, "node_modules", ".bin", "openapi-typescript");
        var schemaPath = Path.Combine(dir, "schema.d.ts");
        var (genExit, genOut, genErr) = RunNode(openapiTs, [specPath, "-o", schemaPath]);
        Assert.True(genExit == 0 && File.Exists(schemaPath),
            $"openapi-typescript failed (exit {genExit}):\n{genOut}\n{genErr}");

        // Hand-written consumer module (the WP-5a shape: createClient<paths>).
        await File.WriteAllTextAsync(Path.Combine(dir, "consumer.ts"), ConsumerModuleSource);
    }

    /// <summary>
    /// The comparison baseline: the generated rivet.ts client, exactly as
    /// SampleProjectTests tier 3 generates it. Dies at Phase 3.
    /// </summary>
    private static async Task GenerateRivetTsClient(string dir)
    {
        var sources = ReadSampleSources();
        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contractEndpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(
            definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();

        var typesDir = Path.Combine(dir, "types");
        Directory.CreateDirectory(typesDir);

        foreach (var group in typeGrouping.Groups)
        {
            await File.WriteAllTextAsync(
                Path.Combine(typesDir, $"{group.FileName}.ts"), TypeEmitter.EmitGroupFile(group));
        }

        var typeFileNames = typeGrouping.Groups.Select(g => g.FileName).ToList();
        await File.WriteAllTextAsync(
            Path.Combine(typesDir, "index.ts"), TypeEmitter.EmitNamespacedBarrel(typeFileNames));

        await File.WriteAllTextAsync(Path.Combine(dir, "rivet.ts"), ClientEmitter.EmitRivetBase());

        var clientDir = Path.Combine(dir, "client");
        Directory.CreateDirectory(clientDir);

        var controllerGroups = ClientEmitter.GroupByController(contractEndpoints);
        var clientFileNames = new List<string>();
        foreach (var (controllerName, groupEndpoints) in controllerGroups)
        {
            await File.WriteAllTextAsync(
                Path.Combine(clientDir, $"{controllerName}.ts"),
                ClientEmitter.EmitControllerClient(controllerName, groupEndpoints, typeFileMap));
            clientFileNames.Add(controllerName);
        }

        await File.WriteAllTextAsync(
            Path.Combine(clientDir, "index.ts"), TypeEmitter.EmitNamespacedBarrel(clientFileNames));
    }

    private static async Task WriteTsConfig(string dir)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, "package.json"), """
            { "type": "module" }
            """);

        await File.WriteAllTextAsync(Path.Combine(dir, "tsconfig.json"), """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "ES2022",
                "moduleResolution": "bundler",
                "strict": true,
                "skipLibCheck": true,
                "outDir": "./dist",
                "declaration": false
              },
              "include": ["./**/*.ts"],
              "exclude": ["node_modules", "dist"]
            }
            """);
    }

    private static void LinkNodeModules(string dir)
    {
        var link = Path.Combine(dir, "node_modules");
        if (!Directory.Exists(link))
        {
            Directory.CreateSymbolicLink(link, Path.Combine(JsDir, "node_modules"));
        }
    }

    // ═══════════════════════ The hand-written consumer ═══════════════════════
    //
    // What WP-5a would emit (or a real app would hand-write): createClient<paths>
    // over the openapi-typescript output, one thin typed wrapper per endpoint.
    // The narrowing assertions inside (`data.totalCount: number`,
    // `error.message: string`) are what tsc --strict actually verifies.

    private const string ConsumerModuleSource = """
        import createClient from "openapi-fetch";
        import type { components, paths } from "./schema";

        export type Api = ReturnType<typeof createApi>;
        export type InviteMemberRequest = components["schemas"]["InviteMemberRequest"];

        export function createApi(baseUrl: string, fetchImpl?: typeof fetch) {
          return createClient<paths>(fetchImpl ? { baseUrl, fetch: fetchImpl } : { baseUrl });
        }

        export async function listMembers(api: Api) {
          const { data, error, response } = await api.GET("/api/members");
          if (data !== undefined) {
            // strict: schema narrows — PagedResult_MemberDto
            const totalCount: number = data.totalCount;
            void totalCount;
          }
          return { status: response.status, data, error };
        }

        export async function inviteMember(api: Api, body: InviteMemberRequest) {
          const { data, error, response } = await api.POST("/api/members", { body });
          if (error !== undefined) {
            // 422 is the only declared error response: error is ValidationErrorDto
            const message: string = error.message;
            void message;
          }
          return { status: response.status, data, error };
        }

        export async function removeMember(api: Api, id: string) {
          const { data, error, response } = await api.DELETE("/api/members/{id}", {
            params: { path: { id } },
          });
          return { status: response.status, data, error };
        }

        export async function updateMemberRole(api: Api, id: string, role: string) {
          const { data, error, response } = await api.PUT("/api/members/{id}/role", {
            params: { path: { id } },
            body: { role },
          });
          return { status: response.status, data, error };
        }

        export async function checkHealth(api: Api) {
          const { data, error, response } = await api.GET("/api/health");
          return { status: response.status, data, error };
        }

        export async function getAvatar(api: Api, id: string, token: string) {
          // queryAuth: the spec marks `token` a required query param, so openapi-fetch
          // demands it on every call — there is no per-client config injection point.
          const { error, response } = await api.GET("/api/members/{id}/avatar", {
            params: { path: { id }, query: { token } },
            parseAs: "blob",
          });
          return { status: response.status, error };
        }
        """;

    // ═══════════════════════════ The dual-run script ═══════════════════════════
    //
    // Body comparison normalizes "no meaningful body" representations: the rivet
    // client yields `undefined` for empty bodies; openapi-fetch yields `{}` or `""`
    // depending on content-length/parse failure. Everything else must DeepEqual.
    // invite's 201 body is compared structurally (the server mints a fresh Guid per
    // request, so two calls can never return byte-identical ids).

    private const string DualRunScript = """
        import { configureRivet } from "./dist/rivet.js";
        import * as rivet from "./dist/client/members.js";
        import { createApi, listMembers, inviteMember, removeMember, updateMemberRole, checkHealth, getAvatar } from "./dist/consumer.js";

        const baseUrl = process.argv[2];
        configureRivet({ baseUrl });
        const api = createApi(baseUrl);

        const failures = [];

        // Normalize "empty body": rivet → undefined; openapi-fetch → {} / "" / undefined.
        function norm(body) {
          if (body === undefined || body === null || body === "") return null;
          if (typeof body === "object" && !Array.isArray(body) && Object.keys(body).length === 0) return null;
          return body;
        }

        function compare(name, r, o, { structural = false } = {}) {
          if (r.status !== o.status) {
            failures.push(`${name}: status divergence — rivet=${r.status} openapi-fetch=${o.status}`);
            return;
          }
          const rb = norm(r.body);
          const ob = norm(o.body);
          if (structural) {
            const rk = rb === null ? null : Object.keys(rb).sort().join(",");
            const ok = ob === null ? null : Object.keys(ob).sort().join(",");
            if (rk !== ok) {
              failures.push(`${name}: body shape divergence — rivet keys=[${rk}] openapi-fetch keys=[${ok}]`);
            }
            return;
          }
          if (JSON.stringify(rb) !== JSON.stringify(ob)) {
            failures.push(`${name}: body divergence —\n  rivet:         ${JSON.stringify(rb)}\n  openapi-fetch: ${JSON.stringify(ob)}`);
          }
        }

        function expectStatus(name, status, expected) {
          if (status !== expected) {
            failures.push(`${name}: expected status ${expected}, got ${status}`);
          }
        }

        const isUuid = (s) => typeof s === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s);

        // ── 1. GET /api/members → 200 + PagedResult ──
        {
          const r = await rivet.list({ unwrap: false });
          const o = await listMembers(api);
          expectStatus("list(rivet)", r.status, 200);
          compare("list", { status: r.status, body: r.data }, { status: o.status, body: o.data ?? o.error });
        }

        // ── 2. POST /api/members → 201 + { id } (fresh Guid per request → structural) ──
        {
          // NB: wire shape matches the live server (Email round-trips as an object on
          // the wire in this sample); both client type surfaces brand it as string,
          // so both calls cast identically at the call site.
          const body = { email: { value: "dual@example.com" }, role: "member", nickname: "dual" };
          const r = await rivet.invite({ body }, { unwrap: false });
          const o = await inviteMember(api, body);
          expectStatus("invite(rivet)", r.status, 201);
          compare("invite", { status: r.status, body: r.data }, { status: o.status, body: o.data ?? o.error }, { structural: true });
          if (r.status === 201 && !isUuid(r.data?.id)) failures.push("invite: rivet id is not a uuid");
          if (o.status === 201 && !isUuid(o.data?.id)) failures.push("invite: openapi-fetch id is not a uuid");
        }

        // ── 3. DELETE /api/members/{id} (valid guid) → 204 void ──
        {
          const id = "a1b2c3d4-e5f6-4a7b-8c9d-000000000001";
          const r = await rivet.remove({ params: { id } }, { unwrap: false });
          const o = await removeMember(api, id);
          expectStatus("remove(rivet)", r.status, 204);
          compare("remove", { status: r.status, body: r.data }, { status: o.status, body: o.data ?? o.error });
        }

        // ── 4. DELETE /api/members/{id} (non-guid) → 404 error path ──
        {
          const id = "not-a-guid";
          const r = await rivet.remove({ params: { id } }, { unwrap: false });
          const o = await removeMember(api, id);
          expectStatus("remove-404(rivet)", r.status, 404);
          compare("remove-404", { status: r.status, body: r.data }, { status: o.status, body: o.data ?? o.error });
        }

        // ── 5. PUT /api/members/{id}/role → 204 ──
        {
          const id = "a1b2c3d4-e5f6-4a7b-8c9d-000000000001";
          const r = await rivet.updateRole({ params: { id }, body: { role: "viewer" } }, { unwrap: false });
          const o = await updateMemberRole(api, id, "viewer");
          expectStatus("updateRole(rivet)", r.status, 204);
          compare("updateRole", { status: r.status, body: r.data }, { status: o.status, body: o.data ?? o.error });
        }

        // ── 6. GET /api/health → void ──
        {
          const r = await rivet.health({ unwrap: false });
          const o = await checkHealth(api);
          compare("health", { status: r.status, body: r.data }, { status: o.status, body: o.data ?? o.error });
        }

        // ── 7. GET /api/members/{id}/avatar?token=… (queryAuth file endpoint) ──
        // The rivet client surfaces queryAuth file endpoints as URL builders
        // (avatarUrl injects the token into the query); openapi-fetch types the token
        // as a required query param per call. Same URL through both → same response.
        {
          const id = "a1b2c3d4-e5f6-4a7b-8c9d-000000000001";
          const url = rivet.avatarUrl({ params: { id }, query: { token: "tok-123" } });
          if (!url.includes("token=tok-123")) {
            failures.push(`avatar: rivet avatarUrl did not inject queryAuth token: ${url}`);
          }
          const rResp = await fetch(url);
          const o = await getAvatar(api, id, "tok-123");
          compare("avatar", { status: rResp.status, body: null }, { status: o.status, body: null });
        }

        if (failures.length > 0) {
          console.error(`DUAL-RUN DIVERGENCES (${failures.length}):`);
          for (const f of failures) console.error(`  - ${f}`);
          process.exit(1);
        }

        console.log("Dual-run: both clients produced identical statuses and bodies across all 7 scenarios");
        """;

    // ═══════════════════════════ Process plumbing ═══════════════════════════

    private static string[] ReadSampleSources()
    {
        const string implicitUsings = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;

            """;

        return
        [
            implicitUsings + File.ReadAllText(Path.Combine(SampleDir, "Domain", "ValueObjects.cs")),
            implicitUsings + File.ReadAllText(Path.Combine(SampleDir, "Models", "MemberModels.cs")),
            implicitUsings + File.ReadAllText(Path.Combine(SampleDir, "Contracts", "MembersContract.cs")),
        ];
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Rivet.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not find repo root (Rivet.slnx)");
    }

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

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName, string arguments, string? workingDir = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir ?? RepoRoot,
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

    private static async Task<AsyncServerHandle> StartSampleServer(string url, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{Path.Combine(SampleDir, "ContractApi.csproj")}\" --urls {url}",
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start sample server");

        var started = false;
        var output = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                output.AppendLine(line);

                if (line.Contains("Now listening on:"))
                {
                    started = true;
                    break;
                }
            }

            if (!started)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException(
                    $"Server did not start. Output:\n{output}\nStderr:\n{stderr}");
            }
        }
        catch
        {
            process.Kill();
            process.Dispose();
            throw;
        }

        return new AsyncServerHandle(process);
    }

    private sealed class AsyncServerHandle(Process process) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
            catch
            {
                // Best effort
            }

            process.Dispose();
        }
    }
}
