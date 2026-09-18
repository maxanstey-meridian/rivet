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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await StartAnnotationApiServer(cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };

        // The defaulted scalar query param + inferred complex body — bound by MVC
        // exactly as the emitted contract describes (q + limit on the query string,
        // the JSON DTO as the body).
        var response = await http.PostAsync(
            "/api/tasks/search?q=alpha&limit=7",
            JsonBody(term: "alpha"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using (var doc = JsonDocument.Parse(body))
        {
            // The handler echoes the bound surface: item title "term=alpha" proves the
            // [FromQuery(Name="q")] wire name (the value arrived under q), pageSize 7
            // proves the explicit limit reached the handler, and totalCount 5 proves
            // the body's Term "alpha" was bound (its length, the TotalCount position).
            Assert.Equal(
                "term=alpha",
                doc.RootElement.GetProperty("items")[0].GetProperty("title").GetString()
            );
            Assert.Equal(7, doc.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(5, doc.RootElement.GetProperty("totalCount").GetInt32());
        }

        // The defaulted scalar proves the default end-to-end: omitting limit applies
        // the C# default 20 (still 200 — the parameter is optional), and the response
        // proves the actually bound value.
        var defaultLimit = await http.PostAsync(
            "/api/tasks/search?q=alpha",
            JsonBody(term: "alpha"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, defaultLimit.StatusCode);
        using (
            var doc = JsonDocument.Parse(await defaultLimit.Content.ReadAsStringAsync(cts.Token))
        )
        {
            Assert.Equal(20, doc.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(5, doc.RootElement.GetProperty("totalCount").GetInt32());
        }

        // The complex body is bound with value fidelity: the echoed totalCount is the
        // length of the received Term — "iota" (4) differs from the earlier "alpha"
        // (5), so the test proves the actually received body value, not a constant.
        var bodyEcho = await http.PostAsync(
            "/api/tasks/search?q=alpha&limit=7",
            JsonBody(term: "iota"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, bodyEcho.StatusCode);
        using (var doc = JsonDocument.Parse(await bodyEcho.Content.ReadAsStringAsync(cts.Token)))
        {
            Assert.Equal(7, doc.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(4, doc.RootElement.GetProperty("totalCount").GetInt32());
        }

        // A request violating the body's declared requiredness (an empty
        // application/json body binds null into the non-nullable request) produces
        // the automatic host 400 — the emitted contract's required requestBody
        // agrees with what MVC enforces.
        var emptyBody = await http.PostAsync(
            "/api/tasks/search?q=alpha&limit=7",
            new StringContent("", Encoding.UTF8, "application/json"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.BadRequest, emptyBody.StatusCode);

        // The real host enforces MVC requiredness on the non-nullable string bound
        // via [FromQuery(Name="q")]: omitting q yields the automatic 400 — the
        // emitted contract describes the required query param with the same wire
        // name (acceptance:mvc-inputs: requiredness agrees with enforced binding).
        var renamedQuery = await http.PostAsync(
            "/api/tasks/search?limit=3",
            JsonBody(term: "beta"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.BadRequest, renamedQuery.StatusCode);
    }

    [Fact]
    public async Task Host_Binds_FromRoute_Named_Param_Against_Its_Placeholder()
    {
        // [FromRoute(Name = "taskId")] must agree with the {taskId:guid} route
        // placeholder: MVC rejects a name mismatch when the route is built, so a
        // 200 PUT against the real placeholder proves the binding — the same wire
        // name the emitted-contract test asserts on the path parameter.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await StartAnnotationApiServer(cts.Token);

        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };

        var response = await http.PutAsync(
            $"/api/tasks/{Guid.NewGuid()}/status",
            new StringContent("""{"status":1}""", Encoding.UTF8, "application/json"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Builds the SearchTasksRequest JSON body with the given term.</summary>
    private static StringContent JsonBody(string term) =>
        new(
            $$"""{"term":"{{term}}","minPriority":1,"tag":null}""",
            Encoding.UTF8,
            "application/json"
        );

    [Fact]
    public async Task Emitted_Contract_Describes_The_Bound_Surface()
    {
        // Extraction over the real sample source must emit the search endpoint with
        // exactly the surface the live host binds (proved by the host tests above):
        // the FromQuery(Name="q") wire name, the required q, the defaulted optional
        // limit with its 20, and the required SearchTasksRequest body whose
        // properties (term, minPriority, tag) match what MVC binds by value.
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
        using var document = JsonDocument.Parse(File.ReadAllText(specPath));
        var root = document.RootElement;
        var search = root.GetProperty("paths").GetProperty("/api/tasks/search").GetProperty("post");

        // Parameters parsed from the emitted document, compared with the host-bound
        // surface the live test asserted.
        var emittedParams =
            new Dictionary<string, (string Location, bool Required, JsonElement Schema)>();
        foreach (var parameter in search.GetProperty("parameters").EnumerateArray())
        {
            emittedParams[parameter.GetProperty("name").GetString()!] = (
                parameter.GetProperty("in").GetString()!,
                parameter.GetProperty("required").GetBoolean(),
                parameter.GetProperty("schema")
            );
        }

        // q: the FromQuery(Name=) wire name, required — the host proves the missing
        // q request is a 400, so the contract must not declare it optional.
        Assert.Equal("query", emittedParams["q"].Location);
        Assert.True(emittedParams["q"].Required);
        // limit: the C# default makes it optional and the default is emitted —
        // the host proves omitting it binds 20 and returns 200.
        Assert.Equal("query", emittedParams["limit"].Location);
        Assert.False(emittedParams["limit"].Required);
        Assert.Equal(20, emittedParams["limit"].Schema.GetProperty("default").GetInt32());

        // Body: required (the host proves a bodyless request is a 400) and its
        // schema resolves to the SearchTasksRequest component whose properties are
        // the surface the host binds by value.
        var requestBody = search.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        var bodySchema = requestBody
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        var (bodyComponent, bodyComponentName) = ResolveSchemaComponent(bodySchema, root);
        Assert.Equal("SearchTasksRequest", bodyComponentName);
        var bodyProperties = bodyComponent.GetProperty("properties");
        Assert.True(bodyProperties.TryGetProperty("term", out _));
        Assert.True(bodyProperties.TryGetProperty("minPriority", out _));
        Assert.True(bodyProperties.TryGetProperty("tag", out _));
        var requiredFields = bodyComponent
            .GetProperty("required")
            .EnumerateArray()
            .Select(entry => entry.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("term", requiredFields);
        Assert.Contains("minPriority", requiredFields);
        Assert.DoesNotContain("tag", requiredFields);

        // FromRoute(Name="taskId"): the emitted path parameter carries the wire name
        // that matches the live route placeholder the host test exercises.
        var status = root.GetProperty("paths")
            .GetProperty("/api/tasks/{taskId}/status")
            .GetProperty("put");
        var pathParams = status
            .GetProperty("parameters")
            .EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "path")
            .ToList();
        var pathParam = Assert.Single(pathParams);
        Assert.Equal("taskId", pathParam.GetProperty("name").GetString());
        Assert.True(pathParam.GetProperty("required").GetBoolean());
    }

    /// <summary>
    /// Resolves a schema reference through the emitted document's components,
    /// returning the target component and its name; inline schemas are returned
    /// unchanged with a null name.
    /// </summary>
    private static (JsonElement Component, string? Name) ResolveSchemaComponent(
        JsonElement schema,
        JsonElement root
    )
    {
        if (
            schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("$ref", out var reference)
        )
        {
            var segments = reference.GetString()!.Split('/').Skip(1);
            var name = segments.Last();
            var current = root;
            foreach (var segment in segments)
            {
                current = current.GetProperty(Uri.UnescapeDataString(segment));
            }

            return (current, name);
        }

        return (schema, null);
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
