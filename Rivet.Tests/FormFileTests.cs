using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class FormFileTests
{
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
        var client = ClientEmitter.EmitControllerClient("files", endpoints);

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
        var client = ClientEmitter.EmitControllerClient("tasks", endpoints);

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
        var client = ClientEmitter.EmitControllerClient("avatars", endpoints);

        Assert.Contains("avatar: File", client);
        Assert.Contains("fd.append(\"avatar\", avatar);", client);
        Assert.Contains("Promise<void>", client);
    }
}
