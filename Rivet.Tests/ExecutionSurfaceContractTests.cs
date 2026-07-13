using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace Rivet.Tests;

public sealed class ExecutionSurfaceContractTests
{
    private const string SupportedSurface = """
        using System.IO;
        using System.Text;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Rivet;

        namespace ExecutionSurface;

        public sealed record Input(string Value);
        public sealed record Output(string Value);
        public sealed record Problem(string Message);

        [RivetContract]
        public static class Contract
        {
            public static readonly RouteDefinition<Input, Output> InputOutput =
                Define.Post<Input, Output>("/input-output")
                    .Returns<Problem>(422);

            public static readonly RouteDefinition<Output> Output =
                Define.Get<Output>("/output")
                    .Returns<Problem>(404);

            public static readonly InputRouteDefinition<Input> InputOnly =
                Define.Put("/input-only")
                    .Accepts<Input>()
                    .Returns<Problem>(409);

            public static readonly RouteDefinition Empty =
                Define.Delete("/empty")
                    .Returns(404);

            public static readonly FileRouteDefinition File =
                Define.File("/file")
                    .ContentType("text/plain")
                    .Returns<Problem>(404);

            public static readonly FileRouteDefinition<Input> InputFile =
                Define.File<Input>("/input-file")
                    .ContentType("application/octet-stream")
                    .Returns<Problem>(404);

            public static readonly RouteDefinition<Input, Output> OrdinaryBinary =
                Define.Post<Input, Output>("/ordinary-binary")
                    .ProducesFile("application/pdf");

            public static readonly RouteDefinition<Output> RangeError =
                Define.Get<Output>("/range-error")
                    .Returns<Problem>("4XX");

            public static readonly RouteDefinition<Output> DefaultError =
                Define.Get<Output>("/default-error")
                    .Returns<Problem>("default");

            public static readonly RouteDefinition Suppressed =
                Define.Get("/suppressed")
                    .SuppressImplicitResponse()
                    .Returns<Problem>("default");
        }

        public static class Scenarios
        {
            public static IActionResult DirectTypedSuccess() =>
                Contract.Output.Success(new Output("direct")).ToActionResult();

            public static IResult MinimalTypedSuccess() =>
                Contract.Output.Success(new Output("minimal")).ToResult();

            public static IActionResult BoundTypedSuccess()
            {
                var endpoint = Contract.InputOutput.Bind(new Input("request"));
                var output = new Output("bound");
                return endpoint.Success(output).ToActionResult();
            }

            public static IActionResult BoundBodylessSuccess()
            {
                var endpoint = Contract.InputOnly.Bind(new Input("request"));
                return endpoint.Success().ToActionResult();
            }

            public static IActionResult DirectBodylessSuccess() =>
                Contract.Empty.Success().ToActionResult();

            public static IActionResult TypedError() =>
                Contract.Output.Error(404, new Problem("missing")).ToActionResult();

            public static IActionResult BodylessError() =>
                Contract.Empty.Error(404).ToActionResult();

            public static IActionResult BoundError() =>
                Contract.InputOutput
                    .Bind(new Input("request"))
                    .Error(422, new Problem("invalid"))
                    .ToActionResult();

            public static IActionResult RangeError() =>
                Contract.RangeError.Error(429, new Problem("slow down")).ToActionResult();

            public static IActionResult DefaultError() =>
                Contract.DefaultError.Error(503, new Problem("unavailable")).ToActionResult();

            public static IActionResult SuppressedError() =>
                Contract.Suppressed.Error(503, new Problem("unavailable")).ToActionResult();

            public static IActionResult DirectFile() =>
                Contract.File.File(Encoding.UTF8.GetBytes("file"), "sample.txt").ToActionResult();

            public static IActionResult StreamFile() =>
                Contract.File.File(new MemoryStream(new byte[] { 1, 2, 3 })).ToActionResult();

            public static IActionResult PhysicalFile() =>
                Contract.File.File(Path.GetFullPath("sample.txt")).ToActionResult();

            public static IActionResult BoundFile() =>
                Contract.InputFile
                    .Bind(new Input("request"))
                    .File(new byte[] { 1, 2, 3 }, "sample.bin")
                    .ToActionResult();

            public static IActionResult OrdinaryBinaryFile() =>
                Contract.OrdinaryBinary
                    .Bind(new Input("request"))
                    .File(new byte[] { 0x25, 0x50, 0x44, 0x46 }, "sample.pdf")
                    .ToActionResult();
        }
        """;

    [Fact]
    public async Task Supported_surface_compiles_and_has_observable_wire_outcomes()
    {
        var assembly = CompileAndLoad(SupportedSurface);

        var direct = await ExecuteMvcAsync(Invoke<IActionResult>(assembly, "DirectTypedSuccess"));
        Assert.Equal(StatusCodes.Status200OK, direct.StatusCode);
        Assert.Contains("direct", direct.BodyText);

        var bound = await ExecuteMvcAsync(Invoke<IActionResult>(assembly, "BoundTypedSuccess"));
        Assert.Equal(StatusCodes.Status201Created, bound.StatusCode);
        Assert.Contains("bound", bound.BodyText);

        var bodyless = await ExecuteMvcAsync(
            Invoke<IActionResult>(assembly, "DirectBodylessSuccess")
        );
        Assert.Equal(StatusCodes.Status204NoContent, bodyless.StatusCode);
        Assert.Empty(bodyless.Body);

        var exactError = await ExecuteMvcAsync(Invoke<IActionResult>(assembly, "TypedError"));
        Assert.Equal(StatusCodes.Status404NotFound, exactError.StatusCode);
        Assert.Contains("missing", exactError.BodyText);

        var rangeError = await ExecuteMvcAsync(Invoke<IActionResult>(assembly, "RangeError"));
        Assert.Equal(StatusCodes.Status429TooManyRequests, rangeError.StatusCode);
        Assert.Contains("slow down", rangeError.BodyText);

        var defaultError = await ExecuteMvcAsync(Invoke<IActionResult>(assembly, "DefaultError"));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, defaultError.StatusCode);
        Assert.Contains("unavailable", defaultError.BodyText);

        var file = await ExecuteMvcAsync(Invoke<IActionResult>(assembly, "DirectFile"));
        Assert.Equal(StatusCodes.Status200OK, file.StatusCode);
        Assert.Equal("text/plain", file.ContentType);
        Assert.Contains("sample.txt", file.ContentDisposition);
        Assert.Equal("file", file.BodyText);

        var minimal = await ExecuteMinimalAsync(Invoke<IResult>(assembly, "MinimalTypedSuccess"));
        Assert.Equal(StatusCodes.Status200OK, minimal.StatusCode);
        Assert.Contains("minimal", minimal.BodyText);
    }

    [Fact]
    public void Removed_and_forbidden_surface_does_not_compile()
    {
        AssertDoesNotCompile(
            "Invoke callback",
            """
            using System.Threading.Tasks;
            using Rivet;

            public sealed record Input(string Value);
            public sealed record Output(string Value);

            public static class Forbidden
            {
                private static readonly RouteDefinition<Input, Output> Operation =
                    Define.Post<Input, Output>("/items");

                public static object InvokeCallback() =>
                    Operation.Invoke(new Input("request"), input =>
                        Task.FromResult(new Output(input.Value)));
            }
            """
        );

        AssertDoesNotCompile(
            "unbound success",
            """
            using Rivet;

            public sealed record Input(string Value);
            public sealed record Output(string Value);

            public static class Forbidden
            {
                private static readonly RouteDefinition<Input, Output> Operation =
                    Define.Post<Input, Output>("/items");

                public static object UnboundSuccess() =>
                    Operation.Success(new Output("invalid"));
            }
            """
        );

        AssertDoesNotCompile(
            "callback bind",
            """
            using System.Threading.Tasks;
            using Rivet;

            public sealed record Input(string Value);
            public sealed record Output(string Value);

            public static class Forbidden
            {
                private static readonly RouteDefinition<Input, Output> Operation =
                    Define.Post<Input, Output>("/items");

                public static object CallbackBind() =>
                    Operation.Bind(
                        new Input("request"),
                        input => Task.FromResult(new Output(input.Value)));
            }
            """
        );

        AssertDoesNotCompile(
            "native result payload",
            """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            public sealed record Input(string Value);
            public sealed record Output(string Value);

            public static class Forbidden
            {
                private static readonly RouteDefinition<Input, Output> Operation =
                    Define.Post<Input, Output>("/items");

                public static object NativeResult() =>
                    Operation
                        .Bind(new Input("request"))
                        .Success(TypedResults.Ok(new Output("invalid")));
            }
            """
        );

        AssertDoesNotCompile(
            "parameterless bind",
            """
            using Rivet;
            public sealed record Input(string Value);
            public sealed record Output(string Value);
            public static class Forbidden
            {
                private static readonly RouteDefinition<Input, Output> Operation =
                    Define.Post<Input, Output>("/items");
                public static object Bind() => Operation.Bind();
            }
            """
        );

        AssertDoesNotCompile(
            "typed success without payload",
            """
            using Rivet;
            public sealed record Output(string Value);
            public static class Forbidden
            {
                private static readonly RouteDefinition<Output> Operation = Define.Get<Output>("/items");
                public static object Success() => Operation.Success();
            }
            """
        );

        AssertDoesNotCompile(
            "bodyless success with payload",
            """
            using Rivet;
            public static class Forbidden
            {
                private static readonly RouteDefinition Operation = Define.Get("/items");
                public static object Success() => Operation.Success("body");
            }
            """
        );

        AssertDoesNotCompile(
            "wrong bind input",
            """
            using Rivet;
            public sealed record Input(string Value);
            public sealed record Output(string Value);
            public static class Forbidden
            {
                private static readonly RouteDefinition<Input, Output> Operation =
                    Define.Post<Input, Output>("/items");
                public static object Bind() => Operation.Bind(new Output("wrong"));
            }
            """
        );

        AssertDoesNotCompile(
            "wrong success payload",
            """
            using Rivet;
            public sealed record Output(string Value);
            public static class Forbidden
            {
                private static readonly RouteDefinition<Output> Operation = Define.Get<Output>("/items");
                public static object Success() => Operation.Success("wrong");
            }
            """
        );

        AssertDoesNotCompile(
            "generic compatibility result",
            """
            using Rivet;
            public sealed record Output(string Value);
            public static class Forbidden
            {
                public static RivetResult<Output>? Result { get; }
            }
            """
        );
    }

    private static Assembly CompileAndLoad(string source)
    {
        var compilation = (
            (CSharpCompilation)CompilationHelper.CreateCompilation(source)
        ).WithAssemblyName($"ExecutionSurfaceContract_{Guid.NewGuid():N}");

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(
            result.Success,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => diagnostic.ToString())
            )
        );

        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    private static IReadOnlyList<Diagnostic> CompileWithoutThrowing(string source)
    {
        var compilation = (CSharpCompilation)
            CompilationHelper.CreateCompilation("public sealed class Bootstrap { }");
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest)
        );

        return compilation
            .RemoveAllSyntaxTrees()
            .AddSyntaxTrees(syntaxTree)
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
    }

    private static void AssertDoesNotCompile(string label, string source)
    {
        var diagnostics = CompileWithoutThrowing(source);
        Assert.True(
            diagnostics.Count > 0,
            $"Forbidden execution surface '{label}' compiled successfully."
        );
    }

    private static T Invoke<T>(Assembly assembly, string methodName)
    {
        var method = assembly
            .GetType("ExecutionSurface.Scenarios")!
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!;

        return (T)method.Invoke(null, null)!;
    }

    private static async Task<HttpObservation> ExecuteMvcAsync(IActionResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        var actionContext = new ActionContext(context, new RouteData(), new ActionDescriptor());

        await result.ExecuteResultAsync(actionContext);
        return await ObserveAsync(context.Response);
    }

    private static async Task<HttpObservation> ExecuteMinimalAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);
        return await ObserveAsync(context.Response);
    }

    private static async Task<HttpObservation> ObserveAsync(HttpResponse response)
    {
        var body = (MemoryStream)response.Body;
        var bytes = body.ToArray();
        await Task.CompletedTask;

        return new HttpObservation(
            response.StatusCode,
            bytes,
            response.ContentType,
            response.Headers.ContentDisposition.ToString()
        );
    }

    private sealed record HttpObservation(
        int StatusCode,
        byte[] Body,
        string? ContentType,
        string ContentDisposition
    )
    {
        public string BodyText => Encoding.UTF8.GetString(Body);
    }
}
