using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class ClientEmitterTests
{
    private static (string Types, string Client) Generate(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = EndpointWalker.Walk(walker, discovered.EndpointMethods, discovered.ClientTypes);
        var definitions = walker.Definitions.Values.ToList();
        var brands = walker.Brands.Values.ToList();
        var typeGrouping = TypeGrouper.Group(definitions, brands, walker.Enums, walker.TypeNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var types = string.Concat(typeGrouping.Groups.Select(TypeEmitter.EmitGroupFile));
        var client = ClientEmitter.EmitControllerClient("endpoints", endpoints, typeFileMap);
        return (types, client);
    }

    [Fact]
    public void SimplePostEndpoint_EmitsCorrectly()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CreateMessageCommand(Guid SubmissionId, string Body);

            [RivetType]
            public sealed record MessageDto(Guid Id, string Body, string AuthorName, DateTime CreatedAt);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpPost("/api/submissions/{id}/messages")]
                public static Task<MessageDto> CreateMessage(
                    [FromRoute] Guid id,
                    [FromBody] CreateMessageCommand body)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        Assert.Contains("""import type { CreateMessageCommand, MessageDto } from "../types/test.js";""", client);
        // Overload signatures
        Assert.Contains("export function createMessage(id: string, body: CreateMessageCommand): Promise<MessageDto>;", client);
        Assert.Contains("export function createMessage(id: string, body: CreateMessageCommand, opts: { unwrap: true }): Promise<MessageDto>;", client);
        Assert.Contains("export function createMessage(id: string, body: CreateMessageCommand, opts: { unwrap: false }): Promise<RivetResult<MessageDto>>;", client);
        // Implementation body
        Assert.Contains("""rivetFetch<RivetResult<MessageDto>>("POST", `/api/submissions/${id}/messages`, { body: body, unwrap: opts?.unwrap });""", client);
    }

    [Fact]
    public void GetEndpoint_NoBody()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SubmissionDto(Guid Id, string Reference);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/submissions/{id}")]
                public static Task<SubmissionDto> GetSubmission(
                    [FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        Assert.Contains("export function getSubmission(id: string): Promise<SubmissionDto>;", client);
        Assert.Contains("""rivetFetch<RivetResult<SubmissionDto>>("GET", `/api/submissions/${id}`, { unwrap: opts?.unwrap });""", client);
    }

    [Fact]
    public void DeleteEndpoint_VoidReturn()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpDelete("/api/submissions/{id}")]
                public static Task DeleteSubmission(
                    [FromRoute] Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        Assert.Contains("export function deleteSubmission(id: string): Promise<void>;", client);
        Assert.Contains("export function deleteSubmission(id: string, opts: { unwrap: false }): Promise<RivetResult<void>>;", client);
        Assert.Contains("""rivetFetch<RivetResult<void>>("DELETE", `/api/submissions/${id}`, { unwrap: opts?.unwrap });""", client);
    }

    [Fact]
    public void QueryParams_EmittedCorrectly()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record SubmissionDto(Guid Id, string Reference);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/submissions")]
                public static Task<List<SubmissionDto>> ListSubmissions(
                    [FromQuery] string status,
                    [FromQuery] int page)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        Assert.Contains("export function listSubmissions(status: string, page: number): Promise<SubmissionDto[]>;", client);
        Assert.Contains("query: { status, page }", client);
    }

    [Fact]
    public void DiParams_Skipped()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public interface IMyService { }

            [RivetType]
            public sealed record ResultDto(string Value);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/things/{id}")]
                public static Task<ResultDto> GetThing(
                    [FromRoute] Guid id,
                    IMyService service,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        // Only route param should appear, DI params skipped
        Assert.Contains("export function getThing(id: string): Promise<ResultDto>;", client);
        Assert.DoesNotContain("service:", client);
        Assert.DoesNotContain("ct:", client);
    }

    [Fact]
    public void MultipleEndpoints_AllEmitted()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(Guid Id, string Name);
            [RivetType]
            public sealed record CreateItemCommand(string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/items/{id}")]
                public static Task<ItemDto> GetItem([FromRoute] Guid id)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpPost("/api/items")]
                public static Task<ItemDto> CreateItem([FromBody] CreateItemCommand body)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        Assert.Contains("export function getItem", client);
        Assert.Contains("export function createItem", client);
    }

    [Fact]
    public void RivetFetchBoilerplate_Emitted()
    {
        var rivetBase = ClientEmitter.EmitRivetBase();

        Assert.Contains("export type RivetConfig", rivetBase);
        Assert.Contains("export type RivetResult<T>", rivetBase);
        Assert.Contains("export const configureRivet", rivetBase);
        Assert.Contains("export const rivetFetch", rivetBase);
        Assert.Contains("unwrap?: boolean", rivetBase);
    }

    [Fact]
    public void ComplexScenario_MatchesSpec()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public enum MessageVisibility { Internal, Public }

            [RivetType]
            public sealed record CreateMessageCommand(
                Guid SubmissionId,
                string Body,
                MessageVisibility Visibility);

            [RivetType]
            public sealed record MessageDto(
                Guid Id,
                string Body,
                string AuthorName,
                DateTime CreatedAt);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpPost("/api/submissions/{id}/messages")]
                public static Task<MessageDto> CreateMessage(
                    [FromRoute] Guid id,
                    [FromBody] CreateMessageCommand body)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        Assert.Contains("export function createMessage(id: string, body: CreateMessageCommand): Promise<MessageDto>;", client);
        Assert.Contains("""rivetFetch<RivetResult<MessageDto>>("POST", `/api/submissions/${id}/messages`, { body: body, unwrap: opts?.unwrap });""", client);
    }

    [Fact]
    public void MultiResponse_EmitsResultDU()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDetailDto(Guid Id, string Title);
            [RivetType]
            public sealed record NotFoundDto(string Message);

            [RivetClient]
            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet("{id}")]
                [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
                [ProducesResponseType(typeof(NotFoundDto), StatusCodes.Status404NotFound)]
                public async Task<IActionResult> Get(Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        // Result DU type with catch-all arm
        Assert.Contains("export type GetResult =", client);
        Assert.Contains("{ status: 200; data: TaskDetailDto; response: Response }", client);
        Assert.Contains("{ status: 404; data: NotFoundDto; response: Response }", client);
        Assert.Contains("{ status: Exclude<number, 200 | 404>; data: unknown; response: Response }", client);

        // Overloads use GetResult for unwrap: false
        Assert.Contains("export function get(id: string): Promise<TaskDetailDto>;", client);
        Assert.Contains("export function get(id: string, opts: { unwrap: false }): Promise<GetResult>;", client);
    }

    [Fact]
    public void SingleResponse_UsesRivetResult()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TaskDetailDto(Guid Id, string Title);

            [RivetClient]
            [Route("api/tasks")]
            public sealed class TasksController : ControllerBase
            {
                [HttpGet("{id}")]
                [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
                public async Task<IActionResult> Get(Guid id)
                    => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        // Single response → no DU, uses RivetResult<T>
        Assert.DoesNotContain("export type GetResult", client);
        Assert.Contains("export function get(id: string, opts: { unwrap: false }): Promise<RivetResult<TaskDetailDto>>;", client);
    }

    [Fact]
    public void FalsyBodyHandling_UsesNullCheck()
    {
        var rivetBase = ClientEmitter.EmitRivetBase();

        // Must use != null, not truthiness check, to avoid dropping 0/false/""
        Assert.Contains("options?.body != null && !isFormData", rivetBase);
        Assert.Contains("options?.body != null ?", rivetBase);
        Assert.DoesNotContain("options?.body &&", rivetBase);
        Assert.DoesNotContain("options?.body ?", rivetBase);
    }

    [Fact]
    public void ReservedWordFunctionName_Delete_BecomesRemove()
    {
        // The function name "delete" should be replaced with "remove"
        var funcName = typeof(ClientEmitter)
            .GetMethod("SafeFunctionName", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, ["delete"]) as string;
        Assert.Equal("remove", funcName);

        var funcName2 = typeof(ClientEmitter)
            .GetMethod("SafeFunctionName", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, ["class"]) as string;
        Assert.Equal("_class", funcName2);
    }
}
