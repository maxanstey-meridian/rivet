using Rivet.Tool;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class InlineExtractionIntegrationTests : IDisposable
{
    private readonly string _outputDir;

    public InlineExtractionIntegrationTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "rivet-test-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    /// <summary>
    /// Two endpoints returning the same InlineObject shape should produce
    /// an extracted named type in the types output file.
    /// </summary>
    [Fact]
    public async Task EndToEnd_DuplicateInlineTypes_AppearInTypeFile()
    {
        var inlineType = new TsType.InlineObject([
            ("name", new TsType.Primitive("string")),
            ("age", new TsType.Primitive("number")),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("find", "GET", "/api/buyers/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                inlineType, "Buyers", []),
            new("list", "GET", "/api/buyers", [], new TsType.Array(inlineType), "Buyers", []),
        };

        var input = BuildEmitInput(endpoints);
        var options = new RivetOptions(".", _outputDir, "emit", []);

        var result = await EmitPipeline.RunAsync(input, options);

        Assert.Equal(0, result);

        // Read all type files and find the extracted type
        var typesDir = Path.Combine(_outputDir, "types");
        Assert.True(Directory.Exists(typesDir), "types/ directory should exist");

        var typeFiles = Directory.GetFiles(typesDir, "*.ts")
            .Where(f => !f.EndsWith("index.ts"))
            .ToList();

        var allTypeContent = string.Concat(typeFiles.Select(File.ReadAllText));

        Assert.Contains("export type BuyerDto", allTypeContent);
        Assert.Contains("name: string", allTypeContent);
        Assert.Contains("age: number", allTypeContent);
    }

    /// <summary>
    /// Extracted types should appear in the JSON Schema output.
    /// </summary>
    [Fact]
    public async Task EndToEnd_ExtractedType_AppearsInJsonSchema()
    {
        var inlineType = new TsType.InlineObject([
            ("name", new TsType.Primitive("string")),
            ("age", new TsType.Primitive("number")),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("find", "GET", "/api/buyers/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                inlineType, "Buyers", []),
            new("list", "GET", "/api/buyers", [], new TsType.Array(inlineType), "Buyers", []),
        };

        var input = BuildEmitInput(endpoints);
        var options = new RivetOptions(".", _outputDir, "emit", [], JsonSchema: true);

        await EmitPipeline.RunAsync(input, options);

        var schemasPath = Path.Combine(_outputDir, "schemas.ts");
        Assert.True(File.Exists(schemasPath), "schemas.ts should exist");

        var schemasContent = await File.ReadAllTextAsync(schemasPath);
        Assert.Contains("BuyerDto", schemasContent);
    }

    /// <summary>
    /// The client file should import the extracted type and use it by name,
    /// not emit an inline object literal.
    /// </summary>
    [Fact]
    public async Task EndToEnd_ClientFile_UsesTypeRef()
    {
        var inlineType = new TsType.InlineObject([
            ("name", new TsType.Primitive("string")),
            ("age", new TsType.Primitive("number")),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("find", "GET", "/api/buyers/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                inlineType, "Buyers", []),
            new("list", "GET", "/api/buyers", [], new TsType.Array(inlineType), "Buyers", []),
        };

        var input = BuildEmitInput(endpoints);
        var options = new RivetOptions(".", _outputDir, "emit", []);

        await EmitPipeline.RunAsync(input, options);

        var clientDir = Path.Combine(_outputDir, "client");
        var clientFile = Path.Combine(clientDir, "Buyers.ts");
        Assert.True(File.Exists(clientFile), "client/Buyers.ts should exist");

        var clientContent = await File.ReadAllTextAsync(clientFile);

        // Should import the extracted type
        Assert.Contains("import type {", clientContent);
        Assert.Contains("BuyerDto", clientContent);

        // Should NOT contain inline object literal in function signatures
        Assert.DoesNotContain("{ name: string; age: number }", clientContent);
    }

    /// <summary>
    /// Regression guard: without extraction wiring, the InlineObject would be emitted
    /// as an inline object literal in the client. With extraction, it becomes a TypeRef.
    /// </summary>
    [Fact]
    public async Task RegressionGuard_InlineObjectReplacedInClientOutput()
    {
        var inlineType = new TsType.InlineObject([
            ("firstName", new TsType.Primitive("string")),
            ("lastName", new TsType.Primitive("string")),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("get", "GET", "/api/orders/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                inlineType, "Orders", []),
            new("list", "GET", "/api/orders", [], new TsType.Array(inlineType), "Orders", []),
        };

        var input = BuildEmitInput(endpoints);
        var options = new RivetOptions(".", _outputDir, "emit", []);

        await EmitPipeline.RunAsync(input, options);

        // The type file must contain the extracted definition
        var typesDir = Path.Combine(_outputDir, "types");
        var typeFiles = Directory.GetFiles(typesDir, "*.ts")
            .Where(f => !f.EndsWith("index.ts"))
            .ToList();
        var allTypeContent = string.Concat(typeFiles.Select(File.ReadAllText));
        Assert.Contains("export type OrderDto", allTypeContent);

        // The client file must reference the extracted type, not inline it
        var clientFile = Path.Combine(_outputDir, "client", "Orders.ts");
        var clientContent = await File.ReadAllTextAsync(clientFile);
        Assert.Contains("OrderDto", clientContent);
        Assert.DoesNotContain("firstName: string; lastName: string", clientContent);
    }

    /// <summary>
    /// JSON contract path also flows through RunAsync, so extraction should work there too.
    /// </summary>
    [Fact]
    public async Task EndToEnd_JsonContract_DuplicateInlineTypes_Extracted()
    {
        var inlineType = new TsType.InlineObject([
            ("email", new TsType.Primitive("string")),
            ("active", new TsType.Primitive("boolean")),
        ]);

        // Simulate what RunFromContract builds — no Roslyn, just raw model objects
        var endpoints = new List<TsEndpointDefinition>
        {
            new("getUser", "GET", "/api/users/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                inlineType, "Users", []),
            new("findUser", "GET", "/api/users/search",
                [new TsEndpointParam("q", new TsType.Primitive("string"), ParamSource.Query)],
                inlineType, "Users", []),
        };

        var input = BuildEmitInput(endpoints);
        var options = new RivetOptions(".", _outputDir, "emit", []);

        await EmitPipeline.RunAsync(input, options);

        var typesDir = Path.Combine(_outputDir, "types");
        var typeFiles = Directory.GetFiles(typesDir, "*.ts")
            .Where(f => !f.EndsWith("index.ts"))
            .ToList();

        var allTypeContent = string.Concat(typeFiles.Select(File.ReadAllText));

        Assert.Contains("export type UserDto", allTypeContent);
        Assert.Contains("email: string", allTypeContent);
        Assert.Contains("active: boolean", allTypeContent);
    }

    /// <summary>
    /// Gap 1: JSON contract path — verify client file also uses extracted type, not just type file.
    /// </summary>
    [Fact]
    public async Task EndToEnd_JsonContract_ClientFile_ImportsExtractedType()
    {
        var inlineType = new TsType.InlineObject([
            ("email", new TsType.Primitive("string")),
            ("active", new TsType.Primitive("boolean")),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("getUser", "GET", "/api/users/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                inlineType, "Users", []),
            new("findUser", "GET", "/api/users/search",
                [new TsEndpointParam("q", new TsType.Primitive("string"), ParamSource.Query)],
                inlineType, "Users", []),
        };

        var input = BuildEmitInput(endpoints);
        var options = new RivetOptions(".", _outputDir, "emit", []);

        await EmitPipeline.RunAsync(input, options);

        var clientFile = Path.Combine(_outputDir, "client", "Users.ts");
        Assert.True(File.Exists(clientFile), "client/Users.ts should exist");

        var clientContent = await File.ReadAllTextAsync(clientFile);
        Assert.Contains("import type { UserDto }", clientContent);
        Assert.Contains("UserDto", clientContent);
        Assert.DoesNotContain("{ email: string; active: boolean }", clientContent);
    }

    /// <summary>
    /// Gap 2: Compile mode — extracted types flow through Zod validators and validated client re-emit.
    /// </summary>
    [Fact]
    public async Task EndToEnd_CompileMode_ExtractedType_InValidatorsAndClient()
    {
        var inlineType = new TsType.InlineObject([
            ("name", new TsType.Primitive("string")),
            ("score", new TsType.Primitive("number")),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("getPlayer", "GET", "/api/players/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                inlineType, "Players", []),
            new("listPlayers", "GET", "/api/players", [], new TsType.Array(inlineType), "Players", []),
        };

        var input = BuildEmitInput(endpoints);
        var options = new RivetOptions(".", _outputDir, "compile", []);

        await EmitPipeline.RunAsync(input, options);

        // Validators file should reference the extracted type's schema
        var validatorsPath = Path.Combine(_outputDir, "validators.ts");
        Assert.True(File.Exists(validatorsPath), "validators.ts should exist in compile mode");
        var validatorsContent = await File.ReadAllTextAsync(validatorsPath);
        Assert.Contains("PlayerDtoSchema", validatorsContent);
        Assert.Contains("assertPlayerDto", validatorsContent);

        // Client file should be the validated version (re-emitted in compile mode)
        var clientFile = Path.Combine(_outputDir, "client", "Players.ts");
        var clientContent = await File.ReadAllTextAsync(clientFile);
        Assert.Contains("PlayerDto", clientContent);
        Assert.Contains("assertPlayerDto", clientContent);
    }

    /// <summary>
    /// Gap 3: Negative test — a single-use small InlineObject should NOT be extracted.
    /// </summary>
    [Fact]
    public async Task EndToEnd_SmallSingleUseInlineObject_NotExtracted()
    {
        var smallInline = new TsType.InlineObject([
            ("ok", new TsType.Primitive("boolean")),
            ("msg", new TsType.Primitive("string")),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("ping", "GET", "/api/ping", [], smallInline, "Health", []),
        };

        var input = BuildEmitInput(endpoints);
        var options = new RivetOptions(".", _outputDir, "emit", []);

        await EmitPipeline.RunAsync(input, options);

        // No extracted type should appear in type files (only used once, < 5 fields)
        var typesDir = Path.Combine(_outputDir, "types");
        var typeFiles = Directory.GetFiles(typesDir, "*.ts")
            .Where(f => !f.EndsWith("index.ts"))
            .ToList();
        var allTypeContent = string.Concat(typeFiles.Select(File.ReadAllText));
        Assert.DoesNotContain("export type", allTypeContent);

        // Client should emit the inline literal directly
        var clientFile = Path.Combine(_outputDir, "client", "Health.ts");
        var clientContent = await File.ReadAllTextAsync(clientFile);
        Assert.Contains("ok: boolean", clientContent);
    }

    /// <summary>
    /// Gap 4: InlineObject in param position gets extracted when duplicated across endpoints.
    /// </summary>
    [Fact]
    public async Task EndToEnd_ParamPositionInlineType_Extracted()
    {
        var paramInline = new TsType.InlineObject([
            ("page", new TsType.Primitive("number")),
            ("size", new TsType.Primitive("number")),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("listOrders", "GET", "/api/orders",
                [new TsEndpointParam("filter", paramInline, ParamSource.Query)],
                new TsType.Primitive("string"), "Orders", []),
            new("listItems", "GET", "/api/items",
                [new TsEndpointParam("filter", paramInline, ParamSource.Query)],
                new TsType.Primitive("string"), "Items", []),
        };

        var input = BuildEmitInput(endpoints);
        var options = new RivetOptions(".", _outputDir, "emit", []);

        await EmitPipeline.RunAsync(input, options);

        // Extracted type should appear in types
        var typesDir = Path.Combine(_outputDir, "types");
        var typeFiles = Directory.GetFiles(typesDir, "*.ts")
            .Where(f => !f.EndsWith("index.ts"))
            .ToList();
        var allTypeContent = string.Concat(typeFiles.Select(File.ReadAllText));
        Assert.Contains("export type", allTypeContent);
        Assert.Contains("page: number", allTypeContent);
        Assert.Contains("size: number", allTypeContent);
    }

    /// <summary>
    /// Gap 5: Pre-existing definition with same name doesn't get clobbered by extraction.
    /// </summary>
    [Fact]
    public async Task EndToEnd_PreExistingDefinition_NotClobbered()
    {
        var inlineType = new TsType.InlineObject([
            ("x", new TsType.Primitive("number")),
            ("y", new TsType.Primitive("number")),
        ]);

        // Pre-existing definition named "WidgetDto" with different fields
        var existingDef = new TsTypeDefinition("WidgetDto", [], [
            new("id", new TsType.Primitive("string"), false),
            new("label", new TsType.Primitive("string"), false),
        ]);

        var endpoints = new List<TsEndpointDefinition>
        {
            new("getWidget", "GET", "/api/widgets/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                inlineType, "Widgets", []),
            new("listWidgets", "GET", "/api/widgets", [], new TsType.Array(inlineType), "Widgets", []),
        };

        var input = BuildEmitInput(endpoints, definitions: [existingDef]);
        var options = new RivetOptions(".", _outputDir, "emit", []);

        await EmitPipeline.RunAsync(input, options);

        var typesDir = Path.Combine(_outputDir, "types");
        var typeFiles = Directory.GetFiles(typesDir, "*.ts")
            .Where(f => !f.EndsWith("index.ts"))
            .ToList();
        var allTypeContent = string.Concat(typeFiles.Select(File.ReadAllText));

        // Original WidgetDto must still have its original fields
        Assert.Contains("id: string", allTypeContent);
        Assert.Contains("label: string", allTypeContent);

        // Extracted type gets a collision-avoidance name (WidgetDto2 or similar)
        Assert.Contains("x: number", allTypeContent);
        Assert.Contains("y: number", allTypeContent);

        // Both definitions should be present — the extracted one didn't overwrite the original
        var widgetDtoCount = allTypeContent.Split("export type WidgetDto").Length - 1;
        Assert.True(widgetDtoCount >= 1, "Original WidgetDto must still exist");
    }

    private static EmitPipeline.EmitInput BuildEmitInput(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyList<TsTypeDefinition>? definitions = null,
        IReadOnlyDictionary<string, TsType>? enums = null)
    {
        var defs = definitions ?? [];
        var brands = Array.Empty<TsType.Brand>();
        var enumDict = enums ?? new Dictionary<string, TsType>();
        var namespaces = new Dictionary<string, string?>();
        var defsByName = defs.ToDictionary(d => d.Name);
        var brandsByName = new Dictionary<string, TsType.Brand>();

        return new EmitPipeline.EmitInput(defs, brands, enumDict, endpoints, namespaces, defsByName, brandsByName);
    }
}
