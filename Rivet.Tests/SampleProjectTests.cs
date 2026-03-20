using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Integration tests that build the sample ContractApi project, boot it,
/// and verify the generated TypeScript client works with mocked fetch.
/// </summary>
[Trait("Category", "Local")]
public sealed class SampleProjectTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SampleDir = Path.Combine(RepoRoot, "samples", "ContractApi");
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"rivet-sample-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ========== Tier 1: Build + Roslyn round-trip ==========

    [Fact]
    public async Task SampleProject_Builds()
    {
        var (exitCode, output) = await RunProcessAsync(
            "dotnet", $"build \"{Path.Combine(SampleDir, "ContractApi.csproj")}\" --verbosity quiet");

        Assert.True(exitCode == 0, $"dotnet build failed:\n{output}");
    }

    [Fact]
    public async Task ImportDemo_Builds()
    {
        var importDemoDir = Path.Combine(RepoRoot, "samples", "ImportDemo");
        var (exitCode, output) = await RunProcessAsync(
            "dotnet", $"build \"{Path.Combine(importDemoDir, "ImportDemo.csproj")}\" --verbosity quiet");

        Assert.True(exitCode == 0, $"ImportDemo build failed:\n{output}");
    }

    [Fact]
    public void SampleProject_Contracts_Survive_Roslyn_RoundTrip()
    {
        var sources = ReadSampleSources();
        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);

        Assert.Equal(5, endpoints.Count);

        // List: GET /api/members → List<MemberDto>
        var list = Assert.Single(endpoints, e => e.Name == "list");
        Assert.Equal("GET", list.HttpMethod);
        Assert.Equal("/api/members", list.RouteTemplate);
        Assert.NotNull(list.ReturnType);

        // Invite: POST /api/members → InviteMemberResponse (201)
        var invite = Assert.Single(endpoints, e => e.Name == "invite");
        Assert.Equal("POST", invite.HttpMethod);
        Assert.Equal("/api/members", invite.RouteTemplate);
        Assert.NotNull(invite.ReturnType);
        Assert.Contains(invite.Params, p => p.Source == ParamSource.Body);
        Assert.Contains(invite.Responses, r => r.StatusCode == 422);

        // Remove: DELETE /api/members/{id} → void
        var remove = Assert.Single(endpoints, e => e.Name == "remove");
        Assert.Equal("DELETE", remove.HttpMethod);
        Assert.Equal("/api/members/{id}", remove.RouteTemplate);
        Assert.Null(remove.ReturnType);
        Assert.Contains(remove.Params, p => p.Name == "id" && p.Source == ParamSource.Route);
        Assert.Contains(remove.Responses, r => r.StatusCode == 404);

        // UpdateRole: PUT /api/members/{id}/role → void (204)
        var updateRole = Assert.Single(endpoints, e => e.Name == "updateRole");
        Assert.Equal("PUT", updateRole.HttpMethod);
        Assert.Equal("/api/members/{id}/role", updateRole.RouteTemplate);
        Assert.Null(updateRole.ReturnType);
        Assert.Contains(updateRole.Params, p => p.Name == "id" && p.Source == ParamSource.Route);
        Assert.Contains(updateRole.Params, p => p.Source == ParamSource.Body);

        // Health: GET /api/health → void
        var health = Assert.Single(endpoints, e => e.Name == "health");
        Assert.Equal("GET", health.HttpMethod);
        Assert.Equal("/api/health", health.RouteTemplate);
        Assert.Null(health.ReturnType);
    }

    // ========== Tier 2: API serves correct responses ==========

    [Fact]
    public async Task SampleProject_Api_Endpoints_Respond_Correctly()
    {
        var port = Random.Shared.Next(49152, 65000);
        var url = $"http://localhost:{port}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await StartSampleServer(url, cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri(url) };

        // GET /api/members → 200 + JSON object (PagedResult)
        var listResponse = await http.GetAsync("/api/members", cts.Token);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadAsStringAsync(cts.Token);
        Assert.StartsWith("{", listBody);
        Assert.Contains("\"items\"", listBody);

        // POST /api/members → 201 + JSON with Id
        var invitePayload = new StringContent(
            """{"email":{"value":"test@example.com"},"role":"admin","nickname":"tester"}""",
            Encoding.UTF8, "application/json");
        var inviteResponse = await http.PostAsync("/api/members", invitePayload, cts.Token);
        Assert.Equal(HttpStatusCode.Created, inviteResponse.StatusCode);
        var inviteBody = await inviteResponse.Content.ReadAsStringAsync(cts.Token);
        using var inviteDoc = JsonDocument.Parse(inviteBody);
        Assert.True(inviteDoc.RootElement.TryGetProperty("id", out _));

        // DELETE /api/members/{id} → 200
        var deleteResponse = await http.DeleteAsync($"/api/members/{Guid.NewGuid()}", cts.Token);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // PUT /api/members/{id}/role → 204
        var updatePayload = new StringContent(
            """{"role":"viewer"}""", Encoding.UTF8, "application/json");
        var updateResponse = await http.PutAsync($"/api/members/{Guid.NewGuid()}/role", updatePayload, cts.Token);
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);
    }

    // ========== Tier 3: Generated TS client with mocked fetch ==========

    [Fact]
    public async Task SampleProject_GeneratedTsClient_Works_With_MockedFetch()
    {
        // 1. Generate TS from sample source
        var sources = ReadSampleSources();
        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contractEndpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);

        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(
            definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();

        // 2. Write generated files to temp directory
        Directory.CreateDirectory(_tempDir);

        var typesDir = Path.Combine(_tempDir, "types");
        Directory.CreateDirectory(typesDir);

        foreach (var group in typeGrouping.Groups)
        {
            var content = TypeEmitter.EmitGroupFile(group);
            await File.WriteAllTextAsync(Path.Combine(typesDir, $"{group.FileName}.ts"), content);
        }

        var typeFileNames = typeGrouping.Groups.Select(g => g.FileName).ToList();
        await File.WriteAllTextAsync(
            Path.Combine(typesDir, "index.ts"), TypeEmitter.EmitNamespacedBarrel(typeFileNames));

        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "rivet.ts"), ClientEmitter.EmitRivetBase());

        var clientDir = Path.Combine(_tempDir, "client");
        Directory.CreateDirectory(clientDir);

        var controllerGroups = ClientEmitter.GroupByController(contractEndpoints);
        var clientFileNames = new List<string>();
        foreach (var (controllerName, groupEndpoints) in controllerGroups)
        {
            var clientContent = ClientEmitter.EmitControllerClient(
                controllerName, groupEndpoints, typeFileMap);
            await File.WriteAllTextAsync(
                Path.Combine(clientDir, $"{controllerName}.ts"), clientContent);
            clientFileNames.Add(controllerName);
        }

        await File.WriteAllTextAsync(
            Path.Combine(clientDir, "index.ts"), TypeEmitter.EmitNamespacedBarrel(clientFileNames));

        // 3. Write tsconfig that emits JS (not --noEmit)
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "tsconfig.json"), """
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
              "exclude": ["node_modules"]
            }
            """);

        // 4. Compile TS → JS
        var (tscExit, tscOutput) = await RunProcessAsync(
            "npx", $"--yes tsc --project \"{Path.Combine(_tempDir, "tsconfig.json")}\"",
            workingDir: _tempDir);
        Assert.True(tscExit == 0, $"tsc failed:\n{tscOutput}");

        // 5. Write test script with mocked fetch and static fixture responses
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "test.mjs"), """
            // Mock fetch with static responses matching contract shapes
            globalThis.fetch = async (url, opts) => {
              const path = new URL(url).pathname;
              const method = (opts?.method ?? "GET").toUpperCase();

              const json = (data, status = 200) => new Response(
                JSON.stringify(data),
                { status, headers: { "content-type": "application/json" } }
              );

              if (method === "GET" && path === "/api/members") {
                return json([
                  { id: "a1b2c3d4-0000-0000-0000-000000000001", name: "Alice", email: { value: "alice@example.com" }, role: "admin" },
                  { id: "a1b2c3d4-0000-0000-0000-000000000002", name: "Bob", email: { value: "bob@example.com" }, role: "member" },
                ]);
              }

              if (method === "POST" && path === "/api/members") {
                return json({ id: "a1b2c3d4-0000-0000-0000-000000000099" }, 201);
              }

              if (method === "DELETE" && path.startsWith("/api/members/")) {
                return new Response(null, { status: 200 });
              }

              if (method === "PUT" && path.match(/^\/api\/members\/[^/]+\/role$/)) {
                return new Response(null, { status: 204 });
              }

              if (method === "GET" && path === "/api/health") {
                return new Response(null, { status: 200 });
              }

              throw new Error(`Unmocked request: ${method} ${path}`);
            };

            // Import generated client
            const { configureRivet } = await import("./dist/rivet.js");
            const client = await import("./dist/client/members.js");

            configureRivet({ baseUrl: "http://localhost:9999" });

            // Test list()
            const members = await client.list();
            assert(Array.isArray(members), "list() should return an array");
            assert(members.length === 2, "list() should return 2 members");
            assert(members[0].name === "Alice", "first member should be Alice");
            assert(members[0].email.value === "alice@example.com", "first member email should match");

            // Test invite()
            const invited = await client.invite({ email: { value: "new@example.com" }, role: "member", nickname: "newbie" });
            assert(invited.id === "a1b2c3d4-0000-0000-0000-000000000099", "invite() should return the new member id");

            // Test remove() — void endpoint
            const removeResult = await client.remove("some-id");
            assert(removeResult === undefined, "remove() should return undefined");

            // Test updateRole() — void endpoint, 204
            const updateResult = await client.updateRole("some-id", { role: "viewer" });
            assert(updateResult === undefined, "updateRole() should return undefined");

            // Test health() — void endpoint
            const healthResult = await client.health();
            assert(healthResult === undefined, "health() should return undefined");

            console.log("All client tests passed");

            function assert(condition, message) {
              if (!condition) {
                throw new Error(`Assertion failed: ${message}`);
              }
            }
            """);

        // 6. Run the test script
        var (nodeExit, nodeOutput) = await RunProcessAsync(
            "node", $"\"{Path.Combine(_tempDir, "test.mjs")}\"",
            workingDir: _tempDir);

        Assert.True(nodeExit == 0, $"Node test failed:\n{nodeOutput}");
    }

    // ========== Tier 4: Zod-validated client with mocked fetch ==========

    [Fact]
    public async Task SampleProject_ZodValidatedClient_Validates_Responses()
    {
        var jsDir = Path.Combine(RepoRoot, "Rivet.Tests", "js");
        Assert.True(
            File.Exists(Path.Combine(jsDir, "node_modules", "zod", "package.json")),
            "Zod not installed — run 'npm install' in Rivet.Tests/js/");

        // 1. Generate TS from sample source
        var sources = ReadSampleSources();
        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contractEndpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);

        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(
            definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();

        // 2. Write generated files to temp directory
        Directory.CreateDirectory(_tempDir);

        var typesDir = Path.Combine(_tempDir, "types");
        Directory.CreateDirectory(typesDir);

        foreach (var group in typeGrouping.Groups)
        {
            var content = TypeEmitter.EmitGroupFile(group);
            await File.WriteAllTextAsync(Path.Combine(typesDir, $"{group.FileName}.ts"), content);
        }

        var typeFileNames = typeGrouping.Groups.Select(g => g.FileName).ToList();
        await File.WriteAllTextAsync(
            Path.Combine(typesDir, "index.ts"), TypeEmitter.EmitNamespacedBarrel(typeFileNames));

        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "rivet.ts"), ClientEmitter.EmitRivetBase());

        // 3. Emit schemas.ts + zod validators.ts
        var schemasOutput = JsonSchemaEmitter.Emit(walker.Definitions, walker.Brands, walker.Enums, contractEndpoints);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "schemas.ts"), schemasOutput);

        var zodValidators = ZodValidatorEmitter.Emit(contractEndpoints, typeFileMap);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "validators.ts"), zodValidators);

        // 4. Emit zod-validated clients
        var clientDir = Path.Combine(_tempDir, "client");
        Directory.CreateDirectory(clientDir);

        var controllerGroups = ClientEmitter.GroupByController(contractEndpoints);
        var clientFileNames = new List<string>();
        foreach (var (controllerName, groupEndpoints) in controllerGroups)
        {
            var clientContent = ClientEmitter.EmitControllerClient(
                controllerName, groupEndpoints, typeFileMap, ValidateMode.Zod);
            await File.WriteAllTextAsync(
                Path.Combine(clientDir, $"{controllerName}.ts"), clientContent);
            clientFileNames.Add(controllerName);
        }

        await File.WriteAllTextAsync(
            Path.Combine(clientDir, "index.ts"), TypeEmitter.EmitNamespacedBarrel(clientFileNames));

        // 5. tsconfig + node_modules symlink for zod
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "tsconfig.json"), """
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
              "exclude": ["node_modules"]
            }
            """);

        // Symlink node_modules from Rivet.Tests/js so zod is available
        var nodeModulesLink = Path.Combine(_tempDir, "node_modules");
        var nodeModulesTarget = Path.Combine(jsDir, "node_modules");
        Directory.CreateSymbolicLink(nodeModulesLink, nodeModulesTarget);

        // 6. Compile TS → JS
        var (tscExit, tscOutput) = await RunProcessAsync(
            "npx", $"--yes tsc --project \"{Path.Combine(_tempDir, "tsconfig.json")}\"",
            workingDir: _tempDir);
        Assert.True(tscExit == 0, $"tsc failed:\n{tscOutput}");

        // 7. Test script: valid response passes, invalid response throws
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "test.mjs"), $$"""
            globalThis.fetch = async (url, opts) => {
              const path = new URL(url).pathname;
              const method = (opts?.method ?? "GET").toUpperCase();

              const json = (data, status = 200) => new Response(
                JSON.stringify(data),
                { status, headers: { "content-type": "application/json" } }
              );

              // Valid response (PagedResult)
              if (method === "GET" && path === "/api/members") {
                return json({
                  items: [
                    { id: "a1b2c3d4-0000-0000-0000-000000000001", name: "Alice", email: "alice@example.com", role: "admin" },
                  ],
                  totalCount: 1,
                });
              }

              // Valid 201 response
              if (method === "POST" && path === "/api/members") {
                return json({ id: "a1b2c3d4-0000-0000-0000-000000000099" }, 201);
              }

              // Invalid response — totalCount should be number, not string
              if (method === "GET" && path === "/api/members/bad") {
                return json({ items: [{ id: 123, name: 456 }], totalCount: "not-a-number" });
              }

              // Void endpoints
              if (method === "DELETE" && path.startsWith("/api/members/")) {
                return new Response(null, { status: 200 });
              }

              throw new Error(`Unmocked: ${method} ${path}`);
            };

            const { configureRivet } = await import("./dist/rivet.js");
            const client = await import("./dist/client/members.js");

            configureRivet({ baseUrl: "http://localhost:9999" });

            // Valid data passes validation (PagedResult)
            const result = await client.list();
            assert(result.items !== undefined, "list() should return a PagedResult with items");
            assert(result.items[0].name === "Alice", "first member should be Alice");
            assert(result.totalCount === 1, "totalCount should be 1");

            // Valid invite passes
            const invited = await client.invite({ email: "new@example.com", role: "member", nickname: "newbie" });
            assert(invited.id === "a1b2c3d4-0000-0000-0000-000000000099", "invite() should return id");

            // Void endpoint — no validation, should work
            const removeResult = await client.remove("some-id");
            assert(removeResult === undefined, "remove() should return undefined");

            console.log("All zod-validated client tests passed");

            function assert(condition, message) {
              if (!condition) {
                throw new Error(`Assertion failed: ${message}`);
              }
            }
            """);

        // 8. Run
        var (nodeExit, nodeOutput) = await RunProcessAsync(
            "node", $"\"{Path.Combine(_tempDir, "test.mjs")}\"",
            workingDir: _tempDir);

        Assert.True(nodeExit == 0, $"Zod validated client test failed:\n{nodeOutput}");
    }

    // ========== rivetFetch response handling ==========

    [Fact]
    public async Task RivetFetch_Handles_All_Response_Variants()
    {
        Directory.CreateDirectory(_tempDir);

        // Write rivet.ts and compile it
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "rivet.ts"), ClientEmitter.EmitRivetBase());

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "tsconfig.json"), """
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
              "exclude": ["node_modules"]
            }
            """);

        var (tscExit, tscOutput) = await RunProcessAsync(
            "npx", $"--yes tsc --project \"{Path.Combine(_tempDir, "tsconfig.json")}\"",
            workingDir: _tempDir);
        Assert.True(tscExit == 0, $"tsc failed:\n{tscOutput}");

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "test.mjs"), """
            import { configureRivet, rivetFetch } from "./dist/rivet.js";

            let mockResponse;
            globalThis.fetch = async () => mockResponse;

            configureRivet({ baseUrl: "http://localhost:9999" });

            function mock(body, status, contentType) {
              const headers = new Headers();
              if (contentType) headers.set("content-type", contentType);
              mockResponse = new Response(body, { status, headers });
            }

            function assert(condition, message) {
              if (!condition) throw new Error(`FAIL: ${message}`);
            }

            // --- Success path variants ---

            // 200 + JSON body
            mock(JSON.stringify({ id: 1 }), 200, "application/json");
            const jsonResult = await rivetFetch("GET", "/test");
            assert(jsonResult.id === 1, "200+JSON should parse body");

            // 200 + empty body (void endpoint returning 200, no content-type)
            mock(null, 200, null);
            const emptyResult = await rivetFetch("GET", "/test");
            assert(emptyResult === undefined, `200+empty should return undefined, got: ${JSON.stringify(emptyResult)}`);

            // 200 + empty string body with application/json content-type
            mock("", 200, "application/json");
            const emptyJsonResult = await rivetFetch("GET", "/test");
            assert(emptyJsonResult === undefined, `200+empty+json ct should return undefined, got: ${JSON.stringify(emptyJsonResult)}`);

            // 201 + JSON body
            mock(JSON.stringify({ id: "new" }), 201, "application/json");
            const createdResult = await rivetFetch("POST", "/test");
            assert(createdResult.id === "new", "201+JSON should parse body");

            // 204 No Content
            mock(null, 204, null);
            const noContentResult = await rivetFetch("DELETE", "/test");
            assert(noContentResult === undefined, "204 should return undefined");

            // 200 + plain text body
            mock("OK", 200, "text/plain");
            const textResult = await rivetFetch("GET", "/test");
            assert(textResult === "OK", `200+text should return text, got: ${JSON.stringify(textResult)}`);

            // --- unwrap: false variants ---

            // unwrap: false + 200 + JSON
            mock(JSON.stringify({ name: "test" }), 200, "application/json");
            const unwrappedJson = await rivetFetch("GET", "/test", { unwrap: false });
            assert(unwrappedJson.status === 200, "unwrap:false should include status");
            assert(unwrappedJson.data.name === "test", "unwrap:false should include parsed data");
            assert(unwrappedJson.response instanceof Response, "unwrap:false should include response");

            // unwrap: false + 200 + empty body
            mock(null, 200, null);
            const unwrappedEmpty = await rivetFetch("GET", "/test", { unwrap: false });
            assert(unwrappedEmpty.status === 200, "unwrap:false+empty status");
            assert(unwrappedEmpty.data === undefined, "unwrap:false+empty data should be undefined");

            // unwrap: false + 404 + JSON error body
            mock(JSON.stringify({ message: "not found" }), 404, "application/json");
            const unwrapped404 = await rivetFetch("GET", "/test", { unwrap: false });
            assert(unwrapped404.status === 404, "unwrap:false+404 status");
            assert(unwrapped404.data.message === "not found", "unwrap:false+404 should include error body");

            // --- Error path (unwrap: true / default) ---

            // 500 + JSON error → should throw RivetError
            mock(JSON.stringify({ error: "boom" }), 500, "application/json");
            try {
              await rivetFetch("GET", "/test");
              assert(false, "500 should throw");
            } catch (e) {
              assert(e.name === "RivetError", `should be RivetError, got: ${e.name}`);
              assert(e.status === 500, "RivetError should have status 500");
              assert(e.body.error === "boom", "RivetError should include parsed body");
            }

            // 404 + empty body → should throw RivetError
            mock(null, 404, null);
            try {
              await rivetFetch("GET", "/test");
              assert(false, "404 should throw");
            } catch (e) {
              assert(e.name === "RivetError", "404 empty should throw RivetError");
              assert(e.status === 404, "RivetError should have status 404");
            }

            // Network error (fetch throws) → should throw RivetError
            mockResponse = null;
            globalThis.fetch = async () => { throw new TypeError("fetch failed"); };
            try {
              await rivetFetch("GET", "/test");
              assert(false, "network error should throw");
            } catch (e) {
              assert(e.name === "RivetError", "network error should throw RivetError");
              assert(e.status === 0, "network error should have status 0");
            }

            console.log("All rivetFetch variant tests passed");
            """);

        var (nodeExit, nodeOutput) = await RunProcessAsync(
            "node", $"\"{Path.Combine(_tempDir, "test.mjs")}\"",
            workingDir: _tempDir);

        Assert.True(nodeExit == 0, $"rivetFetch variant tests failed:\n{nodeOutput}");
    }

    // ========== Helpers ==========

    /// <summary>
    /// Reads the sample source files needed for Roslyn analysis.
    /// Prepends implicit usings since the sample project uses &lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;
    /// but CompilationHelper doesn't add those automatically.
    /// The controller file is excluded — ContractWalker only needs the contract and its referenced types.
    /// </summary>
    private static string[] ReadSampleSources()
    {
        const string implicitUsings = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;

            """;

        // The controller file contains both DTOs and the controller class.
        // The controller class uses ASP.NET types (ObjectResult, StatusCodeResult, Results)
        // that aren't fully stubbed in CompilationHelper. We only need the DTOs for
        // ContractWalker, so extract everything up to the controller class declaration.
        var controllerSource = File.ReadAllText(
            Path.Combine(SampleDir, "Controllers", "MembersController.cs"));
        var controllerClassIndex = controllerSource.IndexOf("[Route(", StringComparison.Ordinal);
        var dtosOnly = controllerClassIndex > 0
            ? controllerSource[..controllerClassIndex]
            : controllerSource;

        return
        [
            implicitUsings + File.ReadAllText(Path.Combine(SampleDir, "Domain", "ValueObjects.cs")),
            implicitUsings + dtosOnly,
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

        // Wait for the server to start listening
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
