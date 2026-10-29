using System.Text.Json;
using Rivet.Tool;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

/// <summary>
/// K.3: [RivetEndpoint]/[RivetClient] operations declare security with [RivetSecure],
/// [RivetAndSecure], [RivetOrAnonymous] and [RivetAnonymous], read through the contract
/// builder's own model (SecurityDeclaration), so they mean what .Secure(), .AndSecure(),
/// .OrAnonymous() and .Anonymous() mean and contradict the same way (RIV1114). The nearest
/// level that declares any security says all of it; ASP.NET's [AllowAnonymous] declares a
/// level anonymous when no Rivet security attribute sits beside it.
/// </summary>
public sealed class ControllerSecurityTests
{
    private static string Controller(
        string controllerAttributes,
        string actionAttributes,
        string baseAttributes = ""
    ) =>
        $$"""
            using Microsoft.AspNetCore.Authorization;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            {{baseAttributes}}
            public abstract class ApiController : ControllerBase;

            {{controllerAttributes}}
            [Route("api/tasks")]
            public sealed class TasksController : ApiController
            {
                {{actionAttributes}}
                [RivetEndpoint]
                [HttpGet("")]
                public TaskDto Get() => new("1");
            }
            """;

    /// <summary>The operation's security in OpenAPI's shape: alternatives of scheme:scopes.</summary>
    private static string? Security(string source)
    {
        var (endpoints, _) = CompilationHelper.WalkMerged(source);
        return Assert.Single(endpoints).Security is { } security
            ? string.Join(
                " | ",
                security.Alternatives.Select(alternative =>
                    "{"
                    + string.Join(
                        " & ",
                        alternative.Schemes.Select(scheme =>
                            $"{scheme.Name}[{string.Join(",", scheme.Scopes)}]"
                        )
                    )
                    + "}"
                )
            )
            : null;
    }

    [Theory]
    [InlineData("", "", "", null)]
    [InlineData("[RivetSecure(\"bearer\")]", "", "", "{bearer[]}")]
    [InlineData("", "[RivetSecure(\"oauth\", \"tasks:read\")]", "", "{oauth[tasks:read]}")]
    [InlineData(
        "",
        "[RivetSecure(\"oauth\", \"tasks:read\")] [RivetAndSecure(\"key\")] [RivetSecure(\"admin\")]",
        "",
        "{oauth[tasks:read] & key[]} | {admin[]}"
    )]
    [InlineData("", "[RivetSecure(\"bearer\")] [RivetOrAnonymous]", "", "{bearer[]} | {}")]
    [InlineData("", "[RivetAnonymous]", "", "")]
    // The action wins, wholesale.
    [InlineData("[RivetSecure(\"bearer\")]", "[RivetSecure(\"key\")]", "", "{key[]}")]
    [InlineData("[RivetSecure(\"bearer\")]", "[RivetAnonymous]", "", "")]
    [InlineData("[RivetAnonymous]", "[RivetSecure(\"bearer\")]", "", "{bearer[]}")]
    // A base controller's security reaches its actions unless the controller declares its own.
    [InlineData("", "", "[RivetSecure(\"bearer\")]", "{bearer[]}")]
    [InlineData("[RivetSecure(\"key\")]", "", "[RivetSecure(\"bearer\")]", "{key[]}")]
    // ASP.NET's [AllowAnonymous] alone reads as [RivetAnonymous], at any level.
    [InlineData("[RivetSecure(\"bearer\")]", "[AllowAnonymous]", "", "")]
    [InlineData("[AllowAnonymous]", "", "[RivetSecure(\"bearer\")]", "")]
    // Beside Rivet's attributes it's ASP.NET's business: here, optional credentials.
    [InlineData(
        "",
        "[AllowAnonymous] [RivetSecure(\"bearer\")] [RivetOrAnonymous]",
        "",
        "{bearer[]} | {}"
    )]
    // [Authorize] names policies, not schemes, so the spec doesn't read it.
    [InlineData("[Authorize(\"Admins\")]", "", "", null)]
    public void Attributes_declare_the_operation_security(
        string controllerAttributes,
        string actionAttributes,
        string baseAttributes,
        string? expected
    )
    {
        Assert.Equal(
            expected,
            Security(Controller(controllerAttributes, actionAttributes, baseAttributes))
        );
    }

    [Theory]
    [InlineData("[RivetAnonymous] [RivetSecure(\"bearer\")]", "[RivetAnonymous] declares")]
    [InlineData("[RivetAnonymous] [RivetOrAnonymous]", "[RivetAnonymous] declares")]
    [InlineData("[RivetAndSecure(\"key\")]", "[RivetAndSecure] must follow [RivetSecure]")]
    [InlineData(
        "[RivetAndSecure(\"key\")] [RivetSecure(\"bearer\")]",
        "[RivetAndSecure] must follow [RivetSecure]"
    )]
    [InlineData(
        "[RivetSecure(\"bearer\")] [RivetOrAnonymous] [RivetAndSecure(\"key\")]",
        "[RivetAndSecure] must follow [RivetSecure]"
    )]
    public void Contradictory_attributes_are_refused(string actionAttributes, string reason)
    {
        var error = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkMerged(Controller("", actionAttributes))
        );

        Assert.StartsWith("error RIV1114:", error.Message);
        Assert.Contains("endpoint 'TasksController.Get'", error.Message);
        Assert.Contains(reason, error.Message);
        Assert.Contains("(on 'Get')", error.Message);
    }

    [Fact]
    public void A_contradiction_on_the_controller_names_the_class()
    {
        var error = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkMerged(Controller("[RivetAndSecure(\"key\")]", ""))
        );

        Assert.StartsWith("error RIV1114:", error.Message);
        Assert.Contains("(on class 'TasksController')", error.Message);
    }

    [Fact]
    public void An_AND_split_across_partial_declarations_in_two_files_is_refused()
    {
        var compilation = CompilationHelper.CreateCompilationFromMultiple(
            [
                """
                using Microsoft.AspNetCore.Mvc;
                using Rivet;

                namespace Test;

                [RivetType]
                public sealed record TaskDto(string Id);

                [RivetSecure("bearer")]
                [Route("api/tasks")]
                public sealed partial class TasksController : ControllerBase
                {
                    [RivetEndpoint]
                    [HttpGet("")]
                    public TaskDto Get() => new("1");
                }
                """,
                """
                using Rivet;

                namespace Test;

                [RivetAndSecure("key")]
                public sealed partial class TasksController;
                """,
            ],
            ["TasksController.cs", "TasksController.Security.cs"]
        );
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var error = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkEndpoints(compilation, discovered, walker)
        );

        Assert.StartsWith("error RIV1114:", error.Message);
        Assert.Contains("split across partial declarations", error.Message);
    }

    [Fact]
    public void A_RivetClient_class_and_a_RivetContract_method_declare_security_too()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetClient]
            [RivetSecure("bearer")]
            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet("")]
                public TaskDto List() => new("1");

                [RivetAnonymous]
                [HttpGet("public")]
                public TaskDto Public() => new("1");
            }

            [RivetContract]
            [Route("api/notes")]
            public abstract class NotesContract
            {
                [RivetSecure("oauth", "notes:read")]
                [HttpGet("")]
                public abstract TaskDto List();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        var list = endpoints.Single(endpoint => endpoint.RouteTemplate == "/api/tasks");
        Assert.Equal(
            "bearer",
            Assert.Single(Assert.Single(list.Security!.Alternatives).Schemes).Name
        );
        var open = endpoints.Single(endpoint => endpoint.RouteTemplate == "/api/tasks/public");
        Assert.Empty(open.Security!.Alternatives);
        var notes = endpoints.Single(endpoint => endpoint.RouteTemplate == "/api/notes");
        var scheme = Assert.Single(Assert.Single(notes.Security!.Alternatives).Schemes);
        Assert.Equal("oauth", scheme.Name);
        Assert.Equal(["notes:read"], scheme.Scopes);
    }

    [Fact]
    public void The_operation_security_reaches_the_spec()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            [assembly: RivetSecurityScheme("bearer", "http", scheme: "bearer")]
            [assembly: RivetSecurityScheme("key", "apiKey", parameterName: "X-Api-Key", location: "header")]
            [assembly: RivetGlobalSecurity(0)]
            [assembly: RivetGlobalSecurityScheme(0, "key", new string[0])]

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetSecure("bearer")]
            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("")]
                public TaskDto List() => new("1");

                [RivetEndpoint]
                [RivetAnonymous]
                [HttpGet("public")]
                public TaskDto Public() => new("1");
            }
            """;
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        using var document = JsonDocument.Parse(
            OpenApiEmitter.Emit(
                endpoints,
                walker.Definitions,
                walker.Brands,
                walker.Enums,
                SecurityMetadataWalker.Walk(compilation)
            )
        );

        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal(
            """[{"bearer":[]}]""",
            JsonSerializer.Serialize(
                paths.GetProperty("/api/tasks").GetProperty("get").GetProperty("security")
            )
        );
        Assert.Equal(
            "[]",
            JsonSerializer.Serialize(
                paths.GetProperty("/api/tasks/public").GetProperty("get").GetProperty("security")
            )
        );
        Assert.Equal(
            """[{"key":[]}]""",
            JsonSerializer.Serialize(document.RootElement.GetProperty("security"))
        );
    }

    [Fact]
    public void An_action_that_names_an_undeclared_scheme_is_refused_like_a_contract()
    {
        var compilation = CompilationHelper.CreateCompilation(
            Controller("", "[RivetSecure(\"nobody\")]")
        );
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        var error = Assert.Throws<RivetUserException>(() =>
            OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null)
        );
        Assert.Contains("nobody", error.Message);
    }

    /// <summary>
    /// A contract and an annotated action at one transport identity collapse only when they
    /// declare the same security; otherwise either one would publish the other's.
    /// </summary>
    [Theory]
    [InlineData("[RivetSecure(\"bearer\")]", true)]
    [InlineData("[RivetSecure(\"key\")]", false)]
    [InlineData("", false)]
    public void Merging_a_contract_and_an_action_compares_their_security(
        string actionAttributes,
        bool merges
    )
    {
        var source = $$"""
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> List =
                    Define.Get<TaskDto>("/api/tasks").Secure("bearer");
            }

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                {{actionAttributes}}
                [RivetEndpoint]
                [HttpGet("")]
                public TaskDto List() => new("1");
            }
            """;

        if (merges)
        {
            Assert.Single(CompilationHelper.WalkMerged(source).Endpoints);
            return;
        }

        var error = Assert.Throws<RivetUserException>(() => CompilationHelper.WalkMerged(source));
        Assert.StartsWith($"error {Diagnostics.ConflictingOperations}:", error.Message);
    }
}
