using System.Reflection;
using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// File-upload classification is walker behavior — asserted on the endpoint model
/// (ParamSource, Primitive("File")) and on the emitted OpenAPI multipart request body.
/// </summary>
public sealed class FormFileTests
{
    private static JsonElement GetOperation(JsonDocument doc, string path, string method) =>
        doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static JsonElement GetMultipartSchema(JsonElement operation)
    {
        var requestBody = operation.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        return requestBody
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema");
    }

    private static void AssertBinaryFileProperty(JsonElement schema, string name)
    {
        var prop = schema.GetProperty("properties").GetProperty(name);
        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("binary", prop.GetProperty("format").GetString());
        Assert.True(prop.GetProperty("x-rivet-file").GetBoolean());
        var required = schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
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
            operation
                .GetProperty("responses")
                .GetProperty("201")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
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
            p => p.GetProperty("name").GetString() == "id"
        );
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
    public void Contract_IFormFile_InTInput_EmitsFormData()
    {
        var source = """
            using System;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

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
        Assert.Equal(
            "#/components/schemas/FileUploadRequest",
            schema.GetProperty("$ref").GetString()
        );

        var component = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("FileUploadRequest");
        AssertBinaryFileProperty(component, "file");

        Assert.Equal(
            "#/components/schemas/FileUploadResult",
            operation
                .GetProperty("responses")
                .GetProperty("201")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
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
                    Define.Post<AttachRequest, AttachmentResult>("/api/tasks/{id:guid}/attachments");
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
            p => p.GetProperty("name").GetString() == "id"
        );
        Assert.Equal("path", idParam.GetProperty("in").GetString());
        Assert.True(idParam.GetProperty("required").GetBoolean());

        var schema = GetMultipartSchema(operation);
        Assert.Equal("#/components/schemas/AttachRequest", schema.GetProperty("$ref").GetString());

        var component = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("AttachRequest");
        AssertBinaryFileProperty(component, "file");

        Assert.Equal(
            "#/components/schemas/AttachmentResult",
            operation
                .GetProperty("responses")
                .GetProperty("201")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
    }

    [Fact]
    public void Generic_Multipart_Input_Is_Emitted_Inline_Without_A_Dangling_Template_Reference()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Upload<T>(IFormFile File, T Metadata);

            [RivetType]
            public sealed record Metadata(string Name);

            [RivetType]
            public sealed record UploadResult(string Id);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<Upload<Metadata>, UploadResult> Upload =
                    Define.Post<Upload<Metadata>, UploadResult>("/api/files");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schema = GetMultipartSchema(GetOperation(doc, "/api/files", "post"));

        Assert.False(schema.TryGetProperty("$ref", out _));
        var properties = schema.GetProperty("properties");
        AssertBinaryFileProperty(schema, "file");
        Assert.Equal(
            "#/components/schemas/Metadata",
            properties.GetProperty("metadata").GetProperty("$ref").GetString()
        );
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
        Assert.True(
            filesParam.Type is TsType.Array { Element: TsType.Primitive { Name: "File" } },
            $"Expected Array(File) but got {filesParam.Type}"
        );
        var albumParam = Assert.Single(ep.Params, p => p.Name == "album");
        Assert.Equal(ParamSource.FormField, albumParam.Source);

        // OpenAPI: multipart/form-data (NOT application/json), array-of-binary part
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/photos", "post");
        var schema = GetMultipartSchema(operation);
        Assert.Equal(
            "#/components/schemas/BatchUploadRequest",
            schema.GetProperty("$ref").GetString()
        );

        var component = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("BatchUploadRequest");
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
                public static readonly RouteDefinition<UploadRequest, string> Upload =
                    Define.Post<UploadRequest, string>("/api/files");
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkContract(source);

        var ep = Assert.Single(endpoints);
        var filesParam = Assert.Single(ep.Params, p => p.Name == "files");
        Assert.Equal(ParamSource.File, filesParam.Source);
        Assert.True(
            filesParam.Type is TsType.Array { Element: TsType.Primitive { Name: "File" } },
            $"Expected Array(File) for {propertyType} but got {filesParam.Type}"
        );
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
                    [FromForm] string album,
                    CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);

        // Walker classification: collection param → File source with Array(File) type,
        // the explicitly declared [FromForm] field joins the multipart surface.
        var ep = Assert.Single(endpoints);
        var photosParam = Assert.Single(ep.Params, p => p.Name == "photos");
        Assert.Equal(ParamSource.File, photosParam.Source);
        Assert.True(
            photosParam.Type is TsType.Array { Element: TsType.Primitive { Name: "File" } },
            $"Expected Array(File) but got {photosParam.Type}"
        );
        var albumParam = Assert.Single(ep.Params, p => p.Name == "album");
        Assert.Equal(ParamSource.FormField, albumParam.Source);

        // OpenAPI: inline multipart schema with the array-of-binary part
        using var doc = CompilationHelper.EmitOpenApi(source);
        var operation = GetOperation(doc, "/api/photos", "post");
        var schema = GetMultipartSchema(operation);
        AssertBinaryFileArrayProperty(schema, "photos");
        var required = schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
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
                public static readonly RouteDefinition<BatchUploadRequest, string> Upload =
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
            operation
                .GetProperty("responses")
                .GetProperty("201")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
    }

    // ========== Multipart schema ==========

    [Fact]
    public void OpenApi_Multipart_Includes_Required_Array()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadInput(IFormFile Document, string Title);

            [RivetType]
            public sealed record UploadResult(string Url);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<UploadInput, UploadResult> Upload =
                    Define.Post<UploadInput, UploadResult>("/api/files");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);

        // Multipart with named input type emits $ref to component schema
        var multipart = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/files")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema");
        Assert.True(
            multipart.TryGetProperty("$ref", out var refVal),
            "Named multipart input should emit as $ref"
        );
        Assert.Equal("#/components/schemas/UploadInput", refVal.GetString());

        // The component schema has the required array
        var uploadSchema = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("UploadInput");
        Assert.True(
            uploadSchema.TryGetProperty("required", out var required),
            "UploadInput schema should include a 'required' array"
        );

        var requiredFields = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("document", requiredFields);
        Assert.Contains("title", requiredFields);
    }

    [Fact]
    public void Multipart_RouteParam_Not_Duplicated_As_FormField()
    {
        var source = """
            using System;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record UploadInput(Guid TaskId, IFormFile Document, string Title);

            [RivetType]
            public sealed record UploadResult(string Url);

            [RivetContract]
            public static class FilesContract
            {
                public static readonly RouteDefinition<UploadInput, UploadResult> Upload =
                    Define.Post<UploadInput, UploadResult>("/api/tasks/{taskId}/files");
            }
            """;

        var endpoints = CompilationHelper.WalkContract(source).Endpoints;
        var ep = Assert.Single(endpoints);

        // taskId should appear exactly once as Route
        var routeParams = ep.Params.Where(p => p.Source == ParamSource.Route).ToList();
        Assert.Single(routeParams);
        Assert.Equal("taskId", routeParams[0].Name);

        // taskId should NOT appear as a FormField
        var formFields = ep.Params.Where(p => p.Source == ParamSource.FormField).ToList();
        Assert.DoesNotContain(
            formFields,
            f => string.Equals(f.Name, "taskId", StringComparison.OrdinalIgnoreCase)
        );

        // document is File, title is FormField
        Assert.Single(ep.Params, p => p.Source == ParamSource.File);
        Assert.Single(formFields, f => f.Name == "title");

        using var document = CompilationHelper.EmitOpenApi(source);
        var bodySchema = document
            .RootElement.GetProperty("paths")
            .GetProperty("/api/tasks/{taskId}/files")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema");
        Assert.False(bodySchema.TryGetProperty("$ref", out _));
        var bodyProperties = bodySchema.GetProperty("properties");
        Assert.False(bodyProperties.TryGetProperty("taskId", out _));
        Assert.True(bodyProperties.TryGetProperty("document", out _));
        Assert.True(bodyProperties.TryGetProperty("title", out _));
    }

    /// <summary>
    /// Whether TInput carries files, and which parts its multipart body lists, are worked out
    /// twice: from Roslyn symbols in the tool (FormBinding.FormBoundProperties) and by
    /// reflection at runtime (RequestInputLowering.CarriesFormFiles). Both must count what
    /// ASP.NET's form binder binds, so each shape is compiled once, walked by the tool, then
    /// emitted, loaded and put to the runtime, and the two must give the same answer. When the
    /// input is multipart, the parts are the bound members, less route and [RivetHeader] ones
    /// and the non-file members the binder can't write.
    /// </summary>
    [Theory]
    [InlineData("public IFormFile File { get; init; } = null!;", "", true, "file,title")]
    [InlineData("internal IFormFile? File { get; init; }", "", false, "")]
    [InlineData(
        "public new string File { get; init; } = \"\";",
        "public IFormFile File { get; init; } = null!;",
        false,
        ""
    )]
    [InlineData(
        "internal new string File { get; init; } = \"\";",
        "public IFormFile File { get; init; } = null!;",
        false,
        ""
    )]
    [InlineData("", "public List<IFormFile> Files { get; init; } = [];", true, "files,title")]
    [InlineData("public IFormFile[] Files { get; init; } = [];", "", true, "files,title")]
    [InlineData("public IList<IFormFile> Files { get; init; } = [];", "", true, "files,title")]
    [InlineData(
        "public ICollection<IFormFile> Files { get; init; } = [];",
        "",
        true,
        "files,title"
    )]
    [InlineData(
        "public IEnumerable<IFormFile> Files { get; init; } = [];",
        "",
        true,
        "files,title"
    )]
    [InlineData(
        "public IReadOnlyList<IFormFile> Files { get; init; } = [];",
        "",
        true,
        "files,title"
    )]
    [InlineData(
        "public IReadOnlyCollection<IFormFile> Files { get; init; } = [];",
        "",
        true,
        "files,title"
    )]
    [InlineData("public IFormFileCollection Files { get; init; } = null!;", "", false, "")]
    [InlineData("public HashSet<IFormFile> Files { get; init; } = [];", "", false, "")]
    // The form binder never reads [JsonIgnore], so the ignored file is still a part.
    [InlineData(
        "[System.Text.Json.Serialization.JsonIgnore] public IFormFile File { get; init; } = null!;",
        "",
        true,
        "file,title"
    )]
    // A non-public member isn't bound, so it isn't a form field.
    [InlineData(
        "public IFormFile File { get; init; } = null!; internal string Secret { get; init; } = \"\";",
        "",
        true,
        "file,title"
    )]
    // A computed member has nothing the binder can write.
    [InlineData(
        "public IFormFile File { get; init; } = null!; public string Display => Title;",
        "",
        true,
        "file,title"
    )]
    // The [JsonExtensionData] remainder is no field of its own.
    [InlineData(
        "public IFormFile File { get; init; } = null!; [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement> Extra { get; init; } = [];",
        "",
        true,
        "file,title"
    )]
    // A header member is a header param, not a part.
    [InlineData(
        "public IFormFile File { get; init; } = null!; [RivetHeader(\"X-Trace\")] public string Trace { get; init; } = \"\";",
        "",
        true,
        "file,title"
    )]
    public void Tool_and_runtime_agree_on_which_inputs_carry_files(
        string members,
        string baseMembers,
        bool form,
        string parts
    )
    {
        var compilation = CompilationHelper.CreateCompilation(
            UploadSource($"UploadBase", members, $"public class UploadBase {{ {baseMembers} }}")
        );

        AssertToolAndRuntimeAgree(compilation, packageImage: null, form, parts);
    }

    /// <summary>
    /// The same table for a base class from a package: outside the assemblies the type walk
    /// follows, and imported by Roslyn without its non-public members, which still hide a
    /// base's property at runtime.
    /// </summary>
    [Theory]
    [InlineData("", "", "public IFormFile File { get; init; } = null!;", true, "file,title")]
    [InlineData(
        "",
        "public string Note { get; init; } = \"\"; internal string Secret { get; init; } = \"\";",
        "public List<IFormFile> Files { get; init; } = [];",
        true,
        "files,note,title"
    )]
    [InlineData(
        "public IFormFile File { get; init; } = null!;",
        "",
        "public string Note { get; init; } = \"\";",
        true,
        "file,note,title"
    )]
    [InlineData(
        "",
        "public IFormFile File { get; init; } = null!;",
        "internal new string File { get; init; } = \"\";",
        false,
        ""
    )]
    [InlineData(
        "",
        "public IFormFile File { get; init; } = null!;",
        "private new string File { get; init; } = \"\";",
        false,
        ""
    )]
    [InlineData("", "", "public static IFormFile? Shared { get; set; }", false, "")]
    public void Tool_and_runtime_agree_on_which_inputs_carry_files_from_a_package_base(
        string members,
        string packageRootMembers,
        string packageBaseMembers,
        bool form,
        string parts
    )
    {
        var (compilation, packageImage) = CompilationHelper.CreateCompilationWithPackageReference(
            UploadSource("Packaged.PackageBase", members, ""),
            PackageSource(packageRootMembers, packageBaseMembers)
        );

        AssertToolAndRuntimeAgree(compilation, packageImage, form, parts);
    }

    /// <summary>
    /// A file on a package's base class is a part of the emitted multipart body. The input's
    /// component stops at the package, as the type walk does, so the body is built inline from
    /// the parts rather than referencing a component that has no file.
    /// </summary>
    [Fact]
    public void A_file_on_a_package_base_is_a_part_of_the_multipart_body()
    {
        var (compilation, _) = CompilationHelper.CreateCompilationWithPackageReference(
            UploadSource("Packaged.PackageBase", "", ""),
            PackageSource("", "public IFormFile File { get; init; } = null!;")
        );
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        using var document = JsonDocument.Parse(
            Rivet.Tool.Emit.OpenApiEmitter.Emit(
                endpoints,
                walker.Definitions,
                walker.Brands,
                walker.Enums,
                null
            )
        );

        var schema = GetMultipartSchema(GetOperation(document, "/api/uploads", "post"));
        Assert.False(schema.TryGetProperty("$ref", out _));
        AssertBinaryFileProperty(schema, "file");
        Assert.True(schema.GetProperty("properties").TryGetProperty("title", out _));
    }

    private static string UploadSource(string baseType, string members, string baseDeclaration) =>
        $$"""
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            {{baseDeclaration}}

            public class Upload : {{baseType}}
            {
                public string Title { get; init; } = "";
                {{members}}
            }

            [RivetContract]
            public static class UploadsContract
            {
                public static readonly RouteDefinition<Upload, string> Create =
                    Define.Post<Upload, string>("/api/uploads");
            }
            """;

    private static string PackageSource(string rootMembers, string baseMembers) =>
        $$"""
            using System.Collections.Generic;
            using Microsoft.AspNetCore.Http;

            namespace Packaged;

            public class PackageRoot { {{rootMembers}} }

            public class PackageBase : PackageRoot { {{baseMembers}} }
            """;

    private static void AssertToolAndRuntimeAgree(
        Microsoft.CodeAnalysis.Compilation compilation,
        byte[]? packageImage,
        bool form,
        string parts
    )
    {
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoint = Assert.Single(
            CompilationHelper.WalkContracts(compilation, discovered, walker)
        );
        var listed = endpoint
            .Params.Where(p => p.Source is ParamSource.File or ParamSource.FormField)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(parts, string.Join(",", listed));

        using var image = new MemoryStream();
        Assert.True(compilation.Emit(image).Success);
        var context = new UploadLoadContext(packageImage);
        try
        {
            var upload = context
                .LoadMain(image.ToArray())
                .GetType("Test.Upload", throwOnError: true);
            var carriesFormFiles = typeof(Define)
                .Assembly.GetType("Rivet.RequestInputLowering", throwOnError: true)!
                .GetMethod("CarriesFormFiles", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Equal(form, carriesFormFiles.Invoke(null, [upload]));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Loads the compiled test assembly beside the package it references.</summary>
    private sealed class UploadLoadContext(byte[]? packageImage)
        : System.Runtime.Loader.AssemblyLoadContext(isCollectible: true)
    {
        private Assembly? _package;

        public Assembly LoadMain(byte[] mainImage)
        {
            if (packageImage is not null)
            {
                _package = LoadFromStream(new MemoryStream(packageImage));
            }

            return LoadFromStream(new MemoryStream(mainImage));
        }

        protected override Assembly? Load(AssemblyName assemblyName) =>
            assemblyName.Name == "PackageAssembly" ? _package : null;
    }
}
