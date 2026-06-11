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
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        Assert.Equal(6, endpoints.Count);

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

        // Avatar: GET /api/members/{id}/avatar → file (QueryAuth)
        var avatar = Assert.Single(endpoints, e => e.Name == "avatar");
        Assert.Equal("GET", avatar.HttpMethod);
        Assert.Equal("/api/members/{id}/avatar", avatar.RouteTemplate);
        Assert.True(avatar.IsFileEndpoint);
        Assert.Equal("image/jpeg", avatar.FileContentType);
        Assert.NotNull(avatar.QueryAuth);
        Assert.Equal("token", avatar.QueryAuth!.ParameterName);

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

        // DELETE /api/members/{id} → 204 (A1 policy: DELETE without output defaults to 204)
        var deleteResponse = await http.DeleteAsync($"/api/members/{Guid.NewGuid()}", cts.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // PUT /api/members/{id}/role → 204
        var updatePayload = new StringContent(
            """{"role":"viewer"}""", Encoding.UTF8, "application/json");
        var updateResponse = await http.PutAsync($"/api/members/{Guid.NewGuid()}/role", updatePayload, cts.Token);
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);
    }

    // ========== Tier 3: Generated TS client with mocked fetch ==========

    // ========== Tier 4: Zod-validated client with mocked fetch ==========

    // ========== rivetFetch response handling ==========

    // ========== rivetFetch config options ==========

    // ========== rivetFetch query param null handling ==========

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
