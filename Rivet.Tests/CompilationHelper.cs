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
    private static readonly MetadataReference[] _coreReferences = GetCoreReferences();

    // The second reference profile: the whole .NET and ASP.NET shared frameworks, so a fixture
    // can be written the way an app's Program.cs is.
    private static readonly Lazy<MetadataReference[]> _frameworkReferences = new(
        GetFrameworkReferences
    );

    /// <summary>
    /// Compiles sources against the whole .NET and ASP.NET shared frameworks plus
    /// Rivet.Attributes, nullable enabled, as a library or a console app (for top-level
    /// statements).
    /// </summary>
    public static Compilation CreateFrameworkCompilation(
        OutputKind kind,
        params string[] sources
    ) => CreateFrameworkCompilation(kind, [], sources);

    /// <summary>
    /// <see cref="CreateFrameworkCompilation(OutputKind, string[])"/>, also referencing
    /// <paramref name="references"/>, such as a <see cref="FrameworkLibrary"/>.
    /// </summary>
    public static Compilation CreateFrameworkCompilation(
        OutputKind kind,
        IEnumerable<MetadataReference> references,
        params string[] sources
    ) => CreateFrameworkCompilation("FrameworkApp", kind, references, sources);

    /// <summary>
    /// A library compiled from <paramref name="source"/> against the shared frameworks, as a
    /// reference without source: what an app sees of a NuGet package.
    /// </summary>
    public static MetadataReference FrameworkLibrary(string source)
    {
        using var image = new MemoryStream();
        var emitted = CreateFrameworkCompilation(
                "Lib",
                OutputKind.DynamicallyLinkedLibrary,
                [],
                [source]
            )
            .Emit(image);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(
                $"Test library doesn't emit:\n{string.Join("\n", emitted.Diagnostics)}"
            );
        }

        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static Compilation CreateFrameworkCompilation(
        string assemblyName,
        OutputKind kind,
        IEnumerable<MetadataReference> references,
        string[] sources
    )
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            sources.Select(source =>
                CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))
            ),
            [.. _frameworkReferences.Value, .. references],
            new CSharpCompilationOptions(
                kind,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        var errors = compilation
            .GetDiagnostics()
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
    /// Compiles multiple C# source files, each as a separate syntax tree.
    /// Use when sources contain file-scoped namespace declarations.
    /// </summary>
    public static Compilation CreateCompilationFromMultiple(
        string[] sources,
        string[]? paths = null
    )
    {
        var trees = new List<SyntaxTree>();
        for (var index = 0; index < sources.Length; index++)
        {
            trees.Add(
                CSharpSyntaxTree.ParseText(
                    sources[index],
                    new CSharpParseOptions(LanguageVersion.Latest),
                    paths?[index] ?? ""
                )
            );
        }

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            trees,
            _coreReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        var errors = compilation
            .GetDiagnostics()
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
            _coreReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        // Verify no compilation errors (warnings are OK)
        var errors = compilation
            .GetDiagnostics()
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
    public static (DiscoveredSymbols Discovered, TypeWalker Walker) DiscoverAndWalk(
        Compilation compilation
    )
    {
        var discovered = SymbolDiscovery.Discover(compilation);
        var walker = TypeWalker.Create(
            compilation,
            new Rivet.Tool.Analysis.WellKnownTypes(compilation),
            discovered.RivetTypes
        );
        return (discovered, walker);
    }

    public static IReadOnlyList<TsEndpointDefinition> WalkEndpoints(
        Compilation compilation,
        DiscoveredSymbols discovered,
        TypeWalker walker
    )
    {
        var wkt = new WellKnownTypes(compilation);
        return EndpointWalker.Walk(wkt, walker, discovered.EndpointMethods, discovered.ClientTypes);
    }

    public static IReadOnlyList<TsEndpointDefinition> WalkContracts(
        Compilation compilation,
        DiscoveredSymbols discovered,
        TypeWalker walker
    )
    {
        var wkt = new WellKnownTypes(compilation);
        return ContractWalker
            .Walk(compilation, wkt, walker, discovered.ContractTypes)
            .Select(contract => contract.Endpoint)
            .ToList();
    }

    /// <summary>Discovers and walks the compilation's contracts, then checks coverage.</summary>
    public static IReadOnlyList<CoverageWarning> CheckCoverage(
        Compilation compilation,
        string functionsRoutePrefix = "api"
    )
    {
        var (discovered, walker) = DiscoverAndWalk(compilation);
        var wkt = new WellKnownTypes(compilation);
        var contracts = ContractWalker.Walk(compilation, wkt, walker, discovered.ContractTypes);
        return CoverageChecker.Check(
            compilation,
            wkt,
            discovered,
            contracts,
            SecurityMetadataWalker.Walk(compilation),
            functionsRoutePrefix
        );
    }

    // --- Canonical walk/emit helpers (survive the Phase 3 pivot) ---

    /// <summary>
    /// Compiles source and runs the contract walker — the canonical analysis-side oracle.
    /// </summary>
    public static (IReadOnlyList<TsEndpointDefinition> Endpoints, TypeWalker Walker) WalkContract(
        string source
    )
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
    public static (IReadOnlyList<TsEndpointDefinition> Endpoints, TypeWalker Walker) WalkMerged(
        string source
    )
    {
        var compilation = CreateCompilation(source);
        var (discovered, walker) = DiscoverAndWalk(compilation);
        return (WalkMerged(compilation, discovered, walker), walker);
    }

    /// <summary>
    /// Runs both walkers over an already-walked compilation and merges them as Program.cs does.
    /// </summary>
    public static IReadOnlyList<TsEndpointDefinition> WalkMerged(
        Compilation compilation,
        DiscoveredSymbols discovered,
        TypeWalker walker
    ) =>
        EndpointMerger.Merge(
            ContractWalker.Walk(
                compilation,
                new WellKnownTypes(compilation),
                walker,
                discovered.ContractTypes
            ),
            WalkEndpoints(compilation, discovered, walker)
        );

    /// <summary>
    /// Compiles source, walks both walkers (merged like Program.cs), and emits OpenAPI
    /// as a parsed JSON document — the canonical emission-side oracle post-pivot.
    /// </summary>
    public static System.Text.Json.JsonDocument EmitOpenApi(
        string source,
        ContractSecurityMetadata? security = null
    )
    {
        var (endpoints, walker) = WalkMerged(source);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            security
        );
        return System.Text.Json.JsonDocument.Parse(json);
    }

    /// <summary>
    /// Captures what this flow writes to Console.Error while <paramref name="action"/> runs.
    /// Writes from other tests, which run in parallel on their own flows, never land in it.
    /// </summary>
    public static string CaptureStdErr(Action action) => FlowConsole.Error.Capture(action);

    /// <summary>
    /// Captures what this flow writes to Console.Out while <paramref name="action"/> runs.
    /// Writes from other tests, which run in parallel on their own flows, never land in it.
    /// </summary>
    public static string CaptureStdOut(Action action) => FlowConsole.Out.Capture(action);

    /// <summary>
    /// Console.Error and Console.Out are process-wide, so each is replaced once, for the whole
    /// test run, by a writer that routes every write by async flow: to the capture that flow
    /// set, else to the real stream. A capture never swaps the console, so it needs no lock.
    /// </summary>
    private static class FlowConsole
    {
        public static readonly FlowRoutedWriter Error = Install(Console.Error, Console.SetError);
        public static readonly FlowRoutedWriter Out = Install(Console.Out, Console.SetOut);

        private static FlowRoutedWriter Install(TextWriter original, Action<TextWriter> set)
        {
            var writer = new FlowRoutedWriter(original);
            set(writer);
            return writer;
        }
    }

    private sealed class FlowRoutedWriter(TextWriter original) : TextWriter
    {
        private readonly AsyncLocal<StringWriter?> _capture = new();

        private TextWriter Target => _capture.Value ?? original;

        public override System.Text.Encoding Encoding => original.Encoding;

        public override void Write(char value) => Target.Write(value);

        public override void Write(string? value) => Target.Write(value);

        public override void Write(char[] buffer, int index, int count) =>
            Target.Write(buffer, index, count);

        public override void WriteLine(string? value) => Target.WriteLine(value);

        public override void Flush() => Target.Flush();

        public string Capture(Action action)
        {
            var outer = _capture.Value;
            var writer = new StringWriter();
            _capture.Value = writer;
            try
            {
                action();
            }
            finally
            {
                // A synchronous method's AsyncLocal change outlives it, so restore the caller's.
                _capture.Value = outer;
            }

            return writer.ToString();
        }
    }

    /// <summary>
    /// Creates a compilation where domainSource lives in a separate "project" (CompilationReference),
    /// simulating types from a referenced project assembly.
    /// </summary>
    public static Compilation CreateCompilationWithProjectReference(
        string mainSource,
        string domainSource
    ) =>
        CompileReferencing(
            mainSource,
            domainSource,
            "DomainAssembly",
            "Domain test source",
            domain => domain.ToMetadataReference()
        );

    /// <summary>
    /// Creates a compilation where packageSource is a compiled assembly the main source
    /// references by its image, as it would a NuGet package: outside the walkable assemblies,
    /// and imported with <c>MetadataImportOptions.Public</c>, so its non-public members aren't
    /// symbols. Returns the image too, to load beside the main assembly at runtime.
    /// </summary>
    public static (
        Compilation Compilation,
        byte[] PackageImage
    ) CreateCompilationWithPackageReference(string mainSource, string packageSource)
    {
        byte[] packageImage = [];
        var compilation = CompileReferencing(
            mainSource,
            packageSource,
            "PackageAssembly",
            "Package test source",
            package =>
            {
                using var image = new MemoryStream();
                if (!package.Emit(image).Success)
                {
                    throw new InvalidOperationException("Package test source failed to emit.");
                }

                packageImage = image.ToArray();
                return MetadataReference.CreateFromImage(packageImage);
            }
        );
        return (compilation, packageImage);
    }

    /// <summary>
    /// <paramref name="mainSource"/> compiled against <paramref name="referencedSource"/>,
    /// compiled first as <paramref name="assemblyName"/> and referenced the way
    /// <paramref name="reference"/> makes it.
    /// </summary>
    private static Compilation CompileReferencing(
        string mainSource,
        string referencedSource,
        string assemblyName,
        string label,
        Func<CSharpCompilation, MetadataReference> reference
    )
    {
        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable
        );
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var referenced = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(referencedSource, parseOptions)],
            _coreReferences,
            options
        );
        ThrowOnErrors(referenced, label);

        var main = CSharpCompilation.Create(
            "TestAssembly",
            [CSharpSyntaxTree.ParseText(mainSource, parseOptions)],
            [.. _coreReferences, reference(referenced)],
            options
        );
        ThrowOnErrors(main, "Main test source");
        return main;
    }

    private static void ThrowOnErrors(Compilation compilation, string label)
    {
        var errors = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            var messages = string.Join("\n", errors.Select(e => e.ToString()));
            throw new InvalidOperationException($"{label} has compilation errors:\n{messages}");
        }
    }

    // --- Import pipeline helpers ---

    public static ImportResult Import(string json, string ns = "Test", string? security = null) =>
        OpenApiImporter.Import(json, new ImportOptions(ns, security));

    /// <summary>
    /// Compiles an import's generated C#, walks its types, contracts and security, and
    /// emits OpenAPI as a parsed JSON document: the CLI's import → emit path.
    /// </summary>
    public static System.Text.Json.JsonDocument EmitImported(ImportResult imported)
    {
        var compilation = CompileImportResult(imported);
        var (discovered, walker) = DiscoverAndWalk(compilation);
        var endpoints = WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            SecurityMetadataWalker.Walk(compilation)
        );
        return System.Text.Json.JsonDocument.Parse(json);
    }

    /// <summary>Imports <paramref name="spec"/> and emits it again through <see cref="EmitImported"/>.</summary>
    public static System.Text.Json.JsonDocument ImportCompileWalkAndEmit(
        string spec,
        string ns = "Test"
    ) => EmitImported(Import(spec, ns));

    /// <summary>
    /// Like <see cref="EmitImported"/>, but the walked contract goes out as contract JSON and
    /// back through <see cref="JsonContractReader"/> first, the way <c>--from</c> sees it.
    /// </summary>
    public static System.Text.Json.JsonDocument EmitImportedThroughContractJson(
        ImportResult imported
    )
    {
        var compilation = CompileImportResult(imported);
        var (discovered, walker) = DiscoverAndWalk(compilation);
        var endpoints = WalkContracts(compilation, discovered, walker);
        var contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            endpoints
        );
        var read = JsonContractReader.Read(contractJson);
        return System.Text.Json.JsonDocument.Parse(
            OpenApiEmitter.Emit(
                read.Endpoints,
                read.Types.ToDictionary(type => type.Name),
                read.Brands,
                read.Enums,
                security: null
            )
        );
    }

    public static string FindFile(ImportResult result, string fileName)
    {
        var file = result.Files.FirstOrDefault(f => f.FileName.EndsWith(fileName));
        Assert.NotNull(file);
        return file.Content;
    }

    public static Compilation CompileImportResult(ImportResult result) =>
        CreateCompilationFromMultiple(
            result.Files.Select(f => f.Content).ToArray(),
            result.Files.Select(f => f.FileName).ToArray()
        );

    public static string BuildSpec(
        string? schemas = null,
        string? paths = null,
        string title = "Test"
    )
    {
        var schemasBlock = schemas is not null
            ? $"\"components\": {{ \"schemas\": {{ {schemas} }} }},"
            : "";

        var pathsBlock = paths is not null ? $"\"paths\": {{ {paths} }}" : "\"paths\": {}";

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

    public static string EmitOpenApiFromJson(
        string json,
        OpenApiDocumentInfo? documentInfo = null,
        ContractSecurityMetadata? security = null
    )
    {
        var contract = JsonContractReader.Read(json);
        return OpenApiEmitter.Emit(
            contract.Endpoints,
            contract.Types.ToDictionary(t => t.Name),
            contract.Brands,
            contract.Enums,
            contract.WithSecuritySchemes(security),
            documentInfo
        );
    }

    // --- Emission helpers ---

    private static MetadataReference[] GetFrameworkReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var aspNetDir = Path.GetDirectoryName(
            typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location
        )!;
        return
        [
            .. Directory
                .GetFiles(runtimeDir, "*.dll")
                .Concat(Directory.GetFiles(aspNetDir, "*.dll"))
                .Where(IsManagedAssembly)
                .Select(path => MetadataReference.CreateFromFile(path)),
            MetadataReference.CreateFromFile(typeof(RivetTypeAttribute).Assembly.Location),
        ];

        static bool IsManagedAssembly(string path)
        {
            try
            {
                System.Reflection.AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }
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
            MetadataReference.CreateFromFile(
                Path.Combine(runtimeDir, "System.Runtime.Numerics.dll")
            ),
            MetadataReference.CreateFromFile(typeof(RivetTypeAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(
                typeof(System.ComponentModel.DataAnnotations.RequiredAttribute).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Mvc.ControllerBase).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Mvc.IActionResult).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Http.IResult).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Http.IFormFile).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Http.HttpResults.Ok<>).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions)
                    .Assembly
                    .Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Routing.RouteData).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute).Assembly.Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Builder.AuthorizationEndpointConventionBuilderExtensions)
                    .Assembly
                    .Location
            ),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.AspNetCore.Authorization.IAuthorizeData).Assembly.Location
            ),
            // [DataMember(Name = ...)]: the minimal-API form mapper's rename.
            MetadataReference.CreateFromFile(
                typeof(System.Runtime.Serialization.DataMemberAttribute).Assembly.Location
            ),
        ];
    }
}
