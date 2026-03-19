using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class FormFileTests
{
    private static Dictionary<string, string> BuildTypeFileMap(TypeWalker walker)
    {
        var typeGrouping = TypeGrouper.Group(
            walker.Definitions.Values.ToList(),
            walker.Brands.Values.ToList(),
            walker.Enums,
            walker.TypeNamespaces);
        return typeGrouping.BuildTypeFileMap();
    }

    private static string GenerateContractClient(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var endpoints = ContractWalker.Walk(compilation, walker);
        var typeFileMap = BuildTypeFileMap(walker);
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        return string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
    }

    [Fact]
    public void IFormFile_EmitsFileParam_WithFormData()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FileUploadResult(Guid Id, string FileName);

            [Route("api/files")]
            public sealed class FilesController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(FileUploadResult), 201)]
                public Task<IActionResult> Upload(
                    IFormFile file,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var endpoints = EndpointWalker.Walk(compilation, walker);
        var typeFileMap = BuildTypeFileMap(walker);
        var client = ClientEmitter.EmitControllerClient("files", endpoints, typeFileMap);

        // Function signature takes File
        Assert.Contains("file: File", client);
        // FormData construction
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("fd.append(\"file\", file);", client);
        // Body is fd
        Assert.Contains("body: fd", client);
        // Return type is correct
        Assert.Contains("Promise<FileUploadResult>", client);
    }

    [Fact]
    public void IFormFile_WithRouteParam()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AttachmentResult(Guid Id);

            [Route("api/tasks")]
            public sealed class TasksController
            {
                [RivetEndpoint]
                [HttpPost("{id:guid}/attachments")]
                [ProducesResponseType(typeof(AttachmentResult), 201)]
                public Task<IActionResult> Attach(
                    Guid id,
                    IFormFile file,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var endpoints = EndpointWalker.Walk(compilation, walker);
        var typeFileMap = BuildTypeFileMap(walker);
        var client = ClientEmitter.EmitControllerClient("tasks", endpoints, typeFileMap);

        // Both route param and file param
        Assert.Contains("id: string, file: File", client);
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("fd.append(\"file\", file);", client);
        Assert.Contains("${id}", client);
    }

    [Fact]
    public void IFormFile_VoidReturn()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/avatars")]
            public sealed class AvatarsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(200)]
                public Task<IActionResult> Upload(
                    IFormFile avatar,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var endpoints = EndpointWalker.Walk(compilation, walker);
        var typeFileMap = BuildTypeFileMap(walker);
        var client = ClientEmitter.EmitControllerClient("avatars", endpoints, typeFileMap);

        Assert.Contains("avatar: File", client);
        Assert.Contains("fd.append(\"avatar\", avatar);", client);
        Assert.Contains("Promise<void>", client);
    }

    [Fact]
    public void Contract_IFormFile_InTInput_EmitsFormData()
    {
        var source = """
            using System;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FileUploadRequest(IFormFile File);

            [RivetType]
            public sealed record FileUploadResult(Guid Id, string FileName);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<FileUploadRequest, FileUploadResult> Upload =
                    Define.Post<FileUploadRequest, FileUploadResult>("/api/files");
            }
            """;

        var client = GenerateContractClient(source);

        // Should detect IFormFile in TInput and emit File param + FormData
        Assert.Contains("file: File", client);
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("fd.append(\"file\", file);", client);
        Assert.Contains("body: fd", client);
        Assert.Contains("Promise<FileUploadResult>", client);
    }

    [Fact]
    public void Contract_IFormFile_WithRouteParam_EmitsFormData()
    {
        var source = """
            using System;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AttachRequest(IFormFile File);

            [RivetType]
            public sealed record AttachmentResult(Guid Id);

            [RivetContract]
            public static class TasksContract
            {
                public static readonly RouteDefinition<AttachRequest, AttachmentResult> Attach =
                    Define.Post<AttachRequest, AttachmentResult>("/api/tasks/{id}/attachments");
            }
            """;

        var client = GenerateContractClient(source);

        // Route param + file param
        Assert.Contains("id: string, file: File", client);
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("fd.append(\"file\", file);", client);
        Assert.Contains("${id}", client);
        Assert.Contains("Promise<AttachmentResult>", client);
    }

    [Fact]
    public void Contract_BareIFormFile_AsTInput_EmitsFormData()
    {
        var source = """
            using System;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AvatarResult(Guid Id);

            [RivetContract]
            public static class AvatarsContract
            {
                public static readonly RouteDefinition<IFormFile, AvatarResult> Upload =
                    Define.Post<IFormFile, AvatarResult>("/api/avatars");
            }
            """;

        var client = GenerateContractClient(source);

        // Direct IFormFile as TInput — emits File param
        Assert.Contains("file: File", client);
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("Promise<AvatarResult>", client);
    }
}
