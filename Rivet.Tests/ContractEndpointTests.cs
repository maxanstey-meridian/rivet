using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ContractEndpointTests
{
    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, string Client) Generate(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        var client = string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
        return (endpoints, client);
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
                public static readonly Define GetTask =
                    Define.Get<GetTaskInput, TaskDto>("/api/tasks/{id}");
            }
            """;

        var (endpoints, client) = Generate(source);

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

        Assert.Contains("Promise<TaskDto>", client);
        Assert.Contains("`/api/tasks/${encodeURIComponent(String(id))}`", client);
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
                public static readonly Define CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId}/comments");
            }
            """;

        var (endpoints, client) = Generate(source);

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

        Assert.Contains("body: body", client);
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
                public static readonly Define CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId}/comments")
                        .RequestExampleJson("{\"text\":\"hello\"}");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<ValidationProblemDto>(422)
                        .ResponseExampleJson(422, "{\"message\":\"Validation failed\"}", name: "validationProblem");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .ResponseExampleJson(422, "{\"message\":\"Validation failed\"}", name: "validationProblem")
                        .Returns<ValidationProblemDto>(422);
            }
            """;

        var (endpoints, _) = Generate(source);

        var ep = Assert.Single(endpoints);
        var response = ep.Responses.First(r => r.StatusCode == 422);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("validationProblem", example.Name);
        Assert.Equal("""{"message":"Validation failed"}""", example.Json);
    }

    [Fact]
    public void ResponseExampleJson_Without_Declared_Response_Is_Ignored()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDto(string Id, string Title);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .ResponseExampleJson(422, "{\"message\":\"Validation failed\"}", name: "validationProblem");
            }
            """;

        var (endpoints, _) = Generate(source);

        var ep = Assert.Single(endpoints);
        Assert.Single(ep.Responses);
        Assert.Equal(200, ep.Responses[0].StatusCode);
        Assert.Null(ep.Responses[0].Examples);
        Assert.DoesNotContain(ep.Responses, response => response.StatusCode == 422);
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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<ValidationProblemDto>(422)
                        .ResponseExampleJson(422, "{\"message\":\"Validation failed\"}", name: "validationProblem")
                        .ResponseExampleJson(422, "{\"message\":\"Already archived\"}", name: "archivedProblem");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<ValidationProblemDto>(422)
                        .ResponseExampleJson(
                            422,
                            "{\"title\":\"Bad request\"}",
                            name: "problem",
                            mediaType: "application/problem+json");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<ValidationProblemDto>(422)
                        .ResponseExampleRef(
                            422,
                            "validation-problem",
                            "{\"message\":\"Validation failed\"}",
                            name: "validationProblem");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId}/comments")
                        .RequestExampleRef(
                            "create-comment",
                            "{\"text\":\"hello\"}",
                            name: "commentExample");
            }
            """;

        var (endpoints, _) = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("commentExample", example.Name);
        Assert.Equal("application/json", example.MediaType);
        Assert.Null(example.Json);
        Assert.Equal("create-comment", example.ComponentExampleId);
        Assert.Equal("""{"text":"hello"}""", example.ResolvedJson);
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
                public static readonly Define CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId}/comments")
                        .RequestExampleJson(
                            "{\"text\":\"hello\"}",
                            name: "commentExample",
                            mediaType: "application/problem+json");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define CreateComment =
                    Define.Post<CreateCommentInput, CommentDto>("/api/tasks/{taskId}/comments")
                        .RequestExampleRef(
                            "create-comment",
                            "{\"text\":\"hello\"}",
                            name: "commentExample",
                            mediaType: "application/problem+json");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define Upload =
                    Define.Post<UploadInput, UploadResultDto>("/api/uploads")
                        .RequestExampleJson("{\"title\":\"Quarterly report\"}");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define Login =
                    Define.Post<LoginInput, TokenDto>("/api/login")
                        .FormEncoded()
                        .RequestExampleJson("{\"email\":\"ada@example.com\",\"password\":\"secret\"}");
            }
            """;

        var (endpoints, _) = Generate(source);

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

        var (endpoints, _) = Generate(source);

        var ep = Assert.Single(endpoints);
        var example = Assert.Single(ep.RequestExamples!);
        Assert.Equal("multipart/form-data", example.MediaType);
        Assert.Equal("""{"file":"ignored"}""", example.Json);
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
                    Define.Get("/api/documents/{id}")
                        .ProducesFile("application/pdf")
                        .ResponseExampleRef(200, "document-example", "{\"href\":\"/api/documents/123\"}", name: "document");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define ListTasks =
                    Define.Get<TaskDto>("/api/tasks");
            }
            """;

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("listTasks", ep.Name);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Empty(ep.Params);
        Assert.True(ep.ReturnType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Single(ep.Responses);
        Assert.Equal(200, ep.Responses[0].StatusCode);
        Assert.True(ep.Responses[0].DataType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Contains("Promise<TaskDto>", client);
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
                public static readonly Define DeleteTask =
                    Define.Delete("/api/tasks/{id}");
            }
            """;

        var (endpoints, client) = Generate(source);

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

        Assert.Contains("Promise<void>", client);
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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<NotFoundDto>(404);
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define CreateTask =
                    Define.Post<CreateInput, CreatedDto>("/api/tasks")
                        .Status(201);
            }
            """;

        var (endpoints, _) = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.True(ep.ReturnType is TsType.TypeRef { Name: "CreatedDto" });
        Assert.Contains(ep.Responses, r => r.StatusCode == 201 && r.DataType is TsType.TypeRef { Name: "CreatedDto" });
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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<NotFoundDto>(404)
                        .Returns<ConflictDto>(409);
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define GetItem =
                    Define.Get<ItemDto>("/api/case-statuses");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define ListTasks =
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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id:guid}");
            }
            """;

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("/api/tasks/{id}", endpoints[0].RouteTemplate);
        Assert.True(endpoints[0].ReturnType is TsType.TypeRef { Name: "TaskDto" });
        Assert.Single(endpoints[0].Params);
        Assert.True(endpoints[0].Params[0].Type is TsType.Primitive { Name: "string" });
        Assert.DoesNotContain(":guid", client);
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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Description("Retrieve a single task by ID");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Returns<NotFoundDto>(404, "Task not found");
            }
            """;

        var (endpoints, _) = Generate(source);

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
                public static readonly Define GetTask =
                    Define.Get<TaskDto>("/api/tasks/{id}")
                        .Description("Retrieve a task")
                        .Returns<NotFoundDto>(404, "Task not found");
            }
            """;

        var (endpoints, _) = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("Retrieve a task", endpoints[0].Description);
        Assert.True(endpoints[0].ReturnType is TsType.TypeRef { Name: "TaskDto" });
        Assert.True(endpoints[0].Responses.First(r => r.StatusCode == 200).DataType is TsType.TypeRef { Name: "TaskDto" });
        Assert.True(endpoints[0].Responses.First(r => r.StatusCode == 404).DataType is TsType.TypeRef { Name: "NotFoundDto" });
        Assert.Equal("Task not found", endpoints[0].Responses.First(r => r.StatusCode == 404).Description);
    }

    // --- Abstract base class contract tests ---

    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, string Client) GenerateWithBothWalkers(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contractEndpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var annotationEndpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);

        // Merge: contract wins on collision (same as Program.cs)
        var seen = new HashSet<(string, string)>(
            contractEndpoints.Select(e => (e.ControllerName, e.Name)));
        var merged = new List<TsEndpointDefinition>(contractEndpoints);
        foreach (var ep in annotationEndpoints)
        {
            if (seen.Add((ep.ControllerName, ep.Name)))
            {
                merged.Add(ep);
            }
        }

        var definitions = walker.Definitions.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, walker.Brands.Values.ToList(), walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var controllerGroups = ClientEmitter.GroupByController(merged);
        var client = string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
        return (merged, client);
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

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("list", ep.Name);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Equal("/api/tasks", ep.RouteTemplate);
        Assert.Equal("tasks", ep.ControllerName);
        Assert.Contains("Promise<TaskDto>", client);
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

        var (endpoints, client) = Generate(source);

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

        var (endpoints, _) = Generate(source);

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

        var (endpoints, _) = GenerateWithBothWalkers(source);

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

        var (endpoints, _) = Generate(source);

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

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("/api/tasks/{id}", endpoints[0].RouteTemplate);
        Assert.DoesNotContain(":guid", client);
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

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("upload", ep.Name);
        Assert.Equal("POST", ep.HttpMethod);

        // Should have a file param
        Assert.Single(ep.Params);
        Assert.Equal("file", ep.Params[0].Name);
        Assert.Equal(ParamSource.File, ep.Params[0].Source);

        // Client should use FormData
        Assert.Contains("file: File", client);
        Assert.Contains("FormData", client);
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
                    Define.Post<AvatarResult>("/api/users/{id}/avatar")
                        .AcceptsFile()
                        .Description("Upload a profile picture");
            }
            """;

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];

        // Route param + file param
        Assert.Equal(2, ep.Params.Count);
        Assert.Equal("id", ep.Params[0].Name);
        Assert.Equal(ParamSource.Route, ep.Params[0].Source);
        Assert.Equal("file", ep.Params[1].Name);
        Assert.Equal(ParamSource.File, ep.Params[1].Source);

        Assert.Contains("id: string, file: File", client);
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
                    Define.Get("/api/documents/{id}")
                        .Description("Download a document")
                        .ProducesFile("application/octet-stream")
                        .Returns<ErrorDto>(404, "Document not found");
            }
            """;

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("getDocument", ep.Name);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Equal("application/octet-stream", ep.FileContentType);
        Assert.Null(ep.ReturnType);

        // Client should return Blob
        Assert.Contains("Promise<Blob>", client);
        Assert.Contains("blob: true", client);

        // Result DU should use Blob for success, not void
        Assert.Contains("data: Blob", client);
        Assert.DoesNotContain("status: 200; data: void", client);

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
                    Define.Get("/api/files/{id}")
                        .ProducesFile();
            }
            """;

        var (endpoints, _) = Generate(source);

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
                    Define.Get("/api/documents/{id}")
                        .ProducesFile("application/pdf")
                        .Returns<ErrorDto>(404, "Not found");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = Rivet.Tool.Emit.OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        // Success response should use application/pdf with binary schema
        Assert.Contains("application/pdf", json);
        Assert.Contains("\"format\": \"binary\"", json);
        // Error response should still be application/json
        Assert.Contains("application/json", json);
    }

    [Fact]
    public void ByteArray_TOutput_InfersFileEndpoint()
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
                    Define.Get<byte[]>("/api/files/{id}")
                        .Description("Download a file")
                        .Returns<ErrorDto>(404, "Not found");
            }
            """;

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("download", ep.Name);
        // byte[] infers file endpoint — no explicit .ProducesFile() needed
        Assert.Equal("application/octet-stream", ep.FileContentType);
        Assert.Null(ep.ReturnType); // TS gets Blob, not number[]
        Assert.Contains("Promise<Blob>", client);
        Assert.Contains("blob: true", client);
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
                    Define.Get<byte[]>("/api/files/{id}")
                        .ProducesFile("application/pdf");
            }
            """;

        var (endpoints, _) = Generate(source);

        Assert.Single(endpoints);
        // Explicit .ProducesFile() wins over the byte[] default
        Assert.Equal("application/pdf", endpoints[0].FileContentType);
    }

    [Fact]
    public void ByteArray_TOutput_OpenApi_EmitsBinarySchema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<byte[]> Download =
                    Define.Get<byte[]>("/api/files/{id}");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = Rivet.Tool.Emit.OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        Assert.Contains("application/octet-stream", json);
        Assert.Contains("\"format\": \"binary\"", json);
        Assert.DoesNotContain("application/json", json);
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
                    Define.Get<(byte[] Content, string FileName)>("/api/files/{id}")
                        .Description("Download a file");
            }
            """;

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal("download", ep.Name);
        Assert.Equal("application/octet-stream", ep.FileContentType);
        Assert.Null(ep.ReturnType);
        Assert.Contains("Promise<Blob>", client);
        Assert.Contains("blob: true", client);
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
                    Define.Get<(byte[] Content, string FileName)>("/api/files/{id}");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = Rivet.Tool.Emit.OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

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
                    Define.Get<byte[]>("/api/files/{id}");
            }
            """;

        var (endpoints, client) = Generate(source);

        Assert.Single(endpoints);
        Assert.Equal("application/octet-stream", endpoints[0].FileContentType);
        Assert.Null(endpoints[0].ReturnType);
        Assert.Contains("Promise<Blob>", client);
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
                public static readonly Define Upload =
                    Define.Post<UploadInput, UploadResult>("/api/uploads");
            }
            """;

        var (endpoints, client) = Generate(source);

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

        // Client should append text fields to FormData
        Assert.Contains("fd.append(\"title\", title)", client);
        Assert.Contains("fd.append(\"categoryId\", JSON.stringify(categoryId))", client);
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

        var (endpoints, client) = Generate(source);
        var ep = Assert.Single(endpoints);

        Assert.True(ep.IsFormEncoded, "Endpoint should have IsFormEncoded=true");

        // Client should emit URLSearchParams for form-encoded body
        Assert.Contains("URLSearchParams", client);
        Assert.Contains("formEncoded: true", client);
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
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);

        Assert.Contains("application/x-www-form-urlencoded", json);
        Assert.DoesNotContain("\"application/json\"", json.Split("requestBody")[1].Split("responses")[0]);
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
                    Define.Put<ItemDto, ItemDto>("/api/items/{id}")
                        .Returns(404, "Not found")
                        .Returns(409);
            }
            """;

        var (endpoints, client) = Generate(source);
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

        // Client result DU should have void for both
        Assert.Contains("status: 404; data: void", client);
        Assert.Contains("status: 409; data: void", client);
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
                    Define.Put<ItemDto, ItemDto>("/api/items/{id}")
                        .Returns(404);
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var doc = System.Text.Json.JsonDocument.Parse(json);

        var resp404 = doc.RootElement
            .GetProperty("paths").GetProperty("/api/items/{id}")
            .GetProperty("put").GetProperty("responses")
            .GetProperty("404");

        Assert.False(resp404.TryGetProperty("content", out _),
            "Void 404 response should have no content block");
    }

    // ========== GAP-3: JsonStringEnumMemberName forward pipeline ==========

    [Fact]
    public void JsonStringEnumMemberName_Preserves_Original_In_Forward_Pipeline()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            public enum Status
            {
                [JsonStringEnumMemberName("in-progress")]
                InProgress,
                [JsonStringEnumMemberName("on_hold")]
                OnHold,
                Done
            }

            [RivetType]
            public sealed record ItemDto(string Name, Status Status);

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition<ItemDto> GetItem =
                    Define.Get<ItemDto>("/api/items/{id}");
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
    }

    // ========== GAP-4: DELETE default 204 isolated forward test ==========

    [Fact]
    public void Delete_Without_Status_Defaults_To_204()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class ItemContract
            {
                public static readonly RouteDefinition DeleteItem =
                    Define.Delete("/api/items/{id}");
            }
            """;

        var (endpoints, _) = Generate(source);
        var ep = Assert.Single(endpoints);

        var successResp = ep.Responses.FirstOrDefault(r => r.StatusCode is >= 200 and < 300);
        Assert.NotNull(successResp);
        Assert.Equal(204, successResp.StatusCode);
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

        var (endpoints, client) = Generate(source);
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

        var (endpoints, _) = Generate(source);
        var ep = Assert.Single(endpoints);
        Assert.True(ep.IsFormEncoded);
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

        var (endpoints, _) = Generate(source);
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
                    Define.Get<ItemDto>("/api/items/{id}")
                        .Summary("Get an item")
                        .Description("Retrieves a single item by its unique identifier");
            }
            """;

        var (endpoints, _) = Generate(source);
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
                    Define.Get<ItemDto>("/api/items/{id}")
                        .Summary("Get an item")
                        .Description("Retrieves a single item by its unique identifier");
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var json = Rivet.Tool.Emit.OpenApiEmitter.Emit(endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var doc = System.Text.Json.JsonDocument.Parse(json);

        var operation = doc.RootElement
            .GetProperty("paths").GetProperty("/api/items/{id}")
            .GetProperty("get");

        Assert.Equal("Get an item", operation.GetProperty("summary").GetString());
        Assert.Equal("Retrieves a single item by its unique identifier",
            operation.GetProperty("description").GetString());
    }
}
