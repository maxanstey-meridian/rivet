using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.CSharp;

namespace Rivet.Tests;

internal static class GeneratedCarrierFixture
{
    public static Type ImportCompileAndLoad(string schema, string typeName)
    {
        var workDirectory = Directory.CreateTempSubdirectory("rivet-generated-carrier-");
        try
        {
            return ImportCompileAndLoad(workDirectory, schema, typeName);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    public static ReemittedGeneratedCarrier ImportCompileLoadAndEmit(string schema, string typeName)
    {
        var workDirectory = Directory.CreateTempSubdirectory("rivet-generated-carrier-");
        try
        {
            var requestType = ImportCompileAndLoad(workDirectory, schema, typeName);
            var generatedDirectory = Path.Combine(workDirectory.FullName, "generated");
            var emittedDirectory = Path.Combine(workDirectory.FullName, "emitted");
            var emission = CliRunner.RunCli(
                workDirectory.FullName,
                [generatedDirectory, "--openapi", "--output", emittedDirectory]
            );
            Assert.True(emission.ExitCode == 0, emission.StdErr);
            using var emittedDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(emittedDirectory, "openapi.json"))
            );

            return new ReemittedGeneratedCarrier(requestType, emittedDocument.RootElement.Clone());
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    private static Type ImportCompileAndLoad(
        DirectoryInfo workDirectory,
        string schema,
        string typeName
    )
    {
        var sourcePath = Path.Combine(workDirectory.FullName, "source.json");
        File.WriteAllText(
            sourcePath,
            CompilationHelper.BuildSpec(
                schemas: schema,
                paths: $$"""
                "/items": {
                    "post": {
                        "operationId": "items_create",
                        "requestBody": {
                            "required": true,
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/{{typeName}}" }
                                }
                            }
                        },
                        "responses": { "204": { "description": "Created" } }
                    }
                }
                """
            )
        );
        var generatedDirectory = Path.Combine(workDirectory.FullName, "generated");
        var import = CliRunner.RunCli(
            workDirectory.FullName,
            [
                "--from-openapi",
                sourcePath,
                "--output",
                generatedDirectory,
                "--namespace",
                "Generated",
            ]
        );
        Assert.True(import.ExitCode == 0, import.StdErr);

        var sourceFiles = Directory.GetFiles(
            generatedDirectory,
            "*.cs",
            SearchOption.AllDirectories
        );
        var compilation = (
            (CSharpCompilation)
                CompilationHelper.CreateCompilationFromMultiple(
                    sourceFiles.Select(File.ReadAllText).ToArray(),
                    sourceFiles
                )
        ).WithAssemblyName($"GeneratedCarrier_{Guid.NewGuid():N}");
        using var assemblyStream = new MemoryStream();
        var emit = compilation.Emit(assemblyStream);
        Assert.True(
            emit.Success,
            string.Join(
                Environment.NewLine,
                emit.Diagnostics.Select(diagnostic => diagnostic.ToString())
            )
        );
        var assembly = Assembly.Load(assemblyStream.ToArray());
        return Assert.IsAssignableFrom<Type>(assembly.GetType($"Generated.{typeName}"));
    }
}

internal sealed record ReemittedGeneratedCarrier(Type RequestType, JsonElement EmittedRoot);

internal static class RealCliOpenApiPass
{
    public static JsonObject ImportAndEmit(
        string workingDirectory,
        string sourcePath,
        string pass
    ) => ImportEmitAndReadGeneratedSource(workingDirectory, sourcePath, pass).Document;

    public static RealCliOpenApiPassResult ImportEmitAndReadGeneratedSource(
        string workingDirectory,
        string sourcePath,
        string pass
    )
    {
        var generatedDirectory = Path.Combine(workingDirectory, $"generated-{pass}");
        var import = CliRunner.RunCli(
            workingDirectory,
            [
                "--from-openapi",
                sourcePath,
                "--output",
                generatedDirectory,
                "--namespace",
                "Generated",
            ]
        );
        Assert.True(import.ExitCode == 0, import.StdErr);
        var generatedSource = string.Join(
            "\n",
            Directory
                .GetFiles(generatedDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
        );

        var outputDirectory = Path.Combine(workingDirectory, $"output-{pass}");
        var emit = CliRunner.RunCli(
            workingDirectory,
            [generatedDirectory, "--openapi", "--output", outputDirectory]
        );
        Assert.True(emit.ExitCode == 0, emit.StdErr);
        var document = JsonNode
            .Parse(File.ReadAllText(Path.Combine(outputDirectory, "openapi.json")))!
            .AsObject();
        return new RealCliOpenApiPassResult(document, generatedSource);
    }
}

internal sealed record RealCliOpenApiPassResult(JsonObject Document, string GeneratedSource);

internal sealed class TemporaryJson(string path) : IDisposable
{
    public string Path { get; } = path;

    public static TemporaryJson Write(JsonNode value)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"rivet-test-json-{Guid.NewGuid():N}.json"
        );
        File.WriteAllText(path, value.ToJsonString());
        return new TemporaryJson(path);
    }

    public void Dispose() => File.Delete(Path);
}
