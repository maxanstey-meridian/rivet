using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rivet.Tool.Analysis;

namespace Rivet.Tests;

/// <summary>
/// K.5's authorization rules held against a running ASP.NET host (decision 9). Each rule is
/// written twice: as the host registers it, served by <see cref="BindingHost"/> to a caller
/// with no credentials (401 when it requires authentication, 200 when it doesn't), and as an
/// app's Program.cs that <c>--check</c> reads against a secured contract. Both must give the
/// rule's answer. The host's authentication handler authenticates nobody.
/// </summary>
public sealed class AuthorizationHostTests
{
    // What each rule says ASP.NET does: whether a caller with no credentials is refused.
    private static readonly Dictionary<string, bool> _requires = new()
    {
        ["AllowAnonymous on an action beats its controller's Authorize"] = false,
        ["AllowAnonymous on an action beats a global AuthorizeFilter"] = false,
        ["AllowAnonymous on a handler beats its group's RequireAuthorization"] = false,
        ["AllowAnonymous on a handler beats a requiring fallback"] = false,
        ["A requiring fallback secures a bare endpoint"] = true,
        ["The fallback doesn't apply over a named policy that admits anyone"] = false,
        ["AddFallbackPolicy registers its policy under its name too"] = false,
        ["A policy name is looked up ignoring case"] = false,
        ["Combined policies require a user when any one does"] = true,
        ["A minimal API reads its handler method's attributes, not its class's"] = false,
    };

    // Each rule as the host registers it.
    private static readonly Dictionary<string, Rule> _hosts = new()
    {
        ["AllowAnonymous on an action beats its controller's Authorize"] = new(
            "/auth/controller",
            Mvc: true,
            Control: "/auth/controller-control"
        ),
        ["AllowAnonymous on an action beats a global AuthorizeFilter"] = new(
            "/auth/filtered",
            Mvc: true,
            Control: "/auth/filtered-control",
            Services: services =>
                services.Configure<MvcOptions>(options =>
                    options.Filters.Add(new AuthorizeFilter())
                )
        ),
        ["AllowAnonymous on a handler beats its group's RequireAuthorization"] = new(
            "/auth/group/items",
            Control: "/auth/group/control",
            Map: app =>
            {
                var group = app.MapGroup("/auth/group").RequireAuthorization();
                group.MapGet("/items", [AllowAnonymous] () => "ok");
                group.MapGet("/control", () => "ok");
            }
        ),
        ["AllowAnonymous on a handler beats a requiring fallback"] = new(
            "/auth/fallback-anonymous",
            Services: RequiringFallback,
            Map: app => app.MapGet("/auth/fallback-anonymous", [AllowAnonymous] () => "ok")
        ),
        ["A requiring fallback secures a bare endpoint"] = new(
            "/auth/fallback",
            Services: RequiringFallback,
            Map: app => app.MapGet("/auth/fallback", () => "ok")
        ),
        ["The fallback doesn't apply over a named policy that admits anyone"] = new(
            "/auth/open",
            Services: services =>
            {
                RequiringFallback(services);
                services.AddAuthorization(options =>
                    options.AddPolicy("open", policy => policy.RequireAssertion(_ => true))
                );
            },
            Map: app => app.MapGet("/auth/open", [Authorize(Policy = "open")] () => "ok")
        ),
        ["AddFallbackPolicy registers its policy under its name too"] = new(
            "/auth/named-fallback",
            Services: services =>
                services
                    .AddAuthorizationBuilder()
                    .AddFallbackPolicy("open", policy => policy.RequireAssertion(_ => true)),
            Map: app => app.MapGet("/auth/named-fallback", [Authorize(Policy = "open")] () => "ok")
        ),
        ["A policy name is looked up ignoring case"] = new(
            "/auth/open-case",
            Services: services =>
                services.AddAuthorization(options =>
                    options.AddPolicy("Open", policy => policy.RequireAssertion(_ => true))
                ),
            Map: app => app.MapGet("/auth/open-case", () => "ok").RequireAuthorization("open")
        ),
        ["Combined policies require a user when any one does"] = new(
            "/auth/combined",
            Services: services =>
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("open", policy => policy.RequireAssertion(_ => true));
                    options.AddPolicy("staff", policy => policy.RequireRole("Staff"));
                }),
            Map: app =>
                app.MapGet("/auth/combined", () => "ok").RequireAuthorization("open", "staff")
        ),
        ["A minimal API reads its handler method's attributes, not its class's"] = new(
            "/auth/method-group",
            Map: app => app.MapGet("/auth/method-group", AuthHostHandlers.Get)
        ),
    };

    // Each rule as --check reads it: an app's top-level statements, then its types.
    private static readonly Dictionary<string, string> _checker = new()
    {
        ["AllowAnonymous on an action beats its controller's Authorize"] = Program(
            "builder.Services.AddControllers();",
            "app.MapControllers();",
            Controller("[Authorize]", "[AllowAnonymous]")
        ),
        ["AllowAnonymous on an action beats a global AuthorizeFilter"] = Program(
            "builder.Services.AddControllers(options => options.Filters.Add(new AuthorizeFilter()));",
            "app.MapControllers();",
            Controller("", "[AllowAnonymous]")
        ),
        ["AllowAnonymous on a handler beats its group's RequireAuthorization"] = Program(
            "",
            $"app.MapGroup(\"/api\").RequireAuthorization().MapGet(\"/items\", [AllowAnonymous] {Handler});"
        ),
        ["AllowAnonymous on a handler beats a requiring fallback"] = Program(
            $"builder.Services.AddAuthorization(o => o.FallbackPolicy = {Requiring});",
            $"app.MapGet(\"/api/items\", [AllowAnonymous] {Handler});"
        ),
        ["A requiring fallback secures a bare endpoint"] = Program(
            $"builder.Services.AddAuthorization(o => o.FallbackPolicy = {Requiring});",
            $"app.MapGet(\"/api/items\", {Handler});"
        ),
        ["The fallback doesn't apply over a named policy that admits anyone"] = Program(
            $"builder.Services.AddAuthorization(o => {{ o.FallbackPolicy = {Requiring}; "
                + "o.AddPolicy(\"open\", p => p.RequireAssertion(_ => true)); });",
            $"app.MapGet(\"/api/items\", [Authorize(Policy = \"open\")] {Handler});"
        ),
        ["AddFallbackPolicy registers its policy under its name too"] = Program(
            "builder.Services.AddAuthorizationBuilder().AddFallbackPolicy(\"open\", p => p.RequireAssertion(_ => true));",
            $"app.MapGet(\"/api/items\", [Authorize(Policy = \"open\")] {Handler});"
        ),
        ["A policy name is looked up ignoring case"] = Program(
            "builder.Services.AddAuthorization(o => o.AddPolicy(\"Open\", p => p.RequireAssertion(_ => true)));",
            $"app.MapGet(\"/api/items\", {Handler}).RequireAuthorization(\"open\");"
        ),
        ["Combined policies require a user when any one does"] = Program(
            "builder.Services.AddAuthorization(o => { o.AddPolicy(\"open\", p => p.RequireAssertion(_ => true)); "
                + "o.AddPolicy(\"staff\", p => p.RequireRole(\"Staff\")); });",
            $"app.MapGet(\"/api/items\", {Handler}).RequireAuthorization(\"open\", \"staff\");"
        ),
        ["A minimal API reads its handler method's attributes, not its class's"] = Program(
            "",
            "app.MapGet(\"/api/items\", ItemsHandlers.Get);",
            """
            [Authorize]
            public static class ItemsHandlers
            {
                public static IResult Get() => ItemsContract.Get.Success(new ItemDto("1")).ToResult();
            }
            """
        ),
    };

    public static TheoryData<string> Rules => [.. _requires.Keys];

    [Fact]
    public void Every_rule_is_written_for_the_host_and_for_the_checker()
    {
        Assert.Equal(_requires.Keys.Order(), _hosts.Keys.Order());
        Assert.Equal(_requires.Keys.Order(), _checker.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public async Task Host_and_checker_agree(string rule)
    {
        var requires = _requires[rule];
        var host = _hosts[rule];
        await using var running = await BindingHost.StartAsync(
            host.Mvc,
            host.Map,
            services =>
            {
                services
                    .AddAuthentication(NobodyHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, NobodyHandler>(
                        NobodyHandler.Scheme,
                        null
                    );
                services.AddAuthorization();
                host.Services?.Invoke(services);
            }
        );

        var (status, _) = await running.GetAsync(host.Path);
        Assert.Equal(requires ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, status);

        // The same registration without the rule's opt-out: the requirement it beats is in force.
        if (host.Control is { } control)
        {
            var (controlStatus, _) = await running.GetAsync(control);
            Assert.Equal(HttpStatusCode.Unauthorized, controlStatus);
        }

        var warnings = CompilationHelper.CheckCoverage(
            CompilationHelper.CreateFrameworkCompilation(
                OutputKind.ConsoleApplication,
                _checker[rule],
                Contract
            )
        );
        Assert.Equal(requires, CheckerRequires(warnings));
    }

    // ---------------------------------------------------------------
    // The checker's half
    // ---------------------------------------------------------------

    private const string Handler = "() => ItemsContract.Get.Success(new ItemDto(\"1\")).ToResult()";

    private const string Requiring =
        "new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()";

    // Every endpoint is secured by default, so a handler anyone can call is RIV4008.
    private const string Contract = """
        using Rivet;

        [assembly: RivetSecurityScheme("bearer", "http", scheme: "bearer")]
        [assembly: RivetGlobalSecurity(0)]
        [assembly: RivetGlobalSecurityScheme(0, "bearer", new string[0])]

        [RivetType]
        public sealed record ItemDto(string Id);

        [RivetContract]
        public static class ItemsContract
        {
            public static readonly RouteDefinition<ItemDto> Get = Define.Get<ItemDto>("/api/items");
        }
        """;

    private static string Program(string services, string map, string types = "") =>
        $$"""
            using Microsoft.AspNetCore.Authorization;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Mvc.Authorization;
            using Microsoft.Extensions.DependencyInjection;
            using Rivet;

            var builder = WebApplication.CreateBuilder(args);
            {{services}}
            var app = builder.Build();
            {{map}}
            app.Run();

            {{types}}
            """;

    private static string Controller(string controllerAttributes, string actionAttributes) =>
        $$"""
            {{controllerAttributes}}
            [Route("api")]
            public class ItemsController : ControllerBase
            {
                {{actionAttributes}}
                [HttpGet("items")]
                public IActionResult Get() => ItemsContract.Get.Success(new ItemDto("1")).ToActionResult();
            }
            """;

    // True when the handler requires authentication (the secured contract agrees), false when
    // anyone can call it (RIV4008), null when --check can't tell (RIV4009). Any other warning
    // means the checker didn't read the handler at all.
    private static bool? CheckerRequires(IReadOnlyList<CoverageWarning> warnings)
    {
        if (warnings.Count == 0)
        {
            return true;
        }

        var warning = Assert.Single(warnings);
        return warning switch
        {
            { Kind: CoverageWarningKind.SecurityMismatch, Message: { } message }
                when message.Contains("anyone can call the handler", StringComparison.Ordinal) =>
                false,
            { Kind: CoverageWarningKind.AuthorizationUnreadable } => null,
            _ => throw new Xunit.Sdk.XunitException($"Unexpected warning: {warning.Message}"),
        };
    }

    /// <summary>
    /// A rule as the host registers it, served at <paramref name="Path"/>. A rule that says one
    /// thing beats another has a <paramref name="Control"/>: a path the same registration
    /// serves without it, which an anonymous caller is refused.
    /// </summary>
    private sealed record Rule(
        string Path,
        bool Mvc = false,
        string? Control = null,
        Action<IServiceCollection>? Services = null,
        Action<WebApplication>? Map = null
    );

    private static void RequiringFallback(IServiceCollection services) =>
        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build()
        );
}

/// <summary>Authenticates nobody, so every request is an anonymous caller's.</summary>
internal sealed class NobodyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "Nobody";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}

[Authorize]
[Route("auth")]
public sealed class AuthHostController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("controller")]
    public string Get() => "ok";

    [HttpGet("controller-control")]
    public string Control() => "ok";
}

[Route("auth")]
public sealed class AuthHostFilteredController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("filtered")]
    public string Get() => "ok";

    [HttpGet("filtered-control")]
    public string Control() => "ok";
}

[Authorize]
public static class AuthHostHandlers
{
    public static string Get() => "ok";
}
