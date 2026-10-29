using System.Net;
using System.Runtime.Serialization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rivet.Tool.Analysis;

namespace Rivet.Tests;

/// <summary>
/// What a real minimal-API host binds for the members of a <c>[FromForm]</c> complex
/// parameter, against <see cref="ParameterBindingSources"/>' reading of the same handlers
/// (RIV4005 and RIV4006 read binding through it). The form mapper reads each member as a form
/// field under its C# name, or its <c>[DataMember(Name =)]</c>, and a nested member under
/// <c>Outer.Name</c>. It ignores a <c>Name =</c> on the parameter (no prefix) and every binding
/// attribute on a member: <c>[FromForm(Name =)]</c> renames nothing, and <c>[FromQuery]</c>
/// doesn't move a member to the query.
/// </summary>
public sealed class MinimalApiBindingHostTests
{
    [Fact]
    public async Task Form_mapper_binds_each_member_under_the_key_the_checker_reads()
    {
        await using var host = await BindingHost.StartAsync(mvc: false);

        foreach (
            var (path, handler) in new[]
            {
                ("/minimal/form", nameof(MinimalFormHandlers.Plain)),
                ("/minimal/form-named", nameof(MinimalFormHandlers.Named)),
            }
        )
        {
            var inputs = BindingHost.CheckerInputs(
                typeof(MinimalFormHandlers),
                handler,
                ParameterBindingHost.MinimalApi,
                infersBody: true,
                route: path
            );

            await host.AssertBindsUnder(
                HttpMethod.Post,
                path,
                inputs,
                ["Plain", "Aliased", "data_name", "Queried", "Inner", "Inner.Value", "Inner.deep"],
                sentFrom: InputSource.Form
            );
            Assert.All(inputs, input => Assert.Equal(InputSource.Form, input.Source));
        }
    }

    [Theory]
    // [FromForm(Name = "p")] on the parameter is no prefix.
    [InlineData("/minimal/form-named", "p.Plain", "Plain")]
    // A member's own [FromForm(Name =)] renames nothing: its C# name binds.
    [InlineData("/minimal/form", "alias", "Aliased")]
    // [DataMember(Name =)] replaces the C# name.
    [InlineData("/minimal/form", "Data", "Data")]
    // Nested members take a dot, not brackets.
    [InlineData("/minimal/form", "Inner[Value]", "Inner.Value")]
    public async Task Form_mapper_ignores_these_keys(string path, string key, string member)
    {
        await using var host = await BindingHost.StartAsync(mvc: false);

        Assert.Null(
            await host.BoundAsync(HttpMethod.Post, path, [(InputSource.Form, key)], member)
        );
    }

    [Fact]
    public async Task Form_mapper_reads_a_FromQuery_member_from_the_form_not_the_query()
    {
        await using var host = await BindingHost.StartAsync(mvc: false);

        Assert.Null(
            await host.BoundAsync(
                HttpMethod.Post,
                "/minimal/form",
                // A form field keeps the form non-empty, so the record itself binds.
                [
                    (InputSource.Query, "q"),
                    (InputSource.Query, "Queried"),
                    (InputSource.Form, "Plain"),
                ],
                "Queried"
            )
        );
        Assert.Equal(
            BindingHost.Value,
            await host.BoundAsync(
                HttpMethod.Post,
                "/minimal/form",
                [(InputSource.Form, "Queried")],
                "Queried"
            )
        );
    }
}

public sealed class MinimalForm
{
    public string? Plain { get; set; }

    [FromForm(Name = "alias")]
    public string? Aliased { get; set; }

    [DataMember(Name = "data_name")]
    public string? Data { get; set; }

    [FromQuery(Name = "q")]
    public string? Queried { get; set; }

    public MinimalFormInner? Inner { get; set; }
}

public sealed class MinimalFormInner
{
    public string? Value { get; set; }

    [DataMember(Name = "deep")]
    public string? Renamed { get; set; }
}

public static class MinimalFormHandlers
{
    public static string Plain([FromForm] MinimalForm f) => BindingHost.Echo(f);

    public static string Named([FromForm(Name = "p")] MinimalForm f) => BindingHost.Echo(f);
}

/// <summary>
/// An in-process Kestrel host for the binding tests' handlers (the MVC ones in
/// <see cref="MvcBindingHostTests"/>, the minimal-API ones above), plus the checker's reading
/// of the same compiled handlers, so a test holds the two to one answer. Each handler echoes
/// what it bound as JSON under the C# member names.
/// </summary>
internal sealed class BindingHost : IAsyncDisposable
{
    public const string Value = "bound";

    private static readonly Lazy<(Compilation Compilation, WellKnownTypes Wkt)> _compilation = new(
        () =>
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Append(typeof(BindingHost).Assembly.Location)
                .Distinct()
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("BindingHostProbe", [], references);
            return (compilation, new WellKnownTypes(compilation));
        }
    );

    /// <summary>The test assembly as the tool sees it, for reading the handlers' types.</summary>
    public static (Compilation Compilation, WellKnownTypes Wkt) Probe => _compilation.Value;

    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private BindingHost(WebApplication app, HttpClient client)
    {
        _app = app;
        _client = client;
    }

    public static string Echo(object? value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// Starts a host serving the test assembly's controllers (<paramref name="mvc"/>) or the
    /// minimal-API handlers above, plus whatever <paramref name="map"/> adds.
    /// </summary>
    public static async Task<BindingHost> StartAsync(bool mvc, Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (mvc)
        {
            builder.Services.AddControllers().AddApplicationPart(typeof(BindingHost).Assembly);
        }

        var app = builder.Build();
        if (mvc)
        {
            app.MapControllers();
        }
        else
        {
            app.MapPost("/minimal/form", MinimalFormHandlers.Plain).DisableAntiforgery();
            app.MapPost("/minimal/form-named", MinimalFormHandlers.Named).DisableAntiforgery();
        }
        map?.Invoke(app);

        await app.StartAsync();
        var address = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        return new BindingHost(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    /// <summary>
    /// <see cref="ParameterBindingSources.Inputs"/> for every parameter of
    /// <paramref name="type"/>'s method <paramref name="method"/>, read from this test
    /// assembly's metadata.
    /// </summary>
    public static IReadOnlyList<BoundInput> CheckerInputs(
        Type type,
        string method,
        ParameterBindingHost host,
        bool infersBody,
        string route
    )
    {
        var (compilation, wkt) = _compilation.Value;
        var symbol = compilation
            .GetTypeByMetadataName(type.FullName!)!
            .GetMembers(method)
            .OfType<IMethodSymbol>()
            .Single();
        return symbol
            .Parameters.SelectMany(parameter =>
                ParameterBindingSources.Inputs(
                    wkt,
                    parameter,
                    host,
                    infersBody,
                    RouteParser.ParseRouteParamNames(route)
                )
            )
            .ToList();
    }

    /// <summary>
    /// Pins <paramref name="wireNames"/>, one per checker input in order, as the keys the host
    /// binds: each string input is sent on its own under its pinned key, in the place the
    /// checker reads it from (or <paramref name="sentFrom"/>), and must reach the member the
    /// checker names (a route value rides in <paramref name="path"/> already, so it isn't
    /// sent). Then the checker's own wire names must be those keys.
    /// </summary>
    public async Task AssertBindsUnder(
        HttpMethod method,
        string path,
        IReadOnlyList<BoundInput> inputs,
        IReadOnlyList<string> wireNames,
        InputSource? sentFrom = null
    )
    {
        Assert.Equal(wireNames.Count, inputs.Count);
        foreach (
            var (input, wireName) in inputs
                .Zip(wireNames)
                .Where(pair =>
                    pair.First.Type.SpecialType == SpecialType.System_String
                    && pair.First.Source != InputSource.Route
                )
        )
        {
            var member = string.Join('.', input.Path.Split('.').Skip(1));
            var source =
                sentFrom
                ?? (input.Source == InputSource.ValueProviders ? InputSource.Query : input.Source);
            Assert.True(
                await BoundAsync(method, path, [(source, wireName)], member) == Value,
                $"{method} {path}: the host didn't bind {input.Path} from {source} '{wireName}'"
            );
        }

        Assert.Equal(wireNames, inputs.Select(input => input.WireName));
    }

    /// <summary>
    /// Sends <see cref="Value"/> under each key, in the query, a header or a form body (a POST
    /// always carries a form, possibly empty), and returns what the handler bound at
    /// <paramref name="member"/> (a dotted path below the parameter; empty for the parameter
    /// itself), or null.
    /// </summary>
    public async Task<string?> BoundAsync(
        HttpMethod method,
        string path,
        IReadOnlyList<(InputSource Source, string Key)> values,
        string member
    )
    {
        var query = string.Join(
            '&',
            values
                .Where(value => value.Source == InputSource.Query)
                .Select(value => $"{Uri.EscapeDataString(value.Key)}={Value}")
        );
        using var request = new HttpRequestMessage(
            method,
            query.Length == 0 ? path : $"{path}?{query}"
        );
        foreach (var (_, key) in values.Where(value => value.Source == InputSource.Header))
        {
            request.Headers.Add(key, Value);
        }
        if (method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent(
                values
                    .Where(value => value.Source == InputSource.Form)
                    .Select(value => new KeyValuePair<string, string>(value.Key, Value))
            );
        }

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var element = document.RootElement;
        foreach (var segment in member.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (
                element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(segment, out element)
            )
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    /// <summary>Sends <paramref name="content"/> as a POST and returns the status and body.</summary>
    public async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string path,
        HttpContent content
    )
    {
        using var response = await _client.PostAsync(path, content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
