using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
        """;

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

    private static MetadataReference[] GetCoreReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Text.Json.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
            MetadataReference.CreateFromFile(typeof(RivetTypeAttribute).Assembly.Location),
        ];
    }
}
