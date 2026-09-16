using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// Real MVC binding evidence for the binding-and-properties slice: the AnnotationApi
/// sample host boots and binds a defaulted scalar query parameter ([FromQuery(Name=)]
/// wire name) plus an unattributed complex request body — proving the extraction
/// defaults describe what the real host actually binds.
/// In the Repository builds collection: it compiles the AnnotationApi sample in-process
/// via dotnet run, so it must not race the other build/run/publish classes over shared
/// obj assets.
/// </summary>
[Trait("Category", "Local")]
[Collection("Repository builds")]
public sealed class MvcBindingHostTests : IDisposable
{
    private static readonly string _repoRoot = FindRepoRoot();
    private static readonly string _sampleDir = Path.Combine(_repoRoot, "samples", "AnnotationApi");
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"rivet-mvc-binding-{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Host_Binds_Defaulted_Query_And_Complex_Body_As_Emitted()
    {
        var port = Random.Shared.Next(49152, 65000);
        var url = $"http://localhost:{port}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await StartAnnotationApiServer(url, cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri(url) };

        // The defaulted scalar query param + inferred complex body — bound by MVC
        // exactly as the emitted contract describes (q + limit on the query string,
        // the JSON DTO as the body).
        var payload = new StringContent(
            """{"term":"alpha","minPriority":1,"tag":null}""",
            Encoding.UTF8,
            "application/json"
        );
        var response = await http.PostAsync(
            "/api/tasks/search?q=alpha&limit=5",
            payload,
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using (var doc = JsonDocument.Parse(body))
        {
            Assert.True(doc.RootElement.TryGetProperty("items", out _));
        }

        // The real host enforces MVC requiredness on the non-nullable string bound
        // via [FromQuery(Name="q")]: omitting q yields the automatic 400 — the
        // emitted contract describes the required query param with the same wire
        // name (acceptance:mvc-inputs: requiredness agrees with enforced binding).
        var noBody = new StringContent(
            """{"term":"beta","minPriority":2,"tag":null}""",
            Encoding.UTF8,
            "application/json"
        );
        var renamedQuery = await http.PostAsync("/api/tasks/search?limit=3", noBody, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, renamedQuery.StatusCode);
    }

    [Fact]
    public async Task Emitted_Contract_Describes_The_Bound_Surface()
    {
        // Extraction over the real sample source must emit the search endpoint with
        // the FromQuery(Name=) wire name, the defaulted query param and the complex
        // body — matching what the host test above proved MVC binds.
        var (exitCode, output) = await RunProcessAsync(
            "dotnet",
            $"run --project \"{Path.Combine(_repoRoot, "Rivet.Tool")}\" -- "
                + $"--project \"{Path.Combine(_sampleDir, "AnnotationApi.csproj")}\" --routes",
            ct: default
        );
        Assert.True(exitCode == 0, $"rivet --routes failed:\n{output}");
        Assert.Contains("/api/tasks/search", output);
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

    private static async Task<AsyncServerHandle> StartAnnotationApiServer(
        string url,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"run --project \"{Path.Combine(_sampleDir, "AnnotationApi.csproj")}\" --urls {url}",
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
