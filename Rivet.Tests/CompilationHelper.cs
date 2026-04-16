using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Creates in-memory C# compilations for testing the type walker.
/// </summary>
public static class CompilationHelper
{
    private static readonly MetadataReference[] CoreReferences = GetCoreReferences();

    /// <summary>
    /// Compiles multiple C# source files, each as a separate syntax tree.
    /// Use when sources contain file-scoped namespace declarations.
    /// </summary>
    public static Compilation CreateCompilationFromMultiple(string[] sources)
    {
        var trees = new List<SyntaxTree>();
        foreach (var source in sources)
        {
            trees.Add(CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)));
        }

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            trees,
            CoreReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            var messages = string.Join("\n", errors.Select(e => e.ToString()));
            throw new InvalidOperationException($"Test source has compilation errors:\n{messages}");
        }

        return compilation;
    }

    /// <summary>
    /// Compiles C# source with Rivet.Attributes referenced and nullable enabled.
    /// </summary>
    public static Compilation CreateCompilation(string source)
    {
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)),
        };

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees,
            CoreReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        // Verify no compilation errors (warnings are OK)
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            var messages = string.Join("\n", errors.Select(e => e.ToString()));
            throw new InvalidOperationException($"Test source has compilation errors:\n{messages}");
        }

        return compilation;
    }

    /// <summary>
    /// Runs single-pass discovery and creates a TypeWalker — the same path as Program.cs.
    /// </summary>
    public static (DiscoveredSymbols Discovered, TypeWalker Walker) DiscoverAndWalk(Compilation compilation)
    {
        var discovered = SymbolDiscovery.Discover(compilation);
        var walker = TypeWalker.Create(compilation, discovered.RivetTypes);
        return (discovered, walker);
    }

    public static IReadOnlyList<TsEndpointDefinition> WalkEndpoints(
        Compilation compilation, DiscoveredSymbols discovered, TypeWalker walker)
    {
        var wkt = new WellKnownTypes(compilation);
        return EndpointWalker.Walk(wkt, walker, discovered.EndpointMethods, discovered.ClientTypes);
    }

    public static IReadOnlyList<TsEndpointDefinition> WalkContracts(
        Compilation compilation, DiscoveredSymbols discovered, TypeWalker walker)
    {
        var wkt = new WellKnownTypes(compilation);
        return ContractWalker.Walk(compilation, wkt, walker, discovered.ContractTypes);
    }

    public static IReadOnlyList<CoverageWarning> CheckCoverage(
        Compilation compilation, IReadOnlyList<TsEndpointDefinition> contractEndpoints)
    {
        var wkt = new WellKnownTypes(compilation);
        return CoverageChecker.Check(compilation, wkt, contractEndpoints);
    }

    /// <summary>
    /// Creates a compilation where domainSource lives in a separate "project" (CompilationReference),
    /// simulating types from a referenced project assembly.
    /// </summary>
    public static Compilation CreateCompilationWithProjectReference(string mainSource, string domainSource)
    {
        var domainTree = CSharpSyntaxTree.ParseText(domainSource);
        var domainCompilation = CSharpCompilation.Create(
            "DomainAssembly",
            [domainTree],
            CoreReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var mainTree = CSharpSyntaxTree.ParseText(mainSource);
        return CSharpCompilation.Create(
            "TestAssembly",
            [mainTree],
            [.. CoreReferences, domainCompilation.ToMetadataReference()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    // --- Import pipeline helpers ---

    public static ImportResult Import(string json, string ns = "Test", string? security = null)
        => OpenApiImporter.Import(json, new ImportOptions(ns, security));

    public static string FindFile(ImportResult result, string fileName)
    {
        var file = result.Files.FirstOrDefault(f => f.FileName.EndsWith(fileName));
        Assert.NotNull(file);
        return file.Content;
    }

    public static Compilation CompileImportResult(ImportResult result)
        => CreateCompilationFromMultiple(result.Files.Select(f => f.Content).ToArray());

    public static string BuildSpec(string? schemas = null, string? paths = null, string title = "Test")
    {
        var schemasBlock = schemas is not null
            ? $"\"components\": {{ \"schemas\": {{ {schemas} }} }},"
            : "";

        var pathsBlock = paths is not null
            ? $"\"paths\": {{ {paths} }}"
            : "\"paths\": {}";

        return $$"""
            {
                "openapi": "3.1.0",
                "info": { "title": "{{title}}", "version": "1.0.0" },
                {{schemasBlock}}
                {{pathsBlock}}
            }
            """;
    }

    // --- JSON contract helpers ---

    public static string EmitTypesFromJson(string json)
    {
        var (types, enums, _) = JsonContractReader.Read(json);
        var grouping = TypeGrouper.Group(types, [], enums, new Dictionary<string, string?>());
        return string.Concat(grouping.Groups.Select(TypeEmitter.EmitGroupFile));
    }

    public static string EmitClientFromJson(string json)
    {
        var (types, enums, endpoints) = JsonContractReader.Read(json);
        var grouping = TypeGrouper.Group(types, [], enums, new Dictionary<string, string?>());
        var typeFileMap = grouping.BuildTypeFileMap();
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        return string.Concat(
            controllerGroups.Select(g => ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
    }

    public static string EmitOpenApiFromJson(string json)
    {
        var (types, enums, endpoints) = JsonContractReader.Read(json);
        var definitions = types.ToDictionary(t => t.Name);
        var brands = new Dictionary<string, TsType.Brand>();
        return OpenApiEmitter.Emit(endpoints, definitions, brands, enums, security: null);
    }

    public static string EmitZodFromJson(string json)
    {
        var (types, enums, endpoints) = JsonContractReader.Read(json);
        var grouping = TypeGrouper.Group(types, [], enums, new Dictionary<string, string?>());
        var typeFileMap = grouping.BuildTypeFileMap();
        return ZodValidatorEmitter.Emit(endpoints, typeFileMap);
    }

    // --- Emission helpers ---

    public static string EmitTypes(string source)
    {
        var compilation = CreateCompilation(source);
        var (_, walker) = DiscoverAndWalk(compilation);
        var definitions = walker.Definitions.Values.ToList();
        var brands = walker.Brands.Values.ToList();
        var grouping = TypeGrouper.Group(definitions, brands, walker.Enums, walker.TypeNamespaces);
        return string.Concat(grouping.Groups.Select(TypeEmitter.EmitGroupFile));
    }

    public static string EmitSchemas(string source)
    {
        var compilation = CreateCompilation(source);
        var (_, walker) = DiscoverAndWalk(compilation);
        return JsonSchemaEmitter.Emit(walker.Definitions, walker.Brands, walker.Enums);
    }

    public static Dictionary<string, string> BuildTypeFileMap(TypeWalker walker)
    {
        var grouping = TypeGrouper.Group(
            walker.Definitions.Values.ToList(),
            walker.Brands.Values.ToList(),
            walker.Enums,
            walker.TypeNamespaces);
        return grouping.BuildTypeFileMap();
    }

    private static MetadataReference[] GetCoreReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Text.Json.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Memory.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Private.Uri.dll")),
            MetadataReference.CreateFromFile(typeof(RivetTypeAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.ComponentModel.DataAnnotations.RequiredAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Mvc.ControllerBase).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Mvc.IActionResult).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Http.IResult).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Http.IFormFile).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Http.HttpResults.Ok<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.AspNetCore.Routing.RouteData).Assembly.Location),
        ];
    }
}
