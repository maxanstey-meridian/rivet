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
/// Pins of what ASP.NET itself does with an enum in the route, the query string, a header or
/// a form field. Rivet's JSON converters play no part here: minimal APIs call
/// <c>Enum.TryParse</c> (case-sensitive, numbers accepted) and MVC uses <c>EnumConverter</c>
/// (case-insensitive) plus <c>EnumTypeModelBinder</c> (refuses numbers that aren't members).
/// So a converter's wire name binds only where it equals the member name: exactly on minimal
/// APIs, ignoring case on MVC. RIV4005 and RIV1110 refuse a spec that advertises names the
/// host can't bind, and these tests keep the rule they encode true. If a framework release
/// changes the binders, a pin fails here before a spec starts lying.
///
/// Integers and the other leniencies are pinned too, as ASP.NET's behaviour rather than
/// Rivet's promise: the spec never advertises them, and Rivet doesn't stop them binding.
/// </summary>
public sealed class EnumParameterBindingTests(EnumParameterBindingTests.Hosts hosts)
    : IClassFixture<EnumParameterBindingTests.Hosts>
{
    private const string Member = "RestrictedByway2A";

    private static readonly string[] _locations = ["query", "route", "header", "form"];

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

    // Numbers that name a member, in the spellings the number parsers accept.
    private static readonly (string Value, string Member)[] _memberNumbers =
    [
        ("0", "Unknown"),
        ("+1", "DarkGreen"),
        (" 1", "DarkGreen"),
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

    public static TheoryData<string, string, string, string> MemberNumbersByLocation()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var style in Hosts.Styles)
        {
            foreach (var location in _locations)
            {
                foreach (var (value, member) in _memberNumbers)
                {
                    // A header can't carry a leading space: RFC 9110 strips the whitespace
                    // around a field value and Kestrel does, so the host would see "1".
                    if (location == "header" && value.StartsWith(' '))
                    {
                        continue;
                    }

                    data.Add(style, location, value, member);
                }
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
        await AssertBinds(style, converter, location, Member, Member);
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
        if (binds)
        {
            await AssertBinds(style, converter, location, wireName, Member);
        }
        else
        {
            await AssertRefused(style, converter, location, wireName);
        }
    }

    [Theory]
    [MemberData(nameof(MemberNumbersByLocation))]
    public async Task Member_number_binds_on_both_hosts(
        string style,
        string location,
        string value,
        string member
    )
    {
        await AssertBinds(style, "camel", location, value, member);
    }

    [Theory]
    [MemberData(nameof(StylesByLocation))]
    public async Task Undefined_number_binds_on_minimal_apis_and_is_refused_by_mvc(
        string style,
        string location
    )
    {
        if (style == "minimal")
        {
            await AssertBinds(style, "camel", location, "99", "99");
        }
        else
        {
            await AssertRefused(style, "camel", location, "99");
        }
    }

    [Theory]
    [MemberData(nameof(StylesByLocation))]
    public async Task Comma_list_is_refused_by_minimal_apis_and_ored_together_by_mvc_when_the_result_is_a_member(
        string style,
        string location
    )
    {
        // 1 | 2 is 3, RestrictedByway2A, though the enum isn't [Flags].
        if (style == "minimal")
        {
            await AssertRefused(style, "camel", location, "1,2");
        }
        else
        {
            await AssertBinds(style, "camel", location, "1,2", Member);
        }
    }

    [Theory]
    [MemberData(nameof(StylesByLocation))]
    public async Task Comma_list_whose_result_is_not_a_member_is_refused_by_both_hosts(
        string style,
        string location
    )
    {
        // 1 | 2 is 3, which Bits doesn't define, and Bits isn't [Flags]: MVC's binder checks
        // the combined value with Enum.IsDefined.
        await AssertRefused(style, "bits", location, "1,2");
    }

    [Theory]
    [MemberData(nameof(StylesByLocation))]
    public async Task Comma_list_on_a_flags_enum_is_refused_by_minimal_apis_and_ored_together_by_mvc(
        string style,
        string location
    )
    {
        // On a [Flags] enum MVC takes any combination of defined bits.
        if (style == "minimal")
        {
            await AssertRefused(style, "flags", location, "1,2");
        }
        else
        {
            await AssertBinds(style, "flags", location, "1,2", "One, Two");
        }
    }

    private static HttpRequestMessage Request(string converter, string location, string value)
    {
        switch (location)
        {
            case "query":
                return new(
                    HttpMethod.Get,
                    $"/{converter}/query?kind={Uri.EscapeDataString(value)}"
                );
            case "route":
                return new(HttpMethod.Get, $"/{converter}/route/{Uri.EscapeDataString(value)}");
            case "header":
                var request = new HttpRequestMessage(HttpMethod.Get, $"/{converter}/header");
                Assert.True(request.Headers.TryAddWithoutValidation("kind", value));
                return request;
            case "form":
                return new(HttpMethod.Post, $"/{converter}/form")
                {
                    Content = new FormUrlEncodedContent([new("kind", value)]),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(location), location, null);
        }
    }

    private async Task AssertBinds(
        string style,
        string converter,
        string location,
        string value,
        string expected
    )
    {
        var response = await hosts.SendAsync(style, Request(converter, location, value));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK && body == expected,
            $"{style} {converter} {location} '{value}': expected 200 '{expected}', got {(int)response.StatusCode} '{body}'"
        );
    }

    private async Task AssertRefused(string style, string converter, string location, string value)
    {
        var response = await hosts.SendAsync(style, Request(converter, location, value));

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"{style} {converter} {location} '{value}': expected 400, got {(int)response.StatusCode} '{await response.Content.ReadAsStringAsync()}'"
        );
    }

    /// <summary>Not [Flags], and 3 isn't a member, so 1 | 2 lands outside it.</summary>
    public enum Bits
    {
        None = 0,
        One = 1,
        Two = 2,
        Four = 4,
    }

    [Flags]
    public enum FlagBits
    {
        None = 0,
        One = 1,
        Two = 2,
        Four = 4,
    }

    /// <summary>A minimal-API host and an MVC host, each on its own loopback port.</summary>
    public sealed class Hosts : IAsyncLifetime
    {
        public static readonly string[] Styles = ["minimal", "mvc"];

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

        public async Task<HttpResponseMessage> SendAsync(string style, HttpRequestMessage request)
        {
            using (request)
            {
                return await _hosts[style].Client.SendAsync(request);
            }
        }

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
                Map<Bits>(app, "bits");
                Map<FlagBits>(app, "flags");
            }

            await app.StartAsync();
            return app;
        }

        private static void Map<T>(IEndpointRouteBuilder app, string converter)
            where T : struct, Enum
        {
            app.MapGet($"/{converter}/query", (T kind) => kind.ToString());
            app.MapGet($"/{converter}/route/{{kind}}", (T kind) => kind.ToString());
            app.MapGet($"/{converter}/header", ([FromHeader] T kind) => kind.ToString());
            app.MapPost($"/{converter}/form", ([FromForm] T kind) => kind.ToString())
                .DisableAntiforgery();
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

    [HttpGet("/camel/header")]
    public string CamelHeader([FromHeader] Colour kind) => kind.ToString();

    [HttpPost("/camel/form")]
    public string CamelForm([FromForm] Colour kind) => kind.ToString();

    [HttpGet("/snake/query")]
    public string SnakeQuery([FromQuery] ColourSnake kind) => kind.ToString();

    [HttpGet("/snake/route/{kind}")]
    public string SnakeRoute(ColourSnake kind) => kind.ToString();

    [HttpGet("/snake/header")]
    public string SnakeHeader([FromHeader] ColourSnake kind) => kind.ToString();

    [HttpPost("/snake/form")]
    public string SnakeForm([FromForm] ColourSnake kind) => kind.ToString();

    [HttpGet("/kebab/query")]
    public string KebabQuery([FromQuery] ColourKebab kind) => kind.ToString();

    [HttpGet("/kebab/route/{kind}")]
    public string KebabRoute(ColourKebab kind) => kind.ToString();

    [HttpGet("/kebab/header")]
    public string KebabHeader([FromHeader] ColourKebab kind) => kind.ToString();

    [HttpPost("/kebab/form")]
    public string KebabForm([FromForm] ColourKebab kind) => kind.ToString();

    [HttpGet("/lower/query")]
    public string LowerQuery([FromQuery] ColourLower kind) => kind.ToString();

    [HttpGet("/lower/route/{kind}")]
    public string LowerRoute(ColourLower kind) => kind.ToString();

    [HttpGet("/lower/header")]
    public string LowerHeader([FromHeader] ColourLower kind) => kind.ToString();

    [HttpPost("/lower/form")]
    public string LowerForm([FromForm] ColourLower kind) => kind.ToString();

    [HttpGet("/bits/query")]
    public string BitsQuery([FromQuery] EnumParameterBindingTests.Bits kind) => kind.ToString();

    [HttpGet("/bits/route/{kind}")]
    public string BitsRoute(EnumParameterBindingTests.Bits kind) => kind.ToString();

    [HttpGet("/bits/header")]
    public string BitsHeader([FromHeader] EnumParameterBindingTests.Bits kind) => kind.ToString();

    [HttpPost("/bits/form")]
    public string BitsForm([FromForm] EnumParameterBindingTests.Bits kind) => kind.ToString();

    [HttpGet("/flags/query")]
    public string FlagsQuery([FromQuery] EnumParameterBindingTests.FlagBits kind) =>
        kind.ToString();

    [HttpGet("/flags/route/{kind}")]
    public string FlagsRoute(EnumParameterBindingTests.FlagBits kind) => kind.ToString();

    [HttpGet("/flags/header")]
    public string FlagsHeader([FromHeader] EnumParameterBindingTests.FlagBits kind) =>
        kind.ToString();

    [HttpPost("/flags/form")]
    public string FlagsForm([FromForm] EnumParameterBindingTests.FlagBits kind) => kind.ToString();
}
