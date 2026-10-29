using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// G.8: a route, query or header param lowered from an input member keeps the member's
/// annotations, as the member's schema would carry them in a body.
/// </summary>
public sealed class LoweredParamMetadataTests
{
    private static JsonElement Param(string source, string location, string name)
    {
        var document = CompilationHelper.EmitOpenApi(source);
        var operation = document
            .RootElement.GetProperty("paths")
            .EnumerateObject()
            .Single()
            .Value.EnumerateObject()
            .Single()
            .Value;
        return operation
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter =>
                parameter.GetProperty("in").GetString() == location
                && parameter.GetProperty("name").GetString() == name
            )
            .Clone();
    }

    private const string Input = """
        [RivetType]
        public sealed record SearchInput
        {
            public required Guid OwnerId { get; init; }

            [Range(1, 100)]
            public int Limit { get; init; }

            [RivetDescription("Free-text search")]
            [StringLength(50, MinimumLength = 2)]
            public string? Q { get; init; }

            [RivetDefault("20")]
            public int Size { get; init; }

            [Obsolete]
            public string? Legacy { get; init; }

            [EmailAddress]
            public string? Contact { get; init; }

            [RivetFormat("")]
            public DateTime? Since { get; init; }

            [RivetExample("\"red\"")]
            public string? Colour { get; init; }

            [RivetHeader("X-Trace-Id")]
            [RivetDescription("Correlation id")]
            [RegularExpression("^[a-f0-9]{32}$")]
            public string? TraceId { get; init; }
        }
        """;

    private static string Contract(string definition) =>
        $$"""
            using System;
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            {{Input}}

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly {{definition}};
            }
            """;

    private static readonly string _get = Contract(
        "RouteDefinition<SearchInput, ThingDto> Search = Define.Get<SearchInput, ThingDto>(\"/owners/{ownerId}/things\")"
    );

    private static readonly string _acceptsBinary = Contract(
        "RouteDefinition<SearchInput, ThingDto> Upload = Define.Post<SearchInput, ThingDto>(\"/owners/{ownerId}/things\").AcceptsBinary(\"application/octet-stream\")"
    );

    public static TheoryData<string> Lowerings => new() { _get, _acceptsBinary };

    [Theory]
    [MemberData(nameof(Lowerings))]
    public void Query_Param_Keeps_Its_Constraints(string source)
    {
        var schema = Param(source, "query", "limit").GetProperty("schema");

        Assert.Equal(1, schema.GetProperty("minimum").GetDouble());
        Assert.Equal(100, schema.GetProperty("maximum").GetDouble());
    }

    [Fact]
    public void Query_Param_Keeps_Description_And_Length()
    {
        var parameter = Param(_get, "query", "q");

        Assert.Equal("Free-text search", parameter.GetProperty("description").GetString());
        Assert.Equal(2, parameter.GetProperty("schema").GetProperty("minLength").GetInt32());
        Assert.Equal(50, parameter.GetProperty("schema").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void Query_Param_Keeps_Default_Deprecated_Format_And_Example()
    {
        Assert.Equal(
            20,
            Param(_get, "query", "size").GetProperty("schema").GetProperty("default").GetInt32()
        );
        Assert.True(Param(_get, "query", "legacy").GetProperty("deprecated").GetBoolean());
        Assert.Equal(
            "email",
            Param(_get, "query", "contact").GetProperty("schema").GetProperty("format").GetString()
        );
        Assert.False(
            Param(_get, "query", "since").GetProperty("schema").TryGetProperty("format", out _)
        );
        Assert.Equal(
            ["red"],
            Param(_get, "query", "colour")
                .GetProperty("schema")
                .GetProperty("examples")
                .EnumerateArray()
                .Select(example => example.GetString())
        );
    }

    [Fact]
    public void Header_Param_Keeps_Its_Annotations()
    {
        var parameter = Param(_get, "header", "X-Trace-Id");

        Assert.Equal("Correlation id", parameter.GetProperty("description").GetString());
        Assert.Equal(
            "^[a-f0-9]{32}$",
            parameter.GetProperty("schema").GetProperty("pattern").GetString()
        );
    }

    [Fact]
    public void Route_Param_Keeps_Its_Annotations_In_Body_Lowering()
    {
        var source = """
            using System;
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [RivetType]
            public sealed record RenameInput(
                [property: RivetDescription("The thing's slug")]
                [property: StringLength(40, MinimumLength = 3)]
                    string Slug,
                string Name
            );

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly RouteDefinition<RenameInput, ThingDto> Rename =
                    Define.Put<RenameInput, ThingDto>("/things/{slug}");
            }
            """;

        var parameter = Param(source, "path", "slug");

        Assert.Equal("The thing's slug", parameter.GetProperty("description").GetString());
        Assert.Equal(3, parameter.GetProperty("schema").GetProperty("minLength").GetInt32());
        Assert.Equal(40, parameter.GetProperty("schema").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void Positional_Record_Parameter_Annotations_Count()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [RivetType]
            public sealed record PageInput([Range(1, 10)] int Page);

            [RivetContract]
            public static class ThingsContract
            {
                public static readonly RouteDefinition<PageInput, ThingDto> List =
                    Define.Get<PageInput, ThingDto>("/things");
            }
            """;

        var schema = Param(source, "query", "page").GetProperty("schema");

        Assert.Equal(1, schema.GetProperty("minimum").GetDouble());
        Assert.Equal(10, schema.GetProperty("maximum").GetDouble());
    }

    [Fact]
    public void Controller_Action_Params_Keep_Their_Annotations()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            [Route("api/things")]
            public sealed class ThingsController : ControllerBase
            {
                [RivetEndpoint]
                [HttpGet("{slug}")]
                public ThingDto Get(
                    [FromRoute] [StringLength(40, MinimumLength = 3)] string slug,
                    [FromQuery] [Range(1, 10)] int page,
                    [FromHeader(Name = "X-Contact")] [EmailAddress] string? contact
                ) => new(slug);
            }
            """;

        var slug = Param(source, "path", "slug").GetProperty("schema");
        Assert.Equal(3, slug.GetProperty("minLength").GetInt32());
        Assert.Equal(40, slug.GetProperty("maxLength").GetInt32());

        var page = Param(source, "query", "page").GetProperty("schema");
        Assert.Equal(1, page.GetProperty("minimum").GetDouble());
        Assert.Equal(10, page.GetProperty("maximum").GetDouble());

        Assert.Equal(
            "email",
            Param(source, "header", "X-Contact")
                .GetProperty("schema")
                .GetProperty("format")
                .GetString()
        );
    }

    /// <summary>
    /// The multipart leg (decision 31): a form field lowered from an input record, or bound
    /// by an action's [FromForm] parameter, carries the member's annotations on the param.
    /// The emitter's form schema writing them is Track A's (A3); this holds the walkers.
    /// </summary>
    [Fact]
    public void Form_Field_Params_Keep_Their_Annotations()
    {
        var source = """
            using System;
            using System.ComponentModel.DataAnnotations;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ThingDto(string Name);

            public sealed record UploadInput
            {
                public required IFormFile File { get; init; }

                [RivetDescription("Shown under the thumbnail")]
                [StringLength(80, MinimumLength = 1)]
                [RivetExample("\"Holiday\"")]
                public required string Caption { get; init; }

                [Obsolete]
                [RivetDefault("1")]
                [Range(1, 9)]
                public int Copies { get; init; }
            }

            [RivetContract]
            public static class UploadsContract
            {
                public static readonly RouteDefinition<UploadInput, ThingDto> Upload =
                    Define.Post<UploadInput, ThingDto>("/api/uploads");
            }

            [Route("api/photos")]
            public sealed class PhotosController : ControllerBase
            {
                [RivetEndpoint]
                [HttpPost("")]
                public ThingDto Upload(
                    IFormFile photo,
                    [FromForm] [EmailAddress] [StringLength(60)] string contact
                ) => new(contact);
            }
            """;

        var (endpoints, _) = CompilationHelper.WalkMerged(source);
        var caption = endpoints
            .Single(endpoint =>
                endpoint.Name == "upload" && endpoint.RouteTemplate == "/api/uploads"
            )
            .Params.Single(param => param.Name == "caption");
        Assert.Equal(Rivet.Tool.Model.ParamSource.FormField, caption.Source);
        Assert.Equal("Shown under the thumbnail", caption.Description);
        Assert.Equal(1, caption.Constraints?.MinLength);
        Assert.Equal(80, caption.Constraints?.MaxLength);
        Assert.Equal("[\"Holiday\"]", caption.SchemaExamples?.GetRawText());

        var copies = endpoints
            .Single(endpoint => endpoint.RouteTemplate == "/api/uploads")
            .Params.Single(param => param.Name == "copies");
        Assert.True(copies.IsDeprecated);
        Assert.Equal("1", copies.DefaultValue);
        Assert.Equal(1, copies.Constraints?.Minimum);
        Assert.Equal(9, copies.Constraints?.Maximum);

        var contact = endpoints
            .Single(endpoint => endpoint.RouteTemplate == "/api/photos")
            .Params.Single(param => param.Name == "contact");
        Assert.Equal(Rivet.Tool.Model.ParamSource.FormField, contact.Source);
        Assert.Equal(60, contact.Constraints?.MaxLength);
        Assert.Equal(
            "email",
            Assert.IsType<Rivet.Tool.Model.TsType.Primitive>(contact.Type).Format
        );

        // Decision 31: the emitted form schema writes them, as a route, query or header
        // param's schema and Parameter Object would.
        using var doc = CompilationHelper.EmitOpenApi(source);
        // The body $refs the input's component when it lists exactly the parts (G.14).
        JsonElement FormProperties(string path)
        {
            var schema = doc
                .RootElement.GetProperty("paths")
                .GetProperty(path)
                .GetProperty("post")
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("multipart/form-data")
                .GetProperty("schema");
            if (schema.TryGetProperty("$ref", out var reference))
            {
                schema = doc
                    .RootElement.GetProperty("components")
                    .GetProperty("schemas")
                    .GetProperty(reference.GetString()!["#/components/schemas/".Length..]);
            }
            return schema.GetProperty("properties");
        }

        var uploads = FormProperties("/api/uploads");
        var captionSchema = uploads.GetProperty("caption");
        Assert.Equal(
            "Shown under the thumbnail",
            captionSchema.GetProperty("description").GetString()
        );
        Assert.Equal(1, captionSchema.GetProperty("minLength").GetInt32());
        Assert.Equal(80, captionSchema.GetProperty("maxLength").GetInt32());
        Assert.Equal("Holiday", captionSchema.GetProperty("examples")[0].GetString());
        var copiesSchema = uploads.GetProperty("copies");
        Assert.True(copiesSchema.GetProperty("deprecated").GetBoolean());
        Assert.Equal(1, copiesSchema.GetProperty("default").GetInt32());
        Assert.Equal(1, copiesSchema.GetProperty("minimum").GetInt32());
        Assert.Equal(9, copiesSchema.GetProperty("maximum").GetInt32());

        var contactSchema = FormProperties("/api/photos").GetProperty("contact");
        Assert.Equal("email", contactSchema.GetProperty("format").GetString());
        Assert.Equal(60, contactSchema.GetProperty("maxLength").GetInt32());
    }
}
