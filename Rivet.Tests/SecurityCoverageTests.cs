using Microsoft.CodeAnalysis;
using Rivet.Tool.Analysis;

namespace Rivet.Tests;

/// <summary>
/// K.5: <c>--check</c> compares each contract endpoint's security with its handler's
/// authorization, read from ASP.NET's own metadata. RIV4008 when they disagree, RIV4009 when the
/// setup can't be read. The fixtures compile against the whole ASP.NET shared framework, so
/// they can be written the way an app's Program.cs is.
/// </summary>
public sealed class SecurityCoverageTests
{
    private const string Scheme =
        """[assembly: RivetSecurityScheme("bearer", "http", scheme: "bearer")]""";

    private const string SecuredDefault =
        Scheme
        + "\n[assembly: RivetGlobalSecurity(0)]"
        + "\n[assembly: RivetGlobalSecurityScheme(0, \"bearer\", new string[0])]";

    private const string Handler = "() => ItemsContract.Get.Success(new ItemDto(\"1\")).ToResult()";

    // ---------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------

    private static string Contract(string security = "", string assembly = "") =>
        $$"""
            using Rivet;

            {{assembly}}

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<ItemDto> Get =
                    Define.Get<ItemDto>("/api/items"){{security}};

                public static readonly RouteDefinition<ItemDto> Other =
                    Define.Get<ItemDto>("/api/other"){{security}};
            }
            """;

    private static string Controller(
        string controllerAttributes = "",
        string actionAttributes = "",
        string baseType = "ControllerBase",
        string name = "ItemsController",
        string field = "Get",
        string route = "items"
    ) =>
        $$"""
            using Microsoft.AspNetCore.Authorization;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            {{controllerAttributes}}
            [Route("api")]
            public class {{name}} : {{baseType}}
            {
                {{actionAttributes}}
                [HttpGet("{{route}}")]
                public IActionResult {{field}}() =>
                    ItemsContract.{{field}}.Success(new ItemDto("1")).ToActionResult();
            }
            """;

    private static string App(string body, string members = "") =>
        $$"""
            using System;
            using Microsoft.AspNetCore.Authorization;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Mvc.Authorization;
            using Microsoft.AspNetCore.Mvc.Filters;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.Extensions.DependencyInjection;
            using Rivet;

            namespace Test;

            public static class Setup
            {
                public static void Configure(
                    WebApplication app,
                    IServiceCollection services,
                    bool flag,
                    AuthorizationPolicy policyFromOutside,
                    RouteGroupBuilder groupFromOutside
                )
                {
                    {{body}}
                }

                {{members}}
            }

            public sealed class AntiforgeryFilter : IAsyncAuthorizationFilter
            {
                public System.Threading.Tasks.Task OnAuthorizationAsync(AuthorizationFilterContext context) =>
                    System.Threading.Tasks.Task.CompletedTask;
            }
            """;

    private static IReadOnlyList<CoverageWarning> Check(params string[] sources) =>
        CheckWith(OutputKind.DynamicallyLinkedLibrary, sources);

    private static IReadOnlyList<CoverageWarning> CheckWith(
        OutputKind kind,
        params string[] sources
    ) =>
        CompilationHelper.CheckCoverage(
            CompilationHelper.CreateFrameworkCompilation(kind, sources)
        );

    private static List<CoverageWarning> Security(
        IReadOnlyList<CoverageWarning> warnings,
        string field = "Get"
    ) =>
        warnings
            .Where(warning =>
                warning.Kind
                    is CoverageWarningKind.SecurityMismatch
                        or CoverageWarningKind.AuthorizationUnreadable
                && warning.FieldName == field
            )
            .ToList();

    private static CoverageWarning Summary(IReadOnlyList<CoverageWarning> warnings) =>
        Assert.Single(
            warnings,
            warning =>
                warning.Kind == CoverageWarningKind.SecurityMismatch && warning.FieldName == ""
        );

    private static void AssertVerdict(IReadOnlyList<CoverageWarning> warnings, string? expected)
    {
        var found = Security(warnings);
        if (expected is null)
        {
            Assert.Empty(found);
            return;
        }

        var warning = Assert.Single(found);
        Assert.Equal(
            expected == "RIV4009"
                ? CoverageWarningKind.AuthorizationUnreadable
                : CoverageWarningKind.SecurityMismatch,
            warning.Kind
        );
        if (expected != "RIV4009")
        {
            Assert.Contains(expected, warning.Message);
        }
    }

    private const string Case1 = "anyone can call the handler";
    private const string Case2 = "an anonymous caller gets a 401";

    // ---------------------------------------------------------------
    // Symbols
    // ---------------------------------------------------------------

    [Fact]
    public void Authorization_symbols_resolve_in_the_test_compilation()
    {
        var wkt = new WellKnownTypes(CompilationHelper.CreateCompilation("namespace Test;"));

        Assert.NotNull(wkt.Authorize);
        Assert.NotNull(wkt.AllowAnonymous);
        Assert.NotNull(wkt.IAuthorizeData);
        Assert.NotNull(wkt.IAllowAnonymous);
        Assert.NotNull(wkt.AuthorizationEndpointConventionBuilderExtensions);
        Assert.NotNull(wkt.AuthorizationOptions);
    }

    // ---------------------------------------------------------------
    // MVC attributes
    // ---------------------------------------------------------------

    [Theory]
    // A secured contract needs a handler that requires authentication.
    [InlineData(".Secure(\"bearer\")", Scheme, "", "", Case1)]
    [InlineData(".Secure(\"bearer\")", Scheme, "[Authorize]", "", null)]
    [InlineData(".Secure(\"bearer\")", Scheme, "", "[Authorize(Roles = \"Admin\")]", null)]
    [InlineData(".Secure(\"bearer\")", Scheme, "[Authorize]", "[AllowAnonymous]", Case1)]
    [InlineData(".Secure(\"bearer\")", Scheme, "[AllowAnonymous]", "[Authorize]", Case1)]
    [InlineData("", SecuredDefault, "", "", "the document's default security")]
    [InlineData("", SecuredDefault, "[Authorize]", "", null)]
    // A contract that admits anonymous callers needs a handler that lets them in.
    [InlineData(".Anonymous()", SecuredDefault, "[Authorize]", "", ".Anonymous()")]
    [InlineData(".Secure(\"bearer\").OrAnonymous()", Scheme, "[Authorize]", "", ".OrAnonymous()")]
    [InlineData(".Anonymous()", SecuredDefault, "", "", null)]
    [InlineData(".Anonymous()", SecuredDefault, "[Authorize]", "[AllowAnonymous]", null)]
    // No requirement anywhere is "no credentials needed"; with a scheme declared, it's per endpoint.
    [InlineData(
        "",
        Scheme,
        "[Authorize]",
        "",
        "declares no security and the document has no default"
    )]
    [InlineData("", Scheme, "", "", null)]
    [InlineData("", "", "", "", null)]
    public void Mvc_action_authorization_against_the_contract(
        string security,
        string assembly,
        string controllerAttributes,
        string actionAttributes,
        string? expected
    )
    {
        var warnings = Check(
            Contract(security, assembly),
            Controller(controllerAttributes, actionAttributes)
        );

        AssertVerdict(warnings, expected);
    }

    [Theory]
    [InlineData("[Authorize]", "", null)]
    [InlineData("[Authorize]", "[AllowAnonymous]", Case1)]
    [InlineData("[AllowAnonymous]", "[Authorize]", Case1)]
    public void Mvc_reads_base_controllers_and_overridden_actions(
        string baseAttributes,
        string controllerAttributes,
        string? expected
    )
    {
        var baseController = $$"""
            using Microsoft.AspNetCore.Authorization;
            using Microsoft.AspNetCore.Mvc;

            namespace Test;

            {{baseAttributes}}
            public abstract class SecureController : ControllerBase;
            """;

        var warnings = Check(
            Contract(".Secure(\"bearer\")", Scheme),
            baseController,
            Controller(controllerAttributes, baseType: "SecureController")
        );

        AssertVerdict(warnings, expected);
    }

    [Fact]
    public void Mvc_reads_a_custom_authorize_attribute_by_its_metadata_interface()
    {
        var attribute = """
            using System;
            using Microsoft.AspNetCore.Authorization;

            namespace Test;

            public sealed class StaffAttribute : Attribute, IAuthorizeData
            {
                public string? Policy { get; set; }
                public string? Roles { get; set; } = "Staff";
                public string? AuthenticationSchemes { get; set; }
            }
            """;

        var warnings = Check(
            Contract(".Anonymous()", SecuredDefault),
            attribute,
            Controller("[Staff]")
        );

        AssertVerdict(warnings, "[Staff] on ItemsController");
    }

    // ---------------------------------------------------------------
    // [RivetEndpoint] actions: the action's own security attributes are its spec
    // ---------------------------------------------------------------

    private static string Annotated(string attributes) =>
        $$"""
            using Microsoft.AspNetCore.Authorization;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            {{Scheme}}

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                {{attributes}}
                [RivetEndpoint]
                [HttpGet("")]
                public TaskDto List() => new("1");
            }
            """;

    [Theory]
    [InlineData("[RivetSecure(\"bearer\")]", Case1)]
    [InlineData("[RivetSecure(\"bearer\")] [Authorize]", null)]
    // [AllowAnonymous] beside [RivetSecure] is ASP.NET's business: K.3 left it to --check.
    [InlineData("[RivetSecure(\"bearer\")] [AllowAnonymous]", Case1)]
    [InlineData("[RivetSecure(\"bearer\")] [RivetOrAnonymous] [AllowAnonymous]", null)]
    [InlineData("[RivetAnonymous] [Authorize]", "the action's security is anonymous")]
    [InlineData("[AllowAnonymous]", null)]
    [InlineData("[Authorize]", "declares no security and the document has no default")]
    [InlineData("", null)]
    public void RivetEndpoint_action_security_against_its_authorization(
        string attributes,
        string? expected
    )
    {
        var warnings = Check(Annotated(attributes));

        var found = Security(warnings, "List");
        if (expected is null)
        {
            Assert.Empty(found);
            return;
        }

        var warning = Assert.Single(found);
        Assert.Equal(CoverageWarningKind.SecurityMismatch, warning.Kind);
        Assert.Contains("TasksController.List (GET /api/tasks)", warning.Message);
        Assert.Contains(expected, warning.Message);
    }

    [Fact]
    public void RivetEndpoint_action_implementing_a_contract_is_compared_once()
    {
        var action = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [RivetEndpoint]
                [RivetSecure("bearer")]
                [HttpGet("items")]
                public IActionResult Get() => ItemsContract.Get.Success(new ItemDto("1")).ToActionResult();
            }
            """;

        var warnings = Check(Contract(".Secure(\"bearer\")", Scheme), action);

        Assert.Single(warnings, warning => warning.Kind == CoverageWarningKind.SecurityMismatch);
    }

    [Fact]
    public void A_handler_serving_two_contract_fields_is_compared_against_each()
    {
        var contract = $$"""
            using Rivet;

            {{Scheme}}

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<ItemDto> Get =
                    Define.Get<ItemDto>("/api/items").Anonymous();

                public static readonly RouteDefinition<ItemDto> Other =
                    Define.Get<ItemDto>("/api/items").Secure("bearer");
            }
            """;
        var controller = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpGet("items")]
                public IActionResult Get(bool other) =>
                    other
                        ? ItemsContract.Other.Success(new ItemDto("1")).ToActionResult()
                        : ItemsContract.Get.Success(new ItemDto("1")).ToActionResult();
            }
            """;

        var warnings = Check(contract, controller);

        Assert.Empty(Security(warnings));
        Assert.Contains(Case1, Assert.Single(Security(warnings, "Other")).Message);
    }

    /// <summary>
    /// Every operation whose security is declared with attributes is held to it, whatever
    /// marks it: a [RivetClient] controller's action, an override of an abstract
    /// [RivetContract] method, and a static [RivetEndpoint] method a minimal API maps.
    /// </summary>
    [Theory]
    [InlineData("client", "", "", Case1)]
    [InlineData("client", "[Authorize]", "", null)]
    [InlineData("override", "", "", Case1)]
    [InlineData("override", "[Authorize]", "", null)]
    [InlineData("static", "", "", Case1)]
    [InlineData("static", "", ".RequireAuthorization()", null)]
    [InlineData("static", "[Authorize]", "", null)]
    public void Every_attribute_declared_operation_is_compared(
        string kind,
        string authorization,
        string convention,
        string? expected
    )
    {
        var operation = kind switch
        {
            "client" => $$"""
                [RivetClient]
                [Route("api/tasks")]
                public sealed class TasksController : ControllerBase
                {
                    [RivetSecure("bearer")]
                    {{authorization}}
                    [HttpGet("")]
                    public TaskDto List() => new("1");
                }
                """,
            "override" => $$"""
                [RivetContract]
                [Route("api/tasks")]
                public abstract class TasksContract : ControllerBase
                {
                    [RivetSecure("bearer")]
                    [HttpGet("")]
                    public abstract TaskDto List();
                }

                public sealed class TasksController : TasksContract
                {
                    {{authorization}}
                    public override TaskDto List() => new("1");
                }
                """,
            _ => $$"""
                public static class Tasks
                {
                    public static void Map(WebApplication app) =>
                        app.MapGet("/api/tasks", List){{convention}};

                    [RivetEndpoint]
                    [RivetSecure("bearer")]
                    {{authorization}}
                    [HttpGet("/api/tasks")]
                    public static TaskDto List() => new("1");
                }
                """,
        };
        var source = $$"""
            using Microsoft.AspNetCore.Authorization;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            {{Scheme}}

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            {{operation}}
            """;

        var found = Security(Check(source), "List");

        if (expected is null)
        {
            Assert.Empty(found);
            return;
        }

        var warning = Assert.Single(found);
        Assert.Equal(CoverageWarningKind.SecurityMismatch, warning.Kind);
        Assert.Contains("(GET /api/tasks)", warning.Message);
        Assert.Contains(expected, warning.Message);
    }

    // ---------------------------------------------------------------
    // casebridge: a global AuthorizeFilter, opted out with [AllowAnonymous]
    // ---------------------------------------------------------------

    private static readonly string[] _casebridge =
    [
        App(
            """
            services.AddControllers(options =>
            {
                options.Filters.Add(new AuthorizeFilter());
                options.Filters.Add<AntiforgeryFilter>();
                options.Filters.Add(new AntiforgeryFilter());
            });
            services.AddAuthorization();
            app.MapControllers();
            """
        ),
        Controller(),
        Controller("[AllowAnonymous]", name: "OtherController", field: "Other", route: "other"),
    ];

    [Fact]
    public void Casebridge_global_filter_with_no_security_declared_is_one_summary()
    {
        var warnings = Check([Contract(), .. _casebridge]);

        var summary = Summary(warnings);
        Assert.Contains("1 endpoint(s) require authentication", summary.Message);
        Assert.Contains("ItemsContract.Get (GET /api/items)", summary.Message);
        Assert.DoesNotContain("ItemsContract.Other", summary.Message);
        Assert.Contains("[assembly: RivetGlobalSecurity(0)]", summary.Message);
        Assert.Empty(Security(warnings));
        Assert.Empty(Security(warnings, "Other"));
        // A custom authorization filter is invisible: it makes nothing unreadable.
        Assert.DoesNotContain(
            warnings,
            warning => warning.Kind == CoverageWarningKind.AuthorizationUnreadable
        );
    }

    [Fact]
    public void Casebridge_global_filter_with_a_secured_default_flags_the_anonymous_actions()
    {
        var warnings = Check([Contract(assembly: SecuredDefault), .. _casebridge]);

        AssertVerdict(warnings, null);
        var other = Assert.Single(Security(warnings, "Other"));
        Assert.Equal(CoverageWarningKind.SecurityMismatch, other.Kind);
        Assert.Contains(Case1, other.Message);
        Assert.Contains("[AllowAnonymous] on OtherController", other.Message);
    }

    [Theory]
    [InlineData("options.Filters.Add(new AuthorizeFilter())", null)]
    [InlineData(
        "options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()))",
        null
    )]
    [InlineData(
        "var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(); options.Filters.Add(new AuthorizeFilter(policy))",
        null
    )]
    [InlineData("options.Filters.Add(new AuthorizeFilter(\"Staff\"))", null)]
    [InlineData("options.Filters.Insert(0, new AuthorizeFilter())", null)]
    [InlineData("options.Filters.Add(new AuthorizeFilter(policyFromOutside))", "RIV4009")]
    [InlineData(
        "options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build()))",
        "RIV4009"
    )]
    [InlineData("options.Filters.Add<AuthorizeFilter>()", "RIV4009")]
    [InlineData("if (flag) options.Filters.Add(new AuthorizeFilter())", "RIV4009")]
    [InlineData("options.Filters.Add(new AntiforgeryFilter())", Case1)]
    public void Mvc_filter_is_read_only_when_it_is_an_authorize_filter_built_where_check_can_see(
        string registration,
        string? expected
    )
    {
        var warnings = Check(
            Contract(assembly: SecuredDefault),
            App($"services.AddControllers(options => {{ {registration}; }});"),
            Controller()
        );

        AssertVerdict(warnings, expected);
    }

    // Past 16 unknowns the cases aren't enumerated, but an [AllowAnonymous] decides anyway.
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    public void AllowAnonymous_decides_the_action_even_under_a_conditional_filter(int filters)
    {
        var conditional = string.Concat(
            Enumerable.Repeat("if (flag) options.Filters.Add(new AuthorizeFilter()); ", filters)
        );
        var warnings = Check(
            Contract(".Anonymous()", SecuredDefault),
            App($"services.AddControllers(options => {{ {conditional}}});"),
            Controller("[AllowAnonymous]")
        );

        AssertVerdict(warnings, null);
    }

    /// <summary>
    /// An MVC filter or a fallback policy set in a helper applies where the helper is called:
    /// under the call's condition, never when nothing calls it, and wherever the call it's
    /// handed to as a method group runs.
    /// </summary>
    [Theory]
    [InlineData("AddAppAuthorization(services);", null)]
    [InlineData("if (flag) AddAppAuthorization(services);", "RIV4009")]
    [InlineData("if (flag) AddAppAuthorization(services); AddAppAuthorization(services);", null)]
    [InlineData("", Case1)]
    [InlineData("services.AddControllers(ConfigureMvc);", null)]
    [InlineData("if (flag) services.AddControllers(ConfigureMvc);", "RIV4009")]
    [InlineData("Action<MvcOptions> later = ConfigureMvc;", "RIV4009")]
    [InlineData("if (flag) AddFallback(services);", "RIV4009")]
    [InlineData("AddFallback(services);", null)]
    public void App_wide_setup_in_a_helper_runs_where_the_helper_is_called(
        string body,
        string? expected
    )
    {
        var warnings = Check(
            Contract(assembly: SecuredDefault),
            App(
                body,
                "private static void AddAppAuthorization(IServiceCollection services) => "
                    + "services.AddControllers(options => options.Filters.Add(new AuthorizeFilter()));\n"
                    + "private static void ConfigureMvc(MvcOptions options) => options.Filters.Add(new AuthorizeFilter());\n"
                    + "private static void AddFallback(IServiceCollection services) => "
                    + "services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());"
            ),
            Controller()
        );

        AssertVerdict(warnings, expected);
    }

    // ---------------------------------------------------------------
    // speechscribe: RequireAuthorization() on MapControllers(), under a config switch
    // ---------------------------------------------------------------

    private static string SpeechscribeProgram(string requireAuthorization) =>
        $$"""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Configuration;

            var builder = WebApplication.CreateBuilder(args);
            var entraEnabled = builder.Configuration.GetValue<bool>("Entra:Enabled");
            var app = builder.Build();
            app.UseAuthorization();
            var controllerEndpoints = app.MapControllers();
            {{requireAuthorization}}
            app.Run();
            """;

    [Theory]
    [InlineData(
        "if (entraEnabled)\n{\n    controllerEndpoints.RequireAuthorization();\n}",
        "",
        "RIV4009"
    )]
    [InlineData(
        "if (entraEnabled)\n{\n    controllerEndpoints.RequireAuthorization();\n}",
        "[AllowAnonymous]",
        Case1
    )]
    [InlineData(
        "if (entraEnabled)\n{\n    controllerEndpoints.RequireAuthorization();\n}",
        "[Authorize]",
        null
    )]
    [InlineData("controllerEndpoints.RequireAuthorization();", "", null)]
    [InlineData("app.MapControllers().RequireAuthorization();", "", null)]
    [InlineData("controllerEndpoints.RequireAuthorization().AllowAnonymous();", "", Case1)]
    [InlineData("", "", Case1)]
    public void Speechscribe_conditional_map_controllers_requirement(
        string requireAuthorization,
        string controllerAttributes,
        string? expected
    )
    {
        var warnings = CheckWith(
            OutputKind.ConsoleApplication,
            SpeechscribeProgram(requireAuthorization),
            Contract(assembly: SecuredDefault),
            Controller(controllerAttributes)
        );

        AssertVerdict(warnings, expected);
    }

    [Fact]
    public void Unreadable_requirement_names_the_site_and_its_condition()
    {
        var warnings = CheckWith(
            OutputKind.ConsoleApplication,
            SpeechscribeProgram(
                "if (entraEnabled)\n{\n    controllerEndpoints.RequireAuthorization();\n}"
            ),
            Contract(assembly: SecuredDefault),
            Controller()
        );

        var warning = Assert.Single(Security(warnings));
        Assert.Equal(CoverageWarningKind.AuthorizationUnreadable, warning.Kind);
        Assert.Contains("RequireAuthorization() on MapControllers() at (11,", warning.Message);
        Assert.Contains("is applied only under if (entraEnabled)", warning.Message);
    }

    [Fact]
    public void Speechscribe_today_is_unreadable_and_one_summary()
    {
        // No security declared: the conditional requirement can't be read, an [AllowAnonymous]
        // action agrees, and an [Authorize] action is the summary's.
        var warnings = CheckWith(
            OutputKind.ConsoleApplication,
            SpeechscribeProgram(
                "if (entraEnabled)\n{\n    controllerEndpoints.RequireAuthorization();\n}"
            ),
            Contract(),
            Controller(),
            Controller("[Authorize]", name: "OtherController", field: "Other", route: "other")
        );

        AssertVerdict(warnings, "RIV4009");
        Assert.Contains("ItemsContract.Other (GET /api/other)", Summary(warnings).Message);
        Assert.Empty(Security(warnings, "Other"));
    }

    // ---------------------------------------------------------------
    // Minimal APIs: endpoint and group conventions, handler attributes
    // ---------------------------------------------------------------

    [Theory]
    [InlineData($"app.MapGet(\"/api/items\", {Handler});", Case1)]
    [InlineData($"app.MapGet(\"/api/items\", {Handler}).RequireAuthorization();", null)]
    [InlineData(
        $"app.MapGet(\"/api/items\", {Handler}).WithName(\"Get\").RequireAuthorization(\"Staff\");",
        null
    )]
    [InlineData(
        $"app.MapGet(\"/api/items\", {Handler}).RequireAuthorization(policy => policy.RequireAuthenticatedUser());",
        null
    )]
    [InlineData(
        $"app.MapGet(\"/api/items\", {Handler}).RequireAuthorization(new AuthorizeAttribute {{ Roles = \"Staff\" }});",
        null
    )]
    [InlineData(
        $"app.MapGet(\"/api/items\", {Handler}).RequireAuthorization(policyFromOutside);",
        "RIV4009"
    )]
    [InlineData(
        $"app.MapGet(\"/api/items\", {Handler}).RequireAuthorization(policy => policy.RequireAssertion(_ => true));",
        "RIV4009"
    )]
    [InlineData(
        $"var endpoint = app.MapGet(\"/api/items\", {Handler}); endpoint.RequireAuthorization();",
        null
    )]
    [InlineData(
        $"var endpoint = app.MapGet(\"/api/items\", {Handler}); if (flag) endpoint.RequireAuthorization();",
        "RIV4009"
    )]
    [InlineData($"if (flag) app.MapGet(\"/api/items\", {Handler}).RequireAuthorization();", null)]
    [InlineData(
        $"app.MapGet(\"/api/items\", {Handler}).RequireAuthorization().AllowAnonymous();",
        Case1
    )]
    [InlineData($"app.MapGet(\"/api/items\", [Authorize] {Handler});", null)]
    [InlineData(
        $"app.MapGet(\"/api/items\", [AllowAnonymous] {Handler}).RequireAuthorization();",
        Case1
    )]
    public void Minimal_api_endpoint_authorization(string body, string? expected)
    {
        var warnings = Check(Contract(assembly: SecuredDefault), App(body));

        AssertVerdict(warnings, expected);
    }

    [Theory]
    [InlineData(
        $"var api = app.MapGroup(\"/api\").RequireAuthorization(); api.MapGet(\"/items\", {Handler});",
        null
    )]
    [InlineData(
        $"var api = app.MapGroup(\"/api\"); api.RequireAuthorization(); api.MapGet(\"/items\", {Handler});",
        null
    )]
    [InlineData(
        $"var outer = app.MapGroup(\"\").RequireAuthorization(); var api = outer.MapGroup(\"/api\"); api.MapGet(\"/items\", {Handler});",
        null
    )]
    [InlineData(
        $"var api = app.MapGroup(\"/api\").RequireAuthorization(); api.MapGet(\"/items\", {Handler}).AllowAnonymous();",
        Case1
    )]
    [InlineData(
        $"var api = app.MapGroup(\"/api\"); if (flag) api.RequireAuthorization(); api.MapGet(\"/items\", {Handler});",
        "RIV4009"
    )]
    [InlineData($"var api = app.MapGroup(\"/api\"); api.MapGet(\"/items\", {Handler});", Case1)]
    [InlineData(
        $"app.MapGroup(\"/api\").RequireAuthorization().MapGet(\"/items\", {Handler});",
        null
    )]
    [InlineData($"app.MapGroup(\"/api\").Secured().MapGet(\"/items\", {Handler});", null)]
    [InlineData(
        $"var api = app.MapGroup(\"/api\"); if (flag) api.Secured(); api.MapGet(\"/items\", {Handler});",
        "RIV4009"
    )]
    public void Minimal_api_route_group_authorization(string body, string? expected)
    {
        var warnings = Check(
            Contract(assembly: SecuredDefault),
            App(
                body,
                "private static RouteGroupBuilder Secured(this RouteGroupBuilder group) { group.RequireAuthorization(); return group; }"
            )
        );

        Assert.DoesNotContain(
            warnings,
            warning => warning.Kind == CoverageWarningKind.RouteMismatch
        );
        AssertVerdict(warnings, expected);
    }

    [Fact]
    public void Minimal_api_on_a_group_parameter_is_unreadable()
    {
        var warnings = Check(
            Contract(assembly: SecuredDefault),
            App($"groupFromOutside.MapGet(\"/api/items\", {Handler});")
        );

        var warning = Assert.Single(Security(warnings));
        Assert.Equal(CoverageWarningKind.AuthorizationUnreadable, warning.Kind);
        Assert.Contains("route group 'groupFromOutside'", warning.Message);
    }

    [Theory]
    [InlineData($"Secure(app.MapGet(\"/api/items\", {Handler}));", null)]
    [InlineData(
        $"var endpoint = app.MapGet(\"/api/items\", {Handler}); if (flag) Secure(endpoint);",
        "RIV4009"
    )]
    [InlineData($"app.MapGet(\"/api/items\", {Handler}).Also();", null)]
    [InlineData(
        $"Action<RouteHandlerBuilder> later = Secure; later(app.MapGet(\"/api/items\", {Handler}));",
        "RIV4009"
    )]
    public void Helper_applying_a_convention_is_followed_to_its_callers(
        string body,
        string? expected
    )
    {
        var warnings = Check(
            Contract(assembly: SecuredDefault),
            App(
                body,
                "private static void Secure(RouteHandlerBuilder builder) => builder.RequireAuthorization();\n"
                    + "private static RouteHandlerBuilder Also(this RouteHandlerBuilder builder) { Secure(builder); return builder; }"
            )
        );

        AssertVerdict(warnings, expected);
    }

    [Fact]
    public void A_builder_reached_along_two_paths_is_traced_along_both()
    {
        // Secure's group comes from Configure directly and through Wire: the second path
        // reaches 'api' again, which is the same origin, not an untraceable builder.
        var warnings = Check(
            Contract(assembly: SecuredDefault),
            App(
                $"var api = app.MapGroup(\"/api\"); Secure(api); Wire(api); api.MapGet(\"/items\", {Handler});"
                    + " app.MapGet(\"/api/other\", () => ItemsContract.Other.Success(new ItemDto(\"1\")).ToResult());",
                "private static void Secure(RouteGroupBuilder group) => group.RequireAuthorization();\n"
                    + "private static void Wire(RouteGroupBuilder group) => Secure(group);"
            )
        );

        AssertVerdict(warnings, null);
        var other = Assert.Single(Security(warnings, "Other"));
        Assert.Equal(CoverageWarningKind.SecurityMismatch, other.Kind);
        Assert.Contains(Case1, other.Message);
    }

    [Fact]
    public void Untraced_convention_is_unreadable_for_the_hosts_it_could_reach()
    {
        // A lambda's parameter can't be traced to its Map* call, so the RouteHandlerBuilder
        // requirement may reach any minimal-API endpoint, but never an MVC action.
        var warnings = Check(
            Contract(assembly: SecuredDefault),
            App(
                "Action<RouteHandlerBuilder> secure = builder => builder.RequireAuthorization();"
                    + $" secure(app.MapGet(\"/api/items\", {Handler})); app.MapControllers().RequireAuthorization();"
            ),
            Controller(name: "OtherController", field: "Other", route: "other")
        );

        var warning = Assert.Single(Security(warnings));
        Assert.Equal(CoverageWarningKind.AuthorizationUnreadable, warning.Kind);
        Assert.Contains("can't trace to the Map* call", warning.Message);
        Assert.Empty(Security(warnings, "Other"));
    }

    [Fact]
    public void Method_group_handler_attributes_count()
    {
        var warnings = Check(
            Contract(".Anonymous()", SecuredDefault),
            App(
                "app.MapGet(\"/api/items\", Get);",
                "[Authorize] private static IResult Get() => ItemsContract.Get.Success(new ItemDto(\"1\")).ToResult();"
            )
        );

        AssertVerdict(warnings, "[Authorize] on the handler");
    }

    // ---------------------------------------------------------------
    // Fallback policy
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(
        "services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());",
        "",
        null
    )]
    [InlineData(
        "services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());",
        "",
        null
    )]
    [InlineData(
        "services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());",
        ".AllowAnonymous()",
        Case1
    )]
    [InlineData(
        "services.AddAuthorizationBuilder().AddFallbackPolicy(\"signed-in\", new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());",
        "",
        null
    )]
    [InlineData(
        "services.AddAuthorizationBuilder().AddFallbackPolicy(\"signed-in\", policy => policy.RequireAuthenticatedUser());",
        "",
        null
    )]
    [InlineData(
        "services.AddAuthorizationBuilder().AddFallbackPolicy(\"outside\", policyFromOutside);",
        "",
        "RIV4009"
    )]
    // The default policy requires an authenticated user unless the app sets another.
    [InlineData("services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);", "", null)]
    [InlineData(
        "services.AddAuthorization(o => { o.DefaultPolicy = policyFromOutside; o.FallbackPolicy = o.DefaultPolicy; });",
        "",
        "RIV4009"
    )]
    [InlineData(
        "services.AddAuthorizationBuilder().SetDefaultPolicy(policyFromOutside);",
        ".RequireAuthorization()",
        "RIV4009"
    )]
    [InlineData(
        "services.AddAuthorizationBuilder().SetDefaultPolicy(policyFromOutside);",
        ".RequireAuthorization(\"Staff\")",
        null
    )]
    [InlineData(
        "services.AddAuthorizationBuilder().AddDefaultPolicy(\"signed-in\", policy => policy.RequireAuthenticatedUser());",
        ".RequireAuthorization()",
        null
    )]
    [InlineData("services.AddAuthorization(o => o.FallbackPolicy = null);", "", Case1)]
    [InlineData(
        "services.AddAuthorization(o => o.FallbackPolicy = policyFromOutside);",
        "",
        "RIV4009"
    )]
    [InlineData(
        "services.AddAuthorization(o => { if (flag) o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(); });",
        "",
        "RIV4009"
    )]
    // A requirement on the endpoint decides it whatever the fallback, which then doesn't apply.
    [InlineData(
        "services.AddAuthorization(o => o.FallbackPolicy = policyFromOutside);",
        ".RequireAuthorization()",
        null
    )]
    [InlineData(
        "services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());",
        ".RequireAuthorization(policyFromOutside)",
        "RIV4009"
    )]
    public void Fallback_policy_applies_when_the_endpoint_has_no_authorization_of_its_own(
        string setup,
        string convention,
        string? expected
    )
    {
        var warnings = Check(
            Contract(assembly: SecuredDefault),
            App($"{setup} app.MapGet(\"/api/items\", {Handler}){convention};")
        );

        AssertVerdict(warnings, expected);
    }

    [Fact]
    public void Conditional_requirement_over_a_fallback_that_requires_is_decided()
    {
        // Either the endpoint's requirement applies or the fallback does: both require.
        var warnings = Check(
            Contract(".Anonymous()", SecuredDefault),
            App(
                "services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());"
                    + $" var endpoint = app.MapGet(\"/api/items\", {Handler}); if (flag) endpoint.RequireAuthorization();"
            )
        );

        AssertVerdict(warnings, Case2);
    }

    [Theory]
    [InlineData(
        $"var endpoint = app.MapGet(\"/api/items\", {Handler}); if (!flag) return; endpoint.RequireAuthorization();"
    )]
    // A helper declared above the call: its own early return still counts.
    [InlineData(
        "void Secure(RouteHandlerBuilder builder) { if (!flag) return; builder.RequireAuthorization(); }"
            + $" Secure(app.MapGet(\"/api/items\", {Handler}));"
    )]
    public void Early_return_before_a_requirement_makes_it_conditional(string body)
    {
        var warnings = Check(Contract(assembly: SecuredDefault), App(body));

        var warning = Assert.Single(Security(warnings));
        Assert.Equal(CoverageWarningKind.AuthorizationUnreadable, warning.Kind);
        Assert.Contains("after the return at", warning.Message);
    }

    // ---------------------------------------------------------------
    // Azure Functions
    // ---------------------------------------------------------------

    private const string FunctionsAttributes = """
        using System;

        namespace Microsoft.Azure.Functions.Worker;

        public enum AuthorizationLevel { Anonymous, User, Function, System, Admin }

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class FunctionAttribute(string name) : Attribute
        {
            public string Name { get; } = name;
        }

        [AttributeUsage(AttributeTargets.Parameter)]
        public sealed class HttpTriggerAttribute : Attribute
        {
            public HttpTriggerAttribute() => AuthLevel = AuthorizationLevel.Function;
            public HttpTriggerAttribute(params string[] methods) : this() => Methods = methods;
            public HttpTriggerAttribute(AuthorizationLevel authLevel, params string[] methods)
            {
                AuthLevel = authLevel;
                Methods = methods;
            }

            public AuthorizationLevel AuthLevel { get; set; }
            public string[]? Methods { get; }
            public string? Route { get; set; }
        }
        """;

    private static string Function(string trigger) =>
        $$"""
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Azure.Functions.Worker;
            using Rivet;

            namespace Test;

            public sealed class ItemsFunction
            {
                [Function("Get")]
                public IActionResult Run([HttpTrigger({{trigger}}, Route = "items")] object request) =>
                    ItemsContract.Get.Success(new ItemDto("1")).ToActionResult();
            }
            """;

    [Theory]
    [InlineData(".Secure(\"bearer\")", Scheme, "AuthorizationLevel.Anonymous, \"get\"", Case1)]
    [InlineData(".Secure(\"bearer\")", Scheme, "AuthorizationLevel.Function, \"get\"", null)]
    [InlineData(".Secure(\"bearer\")", Scheme, "\"get\"", null)]
    [InlineData(".Anonymous()", SecuredDefault, "AuthorizationLevel.Function, \"get\"", Case2)]
    [InlineData(
        ".Anonymous()",
        SecuredDefault,
        "\"get\", AuthLevel = AuthorizationLevel.Anonymous",
        null
    )]
    [InlineData("", "", "AuthorizationLevel.Anonymous, \"get\"", null)]
    public void Functions_trigger_level_against_the_contract(
        string security,
        string assembly,
        string trigger,
        string? expected
    )
    {
        var warnings = Check(Contract(security, assembly), FunctionsAttributes, Function(trigger));

        AssertVerdict(warnings, expected);
    }

    [Fact]
    public void Functions_key_level_with_no_security_declared_is_the_summary()
    {
        var warnings = Check(Contract(), FunctionsAttributes, Function("\"get\""));

        Assert.Contains("ItemsContract.Get (GET /api/items)", Summary(warnings).Message);
    }

    // ---------------------------------------------------------------
    // Top-level programs and the CLI
    // ---------------------------------------------------------------

    [Fact]
    public void Top_level_route_group_local_resolves_its_prefix()
    {
        var program = $$"""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Rivet;
            using Test;

            var app = WebApplication.CreateBuilder(args).Build();
            var api = app.MapGroup("/api").RequireAuthorization();
            api.MapGet("/items", {{Handler}});
            app.Run();
            """;

        var warnings = CheckWith(
            OutputKind.ConsoleApplication,
            program,
            Contract(assembly: SecuredDefault)
        );

        Assert.DoesNotContain(
            warnings,
            warning =>
                warning.FieldName == "Get" && warning.Kind == CoverageWarningKind.RouteMismatch
        );
        AssertVerdict(warnings, null);
    }

    [Fact]
    public async Task Cli_prints_both_ids_as_errors_and_exits_1()
    {
        const string source = """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            [assembly: RivetSecurityScheme("bearer", "http", scheme: "bearer")]
            [assembly: RivetGlobalSecurity(0)]
            [assembly: RivetGlobalSecurityScheme(0, "bearer", new string[0])]

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
                public static readonly RouteDefinition<ItemDto> Other = Define.Get<ItemDto>("/api/other");
            }

            [Route("api")]
            public sealed class ItemsController : ControllerBase
            {
                [HttpGet("items")]
                public IActionResult Get() => ItemsContract.Get.Success(new ItemDto("1")).ToActionResult();
            }

            public static class Setup
            {
                public static void Map(WebApplication app, bool flag)
                {
                    var endpoint = app.MapGet("/api/other", () => ItemsContract.Other.Success(new ItemDto("1")).ToResult());
                    if (flag) endpoint.RequireAuthorization();
                }
            }
            """;
        using var work = new TempDir();
        var sourcePath = Path.Combine(work.FullName, "Api.cs");
        await File.WriteAllTextAsync(sourcePath, source);

        var result = CliRunner.RunCli(work.FullName, [sourcePath, "--check"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("error RIV4008: ItemsContract.Get (GET /api/items):", result.StdErr);
        Assert.Contains("error RIV4009: ItemsContract.Other (GET /api/other):", result.StdErr);
        Assert.Contains("2 mismatch(es)", result.StdErr);
    }
}
