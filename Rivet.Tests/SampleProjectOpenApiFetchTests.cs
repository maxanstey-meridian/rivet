using System.Diagnostics;
using System.Text;

namespace Rivet.Tests;

/// <summary>
/// Phase 2 (FABLE_REWRITE_PLAN.md WP-2.1 / WP-2.2): the openapi-fetch parallel run.
///
/// WP-2.1 — the openapi-typescript consumer path: emit the ContractApi sample's
/// OpenAPI 3.1, run the vendored <c>openapi-typescript</c> over it, and type-check a
/// small hand-written <c>openapi-fetch</c> consumer module (<c>createClient&lt;paths&gt;</c>)
/// under <c>tsc --strict</c>.
///
/// Phase 3 (single-arm): boot the real ContractApi server and exercise a
/// representative endpoint set through the openapi-fetch consumer, asserting
/// statuses and bodies against expected literals. (The rivet.ts comparison arm
/// died with the TS emitters at Phase 3.)
///
/// Tooling is vendored in Rivet.Tests/js (openapi-fetch added as a devDependency
/// for this suite); runs offline after one `pnpm install` there.
/// </summary>
[Trait("Category", "Local")]
public sealed class SampleProjectOpenApiFetchTests : IDisposable
{
    private static readonly string _repoRoot = FindRepoRoot();
    private static readonly string _sampleDir = Path.Combine(_repoRoot, "samples", "ContractApi");
    private static readonly string _jsDir = Path.Combine(_repoRoot, "Rivet.Tests", "js");
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"rivet-oaf-test-{Guid.NewGuid():N}"
    );

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

        var tsc = Path.Combine(_jsDir, "node_modules", "typescript", "bin", "tsc");
        var (tscExit, tscOut, tscErr) = RunNode(
            tsc,
            ["--project", Path.Combine(_tempDir, "tsconfig.json")]
        );
        Assert.True(
            tscExit == 0,
            $"tsc --strict rejected the openapi-fetch consumer:\n{tscOut}\n{tscErr}"
        );
    }

    // ═════════ Single-arm run: openapi-fetch consumer against the live server ═════════
    //
    // Same booted ContractApi server, results asserted against expected literals.
    // Covered: GET (200 JSON), POST with body (201), DELETE with route param (204
    // void), an error path (404), PUT with route param + body (204), a bare GET void
    // (health), and the queryAuth file endpoint (avatar — the spec marks the token a
    // required query param, so openapi-fetch demands it per call).

    [Fact]
    public async Task OpenApiFetch_Consumer_Produces_Expected_Responses_Against_Live_Server()
    {
        Directory.CreateDirectory(_tempDir);

        // 1. Generate the openapi-fetch consumer package
        await GenerateOpenApiFetchArtifacts(_tempDir);
        await WriteTsConfig(_tempDir);
        LinkNodeModules(_tempDir);

        // 2. Compile TS → JS
        var tsc = Path.Combine(_jsDir, "node_modules", "typescript", "bin", "tsc");
        var (tscExit, tscOut, tscErr) = RunNode(
            tsc,
            ["--project", Path.Combine(_tempDir, "tsconfig.json")]
        );
        Assert.True(tscExit == 0, $"tsc failed:\n{tscOut}\n{tscErr}");

        // 3. Boot the real ContractApi server
        var port = Random.Shared.Next(49152, 65000);
        var url = $"http://localhost:{port}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var server = await StartSampleServer(url, cts.Token);

        // 4. Run the consumer against the live server, assert expected literals
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "single-run.mjs"), SingleRunScript);

        var (nodeExit, nodeOutput) = await RunProcessAsync(
            "node",
            $"\"{Path.Combine(_tempDir, "single-run.mjs")}\" {url}",
            workingDir: _tempDir
        );

        Assert.True(nodeExit == 0, $"openapi-fetch run found mismatches:\n{nodeOutput}");
    }

    // ═══════════════════════════ Generation helpers ═══════════════════════════

    /// <summary>
    /// The openapi-typescript consumer path: emitted spec → schema.d.ts →
    /// hand-written openapi-fetch consumer module.
    /// </summary>
    private async Task GenerateOpenApiFetchArtifacts(string dir)
    {
        Assert.True(
            File.Exists(Path.Combine(_jsDir, "node_modules", "openapi-fetch", "package.json")),
            "openapi-fetch not installed — run 'pnpm install' in Rivet.Tests/js/"
        );

        // Emit the sample's OpenAPI 3.1 (same emission path as `rivet --openapi`).
        var specPath = Path.Combine(dir, "openapi.json");
        await File.WriteAllTextAsync(
            specPath,
            OpenApiConformanceTests.EmitSpec("contractapi-sample")
        );

        // openapi-typescript over it (vendored, offline).
        var openapiTs = Path.Combine(_jsDir, "node_modules", "openapi-typescript", "bin", "cli.js");
        var schemaPath = Path.Combine(dir, "schema.d.ts");
        var (genExit, genOut, genErr) = RunNode(openapiTs, [specPath, "-o", schemaPath]);
        Assert.True(
            genExit == 0 && File.Exists(schemaPath),
            $"openapi-typescript failed (exit {genExit}):\n{genOut}\n{genErr}"
        );

        // Hand-written consumer module (the WP-5a shape: createClient<paths>).
        await File.WriteAllTextAsync(Path.Combine(dir, "consumer.ts"), ConsumerModuleSource);
    }

    private static async Task WriteTsConfig(string dir)
    {
        await File.WriteAllTextAsync(
            Path.Combine(dir, "package.json"),
            """
            { "type": "module" }
            """
        );

        await File.WriteAllTextAsync(
            Path.Combine(dir, "tsconfig.json"),
            """
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
            """
        );
    }

    private static void LinkNodeModules(string dir)
    {
        var link = Path.Combine(dir, "node_modules");
        if (!Directory.Exists(link))
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_jsDir, "node_modules"));
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

    // ═══════════════════════ The single-arm run script ═══════════════════════
    //
    // openapi-fetch results asserted against expected literals (the live server's
    // known behavior): list → 200 {items:[],totalCount:0}; invite → 201 {id:uuid}
    // (fresh Guid per request, so the id is shape-checked); remove(guid) → 204 void;
    // remove(non-guid) → 404; updateRole → 204; health → 200 void; avatar → 404
    // (declared in the contract, not implemented by the sample controller).

    private const string SingleRunScript = """
        import { createApi, listMembers, inviteMember, removeMember, updateMemberRole, checkHealth, getAvatar } from "./dist/consumer.js";

        const baseUrl = process.argv[2];
        const api = createApi(baseUrl);

        const failures = [];

        // Normalize "empty body": openapi-fetch yields {} / "" / undefined depending
        // on content-length/parse behavior.
        function norm(body) {
          if (body === undefined || body === null || body === "") return null;
          if (typeof body === "object" && !Array.isArray(body) && Object.keys(body).length === 0) return null;
          return body;
        }

        function expectStatus(name, status, expected) {
          if (status !== expected) {
            failures.push(`${name}: expected status ${expected}, got ${status}`);
          }
        }

        function expectBody(name, body, expected) {
          if (JSON.stringify(norm(body)) !== JSON.stringify(expected)) {
            failures.push(`${name}: body mismatch — actual: ${JSON.stringify(norm(body))} expected: ${JSON.stringify(expected)}`);
          }
        }

        const isUuid = (s) => typeof s === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s);

        // ── 1. GET /api/members → 200 + empty PagedResult ──
        {
          const o = await listMembers(api);
          expectStatus("list", o.status, 200);
          expectBody("list", o.data, { items: [], totalCount: 0 });
        }

        // ── 2. POST /api/members → 201 + { id } (fresh Guid per request → shape) ──
        {
          // NB: wire shape matches the live server (Email round-trips as an object on
          // the wire in this sample); the client type surface brands it as string, so
          // the call site casts.
          const body = { email: { value: "single@example.com" }, role: "member", nickname: "single" };
          const o = await inviteMember(api, body);
          expectStatus("invite", o.status, 201);
          if (!isUuid(o.data?.id)) failures.push(`invite: id is not a uuid: ${JSON.stringify(o.data)}`);
        }

        // ── 3. DELETE /api/members/{id} (valid guid) → 204 void ──
        {
          const o = await removeMember(api, "a1b2c3d4-e5f6-4a7b-8c9d-000000000001");
          expectStatus("remove", o.status, 204);
          expectBody("remove", o.data ?? o.error, null);
        }

        // ── 4. DELETE /api/members/{id} (non-guid) → 404 error path ──
        {
          const o = await removeMember(api, "not-a-guid");
          expectStatus("remove-404", o.status, 404);
        }

        // ── 5. PUT /api/members/{id}/role → 204 ──
        {
          const o = await updateMemberRole(api, "a1b2c3d4-e5f6-4a7b-8c9d-000000000001", "viewer");
          expectStatus("updateRole", o.status, 204);
          expectBody("updateRole", o.data ?? o.error, null);
        }

        // ── 6. GET /api/health → 200 void ──
        {
          const o = await checkHealth(api);
          expectStatus("health", o.status, 200);
          expectBody("health", o.data ?? o.error, null);
        }

        // ── 7. GET /api/members/{id}/avatar?token=… (queryAuth file endpoint) ──
        // Declared in the contract but not implemented by the sample controller → 404.
        {
          const o = await getAvatar(api, "a1b2c3d4-e5f6-4a7b-8c9d-000000000001", "tok-123");
          expectStatus("avatar", o.status, 404);
        }

        if (failures.length > 0) {
          console.error(`OPENAPI-FETCH RUN MISMATCHES (${failures.length}):`);
          for (const f of failures) console.error(`  - ${f}`);
          process.exit(1);
        }

        console.log("Single-arm run: openapi-fetch produced the expected statuses and bodies across all 7 scenarios");
        """;

    // ═══════════════════════════ Process plumbing ═══════════════════════════

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Rivet.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not find repo root (Rivet.slnx)");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunNode(
        string script,
        string[] args
    )
    {
        if (!File.Exists(script))
        {
            throw new InvalidOperationException(
                $"Node tool not found: {script}. Run 'pnpm install' in {_jsDir} (see its README)."
            );
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = _jsDir,
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

        using var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start node {script}");

        // Drain both pipes concurrently — sequential ReadToEnd deadlocks when the
        // child fills the other pipe's buffer (same family as RunProcessAsync).
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        string arguments,
        string? workingDir = null,
        CancellationToken ct = default
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir ?? _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        SampleProjectTests.MakeBuildHermetic(psi);

        using var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");

        // Drain both pipes CONCURRENTLY: reading stdout to EOF before touching
        // stderr deadlocks when the child fills the stderr pipe buffer and blocks
        // on write — stdout then never EOFs (CliPipelineTests' flake, same family).
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var output = string.Join(
            "\n",
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s))
        );

        return (process.ExitCode, output);
    }

    private static async Task<AsyncServerHandle> StartSampleServer(string url, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"run --project \"{Path.Combine(_sampleDir, "ContractApi.csproj")}\" --urls {url}",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        SampleProjectTests.MakeBuildHermetic(psi);

        var process =
            Process.Start(psi)
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
                    $"Server did not start. Output:\n{output}\nStderr:\n{stderr}"
                );
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
