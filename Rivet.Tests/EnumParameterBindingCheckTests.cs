using Rivet.Tool;
using Rivet.Tool.Analysis;

namespace Rivet.Tests;

/// <summary>
/// Invariant: every value the spec advertises for a parameter binds on the host that
/// implements it. ASP.NET binds route, query, header and form enums with its own parser,
/// not System.Text.Json, so a string enum converter's wire names reach a parser that
/// ignores the converter. Minimal APIs call Enum.TryParse (exact member name); MVC's
/// EnumTypeModelBinder matches member names ignoring case. --check reports a handler
/// whose enum would refuse an advertised value (RIV4005); generation refuses a
/// [RivetEndpoint] MVC action that would (RIV1110).
/// </summary>
public sealed class EnumParameterBindingCheckTests
{
    private const string Contract = """
        using Rivet;

        namespace Test;

        [RivetType]
        public sealed record StreetDto(string Id);

        [RivetContract]
        public static class StreetsContract
        {
            public static readonly RouteDefinition<StreetDto> List =
                Define.Get<StreetDto>("/api/streets");

            public static readonly RouteDefinition<StreetDto> Get =
                Define.Get<StreetDto>("/api/streets/{kind}");
        }
        """;

    private const string Camel = "[JsonConverter(typeof(RivetCamelCaseEnumConverter<StreetKind>))]";
    private const string Snake = "[JsonConverter(typeof(RivetSnakeCaseEnumConverter<StreetKind>))]";
    private const string Builtin = "[JsonConverter(typeof(JsonStringEnumConverter<StreetKind>))]";

    private static string StreetKind(string converter) =>
        $$"""
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            {{converter}}
            public enum StreetKind
            {
                Unknown,
                RestrictedByway,
                Route2,
                RestrictedByway2A,
            }
            """;

    private const string RenamedColour = """
        using System.Text.Json.Serialization;

        namespace Test;

        [JsonConverter(typeof(JsonStringEnumConverter<StreetKind>))]
        public enum StreetKind
        {
            Unknown,
            [JsonStringEnumMemberName("dark-green")]
            DarkGreen,
        }
        """;

    private static string Field(string route) => route.Contains('{') ? "Get" : "List";

    private static string MinimalHandler(string parameters, string route = "/api/streets") =>
        $$"""
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class StreetEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet("{{route}}", ({{parameters}}) =>
                        StreetsContract.{{Field(route)}}.Success(new StreetDto("1")).ToResult());
                }
            }
            """;

    private static string MvcHandler(string parameters, string route = "") =>
        $$"""
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [ApiController]
            [Route("api/streets")]
            public sealed class StreetsController : ControllerBase
            {
                [HttpGet("{{route}}")]
                public IActionResult Handle({{parameters}}) =>
                    StreetsContract.{{Field(route)}}.Success(new StreetDto("1")).ToActionResult();
            }
            """;

    private static IReadOnlyList<CoverageWarning> RunCheck(params string[] sources) =>
        CompilationHelper.CheckCoverage(CompilationHelper.CreateCompilationFromMultiple(sources));

    private static CoverageWarning AssertReported(IReadOnlyList<CoverageWarning> warnings) =>
        Assert.Single(warnings, w => w.Kind == CoverageWarningKind.UnbindableEnumParameter);

    /// <summary>
    /// The handler is recognised as the field's implementation (no missing or mismatch
    /// warning either), so the absence of RIV4005 is not vacuous.
    /// </summary>
    private static void AssertNotReported(
        IReadOnlyList<CoverageWarning> warnings,
        string fieldName = "List"
    ) => Assert.DoesNotContain(warnings, w => w.FieldName == fieldName);

    // ---------------------------------------------------------------
    // RIV4005 — --check
    // ---------------------------------------------------------------

    [Fact]
    public void Minimal_Api_Camel_Case_Enum_Query_Is_Reported()
    {
        var warnings = RunCheck(Contract, StreetKind(Camel), MinimalHandler("StreetKind kind"));

        var warning = AssertReported(warnings);
        Assert.Equal("StreetsContract", warning.ContractName);
        Assert.Equal("List", warning.FieldName);
        Assert.Equal("unknown", warning.Expected);
        Assert.Equal("Unknown", warning.Actual);
        Assert.Contains("StreetsContract.List", warning.Message);
        Assert.Contains("GET /api/streets", warning.Message);
        Assert.Contains("minimal API", warning.Message);
        Assert.Contains("'kind'", warning.Message);
        Assert.Contains("'StreetKind'", warning.Message);
        Assert.Contains("'unknown'", warning.Message);
        Assert.Contains("JsonStringEnumConverter<StreetKind>", warning.Message);
        Assert.Contains("string", warning.Message);
    }

    [Fact]
    public void Mvc_Camel_Case_Enum_Query_Is_Not_Reported()
    {
        // MVC's EnumTypeModelBinder ignores case, so a casing-only policy binds.
        var warnings = RunCheck(Contract, StreetKind(Camel), MvcHandler("StreetKind kind"));

        AssertNotReported(warnings);
    }

    [Fact]
    public void Mvc_Snake_Case_Enum_Query_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            MvcHandler("[FromQuery] StreetKind kind")
        );

        var warning = AssertReported(warnings);
        Assert.Equal("restricted_byway", warning.Expected);
        Assert.Equal("RestrictedByway", warning.Actual);
        Assert.Contains("MVC", warning.Message);
        Assert.Contains("'restricted_byway'", warning.Message);
    }

    [Fact]
    public void Minimal_Api_Builtin_Converter_Without_Renames_Is_Not_Reported()
    {
        var warnings = RunCheck(Contract, StreetKind(Builtin), MinimalHandler("StreetKind kind"));

        AssertNotReported(warnings);
    }

    [Fact]
    public void Mvc_Rename_That_Changes_Letters_Is_Reported()
    {
        var warnings = RunCheck(Contract, RenamedColour, MvcHandler("StreetKind kind"));

        var warning = AssertReported(warnings);
        Assert.Equal("dark-green", warning.Expected);
        Assert.Equal("DarkGreen", warning.Actual);
    }

    [Fact]
    public void Plain_Enum_Is_Not_Reported()
    {
        var warnings = RunCheck(Contract, StreetKind(""), MinimalHandler("StreetKind kind"));

        AssertNotReported(warnings);
    }

    [Fact]
    public void Handler_Binding_A_String_Is_Not_Reported()
    {
        var warnings = RunCheck(Contract, StreetKind(Camel), MinimalHandler("string kind"));

        AssertNotReported(warnings);
    }

    [Fact]
    public void Mvc_FromBody_Enum_Is_Not_Reported()
    {
        // A JSON body goes through System.Text.Json, which honours the converter.
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            MvcHandler("[FromBody] StreetKind kind")
        );

        AssertNotReported(warnings);
    }

    [Fact]
    public void Mvc_Custom_Model_Binder_Is_Not_Reported()
    {
        // An explicit [ModelBinder] replaces MVC's enum binder; parsing is the binder's job.
        const string binder = """
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc.ModelBinding;

            namespace Test;

            public sealed class StreetKindBinder : IModelBinder
            {
                public Task BindModelAsync(ModelBindingContext bindingContext) => Task.CompletedTask;
            }
            """;

        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            binder,
            MvcHandler("[ModelBinder(typeof(StreetKindBinder))] StreetKind kind")
        );

        AssertNotReported(warnings);
    }

    [Fact]
    public void Minimal_Api_FromBody_Enum_Is_Not_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            MinimalHandler("[FromBody] StreetKind kind")
        );

        AssertNotReported(warnings);
    }

    [Fact]
    public void Minimal_Api_Route_Enum_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            MinimalHandler("StreetKind kind", "/api/streets/{kind}")
        );

        var warning = AssertReported(warnings);
        Assert.Equal("Get", warning.FieldName);
        Assert.Contains("GET /api/streets/{kind}", warning.Message);
    }

    [Fact]
    public void Mvc_Route_Enum_With_Snake_Case_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            MvcHandler("StreetKind kind", "{kind}")
        );

        Assert.Equal("Get", AssertReported(warnings).FieldName);
    }

    [Fact]
    public void Nullable_Enum_Is_Reported()
    {
        var warnings = RunCheck(Contract, StreetKind(Camel), MinimalHandler("StreetKind? kind"));

        AssertReported(warnings);
    }

    [Fact]
    public void Enum_Array_Is_Reported()
    {
        var warnings = RunCheck(Contract, StreetKind(Camel), MinimalHandler("StreetKind[] kinds"));

        Assert.Contains("'kinds'", AssertReported(warnings).Message);
    }

    [Fact]
    public void Mvc_Enum_List_Header_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            MvcHandler("[FromHeader] List<StreetKind> kinds")
        );

        AssertReported(warnings);
    }

    // ---------------------------------------------------------------
    // Complex parameters bound member by member: [AsParameters] on a
    // minimal API, an explicitly sourced complex type on MVC.
    // ---------------------------------------------------------------

    private static string StreetRequest(string kindDeclaration = "StreetKind Kind") =>
        $$"""
            using Microsoft.AspNetCore.Mvc;

            namespace Test;

            public sealed record StreetRequest({{kindDeclaration}}, string? Name);
            """;

    [Fact]
    public void Minimal_Api_AsParameters_Camel_Case_Enum_Member_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            StreetRequest(),
            MinimalHandler("[AsParameters] StreetRequest request")
        );

        var warning = AssertReported(warnings);
        Assert.Equal("unknown", warning.Expected);
        Assert.Contains("minimal API parameter 'request.Kind'", warning.Message);
    }

    [Fact]
    public void Minimal_Api_AsParameters_Settable_Property_Is_Reported()
    {
        const string request = """
            namespace Test;

            public sealed class StreetRequest
            {
                public StreetKind Kind { get; set; }
            }
            """;

        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            request,
            MinimalHandler("[AsParameters] StreetRequest request")
        );

        Assert.Contains("'request.Kind'", AssertReported(warnings).Message);
    }

    [Fact]
    public void Minimal_Api_AsParameters_FromBody_Member_Is_Not_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            StreetRequest("[FromBody] StreetKind Kind"),
            MinimalHandler("[AsParameters] StreetRequest request")
        );

        AssertNotReported(warnings);
    }

    [Fact]
    public void Mvc_FromQuery_Record_With_Camel_Case_Enum_Is_Not_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            StreetRequest(),
            MvcHandler("[FromQuery] StreetRequest request")
        );

        AssertNotReported(warnings);
    }

    [Fact]
    public void Mvc_FromQuery_Record_With_Snake_Case_Enum_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            StreetRequest(),
            MvcHandler("[FromQuery] StreetRequest request")
        );

        var warning = AssertReported(warnings);
        Assert.Equal("restricted_byway", warning.Expected);
        Assert.Contains("MVC parameter 'request.Kind'", warning.Message);
    }

    [Fact]
    public void Mvc_Unattributed_Complex_Parameter_Is_Not_Reported()
    {
        // [ApiController] infers a JSON body for an unattributed complex type.
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            StreetRequest(),
            MvcHandler("StreetRequest request")
        );

        AssertNotReported(warnings);
    }

    [Fact]
    public void Functions_Handler_Is_Not_Checked()
    {
        // Functions binds its own parameters and its enum behaviour is unmeasured.
        const string azure = """
            using System;

            namespace Microsoft.Azure.Functions.Worker;

            public enum AuthorizationLevel { Anonymous }

            [AttributeUsage(AttributeTargets.Method)]
            public sealed class FunctionAttribute(string name) : Attribute
            {
                public string Name { get; } = name;
            }

            [AttributeUsage(AttributeTargets.Parameter)]
            public sealed class HttpTriggerAttribute(
                AuthorizationLevel authorizationLevel,
                params string[] methods
            ) : Attribute
            {
                public string? Route { get; set; }
            }
            """;
        const string function = """
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Azure.Functions.Worker;
            using Rivet;

            namespace Test;

            public sealed class StreetFunctions
            {
                [Function("list-streets")]
                public IActionResult Run(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "streets")] object request,
                    StreetKind kind
                ) => StreetsContract.List.Success(new StreetDto("1")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, StreetKind(Camel), azure, function);

        AssertNotReported(warnings);
    }

    [Fact]
    public async Task Check_Prints_RIV4005_As_An_Error_And_Exits_Nonzero()
    {
        using var work = new TempDir();
        var sourcePath = Path.Combine(work.FullName, "Api.cs");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            using System.Text.Json.Serialization;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [JsonConverter(typeof(RivetSnakeCaseEnumConverter<StreetKind>))]
            public enum StreetKind { Unknown, RestrictedByway }

            [RivetType]
            public sealed record StreetDto(string Id);

            [RivetContract]
            public static class StreetsContract
            {
                public static readonly RouteDefinition<StreetDto> List =
                    Define.Get<StreetDto>("/api/streets");
            }

            [ApiController]
            [Route("api/streets")]
            public sealed class StreetsController : ControllerBase
            {
                [HttpGet]
                public IActionResult List([FromQuery] StreetKind kind)
                    => StreetsContract.List.Success(new StreetDto("1")).ToActionResult();
            }
            """
        );

        var result = CliRunner.RunCli(work.FullName, [sourcePath, "--check"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error RIV4005:", result.StdErr);
        Assert.Contains("'restricted_byway'", result.StdErr);
    }

    // ---------------------------------------------------------------
    // RIV1110 — generation, [RivetEndpoint] MVC actions
    // ---------------------------------------------------------------

    private static string AnnotatedAction(
        string converter,
        string parameter = "[FromQuery] StreetKind kind"
    ) =>
        $$"""
            using System;
            using System.Text.Json.Serialization;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            {{converter}}
            public enum StreetKind
            {
                Unknown,
                RestrictedByway,
                Route2,
                RestrictedByway2A,
            }

            [RivetType]
            public sealed record StreetDto(string Id);

            public sealed record StreetRequest(StreetKind Kind, string? Name);

            [Route("api/streets")]
            public sealed class StreetsController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet]
                [ProducesResponseType(typeof(StreetDto), 200)]
                public IActionResult List({{parameter}})
                    => throw new NotImplementedException();
            }
            """;

    private static void WalkEndpoints(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        CompilationHelper.WalkEndpoints(compilation, discovered, walker);
    }

    [Fact]
    public void Annotated_Action_With_Snake_Case_Enum_Query_Fails_Generation()
    {
        var exception = Assert.Throws<RivetUserException>(() =>
            WalkEndpoints(AnnotatedAction(Snake))
        );

        Assert.StartsWith($"error {Diagnostics.UnbindableEnumParameter}:", exception.Message);
        Assert.Contains("StreetsController.List", exception.Message);
        Assert.Contains("'kind'", exception.Message);
        Assert.Contains("'StreetKind'", exception.Message);
        Assert.Contains("'restricted_byway'", exception.Message);
        Assert.Contains("JsonStringEnumConverter<StreetKind>", exception.Message);
    }

    [Fact]
    public void Annotated_Action_With_Camel_Case_Enum_Query_Generates()
    {
        WalkEndpoints(AnnotatedAction(Camel));
    }

    [Fact]
    public void Annotated_Action_With_Snake_Case_Enum_In_FromQuery_Record_Fails_Generation()
    {
        var exception = Assert.Throws<RivetUserException>(() =>
            WalkEndpoints(AnnotatedAction(Snake, "[FromQuery] StreetRequest request"))
        );

        Assert.StartsWith($"error {Diagnostics.UnbindableEnumParameter}:", exception.Message);
        Assert.Contains("MVC parameter 'request.Kind'", exception.Message);
        Assert.Contains("'restricted_byway'", exception.Message);
    }

    [Fact]
    public void Annotated_Action_With_Camel_Case_Enum_In_FromQuery_Record_Generates()
    {
        WalkEndpoints(AnnotatedAction(Camel, "[FromQuery] StreetRequest request"));
    }
}
