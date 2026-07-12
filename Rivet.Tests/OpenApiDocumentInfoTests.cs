using System.Text.Json;
using Rivet.Tool;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

/// <summary>
/// The --title/--version/--server → info/servers plumbing (P2 wave 1):
/// flag-driven emission, no-servers-unless-asked, and a byte-identical pin
/// that the no-flags default output is unchanged.
/// </summary>
public sealed class OpenApiDocumentInfoTests
{
    private const string Source = """
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record TaskDto(string Id, string Title);

        [RivetContract]
        public static class TasksContract
        {
            public static readonly Define GetTask =
                Define.Get<TaskDto>("/api/tasks/{id}");
        }
        """;

    private static string Emit(OpenApiDocumentInfo? documentInfo)
    {
        var compilation = CompilationHelper.CreateCompilation(Source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        return OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null,
            documentInfo
        );
    }

    [Fact]
    public void DocumentInfo_Sets_Info_Title_And_Version()
    {
        using var doc = JsonDocument.Parse(Emit(new OpenApiDocumentInfo("Orders API", "2.3.0")));
        var info = doc.RootElement.GetProperty("info");

        Assert.Equal("Orders API", info.GetProperty("title").GetString());
        Assert.Equal("2.3.0", info.GetProperty("version").GetString());
    }

    [Fact]
    public void Servers_Emitted_In_Order_When_Given()
    {
        using var doc = JsonDocument.Parse(
            Emit(new OpenApiDocumentInfo(Servers: ["https://api.example.com", "/relative-base"]))
        );

        var servers = doc.RootElement.GetProperty("servers");
        Assert.Equal(2, servers.GetArrayLength());
        Assert.Equal("https://api.example.com", servers[0].GetProperty("url").GetString());
        Assert.Equal("/relative-base", servers[1].GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(false)] // no documentInfo at all
    [InlineData(true)] // documentInfo present but Servers empty
    public void No_Servers_Block_Unless_A_Server_Was_Given(bool emptyList)
    {
        var json = Emit(emptyList ? new OpenApiDocumentInfo(Servers: []) : null);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("servers", out _));
    }

    [Fact]
    public void Default_Output_Is_ByteIdentical_With_And_Without_DocumentInfo()
    {
        var withoutInfo = Emit(null);
        var withDefaults = Emit(new OpenApiDocumentInfo());

        Assert.Equal(withoutInfo, withDefaults);
        Assert.Contains("\"title\": \"API\"", withoutInfo);
        Assert.Contains("\"version\": \"1.0.0\"", withoutInfo);
        Assert.DoesNotContain("\"servers\"", withoutInfo);
    }

    // ════════════════ End-to-end: RivetOptions → EmitPipeline ════════════════

    private static async Task<string> RunPipeline(RivetOptions options)
    {
        var compilation = CompilationHelper.CreateCompilation(Source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var input = new EmitPipeline.EmitInput(
            walker.Definitions.Values.ToList(),
            walker.Brands.Values.ToList(),
            walker.Enums,
            endpoints,
            walker.TypeNamespaces,
            walker.Definitions,
            walker.Brands
        );

        var exit = await EmitPipeline.RunAsync(input, options);
        Assert.Equal(0, exit);

        var specPath = Path.Combine(options.OutputDir!, "openapi.json");
        return await File.ReadAllTextAsync(specPath);
    }

    [Fact]
    public async Task Pipeline_Threads_Flags_Into_Spec()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rivet-docinfo-{Guid.NewGuid():N}");
        try
        {
            var spec = await RunPipeline(
                new RivetOptions(
                    "ignored",
                    tempDir,
                    [],
                    Quiet: true,
                    Title: "Orders API",
                    Version: "2.3.0",
                    Servers: ["https://api.example.com"]
                )
            );

            using var doc = JsonDocument.Parse(spec);
            var root = doc.RootElement;
            Assert.Equal("Orders API", root.GetProperty("info").GetProperty("title").GetString());
            Assert.Equal("2.3.0", root.GetProperty("info").GetProperty("version").GetString());
            Assert.Equal(
                "https://api.example.com",
                root.GetProperty("servers")[0].GetProperty("url").GetString()
            );
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Pipeline_Without_Flags_Matches_Direct_Default_Emit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rivet-docinfo-{Guid.NewGuid():N}");
        try
        {
            var spec = await RunPipeline(new RivetOptions("ignored", tempDir, [], Quiet: true));

            // The pipeline's no-flags output must be byte-identical to the
            // emitter's pre-flag default output.
            Assert.Equal(Emit(null), spec);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
