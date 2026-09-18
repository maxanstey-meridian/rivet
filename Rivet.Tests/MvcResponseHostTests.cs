using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// Real MVC response-fidelity evidence for the response-fidelity slice: the
/// AnnotationApi sample host boots and a plain-string POST action returns
/// 200 text/plain while a plain DTO action returns 200 application/json —
/// proving the emitted contract's status and media-type declarations describe
/// what the real host actually sends (acceptance:frontend-defaults).
/// In the Repository builds collection: it compiles the AnnotationApi sample
/// in-process via dotnet run, so it must not race the other build/run/publish
/// classes over shared obj assets.
/// </summary>
[Trait("Category", "Local")]
[Collection("Repository builds")]
public sealed class MvcResponseHostTests : IDisposable
{
    private static readonly string _repoRoot = FindRepoRoot();
    private static readonly string _sampleDir = Path.Combine(_repoRoot, "samples", "AnnotationApi");
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"rivet-mvc-response-{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Plain_String_Post_Returns_200_Text_Plain_As_Emitted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await StartAnnotationApiServer(cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };

        // The live host's actual wire behavior for a plain string action. The
        // request side uses the JSON string "hello contract" because MVC registers
        // no text/plain input formatter by default (a text/plain body is a diagnosed
        // 415); the RESPONSE side is the evidence under test — the string formatter
        // really writes 200 text/plain.
        using var payload = new StringContent(
            "\"hello contract\"",
            Encoding.UTF8,
            "application/json"
        );
        var response = await http.PostAsync("/api/tasks/echo", payload, cts.Token);

        // The host truth the emitted contract must agree with: status AND media type.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Equal("hello contract", body);
    }

    [Fact]
    public async Task Plain_Dto_Post_Returns_200_Json_As_Emitted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await StartAnnotationApiServer(cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };

        using var payload = new StringContent(
            """{"body":"label text"}""",
            Encoding.UTF8,
            "application/json"
        );
        var response = await http.PostAsync("/api/tasks/labels", payload, cts.Token);

        // The host truth: default JSON formatter, 200 OK.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        Assert.Equal("label text", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Emitted_Contract_Agrees_With_Live_Host_Status_And_Media_Type()
    {
        // Extraction over the real sample source must emit the echo endpoint with
        // the host-truthful 200 + text/plain surface (and labels with default JSON),
        // matching what the live-host tests above prove the host actually sends.
        var outputDir = Path.Combine(_tempDir, "spec");
        var (exitCode, output) = await RunProcessAsync(
            "dotnet",
            $"run --project \"{Path.Combine(_repoRoot, "Rivet.Tool")}\" -- "
                + $"--project \"{Path.Combine(_sampleDir, "AnnotationApi.csproj")}\" "
                + $"--output \"{outputDir}\"",
            ct: default
        );
        Assert.True(exitCode == 0, $"rivet emission failed:\n{output}");

        var specPath = Path.Combine(outputDir, "openapi.json");
        Assert.True(File.Exists(specPath), $"emitted spec missing: {specPath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(specPath));
        var paths = doc.RootElement.GetProperty("paths");

        // Plain-string POST: 200 + text/plain — both status AND media type agree.
        var echo = paths.GetProperty("/api/tasks/echo").GetProperty("post");
        Assert.True(echo.TryGetProperty("responses", out var echoResponses));
        Assert.True(echoResponses.TryGetProperty("200", out var echoResponse));
        Assert.True(echoResponse.TryGetProperty("content", out var echoContent));
        Assert.True(echoContent.TryGetProperty("text/plain", out _));
        Assert.False(echoContent.TryGetProperty("application/json", out _));

        // Plain DTO POST: default 200 + application/json.
        var labels = paths.GetProperty("/api/tasks/labels").GetProperty("post");
        Assert.True(labels.TryGetProperty("responses", out var labelsResponses));
        Assert.True(labelsResponses.TryGetProperty("200", out var labelsResponse));
        Assert.True(labelsResponse.TryGetProperty("content", out var labelsContent));
        Assert.True(labelsContent.TryGetProperty("application/json", out _));
        Assert.False(labelsContent.TryGetProperty("text/plain", out _));
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

    private static async Task<AsyncServerHandle> StartAnnotationApiServer(CancellationToken ct)
    {
        // Port 0 asks the OS for a free ephemeral port: two live-host proofs (or an
        // unrelated process) can no longer collide on a pre-drawn random port and
        // kill Kestrel before the test asserts anything. The actually bound URL is
        // read back from the "Now listening on:" line below.
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"run --project \"{Path.Combine(_sampleDir, "AnnotationApi.csproj")}\" --urls http://127.0.0.1:0",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        MakeBuildHermetic(psi);

        var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start AnnotationApi server");

        const string listeningMarker = "Now listening on:";
        string? boundUrl = null;
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

                var markerIndex = line.IndexOf(listeningMarker, StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    boundUrl = line[(markerIndex + listeningMarker.Length)..].Trim();
                    break;
                }
            }

            if (boundUrl is null)
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

        return new AsyncServerHandle(process, boundUrl);
    }

    private sealed class AsyncServerHandle(Process process, string boundUrl) : IAsyncDisposable
    {
        // The URL the server actually bound — for --urls http://127.0.0.1:0 the OS
        // assigns a free ephemeral port.
        public string Url { get; } = boundUrl;

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
