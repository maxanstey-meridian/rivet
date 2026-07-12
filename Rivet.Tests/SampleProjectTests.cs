using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Integration tests that build the sample ContractApi project, boot it,
/// and verify the generated TypeScript client works with mocked fetch.
/// </summary>
[Trait("Category", "Local")]
public sealed class SampleProjectTests : IDisposable
{
    private static readonly string _repoRoot = FindRepoRoot();
    private static readonly string _sampleDir = Path.Combine(_repoRoot, "samples", "ContractApi");
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"rivet-sample-test-{Guid.NewGuid():N}"
    );

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
            "dotnet",
            $"build \"{Path.Combine(_sampleDir, "ContractApi.csproj")}\" --verbosity quiet"
        );

        Assert.True(exitCode == 0, $"dotnet build failed:\n{output}");
    }

    [Fact]
    public async Task ImportDemo_Builds()
    {
        var importDemoDir = Path.Combine(_repoRoot, "samples", "ImportDemo");
        var (exitCode, output) = await RunProcessAsync(
            "dotnet",
            $"build \"{Path.Combine(importDemoDir, "ImportDemo.csproj")}\" --verbosity quiet"
        );

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
            Encoding.UTF8,
            "application/json"
        );
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
            """{"role":"viewer"}""",
            Encoding.UTF8,
            "application/json"
        );
        var updateResponse = await http.PutAsync(
            $"/api/members/{Guid.NewGuid()}/role",
            updatePayload,
            cts.Token
        );
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);
    }

    [Fact]
    public async Task SampleProject_Api_Rejects_Invalid_Requests_With_422_ValidationErrorDto()
    {
        var port = Random.Shared.Next(49152, 65000);
        var url = $"http://localhost:{port}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await StartSampleServer(url, cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri(url) };

        // (a) DataAnnotations violation: role/nickname too short → 422
        // ValidationErrorDto with field errors; handler not invoked (no id, no 201).
        var invalidPayload = new StringContent(
            """{"email":{"value":"test@example.com"},"role":"x","nickname":"z"}""",
            Encoding.UTF8,
            "application/json"
        );
        var invalidResponse = await http.PostAsync("/api/members", invalidPayload, cts.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);
        using (
            var doc = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync(cts.Token))
        )
        {
            Assert.Equal("Validation failed", doc.RootElement.GetProperty("message").GetString());
            var errors = doc.RootElement.GetProperty("errors");
            Assert.True(errors.TryGetProperty("role", out var roleErrors));
            Assert.True(roleErrors.GetArrayLength() > 0);
            Assert.True(errors.TryGetProperty("nickname", out _));
            Assert.False(doc.RootElement.TryGetProperty("id", out _));
        }

        // (b) [RivetConstraints] facet violation (UniqueItems on tags) → 422,
        // proving the ValidationAttribute participates in MVC model validation.
        var duplicateTagsPayload = new StringContent(
            """{"email":{"value":"test@example.com"},"role":"admin","nickname":"tester","tags":["a","a"]}""",
            Encoding.UTF8,
            "application/json"
        );
        var duplicateTagsResponse = await http.PostAsync(
            "/api/members",
            duplicateTagsPayload,
            cts.Token
        );
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicateTagsResponse.StatusCode);
        using (
            var doc = JsonDocument.Parse(
                await duplicateTagsResponse.Content.ReadAsStringAsync(cts.Token)
            )
        )
        {
            Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty("tags", out _));
        }

        // (b continued) MaxItems = 5 violated → 422.
        var tooManyTagsPayload = new StringContent(
            """{"email":{"value":"test@example.com"},"role":"admin","nickname":"tester","tags":["a","b","c","d","e","f"]}""",
            Encoding.UTF8,
            "application/json"
        );
        var tooManyTagsResponse = await http.PostAsync(
            "/api/members",
            tooManyTagsPayload,
            cts.Token
        );
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooManyTagsResponse.StatusCode);

        // (c) Valid request (with tags) still succeeds → 201 + id.
        var validPayload = new StringContent(
            """{"email":{"value":"test@example.com"},"role":"admin","nickname":"tester","tags":["a","b"]}""",
            Encoding.UTF8,
            "application/json"
        );
        var validResponse = await http.PostAsync("/api/members", validPayload, cts.Token);
        Assert.Equal(HttpStatusCode.Created, validResponse.StatusCode);
        using (
            var doc = JsonDocument.Parse(await validResponse.Content.ReadAsStringAsync(cts.Token))
        )
        {
            Assert.True(doc.RootElement.TryGetProperty("id", out _));
        }
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
            implicitUsings
                + File.ReadAllText(Path.Combine(_sampleDir, "Domain", "ValueObjects.cs")),
            implicitUsings
                + File.ReadAllText(Path.Combine(_sampleDir, "Models", "MemberModels.cs")),
            implicitUsings
                + File.ReadAllText(
                    Path.Combine(_sampleDir, "Contracts", "Members", "MembersContract.cs")
                ),
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

    /// <summary>
    /// Child dotnet builds must not share MSBuild node-reuse workers or the Roslyn
    /// compiler server with the outer `dotnet test` (itself an MSBuild build) —
    /// sharing them deadlocked the testhost under load (hangdumps fingered
    /// SampleProject_Builds / ImportDemo_Builds, which pass instantly in isolation).
    /// MSBuild reads environment variables as global properties, so this covers
    /// `dotnet build` and `dotnet run` alike without argument-parsing risk.
    /// </summary>
    internal static void MakeBuildHermetic(ProcessStartInfo psi)
    {
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["UseSharedCompilation"] = "false";
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
        MakeBuildHermetic(psi);

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
        MakeBuildHermetic(psi);

        var process =
            Process.Start(psi)
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
