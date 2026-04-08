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
    /// Stub ASP.NET MVC attributes so endpoint tests compile without referencing
    /// the full Microsoft.AspNetCore.Mvc package.
    /// </summary>
    private const string AspNetStubs = """
        namespace Microsoft.AspNetCore.Mvc
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpGetAttribute : System.Attribute
            {
                public HttpGetAttribute() { }
                public HttpGetAttribute(string template) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpPostAttribute : System.Attribute
            {
                public HttpPostAttribute() { }
                public HttpPostAttribute(string template) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpPutAttribute : System.Attribute
            {
                public HttpPutAttribute() { }
                public HttpPutAttribute(string template) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpDeleteAttribute : System.Attribute
            {
                public HttpDeleteAttribute() { }
                public HttpDeleteAttribute(string template) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpPatchAttribute : System.Attribute
            {
                public HttpPatchAttribute() { }
                public HttpPatchAttribute(string template) { }
            }
            public class ControllerBase { }
            [System.AttributeUsage(System.AttributeTargets.Parameter)]
            public class FromBodyAttribute : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Parameter)]
            public class FromFormAttribute : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Parameter)]
            public class FromQueryAttribute : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Parameter)]
            public class FromRouteAttribute : System.Attribute { }
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public class RouteAttribute : System.Attribute
            {
                public RouteAttribute(string template) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
            public class ProducesResponseTypeAttribute : System.Attribute
            {
                public ProducesResponseTypeAttribute(System.Type type, int statusCode) { }
                public ProducesResponseTypeAttribute(int statusCode) { }
            }
            public interface IActionResult { }
            public class ActionResult<TValue> { }
        }
        namespace Microsoft.AspNetCore.Http
        {
            public interface IFormFile { }
            public static class StatusCodes
            {
                public const int Status200OK = 200;
                public const int Status201Created = 201;
                public const int Status204NoContent = 204;
                public const int Status400BadRequest = 400;
                public const int Status404NotFound = 404;
                public const int Status409Conflict = 409;
            }
        }
        namespace Microsoft.AspNetCore.Http.HttpResults
        {
            public class Ok<TValue> { }
            public class Ok { }
            public class Created<TValue> { }
            public class Created { }
            public class Accepted<TValue> { }
            public class Accepted { }
            public class NoContent { }
            public class BadRequest<TValue> { }
            public class BadRequest { }
            public class UnauthorizedHttpResult { }
            public class NotFound<TValue> { }
            public class NotFound { }
            public class Conflict<TValue> { }
            public class Conflict { }
            public class UnprocessableEntity<TValue> { }
            public class UnprocessableEntity { }
            public class Results<T1, T2> { }
            public class Results<T1, T2, T3> { }
            public class Results<T1, T2, T3, T4> { }
            public class Results<T1, T2, T3, T4, T5> { }
            public class Results<T1, T2, T3, T4, T5, T6> { }
        }
        namespace Microsoft.AspNetCore.Builder
        {
            public static class EndpointRouteBuilderExtensions
            {
                public static void MapGet(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string pattern, System.Delegate handler) { }
                public static void MapPost(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string pattern, System.Delegate handler) { }
                public static void MapPut(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string pattern, System.Delegate handler) { }
                public static void MapDelete(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string pattern, System.Delegate handler) { }
                public static void MapPatch(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string pattern, System.Delegate handler) { }
            }
        }
        namespace Microsoft.AspNetCore.Routing
        {
            public interface IEndpointRouteBuilder { }
        }
        """;

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

        trees.Add(CSharpSyntaxTree.ParseText(AspNetStubs, new CSharpParseOptions(LanguageVersion.Latest)));

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
            CSharpSyntaxTree.ParseText(AspNetStubs, new CSharpParseOptions(LanguageVersion.Latest)),
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

        var mainTree = CSharpSyntaxTree.ParseText(mainSource + "\n" + AspNetStubs);
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
        ];
    }
}
