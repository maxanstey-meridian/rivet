using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Rivet.RuntimeTests.StringEnumConverterTests;

namespace Rivet.RuntimeTests;

/// <summary>
/// Pins of what ASP.NET itself does with an enum in the route or the query string. Rivet's
/// JSON converters play no part here: minimal APIs call <c>Enum.TryParse</c> (case-sensitive,
/// numbers accepted) and MVC uses <c>EnumConverter</c> (case-insensitive) plus
/// <c>EnumTypeModelBinder</c> (refuses numbers that aren't members). So a converter's wire
/// name binds only where it equals the member name: exactly on minimal APIs, ignoring case
/// on MVC. RIV4005 and RIV1110 refuse a spec that advertises names the host can't bind, and
/// these tests keep the rule they encode true. If a framework release changes the binders,
/// a pin fails here before a spec starts lying.
///
/// Integers and the other leniencies are pinned too, as ASP.NET's behaviour rather than
/// Rivet's promise: the spec never advertises them, and Rivet doesn't stop them binding.
/// </summary>
public sealed class EnumParameterBindingTests(EnumParameterBindingTests.Hosts hosts)
    : IClassFixture<EnumParameterBindingTests.Hosts>
{
    private const string Member = "RestrictedByway2A";

    private static readonly string[] _locations = ["query", "route"];

    // Each converter's wire name for RestrictedByway2A, and whether that host binds it.
    private static readonly (
        string Converter,
        string WireName,
        bool Minimal,
        bool Mvc
    )[] _wireNames =
    [
        ("camel", "restrictedByway2A", false, true),
        ("lower", "restrictedbyway2a", false, true),
        ("snake", "restricted_byway2_a", false, false),
        ("kebab", "restricted-byway2-a", false, false),
    ];

    public static TheoryData<string, string, string> ConvertersByLocation()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var style in Hosts.Styles)
        {
            foreach (var (converter, _, _, _) in _wireNames)
            {
                foreach (var location in _locations)
                {
                    data.Add(style, converter, location);
                }
            }
        }

        return data;
    }

    public static TheoryData<string, string, string, string, bool> WireNamesByHost()
    {
        var data = new TheoryData<string, string, string, string, bool>();
        foreach (var (converter, wireName, minimal, mvc) in _wireNames)
        {
            foreach (var location in _locations)
            {
                data.Add("minimal", converter, location, wireName, minimal);
                data.Add("mvc", converter, location, wireName, mvc);
            }
        }

        return data;
    }

    public static TheoryData<string, string> StylesByLocation()
    {
        var data = new TheoryData<string, string>();
        foreach (var style in Hosts.Styles)
        {
            foreach (var location in _locations)
            {
                data.Add(style, location);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ConvertersByLocation))]
    public async Task Exact_member_name_binds_on_both_hosts(
        string style,
        string converter,
        string location
    )
    {
        await AssertBinds(style, Path(converter, location, Member), Member);
    }

    [Theory]
    [MemberData(nameof(WireNamesByHost))]
    public async Task Wire_name_binds_only_where_it_matches_the_member_name(
        string style,
        string converter,
        string location,
        string wireName,
        bool binds
    )
    {
        var path = Path(converter, location, wireName);
        if (binds)
        {
            await AssertBinds(style, path, Member);
        }
        else
        {
            await AssertRefused(style, path);
        }
    }

    [Theory]
    [MemberData(nameof(Hosts.StyleData), MemberType = typeof(Hosts))]
    public async Task Zero_binds_from_the_query_on_both_hosts(string style)
    {
        await AssertBinds(style, "/camel/query?kind=0", "Unknown");
    }

    [Theory]
    [MemberData(nameof(Hosts.StyleData), MemberType = typeof(Hosts))]
    public async Task Zero_binds_from_the_route_on_both_hosts(string style)
    {
        await AssertBinds(style, "/camel/route/0", "Unknown");
    }

    [Theory]
    [MemberData(nameof(StylesByLocation))]
    public async Task Undefined_number_binds_on_minimal_apis_and_is_refused_by_mvc(
        string style,
        string location
    )
    {
        var path = Path("camel", location, "99");
        if (style == "minimal")
        {
            await AssertBinds(style, path, "99");
        }
        else
        {
            await AssertRefused(style, path);
        }
    }

    [Theory]
    [MemberData(nameof(Hosts.StyleData), MemberType = typeof(Hosts))]
    public async Task Comma_list_is_refused_by_minimal_apis_and_ored_together_by_mvc(string style)
    {
        // 1 | 2 is 3, RestrictedByway2A, though the enum isn't [Flags].
        var path = "/camel/query?kind=1,2";
        if (style == "minimal")
        {
            await AssertRefused(style, path);
        }
        else
        {
            await AssertBinds(style, path, Member);
        }
    }

    private static string Path(string converter, string location, string value) =>
        location == "query"
            ? $"/{converter}/query?kind={Uri.EscapeDataString(value)}"
            : $"/{converter}/route/{Uri.EscapeDataString(value)}";

    private async Task AssertBinds(string style, string path, string expected)
    {
        var response = await hosts.GetAsync(style, path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK && body == expected,
            $"{style} {path}: expected 200 '{expected}', got {(int)response.StatusCode} '{body}'"
        );
    }

    private async Task AssertRefused(string style, string path)
    {
        var response = await hosts.GetAsync(style, path);

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{style} {path}: expected 400, got {(int)response.StatusCode} '{await response.Content.ReadAsStringAsync()}'"
        );
    }

    /// <summary>A minimal-API host and an MVC host, each on its own loopback port.</summary>
    public sealed class Hosts : IAsyncLifetime
    {
        public static readonly string[] Styles = ["minimal", "mvc"];

        public static TheoryData<string> StyleData() => [.. Styles];

        private readonly Dictionary<string, (WebApplication App, HttpClient Client)> _hosts = [];

        public async Task InitializeAsync()
        {
            foreach (var style in Styles)
            {
                var app = await StartAsync(mvc: style == "mvc");
                var address = app
                    .Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single();
                _hosts[style] = (app, new HttpClient { BaseAddress = new Uri(address) });
            }
        }

        public Task<HttpResponseMessage> GetAsync(string style, string path) =>
            _hosts[style].Client.GetAsync(path);

        public async Task DisposeAsync()
        {
            foreach (var (app, client) in _hosts.Values)
            {
                client.Dispose();
                await app.DisposeAsync();
            }
        }

        private static async Task<WebApplication> StartAsync(bool mvc)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            if (mvc)
            {
                builder
                    .Services.AddControllers()
                    .AddApplicationPart(typeof(EnumBindingController).Assembly);
            }

            var app = builder.Build();
            if (mvc)
            {
                app.MapControllers();
            }
            else
            {
                Map<Colour>(app, "camel");
                Map<ColourSnake>(app, "snake");
                Map<ColourKebab>(app, "kebab");
                Map<ColourLower>(app, "lower");
            }

            await app.StartAsync();
            return app;
        }

        private static void Map<T>(IEndpointRouteBuilder app, string converter)
            where T : struct, Enum
        {
            app.MapGet($"/{converter}/query", (T kind) => kind.ToString());
            app.MapGet($"/{converter}/route/{{kind}}", (T kind) => kind.ToString());
        }
    }
}

[ApiController]
public sealed class EnumBindingController : ControllerBase
{
    [HttpGet("/camel/query")]
    public string CamelQuery([FromQuery] Colour kind) => kind.ToString();

    [HttpGet("/camel/route/{kind}")]
    public string CamelRoute(Colour kind) => kind.ToString();

    [HttpGet("/snake/query")]
    public string SnakeQuery([FromQuery] ColourSnake kind) => kind.ToString();

    [HttpGet("/snake/route/{kind}")]
    public string SnakeRoute(ColourSnake kind) => kind.ToString();

    [HttpGet("/kebab/query")]
    public string KebabQuery([FromQuery] ColourKebab kind) => kind.ToString();

    [HttpGet("/kebab/route/{kind}")]
    public string KebabRoute(ColourKebab kind) => kind.ToString();

    [HttpGet("/lower/query")]
    public string LowerQuery([FromQuery] ColourLower kind) => kind.ToString();

    [HttpGet("/lower/route/{kind}")]
    public string LowerRoute(ColourLower kind) => kind.ToString();
}
