using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// File-upload classification is walker behavior — asserted on the endpoint model
/// (ParamSource, Primitive("File")) and on the emitted OpenAPI multipart request body.
/// </summary>
public sealed class FormFileTests
{
    private static JsonElement GetOperation(JsonDocument doc, string path, string method)
        => doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static JsonElement GetMultipartSchema(JsonElement operation)
    {
        var requestBody = operation.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        return requestBody.GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
    }

    private static void AssertBinaryFileProperty(JsonElement schema, string name)
    {
        var prop = schema.GetProperty("properties").GetProperty(name);
        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("binary", prop.GetProperty("format").GetString());
        Assert.True(prop.GetProperty("x-rivet-file").GetBoolean());
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(name, required);
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

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

        // OpenAPI: multipart/form-data request body with an inline binary file property
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/files", "post");
        var schema = GetMultipartSchema(operation);
        AssertBinaryFileProperty(schema, "file");

        // Response type is correct
        Assert.Equal(
            "#/components/schemas/FileUploadResult",
            operation.GetProperty("responses").GetProperty("201")
                .GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
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

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var routeParam = Assert.Single(ep.Params, p => p.Name == "id");
        Assert.Equal(ParamSource.Route, routeParam.Source);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

        // OpenAPI: both the path parameter and the multipart file property
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/tasks/{id}/attachments", "post");
        var idParam = Assert.Single(
            operation.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "id");
        Assert.Equal("path", idParam.GetProperty("in").GetString());
        Assert.True(idParam.GetProperty("required").GetBoolean());

        var schema = GetMultipartSchema(operation);
        AssertBinaryFileProperty(schema, "file");
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

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var avatarParam = Assert.Single(ep.Params, p => p.Name == "avatar");
        Assert.Equal(ParamSource.File, avatarParam.Source);
        Assert.IsType<TsType.Primitive>(avatarParam.Type);
        Assert.Equal("File", ((TsType.Primitive)avatarParam.Type).Name);
        Assert.Null(ep.ReturnType);

        // OpenAPI: multipart body, and the 200 response carries no schema (void)
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/avatars", "post");
        var schema = GetMultipartSchema(operation);
        AssertBinaryFileProperty(schema, "avatar");

        var response = operation.GetProperty("responses").GetProperty("200");
        Assert.False(response.TryGetProperty("content", out _));
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

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

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

        // OpenAPI: file is binary, title is a plain string form field, both required
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/documents", "post");
        var schema = GetMultipartSchema(operation);
        AssertBinaryFileProperty(schema, "file");

        var titleProp = schema.GetProperty("properties").GetProperty("title");
        Assert.Equal("string", titleProp.GetProperty("type").GetString());
        Assert.False(titleProp.TryGetProperty("format", out _));
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("title", required);
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

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

        // OpenAPI: named input type — multipart schema is a $ref into components,
        // and the component carries the binary file property
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/files", "post");
        var schema = GetMultipartSchema(operation);
        Assert.Equal("#/components/schemas/FileUploadRequest", schema.GetProperty("$ref").GetString());

        var component = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("FileUploadRequest");
        AssertBinaryFileProperty(component, "file");

        Assert.Equal(
            "#/components/schemas/FileUploadResult",
            operation.GetProperty("responses").GetProperty("201")
                .GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
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

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var routeParam = Assert.Single(ep.Params, p => p.Name == "id");
        Assert.Equal(ParamSource.Route, routeParam.Source);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

        // OpenAPI: path param + multipart $ref body
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/tasks/{id}/attachments", "post");
        var idParam = Assert.Single(
            operation.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "id");
        Assert.Equal("path", idParam.GetProperty("in").GetString());
        Assert.True(idParam.GetProperty("required").GetBoolean());

        var schema = GetMultipartSchema(operation);
        Assert.Equal("#/components/schemas/AttachRequest", schema.GetProperty("$ref").GetString());

        var component = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("AttachRequest");
        AssertBinaryFileProperty(component, "file");

        Assert.Equal(
            "#/components/schemas/AttachmentResult",
            operation.GetProperty("responses").GetProperty("201")
                .GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
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

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        // Validate endpoint param types
        var ep = Assert.Single(endpoints);
        var fileParam = Assert.Single(ep.Params, p => p.Name == "file");
        Assert.Equal(ParamSource.File, fileParam.Source);
        Assert.IsType<TsType.Primitive>(fileParam.Type);
        Assert.Equal("File", ((TsType.Primitive)fileParam.Type).Name);

        // OpenAPI: bare IFormFile input — inline multipart schema with binary file property
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/avatars", "post");
        var schema = GetMultipartSchema(operation);
        AssertBinaryFileProperty(schema, "file");

        Assert.Equal(
            "#/components/schemas/AvatarResult",
            operation.GetProperty("responses").GetProperty("201")
                .GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
    }
}
