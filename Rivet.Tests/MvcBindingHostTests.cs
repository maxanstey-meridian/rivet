using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rivet.Tool;
using Rivet.Tool.Analysis;

namespace Rivet.Tests;

/// <summary>
/// Real MVC binding evidence for the binding-and-properties slice: the AnnotationApi
/// sample host boots and binds a defaulted scalar query parameter ([FromQuery(Name=)]
/// wire name) plus an unattributed complex request body — proving the extraction
/// defaults describe what the real host actually binds.
/// In the Repository builds collection: it compiles the AnnotationApi sample in-process
/// via dotnet run, so it must not race the other build/run/publish classes over shared
/// obj assets.
/// </summary>
[Trait("Category", "Local")]
[Collection("Repository builds")]
public sealed class MvcBindingHostTests : IDisposable
{
    private static readonly string _repoRoot = CliRunner.RepoRoot;
    private static readonly string _sampleDir = Path.Combine(_repoRoot, "samples", "AnnotationApi");
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Host_Binds_Defaulted_Query_And_Complex_Body_As_Emitted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await CliRunner.StartServerAsync(
            Path.Combine(_sampleDir, "AnnotationApi.csproj"),
            cts.Token
        );

        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };

        // The defaulted scalar query param + inferred complex body — bound by MVC
        // exactly as the emitted contract describes (q + limit on the query string,
        // the JSON DTO as the body).
        var response = await http.PostAsync(
            "/api/tasks/search?q=alpha&limit=7",
            JsonBody(term: "alpha"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using (var doc = JsonDocument.Parse(body))
        {
            // The handler echoes the bound surface: item title "term=alpha" proves the
            // [FromQuery(Name="q")] wire name (the value arrived under q), pageSize 7
            // proves the explicit limit reached the handler, and totalCount 5 proves
            // the body's Term "alpha" was bound (its length, the TotalCount position).
            Assert.Equal(
                "term=alpha",
                doc.RootElement.GetProperty("items")[0].GetProperty("title").GetString()
            );
            Assert.Equal(7, doc.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(5, doc.RootElement.GetProperty("totalCount").GetInt32());
        }

        // The defaulted scalar proves the default end-to-end: omitting limit applies
        // the C# default 20 (still 200 — the parameter is optional), and the response
        // proves the actually bound value.
        var defaultLimit = await http.PostAsync(
            "/api/tasks/search?q=alpha",
            JsonBody(term: "alpha"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, defaultLimit.StatusCode);
        using (
            var doc = JsonDocument.Parse(await defaultLimit.Content.ReadAsStringAsync(cts.Token))
        )
        {
            Assert.Equal(20, doc.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(5, doc.RootElement.GetProperty("totalCount").GetInt32());
        }

        // The complex body is bound with value fidelity: the echoed totalCount is the
        // length of the received Term — "iota" (4) differs from the earlier "alpha"
        // (5), so the test proves the actually received body value, not a constant.
        var bodyEcho = await http.PostAsync(
            "/api/tasks/search?q=alpha&limit=7",
            JsonBody(term: "iota"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, bodyEcho.StatusCode);
        using (var doc = JsonDocument.Parse(await bodyEcho.Content.ReadAsStringAsync(cts.Token)))
        {
            Assert.Equal(7, doc.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(4, doc.RootElement.GetProperty("totalCount").GetInt32());
        }

        // A request violating the body's declared requiredness (an empty
        // application/json body binds null into the non-nullable request) produces
        // the automatic host 400 — the emitted contract's required requestBody
        // agrees with what MVC enforces.
        var emptyBody = await http.PostAsync(
            "/api/tasks/search?q=alpha&limit=7",
            new StringContent("", Encoding.UTF8, "application/json"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.BadRequest, emptyBody.StatusCode);

        // The real host enforces MVC requiredness on the non-nullable string bound
        // via [FromQuery(Name="q")]: omitting q yields the automatic 400 — the
        // emitted contract describes the required query param with the same wire
        // name (acceptance:mvc-inputs: requiredness agrees with enforced binding).
        var renamedQuery = await http.PostAsync(
            "/api/tasks/search?limit=3",
            JsonBody(term: "beta"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.BadRequest, renamedQuery.StatusCode);
    }

    [Fact]
    public async Task Host_Binds_FromRoute_Named_Param_Against_Its_Placeholder()
    {
        // [FromRoute(Name = "taskId")] must agree with the {taskId:guid} route
        // placeholder: MVC rejects a name mismatch when the route is built, so a
        // 200 PUT against the real placeholder proves the binding — the same wire
        // name the emitted-contract test asserts on the path parameter.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await CliRunner.StartServerAsync(
            Path.Combine(_sampleDir, "AnnotationApi.csproj"),
            cts.Token
        );

        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };

        var response = await http.PutAsync(
            $"/api/tasks/{Guid.NewGuid()}/status",
            new StringContent("""{"status":1}""", Encoding.UTF8, "application/json"),
            cts.Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Builds the SearchTasksRequest JSON body with the given term.</summary>
    private static StringContent JsonBody(string term) =>
        new(
            $$"""{"term":"{{term}}","minPriority":1,"tag":null}""",
            Encoding.UTF8,
            "application/json"
        );

    [Fact]
    public async Task Emitted_Contract_Describes_The_Bound_Surface()
    {
        // Extraction over the real sample source must emit the search endpoint with
        // exactly the surface the live host binds (proved by the host tests above):
        // the FromQuery(Name="q") wire name, the required q, the defaulted optional
        // limit with its 20, and the required SearchTasksRequest body whose
        // properties (term, minPriority, tag) match what MVC binds by value.
        var outputDir = Path.Combine(_temp.FullName, "spec");
        var (exitCode, output) = await CliRunner.RunAsync(
            "dotnet",
            $"run --project \"{Path.Combine(_repoRoot, "Rivet.Tool")}\" -- "
                + $"--project \"{Path.Combine(_sampleDir, "AnnotationApi.csproj")}\" "
                + $"--output \"{outputDir}\"",
            ct: default
        );
        Assert.True(exitCode == 0, $"rivet emission failed:\n{output}");

        var specPath = Path.Combine(outputDir, "openapi.json");
        Assert.True(File.Exists(specPath), $"emitted spec missing: {specPath}");
        using var document = JsonDocument.Parse(File.ReadAllText(specPath));
        var root = document.RootElement;
        var search = root.GetProperty("paths").GetProperty("/api/tasks/search").GetProperty("post");

        // Parameters parsed from the emitted document, compared with the host-bound
        // surface the live test asserted.
        var emittedParams =
            new Dictionary<string, (string Location, bool Required, JsonElement Schema)>();
        foreach (var parameter in search.GetProperty("parameters").EnumerateArray())
        {
            emittedParams[parameter.GetProperty("name").GetString()!] = (
                parameter.GetProperty("in").GetString()!,
                parameter.GetProperty("required").GetBoolean(),
                parameter.GetProperty("schema")
            );
        }

        // q: the FromQuery(Name=) wire name, required — the host proves the missing
        // q request is a 400, so the contract must not declare it optional.
        Assert.Equal("query", emittedParams["q"].Location);
        Assert.True(emittedParams["q"].Required);
        // limit: the C# default makes it optional and the default is emitted —
        // the host proves omitting it binds 20 and returns 200.
        Assert.Equal("query", emittedParams["limit"].Location);
        Assert.False(emittedParams["limit"].Required);
        Assert.Equal(20, emittedParams["limit"].Schema.GetProperty("default").GetInt32());

        // Body: required (the host proves a bodyless request is a 400) and its
        // schema resolves to the SearchTasksRequest component whose properties are
        // the surface the host binds by value.
        var requestBody = search.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        var bodySchema = requestBody
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        var (bodyComponent, bodyComponentName) = ResolveSchemaComponent(bodySchema, root);
        Assert.Equal("SearchTasksRequest", bodyComponentName);
        var bodyProperties = bodyComponent.GetProperty("properties");
        Assert.True(bodyProperties.TryGetProperty("term", out _));
        Assert.True(bodyProperties.TryGetProperty("minPriority", out _));
        Assert.True(bodyProperties.TryGetProperty("tag", out _));
        var requiredFields = bodyComponent
            .GetProperty("required")
            .EnumerateArray()
            .Select(entry => entry.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("term", requiredFields);
        Assert.Contains("minPriority", requiredFields);
        // Nullability alone no longer makes a member optional; [RivetOptional] does.
        Assert.DoesNotContain("tag", requiredFields);

        // FromRoute(Name="taskId"): the emitted path parameter carries the wire name
        // that matches the live route placeholder the host test exercises.
        var status = root.GetProperty("paths")
            .GetProperty("/api/tasks/{taskId}/status")
            .GetProperty("put");
        var pathParams = status
            .GetProperty("parameters")
            .EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "path")
            .ToList();
        var pathParam = Assert.Single(pathParams);
        Assert.Equal("taskId", pathParam.GetProperty("name").GetString());
        Assert.True(pathParam.GetProperty("required").GetBoolean());
    }

    public static TheoryData<string, string, bool, string[]> NamedMembers() =>
        new()
        {
            // Without a Name =, MVC falls back to the empty prefix, the key clients send.
            {
                "/prefix/mvc/query",
                nameof(NamePrefixController.Query),
                true,
                ["Plain", "alias", "bp", "X-Alias", "Inner", "Inner.Value", "Inner.deep"]
            },
            // A Name = on the parameter prefixes every member, a renamed one included; a
            // header member is read by its own name, never prefixed.
            {
                "/prefix/mvc/query-named",
                nameof(NamePrefixController.QueryNamed),
                true,
                [
                    "p.Plain",
                    "p.alias",
                    "p.bp",
                    "X-Alias",
                    "p.Inner",
                    "p.Inner.Value",
                    "p.Inner.deep",
                ]
            },
            {
                "/prefix/mvc/form",
                nameof(NamePrefixController.Form),
                true,
                ["Plain", "alias", "Inner", "Inner.Value", "Inner.deep"]
            },
            {
                "/prefix/mvc/form-named",
                nameof(NamePrefixController.FormNamed),
                true,
                ["p.Plain", "p.alias", "p.Inner", "p.Inner.Value", "p.Inner.deep"]
            },
            // A member with a source of its own keeps the parameter's prefix: ?p.alias=.
            {
                "/prefix/mvc/mixed",
                nameof(NamePrefixController.Mixed),
                true,
                ["p.Plain", "p.alias", "X-Alias"]
            },
            // [Bind(Prefix =)] names the model just as Name = does, on a parameter or its type.
            {
                "/prefix/mvc/bind",
                nameof(NamePrefixController.Bind),
                true,
                [
                    "p.Plain",
                    "p.alias",
                    "p.bp",
                    "X-Alias",
                    "p.Inner",
                    "p.Inner.Value",
                    "p.Inner.deep",
                ]
            },
            { "/prefix/mvc/bind-type", nameof(NamePrefixController.BindType), true, ["c.Plain"] },
            // A base class's [Bind(Prefix =)] is inherited.
            { "/prefix/mvc/bind-base", nameof(NamePrefixController.BindBase), true, ["b.Plain"] },
            { "/prefix/mvc/bind-simple", nameof(NamePrefixController.BindSimple), true, ["q"] },
            {
                "/prefix/plain/bind",
                nameof(PlainNamePrefixController.Bind),
                false,
                [
                    "p.Plain",
                    "p.alias",
                    "p.bp",
                    "X-Alias",
                    "p.Inner",
                    "p.Inner.Value",
                    "p.Inner.deep",
                ]
            },
            {
                "/prefix/plain/bind-simple",
                nameof(PlainNamePrefixController.BindSimple),
                false,
                ["q"]
            },
        };

    [Theory]
    [MemberData(nameof(NamedMembers))]
    public async Task Host_Binds_Each_Member_Under_The_Key_The_Checker_Reads(
        string path,
        string action,
        bool apiController,
        string[] wireNames
    )
    {
        // An in-process host over this file's controllers: each pinned key is sent on its own
        // and must reach its member, and the checker must read the same keys.
        await using var host = await BindingHost.StartAsync(mvc: true);
        var inputs = BindingHost.CheckerInputs(
            apiController ? typeof(NamePrefixController) : typeof(PlainNamePrefixController),
            action,
            ParameterBindingHost.Mvc,
            infersBody: apiController,
            route: path
        );

        await host.AssertBindsUnder(
            path.Contains("form") || path.Contains("mixed") ? HttpMethod.Post : HttpMethod.Get,
            path,
            inputs,
            wireNames
        );
    }

    [Theory]
    [InlineData("/prefix/mvc/query-named", "Plain", "Plain")]
    [InlineData("/prefix/mvc/query-named", "alias", "Renamed")]
    [InlineData("/prefix/mvc/bind", "alias", "Renamed")]
    [InlineData("/prefix/mvc/bind-type", "Plain", "Plain")]
    public async Task Host_Ignores_A_Member_Without_The_Parameters_Prefix(
        string path,
        string key,
        string member
    )
    {
        await using var host = await BindingHost.StartAsync(mvc: true);

        Assert.Null(
            await host.BoundAsync(HttpMethod.Get, path, [(InputSource.Query, key)], member)
        );
    }

    [Fact]
    public async Task Host_Never_Prefixes_A_Header_Member()
    {
        await using var host = await BindingHost.StartAsync(mvc: true);

        Assert.Null(
            await host.BoundAsync(
                HttpMethod.Get,
                "/prefix/mvc/query-named",
                [(InputSource.Header, "p.X-Alias")],
                "Header"
            )
        );
    }

    [Fact]
    public async Task Host_Reads_Route_Members_Only_Without_A_Prefix()
    {
        // Route tokens can't carry the prefix, so a Name = on a [FromRoute] complex parameter
        // leaves every member unbound, and the checker's p.Plain matches no token.
        await using var host = await BindingHost.StartAsync(mvc: true);

        foreach (
            var (path, action, plain, renamed, bound) in new[]
            {
                (
                    "/prefix/mvc/route/{Plain}/{alias}",
                    nameof(NamePrefixController.Route),
                    "Plain",
                    "alias",
                    true
                ),
                (
                    "/prefix/mvc/route-named/{Plain}/{alias}",
                    nameof(NamePrefixController.RouteNamed),
                    "p.Plain",
                    "p.alias",
                    false
                ),
            }
        )
        {
            var inputs = BindingHost.CheckerInputs(
                typeof(NamePrefixController),
                action,
                ParameterBindingHost.Mvc,
                infersBody: true,
                route: path
            );
            Assert.Equal([plain, renamed], inputs.Select(input => input.WireName));
            Assert.All(inputs, input => Assert.Equal(InputSource.Route, input.Source));

            var request = path.Replace("{Plain}", BindingHost.Value)
                .Replace("{alias}", BindingHost.Value);
            Assert.Equal(
                bound ? BindingHost.Value : null,
                await host.BoundAsync(HttpMethod.Get, request, [], "Plain")
            );
            Assert.Equal(
                bound ? BindingHost.Value : null,
                await host.BoundAsync(HttpMethod.Get, request, [], "Renamed")
            );
        }
    }

    public static TheoryData<string, string[], string[]> FilteredMembers() =>
        new()
        {
            // A parameter's [Bind] include list limits its type's members.
            { nameof(FilterController.Parameter), ["Title"], ["Body"] },
            // So does the type's own list.
            { nameof(FilterController.Type), ["Title"], ["Body"] },
            // A parameter's list and its type's both apply: MVC binds what both name.
            { nameof(FilterController.Both), ["Title"], ["Body"] },
            // A nested type's list limits it where it's bound; the outer type has none.
            {
                nameof(FilterController.Nested),
                ["Title", "Body", "Inner", "Inner.Title"],
                ["Inner.Body"]
            },
            // A positional record's constructor parameter left out is constructed as null.
            { nameof(FilterController.Record), ["Title"], ["Body"] },
            { nameof(FilterController.Never), ["Title"], ["Body"] },
            { nameof(FilterController.Behavior), ["Title"], ["Body"] },
            // A class-level [BindNever] skips every member but one with a behaviour of its own.
            { nameof(FilterController.ClassNever), ["Title"], ["Body"] },
        };

    [Theory]
    [MemberData(nameof(FilteredMembers))]
    public async Task Host_binds_what_MVCs_filters_let_through(
        string action,
        string[] bound,
        string[] skipped
    )
    {
        var path = $"/filters/{action}";
        await using var host = await BindingHost.StartAsync(mvc: true);
        var inputs = BindingHost.CheckerInputs(
            typeof(FilterController),
            action,
            ParameterBindingHost.Mvc,
            infersBody: true,
            route: path
        );

        await host.AssertBindsUnder(HttpMethod.Post, path, inputs, bound);
        foreach (var member in skipped)
        {
            Assert.Null(
                await host.BoundAsync(HttpMethod.Post, path, [(InputSource.Form, member)], member)
            );
        }
    }

    [Fact]
    public void A_property_filter_Rivet_cant_read_stops_an_MVC_check_alone()
    {
        foreach (
            var handler in new[]
            {
                nameof(FilterUnreadable.OnType),
                nameof(FilterUnreadable.OnParameter),
            }
        )
        {
            var error = Assert.Throws<RivetUserException>(() =>
                BindingHost.CheckerInputs(
                    typeof(FilterUnreadable),
                    handler,
                    ParameterBindingHost.Mvc,
                    infersBody: true,
                    route: "/unreadable"
                )
            );
            Assert.StartsWith("error RIV1115: ", error.Message);
            Assert.Contains(
                "[TitleOnly] is a property filter (IPropertyFilterProvider)",
                error.Message
            );

            // The minimal-API form mapper reads no property filter.
            Assert.Equal(
                ["f.Title", "f.Body"],
                BindingHost
                    .CheckerInputs(
                        typeof(FilterUnreadable),
                        handler,
                        ParameterBindingHost.MinimalApi,
                        infersBody: true,
                        route: "/unreadable"
                    )
                    .Select(input => input.Path)
            );
        }

        var behavior = Assert.Throws<RivetUserException>(() =>
            BindingHost.CheckerInputs(
                typeof(FilterUnreadable),
                nameof(FilterUnreadable.CustomBehavior),
                ParameterBindingHost.Mvc,
                infersBody: true,
                route: "/unreadable"
            )
        );
        Assert.StartsWith("error RIV1115: ", behavior.Message);
        Assert.Contains("[Skipped] is a BindingBehaviorAttribute", behavior.Message);
    }

    [Theory]
    // A member MVC skips still carries its binding source, so MVC infers nothing and binds the
    // parameter member by member: [BindNever], a [Bind] list that leaves it out, no setter.
    [InlineData(nameof(InferController.Never), false)]
    [InlineData(nameof(InferController.Listed), false)]
    [InlineData(nameof(InferController.GetOnly), false)]
    // A record constructor's members aren't among the properties inference reads, so their
    // binding sources count for nothing, an attribute's or a type's (IFormFile): the record is
    // a body, which a GET without one refuses.
    [InlineData(nameof(InferController.Record), true)]
    [InlineData(nameof(InferController.RecordFile), true)]
    public async Task ApiController_inference_reads_every_property_MVC_lists(
        string action,
        bool body
    )
    {
        var path = $"/infer/{action}";
        await using var host = await BindingHost.StartAsync(mvc: true);
        var inputs = BindingHost.CheckerInputs(
            typeof(InferController),
            action,
            ParameterBindingHost.Mvc,
            infersBody: true,
            route: path
        );

        if (body)
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await host.GetAsync(path)).Status);
            Assert.Equal(InputSource.BodyOrService, Assert.Single(inputs).Source);
        }
        else
        {
            Assert.All(inputs, input => Assert.Equal(InputSource.ValueProviders, input.Source));
            await host.AssertBindsUnder(HttpMethod.Get, path, inputs, ["Plain"]);
        }
    }

    /// <summary>
    /// Resolves a schema reference through the emitted document's components,
    /// returning the target component and its name; inline schemas are returned
    /// unchanged with a null name.
    /// </summary>
    private static (JsonElement Component, string? Name) ResolveSchemaComponent(
        JsonElement schema,
        JsonElement root
    )
    {
        if (
            schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("$ref", out var reference)
        )
        {
            var segments = reference.GetString()!.Split('/').Skip(1);
            var name = segments.Last();
            var current = root;
            foreach (var segment in segments)
            {
                current = current.GetProperty(Uri.UnescapeDataString(segment));
            }

            return (current, name);
        }

        return (schema, null);
    }
}

public sealed class PrefixQuery
{
    public string? Plain { get; set; }

    [FromQuery(Name = "alias")]
    public string? Renamed { get; set; }

    [BindProperty(Name = "bp")]
    public string? Bound { get; set; }

    [FromHeader(Name = "X-Alias")]
    public string? Header { get; set; }

    public PrefixQueryInner? Inner { get; set; }
}

public sealed class PrefixQueryInner
{
    public string? Value { get; set; }

    [FromQuery(Name = "deep")]
    public string? Renamed { get; set; }
}

public sealed class PrefixForm
{
    public string? Plain { get; set; }

    [FromForm(Name = "alias")]
    public string? Renamed { get; set; }

    public PrefixFormInner? Inner { get; set; }
}

public sealed class PrefixFormInner
{
    public string? Value { get; set; }

    [FromForm(Name = "deep")]
    public string? Renamed { get; set; }
}

public sealed class PrefixRoute
{
    public string? Plain { get; set; }

    [FromRoute(Name = "alias")]
    public string? Renamed { get; set; }
}

public sealed class PrefixMixed
{
    public string? Plain { get; set; }

    [FromQuery(Name = "alias")]
    public string? Renamed { get; set; }

    [FromHeader(Name = "X-Alias")]
    public string? Header { get; set; }
}

[Bind(Prefix = "c")]
public sealed class PrefixedByType
{
    public string? Plain { get; set; }
}

[Bind(Prefix = "b")]
public class PrefixedBase
{
    public string? Plain { get; set; }
}

public sealed class PrefixedByBase : PrefixedBase;

/// <summary>The MVC handlers <see cref="BindingHost"/> serves for the Name = pins.</summary>
[ApiController]
public sealed class NamePrefixController : ControllerBase
{
    [HttpGet("/prefix/mvc/query")]
    public string Query([FromQuery] PrefixQuery f) => BindingHost.Echo(f);

    [HttpGet("/prefix/mvc/query-named")]
    public string QueryNamed([FromQuery(Name = "p")] PrefixQuery f) => BindingHost.Echo(f);

    [HttpPost("/prefix/mvc/form")]
    public string Form([FromForm] PrefixForm f) => BindingHost.Echo(f);

    [HttpPost("/prefix/mvc/form-named")]
    public string FormNamed([FromForm(Name = "p")] PrefixForm f) => BindingHost.Echo(f);

    [HttpGet("/prefix/mvc/route/{Plain}/{alias}")]
    public string Route([FromRoute] PrefixRoute f) => BindingHost.Echo(f);

    [HttpGet("/prefix/mvc/route-named/{Plain}/{alias}")]
    public string RouteNamed([FromRoute(Name = "p")] PrefixRoute f) => BindingHost.Echo(f);

    [HttpPost("/prefix/mvc/mixed")]
    public string Mixed([FromForm(Name = "p")] PrefixMixed f) => BindingHost.Echo(f);

    [HttpGet("/prefix/mvc/bind")]
    public string Bind([FromQuery, Bind(Prefix = "p")] PrefixQuery f) => BindingHost.Echo(f);

    [HttpGet("/prefix/mvc/bind-type")]
    public string BindType([FromQuery] PrefixedByType f) => BindingHost.Echo(f);

    [HttpGet("/prefix/mvc/bind-simple")]
    public string BindSimple([Bind(Prefix = "q")] string? term) => BindingHost.Echo(term);

    [HttpGet("/prefix/mvc/bind-base")]
    public string BindBase([FromQuery] PrefixedByBase f) => BindingHost.Echo(f);
}

public sealed class FilterPlain
{
    public string? Title { get; set; }

    public string? Body { get; set; }
}

[Bind(nameof(Title))]
public sealed class FilterListed
{
    public string? Title { get; set; }

    public string? Body { get; set; }
}

public sealed class FilterOuter
{
    public string? Title { get; set; }

    public string? Body { get; set; }

    public FilterListed? Inner { get; set; }
}

[Bind(nameof(Title))]
public sealed record FilterRecord(string? Title, string? Body);

public sealed class FilterNever
{
    public string? Title { get; set; }

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public string? Body { get; set; }
}

public sealed class FilterBehavior
{
    public string? Title { get; set; }

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior(
        Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Never
    )]
    public string? Body { get; set; }
}

[Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
public sealed class FilterClassNever
{
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior(
        Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Optional
    )]
    public string? Title { get; set; }

    public string? Body { get; set; }
}

/// <summary>The MVC handlers <see cref="BindingHost"/> serves for the property-filter pins.</summary>
[ApiController]
public sealed class FilterController : ControllerBase
{
    [HttpPost("/filters/Parameter")]
    public string Parameter([FromForm, Bind(nameof(FilterPlain.Title))] FilterPlain f) =>
        BindingHost.Echo(f);

    [HttpPost("/filters/Type")]
    public string Type([FromForm] FilterListed f) => BindingHost.Echo(f);

    [HttpPost("/filters/Both")]
    public string Both([FromForm, Bind("Title, Body")] FilterListed f) => BindingHost.Echo(f);

    [HttpPost("/filters/Nested")]
    public string Nested([FromForm] FilterOuter f) => BindingHost.Echo(f);

    [HttpPost("/filters/Record")]
    public string Record([FromForm] FilterRecord f) => BindingHost.Echo(f);

    [HttpPost("/filters/Never")]
    public string Never([FromForm] FilterNever f) => BindingHost.Echo(f);

    [HttpPost("/filters/Behavior")]
    public string Behavior([FromForm] FilterBehavior f) => BindingHost.Echo(f);

    [HttpPost("/filters/ClassNever")]
    public string ClassNever([FromForm] FilterClassNever f) => BindingHost.Echo(f);
}

/// <summary>A property filter that runs code: nothing static can say what it lets through.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Parameter)]
public sealed class TitleOnlyAttribute
    : Attribute,
        Microsoft.AspNetCore.Mvc.ModelBinding.IPropertyFilterProvider
{
    public Func<Microsoft.AspNetCore.Mvc.ModelBinding.ModelMetadata, bool> PropertyFilter =>
        metadata => metadata.PropertyName == nameof(FilterPlain.Title);
}

/// <summary>A binding behaviour its own constructor sets.</summary>
public sealed class SkippedAttribute()
    : Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehaviorAttribute(
        Microsoft.AspNetCore.Mvc.ModelBinding.BindingBehavior.Never
    );

[TitleOnly]
public sealed class FilterCustom
{
    public string? Title { get; set; }

    public string? Body { get; set; }
}

public sealed class FilterCustomBehavior
{
    public string? Title { get; set; }

    [Skipped]
    public string? Body { get; set; }
}

/// <summary>Handlers the checker reads (never served) for the unreadable-filter pins.</summary>
public static class FilterUnreadable
{
    public static string OnType([FromForm] FilterCustom f) => BindingHost.Echo(f);

    public static string OnParameter([FromForm, TitleOnly] FilterPlain f) => BindingHost.Echo(f);

    public static string CustomBehavior([FromForm] FilterCustomBehavior f) => BindingHost.Echo(f);
}

public sealed class InferNever
{
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    [FromQuery(Name = "h")]
    public string? Hidden { get; set; }

    public string? Plain { get; set; }
}

[Bind(nameof(Plain))]
public sealed class InferListed
{
    [FromQuery(Name = "h")]
    public string? Hidden { get; set; }

    public string? Plain { get; set; }
}

public sealed class InferGetOnly
{
    [FromQuery(Name = "h")]
    public string? Hidden { get; }

    public string? Plain { get; set; }
}

public sealed record InferRecord([FromQuery] string? Plain);

public sealed record InferRecordFile(Microsoft.AspNetCore.Http.IFormFile? Plain);

/// <summary>The [ApiController] actions whose unattributed parameter inference decides.</summary>
[ApiController]
public sealed class InferController : ControllerBase
{
    [HttpGet("/infer/Never")]
    public string Never(InferNever f) => BindingHost.Echo(f);

    [HttpGet("/infer/Listed")]
    public string Listed(InferListed f) => BindingHost.Echo(f);

    [HttpGet("/infer/GetOnly")]
    public string GetOnly(InferGetOnly f) => BindingHost.Echo(f);

    [HttpGet("/infer/Record")]
    public string Record(InferRecord f) => BindingHost.Echo(f);

    [HttpGet("/infer/RecordFile")]
    public string RecordFile(InferRecordFile f) => BindingHost.Echo(f);
}

/// <summary>A controller without [ApiController]: no inferred sources, value providers only.</summary>
public sealed class PlainNamePrefixController : Controller
{
    [HttpGet("/prefix/plain/bind")]
    public string Bind([Bind(Prefix = "p")] PrefixQuery f) => BindingHost.Echo(f);

    [HttpGet("/prefix/plain/bind-simple")]
    public string BindSimple([Bind(Prefix = "q")] string? term) => BindingHost.Echo(term);
}
