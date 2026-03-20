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
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
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
        Assert.Equal(ParamSource.Route, ep.Params.First(p => p.Name == "id").Source);
        Assert.Equal(ParamSource.Query, ep.Params.First(p => p.Name == "status").Source);
        Assert.Equal(ParamSource.Query, ep.Params.First(p => p.Name == "page").Source);

        Assert.Contains("Promise<TaskDto>", client);
        Assert.Contains("`/api/tasks/${id}`", client);
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

        var bodyParam = ep.Params.First(p => p.Source == ParamSource.Body);
        Assert.Equal("body", bodyParam.Name);

        Assert.Contains("body: body", client);
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
        Assert.Equal(2, ep.Responses.Count);
        Assert.Equal(200, ep.Responses[0].StatusCode);
        Assert.Equal(404, ep.Responses[1].StatusCode);
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
        Assert.Contains(ep.Responses, r => r.StatusCode == 201 && r.DataType is not null);
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
        Assert.Equal(3, ep.Responses.Count);
        Assert.Equal(200, ep.Responses[0].StatusCode);
        Assert.Equal(404, ep.Responses[1].StatusCode);
        Assert.Equal(409, ep.Responses[2].StatusCode);
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
        var contractEndpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
        var controllerEndpoints = EndpointWalker.Walk(walker, discovered.EndpointMethods, discovered.ClientTypes);

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
        var endpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);

        Assert.Single(endpoints);
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
        var notFoundResponse = endpoints[0].Responses.First(r => r.StatusCode == 404);
        Assert.Equal("Task not found", notFoundResponse.Description);

        // Success response has no description
        var successResponse = endpoints[0].Responses.First(r => r.StatusCode == 200);
        Assert.Null(successResponse.Description);
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
        Assert.Equal("Task not found", endpoints[0].Responses.First(r => r.StatusCode == 404).Description);
    }

    // --- Abstract base class contract tests ---

    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, string Client) GenerateWithBothWalkers(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var contractEndpoints = ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
        var annotationEndpoints = EndpointWalker.Walk(walker, discovered.EndpointMethods, discovered.ClientTypes);

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
        var endpoints = Rivet.Tool.Analysis.ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
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
        var endpoints = Rivet.Tool.Analysis.ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
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
        var endpoints = Rivet.Tool.Analysis.ContractWalker.Walk(compilation, walker, discovered.ContractTypes);
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
}
