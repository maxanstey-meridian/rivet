using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rivet.Tests;

/// <summary>
/// Runs real processes for the disk-true tests: the built Rivet.Tool CLI, child
/// dotnet builds and hosts, the vendored node tooling and the Python round-trip diff.
/// </summary>
internal static class CliRunner
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ToolDllPath => Path.Combine(AppContext.BaseDirectory, "Rivet.Tool.dll");

    public static string RepoPath(params string[] segments) =>
        Path.Combine([RepoRoot, .. segments]);

    public static (int ExitCode, string StdOut, string StdErr) RunCli(
        string workingDirectory,
        IReadOnlyList<string> args
    ) => Run(workingDirectory, "dotnet", ["exec", ToolDllPath, .. args]);

    public static (int ExitCode, string StdOut, string StdErr) Run(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> args
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdOut.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdErr.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not exit within 5 minutes.");
        }

        // The timeout overload returns on process exit WITHOUT draining the async
        // readers — the last buffered lines can land after we read the builders.
        // The parameterless overload waits for stream EOF.
        process.WaitForExit();

        return (process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    /// <summary>Runs a script from the vendored <c>Rivet.Tests/js</c> node tooling.</summary>
    public static (int ExitCode, string StdOut, string StdErr) RunNode(
        string script,
        IReadOnlyList<string> args
    )
    {
        var jsDirectory = RepoPath("Rivet.Tests", "js");
        if (!File.Exists(script))
        {
            throw new InvalidOperationException(
                $"Node tool not found: {script}. Run 'pnpm install' in {jsDirectory} (see its README)."
            );
        }

        return Run(jsDirectory, "node", [script, .. args]);
    }

    /// <summary>
    /// Runs a child process (usually a dotnet build, run, pack or publish) and returns
    /// its exit code with stdout and stderr joined.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken ct = default
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        MakeBuildHermetic(psi);

        using var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");

        // Drain both pipes concurrently: reading stdout to EOF before touching stderr
        // deadlocks when the child fills the stderr pipe buffer and blocks on write.
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

    /// <summary>
    /// Boots <paramref name="projectPath"/> with <c>dotnet run</c> on an OS-assigned
    /// ephemeral port and returns once Kestrel reports the URL it bound.
    /// </summary>
    public static async Task<ServerProcess> StartServerAsync(
        string projectPath,
        CancellationToken ct
    )
    {
        // Port 0 asks the OS for a free port, so concurrent hosts (or unrelated
        // processes) cannot collide on a pre-drawn one. The bound URL is read back
        // from Kestrel's "Now listening on:" line.
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{projectPath}\" --urls http://127.0.0.1:0",
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        MakeBuildHermetic(psi);

        var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {projectPath}");

        const string listeningMarker = "Now listening on:";
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
                    return new ServerProcess(
                        process,
                        line[(markerIndex + listeningMarker.Length)..].Trim()
                    );
                }
            }

            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"Server did not start. Output:\n{output}\nStderr:\n{stderr}"
            );
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Child dotnet builds must not share MSBuild node-reuse workers or the Roslyn
    /// compiler server with the outer <c>dotnet test</c> (itself an MSBuild build):
    /// sharing them deadlocked the testhost under load. MSBuild reads environment
    /// variables as global properties, so this covers build, run, pack and publish.
    /// </summary>
    private static void MakeBuildHermetic(ProcessStartInfo psi)
    {
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["UseSharedCompilation"] = "false";
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
}

internal sealed class ServerProcess(Process process, string url) : IAsyncDisposable
{
    public string Url { get; } = url;

    public async ValueTask DisposeAsync()
    {
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The server already exited.
        }

        process.Dispose();
    }
}

/// <summary>Runs <c>tools/roundtrip_diff.py</c>, the comparator the round-trip gate shares.</summary>
internal static class RoundTripDiff
{
    private static string ScriptPath => CliRunner.RepoPath("tools", "roundtrip_diff.py");

    public static (int ExitCode, string StdOut, string StdErr) Run(
        string workingDirectory,
        IReadOnlyList<string> args
    ) => CliRunner.Run(workingDirectory, "python3", [ScriptPath, .. args]);

    public static RoundTripDiffReport Run(
        JsonObject original,
        JsonObject reemitted,
        string? generatedSource = null
    )
    {
        using var work = new TempDir();
        var originalPath = Path.Combine(work.FullName, "original.json");
        var reemittedPath = Path.Combine(work.FullName, "reemitted.json");
        var summaryPath = Path.Combine(work.FullName, "summary.json");
        var detailsPath = Path.Combine(work.FullName, "details.json");
        File.WriteAllText(originalPath, original.ToJsonString());
        File.WriteAllText(reemittedPath, reemitted.ToJsonString());
        List<string> arguments =
        [
            originalPath,
            reemittedPath,
            "--summary-json",
            summaryPath,
            "--details-json",
            detailsPath,
        ];
        if (generatedSource is not null)
        {
            arguments.Add("--generated-source");
            arguments.Add(generatedSource);
        }

        var process = Run(work.FullName, arguments);
        Assert.True(
            File.Exists(summaryPath),
            $"Comparator did not write summary. Exit: {process.ExitCode}; stderr: {process.StdErr}"
        );
        using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
        using var details = JsonDocument.Parse(File.ReadAllText(detailsPath));
        return new RoundTripDiffReport(
            process.ExitCode,
            summary.RootElement.Clone(),
            details.RootElement.Clone()
        );
    }
}

internal sealed record RoundTripDiffReport(int ExitCode, JsonElement Summary, JsonElement Details)
{
    public JsonElement DocumentFindings => Summary.GetProperty("documentFindings");
    public JsonElement OperationFindings => Summary.GetProperty("opFindings");
    public JsonElement SchemaFindings => Summary.GetProperty("schemaFindings");
    public JsonElement IntegrityFindings => Summary.GetProperty("integrityFindings");

    public static int FindingCount(JsonElement findings, string category) =>
        findings.TryGetProperty(category, out var count) ? count.GetInt32() : 0;

    /// <summary>Counts <paramref name="category"/> across document, operation and schema findings.</summary>
    public int FindingCount(string category) =>
        FindingCount(DocumentFindings, category)
        + FindingCount(OperationFindings, category)
        + FindingCount(SchemaFindings, category);
}
