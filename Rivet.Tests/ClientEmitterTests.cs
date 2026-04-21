using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ClientEmitterTests
{
    private static (string Types, string Client) Generate(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var definitions = walker.Definitions.Values.ToList();
        var brands = walker.Brands.Values.ToList();

        // Extraction pass — matches EmitPipeline.RunAsync wiring
        var extraction = InlineTypeExtractor.Extract(endpoints, definitions);
        var allDefinitions = definitions.Concat(extraction.ExtractedTypes).ToList();
        var allNamespaces = new Dictionary<string, string?>(walker.TypeNamespaces);
        foreach (var (name, ns) in extraction.TypeNamespaces)
            allNamespaces[name] = ns;

        var typeGrouping = TypeGrouper.Group(allDefinitions, brands, walker.Enums, allNamespaces);
        var typeFileMap = typeGrouping.BuildTypeFileMap();
        var types = string.Concat(typeGrouping.Groups.Select(TypeEmitter.EmitGroupFile));
        var client = ClientEmitter.EmitControllerClient("endpoints", extraction.Endpoints, typeFileMap);
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
        Assert.Contains("export function createMessage(input: { params: { id: string; }; body: CreateMessageCommand; }): Promise<MessageDto>;", client);
        Assert.Contains("export function createMessage(input: { params: { id: string; }; body: CreateMessageCommand; }, opts: { unwrap: true }): Promise<MessageDto>;", client);
        Assert.Contains("export function createMessage(input: { params: { id: string; }; body: CreateMessageCommand; }, opts: { unwrap: false }): Promise<RivetResult<MessageDto>>;", client);
        // Implementation body
        Assert.Contains("""rivetFetch<RivetResult<MessageDto>>("POST", `/api/submissions/${encodeURIComponent(String(input.params.id))}/messages`, { body: input.body, unwrap: opts?.unwrap });""", client);
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

        Assert.Contains("export function getSubmission(input: { params: { id: string; }; }): Promise<SubmissionDto>;", client);
        Assert.Contains("""rivetFetch<RivetResult<SubmissionDto>>("GET", `/api/submissions/${encodeURIComponent(String(input.params.id))}`, { unwrap: opts?.unwrap });""", client);
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

        Assert.Contains("export function deleteSubmission(input: { params: { id: string; }; }): Promise<void>;", client);
        Assert.Contains("export function deleteSubmission(input: { params: { id: string; }; }, opts: { unwrap: false }): Promise<RivetResult<void>>;", client);
        Assert.Contains("""rivetFetch<RivetResult<void>>("DELETE", `/api/submissions/${encodeURIComponent(String(input.params.id))}`, { unwrap: opts?.unwrap });""", client);
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

        Assert.Contains("export function listSubmissions(input: { query: { status: string; page: number; }; }): Promise<SubmissionDto[]>;", client);
        Assert.Contains("query: input.query", client);
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
        Assert.Contains("export function getThing(input: { params: { id: string; }; }): Promise<ResultDto>;", client);
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
        Assert.Contains("export type RivetResultOf<TResult extends RivetRawResult>", rivetBase);
        Assert.Contains("export const configureRivet", rivetBase);
        Assert.Contains("export const getBaseUrl", rivetBase);
        Assert.Contains("export const rivetFetch", rivetBase);
        Assert.Contains("unwrap?: boolean", rivetBase);
        Assert.Contains("isSuccessful(): boolean;", rivetBase);
        Assert.Contains("isOk(): this is RivetStatusMatch<TResult, 200>;", rivetBase);
        Assert.Contains("isRedirect(location?: string): boolean;", rivetBase);
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

        Assert.Contains("export function createMessage(input: { params: { id: string; }; body: CreateMessageCommand; }): Promise<MessageDto>;", client);
        Assert.Contains("""rivetFetch<RivetResult<MessageDto>>("POST", `/api/submissions/${encodeURIComponent(String(input.params.id))}/messages`, { body: input.body, unwrap: opts?.unwrap });""", client);
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
        Assert.Contains("import { rivetFetch, type RivetResult, type RivetResultOf } from \"../rivet.js\";", client);
        Assert.Contains("export type GetResult = RivetResultOf<", client);
        Assert.Contains("{ status: 200; data: TaskDetailDto; response: Response }", client);
        Assert.Contains("{ status: 404; data: NotFoundDto; response: Response }", client);
        Assert.Contains("{ status: Exclude<number, 200 | 404>; data: unknown; response: Response }", client);

        // Overloads use GetResult for unwrap: false
        Assert.Contains("export function get(input: { params: { id: string; }; }): Promise<TaskDetailDto>;", client);
        Assert.Contains("export function get(input: { params: { id: string; }; }, opts: { unwrap: false }): Promise<GetResult>;", client);
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
        Assert.Contains("export function get(input: { params: { id: string; }; }, opts: { unwrap: false }): Promise<RivetResult<TaskDetailDto>>;", client);
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

    [Fact]
    public void AccessProperty_UsesDotOrBracketNotation()
    {
        var accessProperty = typeof(ClientEmitter)
            .GetMethod("AccessProperty", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        Assert.Equal("input.query.class", accessProperty.Invoke(null, ["input.query", "class"]) as string);
        Assert.Equal("input.query.normalName", accessProperty.Invoke(null, ["input.query", "normalName"]) as string);
        Assert.Equal("input.query[\"kebab-case\"]", accessProperty.Invoke(null, ["input.query", "kebab-case"]) as string);
    }

    [Fact]
    public void InlineTypeExtracted_ImportedInClient()
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
                [HttpGet("/api/buyers/{id}")]
                public static Task<(Guid Id, string Name)> FindBuyer(
                    [FromRoute] Guid id)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpGet("/api/buyers")]
                public static Task<(Guid Id, string Name)[]> ListBuyers()
                    => throw new NotImplementedException();
            }
            """;

        var (types, client) = Generate(source);

        // Extracted type should exist in types output
        Assert.Contains("export type EndpointFindBuyerDto", types);

        // Client should import the extracted type
        Assert.Contains("import type { EndpointFindBuyerDto }", client);

        // Client should use the extracted name, not an inline literal
        Assert.Contains("Promise<EndpointFindBuyerDto>", client);
        Assert.DoesNotContain("{ id: string; name: string }", client);
    }

    // --- RequestType tests (hand-built endpoints for PHP contract support) ---

    private static TsEndpointDefinition MakeEndpoint(
        string name = "createBuyer",
        string method = "POST",
        string route = "/api/buyers",
        IReadOnlyList<TsEndpointParam>? @params = null,
        TsType? returnType = null,
        string controller = "buyer",
        TsType? requestType = null,
        bool isFormEncoded = false,
        IReadOnlyList<TsResponseType>? responses = null,
        QueryAuthMetadata? queryAuth = null) =>
        new(
            Name: name,
            HttpMethod: method,
            RouteTemplate: route,
            Params: @params ?? [],
            ReturnType: returnType,
            ControllerName: controller,
            Responses: responses ?? [],
            IsFormEncoded: isFormEncoded,
            RequestType: requestType,
            QueryAuth: queryAuth);

    [Fact]
    public void RequestType_TypeRef_EmitsBodyParam()
    {
        var endpoint = MakeEndpoint(
            requestType: new TsType.TypeRef("CreateBuyerRequest"),
            returnType: new TsType.TypeRef("BuyerDto"));

        var typeFileMap = new Dictionary<string, string>
        {
            ["CreateBuyerRequest"] = "buyer",
            ["BuyerDto"] = "buyer",
        };

        var output = ClientEmitter.EmitControllerClient("buyer", [endpoint], typeFileMap);

        // All three overloads should include body param
        Assert.Contains("export function createBuyer(input: { body: CreateBuyerRequest; }): Promise<BuyerDto>;", output);
        Assert.Contains("export function createBuyer(input: { body: CreateBuyerRequest; }, opts: { unwrap: true }): Promise<BuyerDto>;", output);
        Assert.Contains("export function createBuyer(input: { body: CreateBuyerRequest; }, opts: { unwrap: false }): Promise<RivetResult<BuyerDto>>;", output);
        // Fetch options should include body
        Assert.Contains("body: input.body", output);
        // Import should include the request type
        Assert.Contains("CreateBuyerRequest", output.Split('\n').First(l => l.StartsWith("import type")));
    }

    [Fact]
    public void RequestType_InlineObject_EmitsBodyParam()
    {
        var endpoint = MakeEndpoint(
            requestType: new TsType.InlineObject([
                ("id", new TsType.Primitive("number", "int32")),
                ("name", new TsType.Primitive("string")),
            ]),
            returnType: new TsType.Primitive("void"));

        var output = ClientEmitter.EmitControllerClient("buyer", [endpoint], new Dictionary<string, string>());

        Assert.Contains("input: { body: { id: number; name: string; }; }", output);
        Assert.Contains("body: input.body", output);
    }

    [Fact]
    public void RequestType_FormEncoded_EmitsUrlSearchParams()
    {
        var endpoint = MakeEndpoint(
            requestType: new TsType.TypeRef("LoginRequest"),
            returnType: new TsType.Primitive("void"),
            isFormEncoded: true);

        var typeFileMap = new Dictionary<string, string> { ["LoginRequest"] = "auth" };
        var output = ClientEmitter.EmitControllerClient("auth", [endpoint], typeFileMap);

        Assert.Contains("body: new URLSearchParams(input.body as Record<string, string>)", output);
        Assert.Contains("formEncoded: true", output);
        // Ensure raw body: input.body is not emitted (URLSearchParams wrapping is used instead)
        var fetchLines = output.Split('\n').Where(l => l.Contains("body:")).ToList();
        Assert.All(fetchLines, l => Assert.DoesNotContain("body: input.body", l));
    }

    [Fact]
    public void RequestType_Null_NoBodyParam()
    {
        var endpoint = MakeEndpoint(
            name: "listBuyers",
            method: "GET",
            route: "/api/buyers",
            returnType: new TsType.Primitive("void"),
            requestType: null);

        var output = ClientEmitter.EmitControllerClient("buyer", [endpoint], new Dictionary<string, string>());

        Assert.DoesNotContain("body:", output);
    }

    [Fact]
    public void RequestType_IgnoredWhenBodyParamExists()
    {
        var endpoint = MakeEndpoint(
            method: "POST",
            @params: [new TsEndpointParam("body", new TsType.TypeRef("LegacyRequest"), ParamSource.Body)],
            requestType: new TsType.TypeRef("CreateBuyerRequest"),
            returnType: new TsType.Primitive("void"));

        var typeFileMap = new Dictionary<string, string>
        {
            ["LegacyRequest"] = "buyer",
            ["CreateBuyerRequest"] = "buyer",
        };

        var output = ClientEmitter.EmitControllerClient("buyer", [endpoint], typeFileMap);

        Assert.Contains("input: { body: LegacyRequest; }", output);
        // Signature should NOT contain the RequestType name
        var signatures = output.Split('\n').Where(l => l.StartsWith("export function")).ToList();
        Assert.All(signatures, s => Assert.DoesNotContain("CreateBuyerRequest", s));
        // Import should NOT contain the unused RequestType
        var importLines = output.Split('\n').Where(l => l.StartsWith("import type")).ToList();
        Assert.All(importLines, l => Assert.DoesNotContain("CreateBuyerRequest", l));
    }

    [Fact]
    public void RequestType_IgnoredWhenFileParamsExist()
    {
        var endpoint = MakeEndpoint(
            name: "uploadDocument",
            route: "/api/documents",
            @params: [new TsEndpointParam("file", new TsType.Primitive("File"), ParamSource.File)],
            requestType: new TsType.TypeRef("UploadRequest"),
            returnType: new TsType.Primitive("void"));

        var output = ClientEmitter.EmitControllerClient("docs", [endpoint], new Dictionary<string, string>());

        // File param should appear in signature, but NOT a body param from RequestType
        Assert.Contains("input: { body: { file: File; }; }", output);
        var signatures = output.Split('\n').Where(l => l.StartsWith("export function")).ToList();
        Assert.All(signatures, s => Assert.DoesNotContain("UploadRequest", s));
        // Fetch options should use fd (FormData), not body
        Assert.Contains("body: fd", output);
    }

    [Fact]
    public void RequestType_WithRouteParam_EmitsBothInSignature()
    {
        var endpoint = MakeEndpoint(
            route: "/api/buyers/{id}",
            @params: [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
            requestType: new TsType.TypeRef("CreateBuyerRequest"),
            returnType: new TsType.TypeRef("BuyerDto"));

        var typeFileMap = new Dictionary<string, string>
        {
            ["CreateBuyerRequest"] = "buyer",
            ["BuyerDto"] = "buyer",
        };

        var output = ClientEmitter.EmitControllerClient("buyer", [endpoint], typeFileMap);

        Assert.Contains("export function createBuyer(input: { params: { id: string; }; body: CreateBuyerRequest; }): Promise<BuyerDto>;", output);
        Assert.Contains("export function createBuyer(input: { params: { id: string; }; body: CreateBuyerRequest; }, opts: { unwrap: true }): Promise<BuyerDto>;", output);
        Assert.Contains("export function createBuyer(input: { params: { id: string; }; body: CreateBuyerRequest; }, opts: { unwrap: false }): Promise<RivetResult<BuyerDto>>;", output);
    }

    [Fact]
    public void RequestType_WithQueryParam_EmitsBothBodyAndQuery()
    {
        var endpoint = MakeEndpoint(
            route: "/api/buyers",
            @params: [new TsEndpointParam("status", new TsType.Primitive("string"), ParamSource.Query)],
            requestType: new TsType.TypeRef("CreateBuyerRequest"),
            returnType: new TsType.Primitive("void"));

        var typeFileMap = new Dictionary<string, string> { ["CreateBuyerRequest"] = "buyer" };
        var output = ClientEmitter.EmitControllerClient("buyer", [endpoint], typeFileMap);

        Assert.Contains("export function createBuyer(input: { query: { status: string; }; body: CreateBuyerRequest; }): Promise<void>;", output);
        Assert.Contains("body: input.body", output);
        Assert.Contains("query: input.query", output);
    }

    [Fact]
    public void RequestType_WithMultiResponse_EmitsResultDUAndBodyParam()
    {
        var endpoint = MakeEndpoint(
            requestType: new TsType.TypeRef("CreateBuyerRequest"),
            returnType: new TsType.TypeRef("BuyerDto"),
            responses:
            [
                new TsResponseType(200, new TsType.TypeRef("BuyerDto")),
                new TsResponseType(422, new TsType.TypeRef("ValidationError")),
            ]);

        var typeFileMap = new Dictionary<string, string>
        {
            ["CreateBuyerRequest"] = "buyer",
            ["BuyerDto"] = "buyer",
            ["ValidationError"] = "buyer",
        };

        var output = ClientEmitter.EmitControllerClient("buyer", [endpoint], typeFileMap);

        // Result DU should be emitted
        Assert.Contains("export type CreateBuyerResult =", output);
        Assert.Contains("{ status: 200; data: BuyerDto; response: Response }", output);
        Assert.Contains("{ status: 422; data: ValidationError; response: Response }", output);
        // Body param in all overloads including the DU variant
        Assert.Contains("export function createBuyer(input: { body: CreateBuyerRequest; }): Promise<BuyerDto>;", output);
        Assert.Contains("export function createBuyer(input: { body: CreateBuyerRequest; }, opts: { unwrap: false }): Promise<CreateBuyerResult>;", output);
    }

    [Fact]
    public void InlineTypeExtracted_ResultType_UsesName()
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
                [HttpGet("/api/items/{id}")]
                [ProducesResponseType(typeof((Guid Id, string Title)), 200)]
                [ProducesResponseType(404)]
                public static Task<IActionResult> GetItem(
                    [FromRoute] Guid id)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpGet("/api/items")]
                [ProducesResponseType(typeof((Guid Id, string Title)[]), 200)]
                public static Task<IActionResult> ListItems()
                    => throw new NotImplementedException();
            }
            """;

        var (types, client) = Generate(source);

        // Extracted type should appear in types (method name now included)
        Assert.Contains("export type EndpointGetItemDto", types);

        // Discriminated union result type should use the extracted name
        Assert.Contains("data: EndpointGetItemDto", client);
        Assert.DoesNotContain("{ id: string; title: string }", client);
    }

    // --- QueryAuth *Url() function tests ---

    [Fact]
    public void QueryAuth_EmitsUrlFunction()
    {
        var endpoint = MakeEndpoint(
            name: "getStream",
            method: "GET",
            route: "/api/streams/{id}",
            @params: [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
            returnType: new TsType.Primitive("void"),
            queryAuth: new QueryAuthMetadata("token"));

        var output = ClientEmitter.EmitControllerClient("streams", [endpoint], new Dictionary<string, string>());

        // Standard fetch function still emitted
        Assert.Contains("export function getStream(input: { params: { id: string; }; }): Promise<void>;", output);

        // *Url() companion function emitted
        Assert.Contains("export function getStreamUrl(input: { params: { id: string; }; query: { token: string; }; }): string {", output);
        Assert.Contains("getBaseUrl()", output);
        Assert.Contains("token=${encodeURIComponent(input.query.token)}", output);
    }

    [Fact]
    public void QueryAuth_CustomParameterName()
    {
        var endpoint = MakeEndpoint(
            name: "getStream",
            method: "GET",
            route: "/api/streams/{id}",
            @params: [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
            returnType: new TsType.Primitive("void"),
            queryAuth: new QueryAuthMetadata("key"));

        var output = ClientEmitter.EmitControllerClient("streams", [endpoint], new Dictionary<string, string>());

        Assert.Contains("export function getStreamUrl(input: { params: { id: string; }; query: { key: string; }; }): string {", output);
        Assert.Contains("key=${encodeURIComponent(input.query.key)}", output);
        Assert.DoesNotContain("token", output.Split('\n').First(l => l.Contains("getStreamUrl")));
    }

    [Fact]
    public void QueryAuth_ImportsGetBaseUrl()
    {
        var endpoint = MakeEndpoint(
            name: "getStream",
            method: "GET",
            route: "/api/streams",
            returnType: new TsType.Primitive("void"),
            queryAuth: new QueryAuthMetadata("token"));

        var output = ClientEmitter.EmitControllerClient("streams", [endpoint], new Dictionary<string, string>());

        Assert.Contains("import { rivetFetch, getBaseUrl, type RivetResult } from \"../rivet.js\";", output);
    }

    [Fact]
    public void NoQueryAuth_NoUrlFunction_NoGetBaseUrlImport()
    {
        var endpoint = MakeEndpoint(
            name: "getItem",
            method: "GET",
            route: "/api/items/{id}",
            @params: [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
            returnType: new TsType.Primitive("void"));

        var output = ClientEmitter.EmitControllerClient("items", [endpoint], new Dictionary<string, string>());

        Assert.DoesNotContain("Url(", output);
        Assert.DoesNotContain("getBaseUrl", output);
        Assert.Contains("import { rivetFetch, type RivetResult } from \"../rivet.js\";", output);
    }

    [Fact]
    public void QueryAuth_WithExistingQueryParams_MergesInUrl()
    {
        var endpoint = MakeEndpoint(
            name: "getStream",
            method: "GET",
            route: "/api/streams",
            @params: [new TsEndpointParam("quality", new TsType.Primitive("string"), ParamSource.Query)],
            returnType: new TsType.Primitive("void"),
            queryAuth: new QueryAuthMetadata("token"));

        var output = ClientEmitter.EmitControllerClient("streams", [endpoint], new Dictionary<string, string>());

        // Url function should include both the query param and the token
        var urlLine = output.Split('\n').First(l => l.Contains("getStreamUrl"));
        Assert.Contains("input: { query: { quality: string; token: string; }; }", urlLine);

        // Both query param and token in the URL
        Assert.Contains("quality=${encodeURIComponent(String(input.query.quality))}", output);
        Assert.Contains("token=${encodeURIComponent(input.query.token)}", output);
    }

    [Fact]
    public void QueryAuth_ReturnsStringNotPromise()
    {
        var endpoint = MakeEndpoint(
            name: "getStream",
            method: "GET",
            route: "/api/streams",
            returnType: new TsType.Primitive("void"),
            queryAuth: new QueryAuthMetadata("token"));

        var output = ClientEmitter.EmitControllerClient("streams", [endpoint], new Dictionary<string, string>());

        Assert.Contains("): string {", output);
        // The Url function should not contain rivetFetch
        var urlFuncLines = output.Split('\n')
            .SkipWhile(l => !l.Contains("getStreamUrl"))
            .TakeWhile(l => !l.StartsWith("}") || l.Contains("getStreamUrl"))
            .ToList();
        Assert.DoesNotContain(urlFuncLines, l => l.Contains("rivetFetch"));
    }
}
