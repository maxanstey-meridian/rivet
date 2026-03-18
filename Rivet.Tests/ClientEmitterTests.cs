using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class ClientEmitterTests
{
    private static (string Types, string Client) Generate(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var endpoints = EndpointWalker.Walk(compilation, walker);
        var types = TypeEmitter.Emit([.. walker.Definitions.Values], enums: walker.Enums);
        var client = ClientEmitter.Emit(endpoints, [.. walker.Definitions.Values]);
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

        Assert.Contains("""import type { CreateMessageCommand, MessageDto } from "./types.js";""", client);
        Assert.Contains("export const createMessage = (id: string, body: CreateMessageCommand): Promise<MessageDto> =>", client);
        Assert.Contains("""rivetFetch<MessageDto>("POST", `/api/submissions/${id}/messages`, { body: body });""", client);
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

        Assert.Contains("export const getSubmission = (id: string): Promise<SubmissionDto> =>", client);
        Assert.Contains("""rivetFetch<SubmissionDto>("GET", `/api/submissions/${id}`);""", client);
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

        Assert.Contains("export const deleteSubmission = (id: string): Promise<void> =>", client);
        Assert.Contains("""rivetFetch<void>("DELETE", `/api/submissions/${id}`);""", client);
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

        Assert.Contains("export const listSubmissions = (status: string, page: number): Promise<SubmissionDto[]> =>", client);
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
        Assert.Contains("export const getThing = (id: string): Promise<ResultDto> =>", client);
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

        Assert.Contains("export const getItem", client);
        Assert.Contains("export const createItem", client);
    }

    [Fact]
    public void RivetFetchBoilerplate_Emitted()
    {
        var source = """
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Dto(string Name);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpGet("/api/test")]
                public static Task<Dto> Test() => throw new NotImplementedException();
            }
            """;

        var (_, client) = Generate(source);

        Assert.Contains("export type RivetConfig", client);
        Assert.Contains("export const configureRivet", client);
        Assert.Contains("const rivetFetch", client);
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

        // Matches the spec example from RIVET.md
        Assert.Contains("export const createMessage = (id: string, body: CreateMessageCommand): Promise<MessageDto> =>", client);
        Assert.Contains("""rivetFetch<MessageDto>("POST", `/api/submissions/${id}/messages`, { body: body });""", client);
    }
}
