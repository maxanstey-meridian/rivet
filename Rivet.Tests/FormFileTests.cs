using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class FormFileTests
{
    private static string GenerateContractClient(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var typeFileMap = CompilationHelper.BuildTypeFileMap(walker);
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        return string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
    }

    private static (string Client, IReadOnlyList<TsEndpointDefinition> Endpoints) GenerateContractClientWithEndpoints(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var typeFileMap = CompilationHelper.BuildTypeFileMap(walker);
        var controllerGroups = ClientEmitter.GroupByController(endpoints);
        var client = string.Join("\n", controllerGroups.Select(g =>
            ClientEmitter.EmitControllerClient(g.Key, g.Value, typeFileMap)));
        return (client, endpoints);
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
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var typeFileMap = CompilationHelper.BuildTypeFileMap(walker);
        var client = ClientEmitter.EmitControllerClient("files", endpoints, typeFileMap);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

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
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var typeFileMap = CompilationHelper.BuildTypeFileMap(walker);
        var client = ClientEmitter.EmitControllerClient("tasks", endpoints, typeFileMap);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var routeParam = Assert.Single(ep.Params, p => p.Name == "id");
        Assert.Equal(ParamSource.Route, routeParam.Source);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

        // Both route param and file param
        Assert.Contains("id: string, file: File", client);
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("fd.append(\"file\", file);", client);
        Assert.Contains("${encodeURIComponent(String(id))}", client);
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
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var typeFileMap = CompilationHelper.BuildTypeFileMap(walker);
        var client = ClientEmitter.EmitControllerClient("avatars", endpoints, typeFileMap);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var avatarParam = Assert.Single(ep.Params, p => p.Name == "avatar");
        Assert.Equal(ParamSource.File, avatarParam.Source);
        Assert.IsType<TsType.Primitive>(avatarParam.Type);
        Assert.Equal("File", ((TsType.Primitive)avatarParam.Type).Name);
        Assert.Null(ep.ReturnType);

        Assert.Contains("avatar: File", client);
        Assert.Contains("fd.append(\"avatar\", avatar);", client);
        Assert.Contains("Promise<void>", client);
    }

    [Fact]
    public void EndpointWalker_MixedUpload_ClassifiesFormFields()
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
            public sealed record UploadResult(Guid Id);

            [Route("api/documents")]
            public sealed class DocumentsController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(typeof(UploadResult), 201)]
                public Task<IActionResult> Upload(
                    IFormFile file,
                    string title,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkEndpoints(compilation, discovered, walker);
        var typeFileMap = CompilationHelper.BuildTypeFileMap(walker);
        var client = ClientEmitter.EmitControllerClient("documents", endpoints, typeFileMap);

        // Validate endpoint param types and sources
        var ep = Assert.Single(endpoints);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);
        var titleParam = Assert.Single(ep.Params, p => p.Name == "title");
        Assert.Equal(ParamSource.FormField, titleParam.Source);
        Assert.IsType<TsType.Primitive>(titleParam.Type);
        Assert.Equal("string", ((TsType.Primitive)titleParam.Type).Name);

        // file param is File, title is a FormField appended to FormData
        Assert.Contains("file: File, title: string", client);
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("fd.append(\"file\", file);", client);
        Assert.Contains("fd.append(\"title\", title);", client);
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

        var (client, endpoints) = GenerateContractClientWithEndpoints(source);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

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

        var (client, endpoints) = GenerateContractClientWithEndpoints(source);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var routeParam = Assert.Single(ep.Params, p => p.Name == "id");
        Assert.Equal(ParamSource.Route, routeParam.Source);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

        // Route param + file param
        Assert.Contains("id: string, file: File", client);
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("fd.append(\"file\", file);", client);
        Assert.Contains("${encodeURIComponent(String(id))}", client);
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

        var (client, endpoints) = GenerateContractClientWithEndpoints(source);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

        // Direct IFormFile as TInput — emits File param
        Assert.Contains("file: File", client);
        Assert.Contains("const fd = new FormData();", client);
        Assert.Contains("Promise<AvatarResult>", client);
    }
}
