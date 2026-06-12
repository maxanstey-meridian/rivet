using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// End-to-end gate over the REAL CLI and the REAL filesystem — the path the
/// in-memory corpus tests cannot see. Every prior escape in this pipeline was
/// invisible to CompilationHelper-based tests for one of three reasons: they
/// compile with a richer reference set than the CLI's loose-file path, they
/// keep all emitted files in memory so case-insensitive filesystems never
/// clobber anything, and they never feed the round-tripped spec to a
/// downstream generator. This gate is the disk-true equivalent:
/// import → write to disk → compile via the CLI → re-emit openapi.json →
/// resolve every $ref in the result.
/// </summary>
[Trait("Category", "Local")] // needs the gitignored openapi/ corpus on disk
public sealed class CliPipelineTests
{
    private static string SpecPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "openapi", $"{name}.json");

    private static string ToolDllPath =>
        Path.Combine(AppContext.BaseDirectory, "Rivet.Tool.dll");

    private static (int ExitCode, string StdOut, string StdErr) RunCli(
        string workingDirectory,
        IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ToolDllPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdOut.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stdErr.AppendLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Rivet CLI did not exit within 5 minutes.");
        }

        // The timeout overload returns on process exit WITHOUT draining the async
        // readers — the last buffered lines can land after we read the builders.
        // The parameterless overload waits for stream EOF.
        process.WaitForExit();

        return (process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    [Theory]
    [InlineData("notion")]
    [InlineData("github")]
    [InlineData("cloudflare")]
    public void Cli_Import_Compile_Emit_RoundTrips_From_Disk(string spec)
    {
        var workDir = Directory.CreateTempSubdirectory($"rivet-e2e-{spec}-");
        try
        {
            var srcDir = Path.Combine(workDir.FullName, "src");

            // 1. Import via the real CLI, writing C# to disk.
            var import = RunCli(
                workDir.FullName,
                ["--from-openapi", SpecPath(spec), "--output", srcDir, "--namespace", "Generated"]);
            Assert.True(import.ExitCode == 0, $"import failed:\n{import.StdErr}");

            // 2. Every file the CLI claims to have generated must actually exist
            //    afterwards. On a case-insensitive filesystem (APFS/NTFS — i.e.
            //    most dev machines) two names differing only by case clobber
            //    each other at write time, leaving dangling type references.
            var generatedLine = import.StdOut
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("Generated ") && line.EndsWith("file(s)."));
            Assert.NotNull(generatedLine);
            var claimedCount = int.Parse(generatedLine.Split(' ')[1]);
            var writtenFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);
            Assert.Equal(claimedCount, writtenFiles.Length);

            // Deterministic equivalent for case-SENSITIVE hosts (CI on Linux),
            // where both files survive the write but any case-insensitive
            // checkout of the generated code is broken.
            var caseCollisions = writtenFiles
                .GroupBy(file => file.ToLowerInvariant())
                .Where(group => group.Count() > 1)
                .Select(group => string.Join(" vs ", group))
                .ToList();
            Assert.Empty(caseCollisions);

            // 3. Compile the on-disk output through the CLI's loose-file path
            //    and re-emit openapi.json — the importer must be able to eat
            //    its own cooking via its own front door. The directory form is
            //    load-bearing: 11k individual paths overflow ARG_MAX.
            var outDir = Path.Combine(workDir.FullName, "out");
            var emit = RunCli(
                workDir.FullName,
                [srcDir, "--openapi", "--output", outDir]);
            Assert.True(emit.ExitCode == 0, $"compile/emit failed:\n{emit.StdErr}");

            // 4. The round-tripped spec must be internally consistent: every
            //    local $ref resolves. A dangling $ref hard-fails downstream
            //    generators (openapi-typescript et al.).
            var specPath = Path.Combine(outDir, "openapi.json");
            Assert.True(File.Exists(specPath), $"expected {specPath} to exist");
            using var document = JsonDocument.Parse(File.ReadAllText(specPath));
            var danglingRefs = new List<string>();
            CollectDanglingRefs(document.RootElement, document.RootElement, danglingRefs);
            Assert.Empty(danglingRefs);
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    private static void CollectDanglingRefs(
        JsonElement node,
        JsonElement root,
        List<string> dangling)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name == "$ref"
                        && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() is { } reference
                        && reference.StartsWith("#/")
                        && !ResolvesInDocument(reference, root))
                    {
                        dangling.Add(reference);
                    }

                    CollectDanglingRefs(property.Value, root, dangling);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    CollectDanglingRefs(item, root, dangling);
                }

                break;
        }
    }

    private static bool ResolvesInDocument(string reference, JsonElement root)
    {
        var current = root;
        foreach (var rawSegment in reference[2..].Split('/'))
        {
            // JSON Pointer unescaping per RFC 6901.
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    return false;
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, out var index)
                    || index < 0
                    || index >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[index];
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
