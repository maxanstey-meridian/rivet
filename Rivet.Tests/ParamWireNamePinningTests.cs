using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// FABLE_ROUNDTRIP findings #1/#2/#4 — parameter wire-name identity. A route
/// token and an input property are the same param when they match under
/// normalization (case-folded, '_'/'-' stripped); the param keeps the TOKEN's
/// spelling because the route template is wire truth. Before pinning, any
/// casing divergence silently invented a required query twin (#1, 356 ops),
/// truncated hyphenated tokens corrupted templates (#2), and inputs whose
/// every property was route-bound fabricated a required JSON body (#4).
/// </summary>
public sealed class ParamWireNamePinningTests
{
    private static IReadOnlyList<TsEndpointDefinition> Generate(string source)
        => CompilationHelper.WalkContract(source).Endpoints;

    [Fact]
    public void SnakeCase_RouteToken_Matches_PascalCase_Property_Without_QueryTwin()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record GetThingInput(long ThingId);

            [RivetType]
            public sealed record ThingDto(string Name);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define GetThing =
                    Define.Get<GetThingInput, ThingDto>("/things/{thing_id}");
            }
            """;

        var ep = Assert.Single(Generate(source));

        var param = Assert.Single(ep.Params);
        Assert.Equal("thing_id", param.Name); // the token's spelling, not camelCase
        Assert.Equal(ParamSource.Route, param.Source);
        Assert.True(param.Type is TsType.Primitive { Name: "number" }); // typed from ThingId, not defaulted
    }

    [Fact]
    public void Hyphenated_RouteToken_Is_One_Param_Not_A_Truncated_Collision()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record GetGroupTeamInput(string Group, string GroupTeam);

            [RivetContract]
            public static class GroupsContract
            {
                public static readonly Define GetGroupTeam =
                    Define.Get("/groups/{group}/teams/{group-team}")
                        .Status(204)
                        .Accepts<GetGroupTeamInput>();
            }
            """;

        var ep = Assert.Single(Generate(source));

        Assert.Equal("/groups/{group}/teams/{group-team}", ep.RouteTemplate);
        Assert.Equal(2, ep.Params.Count);
        Assert.Contains(ep.Params, p => p is { Name: "group", Source: ParamSource.Route });
        Assert.Contains(ep.Params, p => p is { Name: "group-team", Source: ParamSource.Route });
    }

    [Fact]
    public void Put_Input_Fully_RouteBound_Emits_No_Body()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SuspendThingInput(long ThingId);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define SuspendThing =
                    Define.Put("/things/{thing_id}/suspended")
                        .Status(204)
                        .Accepts<SuspendThingInput>();
            }
            """;

        var ep = Assert.Single(Generate(source));

        var param = Assert.Single(ep.Params);
        Assert.Equal("thing_id", param.Name);
        Assert.Equal(ParamSource.Route, param.Source);
        Assert.True(param.Type is TsType.Primitive { Name: "number" });
        Assert.DoesNotContain(ep.Params, p => p.Source == ParamSource.Body);
    }

    [Fact]
    public void Put_Input_With_NonRoute_Properties_Keeps_Its_Body()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UpdateThingInput(long ThingId, string Name);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define UpdateThing =
                    Define.Put("/things/{thing_id}")
                        .Status(204)
                        .Accepts<UpdateThingInput>();
            }
            """;

        var ep = Assert.Single(Generate(source));

        Assert.Contains(ep.Params, p => p is { Name: "thing_id", Source: ParamSource.Route });
        Assert.Contains(ep.Params, p => p.Source == ParamSource.Body);
    }

    [Fact]
    public void Unmatched_RouteToken_With_Input_Warns_RIV1019()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ListPostsInput(int Page);

            [RivetType]
            public sealed record PostDto(string Id);

            [RivetContract]
            public static class PostsContract
            {
                public static readonly Define ListPosts =
                    Define.Get<ListPostsInput, PostDto>("/users/{userId}/posts");
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() => Generate(source));

        Assert.Contains("RIV1019", stderr);
        Assert.Contains("{userId}", stderr);
    }

    [Fact]
    public void RouteOnly_Endpoint_Without_Input_Does_Not_Warn()
    {
        // The route string doubles as the assertion needle: stderr capture is
        // process-global, so under parallel test classes another fixture's
        // legitimate RIV1019 can land in this window — only an endpoint-unique
        // substring makes the absence assertion sound.
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly Define GetThing =
                    Define.Get<ThingDto>("/route-only-no-input-things/{id}");
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(() => Generate(source));

        Assert.DoesNotContain("/route-only-no-input-things/", stderr);
    }

    [Fact]
    public void Imported_SnakeCase_Query_Params_Are_Pinned_To_Their_Wire_Name()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
                "ItemDto": { "type": "object", "properties": { "id": { "type": "string" } } }
                """,
            paths: """
                "/api/items": {
                    "get": {
                        "operationId": "listItems",
                        "parameters": [
                            {"name": "per_page", "in": "query", "required": false, "schema": {"type": "integer"}},
                            {"name": "page", "in": "query", "required": false, "schema": {"type": "integer"}}
                        ],
                        "responses": {
                            "200": {
                                "description": "ok",
                                "content": {"application/json": {"schema": {"$ref": "#/components/schemas/ItemDto"}}}
                            }
                        }
                    }
                }
                """,
            title: "API");

        var result = CompilationHelper.Import(spec);
        var input = CompilationHelper.FindFile(result, "ListItemsInput.cs");

        // FABLE_ROUNDTRIP #1's query half: per_page must not drift to perPage
        Assert.Contains("[property: JsonPropertyName(\"per_page\")]", input);
        // page -> Page -> camelCase 'page' is already wire-true: no pin
        Assert.DoesNotContain("[property: JsonPropertyName(\"page\")]", input);

        // and the wire name survives to the re-emitted spec
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var ep = Assert.Single(endpoints);
        Assert.Contains(ep.Params, p => p is { Name: "per_page", Source: ParamSource.Query });
    }

    [Fact]
    public void NormalizeForMatching_Strips_Separators_And_Case()
    {
        Assert.Equal("thingid", RouteParser.NormalizeForMatching("thing_id"));
        Assert.Equal("thingid", RouteParser.NormalizeForMatching("ThingId"));
        Assert.Equal("enterpriseteam", RouteParser.NormalizeForMatching("enterprise-team"));
        Assert.Equal("enterpriseteam", RouteParser.NormalizeForMatching("EnterpriseTeam"));
    }

    [Fact]
    public void ParseRouteParamNames_Keeps_Hyphenated_Names_Whole()
    {
        var names = RouteParser.ParseRouteParamNames("/enterprises/{enterprise}/teams/{enterprise-team}");

        Assert.Equal(2, names.Count);
        Assert.Contains("enterprise", names);
        Assert.Contains("enterprise-team", names);
    }

    [Fact]
    public void ParseRouteParamNames_Still_Strips_Constraints_Defaults_And_Optional_Markers()
    {
        var names = RouteParser.ParseRouteParamNames("/a/{id:int}/b/{code:regex(^\\d{4}$)}/c/{slug?}/d/{rest=*}/e/{**path}");

        Assert.Contains("id", names);
        Assert.Contains("code", names);
        Assert.Contains("slug", names);
        Assert.Contains("rest", names);
        Assert.Contains("path", names);
    }
}
