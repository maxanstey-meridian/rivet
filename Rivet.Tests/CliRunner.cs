using System.Diagnostics;
using System.Text;

namespace Rivet.Tests;

/// <summary>
/// Runs the built Rivet.Tool CLI (or any other executable) as a real process.
/// Shared by the disk-true gates (CliPipelineTests, RoundTripCorpusGateTests).
/// </summary>
internal static class CliRunner
{
    public static string ToolDllPath => Path.Combine(AppContext.BaseDirectory, "Rivet.Tool.dll");

    public static string RepoPath(params string[] segments) =>
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", .. segments]);

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
}
