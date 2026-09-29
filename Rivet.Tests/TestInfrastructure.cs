using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.CSharp;
using Rivet.Tool.Model;

namespace Rivet.Tests;

internal static class GeneratedCarrierFixture
{
    public static Type ImportCompileAndLoad(string schema, string typeName)
    {
        using var workDirectory = new TempDir();
        return ImportCompileAndLoad(workDirectory, schema, typeName);
    }

    public static ReemittedGeneratedCarrier ImportCompileLoadAndEmit(string schema, string typeName)
    {
        using var workDirectory = new TempDir();
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

    private static Type ImportCompileAndLoad(TempDir workDirectory, string schema, string typeName)
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

/// <summary>A temporary directory that is deleted, with its contents, on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("rivet-test-");

    public string FullName => _directory.FullName;

    public void Dispose()
    {
        if (Directory.Exists(FullName))
        {
            Directory.Delete(FullName, recursive: true);
        }
    }
}

/// <summary>Reads files from the copied <c>Fixtures</c> directory.</summary>
internal static class Fixture
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name));

    public static JsonObject Json(string name) => JsonNode.Parse(Text(name))!.AsObject();
}

/// <summary>
/// Single-operation slices of the vendored GitHub and Twilio specs, carrying only the
/// components the operation needs.
/// </summary>
internal static class FixtureSlices
{
    private const string BudgetPath = "/organizations/{org}/settings/billing/budgets/{budget_id}";

    public static string TwilioCreateAccount()
    {
        var (source, document) = Slice("openapi-twilio.json");
        CopyOperation(source, document, "/2010-04-01/Accounts.json", "post");
        CopyComponentEntries(source, document, "securitySchemes", "accountSid_authToken");
        return document.ToJsonString();
    }

    public static string GitHubUpdateBudget()
    {
        var (source, document) = Slice("openapi-github.json");
        var patch = CopyOperation(source, document, BudgetPath, "patch");
        patch["responses"] = new JsonObject { ["404"] = patch["responses"]!["404"]!.DeepClone() };
        CopyComponentEntries(source, document, "parameters", "org", "budget");
        CopyComponentEntries(source, document, "schemas", "basic-error");
        return document.ToJsonString();
    }

    public static string GitHubDeleteBudget()
    {
        var (source, document) = Slice("openapi-github.json");
        var delete = CopyOperation(source, document, BudgetPath, "delete");
        delete["responses"] = new JsonObject { ["200"] = delete["responses"]!["200"]!.DeepClone() };
        CopyComponentEntries(source, document, "parameters", "org", "budget");
        CopyComponentEntries(source, document, "responses", "delete-budget");
        CopyComponentEntries(source, document, "examples", "delete-budget");
        CopyComponentEntries(source, document, "schemas", "delete-budget");
        return document.ToJsonString();
    }

    public static string GitHubSetActionsCacheRetentionLimit()
    {
        var (source, document) = Slice("openapi-github.json");
        var put = CopyOperation(
            source,
            document,
            "/enterprises/{enterprise}/actions/cache/retention-limit",
            "put"
        );
        put["responses"] = new JsonObject { ["204"] = put["responses"]!["204"]!.DeepClone() };
        CopyComponentEntries(source, document, "parameters", "enterprise");
        CopyComponentEntries(
            source,
            document,
            "schemas",
            "actions-cache-retention-limit-for-enterprise"
        );
        CopyComponentEntries(source, document, "examples", "actions-cache-retention-limit");
        return document.ToJsonString();
    }

    public static string GitHubUpdateImport()
    {
        var (source, document) = Slice("openapi-github.json");
        var patch = CopyOperation(source, document, "/repos/{owner}/{repo}/import", "patch");
        patch["responses"] = new JsonObject
        {
            ["204"] = new JsonObject { ["description"] = "No Content" },
        };
        CopyComponentEntries(source, document, "parameters", "owner", "repo");
        return document.ToJsonString();
    }

    private static (JsonObject Source, JsonObject Document) Slice(string fixtureName)
    {
        var source = Fixture.Json(fixtureName);
        var document = new JsonObject
        {
            ["openapi"] = source["openapi"]!.DeepClone(),
            ["info"] = source["info"]!.DeepClone(),
            ["paths"] = new JsonObject(),
        };
        return (source, document);
    }

    private static JsonObject CopyOperation(
        JsonObject source,
        JsonObject document,
        string path,
        string method
    )
    {
        var sourcePath = source["paths"]?[path] as JsonObject;
        Assert.NotNull(sourcePath);
        var operation = sourcePath[method]!.DeepClone().AsObject();
        document["paths"]![path] = new JsonObject { [method] = operation };
        return operation;
    }

    private static void CopyComponentEntries(
        JsonObject source,
        JsonObject document,
        string sectionName,
        params string[] keys
    )
    {
        var sourceSection = source["components"]?[sectionName] as JsonObject;
        Assert.NotNull(sourceSection);

        document["components"] ??= new JsonObject();
        var components = document["components"]!.AsObject();
        components[sectionName] ??= new JsonObject();
        var targetSection = components[sectionName]!.AsObject();
        foreach (var key in keys)
        {
            targetSection[key] = sourceSection[key]!.DeepClone();
        }
    }
}

/// <summary>
/// Collects and resolves local <c>$ref</c>s in a JSON document. Kept independent of the
/// tool's <c>JsonPointer</c> so it can act as an oracle for emitted references.
/// </summary>
internal static class JsonRefs
{
    public static List<(string Pointer, string Reference)> Collect(JsonElement root)
    {
        var refs = new List<(string Pointer, string Reference)>();
        Collect(root, "#", refs);
        return refs;
    }

    /// <summary>
    /// Resolves an RFC 6901 <c>#/a/b</c> reference against <paramref name="root"/>.
    /// Anything that is not a local <c>#/</c> reference does not resolve.
    /// </summary>
    public static bool Resolves(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        var current = root;
        foreach (var rawSegment in reference[2..].Split('/'))
        {
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
                if (
                    !int.TryParse(segment, out var index)
                    || index < 0
                    || index >= current.GetArrayLength()
                )
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

    private static void Collect(
        JsonElement element,
        string pointer,
        List<(string Pointer, string Reference)> refs
    )
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "$ref" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        refs.Add((pointer, property.Value.GetString()!));
                    }
                    else
                    {
                        Collect(property.Value, $"{pointer}/{property.Name}", refs);
                    }
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, $"{pointer}/{index}", refs);
                    index++;
                }
                break;
        }
    }
}

internal static class TsTypeRefs
{
    /// <summary>
    /// Recursively collects all named type references from a TsType tree.
    /// </summary>
    public static void Collect(TsType type, HashSet<string> names)
    {
        switch (type)
        {
            case TsType.TypeRef r:
                names.Add(r.Name);
                break;
            case TsType.Nullable n:
                Collect(n.Inner, names);
                break;
            case TsType.Array a:
                Collect(a.Element, names);
                break;
            case TsType.Dictionary d:
                Collect(d.Value, names);
                if (d.Key is not null)
                {
                    Collect(d.Key, names);
                }
                break;
            case TsType.Generic g:
                names.Add(g.Name);
                foreach (var arg in g.TypeArguments)
                {
                    Collect(arg, names);
                }
                break;
            case TsType.Brand b:
                names.Add(b.Name);
                Collect(b.Inner, names);
                break;
            case TsType.StringUnion:
            case TsType.IntUnion:
            case TsType.Literal:
            case TsType.Primitive:
            case TsType.TypeParam:
                // No type refs to collect
                break;
            case TsType.InlineObject obj:
                foreach (var field in obj.Fields)
                {
                    Collect(field.Type, names);
                }
                break;
            case TsType.TaggedUnion tu:
                foreach (var variant in tu.Variants)
                {
                    Collect(variant.Type, names);
                }
                break;
            case TsType.Union u:
                foreach (var variant in u.Variants)
                {
                    Collect(variant, names);
                }
                break;
        }
    }
}
