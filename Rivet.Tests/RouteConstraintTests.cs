using System.Text.Json;
using System.Text.RegularExpressions;
using Rivet.Tool;
using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// G.4: a route constraint types its path parameter and adds its keywords; one the
/// declared type can't hold is RIV1125; a token nothing declares stops generation with RIV1019.
/// </summary>
public sealed class RouteConstraintTests
{
    private static string Contract(string definition, string types = "") =>
        $$"""
            using System;
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            {{types}}

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly {{definition}};
            }
            """;

    private static JsonElement PathParam(string source, string name)
    {
        var document = CompilationHelper.EmitOpenApi(source);
        var operation = document
            .RootElement.GetProperty("paths")
            .EnumerateObject()
            .Single()
            .Value.EnumerateObject()
            .Single()
            .Value;
        return operation
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter =>
                parameter.GetProperty("in").GetString() == "path"
                && parameter.GetProperty("name").GetString() == name
            );
    }

    private static string Keyword(JsonElement parameter, string keyword) =>
        parameter.GetProperty("schema").TryGetProperty(keyword, out var value)
            ? value.ToString()
            : "<absent>";

    [Theory]
    [InlineData("int", "integer", "int32")]
    [InlineData("long", "integer", "int64")]
    [InlineData("guid", "string", "uuid")]
    [InlineData("bool", "boolean", "<absent>")]
    [InlineData("datetime", "string", "date-time")]
    [InlineData("decimal", "number", "decimal")]
    [InlineData("double", "number", "double")]
    [InlineData("float", "number", "float")]
    [InlineData("GUID", "string", "uuid")]
    public void Type_Constraint_Types_A_Token_No_Input_Declares(
        string constraint,
        string type,
        string format
    )
    {
        var route = $"/typed-token-{constraint.ToLowerInvariant()}/{{value:{constraint}}}";
        var source = Contract($"RouteDefinition<ThingDto> Get = Define.Get<ThingDto>(\"{route}\")");

        JsonElement parameter = default;
        var stderr = CompilationHelper.CaptureStdErr(() => parameter = PathParam(source, "value"));

        Assert.Equal(type, Keyword(parameter, "type"));
        Assert.Equal(format, Keyword(parameter, "format"));
        Assert.DoesNotContain($"typed-token-{constraint.ToLowerInvariant()}/", stderr);
    }

    [Fact]
    public void Constraints_Are_Stripped_From_The_Path_But_Kept_For_The_Param()
    {
        var source = Contract(
            "RouteDefinition<ThingDto> Get = Define.Get<ThingDto>(\"/messages/{messageIndex:int:min(0)}\")"
        );

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal("/messages/{messageIndex}", endpoint.RouteTemplate);
        var parameter = Assert.Single(endpoint.Params);
        Assert.Equal(new TsType.Primitive("number", "int32"), parameter.Type);
        Assert.Equal(0, parameter.Constraints?.Minimum);
    }

    [Theory]
    [InlineData("{page:int:min(1)}", "minimum", "1")]
    [InlineData("{page:int:max(50)}", "maximum", "50")]
    [InlineData("{page:long:range(1,50)}", "minimum", "1")]
    [InlineData("{page:long:range(1,50)}", "maximum", "50")]
    [InlineData("{page:int:min(1)?}", "minimum", "1")]
    [InlineData("{page:int:min(1)=1}", "minimum", "1")]
    public void Numeric_Constraints_Become_Bounds(string token, string keyword, string expected)
    {
        var source = Contract(
            $"RouteDefinition<ThingDto> Get = Define.Get<ThingDto>(\"/pages/{token}\")"
        );

        Assert.Equal(expected, Keyword(PathParam(source, "page"), keyword));
    }

    [Theory]
    [InlineData("{code:alpha}", "pattern", "^[a-zA-Z]*$")]
    [InlineData("{code:length(3)}", "minLength", "3")]
    [InlineData("{code:length(3)}", "maxLength", "3")]
    [InlineData("{code:length(2,8)}", "minLength", "2")]
    [InlineData("{code:length(2,8)}", "maxLength", "8")]
    [InlineData("{code:minlength(2)}", "minLength", "2")]
    [InlineData("{code:maxlength(8)}", "maxLength", "8")]
    [InlineData(@"{code:regex(^\\d{{4}}$)}", "pattern", @"^\d{4}$")]
    [InlineData(@"{code:regex(^(a|b)?[0-9]+$)}", "pattern", "^([aA]|[bB])?[0-9]+$")]
    [InlineData("{code:guid:length(36)}", "maxLength", "36")]
    public void String_Constraints_Become_Keywords_On_A_String_Property(
        string token,
        string keyword,
        string expected
    )
    {
        var source = Contract(
            $"RouteDefinition<CodeInput, ThingDto> Get = Define.Get<CodeInput, ThingDto>(\"/codes/{token}\")",
            token.Contains("guid")
                ? "[RivetType] public sealed record CodeInput(Guid Code);"
                : "[RivetType] public sealed record CodeInput(string Code);"
        );

        Assert.Equal(expected, Keyword(PathParam(source, "code"), keyword));
    }

    [Fact]
    public void Constraint_Agreeing_With_Its_Property_Keeps_The_Property_Type()
    {
        var source = Contract(
            "RouteDefinition<MessageInput, ThingDto> Get = Define.Get<MessageInput, ThingDto>(\"/messages/{messageIndex:int}\")",
            "[RivetType] public sealed record MessageInput(int MessageIndex);"
        );

        var parameter = PathParam(source, "messageIndex");

        Assert.Equal("integer", Keyword(parameter, "type"));
        Assert.Equal("int32", Keyword(parameter, "format"));
    }

    [Fact]
    public void Constraint_Agrees_With_A_Property_Its_Schema_Type_Calls_Integer()
    {
        // [RivetSchemaType("integer")] (as import writes it) and {id:int} both emit
        // type: integer, format: int32.
        var source = Contract(
            "RouteDefinition<MessageInput, ThingDto> Get = Define.Get<MessageInput, ThingDto>(\"/integer-messages/{messageIndex:int}\")",
            "[RivetType] public sealed record MessageInput([property: RivetSchemaType(\"integer\")] int MessageIndex);"
        );

        var parameter = PathParam(source, "messageIndex");

        Assert.Equal("integer", Keyword(parameter, "type"));
        Assert.Equal("int32", Keyword(parameter, "format"));
    }

    [Fact]
    public void Constraint_Bounds_That_Meet_An_Exclusive_Bound_Are_RIV1125()
    {
        var source = Contract(
            "RouteDefinition<PageInput, ThingDto> Get = Define.Get<PageInput, ThingDto>(\"/exclusive-pages/{page:int:min(5)}\")",
            "[RivetType] public sealed record PageInput([property: Range(1, 5, MaximumIsExclusive = true)] int Page);"
        );

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains(Diagnostics.RouteConstraintContradictsType, exception.Message);
        Assert.Contains("admit no value", exception.Message);
    }

    [Fact]
    public void Declared_Path_Parameter_Named_Only_Ignoring_Case_Is_RIV1123()
    {
        // An OpenAPI path parameter's name must equal its token, so .Parameter<int>("Id")
        // can't declare {id}.
        var source = Contract(
            "RouteDefinition<ThingDto> Get = Define.Get<ThingDto>(\"/cased-things/{id}\").Parameter<int>(\"Id\", \"path\", true)"
        );

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains(Diagnostics.InvalidBuilderArgument, exception.Message);
        Assert.Contains("the route's token is '{id}'", exception.Message);
        Assert.Contains("spell it \"id\"", exception.Message);
    }

    [Theory]
    [InlineData("/two-params/{a}{b}")]
    [InlineData("/optional-default/{id?=5}")]
    [InlineData("/unclosed/{id")]
    public void Template_Routing_Refuses_Is_RIV1125(string route)
    {
        var source = Contract($"RouteDefinition<ThingDto> Get = Define.Get<ThingDto>(\"{route}\")");

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains(Diagnostics.RouteConstraintContradictsType, exception.Message);
        Assert.Contains("routing refuses it at startup", exception.Message);
    }

    [Theory]
    [InlineData("/literal/{{x}}/things")]
    [InlineData("/literal/a}}b")]
    public void A_Literal_Brace_In_A_Contract_Route_Is_RIV1125(string route)
    {
        // Routing reads {{x}} as the literal text {x}, which is no parameter, but an OpenAPI
        // path has no escape for a brace, so the path can't be emitted.
        var source = Contract($"RouteDefinition<ThingDto> Get = Define.Get<ThingDto>(\"{route}\")");

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains(Diagnostics.RouteConstraintContradictsType, exception.Message);
        Assert.Contains("carries a literal brace", exception.Message);
        Assert.Contains("ThingsContract.Get", exception.Message);
    }

    [Fact]
    public void A_Literal_Brace_In_An_Action_Route_Is_RIV1125()
    {
        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkMerged(AttributeRoute("", "api/[controller]/{{x}}"))
        );

        Assert.Contains(Diagnostics.RouteConstraintContradictsType, exception.Message);
        Assert.Contains("carries a literal brace", exception.Message);
        Assert.Contains("ThingsController.Get", exception.Message);
    }

    [Fact]
    public void A_Repeated_Token_On_An_Endpoint_Method_Is_RIV1125()
    {
        // Unlike a contract, which import can write with a repeat, an MVC action is
        // hand-written, and routing refuses its template at startup.
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            public sealed class ThingsController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("/repeated/{id}/maps/{ID}")]
                public ThingDto Get(string id) => new(id);
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        Assert.Contains(Diagnostics.RouteConstraintContradictsType, exception.Message);
        Assert.Contains("routing refuses it at startup", exception.Message);
        Assert.Contains("appears more than one time", exception.Message);
    }

    [Fact]
    public void A_Repeated_Token_Is_Read_Rather_Than_Refused()
    {
        // Routing refuses a template that repeats a parameter, but an imported OpenAPI path
        // can repeat one (Cloudflare's /accounts/{account_id}/…/accounts/{account_id}), so
        // the read keeps each occurrence under its own spelling and the walk goes on.
        var tokens = RouteParser.ParseRouteTokens("/accounts/{id:int}/maps/{ID}/x/{id?}");

        Assert.Equal(["id", "ID", "id"], tokens.Select(token => token.Name));
        Assert.Equal("int", Assert.Single(tokens[0].Constraints).Name);
        Assert.Empty(tokens[1].Constraints);
        Assert.True(tokens[2].IsOptional);

        var source = Contract(
            "RouteDefinition<ThingDto> Get = Define.Get<ThingDto>(\"/accounts/{id}/maps/{id}\").Parameter<string>(\"id\", \"path\", true)"
        );
        var endpoint = Assert.Single(CompilationHelper.WalkContract(source).Endpoints);
        Assert.Equal("/accounts/{id}/maps/{id}", endpoint.RouteTemplate);
    }

    [Fact]
    public void Constraint_Bounds_Intersect_With_The_Property_Range()
    {
        var source = Contract(
            "RouteDefinition<PageInput, ThingDto> Get = Define.Get<PageInput, ThingDto>(\"/pages/{page:int:range(5,500)}\")",
            "[RivetType] public sealed record PageInput([property: Range(1, 100)] int Page);"
        );

        var parameter = PathParam(source, "page");

        Assert.Equal("5", Keyword(parameter, "minimum"));
        Assert.Equal("100", Keyword(parameter, "maximum"));
    }

    [Theory]
    // Route-and-query lowering (GET)
    [InlineData(
        "RouteDefinition<IdInput, ThingDto> Get = Define.Get<IdInput, ThingDto>(\"/things/{id:int}\")",
        "[RivetType] public sealed record IdInput(string Id);"
    )]
    // Body lowering (PUT with a JSON body)
    [InlineData(
        "RouteDefinition<IdInput, ThingDto> Put = Define.Put<IdInput, ThingDto>(\"/things/{id:int}\")",
        "[RivetType] public sealed record IdInput(Guid Id, string Name);"
    )]
    // A declared path parameter
    [InlineData(
        "RouteDefinition<ThingDto> Get = Define.Get<ThingDto>(\"/things/{id:guid}\").Parameter<int>(\"id\", \"path\", true)",
        ""
    )]
    public void Type_Constraint_Contradicting_The_Declared_Type_Is_RIV1125(
        string definition,
        string types
    )
    {
        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(Contract(definition, types))
        );

        Assert.Contains(Diagnostics.RouteConstraintContradictsType, exception.Message);
        Assert.Contains("'{id}'", exception.Message);
        Assert.Contains("ThingsContract", exception.Message);
    }

    [Theory]
    [InlineData("{id:int:guid}", "give it two types")]
    [InlineData("{id:min(1)}", "admits only integers", true)]
    [InlineData("{id:int:length(3)}", "constrains the characters of a string")]
    [InlineData("{id:int:min(10):max(5)}", "admit no value")]
    [InlineData("{id:int:min(one)}", "needs an integer")]
    [InlineData("{id:regex}", "needs a pattern", true)]
    [InlineData("{id:alpha:regex(^x$)}", "a schema holds one pattern", true)]
    public void Constraint_The_Type_Cannot_Hold_Is_RIV1125(
        string token,
        string reason,
        bool declaredByAStringProperty = false
    )
    {
        // A token with only keyword constraints needs a declaration of its own (RIV1019
        // otherwise): a string input property here.
        var source = declaredByAStringProperty
            ? Contract(
                $"RouteDefinition<IdInput, ThingDto> Get = Define.Get<IdInput, ThingDto>(\"/bad-constraint/{token}\")",
                "[RivetType] public sealed record IdInput(string Id);"
            )
            : Contract(
                $"RouteDefinition<ThingDto> Get = Define.Get<ThingDto>(\"/bad-constraint/{token}\")"
            );

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.CaptureStdErr(() => CompilationHelper.WalkContract(source))
        );

        Assert.Contains(Diagnostics.RouteConstraintContradictsType, exception.Message);
        Assert.Contains(reason, exception.Message);
    }

    [Theory]
    // The silent branch: a POST with no TInput lowers as a body, and its tokens used to
    // become strings without a word.
    [InlineData(
        "RouteDefinition Archive = Define.Post(\"/undeclared-forms/{formId}/archive\")",
        "route token '{formId}' on POST /undeclared-forms/{formId}/archive has no input type",
        "give the endpoint an input type with a 'FormId' property, a type constraint ('{formId:guid}'"
    )]
    [InlineData(
        "RouteDefinition<RenameInput, ThingDto> Rename = Define.Put<RenameInput, ThingDto>(\"/rename-things/{thing_id}\")",
        "route token '{thing_id}' on PUT /rename-things/{thing_id} has no matching property on input type 'RenameInput'",
        "add a 'ThingId' property"
    )]
    [InlineData(
        "RouteDefinition<RenameInput, ThingDto> Find = Define.Get<RenameInput, ThingDto>(\"/find-things/{slug}\")",
        "route token '{slug}' on GET /find-things/{slug} has no matching property on input type 'RenameInput'",
        ".Parameter<T>(\"slug\", \"path\", ...)"
    )]
    [InlineData(
        "RouteDefinition<ThingDto> Show = Define.Get<ThingDto>(\"/show-things/{slug:alpha}\")",
        "route token '{slug}' on GET /show-things/{slug} has no input type",
        "'{slug:int}'"
    )]
    public void Undeclared_Token_Stops_Generation_In_Every_Lowering(
        string field,
        string what,
        string howToDeclare
    )
    {
        var source = Contract(field, "[RivetType] public sealed record RenameInput(string Name);");

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.StartsWith($"error {Diagnostics.RouteTokenUndeclared}: {what}", exception.Message);
        Assert.Contains(howToDeclare, exception.Message);
    }

    [Theory]
    [InlineData("Define.Post(\"/constraint-declares-post/{id:guid}/archive\")", "RouteDefinition")]
    [InlineData(
        "Define.Get<NameInput, ThingDto>(\"/constraint-declares-get/{id:guid}\")",
        "RouteDefinition<NameInput, ThingDto>"
    )]
    [InlineData(
        "Define.Put<NameInput, ThingDto>(\"/constraint-declares-put/{id:guid}\")",
        "RouteDefinition<NameInput, ThingDto>"
    )]
    public void Type_Constraint_Declares_A_Token_In_Every_Lowering(string define, string type)
    {
        var source = Contract(
            $"{type} Op = {define}",
            "[RivetType] public sealed record NameInput(string Name);"
        );

        IReadOnlyList<TsEndpointDefinition> endpoints = [];
        var stderr = CompilationHelper.CaptureStdErr(() =>
            endpoints = CompilationHelper.WalkContract(source).Endpoints
        );

        Assert.DoesNotContain("constraint-declares-", stderr);
        var parameter = Assert.Single(
            Assert.Single(endpoints).Params,
            parameter => parameter.Source == ParamSource.Route
        );
        Assert.Equal(new TsType.Primitive("string", "uuid"), parameter.Type);
    }

    [Fact]
    public void Declared_Path_Parameter_Declares_A_Token_Its_Input_Lacks()
    {
        // The route-and-query lowering used to warn RIV1019 even though the
        // .Parameter<T>() declaration typed the token.
        var source = Contract(
            "RouteDefinition<PageInput, ThingDto> List = Define.Get<PageInput, ThingDto>(\"/declared-path-things/{ownerId:guid}\").Parameter<Guid>(\"ownerId\", \"path\", true)",
            "[RivetType] public sealed record PageInput(int Page);"
        );

        IReadOnlyList<TsEndpointDefinition> endpoints = [];
        var stderr = CompilationHelper.CaptureStdErr(() =>
            endpoints = CompilationHelper.WalkContract(source).Endpoints
        );

        Assert.DoesNotContain("declared-path-things", stderr);
        var parameter = Assert.Single(
            Assert.Single(endpoints).Params,
            parameter => parameter.Source == ParamSource.Route
        );
        Assert.Equal("ownerId", parameter.Name);
        Assert.Equal(new TsType.Primitive("string", "uuid"), parameter.Type);
    }

    [Fact]
    public void Controller_Action_Constraint_Narrows_The_Param()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [Route("api/things")]
            public sealed class ThingsController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("{id:int:min(1)}")]
                public ThingDto Get(int id) => new("x");
            }
            """;

        var endpoint = Assert.Single(CompilationHelper.WalkMerged(source).Endpoints);

        Assert.Equal("/api/things/{id}", endpoint.RouteTemplate);
        var parameter = Assert.Single(endpoint.Params);
        Assert.Equal(new TsType.Primitive("number", "int32"), parameter.Type);
        Assert.Equal(1, parameter.Constraints?.Minimum);
    }

    [Fact]
    public void Controller_Action_Constraint_Contradicting_The_Param_Is_RIV1125()
    {
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [Route("api/things")]
            public sealed class ThingsController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("{id:int}")]
                public ThingDto Get(string id) => new(id);
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkMerged(source)
        );

        Assert.Contains(Diagnostics.RouteConstraintContradictsType, exception.Message);
        Assert.Contains("action parameter 'id'", exception.Message);
        Assert.Contains("ThingsController.Get", exception.Message);
    }

    [Fact]
    public void Controller_Regex_Class_Is_Read_With_MVC_Bracket_Escapes()
    {
        // An MVC attribute route writes a regex class [[a-z]], since a bare [ starts a
        // token; routing reads [a-z].
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [Route("api/[controller]")]
            public sealed class CodesController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("{code:regex(^[[a-z]]{{2}}$)}")]
                public ThingDto Get(string code) => new(code);
            }
            """;

        var endpoint = Assert.Single(CompilationHelper.WalkMerged(source).Endpoints);

        Assert.Equal("/api/Codes/{code}", endpoint.RouteTemplate);
        Assert.Equal("^[a-zA-Z]{2}$", Assert.Single(endpoint.Params).Constraints?.Pattern);
    }

    private static string AttributeRoute(string classAttributes, string route) =>
        $$"""
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            {{classAttributes}}
            public sealed class ThingsController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("{{route}}")]
                public ThingDto Get() => new("x");
            }
            """;

    [Theory]
    // [area] takes the [Area]'s value, and every token is matched ignoring case.
    [InlineData("[Area(\"Admin\")]", "api/[area]/[Controller]/[ACTION]", "/api/Admin/Things/Get")]
    [InlineData("", "api/[controller]/x[[y]]", "/api/Things/x[y]")]
    public void Controller_Route_Tokens_Are_Replaced_As_MVC_Does(
        string classAttributes,
        string route,
        string expected
    )
    {
        var endpoint = Assert.Single(
            CompilationHelper.WalkMerged(AttributeRoute(classAttributes, route)).Endpoints
        );

        Assert.Equal(expected, endpoint.RouteTemplate);
    }

    [Theory]
    // A token the action has no value for (no [Area], or a typo), and an unclosed bracket:
    // MVC refuses each at startup.
    [InlineData("api/[area]/things")]
    [InlineData("api/[controler]")]
    [InlineData("api/[controller")]
    public void Controller_Route_MVC_Refuses_Is_RIV1125(string route)
    {
        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkMerged(AttributeRoute("", route))
        );

        Assert.Contains(Diagnostics.RouteConstraintContradictsType, exception.Message);
        Assert.Contains("MVC refuses it at startup", exception.Message);
        Assert.Contains("ThingsController.Get", exception.Message);
    }

    private static string UnboundControllerToken(string token) =>
        $$"""
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [Route("api/unbound-controller-things")]
            public sealed class ThingsController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("{{token}}")]
                public ThingDto Get() => new("x");
            }
            """;

    [Fact]
    public void Controller_Token_No_Action_Parameter_Binds_Is_Declared_By_Its_Constraint()
    {
        // OpenAPI needs every templated parameter declared; the action ignoring the value
        // doesn't take it out of the path.
        var endpoints = CompilationHelper
            .WalkMerged(UnboundControllerToken("{slug:guid}"))
            .Endpoints;

        var parameter = Assert.Single(Assert.Single(endpoints).Params);
        Assert.Equal(("slug", ParamSource.Route), (parameter.Name, parameter.Source));
        Assert.Equal(new TsType.Primitive("string", "uuid"), parameter.Type);
    }

    [Fact]
    public void Controller_Token_Nothing_Declares_Stops_Generation()
    {
        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkMerged(UnboundControllerToken("{slug}"))
        );

        Assert.StartsWith(
            $"error {Diagnostics.RouteTokenUndeclared}: route token '{{slug}}' on "
                + "/api/unbound-controller-things/{slug} (endpoint 'ThingsController.Get') has no "
                + "action parameter",
            exception.Message
        );
    }

    [Theory]
    [InlineData("{id}", "id", 0, false, null, false)]
    [InlineData("{id?}", "id", 0, true, null, false)]
    [InlineData("{page=1}", "page", 0, false, "1", false)]
    [InlineData("{id:int:min(1)?}", "id", 2, true, null, false)]
    [InlineData("{id:int=5}", "id", 1, false, "5", false)]
    [InlineData("{*path:regex(^a(b)?$)}", "path", 1, false, null, true)]
    public void Route_Tokens_Parse_As_Routing_Reads_Them(
        string template,
        string name,
        int constraintCount,
        bool optional,
        string? defaultValue,
        bool catchAll
    )
    {
        var token = Assert.Single(RouteParser.ParseRouteTokens($"/x/{template}"));

        Assert.Equal(name, token.Name);
        Assert.Equal(constraintCount, token.Constraints.Count);
        Assert.Equal(optional, token.IsOptional);
        Assert.Equal(defaultValue, token.DefaultValue);
        Assert.Equal(catchAll, token.IsCatchAll);
    }

    [Theory]
    [InlineData(@"^\d{4}$", @"^\d{4}$")]
    [InlineData("^ab$", "^[aA][bB]$")]
    [InlineData("^[a-f0-9]+$", "^[a-fA-F0-9]+$")]
    [InlineData("^[a-z0-9_-]+$", "^[a-zA-Z0-9_-]+$")]
    [InlineData("[a-z.-]", "[a-zA-Z.-]")]
    [InlineData("[ab-]", "[aAbB-]")]
    [InlineData("[-a]", "[-aA]")]
    [InlineData("[]a]", "[]aA]")]
    [InlineData("^[^a-z]$", "^[^a-zA-Z]$")]
    [InlineData(@"^\d{3}-[A-Z]{2}$", @"^\d{3}-[A-Za-z]{2}$")]
    [InlineData(@"^(?<year>\d{4})-x$", @"^(?<year>\d{4})-[xX]$")]
    [InlineData(@"^(?:\x4a|\p{Lu})$", @"^(?:\x4a|\p{Lu})$")]
    public void Regex_Pattern_Matches_Letters_In_Either_Case_As_Routing_Does(
        string pattern,
        string expected
    )
    {
        // ASP.NET compiles a regex constraint with IgnoreCase; a JSON Schema pattern has
        // no flags, so the letters are folded into the pattern itself.
        Assert.Equal(expected, RouteConstraints.IgnoringCase(pattern));
    }

    [Theory]
    [InlineData(@"^\d{4}$")]
    [InlineData("^ab$")]
    [InlineData("^[a-f0-9]+$")]
    [InlineData("^(a|b)?[0-9]+$")]
    [InlineData("^[^a-z]$")]
    [InlineData(@"^(?<year>\d{4})-x$")]
    [InlineData("^[a-z0-9_-]+$")]
    [InlineData("^[a-z.-]$")]
    [InlineData("^[ab-]$")]
    [InlineData("^[-a]$")]
    [InlineData("^[]a]$")]
    [InlineData("^[^a-z_-]$")]
    [InlineData("^[A-z]$")]
    [InlineData("^[0-z]$")]
    [InlineData("^[a-~]$")]
    [InlineData(@"^\d{3}-[A-Z]{2}$")]
    public void Folded_Pattern_Matches_What_Routing_Matches(string pattern)
    {
        // The folded pattern, run with no flags, must admit exactly what routing's own regex
        // constraint (compiled with IgnoreCase) admits: a trailing or leading '-' stays a
        // literal.
        var folded = new Regex(RouteConstraints.IgnoringCase(pattern));
        var routing = new Microsoft.AspNetCore.Routing.Constraints.RegexRouteConstraint(
            pattern
        ).Constraint;
        string[] samples =
        [
            "123-AB",
            "123-ab",
            "abc-123_x",
            "ABC-123_X",
            "a-b",
            "AB-",
            "-",
            "2024-X",
            "2024-x",
            "B42",
            "Fe09",
        ];
        var inputs = samples.Concat(Enumerable.Range(0x20, 0x5F).Select(c => ((char)c).ToString()));

        foreach (var input in inputs)
        {
            Assert.True(
                routing.IsMatch(input) == folded.IsMatch(input),
                $"'{input}': routing {routing.IsMatch(input)}, folded {folded} {folded.IsMatch(input)}"
            );
        }
    }

    [Fact]
    public void Regex_Argument_Keeps_Its_Own_Parentheses()
    {
        var token = Assert.Single(RouteParser.ParseRouteTokens("/x/{*path:regex(^a(b)?$)}"));

        Assert.Equal(new RouteConstraint("regex", "^a(b)?$"), Assert.Single(token.Constraints));
    }
}
