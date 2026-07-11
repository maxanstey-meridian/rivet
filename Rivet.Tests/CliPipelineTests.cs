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
        CliRunner.RepoPath("openapi", $"{name}.json");

    private static (int ExitCode, string StdOut, string StdErr) RunCli(
        string workingDirectory,
        IReadOnlyList<string> args) =>
        CliRunner.RunCli(workingDirectory, args);

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
            using var sourceDocument = JsonDocument.Parse(File.ReadAllText(SpecPath(spec)));
            var emitArgs = new List<string> { srcDir, "--openapi", "--output", outDir };
            var securitySchemeNames = new HashSet<string>(StringComparer.Ordinal);
            var sourceSecuritySchemes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (sourceDocument.RootElement.TryGetProperty("components", out var components)
                && components.TryGetProperty("securitySchemes", out var securitySchemes))
            {
                foreach (var securityScheme in securitySchemes.EnumerateObject())
                {
                    securitySchemeNames.Add(securityScheme.Name);
                    sourceSecuritySchemes[securityScheme.Name] = securityScheme.Value;
                }
            }
            CollectSecuritySchemeNames(sourceDocument.RootElement, securitySchemeNames);
            var degradedSchemes = new List<string>();
            foreach (var securitySchemeName in securitySchemeNames.Order(StringComparer.Ordinal))
            {
                emitArgs.Add("--security");
                emitArgs.Add(ToCliSecuritySpec(securitySchemeName, sourceSecuritySchemes, degradedSchemes));
            }
            var emit = RunCli(
                workDir.FullName,
                emitArgs);
            Assert.True(emit.ExitCode == 0, $"compile/emit failed:\n{emit.StdErr}");

            // 4. The round-tripped spec must be internally consistent: every
            //    local $ref resolves. A dangling $ref hard-fails downstream
            //    generators (openapi-typescript et al.).
            var specPath = Path.Combine(outDir, "openapi.json");
            Assert.True(File.Exists(specPath), $"expected {specPath} to exist");
            using var document = JsonDocument.Parse(File.ReadAllText(specPath));
            foreach (var degradedScheme in degradedSchemes)
            {
                var emittedScheme = document.RootElement.GetProperty("components")
                    .GetProperty("securitySchemes").GetProperty(degradedScheme);
                Assert.Equal("http", emittedScheme.GetProperty("type").GetString());
                Assert.Equal("bearer", emittedScheme.GetProperty("scheme").GetString());
            }
            var danglingRefs = new List<string>();
            CollectDanglingRefs(document.RootElement, document.RootElement, danglingRefs);
            Assert.Empty(danglingRefs);
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    private static string ToCliSecuritySpec(
        string name,
        IReadOnlyDictionary<string, JsonElement> sourceSchemes,
        List<string> degradedSchemes)
    {
        if (sourceSchemes.TryGetValue(name, out var scheme)
            && scheme.TryGetProperty("type", out var type))
        {
            if (type.GetString() == "http"
                && scheme.TryGetProperty("scheme", out var httpScheme)
                && httpScheme.GetString() == "bearer")
            {
                return $"{name}=bearer";
            }

            if (type.GetString() == "apiKey"
                && scheme.TryGetProperty("in", out var location)
                && scheme.TryGetProperty("name", out var parameterName))
            {
                return $"{name}=apikey:{location.GetString()}:{parameterName.GetString()}";
            }
        }

        // The public CLI cannot express OAuth2, OpenID Connect, or HTTP basic definitions.
        // Keep the reference resolvable and pin the deliberate loss instead of claiming fidelity.
        degradedSchemes.Add(name);
        return $"{name}=bearer";
    }

    private static void CollectSecuritySchemeNames(JsonElement node, HashSet<string> names)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals("security") && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var requirement in property.Value.EnumerateArray())
                    {
                        if (requirement.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var scheme in requirement.EnumerateObject())
                        {
                            names.Add(scheme.Name);
                        }
                    }
                }

                CollectSecuritySchemeNames(property.Value, names);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                CollectSecuritySchemeNames(item, names);
            }
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
