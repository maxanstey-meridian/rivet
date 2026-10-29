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
                Define.Get<StreetDto>("/api/streets/{kind}").Parameter<string>("kind", "path", true);

            public static readonly RouteDefinition<StreetDto> Create =
                Define.Post<StreetDto>("/api/streets");
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

    private const string CaseOnlyRenamedColour = """
        using System.Text.Json.Serialization;

        namespace Test;

        [JsonConverter(typeof(JsonStringEnumConverter<StreetKind>))]
        public enum StreetKind
        {
            Unknown,
            [JsonStringEnumMemberName("darkgreen")]
            DarkGreen,
        }
        """;

    private const string StreetKindBinder = """
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Mvc.ModelBinding;

        namespace Test;

        public sealed class StreetKindBinder : IModelBinder
        {
            public Task BindModelAsync(ModelBindingContext bindingContext) => Task.CompletedTask;
        }
        """;

    private static string Field(string route, string verb = "Get") =>
        verb == "Post" ? "Create"
        : route.Contains('{') ? "Get"
        : "List";

    private static string MinimalHandler(
        string parameters,
        string route = "/api/streets",
        string verb = "Get"
    ) =>
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
                    app.Map{{verb}}("{{route}}", ({{parameters}}) =>
                        StreetsContract.{{Field(
                route,
                verb
            )}}.Success(new StreetDto("1")).ToResult());
                }
            }
            """;

    private static string MvcHandler(
        string parameters,
        string route = "",
        bool apiController = true
    ) =>
        $$"""
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            {{(apiController ? "[ApiController]" : "")}}
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
    /// warning either), so the absence of RIV4005 is not vacuous. The fixtures bind enums
    /// their contract doesn't declare, which RIV4006 reports; that's not this file's subject.
    /// </summary>
    private static void AssertNotReported(
        IReadOnlyList<CoverageWarning> warnings,
        string fieldName = "List"
    ) =>
        Assert.DoesNotContain(
            warnings,
            w => w.FieldName == fieldName && w.Kind != CoverageWarningKind.InputBindingMismatch
        );

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
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            StreetKindBinder,
            MvcHandler("[ModelBinder(typeof(StreetKindBinder))] StreetKind kind")
        );

        AssertNotReported(warnings);
    }

    [Fact]
    public void Minimal_Api_Custom_Model_Binder_Is_Still_Reported()
    {
        // Minimal APIs ignore model binders and still call Enum.TryParse.
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            StreetKindBinder,
            MinimalHandler("[ModelBinder(typeof(StreetKindBinder))] StreetKind kind")
        );

        Assert.Contains("minimal API parameter 'kind'", AssertReported(warnings).Message);
    }

    [Fact]
    public void Minimal_Api_Model_Binder_On_The_Enum_Is_Still_Reported()
    {
        const string boundKind = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [ModelBinder(typeof(StreetKindBinder))]
            [System.Text.Json.Serialization.JsonConverter(typeof(RivetCamelCaseEnumConverter<StreetKind>))]
            public enum StreetKind
            {
                Unknown,
                RestrictedByway,
            }
            """;

        var warnings = RunCheck(
            Contract,
            boundKind,
            StreetKindBinder,
            MinimalHandler("StreetKind kind")
        );

        Assert.Contains("minimal API parameter 'kind'", AssertReported(warnings).Message);
    }

    [Fact]
    public void Minimal_Api_Header_Enum_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            MinimalHandler("[FromHeader] StreetKind kind")
        );

        Assert.Equal("unknown", AssertReported(warnings).Expected);
    }

    [Fact]
    public void Minimal_Api_Form_Enum_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            MinimalHandler("[FromForm] StreetKind kind", verb: "Post")
        );

        var warning = AssertReported(warnings);
        Assert.Equal("Create", warning.FieldName);
        Assert.Equal("unknown", warning.Expected);
    }

    [Fact]
    public void Mvc_Rename_That_Only_Changes_Case_Is_Not_Reported()
    {
        var warnings = RunCheck(Contract, CaseOnlyRenamedColour, MvcHandler("StreetKind kind"));

        AssertNotReported(warnings);
    }

    [Fact]
    public void Minimal_Api_Rename_That_Only_Changes_Case_Is_Reported()
    {
        var warnings = RunCheck(Contract, CaseOnlyRenamedColour, MinimalHandler("StreetKind kind"));

        var warning = AssertReported(warnings);
        Assert.Equal("darkgreen", warning.Expected);
        Assert.Equal("DarkGreen", warning.Actual);
    }

    [Fact]
    public void Enum_With_An_Unsupported_Converter_Does_Not_Abort_The_Check()
    {
        // Generation never walked this enum, so its converter is not --check's to refuse.
        const string custom = """
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;

            namespace Test;

            [JsonConverter(typeof(StreetKindConverter))]
            public enum StreetKind { Unknown, RestrictedByway }

            public sealed class StreetKindConverter : JsonConverter<StreetKind>
            {
                public override StreetKind Read(
                    ref Utf8JsonReader reader,
                    Type typeToConvert,
                    JsonSerializerOptions options
                ) => StreetKind.Unknown;

                public override void Write(
                    Utf8JsonWriter writer,
                    StreetKind value,
                    JsonSerializerOptions options
                ) => writer.WriteStringValue(value.ToString());
            }
            """;

        var warnings = RunCheck(Contract, custom, MinimalHandler("StreetKind kind"));

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
    public void Minimal_Api_Post_Unattributed_Enum_Array_Is_Not_Reported()
    {
        // A verb that carries a body leaves inferred bodies on: the array is a JSON body.
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            MinimalHandler("StreetKind[] kinds", verb: "Post")
        );

        AssertNotReported(warnings, "Create");
    }

    [Fact]
    public void Minimal_Api_Post_FromQuery_Enum_Array_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            MinimalHandler("[FromQuery] StreetKind[] kinds", verb: "Post")
        );

        Assert.Equal("Create", AssertReported(warnings).FieldName);
    }

    [Fact]
    public void Minimal_Api_Get_Unattributed_Enum_List_Is_Refused_When_Endpoints_Are_Built_So_Not_Reported()
    {
        // Only a T[] is inferred from the query. A List<T> is inferred as a body, which a GET
        // doesn't allow, so ASP.NET refuses the endpoint when the endpoints are built (on the
        // first request): nothing binds to check.
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            MinimalHandler("List<StreetKind> kinds")
        );

        AssertNotReported(warnings);
    }

    [Fact]
    public void Minimal_Api_Get_And_Post_Unattributed_Enum_Array_Is_Reported()
    {
        // Any body-less verb turns off inferred bodies for the whole endpoint, so the array
        // comes from the query even though POST is mapped too.
        const string handler = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class StreetEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapMethods("/api/streets", new[] { "GET", "POST" }, (StreetKind[] kinds) =>
                        StreetsContract.List.Success(new StreetDto("1")).ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, StreetKind(Camel), handler);

        var warning = AssertReported(warnings);
        Assert.Equal("List", warning.FieldName);
        Assert.Contains("minimal API parameter 'kinds'", warning.Message);
    }

    [Fact]
    public void Method_Group_Mapped_With_Post_Then_Get_Is_Read_On_Each_Mapping()
    {
        // Each Map* call infers the body for itself: the POST mapping reads the array from a
        // JSON body, the GET mappings from the query. The first mapping doesn't speak for the
        // others, and a warning two mappings share is reported once.
        const string handler = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class StreetEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapPost("/api/streets", Handle);
                    app.MapGet("/api/streets", Handle);
                    app.MapGet("/api/roads", Handle);
                }

                private static IResult Handle(StreetKind[] kinds) =>
                    StreetsContract.List.Success(new StreetDto("1")).ToResult();
            }
            """;

        var warnings = RunCheck(Contract, StreetKind(Camel), handler);

        var warning = AssertReported(warnings);
        Assert.Equal("List", warning.FieldName);
        Assert.Contains("minimal API parameter 'kinds'", warning.Message);
        var bindings = warnings
            .Where(w => w.FieldName == "List" && w.Kind == CoverageWarningKind.InputBindingMismatch)
            .Select(w => w.Actual)
            .ToList();
        Assert.Equal(["request body", "query parameter 'kinds'"], bindings);
    }

    [Fact]
    public void Minimal_Api_MapMethods_Lowercase_Get_Unattributed_Enum_Array_Is_Not_Reported()
    {
        // ASP.NET matches the body-less verbs ordinally on the verbs as written, so "get"
        // isn't GET there: inferred bodies stay on and the array is a JSON body. Routing
        // still serves GET requests, so the method matches the contract.
        const string handler = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Routing;
            using Rivet;

            namespace Test;

            public static class StreetEndpoints
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapMethods("/api/streets", new[] { "get" }, (StreetKind[] kinds) =>
                        StreetsContract.List.Success(new StreetDto("1")).ToResult());
                }
            }
            """;

        var warnings = RunCheck(Contract, StreetKind(Camel), handler);

        AssertNotReported(warnings);
    }

    [Fact]
    public void Mvc_Unattributed_Enum_Array_Is_Not_Reported()
    {
        // [ApiController] treats a collection as complex and infers [FromBody].
        var warnings = RunCheck(Contract, StreetKind(Snake), MvcHandler("StreetKind[] kinds"));

        AssertNotReported(warnings);
    }

    [Fact]
    public void Mvc_Without_ApiController_Unattributed_Enum_Array_Is_Reported()
    {
        // Without [ApiController] MVC infers no body: the array binds from the query.
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            MvcHandler("StreetKind[] kinds", apiController: false)
        );

        var warning = AssertReported(warnings);
        Assert.Equal("restricted_byway", warning.Expected);
        Assert.Contains("MVC parameter 'kinds'", warning.Message);
    }

    [Fact]
    public void Mvc_ApiController_On_A_Base_Class_Infers_The_Body()
    {
        // [ApiController] is inherited, so a derived controller infers [FromBody] too.
        const string controller = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [ApiController]
            public abstract class ApiControllerBase : ControllerBase;

            [Route("api/streets")]
            public sealed class StreetsController : ApiControllerBase
            {
                [HttpGet]
                public IActionResult Handle(StreetKind[] kinds) =>
                    StreetsContract.List.Success(new StreetDto("1")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, StreetKind(Snake), controller);

        AssertNotReported(warnings);
    }

    [Fact]
    public void Mvc_ApiController_On_The_Assembly_Infers_The_Body()
    {
        const string controller = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            [assembly: ApiController]

            namespace Test;

            [Route("api/streets")]
            public sealed class StreetsController : ControllerBase
            {
                [HttpGet]
                public IActionResult Handle(StreetKind[] kinds) =>
                    StreetsContract.List.Success(new StreetDto("1")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, StreetKind(Snake), controller);

        AssertNotReported(warnings);
    }

    [Fact]
    public void Static_Method_Is_Not_An_Mvc_Implementation()
    {
        // MVC never routes a static method, whatever attributes it carries.
        const string controller = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [ApiController]
            [Route("api/streets")]
            public sealed class StreetsController : ControllerBase
            {
                [HttpGet]
                public static IActionResult Handle([FromQuery] StreetKind kind) =>
                    StreetsContract.List.Success(new StreetDto("1")).ToActionResult();
            }
            """;

        var warnings = RunCheck(Contract, StreetKind(Snake), controller);

        Assert.Contains(
            warnings,
            w => w.Kind == CoverageWarningKind.MissingImplementation && w.FieldName == "List"
        );
        Assert.DoesNotContain(warnings, w => w.Kind == CoverageWarningKind.UnbindableEnumParameter);
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
    public void Minimal_Api_Get_AsParameters_Enum_Array_Member_Is_Reported()
    {
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            StreetRequest("StreetKind[] Kinds"),
            MinimalHandler("[AsParameters] StreetRequest request")
        );

        Assert.Contains("'request.Kinds'", AssertReported(warnings).Message);
    }

    [Fact]
    public void Minimal_Api_Post_AsParameters_Enum_Array_Member_Is_Not_Reported()
    {
        // [AsParameters] members are inferred like parameters: on POST the array is a body.
        var warnings = RunCheck(
            Contract,
            StreetKind(Camel),
            StreetRequest("StreetKind[] Kinds"),
            MinimalHandler("[AsParameters] StreetRequest request", verb: "Post")
        );

        AssertNotReported(warnings, "Create");
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
    public void Mvc_FromQuery_Record_With_Snake_Case_Enum_Array_Member_Is_Reported()
    {
        // The explicit source reaches every member, arrays included: no body is inferred.
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            StreetRequest("StreetKind[] Kinds"),
            MvcHandler("[FromQuery] StreetRequest request")
        );

        var warning = AssertReported(warnings);
        Assert.Equal("restricted_byway", warning.Expected);
        Assert.Contains("MVC parameter 'request.Kinds'", warning.Message);
    }

    [Fact]
    public void Mvc_Without_ApiController_Unattributed_Complex_Parameter_Is_Reported()
    {
        // Without [ApiController] MVC binds the members from route, query and form.
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            StreetRequest(),
            MvcHandler("StreetRequest request", apiController: false)
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

    [Theory]
    [InlineData("[FromQuery] string? Name")]
    [InlineData("Microsoft.AspNetCore.Http.IFormFile? Name")]
    public void Mvc_ApiController_Complex_Parameter_With_A_Sourced_Member_Is_Reported(
        string otherMember
    )
    {
        // InferParameterBindingInfoConvention gives a complex type no binding source when one
        // of its members carries its own, so the type isn't a body: its other members bind
        // from route, query and form, the enum among them.
        var request = $$"""
            using Microsoft.AspNetCore.Mvc;

            namespace Test;

            public sealed record StreetRequest(StreetKind Kind, {{otherMember}});
            """;

        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            request,
            MvcHandler("StreetRequest request")
        );

        var warning = AssertReported(warnings);
        Assert.Equal("restricted_byway", warning.Expected);
        Assert.Contains("MVC parameter 'request.Kind'", warning.Message);
    }

    [Theory]
    [InlineData("options.SuppressInferBindingSourcesForParameters = true;", true)]
    [InlineData("options.SuppressInferBindingSourcesForParameters = false;", false)]
    [InlineData(
        "const bool suppress = true; options.SuppressInferBindingSourcesForParameters = suppress;",
        true
    )]
    [InlineData("", false)]
    public void Mvc_Suppressed_Inference_Binds_An_Unattributed_Complex_Parameter_From_Values(
        string configuration,
        bool reported
    )
    {
        // With inference suppressed, [ApiController] infers no body: the unattributed record
        // is bound member by member from route, query and form, as without [ApiController].
        var startup = $$"""
            using Microsoft.AspNetCore.Mvc;

            namespace Test;

            public static class Startup
            {
                public static void Configure(ApiBehaviorOptions options)
                {
                    {{configuration}}
                }
            }
            """;

        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            StreetRequest(),
            startup,
            MvcHandler("StreetRequest request")
        );

        if (reported)
        {
            Assert.Contains("MVC parameter 'request.Kind'", AssertReported(warnings).Message);
        }
        else
        {
            AssertNotReported(warnings);
        }
    }

    [Theory]
    [InlineData("options.SuppressInferBindingSourcesForParameters = suppress;")]
    [InlineData("options.SuppressInferBindingSourcesForParameters |= suppress;")]
    public void Mvc_Suppressed_Inference_That_Is_Not_A_Constant_Stops_The_Check(
        string configuration
    )
    {
        // Every MVC binding answer turns on this value, so a guess would make RIV4005 and
        // RIV4006 errors up; the check stops and names the assignment instead.
        var exception = Assert.Throws<RivetUserException>(() =>
            RunCheck(
                Contract,
                StreetKind(Snake),
                StreetRequest(),
                NonConstantSuppression(configuration),
                MvcHandler("StreetRequest request")
            )
        );

        Assert.StartsWith("error RIV1115: (9,9): ", exception.Message);
        Assert.Contains("isn't a compile-time constant", exception.Message);
    }

    [Fact]
    public void Mvc_Suppressed_Inference_That_Is_Not_A_Constant_Is_Not_Read_Without_ApiController()
    {
        // A plain controller infers no binding sources whatever the setting says, so its
        // value doesn't matter and the check carries on.
        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            StreetRequest(),
            NonConstantSuppression("options.SuppressInferBindingSourcesForParameters = suppress;"),
            MvcHandler("StreetRequest request", apiController: false)
        );

        Assert.Contains("MVC parameter 'request.Kind'", AssertReported(warnings).Message);
    }

    private static string NonConstantSuppression(string configuration) =>
        $$"""
            using Microsoft.AspNetCore.Mvc;

            namespace Test;

            public static class Startup
            {
                public static void Configure(ApiBehaviorOptions options, bool suppress)
                {
                    {{configuration}}
                }
            }
            """;

    [Fact]
    public void Mvc_Nested_Complex_Member_Is_Reported()
    {
        // ComplexObjectModelBinder recurses: ?Filter.Kind=... reaches the enum binder.
        const string request = """
            namespace Test;

            public sealed record StreetFilter(StreetKind Kind);

            public sealed record StreetRequest(StreetFilter Filter, string? Name);
            """;

        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            request,
            MvcHandler("[FromQuery] StreetRequest request")
        );

        Assert.Contains("MVC parameter 'request.Filter.Kind'", AssertReported(warnings).Message);
    }

    [Fact]
    public void Mvc_Self_Referencing_Complex_Member_Does_Not_Recurse_Forever()
    {
        const string request = """
            namespace Test;

            public sealed class StreetRequest
            {
                public StreetKind Kind { get; set; }

                public StreetRequest? Parent { get; set; }
            }
            """;

        var warnings = RunCheck(
            Contract,
            StreetKind(Snake),
            request,
            MvcHandler("[FromQuery] StreetRequest request")
        );

        Assert.Contains("MVC parameter 'request.Kind'", AssertReported(warnings).Message);
    }

    [Theory]
    [InlineData(Camel, false)]
    [InlineData(Snake, true)]
    public void Minimal_Api_FromForm_Complex_Parameter_Members_Bind_Ignoring_Case(
        string converter,
        bool reported
    )
    {
        // The minimal-API form mapper parses enums with Enum.TryParse(ignoreCase: true).
        var warnings = RunCheck(
            Contract,
            StreetKind(converter),
            StreetRequest(),
            MinimalHandler("[FromForm] StreetRequest request", verb: "Post")
        );

        if (reported)
        {
            Assert.Contains(
                "minimal API parameter 'request.Kind'",
                AssertReported(warnings).Message
            );
        }
        else
        {
            AssertNotReported(warnings, "Create");
        }
    }

    [Theory]
    [InlineData(Camel, true)]
    [InlineData(Builtin, true)]
    [InlineData("", false)]
    [InlineData(Builtin, true, "streets", "List")]
    [InlineData("", false, "streets", "List")]
    public void Functions_Handler_Binding_A_String_Enum_Is_Reported(
        string converter,
        bool reported,
        string route = "streets/{kind}",
        string field = "Get"
    )
    {
        // The worker fills 'kind' from the route values, or from the query when the route
        // has no such token, and none of its input converters turns a string into an enum
        // but JsonPocoConverter, which parses the raw text as JSON. No member name survives
        // that, whatever its casing; an integer does (tools/test_functions_host.py).
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
                [Function("get-street")]
                public IActionResult Run(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ROUTE")] object request,
                    StreetKind kind,
                    System.Threading.CancellationToken cancellationToken
                ) => StreetsContract.FIELD.Success(new StreetDto("1")).ToActionResult();
            }
            """;

        var warnings = RunCheck(
            Contract,
            StreetKind(converter),
            azure,
            function.Replace("ROUTE", route).Replace("FIELD", field)
        );

        if (reported)
        {
            var warning = AssertReported(warnings);
            Assert.Equal(field, warning.FieldName);
            Assert.Contains("Azure Functions parameter 'kind'", warning.Message);
            Assert.Contains("read a route or query value as JSON", warning.Message);
            Assert.Contains("for member Unknown", warning.Message);
        }
        else
        {
            AssertNotReported(warnings, field);
        }
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

            [RivetType]
            public sealed record StreetQuery(StreetKind Kind);

            [RivetContract]
            public static class StreetsContract
            {
                public static readonly RouteDefinition<StreetQuery, StreetDto> List =
                    Define.Get<StreetQuery, StreetDto>("/api/streets");
            }

            [ApiController]
            [Route("api/streets")]
            public sealed class StreetsController : ControllerBase
            {
                [HttpGet]
                public IActionResult List([FromQuery] StreetKind kind)
                    => StreetsContract
                        .List.Bind(new StreetQuery(kind))
                        .Success(new StreetDto("1"))
                        .ToActionResult();
            }
            """
        );

        var result = CliRunner.RunCli(work.FullName, [sourcePath, "--check"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error RIV4005:", result.StdErr);
        Assert.Contains("'restricted_byway'", result.StdErr);
    }

    // ---------------------------------------------------------------
    // RIV1110 — generation, annotated endpoints: MVC actions and static
    // minimal-API handlers
    // ---------------------------------------------------------------

    private enum Annotation
    {
        RivetEndpoint,
        RivetClient,
        StaticRivetEndpoint,
    }

    private static string AnnotatedAction(
        string converter,
        string parameter = "[FromQuery] StreetKind kind",
        Annotation annotation = Annotation.RivetEndpoint
    )
    {
        var (owner, endpointAttribute, modifier) = annotation switch
        {
            Annotation.RivetClient => (
                "[RivetClient]\n[Route(\"api/streets\")]\npublic sealed class StreetsController : ControllerBase",
                "",
                ""
            ),
            Annotation.StaticRivetEndpoint => (
                "[Route(\"api/streets\")]\npublic static class StreetsController",
                "[RivetEndpoint]",
                "static "
            ),
            _ => (
                "[Route(\"api/streets\")]\npublic sealed class StreetsController : ControllerBase",
                "[RivetEndpoint]",
                ""
            ),
        };
        return $$"""
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

            {{owner}}
            {
                {{endpointAttribute}}
                [HttpGet]
                [ProducesResponseType(typeof(StreetDto), 200)]
                public {{modifier}}IActionResult List({{parameter}})
                    => throw new NotImplementedException();
            }
            """;
    }

    private static void WalkEndpoints(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        CompilationHelper.WalkEndpoints(compilation, discovered, walker);
    }

    // Generation reads the setting too: RIV1110 on an [ApiController] action turns on it.
    [Fact]
    public void Annotated_ApiController_Action_Stops_Generation_On_Non_Constant_Suppression()
    {
        var action = AnnotatedAction(Snake)
            .Replace("[Route(\"api/streets\")]", "[ApiController]\n[Route(\"api/streets\")]");
        var compilation = CompilationHelper.CreateCompilationFromMultiple([
            action,
            NonConstantSuppression("options.SuppressInferBindingSourcesForParameters = suppress;"),
        ]);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkEndpoints(compilation, discovered, walker)
        );

        Assert.StartsWith($"error {Diagnostics.BindingInferenceUnreadable}: ", exception.Message);
        Assert.Equal("RIV1115", Diagnostics.BindingInferenceUnreadable);
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

    [Fact]
    public void Client_Action_With_Snake_Case_Enum_Query_Fails_Generation()
    {
        var exception = Assert.Throws<RivetUserException>(() =>
            WalkEndpoints(AnnotatedAction(Snake, annotation: Annotation.RivetClient))
        );

        Assert.StartsWith($"error {Diagnostics.UnbindableEnumParameter}:", exception.Message);
        Assert.Contains("MVC parameter 'kind'", exception.Message);
        Assert.Contains("'restricted_byway'", exception.Message);
    }

    [Fact]
    public void Static_Endpoint_With_Camel_Case_Enum_Query_Fails_Generation()
    {
        // A static [RivetEndpoint] is a minimal-API handler: Enum.TryParse matches case too.
        var exception = Assert.Throws<RivetUserException>(() =>
            WalkEndpoints(AnnotatedAction(Camel, annotation: Annotation.StaticRivetEndpoint))
        );

        Assert.StartsWith($"error {Diagnostics.UnbindableEnumParameter}:", exception.Message);
        Assert.Contains("minimal API parameter 'kind'", exception.Message);
        Assert.Contains("Enum.TryParse", exception.Message);
        Assert.Contains("'unknown'", exception.Message);
    }
}
