using System.Text.Json;
using Rivet.Tool;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Pins the post-Phase-3 emit pipeline: inline-type extraction feeds the OpenAPI
/// emitter (extracted definitions become component schemas, endpoint types become
/// $refs), and the spec path resolution rules (--output default, --openapi override).
/// Successor to the deleted InlineExtractionIntegrationTests (whose oracles were the
/// deleted TS outputs).
/// </summary>
public sealed class EmitPipelineTests : IDisposable
{
    private readonly TempDir _output = new();

    public void Dispose() => _output.Dispose();

    private static EmitPipeline.EmitInput BuildEmitInput(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyList<TsTypeDefinition>? definitions = null
    )
    {
        var defs = definitions ?? [];
        return new EmitPipeline.EmitInput(
            new Dictionary<string, TsType>(),
            endpoints,
            defs.ToDictionary(d => d.Name),
            new Dictionary<string, TsType.Brand>()
        );
    }

    private static List<TsEndpointDefinition> DuplicateInlineEndpoints()
    {
        var inlineType = new TsType.InlineObject([
            new("name", new TsType.Primitive("string")),
            new("age", new TsType.Primitive("number")),
        ]);

        return
        [
            new(
                "find",
                "GET",
                "/api/buyers/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                inlineType,
                "Buyers",
                [new TsResponseType(200, inlineType)]
            ),
            new(
                "list",
                "GET",
                "/api/buyers",
                [],
                new TsType.Array(inlineType),
                "Buyers",
                [new TsResponseType(200, new TsType.Array(inlineType))]
            ),
        ];
    }

    [Fact]
    public async Task ExtractedInlineTypes_Become_Component_Schemas_In_OpenApi()
    {
        var input = BuildEmitInput(DuplicateInlineEndpoints());
        var options = new RivetOptions(_output.FullName, []);

        var result = await EmitPipeline.RunAsync(input, options);

        Assert.Equal(0, result);

        var specPath = Path.Combine(_output.FullName, "openapi.json");
        Assert.True(
            File.Exists(specPath),
            "--output <dir> must write <dir>/openapi.json by default"
        );

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(specPath));
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // The duplicated inline object was extracted into a named component
        var extracted = schemas.GetProperty("BuyerFindDto");
        var props = extracted.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("number", props.GetProperty("age").GetProperty("type").GetString());

        // ...and the operations reference it instead of inlining the object
        var findSchema = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/buyers/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal(
            "#/components/schemas/BuyerFindDto",
            findSchema.GetProperty("$ref").GetString()
        );

        var listSchema = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/buyers")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("array", listSchema.GetProperty("type").GetString());
        Assert.Equal(
            "#/components/schemas/BuyerFindDto",
            listSchema.GetProperty("items").GetProperty("$ref").GetString()
        );
    }

    [Fact]
    public async Task Extraction_Does_Not_Clobber_PreExisting_Definition()
    {
        // A definition named like the would-be extracted type already exists;
        // extraction must pick a collision-avoidance name, not overwrite it.
        var inlineType = new TsType.InlineObject([
            new("x", new TsType.Primitive("number")),
            new("y", new TsType.Primitive("number")),
        ]);

        var existingDef = new TsTypeDefinition(
            "WidgetGetWidgetDto",
            [],
            [
                new TsPropertyDefinition("id", new TsType.Primitive("string"), IsOptional: false),
                new TsPropertyDefinition(
                    "label",
                    new TsType.Primitive("string"),
                    IsOptional: false
                ),
            ]
        );

        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "getWidget",
                "GET",
                "/api/widgets/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
                inlineType,
                "Widget",
                [new TsResponseType(200, inlineType)]
            ),
            new(
                "listWidgets",
                "GET",
                "/api/widgets",
                [],
                new TsType.Array(inlineType),
                "Widget",
                [new TsResponseType(200, new TsType.Array(inlineType))]
            ),
        };

        var input = BuildEmitInput(endpoints, [existingDef]);
        var options = new RivetOptions(_output.FullName, []);

        await EmitPipeline.RunAsync(input, options);

        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_output.FullName, "openapi.json"))
        );
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Original keeps its fields
        var original = schemas.GetProperty("WidgetGetWidgetDto").GetProperty("properties");
        Assert.True(original.TryGetProperty("id", out _));
        Assert.True(original.TryGetProperty("label", out _));

        // Extracted type landed under a collision-avoidance name with its own fields
        var extractedName = schemas
            .EnumerateObject()
            .Select(p => p.Name)
            .Single(n => n.StartsWith("WidgetGetWidget") && n != "WidgetGetWidgetDto");
        var extracted = schemas.GetProperty(extractedName).GetProperty("properties");
        Assert.True(extracted.TryGetProperty("x", out _));
        Assert.True(extracted.TryGetProperty("y", out _));
    }

    /// <summary>
    /// A variant's own inline object is its {Base}_{Tag} component, never an extracted Dto:
    /// extraction used to swap a five-field (or repeated) variant for a $ref, which cut it
    /// off from its authored tag schema and crashed the emitter. What sits inside the variant
    /// is still extracted.
    /// </summary>
    [Fact]
    public async Task Endpoint_Inline_Tagged_Union_Variant_Keeps_Its_Authored_Tag_Schema()
    {
        const string str = """{ "kind": "primitive", "type": "string" }""";
        var address = $$"""
            { "kind": "inlineObject", "properties": [
              { "name": "line1", "type": {{str}} }, { "name": "line2", "type": {{str}} },
              { "name": "city", "type": {{str}} }, { "name": "region", "type": {{str}} },
              { "name": "postcode", "type": {{str}} }] }
            """;
        var union = $$"""
            { "kind": "taggedUnion", "discriminator": "kind", "variants": [{
              "tag": "cat",
              "discriminatorSchema": "{\"type\":\"string\",\"enum\":[\"cat\"],\"description\":\"authored\"}",
              "type": { "kind": "inlineObject", "properties": [
                { "name": "kind", "type": { "kind": "stringUnion", "values": ["cat"] } },
                { "name": "name", "type": {{str}} }, { "name": "colour", "type": {{str}} },
                { "name": "lives", "type": {{str}} }, { "name": "home", "type": {{address}} }] }
            }] }
            """;
        var json = $$"""
            {
              "types": [], "enums": [],
              "endpoints": [
                { "name": "get", "httpMethod": "GET", "routeTemplate": "/api/pets/{id}", "params": [],
                  "controllerName": "pets", "returnType": {{union}},
                  "responses": [{ "statusCode": 200, "dataType": {{union}} }] },
                { "name": "list", "httpMethod": "GET", "routeTemplate": "/api/pets", "params": [],
                  "controllerName": "pets", "returnType": {{union}},
                  "responses": [{ "statusCode": 200, "dataType": {{union}} }] }
              ]
            }
            """;
        var (types, enums, endpoints, brands, _) = JsonContractReader.Read(json);
        var input = new EmitPipeline.EmitInput(
            enums,
            endpoints,
            types.ToDictionary(type => type.Name),
            brands
        );

        Assert.Equal(0, await EmitPipeline.RunAsync(input, new RivetOptions(_output.FullName, [])));

        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_output.FullName, "openapi.json"))
        );
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var variant = schemas
            .EnumerateObject()
            .Single(schema =>
                schema.Value.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("lives", out _)
            );
        Assert.Equal("KindUnion_Cat", variant.Name);
        var tag = variant.Value.GetProperty("properties").GetProperty("kind");
        Assert.Equal("authored", tag.GetProperty("description").GetString());
        Assert.Equal("cat", Assert.Single(tag.GetProperty("enum").EnumerateArray()).GetString());
        var home = variant.Value.GetProperty("properties").GetProperty("home");
        Assert.StartsWith("#/components/schemas/", home.GetProperty("$ref").GetString());
    }

    // ========== --verify: the drift gate ==========

    [Fact]
    public async Task Verify_Passes_When_Spec_Matches_And_Writes_Nothing()
    {
        var input = BuildEmitInput(DuplicateInlineEndpoints());
        Assert.Equal(0, await EmitPipeline.RunAsync(input, new RivetOptions(_output.FullName, [])));

        var specPath = Path.Combine(_output.FullName, "openapi.json");
        var writtenAt = File.GetLastWriteTimeUtc(specPath);

        var result = await EmitPipeline.RunAsync(
            input,
            new RivetOptions(_output.FullName, [], Verify: true)
        );

        Assert.Equal(0, result);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(specPath));
    }

    [Fact]
    public async Task Verify_Fails_On_Stale_Spec_Without_Overwriting_It()
    {
        var input = BuildEmitInput(DuplicateInlineEndpoints());
        await EmitPipeline.RunAsync(input, new RivetOptions(_output.FullName, []));

        var specPath = Path.Combine(_output.FullName, "openapi.json");
        var tampered = (await File.ReadAllTextAsync(specPath)).Replace("\"age\"", "\"years\"");
        await File.WriteAllTextAsync(specPath, tampered);

        int result = -1;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            result = EmitPipeline
                .RunAsync(input, new RivetOptions(_output.FullName, [], Verify: true))
                .GetAwaiter()
                .GetResult()
        );

        Assert.Equal(1, result);
        Assert.Contains("is stale", stderr);
        Assert.Equal(tampered, await File.ReadAllTextAsync(specPath));
    }

    [Fact]
    public async Task Verify_Fails_When_Spec_File_Is_Missing()
    {
        var input = BuildEmitInput(DuplicateInlineEndpoints());

        int result = -1;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            result = EmitPipeline
                .RunAsync(input, new RivetOptions(_output.FullName, [], Verify: true))
                .GetAwaiter()
                .GetResult()
        );

        Assert.Equal(1, result);
        Assert.Contains("does not exist", stderr);
        Assert.False(
            File.Exists(Path.Combine(_output.FullName, "openapi.json")),
            "--verify must never write"
        );
    }

    [Fact]
    public async Task OpenApiOverride_Is_Sole_Writer_When_Both_Given()
    {
        // The rivet-ts vite plugin passes both --output <dir>/rivet and --openapi <abs path>.
        // The override wins; nothing is duplicated into the output directory.
        var specDir = Path.Combine(_output.FullName, "spec-home");
        var overridePath = Path.Combine(specDir, "api-spec.json");
        var input = BuildEmitInput(DuplicateInlineEndpoints());
        var options = new RivetOptions(
            Path.Combine(_output.FullName, "rivet"),
            [],
            OpenApiPath: overridePath
        );

        var result = await EmitPipeline.RunAsync(input, options);

        Assert.Equal(0, result);
        Assert.True(File.Exists(overridePath), "--openapi <abs path> must be honored");
        Assert.False(
            File.Exists(Path.Combine(_output.FullName, "rivet", "openapi.json")),
            "--openapi is the sole writer when given"
        );

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(overridePath));
        Assert.Equal("3.1.0", doc.RootElement.GetProperty("openapi").GetString());
    }

    [Fact]
    public async Task RelativeOpenApiPath_Resolves_Against_OutputDir()
    {
        // The rivet-ts scaffold's generate script relies on this: --openapi ../openapi.json
        // resolved against --output lands the spec next to the output directory.
        var outputDir = Path.Combine(_output.FullName, "client", "generated", "rivet");
        var input = BuildEmitInput(DuplicateInlineEndpoints());
        var options = new RivetOptions(
            outputDir,
            [],
            OpenApiPath: Path.Combine("..", "openapi.json")
        );

        var result = await EmitPipeline.RunAsync(input, options);

        Assert.Equal(0, result);
        Assert.True(
            File.Exists(Path.Combine(_output.FullName, "client", "generated", "openapi.json"))
        );
    }
}
