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

    private static void AssertBinaryFileArrayProperty(JsonElement schema, string name)
    {
        var prop = schema.GetProperty("properties").GetProperty(name);
        Assert.Equal("array", prop.GetProperty("type").GetString());
        var items = prop.GetProperty("items");
        Assert.Equal("string", items.GetProperty("type").GetString());
        Assert.Equal("binary", items.GetProperty("format").GetString());
        Assert.True(items.GetProperty("x-rivet-file").GetBoolean());
    }

    /// <summary>
    /// FABLE_GAPS §7 item 12: a record whose ONLY files are List&lt;IFormFile&gt; used to
    /// emit as application/json with format:binary strings — an unimplementable spec
    /// with zero diagnostics. Collections of IFormFile are multipart array-of-binary.
    /// </summary>
    [Fact]
    public void Contract_FormFileCollection_OnlyFiles_EmitsMultipartArrayOfBinary()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record BatchUploadRequest(List<IFormFile> Files, string Album);

            [RivetType]
            public sealed record BatchUploadResult(int Count);

            [RivetContract]
            public static class PhotosContract
            {
                public static readonly RouteDefinition<BatchUploadRequest, BatchUploadResult> Upload =
                    Define.Post<BatchUploadRequest, BatchUploadResult>("/api/photos");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        // Walker classification: collection-of-file → File param with Array(File) type
        var ep = Assert.Single(endpoints);
        var filesParam = Assert.Single(ep.Params, p => p.Name == "files");
        Assert.Equal(ParamSource.File, filesParam.Source);
        Assert.True(filesParam.Type is TsType.Array { Element: TsType.Primitive { Name: "File" } },
            $"Expected Array(File) but got {filesParam.Type}");
        var albumParam = Assert.Single(ep.Params, p => p.Name == "album");
        Assert.Equal(ParamSource.FormField, albumParam.Source);

        // OpenAPI: multipart/form-data (NOT application/json), array-of-binary part
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/photos", "post");
        var schema = GetMultipartSchema(operation);
        Assert.Equal("#/components/schemas/BatchUploadRequest", schema.GetProperty("$ref").GetString());

        var component = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("BatchUploadRequest");
        AssertBinaryFileArrayProperty(component, "files");
    }

    [Theory]
    [InlineData("IFormFile[]")]
    [InlineData("IReadOnlyList<IFormFile>")]
    [InlineData("IEnumerable<IFormFile>")]
    public void Contract_FormFileCollectionVariants_ClassifyAsFileParams(string propertyType)
    {
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadRequest({{propertyType}} Files);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly Define Upload =
                    Define.Post<UploadRequest, string>("/api/files");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var ep = Assert.Single(endpoints);
        var filesParam = Assert.Single(ep.Params, p => p.Name == "files");
        Assert.Equal(ParamSource.File, filesParam.Source);
        Assert.True(filesParam.Type is TsType.Array { Element: TsType.Primitive { Name: "File" } },
            $"Expected Array(File) for {propertyType} but got {filesParam.Type}");
    }

    [Fact]
    public void Controller_FormFileCollection_OnlyFiles_EmitsMultipartArrayOfBinary()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [Route("api/photos")]
            public sealed class PhotosController
            {
                [RivetEndpoint]
                [HttpPost("")]
                [ProducesResponseType(200)]
                public Task<IActionResult> Upload(
                    List<IFormFile> photos,
                    string album,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        // Walker classification: collection param → File source with Array(File) type,
        // sibling params become form fields (the pre-scan sees the collection)
        var ep = Assert.Single(endpoints);
        var photosParam = Assert.Single(ep.Params, p => p.Name == "photos");
        Assert.Equal(ParamSource.File, photosParam.Source);
        Assert.True(photosParam.Type is TsType.Array { Element: TsType.Primitive { Name: "File" } },
            $"Expected Array(File) but got {photosParam.Type}");
        var albumParam = Assert.Single(ep.Params, p => p.Name == "album");
        Assert.Equal(ParamSource.FormField, albumParam.Source);

        // OpenAPI: inline multipart schema with the array-of-binary part
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/photos", "post");
        var schema = GetMultipartSchema(operation);
        AssertBinaryFileArrayProperty(schema, "photos");
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("photos", required);
        Assert.Contains("album", required);
    }

    [Fact]
    public void FormFileCollection_Survives_Import_As_ListOfIFormFile()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record BatchUploadRequest(List<IFormFile> Files, string Album);

            [RivetContract]
            public static class PhotosContract
            {
                public static readonly Define Upload =
                    Define.Post<BatchUploadRequest, string>("/api/photos");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var result = CompilationHelper.Import(doc.RootElement.GetRawText());

        // Array-of-binary parts come back as List<IFormFile>, not strings
        var request = CompilationHelper.FindFile(result, "BatchUploadRequest.cs");
        Assert.Contains("List<IFormFile> Files", request);
        CompilationHelper.CompileImportResult(result);
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
