using System.Security.Cryptography;
using System.Text.Json;

namespace Rivet.Tests;

public sealed class OpenApiCorpusManifestTests
{
    private static readonly HashSet<string> _methods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get",
        "put",
        "post",
        "delete",
        "patch",
        "head",
        "options",
        "trace",
    };

    [Fact]
    public void Manifest_Pins_Every_Corpus_Artifact_And_Its_Parsed_Shape()
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(CliRunner.RepoPath("corpus", "openapi-manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!;
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.NotEmpty(manifest.Corpora);

        var corpusDirectory = CliRunner.RepoPath("openapi");
        Assert.True(
            Directory.Exists(corpusDirectory),
            $"Pinned corpus directory is missing: {corpusDirectory}"
        );

        var actualFiles = Directory
            .EnumerateFiles(corpusDirectory, "*.json")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var manifestFiles = manifest
            .Corpora.Select(corpus => corpus.File)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(manifestFiles, actualFiles);

        Assert.Equal(manifest.Corpora.Length, manifest.Corpora.Select(c => c.Id).Distinct().Count());
        Assert.Equal(
            manifest.Corpora.Length,
            manifest.Corpora.Select(c => c.Sha256).Distinct().Count()
        );

        foreach (var corpus in manifest.Corpora)
        {
            var path = Path.Combine(corpusDirectory, corpus.File);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(corpus.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var openApiVersion = root.TryGetProperty("openapi", out var openApi)
                ? openApi.GetString()
                : root.GetProperty("swagger").GetString();
            Assert.Equal(corpus.OpenApiVersion, openApiVersion);
            Assert.Equal(corpus.ApiVersion, root.GetProperty("info").GetProperty("version").GetString());

            var paths = root.GetProperty("paths");
            Assert.Equal(corpus.PathCount, paths.EnumerateObject().Count());
            Assert.Equal(
                corpus.OperationCount,
                paths
                    .EnumerateObject()
                    .Sum(pathItem =>
                        pathItem.Value.EnumerateObject().Count(property => _methods.Contains(property.Name))
                    )
            );

            var schemas = root.TryGetProperty("components", out var components)
                && components.TryGetProperty("schemas", out var componentSchemas)
                    ? componentSchemas
                    : root.TryGetProperty("definitions", out var definitions)
                        ? definitions
                        : default;
            var schemaCount = schemas.ValueKind == JsonValueKind.Object
                ? schemas.EnumerateObject().Count()
                : 0;
            Assert.Equal(corpus.SchemaCount, schemaCount);
        }
    }

    private sealed record Manifest(int SchemaVersion, Corpus[] Corpora);

    private sealed record Corpus(
        string Id,
        string File,
        string Sha256,
        string OpenApiVersion,
        string ApiVersion,
        int PathCount,
        int OperationCount,
        int SchemaCount,
        string Provenance
    );
}
