using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project Rivet.Tool -- <file.cs> [file2.cs ...]");
    return 1;
}

var syntaxTrees = new List<SyntaxTree>();

foreach (var path in args)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 1;
    }

    var source = File.ReadAllText(path);
    syntaxTrees.Add(CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)));
}

var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
var references = new MetadataReference[]
{
    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
    MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
    MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
    MetadataReference.CreateFromFile(typeof(Rivet.RivetTypeAttribute).Assembly.Location),
};

var compilation = CSharpCompilation.Create(
    "RivetInput",
    syntaxTrees,
    references,
    new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        nullableContextOptions: NullableContextOptions.Enable));

var errors = compilation.GetDiagnostics()
    .Where(d => d.Severity == DiagnosticSeverity.Error)
    .ToList();

if (errors.Count > 0)
{
    Console.Error.WriteLine("C# compilation errors:");
    foreach (var error in errors)
    {
        Console.Error.WriteLine($"  {error}");
    }
    return 1;
}

var walker = TypeWalker.Create(compilation);
var endpoints = EndpointWalker.Walk(compilation, walker);
var definitions = walker.Definitions.Values.ToList();

Console.WriteLine("=== types.ts ===");
Console.Write(TypeEmitter.Emit(definitions));

if (endpoints.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("=== client.ts ===");
    Console.Write(ClientEmitter.Emit(endpoints, definitions));
}

return 0;
