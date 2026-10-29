using Rivet.Tool;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ContractEndpointTests
{
    private static IReadOnlyList<TsEndpointDefinition> Generate(string source) =>
        CompilationHelper.WalkContract(source).Endpoints;

    [Fact]
    public void A_JSON_body_declared_only_by_its_request_content_lists_its_field_names()
    {
        // Decision 32: --check holds a Functions parameter to the declared JSON body's
        // top-level names. A body .RequestContent<T>() declares lowers no param, but its
        // names are just as closed; read-only members are never sent.
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record NoteBody(string Title, string? Body)
            {
                [RivetReadOnly]
                public string? Slug { get; init; }
            }

            [RivetType]
            public sealed record NoteDto(string Id);

            [RivetContract]
            public static class NotesContract
            {
                public static readonly RouteDefinition<NoteDto> Create =
                    Define.Post<NoteDto>("/api/declared-notes")
                        .RequestContent<NoteBody>("application/json");
            }
            """;

        var endpoint = Assert.Single(Generate(source));

        Assert.Equal(["title", "body"], endpoint.BodyFieldNames);
    }

    [Fact]
    public void A_request_content_body_keeps_a_name_the_route_binds_from_the_input()
    {
        // The emitter writes .RequestContent<T>() as T's whole schema, whatever the route
        // binds from TInput, so a T member sharing a route-bound input member's name is still
        // a body field.
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record NoteInput(Guid ItemId);

            [RivetType]
            public sealed record NoteBody(Guid ItemId, string Title);

            [RivetType]
            public sealed record NoteDto(string Id);

            [RivetContract]
            public static class NotesContract
            {
                public static readonly RouteDefinition<NoteInput, NoteDto> Create =
                    Define.Post<NoteInput, NoteDto>("/api/items/{item_id}/notes")
                        .RequestContent<NoteBody>("application/json");
            }
            """;

        var endpoint = Assert.Single(Generate(source));

        Assert.Contains(
            endpoint.Params,
            param => param is { Name: "item_id", Source: ParamSource.Route }
        );
        Assert.Equal(["itemId", "title"], endpoint.BodyFieldNames);
    }

    [Fact]
    public void Nested_Contract_Class_Is_Discovered()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            public static class Api
            {
                [RivetContract]
                public static class TasksContract
                {
                    public static readonly RouteDefinition<TaskDto> GetTask =
                        Define.Get<TaskDto>("/api/tasks");
                }
            }
            """;

        var endpoint = Assert.Single(Generate(source));
        Assert.Equal("getTask", endpoint.Name);
        Assert.Equal("/api/tasks", endpoint.RouteTemplate);
    }

    [Fact]
    public void User_Extension_Method_In_Chain_Is_Refused()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetType]
            public sealed record ErrorDto(string Message);

            public static class RouteExtensions
            {
                public static RouteDefinition<TaskDto> Returns<TError>(
                    this RouteDefinition<TaskDto> route,
                    int statusCode,
                    bool logged) => route;
            }

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks").Returns<ErrorDto>(404, logged: true);
            }
            """;

        var error = Assert.Throws<RivetUserException>(() => Generate(source));
        Assert.Contains("RIV1113", error.Message);
        Assert.Contains("GetTask", error.Message);
        Assert.Contains("not a Rivet builder method", error.Message);
    }

    [Theory]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Routes.Get<TaskDto>(\"/api/fake\");",
        "isn't initialised with a Define.* builder chain",
        "Initialise the field with Define.*"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(Routes.Prefix + \"/tasks\");",
        "isn't a compile-time constant",
        "Use a literal or a const"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask; static TasksContract() { GetTask = Define.Get<TaskDto>(\"/api/tasks\"); }",
        "has no initializer",
        "Initialise the field with Define.*"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(\"/api/tasks\").Secure(Routes.Prefix);",
        "passes .Secure() a 'scheme' that isn't a compile-time constant",
        "Use a literal or a const"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(\"/api/tasks\").Summary(Routes.Prefix);",
        "passes .Summary() a 'summary' that isn't a compile-time constant",
        "Use a literal or a const"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(\"/api/tasks\").Status(Routes.Code);",
        "passes .Status() a 'statusCode' that isn't a compile-time constant",
        "Use a literal or a const"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(\"/api/tasks\").QueryAuth(Routes.Prefix);",
        "passes .QueryAuth() a 'parameterName' that isn't a compile-time constant",
        "Use a literal or a const"
    )]
    [InlineData(
        "public static readonly FileRouteDefinition GetTask = Define.File(\"/api/tasks\").ProducesFile(Routes.Prefix);",
        "passes .ProducesFile() a 'contentType' that isn't a compile-time constant",
        "Use a literal or a const"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(\"/api/tasks\").Secure(\"oauth\").AndSecure(\"key\", \"read\", Routes.Prefix);",
        "passes .AndSecure() scopes that aren't compile-time constant strings",
        "literal or const strings"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(\"/api/tasks\").Secure(\"oauth\", Routes.Scopes);",
        "passes .Secure() scopes that aren't compile-time constant strings",
        "literal or const strings"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(null!);",
        "has a null route",
        "Pass a route template"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(\"/api/tasks\").ResponseExampleJson(200, null);",
        "passes .ResponseExampleJson() an example with nothing in it",
        "Give the example at least one of them"
    )]
    [InlineData(
        "public static readonly RouteDefinition<TaskDto> GetTask = Define.Get<TaskDto>(\"/api/tasks\").RequestExampleJson(null, name: \"empty\");",
        "passes .RequestExampleJson() an example with nothing in it",
        "Give the example at least one of them"
    )]
    [InlineData(
        "public static RouteDefinition<TaskDto> GetTask { get; } = Define.Get<TaskDto>(\"/api/tasks\");",
        "is a property",
        "'public static readonly' field"
    )]
    public void Unreadable_Endpoint_Field_Is_Refused(string field, string reason, string remedy)
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            public static class Routes
            {
                public static string Prefix = "/api";
                public static int Code = 201;
                public static string[] Scopes = ["read"];
                public static RouteDefinition<TaskDto> Get<T>(string route) => Define.Get<TaskDto>("/api/real");
            }

            [RivetContract]
            public static class TasksContract
            {
                {{field}}
            }
            """;

        var error = Assert.Throws<RivetUserException>(() => Generate(source));
        Assert.Contains("error RIV1113", error.Message);
        Assert.Contains("TasksContract.GetTask", error.Message);
        Assert.Contains(reason, error.Message);
        Assert.Contains(remedy, error.Message);
    }

    [Theory]
    [InlineData(".Anonymous().Secure(\"oauth\")", ".Anonymous() declares")]
    [InlineData(".OrAnonymous().Anonymous()", ".Anonymous() declares")]
    [InlineData(".AndSecure(\"oauth\")", ".AndSecure() must follow .Secure()")]
    [InlineData(
        ".Secure(\"oauth\").OrAnonymous().AndSecure(\"key\")",
        ".AndSecure() must follow .Secure()"
    )]
    public void Contradictory_Security_Calls_Are_Refused(string calls, string reason)
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks"){{calls}};
            }
            """;

        var error = Assert.Throws<RivetUserException>(() => Generate(source));
        Assert.Contains("error RIV1114", error.Message);
        Assert.Contains("TasksContract.GetTask", error.Message);
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void Chain_Reads_Named_And_Default_Arguments()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetType]
            public sealed record ErrorDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>(route: "/api/tasks")
                        .Returns<ErrorDto>(description: "Missing", statusCode: 404)
                        .WithResponseHeader<int>(required: true, name: "X-Rate")
                        .QueryAuth();

                public static readonly FileRouteDefinition Download =
                    Define.File("/api/files").ProducesFile();
            }
            """;

        var endpoints = Generate(source);
        var getTask = endpoints.Single(e => e.Name == "getTask");
        Assert.Equal("/api/tasks", getTask.RouteTemplate);
        var notFound = getTask.Responses.Single(r => r.StatusCode == 404);
        Assert.Equal("Missing", notFound.Description);
        Assert.True(notFound.DataType is TsType.TypeRef { Name: "ErrorDto" });
        var header = Assert.Single(getTask.Responses.Single(r => r.StatusCode == 200).Headers!);
        Assert.Equal("X-Rate", header.Name);
        Assert.True(header.Required);
        Assert.Equal("token", getTask.QueryAuth?.ParameterName);
        Assert.Equal(
            "application/octet-stream",
            endpoints.Single(e => e.Name == "download").FileContentType
        );
    }

    [Fact]
    public void Included_Fields_Are_Required_Whether_Or_Not_Nullable()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed class NoteDto
            {
                [JsonInclude]
                public required string? Text;

                [JsonInclude]
                public string? Tag;
            }

            [RivetContract]
            public static class NotesContract
            {
                public static readonly RouteDefinition<NoteDto> Get = Define.Get<NoteDto>("/api/notes");
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);
        var properties = walker.Definitions["NoteDto"].Properties;
        Assert.False(properties.Single(p => p.Name == "text").IsOptional);
        Assert.False(properties.Single(p => p.Name == "tag").IsOptional);
    }

    [Fact]
    public void Get_WithInputAndOutput_RouteAndQueryParams()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record GetTaskInput(string Id, string Status, int Page);

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<GetTaskInput, TaskDto> GetTask =
                    Define.Get<GetTaskInput, TaskDto>("/api/tasks/{id}");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("getTask", ep.Name);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Equal("/api/tasks/{id}", ep.RouteTemplate);
        Assert.Equal("tasks", ep.ControllerName);

        // id matched to route, status + page are query
        Assert.Equal(3, ep.Params.Count);
        var idParam = ep.Params.First(p => p.Name == "id");
        Assert.Equal(ParamSource.Route, idParam.Source);
        Assert.True(idParam.Type is TsType.Primitive { Name: "string" });

        var statusParam = ep.Params.First(p => p.Name == "status");
        Assert.Equal(ParamSource.Query, statusParam.Source);
        Assert.True(statusParam.Type is TsType.Primitive { Name: "string" });

        var pageParam = ep.Params.First(p => p.Name == "page");
        Assert.Equal(ParamSource.Query, pageParam.Source);
        Assert.True(pageParam.Type is TsType.Primitive { Name: "number", Format: "int32" });

        Assert.True(ep.ReturnType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Equal(200, ep.Responses[0].StatusCode);
        Assert.True(ep.Responses[0].DataType is TsType.TypeRef { Name: "TaskDto" });
    }

    [Fact]
    public void Post_WithBody_RouteParamsSeparate()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateCommentInput(string Text);

            [RivetType]
            public sealed record CommentDto(string Id, string Text);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<CreateCommentInput, CommentDto> CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId:guid}/comments");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("POST", ep.HttpMethod);

        // taskId from route template as standalone string param, body from TInput
        var routeParam = ep.Params.First(p => p.Source == ParamSource.Route);
        Assert.Equal("taskId", routeParam.Name);
        Assert.True(routeParam.Type is TsType.Primitive { Name: "string" });

        var bodyParam = ep.Params.First(p => p.Source == ParamSource.Body);
        Assert.Equal("body", bodyParam.Name);
        Assert.True(bodyParam.Type is TsType.TypeRef { Name: "CreateCommentInput" });

        Assert.True(ep.ReturnType is TsType.TypeRef { Name: "CommentDto" });
    }

    [Fact]
    public void Post_InputRouteDefinition_MidPathRouteParam_NoMatchingProp()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemRequest(string Path);

            [RivetContract]
            public static class TestContract
            {
                public static readonly InputRouteDefinition<ItemRequest> AddItem = Define
                    .Post("/api/collections/{id:guid}/items")
                    .Accepts<ItemRequest>()
                    .Status(204);
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("POST", ep.HttpMethod);

        // {id} from route template as standalone string param, body from TInput
        var routeParam = ep.Params.First(p => p.Source == ParamSource.Route);
        Assert.Equal("id", routeParam.Name);
        Assert.True(routeParam.Type is TsType.Primitive { Name: "string" });

        var bodyParam = ep.Params.First(p => p.Source == ParamSource.Body);
        Assert.Equal("body", bodyParam.Name);
        Assert.True(bodyParam.Type is TsType.TypeRef { Name: "ItemRequest" });

        Assert.Equal("/api/collections/{id}/items", ep.RouteTemplate);
    }

    [Fact]
    public void Delete_InputRouteDefinition_MidPathRouteParam_NoMatchingProp()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemRequest(string Path);

            [RivetContract]
            public static class TestContract
            {
                public static readonly InputRouteDefinition<ItemRequest> RemoveItem = Define
                    .Delete("/api/collections/{id:guid}/items")
                    .Accepts<ItemRequest>();
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("DELETE", ep.HttpMethod);

        // {id} from route as standalone string param, Path from TInput as query
        var routeParam = ep.Params.First(p => p.Source == ParamSource.Route);
        Assert.Equal("id", routeParam.Name);
        Assert.True(routeParam.Type is TsType.Primitive { Name: "string" });

        var queryParam = ep.Params.First(p => p.Source == ParamSource.Query);
        Assert.Equal("path", queryParam.Name);

        Assert.Equal("/api/collections/{id}/items", ep.RouteTemplate);
    }

    [Fact]
    public void Post_RequestExampleJson_Defaults_To_Json_MediaType()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateCommentInput(string Text);

            [RivetType]
            public sealed record CommentDto(string Id, string Text);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<CreateCommentInput, CommentDto> CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId:guid}/comments")
                        .RequestExampleJson("{\"text\":\"hello\"}");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var requestExample = Assert.Single(ep.RequestExamples!);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Equal("""{"text":"hello"}""", requestExample.Json);
        Assert.Null(requestExample.Name);
        Assert.Null(requestExample.ComponentExampleId);
        Assert.Null(requestExample.ResolvedJson);
    }

    [Fact]
    public void ResponseExampleJson_Attaches_To_NonSuccess_Response()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record ValidationProblemDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Returns<ValidationProblemDto>(422)
                        .ResponseExampleJson(422, "{\"message\":\"Validation failed\"}", name: "validationProblem");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 422);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("validationProblem", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Equal("""{"message":"Validation failed"}""", example.Json);
        Assert.Null(example.ComponentExampleId);
        Assert.Null(example.ResolvedJson);
    }

    [Fact]
    public void ResponseExampleJson_Before_Returns_Still_Attaches_To_NonSuccess_Response()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record ValidationProblemDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .ResponseExampleJson(422, "{\"message\":\"Validation failed\"}", name: "validationProblem")
                        .Returns<ValidationProblemDto>(422);
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 422);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("validationProblem", example.Name);
        Assert.Equal("""{"message":"Validation failed"}""", example.Json);
    }

    [Fact]
    public void ResponseExampleJson_Without_Declared_Response_Is_Refused()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .ResponseExampleJson(422, "{\"message\":\"Validation failed\"}", name: "validationProblem");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() => Generate(source));

        Assert.StartsWith(
            "error RIV1002: contract endpoint 'getTask' has a response example for status 422",
            exception.Message
        );
        Assert.Contains(".Returns(...)", exception.Message);
    }

    // On .AcceptsBinary() the body is taken by the payload, so RIV1020 must not suggest
    // sending the input as a JSON body.
    [Fact]
    public void Unlowerable_Input_Beside_A_Binary_Body_Names_The_Payload()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly RouteDefinition<string, ThingDto> Thing =
                    Define.Post<string, ThingDto>("/things").AcceptsBinary();
            }
            """;

        var message = Assert.Throws<RivetUserException>(() => Generate(source)).Message;

        Assert.StartsWith("error RIV1020:", message);
        Assert.Contains("the request body carries the binary payload", message);
        Assert.DoesNotContain("JSON body", message);
    }

    // .ResponseContent(...) declares its status on its own, so an example or a header may
    // target a status declared no other way. Without a mediaType the example takes the
    // status's one declared media type rather than application/json.
    [Theory]
    [InlineData(", mediaType: \"application/problem+json\"")]
    [InlineData("")]
    public void Examples_And_Headers_Reach_A_Status_Declared_Only_By_ResponseContent(
        string mediaTypeArgument
    )
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetType]
            public sealed record Problem(string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .ResponseContent<Problem>(404, "application/problem+json")
                        .ResponseExampleJson(404, "{\"title\":\"Not found\"}"{{mediaTypeArgument}})
                        .WithResponseHeader(404, "X-Trace-Id");
            }
            """;

        var notFound = Assert.Single(
            Assert.Single(Generate(source)).Responses,
            response => response.StatusCode == 404
        );
        Assert.Equal("application/problem+json", Assert.Single(notFound.Contents!).MediaType);
        var example = Assert.Single(notFound.Examples!);
        Assert.Equal("{\"title\":\"Not found\"}", example.Json);
        Assert.Equal("application/problem+json", example.MediaType);
        Assert.Equal("X-Trace-Id", Assert.Single(notFound.Headers!).Name);
    }

    [Fact]
    public void Success_Example_Defaults_To_The_ProducesContentType_Media_Type()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Problem(string Title);

            [RivetContract]
            public static class PagesContract
            {
                public static readonly RouteDefinition<string> GetPage =
                    Define.Get<string>("/api/page")
                        .ProducesContentType("text/html")
                        .Returns<Problem>(422)
                        .ResponseExampleJson(200, "\"<p>hi</p>\"")
                        .ResponseExampleJson(422, "{\"title\":\"Bad\"}");
            }
            """;

        var responses = Assert.Single(Generate(source)).Responses;

        var ok = responses.Single(response => response.StatusCode == 200);
        Assert.Equal("text/html", Assert.Single(ok.Examples!).MediaType);
        var problem = responses.Single(response => response.StatusCode == 422);
        Assert.Equal("application/json", Assert.Single(problem.Examples!).MediaType);
    }

    // An example under a media type its status doesn't declare would add an undeclared
    // content entry; several declared media types leave a bare example ambiguous.
    [Theory]
    [InlineData("", ", mediaType: \"application/json\"", "which that status does not declare")]
    [InlineData(
        ".ResponseContent<Problem>(404, \"application/xml\")",
        "",
        "without a media type, and that status declares several"
    )]
    public void Response_Example_Must_Name_A_Media_Type_Its_ResponseContent_Status_Declares(
        string extraContent,
        string mediaTypeArgument,
        string expected
    )
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetType]
            public sealed record Problem(string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .ResponseContent<Problem>(404, "application/problem+json")
                        {{extraContent}}
                        .ResponseExampleJson(404, "{\"title\":\"Not found\"}"{{mediaTypeArgument}});
            }
            """;

        var message = Assert.Throws<RivetUserException>(() => Generate(source)).Message;

        Assert.StartsWith("error RIV1002: contract endpoint 'getTask'", message);
        Assert.Contains(expected, message);
    }

    [Theory]
    [InlineData("public static class", "public static RouteDefinition<TaskDto>")]
    [InlineData("public sealed class", "public readonly RouteDefinition<TaskDto>")]
    public void Endpoint_Field_That_Is_Not_Static_Readonly_Is_Refused(
        string classDeclaration,
        string fieldDeclaration
    )
    {
        var source = $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            {{classDeclaration}} TasksContract
            {
                {{fieldDeclaration}} GetTask = Define.Get<TaskDto>("/api/tasks/{id:guid}");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() => Generate(source));

        Assert.StartsWith(
            "error RIV1001: contract endpoint 'TasksContract.GetTask' is not 'static readonly'",
            exception.Message
        );
        Assert.Contains("Declare it 'public static readonly'", exception.Message);
    }

    [Fact]
    public void ResponseExampleJson_Multiple_Examples_For_Same_Status_Are_Preserved_In_Order()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record ValidationProblemDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Returns<ValidationProblemDto>(422)
                        .ResponseExampleJson(422, "{\"message\":\"Validation failed\"}", name: "validationProblem")
                        .ResponseExampleJson(422, "{\"message\":\"Already archived\"}", name: "archivedProblem");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 422);
        var examples = Assert.IsAssignableFrom<IReadOnlyList<TsEndpointExample>>(response.Examples);
        Assert.Equal(2, examples.Count);
        Assert.Equal("validationProblem", examples[0].Name);
        Assert.Equal("""{"message":"Validation failed"}""", examples[0].Json);
        Assert.Equal("archivedProblem", examples[1].Name);
        Assert.Equal("""{"message":"Already archived"}""", examples[1].Json);
    }

    [Fact]
    public void Example_MediaType_Override_Is_Preserved()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record ValidationProblemDto(string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Returns<ValidationProblemDto>(422)
                        .ResponseExampleJson(
                            422,
                            "{\"title\":\"Bad request\"}",
                            name: "problem",
                            mediaType: "application/problem+json");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 422);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("problem", example.Name);
        Assert.Equal("application/problem+json", example.MediaType);
        Assert.Equal("""{"title":"Bad request"}""", example.Json);
    }

    [Fact]
    public void ResponseExampleRef_Preserves_ComponentRef_And_ResolvedJson()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record ValidationProblemDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Returns<ValidationProblemDto>(422)
                        .ResponseExampleRef(
                            422,
                            "validation-problem",
                            "{\"message\":\"Validation failed\"}",
                            name: "validationProblem");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 422);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("validationProblem", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Null(example.Json);
        Assert.Equal("validation-problem", example.ComponentExampleId);
        Assert.Equal("""{"message":"Validation failed"}""", example.ResolvedJson);
    }

    [Fact]
    public void ResponseExampleRef_MediaType_Override_Is_Preserved()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record ProblemDto(string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Returns<ProblemDto>(422)
                        .ResponseExampleRef(
                            422,
                            "problem-example",
                            "{\"title\":\"Bad request\"}",
                            name: "problem",
                            mediaType: "application/problem+json");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 422);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("problem", example.Name);
        Assert.Equal("application/problem+json", example.MediaType);
        Assert.Null(example.Json);
        Assert.Equal("problem-example", example.ComponentExampleId);
        Assert.Equal("""{"title":"Bad request"}""", example.ResolvedJson);
    }

    [Fact]
    public void RequestExampleRef_Preserves_ComponentRef_And_ResolvedJson()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateCommentInput(string Text);

            [RivetType]
            public sealed record CommentDto(string Id, string Text);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<CreateCommentInput, CommentDto> CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId:guid}/comments")
                        .RequestExampleRef(
                            "create-comment",
                            "{\"text\":\"hello\"}",
                            name: "commentExample");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("commentExample", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Null(example.Json);
        Assert.Equal("create-comment", example.ComponentExampleId);
        Assert.Equal("""{"text":"hello"}""", example.ResolvedJson);
    }

    [Fact]
    public void EndpointExamples_Survive_Walk_Emit_Read_RoundTrip_From_ContractDsl()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginRequest(string Email, string Password);

            [RivetType]
            public sealed record TokenDto(string Token);

            [RivetType]
            public sealed record ProblemDto(string Title);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition<LoginRequest, TokenDto> Login =
                    Define.Post<LoginRequest, TokenDto>("/api/auth/login")
                        .FormEncoded()
                        .RequestExampleRef(
                            "login-request",
                            "{\"email\":\"ada@example.com\",\"password\":\"secret\"}",
                            name: "loginRequest")
                        .Returns<ProblemDto>(422)
                        .ResponseExampleRef(
                            422,
                            "login-problem",
                            "{\"title\":\"Bad credentials\"}",
                            name: "badCredentials");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var definitions = walker.Definitions.ToDictionary(kv => kv.Key, kv => kv.Value);
        var enums = walker.Enums.ToDictionary(kv => kv.Key, kv => kv.Value);
        var json = ContractEmitter.Emit(definitions, enums, endpoints);
        var result = JsonContractReader.Read(json);

        var endpoint = Assert.Single(result.Endpoints);
        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("loginRequest", requestExample.Name);
        Assert.Equal("application/x-www-form-urlencoded", requestExample.MediaType);
        Assert.Null(requestExample.Json);
        Assert.Equal("login-request", requestExample.ComponentExampleId);
        Assert.Equal(
            """{"email":"ada@example.com","password":"secret"}""",
            requestExample.ResolvedJson
        );

        var response = endpoint.Responses.First(r => r.StatusCode == 422);
        var responseExample = Assert.Single(response.Examples!);
        Assert.Equal("badCredentials", responseExample.Name);
        Assert.Equal("application/json", responseExample.MediaType);
        Assert.Null(responseExample.Json);
        Assert.Equal("login-problem", responseExample.ComponentExampleId);
        Assert.Equal("""{"title":"Bad credentials"}""", responseExample.ResolvedJson);
    }

    [Fact]
    public void RequestExamples_Multiple_Entries_Are_Preserved_In_Order()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateCommentInput(string Text);

            [RivetType]
            public sealed record CommentDto(string Id, string Text);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<CreateCommentInput, CommentDto> CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId:guid}/comments")
                        .RequestExampleJson("{\"text\":\"hello\"}", name: "inline")
                        .RequestExampleRef("create-comment", "{\"text\":\"bonjour\"}", name: "refBacked");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var requestExamples = Assert.IsAssignableFrom<IReadOnlyList<TsEndpointExample>>(
            ep.RequestExamples
        );
        Assert.Equal(2, requestExamples.Count);

        Assert.Equal("inline", requestExamples[0].Name);
        Assert.Equal("""{"text":"hello"}""", requestExamples[0].Json);
        Assert.Null(requestExamples[0].ComponentExampleId);
        Assert.Null(requestExamples[0].ResolvedJson);

        Assert.Equal("refBacked", requestExamples[1].Name);
        Assert.Null(requestExamples[1].Json);
        Assert.Equal("create-comment", requestExamples[1].ComponentExampleId);
        Assert.Equal("""{"text":"bonjour"}""", requestExamples[1].ResolvedJson);
    }

    [Fact]
    public void RequestExampleJson_MediaType_Override_Is_Preserved()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateCommentInput(string Text);

            [RivetType]
            public sealed record CommentDto(string Id, string Text);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<CreateCommentInput, CommentDto> CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId:guid}/comments")
                        .RequestExampleJson(
                            "{\"text\":\"hello\"}",
                            name: "commentExample",
                            mediaType: "application/problem+json");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("commentExample", example.Name);
        Assert.Equal("application/problem+json", example.MediaType);
        Assert.Equal("""{"text":"hello"}""", example.Json);
    }

    [Fact]
    public void RequestExampleRef_MediaType_Override_Is_Preserved()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateCommentInput(string Text);

            [RivetType]
            public sealed record CommentDto(string Id, string Text);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<CreateCommentInput, CommentDto> CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId:guid}/comments")
                        .RequestExampleRef(
                            "create-comment",
                            "{\"text\":\"hello\"}",
                            name: "commentExample",
                            mediaType: "application/problem+json");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("commentExample", example.Name);
        Assert.Equal("application/problem+json", example.MediaType);
        Assert.Null(example.Json);
        Assert.Equal("create-comment", example.ComponentExampleId);
        Assert.Equal("""{"text":"hello"}""", example.ResolvedJson);
    }

    [Fact]
    public void RequestExampleJson_On_ImplicitMultipartInput_Defaults_To_MultipartFormData()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadInput(IFormFile Document, string Title);

            [RivetType]
            public sealed record UploadResultDto(string Id);

            [RivetContract]
            public static class UploadsContract
            {
                public static readonly RouteDefinition<UploadInput, UploadResultDto> Upload =
                    Define.Post<UploadInput, UploadResultDto>("/api/uploads")
                        .RequestExampleJson("{\"title\":\"Quarterly report\"}");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("multipart/form-data", example.MediaType);
        Assert.Equal("""{"title":"Quarterly report"}""", example.Json);
    }

    [Fact]
    public void RequestExampleJson_On_FormEncoded_Input_Defaults_To_FormUrlEncoded()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginInput(string Email, string Password);

            [RivetType]
            public sealed record TokenDto(string Token);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition<LoginInput, TokenDto> Login =
                    Define.Post<LoginInput, TokenDto>("/api/login")
                        .FormEncoded()
                        .RequestExampleJson("{\"email\":\"ada@example.com\",\"password\":\"secret\"}");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("application/x-www-form-urlencoded", example.MediaType);
        Assert.Equal("""{"email":"ada@example.com","password":"secret"}""", example.Json);
    }

    [Fact]
    public void RequestExampleJson_Before_FormEncoded_Defaults_To_FormUrlEncoded()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginInput(string Email, string Password);

            [RivetType]
            public sealed record TokenDto(string Token);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition<LoginInput, TokenDto> Login =
                    Define.Post<LoginInput, TokenDto>("/api/login")
                        .RequestExampleJson("{\"email\":\"ada@example.com\",\"password\":\"secret\"}")
                        .FormEncoded();
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("application/x-www-form-urlencoded", example.MediaType);
        Assert.Equal("""{"email":"ada@example.com","password":"secret"}""", example.Json);
    }

    [Fact]
    public void RequestExampleJson_On_AcceptsFile_Defaults_To_MultipartFormData()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadResultDto(string Id);

            [RivetContract]
            public static class UploadsContract
            {
                public static readonly RouteDefinition<UploadResultDto> Upload =
                    Define.Post<UploadResultDto>("/api/files")
                        .AcceptsFile()
                        .RequestExampleJson("{\"file\":\"ignored\"}");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("multipart/form-data", example.MediaType);
        Assert.Equal("""{"file":"ignored"}""", example.Json);
    }

    [Fact]
    public void RequestExampleJson_Before_AcceptsFile_Defaults_To_MultipartFormData()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadResultDto(string Id);

            [RivetContract]
            public static class UploadsContract
            {
                public static readonly RouteDefinition<UploadResultDto> Upload =
                    Define.Post<UploadResultDto>("/api/files")
                        .RequestExampleJson("{\"file\":\"ignored\"}")
                        .AcceptsFile();
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("multipart/form-data", example.MediaType);
        Assert.Equal("""{"file":"ignored"}""", example.Json);
    }

    [Fact]
    public void RequestExampleRef_On_AcceptsFile_Defaults_To_MultipartFormData()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadResultDto(string Id);

            [RivetContract]
            public static class UploadsContract
            {
                public static readonly RouteDefinition<UploadResultDto> Upload =
                    Define.Post<UploadResultDto>("/api/files")
                        .AcceptsFile()
                        .RequestExampleRef(
                            "upload-example",
                            "{\"file\":\"ignored\"}",
                            name: "upload");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("upload", example.Name);
        Assert.Equal("multipart/form-data", example.MediaType);
        Assert.Null(example.Json);
        Assert.Equal("upload-example", example.ComponentExampleId);
        Assert.Equal("""{"file":"ignored"}""", example.ResolvedJson);
    }

    [Fact]
    public void InputOnly_RequestExamples_And_Untyped_ResponseExamples_Are_Preserved()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginRequest(string Email, string Password);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly InputRouteDefinition<LoginRequest> Login =
                    Define.Delete("/api/auth/session")
                        .Accepts<LoginRequest>()
                        .RequestExampleJson("{\"email\":\"ada@example.com\",\"password\":\"secret\"}", name: "loginRequest")
                        .Returns(422)
                        .ResponseExampleJson(422, "{\"message\":\"Bad credentials\"}", name: "badCredentials");
            }
            """;

        var endpoints = Generate(source);

        var endpoint = Assert.Single(endpoints);
        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("loginRequest", requestExample.Name);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Equal("""{"email":"ada@example.com","password":"secret"}""", requestExample.Json);

        var response = endpoint.Responses.First(r => r.StatusCode == 422);
        Assert.Null(response.DataType);
        var responseExample = Assert.Single(response.Examples!);
        Assert.Equal("badCredentials", responseExample.Name);
        Assert.Equal("application/json", responseExample.MediaType);
        Assert.Equal("""{"message":"Bad credentials"}""", responseExample.Json);
    }

    [Fact]
    public void ResponseExampleJson_Attaches_To_Synthesized_Post_Success_Response()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateOrderInput(string CustomerId);

            [RivetType]
            public sealed record OrderDto(string Id);

            [RivetContract]
            public static class OrdersContract
            {
                public static readonly RouteDefinition<CreateOrderInput, OrderDto> CreateOrder =
                    Define.Post<CreateOrderInput, OrderDto>("/api/orders")
                        .ResponseExampleJson(201, "{\"id\":\"ord_123\"}", name: "created");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 201);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("created", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Equal("""{"id":"ord_123"}""", example.Json);
    }

    [Fact]
    public void ResponseExampleJson_Attaches_To_Status_Overridden_Synthesized_Success_Response()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateOrderInput(string CustomerId);

            [RivetType]
            public sealed record OrderDto(string Id);

            [RivetContract]
            public static class OrdersContract
            {
                public static readonly RouteDefinition<CreateOrderInput, OrderDto> CreateOrder =
                    Define.Post<CreateOrderInput, OrderDto>("/api/orders")
                        .Status(202)
                        .ResponseExampleJson(202, "{\"id\":\"ord_123\"}", name: "accepted");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 202);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("accepted", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Equal("""{"id":"ord_123"}""", example.Json);
    }

    [Fact]
    public void ResponseExampleJson_On_BodyForbidden_204_Fails_Generation()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition DeleteSession =
                    Define.Delete("/api/auth/session")
                        .ResponseExampleJson(204, "{\"message\":\"deleted\"}", name: "deleted");
            }
            """;

        // RIV1102: HTTP forbids a message body on 204 — an authored example there
        // could never reach the wire, so generation fails instead of emitting it.
        // The parse-side guard fires on the shared normalization path every
        // frontend passes through, which is emission — not the walk alone.
        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.EmitOpenApi(source)
        );

        Assert.Contains("RIV1102", exception.Message);
        Assert.Contains("deleteSession", exception.Message);
        Assert.Contains("204", exception.Message);
    }

    [Fact]
    public void ComponentOnly_Ref_Examples_Are_Not_Available_In_Contract_Dsl()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateCommentInput(string Text);

            [RivetType]
            public sealed record CommentDto(string Id, string Text);

            [RivetType]
            public sealed record ValidationProblemDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<CreateCommentInput, CommentDto> CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId:guid}/comments")
                        .RequestExampleRef("create-comment")
                        .Returns<ValidationProblemDto>(422)
                        .ResponseExampleRef(422, "validation-problem");
            }
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            CompilationHelper.CreateCompilation(source)
        );

        Assert.Contains("RequestExampleRef", error.Message);
        Assert.Contains("ResponseExampleRef", error.Message);
    }

    [Fact]
    public void ResponseExampleRef_On_File_Success_Response_Defaults_To_FileContentType()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class DocumentsContract
            {
                public static readonly RouteDefinition Download =
                    Define.Get("/api/documents/{id:guid}")
                        .ProducesFile("application/pdf")
                        .ResponseExampleRef(200, "document-example", "{\"href\":\"/api/documents/123\"}", name: "document");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 200);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("document", example.Name);
        Assert.Equal("application/pdf", example.MediaType);
        Assert.Null(example.Json);
        Assert.Equal("document-example", example.ComponentExampleId);
        Assert.Equal("""{"href":"/api/documents/123"}""", example.ResolvedJson);
    }

    [Fact]
    public void ResponseExampleJson_On_Typed_Second_Success_Of_File_Endpoint_Defaults_To_ApplicationJson()
    {
        // The emitter gives the file's media type to the first 2xx only; a typed 202 is JSON.
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record JobDto(string Id);

            [RivetContract]
            public static class DocumentsContract
            {
                public static readonly RouteDefinition Download =
                    Define.Get("/api/documents/{id:guid}")
                        .ProducesFile("application/pdf")
                        .Returns<JobDto>(202)
                        .ResponseExampleJson(202, "{\"id\":\"job-1\"}", name: "job");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 202);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("application/json", example.MediaType);
        Assert.Equal("""{"id":"job-1"}""", example.Json);
    }

    [Fact]
    public void ResponseExampleJson_On_File_Error_Response_Defaults_To_ApplicationJson()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ProblemDto(string Title);

            [RivetContract]
            public static class DocumentsContract
            {
                public static readonly RouteDefinition Download =
                    Define.Get("/api/documents/{id:guid}")
                        .ProducesFile("application/pdf")
                        .Returns<ProblemDto>(422)
                        .ResponseExampleJson(422, "{\"title\":\"Bad request\"}", name: "problem");
            }
            """;

        var endpoints = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 422);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("problem", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Equal("""{"title":"Bad request"}""", example.Json);
    }

    [Fact]
    public void Get_OutputOnly_NoInput()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> ListTasks =
                    Define.Get<TaskDto>("/api/tasks");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("listTasks", ep.Name);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Empty(ep.Params);
        Assert.True(ep.ReturnType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Single(ep.Responses);
        Assert.Equal(200, ep.Responses[0].StatusCode);
        Assert.True(ep.Responses[0].DataType is TsType.TypeRef { Name: "TaskDto" });
    }

    [Fact]
    public void Delete_NoTypes_RouteParamInferred()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition DeleteTask =
                    Define.Delete("/api/tasks/{id:guid}");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("deleteTask", ep.Name);
        Assert.Equal("DELETE", ep.HttpMethod);

        // Route param inferred from template even without TInput
        Assert.Single(ep.Params);
        Assert.Equal("id", ep.Params[0].Name);
        Assert.Equal(ParamSource.Route, ep.Params[0].Source);
        Assert.True(ep.Params[0].Type is TsType.Primitive { Name: "string" });

        Assert.Null(ep.ReturnType);
    }

    [Fact]
    public void Returns_ProducesResponseEntry()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Returns<NotFoundDto>(404);
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];

        // 200 from TOutput + 404 from .Returns
        Assert.True(ep.ReturnType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Equal(2, ep.Responses.Count);
        Assert.Equal(200, ep.Responses[0].StatusCode);
        Assert.True(ep.Responses[0].DataType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Equal(404, ep.Responses[1].StatusCode);
        Assert.True(ep.Responses[1].DataType is TsType.TypeRef { Name: "NotFoundDto" });
    }

    [Fact]
    public void Status_OverridesSuccessCode()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateInput(string Name);

            [RivetType]
            public sealed record CreatedDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<CreateInput, CreatedDto> CreateTask =
                    Define.Post<CreateInput, CreatedDto>("/api/tasks")
                        .Status(201);
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.ReturnType is TsType.TypeRef { Name: "CreatedDto" });
        Assert.Contains(
            ep.Responses,
            r => r.StatusCode == 201 && r.DataType is TsType.TypeRef { Name: "CreatedDto" }
        );
    }

    [Fact]
    public void MultipleReturns_AllCaptured()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetType]
            public sealed record ConflictDto(string Reason);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Returns<NotFoundDto>(404)
                        .Returns<ConflictDto>(409);
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.ReturnType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Equal(3, ep.Responses.Count);
        Assert.Equal(200, ep.Responses[0].StatusCode);
        Assert.True(ep.Responses[0].DataType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Equal(404, ep.Responses[1].StatusCode);
        Assert.True(ep.Responses[1].DataType is TsType.TypeRef { Name: "NotFoundDto" });
        Assert.Equal(409, ep.Responses[2].StatusCode);
        Assert.True(ep.Responses[2].DataType is TsType.TypeRef { Name: "ConflictDto" });
    }

    [Fact]
    public void ContractName_StripsSuffix_CamelCases()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class CaseStatusesContract
            {
                public static readonly RouteDefinition<ItemDto> GetItem =
                    Define.Get<ItemDto>("/api/case-statuses");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("caseStatuses", endpoints[0].ControllerName);
        Assert.True(endpoints[0].ReturnType is TsType.TypeRef { Name: "ItemDto" });
    }

    [Fact]
    public void MixedContract_AndRivetClient_NoControllerNameCollision()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetType]
            public sealed record ProjectDto(string Id, string Name);

            // Contract-sourced
            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> ListTasks =
                    Define.Get<TaskDto>("/api/tasks");
            }

            // Controller-sourced
            [RivetClient]
            [Route("api/projects")]
            public sealed class ProjectsController
            {
                [HttpGet("")]
                [ProducesResponseType(typeof(ProjectDto), 200)]
                public Task<IActionResult> List(CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contractEndpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var controllerEndpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        // Both sources produce endpoints for different controllers
        Assert.Single(contractEndpoints);
        Assert.Single(controllerEndpoints);
        Assert.Equal("tasks", contractEndpoints[0].ControllerName);
        Assert.Equal("projects", controllerEndpoints[0].ControllerName);
    }

    [Fact]
    public void TransitiveTypeDiscovery_FromContractReferencedTypes()
    {
        var source = """
            using Rivet;

            namespace Test;

            // NOT marked [RivetType] — should be discovered transitively via contract
            public sealed record TaskDto(string Id, string Title, StatusDto Status);

            [RivetType]
            public sealed record StatusDto(string Label);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        Assert.Single(endpoints);
        Assert.True(endpoints[0].ReturnType is TsType.TypeRef { Name: "TaskDto" });
        // TaskDto should have been walked transitively
        Assert.True(walker.Definitions.ContainsKey("TaskDto"));
        Assert.True(walker.Definitions.ContainsKey("StatusDto"));
    }

    [Fact]
    public void RouteConstraints_StrippedInContract()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("/api/tasks/{id}", endpoints[0].RouteTemplate);
        Assert.True(endpoints[0].ReturnType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Single(endpoints[0].Params);
        Assert.True(endpoints[0].Params[0].Type is TsType.Primitive { Name: "string" });
    }

    [Fact]
    public void Description_ExtractedFromBuilder()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Description("Retrieve a single task by ID");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("Retrieve a single task by ID", endpoints[0].Description);
        Assert.True(endpoints[0].ReturnType is TsType.TypeRef { Name: "TaskDto" });
    }

    [Fact]
    public void Description_NullWhenOmitted()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.Null(endpoints[0].Description);
        Assert.True(endpoints[0].ReturnType is TsType.TypeRef { Name: "TaskDto" });
    }

    [Fact]
    public void ReturnsDescription_ExtractedFromBuilder()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Returns<NotFoundDto>(404, "Task not found");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.True(endpoints[0].ReturnType is TsType.TypeRef { Name: "TaskDto" });

        var notFoundResponse = endpoints[0].Responses.First(r => r.StatusCode == 404);
        Assert.Equal("Task not found", notFoundResponse.Description);
        Assert.True(notFoundResponse.DataType is TsType.TypeRef { Name: "NotFoundDto" });

        // Success response has no description
        var successResponse = endpoints[0].Responses.First(r => r.StatusCode == 200);
        Assert.Null(successResponse.Description);
        Assert.True(successResponse.DataType is TsType.TypeRef { Name: "TaskDto" });
    }

    [Fact]
    public void DescriptionAndReturnsDescription_BothWork()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<TaskDto> GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}")
                        .Description("Retrieve a task")
                        .Returns<NotFoundDto>(404, "Task not found");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("Retrieve a task", endpoints[0].Description);
        Assert.True(endpoints[0].ReturnType is TsType.TypeRef { Name: "TaskDto" });
        Assert.True(
            endpoints[0].Responses.First(r => r.StatusCode == 200).DataType
                is TsType.TypeRef { Name: "TaskDto" }
        );
        Assert.True(
            endpoints[0].Responses.First(r => r.StatusCode == 404).DataType
                is TsType.TypeRef { Name: "NotFoundDto" }
        );
        Assert.Equal(
            "Task not found",
            endpoints[0].Responses.First(r => r.StatusCode == 404).Description
        );
    }

    // --- Abstract base class contract tests ---

    private static IReadOnlyList<TsEndpointDefinition> GenerateWithBothWalkers(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        return CompilationHelper.WalkMerged(compilation, discovered, walker);
    }

    [Fact]
    public void AbstractContract_Get_ExtractsEndpoint()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            [Route("api/tasks")]
            public abstract class TasksContract : ControllerBase
            {
                [HttpGet]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public abstract Task<IActionResult> List(CancellationToken ct);
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("list", ep.Name);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Equal("/api/tasks", ep.RouteTemplate);
        Assert.Equal("tasks", ep.ControllerName);
        Assert.True(ep.ReturnType is TsType.TypeRef { Name: "TaskDto" });
    }

    [Theory]
    [InlineData("", "[HttpGet]", "has no route template")]
    [InlineData(
        "[Route(\"api/tasks\")]",
        "",
        "is an abstract method on a [RivetContract] class but has no [HttpGet]"
    )]
    public void AbstractContract_Method_Without_Route_Or_Verb_Is_Refused(
        string classRoute,
        string verb,
        string expected
    )
    {
        var source = $$"""
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            {{classRoute}}
            public abstract class TasksContract : ControllerBase
            {
                public abstract string Area { get; }

                {{verb}}
                [ProducesResponseType(typeof(TaskDto), 200)]
                public abstract Task<IActionResult> List();
            }
            """;

        var message = Assert.Throws<RivetUserException>(() => Generate(source)).Message;

        Assert.StartsWith("error RIV1100: contract endpoint 'TasksContract.List'", message);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void Controller_Duplicate_Success_Status_Fails_With_RIV2010()
    {
        // Two [ProducesResponseType] on one status used to keep the first (no body)
        // under a warning, silently dropping the TaskDto the second one declared.
        var source = """
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [ApiController]
            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet]
                [RivetEndpoint]
                [ProducesResponseType(200)]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public IActionResult Get() => Ok();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkEndpoints(compilation, discovered, walker)
        );

        Assert.StartsWith("error RIV2010:", exception.Message);
        Assert.Contains("'get'", exception.Message);
        Assert.Contains("status 200", exception.Message);
    }

    [Fact]
    public void AbstractContract_Post_WithBodyAndRouteParams()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateCommentInput(string Text);

            [RivetType]
            public sealed record CommentDto(string Id, string Text);

            [RivetContract]
            [Route("api/tasks")]
            public abstract class TasksContract : ControllerBase
            {
                [HttpPost("{taskId}/comments")]
                [ProducesResponseType(typeof(CommentDto), 201)]
                public abstract Task<IActionResult> CreateComment(
                    Guid taskId,
                    [FromBody] CreateCommentInput body,
                    CancellationToken ct);
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("POST", ep.HttpMethod);
        Assert.Equal("/api/tasks/{taskId}/comments", ep.RouteTemplate);

        var routeParam = ep.Params.First(p => p.Source == ParamSource.Route);
        Assert.Equal("taskId", routeParam.Name);

        var bodyParam = ep.Params.First(p => p.Source == ParamSource.Body);
        Assert.Equal("body", bodyParam.Name);
    }

    [Fact]
    public void AbstractContract_MultipleResponses()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            [Route("api/tasks")]
            public abstract class TasksContract : ControllerBase
            {
                [HttpGet("{id}")]
                [ProducesResponseType(typeof(TaskDto), 200)]
                [ProducesResponseType(404)]
                public abstract Task<IActionResult> GetById(Guid id, CancellationToken ct);
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal(2, ep.Responses.Count);
        Assert.Equal(200, ep.Responses[0].StatusCode);
        Assert.Equal(404, ep.Responses[1].StatusCode);
    }

    [Fact]
    public void AbstractContract_ControllerInherits_NoDeduplication()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            [Route("api/tasks")]
            public abstract class TasksContract : ControllerBase
            {
                [HttpGet]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public abstract Task<IActionResult> List(CancellationToken ct);
            }

            // Controller inherits — Rivet should only see the contract, not the controller
            public sealed class TasksController : TasksContract
            {
                public override Task<IActionResult> List(CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = GenerateWithBothWalkers(source);

        // Only one endpoint — from the contract. Controller not discovered (no [RivetClient])
        Assert.Single(endpoints);
        Assert.Equal("tasks", endpoints[0].ControllerName);
    }

    [Fact]
    public void AbstractContract_NameStripsSuffix()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            [Route("api/case-statuses")]
            public abstract class CaseStatusesContract : ControllerBase
            {
                [HttpGet]
                [ProducesResponseType(typeof(ItemDto), 200)]
                public abstract Task<IActionResult> List(CancellationToken ct);
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("caseStatuses", endpoints[0].ControllerName);
    }

    [Fact]
    public void AbstractContract_RouteConstraints_Stripped()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id);

            [RivetContract]
            [Route("api/tasks")]
            public abstract class TasksContract : ControllerBase
            {
                [HttpGet("{id:guid}")]
                [ProducesResponseType(typeof(TaskDto), 200)]
                public abstract Task<IActionResult> GetById(Guid id, CancellationToken ct);
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("/api/tasks/{id}", endpoints[0].RouteTemplate);
    }

    [Fact]
    public void AcceptsFile_GeneratesFileParam()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FileResult(string Id, string FileName);

            [RivetContract]
            public static class UploadsContract
            {
                public static readonly RouteDefinition<FileResult> Upload =
                    Define.Post<FileResult>("/api/files")
                        .AcceptsFile()
                        .Description("Upload a file");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("upload", ep.Name);
        Assert.Equal("POST", ep.HttpMethod);

        // Should have a file param
        Assert.Single(ep.Params);
        Assert.Equal("file", ep.Params[0].Name);
        Assert.Equal(ParamSource.File, ep.Params[0].Source);
    }

    [Fact]
    public void AcceptsFile_WithRouteParams_GeneratesBoth()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AvatarResult(string Url);

            [RivetContract]
            public static class UsersContract
            {
                public static readonly RouteDefinition<AvatarResult> UploadAvatar =
                    Define.Post<AvatarResult>("/api/users/{id:guid}/avatar")
                        .AcceptsFile()
                        .Description("Upload a profile picture");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];

        // Route param + file param
        Assert.Equal(2, ep.Params.Count);
        Assert.Equal("id", ep.Params[0].Name);
        Assert.Equal(ParamSource.Route, ep.Params[0].Source);
        Assert.Equal("file", ep.Params[1].Name);
        Assert.Equal(ParamSource.File, ep.Params[1].Source);
    }

    [Fact]
    public void ProducesFile_SetsFileContentType()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ErrorDto(string Message);

            [RivetContract]
            public static class DocumentsContract
            {
                public static readonly RouteDefinition GetDocument =
                    Define.Get("/api/documents/{id:guid}")
                        .Description("Download a document")
                        .ProducesFile("application/octet-stream")
                        .Returns<ErrorDto>(404, "Document not found");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("getDocument", ep.Name);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Equal("application/octet-stream", ep.FileContentType);
        Assert.Null(ep.ReturnType);

        // Error response should still be typed
        Assert.Single(ep.Responses, r => r.StatusCode == 404);
    }

    [Fact]
    public void ProducesFile_DefaultContentType()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition Download =
                    Define.Get("/api/files/{id:guid}")
                        .ProducesFile();
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("application/octet-stream", endpoints[0].FileContentType);
    }

    [Fact]
    public void ProducesFile_OpenApi_EmitsBinarySchema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ErrorDto(string Message);

            [RivetContract]
            public static class DocumentsContract
            {
                public static readonly RouteDefinition GetDocument =
                    Define.Get("/api/documents/{id:guid}")
                        .ProducesFile("application/pdf")
                        .Returns<ErrorDto>(404, "Not found");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = Rivet.Tool.Emit.OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        // Success response should use application/pdf with binary schema
        Assert.Contains("application/pdf", json);
        Assert.Contains("\"format\": \"binary\"", json);
        // Error response should still be application/json
        Assert.Contains("application/json", json);
    }

    [Fact]
    public void ByteArray_TOutput_Stays_Json_Base64()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ErrorDto(string Message);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<byte[]> Download =
                    Define.Get<byte[]>("/api/files/{id:guid}")
                        .Description("Download a file")
                        .Returns<ErrorDto>(404, "Not found");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("download", ep.Name);
        // Ordinary byte[] is a JSON value: STJ serializes it as a base64 JSON string.
        // No implicit binary file terminal, no octet-stream content type.
        Assert.False(ep.IsFileEndpoint);
        Assert.Null(ep.FileContentType);
        Assert.NotNull(ep.ReturnType);
        Assert.Equal("string", Assert.IsType<TsType.Primitive>(ep.ReturnType).Name);
        Assert.Equal("base64", Assert.IsType<TsType.Primitive>(ep.ReturnType).Format);
        Assert.Equal("byte[]", Assert.IsType<TsType.Primitive>(ep.ReturnType).CSharpType);
        // Error response still typed
        Assert.Single(ep.Responses, r => r.StatusCode == 404);
    }

    [Fact]
    public void ByteArray_TOutput_ProducesFile_OverridesContentType()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<byte[]> Download =
                    Define.Get<byte[]>("/api/files/{id:guid}")
                        .ProducesFile("application/pdf");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        // Explicit .ProducesFile() declares the file — byte[] stays unmapped (Blob)
        Assert.Equal("application/pdf", endpoints[0].FileContentType);
    }

    [Fact]
    public void ByteArray_TOutput_OpenApi_Emits_Json_Base64_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<byte[]> Download =
                    Define.Get<byte[]>("/api/files/{id:guid}");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = Rivet.Tool.Emit.OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        // JSON base64 string schema under application/json — matching the runtime's
        // actual Success(byte[]) wire shape — with no binary file terminal.
        Assert.Contains("\"type\": \"string\"", json);
        Assert.Contains("\"contentEncoding\": \"base64\"", json);
        Assert.Contains("application/json", json);
        Assert.DoesNotContain("application/octet-stream", json);
        Assert.DoesNotContain("\"format\": \"binary\"", json);
    }

    [Fact]
    public void ProducesFileAttribute_ByteArrayStringTuple_SetsFileContentType()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class FilesContract
            {
                [ProducesFile]
                public static readonly RouteDefinition<(byte[] Content, string FileName)> Download =
                    Define.Get<(byte[] Content, string FileName)>("/api/files/{id:guid}")
                        .Description("Download a file");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("download", ep.Name);
        Assert.Equal("application/octet-stream", ep.FileContentType);
        Assert.Null(ep.ReturnType);
    }

    [Fact]
    public void ProducesFileAttribute_ByteArrayStringTuple_OpenApi_EmitsBinarySchema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class FilesContract
            {
                [ProducesFile]
                public static readonly RouteDefinition<(byte[] Content, string FileName)> Download =
                    Define.Get<(byte[] Content, string FileName)>("/api/files/{id:guid}");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = Rivet.Tool.Emit.OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        Assert.Contains("application/octet-stream", json);
        Assert.Contains("\"format\": \"binary\"", json);
        Assert.DoesNotContain("application/json", json);
    }

    [Fact]
    public void ProducesFileAttribute_PlainByteArray_SetsFileContentType()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class FilesContract
            {
                [ProducesFile]
                public static readonly RouteDefinition<byte[]> Download =
                    Define.Get<byte[]>("/api/files/{id:guid}");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("application/octet-stream", endpoints[0].FileContentType);
        Assert.Null(endpoints[0].ReturnType);
    }

    [Fact]
    public void MixedFileUpload_IncludesNonFileProperties()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadInput(IFormFile Document, string Title, int CategoryId);

            [RivetType]
            public sealed record UploadResult(string Id);

            [RivetContract]
            public static class UploadsContract
            {
                public static readonly RouteDefinition<UploadInput, UploadResult> Upload =
                    Define.Post<UploadInput, UploadResult>("/api/uploads");
            }
            """;

        var endpoints = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];

        // File param
        var fileParam = ep.Params.FirstOrDefault(p => p.Source == ParamSource.File);
        Assert.NotNull(fileParam);
        Assert.Equal("document", fileParam.Name);

        // Non-file properties should be FormField, not dropped
        var titleParam = ep.Params.FirstOrDefault(p => p.Name == "title");
        Assert.NotNull(titleParam);
        Assert.Equal(ParamSource.FormField, titleParam.Source);

        var categoryParam = ep.Params.FirstOrDefault(p => p.Name == "categoryId");
        Assert.NotNull(categoryParam);
        Assert.Equal(ParamSource.FormField, categoryParam.Source);
    }

    [Fact]
    public void Status_DoubleCall_Throws()
    {
        // Test that the guard works via the actual Rivet types — all 4 variants
        var voidRoute = Rivet.Define.Get("/test");
        voidRoute.Status(201);
        Assert.Throws<InvalidOperationException>(() => voidRoute.Status(204));

        var outputRoute = Rivet.Define.Get<string>("/test");
        outputRoute.Status(201);
        Assert.Throws<InvalidOperationException>(() => outputRoute.Status(204));

        var inputOutputRoute = Rivet.Define.Get<string, string>("/test");
        inputOutputRoute.Status(201);
        Assert.Throws<InvalidOperationException>(() => inputOutputRoute.Status(204));

        var inputRoute = Rivet.Define.Put("/test").Accepts<string>();
        inputRoute.Status(201);
        Assert.Throws<InvalidOperationException>(() => inputRoute.Status(204));
    }

    // ========== GAP-1: FormEncoded forward pipeline ==========

    [Fact]
    public void FormEncoded_Sets_IsFormEncoded_On_Endpoint()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginRequest(string Email, string Password);

            [RivetType]
            public sealed record TokenResponse(string Token);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition<LoginRequest, TokenResponse> Login =
                    Define.Post<LoginRequest, TokenResponse>("/api/auth/login")
                        .FormEncoded();
            }
            """;

        var endpoints = Generate(source);
        var ep = Assert.Single(endpoints);

        Assert.True(ep.IsFormEncoded, "Endpoint should have IsFormEncoded=true");
    }

    [Fact]
    public void FormEncoded_OpenApi_Emits_FormUrlencoded_ContentType()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginRequest(string Email, string Password);

            [RivetType]
            public sealed record TokenResponse(string Token);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition<LoginRequest, TokenResponse> Login =
                    Define.Post<LoginRequest, TokenResponse>("/api/auth/login")
                        .FormEncoded();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );

        Assert.Contains("application/x-www-form-urlencoded", json);
        Assert.DoesNotContain(
            "\"application/json\"",
            json.Split("requestBody")[1].Split("responses")[0]
        );
    }

    // ========== GAP-2: Void error responses forward pipeline ==========

    [Fact]
    public void Returns_Without_Type_Creates_Void_Response()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Name);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto, ItemDto> Update =
                    Define.Put<ItemDto, ItemDto>("/api/items/{id:guid}")
                        .Returns(404, "Not found")
                        .Returns(409);
            }
            """;

        var endpoints = Generate(source);
        var ep = Assert.Single(endpoints);

        // 404 with description, no data type
        var resp404 = ep.Responses.FirstOrDefault(r => r.StatusCode == 404);
        Assert.NotNull(resp404);
        Assert.Null(resp404.DataType);
        Assert.Equal("Not found", resp404.Description);

        // 409 with no description, no data type
        var resp409 = ep.Responses.FirstOrDefault(r => r.StatusCode == 409);
        Assert.NotNull(resp409);
        Assert.Null(resp409.DataType);
    }

    [Fact]
    public void Returns_Without_Type_OpenApi_Emits_No_Content_Block()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Name);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto, ItemDto> Update =
                    Define.Put<ItemDto, ItemDto>("/api/items/{id:guid}")
                        .Returns(404);
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );
        var doc = System.Text.Json.JsonDocument.Parse(json);

        var resp404 = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/items/{id}")
            .GetProperty("put")
            .GetProperty("responses")
            .GetProperty("404");

        Assert.False(
            resp404.TryGetProperty("content", out _),
            "Void 404 response should have no content block"
        );
    }

    // ========== GAP-3: JsonStringEnumMemberName forward pipeline ==========

    [Fact]
    public void JsonStringEnumMemberName_Preserves_Original_In_Forward_Pipeline()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [JsonConverter(typeof(JsonStringEnumConverter<Status>))]
            public enum Status
            {
                [JsonStringEnumMemberName("in-progress")]
                InProgress,
                [JsonStringEnumMemberName("on_hold")]
                OnHold,
                Done,
            }

            [RivetType]
            public sealed record ItemDto(string Name, Status Status);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> GetItem =
                    Define.Get<ItemDto>("/api/items/{id:guid}");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var statusEnum = (TsType.StringUnion)walker.Enums["Status"];
        Assert.Contains("in-progress", statusEnum.Members);
        Assert.Contains("on_hold", statusEnum.Members);
        Assert.Contains("Done", statusEnum.Members);
        Assert.DoesNotContain("InProgress", statusEnum.Members);
        Assert.DoesNotContain("OnHold", statusEnum.Members);
        Assert.DoesNotContain("done", statusEnum.Members);
    }

    // ========== GAP-4 / A1: DELETE default status — cross-system agreement ==========
    // Policy: DELETE without an output type defaults to 204; DELETE with an output type
    // defaults to 200 (204-with-body is invalid HTTP). The runtime (Define.Delete) and the
    // walker (ContractWalker.DefaultSuccessCode) must implement the same policy so the
    // generated contract and the live server can never diverge again.

    [Fact]
    public void Delete_Default_Status_Runtime_And_Walker_Agree()
    {
        // --- Void DELETE: both sides default to 204 ---
        // The runtime side is pinned by ContractTerminalResultTests.Success_Delete*.

        var voidSource = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition DeleteItem =
                    Define.Delete("/api/items/{id:guid}");
            }
            """;

        var voidEndpoints = Generate(voidSource);
        var voidEp = Assert.Single(voidEndpoints);
        var voidSuccess = voidEp.Responses.FirstOrDefault(r => r.StatusCode is >= 200 and < 300);
        Assert.NotNull(voidSuccess);
        Assert.Equal(204, voidSuccess.StatusCode);

        // --- DELETE with an output type: both sides default to 200 ---
        var outputSource = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record DeletedDto(string Id);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<DeletedDto> DeleteItem =
                    Define.Delete<DeletedDto>("/api/items/{id:guid}");
            }
            """;

        var outputEndpoints = Generate(outputSource);
        var outputEp = Assert.Single(outputEndpoints);
        var outputSuccess = outputEp.Responses.FirstOrDefault(r =>
            r.StatusCode is >= 200 and < 300
        );
        Assert.NotNull(outputSuccess);
        Assert.Equal(200, outputSuccess.StatusCode);
    }

    // ========== GAP-5: FormEncoded on all route definition types ==========

    [Fact]
    public void FormEncoded_Works_On_OutputOnly_RouteDefinition()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TokenResponse(string Token);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition<TokenResponse> Login =
                    Define.Post<TokenResponse>("/api/auth/login")
                        .FormEncoded();
            }
            """;

        var endpoints = Generate(source);
        var ep = Assert.Single(endpoints);
        Assert.True(ep.IsFormEncoded);
    }

    [Fact]
    public void FormEncoded_Works_On_InputOnly_RouteDefinition()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginRequest(string Email, string Password);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly InputRouteDefinition<LoginRequest> Login =
                    Define.Delete("/api/auth/session")
                        .Accepts<LoginRequest>()
                        .FormEncoded();
            }
            """;

        var endpoints = Generate(source);
        var ep = Assert.Single(endpoints);
        Assert.True(ep.IsFormEncoded);
        // .FormEncoded() declares the body, so even a DELETE's input is the form, not the
        // query (RequestInputLowering; the runtime binds it the same way). Its fields are
        // the members the form binders bind.
        Assert.Equal(
            [("email", ParamSource.FormField), ("password", ParamSource.FormField)],
            ep.Params.Select(param => (param.Name, param.Source))
        );
    }

    [Fact]
    public void FormEncoded_Works_On_Bare_RouteDefinition()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition Logout =
                    Define.Post("/api/auth/logout")
                        .FormEncoded();
            }
            """;

        var endpoints = Generate(source);
        var ep = Assert.Single(endpoints);
        Assert.True(ep.IsFormEncoded);
    }

    // ========== Summary / Description separation ==========

    [Fact]
    public void Summary_And_Description_Stored_Separately()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Name);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> GetItem =
                    Define.Get<ItemDto>("/api/items/{id:guid}")
                        .Summary("Get an item")
                        .Description("Retrieves a single item by its unique identifier");
            }
            """;

        var endpoints = Generate(source);
        var ep = Assert.Single(endpoints);

        Assert.Equal("Get an item", ep.Summary);
        Assert.Equal("Retrieves a single item by its unique identifier", ep.Description);
    }

    [Fact]
    public void Summary_And_Description_Emit_Separately_In_OpenApi()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Name);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> GetItem =
                    Define.Get<ItemDto>("/api/items/{id:guid}")
                        .Summary("Get an item")
                        .Description("Retrieves a single item by its unique identifier");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = Rivet.Tool.Emit.OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );
        var doc = System.Text.Json.JsonDocument.Parse(json);

        var operation = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/items/{id}")
            .GetProperty("get");

        Assert.Equal("Get an item", operation.GetProperty("summary").GetString());
        Assert.Equal(
            "Retrieves a single item by its unique identifier",
            operation.GetProperty("description").GetString()
        );
    }

    // ========== Inherited properties ==========

    [Fact]
    public void WalkedDto_WithBaseClass_IncludesInheritedProperties()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public abstract record BaseDto
            {
                public Guid Id { get; init; }
                public DateTime CreatedAt { get; init; }
            }

            [RivetType]
            public sealed record TaskDto : BaseDto
            {
                public string Name { get; init; } = "";
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var taskDto = walker.Definitions["TaskDto"];
        var names = taskDto.Properties.Select(p => p.Name).ToList();

        Assert.Contains("id", names);
        Assert.Contains("createdAt", names);
        Assert.Contains("name", names);
        // Records' synthesized members (EqualityContract) must not leak in
        Assert.DoesNotContain("equalityContract", names);
        Assert.Equal(3, taskDto.Properties.Count);

        // Inherited property types map correctly
        var id = Assert.Single(taskDto.Properties, p => p.Name == "id");
        Assert.Equal("uuid", Assert.IsType<TsType.Primitive>(id.Type).Format);
    }

    [Fact]
    public void MultiLevelBaseChain_FlattensAllLevels()
    {
        var source = """
            using Rivet;

            namespace Test;

            public abstract record Level0
            {
                public string A { get; init; } = "";
            }

            public abstract record Level1 : Level0
            {
                public string B { get; init; } = "";
            }

            [RivetType]
            public sealed record Level2 : Level1
            {
                public string C { get; init; } = "";
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var names = walker.Definitions["Level2"].Properties.Select(p => p.Name).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("a", names);
        Assert.Contains("b", names);
        Assert.Contains("c", names);
    }

    [Fact]
    public void DerivedOverride_WinsOverBaseProperty()
    {
        var source = """
            using Rivet;

            namespace Test;

            public abstract record BaseDto
            {
                public virtual string? Label { get; init; }
            }

            [RivetType]
            public sealed record DerivedDto : BaseDto
            {
                // Override tightens nullability — derived declaration must win
                public override string Label { get; init; } = "";
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var label = Assert.Single(
            walker.Definitions["DerivedDto"].Properties,
            p => p.Name == "label"
        );
        // Derived (non-nullable) wins; base (nullable) would be TsType.Nullable
        Assert.IsType<TsType.Primitive>(label.Type);
        Assert.False(label.IsOptional);
    }

    [Fact]
    public void DerivedShadowing_NewProperty_Wins()
    {
        var source = """
            using Rivet;

            namespace Test;

            public record BaseDto
            {
                public string Code { get; init; } = "";
            }

            [RivetType]
            public sealed record DerivedDto : BaseDto
            {
                public new int Code { get; init; }
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var props = walker.Definitions["DerivedDto"].Properties;
        var code = Assert.Single(props, p => p.Name == "code");
        Assert.Equal("number", Assert.IsType<TsType.Primitive>(code.Type).Name);
    }

    [Fact]
    public void PositionalRecordInheritance_IncludesBasePositionalParams()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public abstract record AuditedDto(Guid Id, DateTime CreatedAt);

            [RivetType]
            public sealed record OrderDto(Guid Id, DateTime CreatedAt, string Number)
                : AuditedDto(Id, CreatedAt);
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var names = walker.Definitions["OrderDto"].Properties.Select(p => p.Name).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("id", names);
        Assert.Contains("createdAt", names);
        Assert.Contains("number", names);
    }

    [Fact]
    public void GenericBase_SubstitutesTypeArguments()
    {
        var source = """
            using Rivet;

            namespace Test;

            public abstract record Envelope<T>
            {
                public T Payload { get; init; } = default!;
                public int Version { get; init; }
            }

            [RivetType]
            public sealed record MessageDto : Envelope<string>
            {
                public string Topic { get; init; } = "";
            }
            """;

        var (_, walker) = CompilationHelper.WalkContract(source);

        var props = walker.Definitions["MessageDto"].Properties;
        Assert.Equal(3, props.Count);

        // T is substituted with the concrete type argument from the base instantiation
        var payload = Assert.Single(props, p => p.Name == "payload");
        Assert.Equal("string", Assert.IsType<TsType.Primitive>(payload.Type).Name);
    }

    [Fact]
    public void ContractInput_WithBaseQuery_InheritedQueryParamsSurvive()
    {
        var source = """
            using Rivet;

            namespace Test;

            public abstract record PagedQuery
            {
                public int Page { get; init; }
                public int PageSize { get; init; }
            }

            public sealed record ListTasksQuery : PagedQuery
            {
                public string? Filter { get; init; }
            }

            public sealed record TaskDto(string Name);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<ListTasksQuery, TaskDto[]> List = Define.Get<ListTasksQuery, TaskDto[]>("/api/tasks");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var list = Assert.Single(endpoints);
        var queryParams = list
            .Params.Where(p => p.Source == ParamSource.Query)
            .Select(p => p.Name)
            .ToList();

        Assert.Contains("page", queryParams);
        Assert.Contains("pageSize", queryParams);
        Assert.Contains("filter", queryParams);
    }

    [Fact]
    public void ContractInput_WithBaseRouteProperty_BindsAsRouteParam()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public abstract record ByIdQuery
            {
                public Guid Id { get; init; }
            }

            public sealed record GetTaskQuery : ByIdQuery
            {
                public bool IncludeDetails { get; init; }
            }

            public sealed record TaskDto(string Name);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<GetTaskQuery, TaskDto> Get = Define.Get<GetTaskQuery, TaskDto>("/api/tasks/{id}");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var get = Assert.Single(endpoints);
        var id = Assert.Single(get.Params, p => p.Name == "id");
        Assert.Equal(ParamSource.Route, id.Source);
        // Typed from the inherited property, not the string fallback
        Assert.Equal("uuid", Assert.IsType<TsType.Primitive>(id.Type).Format);
    }

    [Fact]
    public void MixedUploadInput_WithBaseFormFields_InheritedFieldsSurvive()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            public abstract record UploadMetadata
            {
                public string Category { get; init; } = "";
            }

            public sealed record UploadRequest : UploadMetadata
            {
                public IFormFile File { get; init; } = default!;
            }

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<UploadRequest, string> Upload = Define.Post<UploadRequest, string>("/api/files");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var upload = Assert.Single(endpoints);
        Assert.Single(upload.Params, p => p.Name == "file" && p.Source == ParamSource.File);
        Assert.Single(
            upload.Params,
            p => p.Name == "category" && p.Source == ParamSource.FormField
        );
    }

    // ========== Type name collisions ==========

    // Schema names are global and built from the simple name only. Two distinct types
    // sharing one used to emit the later as "Item2" in discovery order; now generation
    // stops and names both types so one can be renamed.
    public static TheoryData<string[], string, string, string> CollidingTypeSources =>
        new()
        {
            {
                [
                    """
                        using Rivet;
                        namespace Foo.Models
                        {
                            public sealed record Item(string Name);

                            [RivetType]
                            public sealed record FooWrapper(Item Item);
                        }
                        """,
                    """
                        using Rivet;
                        namespace Bar.Models
                        {
                            public sealed record Item(int Count);

                            [RivetType]
                            public sealed record BarWrapper(Item Item);
                        }
                        """,
                ],
                "Item",
                "Foo.Models.Item",
                "Bar.Models.Item"
            },
            {
                [
                    """
                        using Rivet;
                        using System.Text.Json.Serialization;
                        namespace Foo.Models
                        {
                            [JsonConverter(typeof(JsonStringEnumConverter<Status>))]
                            public enum Status { Active, Closed }

                            [RivetType]
                            public sealed record FooDto(Status Status);
                        }
                        """,
                    """
                        using Rivet;
                        namespace Bar.Models
                        {
                            public enum Status { Draft, Published, Archived }

                            [RivetType]
                            public sealed record BarDto(Status Status);
                        }
                        """,
                ],
                "Status",
                "Foo.Models.Status",
                "Bar.Models.Status"
            },
            {
                [
                    """
                        using Rivet;
                        namespace Test;

                        [RivetType]
                        public sealed record Result(bool Ok);

                        [RivetType]
                        public sealed record Result<T>(bool Ok, T Value);
                        """,
                ],
                "Result",
                "Test.Result",
                "Test.Result<T>"
            },
        };

    [Theory]
    [MemberData(nameof(CollidingTypeSources))]
    public void TypeNameCollision_Fails_Naming_Both_Types(
        string[] sources,
        string schemaName,
        string first,
        string second
    )
    {
        var compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        var discovered = SymbolDiscovery.Discover(compilation);

        var ex = Assert.Throws<RivetUserException>(() =>
            TypeWalker.Create(compilation, new WellKnownTypes(compilation), discovered.RivetTypes)
        );

        Assert.StartsWith($"error {Diagnostics.TypeNameCollision}:", ex.Message);
        Assert.Contains($"'{first}'", ex.Message);
        Assert.Contains($"'{second}'", ex.Message);
        Assert.Contains($"schema name '{schemaName}'", ex.Message);
        Assert.Contains("Rename one", ex.Message);
    }

    // ========== Route-bound property naming ==========

    [Fact]
    public void RouteBoundProperty_WithJsonPropertyName_KeepsRouteName_AndDiagnoses()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            public sealed record GetTaskQuery
            {
                [JsonPropertyName("renamed")]
                public string Id { get; init; } = "";

                public bool Verbose { get; init; }
            }

            public sealed record TaskDto(string Name);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<GetTaskQuery, TaskDto> Get = Define.Get<GetTaskQuery, TaskDto>("/api/tasks/{id}");
            }
            """;

        IReadOnlyList<TsEndpointDefinition> endpoints = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (endpoints, _) = CompilationHelper.WalkContract(source);
        });

        var ep = Assert.Single(endpoints);

        // Route param keeps the route-template name, not the JSON rename
        var route = Assert.Single(ep.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", route.Name);
        Assert.DoesNotContain(ep.Params, p => p.Name == "renamed");

        // Loud diagnostic on the rename/route mismatch
        Assert.Contains("renamed", stderr);

        // Non-route props keep JSON naming behavior
        Assert.Single(ep.Params, p => p.Name == "verbose" && p.Source == ParamSource.Query);
    }

    // ========== Input property naming and types ==========

    [Fact]
    public void ContractWalker_Respects_JsonPropertyName_On_QueryParam()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SearchInput(
                [property: JsonPropertyName("q")] string Query,
                int Limit);

            [RivetType]
            public sealed record ResultDto(string Id);

            [RivetContract]
            public static class SearchContract
            {
                public static readonly RouteDefinition<SearchInput, ResultDto> Search =
                    Define.Get<SearchInput, ResultDto>("/api/search");
            }
            """;

        var endpoints = CompilationHelper.WalkContract(source).Endpoints;
        var search = Assert.Single(endpoints);

        var queryParam = Assert.Single(search.Params, p => p.Name == "q");
        Assert.Equal(ParamSource.Query, queryParam.Source);
    }

    [Fact]
    public void ContractWalker_Skips_JsonIgnore_On_InputProperty()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SearchInput(
                string Query,
                [property: JsonIgnore] string InternalToken);

            [RivetType]
            public sealed record ResultDto(string Id);

            [RivetContract]
            public static class SearchContract
            {
                public static readonly RouteDefinition<SearchInput, ResultDto> Search =
                    Define.Get<SearchInput, ResultDto>("/api/search");
            }
            """;

        var endpoints = CompilationHelper.WalkContract(source).Endpoints;
        var search = Assert.Single(endpoints);

        // Only 'query' should be emitted; 'internalToken' is ignored
        Assert.Single(search.Params);
        Assert.Equal("query", search.Params[0].Name);
    }

    [Fact]
    public void Post_Route_Param_Uses_TInput_Property_Type()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UpdateInput(int Id, string Title);

            [RivetType]
            public sealed record ItemDto(int Id, string Title);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<UpdateInput, ItemDto> UpdateItem =
                    Define.Post<UpdateInput, ItemDto>("/api/items/{id}");
            }
            """;

        var endpoints = CompilationHelper.WalkContract(source).Endpoints;
        var update = Assert.Single(endpoints);

        var routeParam = Assert.Single(update.Params, p => p.Source == ParamSource.Route);
        Assert.Equal("id", routeParam.Name);
        // Should be number (from int Id), not string
        Assert.IsType<TsType.Primitive>(routeParam.Type);
        Assert.Equal("number", ((TsType.Primitive)routeParam.Type).Name);
    }
}
