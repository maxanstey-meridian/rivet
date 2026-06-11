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

    // --- Canonical walk/emit helpers (survive the Phase 3 pivot) ---

    /// <summary>
    /// Compiles source and runs the contract walker — the canonical analysis-side oracle.
    /// </summary>
    public static (IReadOnlyList<TsEndpointDefinition> Endpoints, TypeWalker Walker) WalkContract(string source)
    {
        var compilation = CreateCompilation(source);
        var (discovered, walker) = DiscoverAndWalk(compilation);
        var endpoints = WalkContracts(compilation, discovered, walker);
        return (endpoints, walker);
    }

    /// <summary>
    /// Compiles source, runs both walkers, and merges via the production EndpointMerger —
    /// the same pipeline as Program.cs.
    /// </summary>
    public static (IReadOnlyList<TsEndpointDefinition> Endpoints, TypeWalker Walker) WalkMerged(string source)
    {
        var compilation = CreateCompilation(source);
        var (discovered, walker) = DiscoverAndWalk(compilation);
        var contractEndpoints = WalkContracts(compilation, discovered, walker);
        var annotationEndpoints = WalkEndpoints(compilation, discovered, walker);
        var merged = EndpointMerger.Merge(contractEndpoints, annotationEndpoints);
        return (merged, walker);
    }

    /// <summary>
    /// Compiles source, walks both walkers (merged like Program.cs), and emits OpenAPI
    /// as a parsed JSON document — the canonical emission-side oracle post-pivot.
    /// </summary>
    public static System.Text.Json.JsonDocument EmitOpenApi(string source)
    {
        var (endpoints, walker) = WalkMerged(source);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        return System.Text.Json.JsonDocument.Parse(json);
    }

    private static readonly object StdErrLock = new();

    /// <summary>
    /// Captures everything written to Console.Error while <paramref name="action"/> runs.
    /// Serialized behind a process-wide lock because Console.SetError is global —
    /// concurrent captures from parallel test collections would otherwise race.
    /// Assert with Contains (other tests may emit unrelated warnings concurrently).
    /// </summary>
    public static string CaptureStdErr(Action action)
    {
        lock (StdErrLock)
        {
            var original = Console.Error;
            using var writer = new StringWriter();
            try
            {
                Console.SetError(writer);
                action();
            }
            finally
            {
                Console.SetError(original);
            }

            return writer.ToString();
        }
    }

    /// <summary>
    /// Creates a compilation where domainSource lives in a separate "project" (CompilationReference),
    /// simulating types from a referenced project assembly.
    /// </summary>
    public static Compilation CreateCompilationWithProjectReference(string mainSource, string domainSource)
    {
        var domainTree = CSharpSyntaxTree.ParseText(domainSource, new CSharpParseOptions(LanguageVersion.Latest));
        var domainCompilation = CSharpCompilation.Create(
            "DomainAssembly",
            [domainTree],
            CoreReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        ThrowOnErrors(domainCompilation, "Domain test source");

        var mainTree = CSharpSyntaxTree.ParseText(mainSource, new CSharpParseOptions(LanguageVersion.Latest));
        var mainCompilation = CSharpCompilation.Create(
            "TestAssembly",
            [mainTree],
            [.. CoreReferences, domainCompilation.ToMetadataReference()],
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        ThrowOnErrors(mainCompilation, "Main test source");

        return mainCompilation;
    }

    private static void ThrowOnErrors(Compilation compilation, string label)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            var messages = string.Join("\n", errors.Select(e => e.ToString()));
            throw new InvalidOperationException($"{label} has compilation errors:\n{messages}");
        }
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

    public static string EmitOpenApiFromJson(string json)
    {
        var (types, enums, endpoints, brands) = JsonContractReader.Read(json);
        var definitions = types.ToDictionary(t => t.Name);
        return OpenApiEmitter.Emit(endpoints, definitions, brands, enums, security: null);
    }

    // --- Emission helpers ---

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
