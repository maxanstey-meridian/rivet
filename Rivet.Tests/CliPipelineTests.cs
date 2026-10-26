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
[Trait("Category", "Local")] // exercises the tracked openapi/ corpus on disk
public sealed class CliPipelineTests
{
    private static string SpecPath(string name) => CliRunner.RepoPath("openapi", $"{name}.json");

    private static (int ExitCode, string StdOut, string StdErr) RunCli(
        string workingDirectory,
        IReadOnlyList<string> args
    ) => CliRunner.RunCli(workingDirectory, args);

    [Fact]
    public void Cli_check_reports_orphaned_binding_without_crashing()
    {
        using var workDir = new TempDir();
        var sourcePath = Path.Combine(workDir.FullName, "Endpoint.cs");
        File.WriteAllText(
            sourcePath,
            """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            [RivetContract]
            public static class Contract
            {
                public static readonly RouteDefinition<Input, Output> Create =
                    Define.Post<Input, Output>("/items");
            }

            public sealed record Input(string Value);
            public sealed record Output(string Value);

            [ApiController]
            [Route("/items")]
            public sealed class Controller : ControllerBase
            {
                [HttpPost]
                public IActionResult Create(Input input)
                {
                    _ = Contract.Create.Bind(input);
                    return NoContent();
                }
            }
            """
        );

        var result = RunCli(workDir.FullName, [sourcePath, "--check"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("warning RIV4004: [OrphanedBinding]", result.StdErr);
        Assert.DoesNotContain("Unmapped coverage warning kind", result.StdErr);
    }

    [Theory]
    [InlineData("notion")]
    [InlineData("github")]
    [InlineData("cloudflare")]
    public void Cli_Import_Compile_Emit_RoundTrips_From_Disk(string spec)
    {
        using var workDir = new TempDir();
        var srcDir = Path.Combine(workDir.FullName, "src");

        // 1. Import via the real CLI, writing C# to disk.
        var import = RunCli(
            workDir.FullName,
            ["--from-openapi", SpecPath(spec), "--output", srcDir, "--namespace", "Generated"]
        );
        Assert.True(import.ExitCode == 0, $"import failed:\n{import.StdErr}");

        // 2. Every file the CLI claims to have generated must actually exist
        //    afterwards. On a case-insensitive filesystem (APFS/NTFS — i.e.
        //    most dev machines) two names differing only by case clobber
        //    each other at write time, leaving dangling type references.
        var generatedLine = import
            .StdOut.Split('\n')
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
        var sourceSecuritySchemes =
            sourceDocument.RootElement.TryGetProperty("components", out var components)
            && components.TryGetProperty("securitySchemes", out var securitySchemes)
                ? securitySchemes
                    .EnumerateObject()
                    .OrderBy(scheme => scheme.Name, StringComparer.Ordinal)
                    .ToList()
                : [];
        foreach (var securityScheme in sourceSecuritySchemes)
        {
            emitArgs.Add("--security");
            emitArgs.Add(ToCliSecuritySpec(securityScheme.Name, securityScheme.Value));
        }
        var emit = RunCli(workDir.FullName, emitArgs);
        Assert.True(emit.ExitCode == 0, $"compile/emit failed:\n{emit.StdErr}");

        // 4. The round-tripped spec must be internally consistent: every
        //    local $ref resolves. A dangling $ref hard-fails downstream
        //    generators (openapi-typescript et al.).
        var specPath = Path.Combine(outDir, "openapi.json");
        Assert.True(File.Exists(specPath), $"expected {specPath} to exist");
        using var document = JsonDocument.Parse(File.ReadAllText(specPath));
        foreach (var sourceScheme in sourceSecuritySchemes)
        {
            var emittedScheme = document
                .RootElement.GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty(sourceScheme.Name);
            string[] keywords = sourceScheme.Value.GetProperty("type").GetString() switch
            {
                "http" => ["type", "scheme"],
                _ => ["type", "in", "name"],
            };
            foreach (var keyword in keywords)
            {
                Assert.Equal(
                    sourceScheme.Value.GetProperty(keyword).GetString(),
                    emittedScheme.GetProperty(keyword).GetString()
                );
            }
        }
        Assert.DoesNotContain(
            JsonRefs.Collect(document.RootElement),
            r =>
                r.Reference.StartsWith("#/")
                && !JsonRefs.Resolves(document.RootElement, r.Reference)
        );
    }

    [Fact]
    public void Cli_Disk_Pipeline_Preserves_Component_Referenced_By_Schema_Example()
    {
        using var workDir = new TempDir();
        var sourcePath = Path.Combine(workDir.FullName, "source.json");
        File.WriteAllText(
            sourcePath,
            """
            {
              "openapi": "3.1.0",
              "info": { "title": "Component examples", "version": "1.0.0" },
              "paths": {
                "/deployments": {
                  "get": {
                    "responses": {
                      "200": {
                        "description": "Deployment rules",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/DeploymentRules" }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "DeploymentRules": {
                    "type": "object",
                    "properties": {
                      "rules": {
                        "type": "array",
                        "items": { "type": "object" }
                      }
                    },
                    "examples": [
                      { "$ref": "#/components/examples/deployment-protection-rules" }
                    ]
                  }
                },
                "examples": {
                  "deployment-protection-rules": {
                    "summary": "Deployment protection rules",
                    "description": "The exact authored component example.",
                    "value": [
                      { "total_count": 2 },
                      { "custom_deployment_protection_rules": [{ "id": 3, "enabled": true }] }
                    ]
                  }
                }
              }
            }
            """
        );
        var srcDir = Path.Combine(workDir.FullName, "src");
        var import = RunCli(
            workDir.FullName,
            ["--from-openapi", sourcePath, "--output", srcDir, "--namespace", "Generated"]
        );
        Assert.True(import.ExitCode == 0, $"import failed:\n{import.StdErr}");

        var outDir = Path.Combine(workDir.FullName, "out");
        var emit = RunCli(workDir.FullName, [srcDir, "--openapi", "--output", outDir]);
        Assert.True(emit.ExitCode == 0, $"compile/emit failed:\n{emit.StdErr}");

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(outDir, "openapi.json"))
        );
        var schemaExample = document
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("DeploymentRules")
            .GetProperty("examples")[0];
        Assert.Equal(
            "#/components/examples/deployment-protection-rules",
            schemaExample.GetProperty("$ref").GetString()
        );
        Assert.Single(schemaExample.EnumerateObject());

        var componentExample = document
            .RootElement.GetProperty("components")
            .GetProperty("examples")
            .GetProperty("deployment-protection-rules");
        Assert.Equal(
            "Deployment protection rules",
            componentExample.GetProperty("summary").GetString()
        );
        Assert.Equal(
            "The exact authored component example.",
            componentExample.GetProperty("description").GetString()
        );
        var value = componentExample.GetProperty("value");
        Assert.Equal(2, value.GetArrayLength());
        Assert.Equal(2, value[0].GetProperty("total_count").GetInt32());
        Assert.Equal(
            3,
            value[1]
                .GetProperty("custom_deployment_protection_rules")[0]
                .GetProperty("id")
                .GetInt32()
        );

        Assert.DoesNotContain(
            JsonRefs.Collect(document.RootElement),
            r =>
                r.Reference.StartsWith("#/")
                && !JsonRefs.Resolves(document.RootElement, r.Reference)
        );
    }

    [Fact]
    public void Cli_Import_Diagnoses_Reserved_Content_Type_Without_Emitting_Unsupported_Source()
    {
        using var workDir = new TempDir();
        var sourcePath = Path.Combine(workDir.FullName, "source.json");
        File.WriteAllText(
            sourcePath,
            """
            {
              "openapi":"3.1.0","info":{"title":"Reserved header","version":"1"},
              "paths":{"/projects/{project}/ssh-key":{"post":{
                "operationId":"addSshKey","parameters":[
                  {"name":"project","in":"path","required":true,"schema":{"type":"string"}},
                  {"name":"Content-Type","in":"header","required":true,"schema":{"type":"string","enum":["application/json"]}},
                  {"name":"X-Trace","in":"header","schema":{"type":"string"}},
                  {"name":"notify","in":"query","schema":{"type":"boolean"}}
                ],
                "requestBody":{"required":true,"content":{"application/json":{"schema":{"type":"object","properties":{"hostname":{"type":"string"}}}}}},
                "responses":{"204":{"description":"No Content"}}
              }}}
            }
            """
        );
        var sourceDirectory = Path.Combine(workDir.FullName, "src");

        var import = RunCli(
            workDir.FullName,
            ["--from-openapi", sourcePath, "--output", sourceDirectory]
        );

        Assert.Equal(0, import.ExitCode);
        Assert.Contains(
            "warning RIV3021: Reserved header parameter dropped: POST /projects/{project}/ssh-key declares 'Content-Type'; request media types are represented by requestBody.content.",
            import.StdErr
        );
        var generatedSource = string.Join(
            "\n",
            Directory
                .GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
        );
        Assert.DoesNotContain("[rivet:unsupported param name=Content-Type", generatedSource);
        Assert.Contains(".RequestContent<", generatedSource);
        Assert.Contains(".Parameter<string>(\"project\", \"path\", true", generatedSource);
        Assert.Contains(".Parameter<string>(\"X-Trace\", \"header\", false", generatedSource);
        Assert.Contains(".Parameter<bool>(\"notify\", \"query\", false", generatedSource);
    }

    [Fact]
    public void Cli_Import_Preserves_Request_Content_And_Drops_A_Finite_Content_Type_Header()
    {
        using var workDir = new TempDir();
        var sourcePath = Path.Combine(workDir.FullName, "source.json");
        File.WriteAllText(
            sourcePath,
            """
            {
              "openapi":"3.1.0","info":{"title":"Mapped header","version":"1"},
              "paths":{"/build":{"post":{
                "operationId":"build","parameters":[
                  {"name":"Content-type","in":"header","schema":{"type":"string","enum":["application/x-tar"]}}
                ],
                "requestBody":{"content":{"application/octet-stream":{"schema":{"type":"string","format":"binary"}}}},
                "responses":{"200":{"description":"Done"}}
              }}}
            }
            """
        );
        var sourceDirectory = Path.Combine(workDir.FullName, "src");
        var outputDirectory = Path.Combine(workDir.FullName, "out");

        var import = RunCli(
            workDir.FullName,
            ["--from-openapi", sourcePath, "--output", sourceDirectory]
        );
        Assert.Equal(0, import.ExitCode);
        Assert.Contains(
            "warning RIV3021: Reserved header parameter dropped: POST /build declares 'Content-type'; request media types are represented by requestBody.content.",
            import.StdErr
        );
        var emit = RunCli(
            workDir.FullName,
            [sourceDirectory, "--openapi", "--output", outputDirectory]
        );
        Assert.Equal(0, emit.ExitCode);

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(outputDirectory, "openapi.json"))
        );
        var operation = document
            .RootElement.GetProperty("paths")
            .GetProperty("/build")
            .GetProperty("post");
        var content = operation.GetProperty("requestBody").GetProperty("content");
        Assert.True(content.TryGetProperty("application/octet-stream", out _));
        Assert.False(content.TryGetProperty("application/x-tar", out _));
        if (operation.TryGetProperty("parameters", out var parameters))
        {
            Assert.DoesNotContain(
                parameters.EnumerateArray(),
                parameter =>
                    parameter
                        .GetProperty("name")
                        .GetString()
                        ?.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) == true
            );
        }
    }

    private static string ToCliSecuritySpec(string name, JsonElement scheme)
    {
        var type = scheme.GetProperty("type").GetString();
        if (
            type == "http"
            && scheme.TryGetProperty("scheme", out var httpScheme)
            && httpScheme.GetString() == "bearer"
        )
        {
            return $"{name}=bearer";
        }

        if (
            type == "apiKey"
            && scheme.TryGetProperty("in", out var location)
            && scheme.TryGetProperty("name", out var parameterName)
        )
        {
            return $"{name}=apikey:{location.GetString()}:{parameterName.GetString()}";
        }

        // --security only spells bearer and apiKey. A corpus with OAuth2, OpenID Connect or
        // HTTP basic needs a CLI that can express it, not a silent swap to bearer.
        throw new InvalidOperationException(
            $"--security cannot express source scheme '{name}': {scheme.GetRawText()}"
        );
    }
}
