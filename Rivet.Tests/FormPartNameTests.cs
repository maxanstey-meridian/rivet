using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Rivet.Tool;
using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Decision 33: a form body's parts are named as the hosts' form binders read them, never by
/// <c>[JsonPropertyName]</c>. MVC's model binder reads a member's <c>[FromForm(Name)]</c> (on a
/// positional record, the constructor parameter's alone), else its C# name; the minimal-API
/// form mapper reads its <c>[DataMember(Name)]</c>, else its C# name; both ignore case. A
/// contract can be served by either, so a rename only one of them reads is refused (RIV1100).
/// The host pins below send what generation names, to real hosts.
/// </summary>
public sealed class FormPartNameTests
{
    // ---------------------------------------------------------------
    // Real hosts: what each form binder reads.
    // ---------------------------------------------------------------

    public static TheoryData<Type, string, string[]> AgreedShapes =>
        new()
        {
            {
                typeof(FormAgreedClass),
                "class",
                ["plain", "json", "both_name", "upload", "file_name"]
            },
            // The shape --from-openapi writes for a renamed part.
            { typeof(FormAgreedInit), "init", ["title", "x-meta", "file_name"] },
            { typeof(FormAgreedRecord), "record", ["plain", "x-meta", "upload"] },
        };

    [Theory]
    [MemberData(nameof(AgreedShapes))]
    public async Task Both_hosts_bind_every_part_under_the_name_generation_gives_it(
        Type type,
        string shape,
        string[] expected
    )
    {
        var parts = GenerationParts(type, FormBinding.FormHosts.Any);
        Assert.Equal(expected, parts.Select(part => part.Name));

        foreach (var mvc in new[] { true, false })
        {
            await using var host = await StartAsync(mvc);
            var (status, body) = await host.PostAsync(
                $"/form-parts/{(mvc ? "mvc" : "minimal")}/{shape}",
                Multipart(parts.Select(part => (part.Name, part.IsFile)))
            );

            Assert.Equal(HttpStatusCode.OK, status);
            var bound = JsonSerializer.Deserialize<Dictionary<string, string>>(body)!;
            foreach (var part in parts)
            {
                Assert.True(
                    bound.TryGetValue(part.Property.Name, out var value)
                        && value == (part.IsFile ? $"file:{part.Name}" : part.Name),
                    $"{(mvc ? "MVC" : "the form mapper")} didn't bind {type.Name}.{part.Property.Name} "
                        + $"from part '{part.Name}': {body}"
                );
            }
        }
    }

    [Theory]
    // Neither host reads [JsonPropertyName].
    [InlineData(true, "disagreed", "json_name", "Json", false)]
    [InlineData(false, "disagreed", "json_name", "Json", false)]
    // MVC ignores [DataMember]; the form mapper reads it.
    [InlineData(true, "disagreed", "data_only", "DataOnly", false)]
    [InlineData(true, "disagreed", "DataOnly", "DataOnly", true)]
    [InlineData(false, "disagreed", "data_only", "DataOnly", true)]
    // The form mapper ignores a member's [FromForm(Name)]; MVC reads it.
    [InlineData(false, "disagreed", "form_only", "FormOnly", false)]
    [InlineData(false, "disagreed", "FormOnly", "FormOnly", true)]
    [InlineData(true, "disagreed", "form_only", "FormOnly", true)]
    // On a positional record MVC reads the constructor parameter's attributes only, so a
    // [property: FromForm(Name)] renames nothing there.
    [InlineData(true, "disagreed-record", "prop_form", "PropForm", false)]
    [InlineData(true, "disagreed-record", "PropForm", "PropForm", true)]
    public async Task The_hosts_read_these_renames_differently(
        bool mvc,
        string shape,
        string key,
        string member,
        bool bound
    )
    {
        await using var host = await StartAsync(mvc);
        // A positional record's form mapper wants every constructor parameter (below).
        var keys = shape == "disagreed-record" ? new[] { key, "Plain" } : [key];
        var (status, body) = await host.PostAsync(
            $"/form-parts/{(mvc ? "mvc" : "minimal")}/{shape}",
            Multipart(keys.Select(name => (name, false)))
        );

        Assert.Equal(HttpStatusCode.OK, status);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>?>(body) ?? [];
        Assert.Equal(bound, values.ContainsKey(member));
    }

    [Fact]
    public async Task The_form_mapper_refuses_a_positional_record_missing_any_constructor_parameter()
    {
        // Why --from-openapi writes a form input in the init form: B is nullable, yet the
        // minimal-API form mapper answers 400 without it. MVC binds it as null.
        await using var minimal = await StartAsync(mvc: false);
        await using var mvc = await StartAsync(mvc: true);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await minimal.PostAsync("/form-parts/minimal/bare", Multipart([("A", false)]))).Status
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await minimal.PostAsync("/form-parts/minimal/init", Multipart([("title", false)]))
            ).Status
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await mvc.PostAsync("/form-parts/mvc/bare", Multipart([("A", false)]))).Status
        );
    }

    [Fact]
    public async Task A_contract_requires_every_part_a_positional_record_s_constructor_binds()
    {
        // Decision 35. FormOptionalRecord's Note is [RivetOptional], but the form mapper
        // answers 400 without it, as without any constructor parameter (above), and MVC
        // binds it as null. A contract may be served by either, so there every
        // constructor-bound part is required, which both hosts accept; an MVC-only endpoint
        // keeps the member's own optionality.
        Assert.All(
            GenerationParts(typeof(FormOptionalRecord), FormBinding.FormHosts.Any),
            part => Assert.True(part.Required, $"'{part.Name}' should be required")
        );
        Assert.All(
            GenerationParts(typeof(FormOptionalRecord), FormBinding.FormHosts.Mvc),
            part => Assert.False(part.Required, $"'{part.Name}' should keep its optionality")
        );

        await using var minimal = await StartAsync(mvc: false);
        await using var mvc = await StartAsync(mvc: true);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await minimal.PostAsync(
                    "/form-parts/minimal/optional",
                    Multipart([("Title", false)])
                )
            ).Status
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await mvc.PostAsync("/form-parts/mvc/optional", Multipart([("Title", false)]))).Status
        );
        foreach (var host in new[] { minimal, mvc })
        {
            var path = host == minimal ? "minimal" : "mvc";
            Assert.Equal(
                HttpStatusCode.OK,
                (
                    await host.PostAsync(
                        $"/form-parts/{path}/optional",
                        Multipart([("Title", false), ("Note", false)])
                    )
                ).Status
            );
        }
    }

    [Fact]
    public void A_contract_s_positional_form_record_emits_its_constructor_bound_parts_as_required()
    {
        var source = """
            using Microsoft.AspNetCore.Http;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record NoteUpload(
                string Title,
                [property: RivetOptional] string? Note,
                IFormFile Scan
            )
            {
                [RivetOptional]
                public string? Tag { get; init; }
            }

            [RivetType]
            public sealed record NoteReceipt(string Id);

            [RivetContract]
            public static class NotesContract
            {
                public static readonly RouteDefinition<NoteUpload, NoteReceipt> Upload =
                    Define.Post<NoteUpload, NoteReceipt>("/api/note-uploads");
            }
            """;

        var endpoint = Assert.Single(CompilationHelper.WalkContract(source).Endpoints);
        Assert.Equal(
            [("title", false), ("note", false), ("scan", false), ("tag", true)],
            endpoint.Params.Select(param => (param.Name, param.IsOptional))
        );

        // The component calls note optional, so the body can't $ref it: it's spelt out, with
        // note required and the init-only tag (no constructor parameter) still optional.
        using var document = CompilationHelper.EmitOpenApi(source);
        var schema = RequestSchema(document, "/api/note-uploads", "multipart/form-data");
        Assert.False(schema.TryGetProperty("$ref", out _));
        Assert.Equal(
            ["title", "note", "scan"],
            schema.GetProperty("required").EnumerateArray().Select(name => name.GetString())
        );
    }

    [Theory]
    [InlineData(nameof(FormDisagreedClass.DataOnly), "data_only")]
    [InlineData(nameof(FormDisagreedClass.FormOnly), "form_only")]
    public void Generation_refuses_a_rename_one_host_never_reads(string member, string rename)
    {
        var error = Assert.Throws<RivetUserException>(() =>
            GenerationParts(typeof(FormDisagreedClass), FormBinding.FormHosts.Any, member)
        );

        Assert.Contains("RIV1100", error.Message);
        Assert.Contains($"'{member}'", error.Message);
        Assert.Contains($"'{rename}'", error.Message);
    }

    [Fact]
    public void Generation_refuses_a_property_targeted_rename_MVC_never_sees()
    {
        var error = Assert.Throws<RivetUserException>(() =>
            GenerationParts(typeof(FormDisagreedRecord), FormBinding.FormHosts.Any)
        );

        Assert.Contains("RIV1100", error.Message);
        Assert.Contains("'PropForm'", error.Message);
    }

    [Fact]
    public void An_MVC_only_endpoint_reads_FromForm_renames_and_ignores_DataMember()
    {
        Assert.Equal(
            ["json", "dataOnly", "form_only"],
            GenerationParts(typeof(FormDisagreedClass), FormBinding.FormHosts.Mvc)
                .Select(part => part.Name)
        );
    }

    // ---------------------------------------------------------------
    // Generation: contracts and [FromForm] actions.
    // ---------------------------------------------------------------

    [Fact]
    public void Form_encoded_fields_are_the_members_the_form_binders_bind()
    {
        var source = """
            using System.Runtime.Serialization;
            using System.Text.Json.Serialization;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public class SignupBase
            {
                public string? Campaign { get; set; }
            }

            [RivetType]
            public sealed class SignupForm : SignupBase
            {
                public string Email { get; set; } = "";

                // The form binders never read [JsonIgnore], [JsonPropertyName] or an
                // internal member.
                [JsonIgnore]
                public string? Referrer { get; set; }

                internal string? Secret { get; set; }

                [JsonPropertyName("display_name")]
                public string? DisplayName { get; set; }

                [DataMember(Name = "x-source")]
                [FromForm(Name = "x-source")]
                public string? Source { get; set; }
            }

            [RivetType]
            public sealed record SignupResult(string Id);

            [RivetContract]
            public static class SignupContract
            {
                public static readonly RouteDefinition<SignupForm, SignupResult> Signup =
                    Define.Post<SignupForm, SignupResult>("/api/signup").FormEncoded();
            }
            """;

        var endpoint = Assert.Single(CompilationHelper.WalkContract(source).Endpoints);
        Assert.Equal(
            ["campaign", "email", "referrer", "displayName", "x-source"],
            endpoint.Params.Select(param => param.Name)
        );
        Assert.All(endpoint.Params, param => Assert.Equal(ParamSource.FormField, param.Source));

        // The component lists display_name and leaves referrer out, so the body is inline.
        using var document = CompilationHelper.EmitOpenApi(source);
        var schema = RequestSchema(document, "/api/signup", "application/x-www-form-urlencoded");
        Assert.False(schema.TryGetProperty("$ref", out _));
        Assert.Equal(
            ["campaign", "email", "referrer", "displayName", "x-source"],
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name)
        );
        Assert.Equal("SignupForm", schema.GetProperty("x-rivet-input-type").GetString());
    }

    [Fact]
    public void A_form_encoded_body_references_its_component_when_the_names_agree()
    {
        var source = """
            using System.Runtime.Serialization;
            using System.Text.Json.Serialization;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LoginForm
            {
                public required string Email { get; init; }

                [JsonPropertyName("pass_word")]
                [DataMember(Name = "pass_word")]
                [FromForm(Name = "pass_word")]
                public required string Password { get; init; }
            }

            [RivetType]
            public sealed record TokenDto(string Token);

            [RivetContract]
            public static class AuthContract
            {
                public static readonly RouteDefinition<LoginForm, TokenDto> Login =
                    Define.Post<LoginForm, TokenDto>("/api/login").FormEncoded();
            }
            """;

        using var document = CompilationHelper.EmitOpenApi(source);
        Assert.Equal(
            "#/components/schemas/LoginForm",
            RequestSchema(document, "/api/login", "application/x-www-form-urlencoded")
                .GetProperty("$ref")
                .GetString()
        );
    }

    [Theory]
    [InlineData("[FromForm(Name = \"alias\")]", ".FormEncoded()")]
    [InlineData("[DataMember(Name = \"alias\")]", ".FormEncoded()")]
    [InlineData("[FromQuery]", ".FormEncoded()")]
    [InlineData("[FromForm(Name = \"alias\")]", "")]
    [InlineData("[DataMember(Name = \"alias\")]", "")]
    public void A_contract_form_member_the_hosts_read_differently_is_RIV1100(
        string rename,
        string marker
    )
    {
        var source = $$"""
            using System.Runtime.Serialization;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed class NoteForm
            {
                public string Title { get; set; } = "";

                {{rename}}
                public string? Body { get; set; }

                {{(marker == "" ? "public IFormFile? Attachment { get; set; }" : "")}}
            }

            [RivetType]
            public sealed record NoteDto(string Id);

            [RivetContract]
            public static class NotesContract
            {
                public static readonly RouteDefinition<NoteForm, NoteDto> Submit =
                    Define.Post<NoteForm, NoteDto>("/api/notes"){{marker}};
            }
            """;

        var error = Assert.Throws<RivetUserException>(() => CompilationHelper.WalkContract(source));
        Assert.Contains("RIV1100", error.Message);
        Assert.Contains("'Body'", error.Message);
        Assert.Contains("POST /api/notes", error.Message);
    }

    [Fact]
    public void A_FromForm_record_on_an_action_is_its_parts_named_as_MVC_reads_them()
    {
        var source = """
            using System;
            using System.Runtime.Serialization;
            using System.Text.Json.Serialization;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed class UploadForm
            {
                [FromForm(Name = "alias")]
                public string? Renamed { get; set; }

                // MVC never reads either.
                [DataMember(Name = "ignored")]
                [JsonPropertyName("also_ignored")]
                public string? Other { get; set; }

                public IFormFile? File { get; set; }
            }

            [RivetType]
            public sealed record UploadResult(string Id);

            public static class Endpoints
            {
                [RivetEndpoint]
                [HttpPost("/api/uploads")]
                [ProducesResponseType(typeof(UploadResult), 200)]
                public static Task<IActionResult> Upload([FromForm] UploadForm form)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpPost("/api/uploads/prefixed")]
                [ProducesResponseType(typeof(UploadResult), 200)]
                public static Task<IActionResult> Prefixed([FromForm(Name = "p")] UploadForm form)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = CompilationHelper.WalkMerged(source).Endpoints;
        Assert.Equal(
            [
                ("alias", ParamSource.FormField),
                ("other", ParamSource.FormField),
                ("file", ParamSource.File),
            ],
            endpoints
                .Single(endpoint => endpoint.RouteTemplate == "/api/uploads")
                .Params.Select(param => (param.Name, param.Source))
        );
        // The parameter's own Name = is a prefix MVC then reads every part under.
        Assert.Equal(
            ["p.alias", "p.other", "p.file"],
            endpoints
                .Single(endpoint => endpoint.RouteTemplate == "/api/uploads/prefixed")
                .Params.Select(param => param.Name)
        );

        // A file in the record makes the body multipart, which a urlencoded body can't carry.
        using var document = CompilationHelper.EmitOpenApi(source);
        var schema = RequestSchema(document, "/api/uploads", "multipart/form-data");
        Assert.Equal(
            ["alias", "other", "file"],
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name)
        );
        Assert.Equal(
            "binary",
            schema.GetProperty("properties").GetProperty("file").GetProperty("format").GetString()
        );
    }

    // ---------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------

    private static IReadOnlyList<FormBinding.FormPart> GenerationParts(
        Type type,
        FormBinding.FormHosts hosts,
        string? only = null
    )
    {
        var (compilation, wkt) = BindingHost.Probe;
        var symbol = compilation.GetTypeByMetadataName(type.FullName!)!;
        return FormBinding.Parts(
            compilation,
            wkt,
            TypeWalker.Create(compilation, wkt, []),
            symbol,
            symbol
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Where(property => only is not null && property.Name != only)
                .Select(property => property.Name)
                .ToHashSet(),
            hosts,
            $"POST /form-parts ({type.Name})"
        );
    }

    private static Task<BindingHost> StartAsync(bool mvc) =>
        BindingHost.StartAsync(
            mvc,
            app =>
            {
                Map<FormAgreedClass>(app, "class");
                Map<FormAgreedInit>(app, "init");
                Map<FormAgreedRecord>(app, "record");
                Map<FormDisagreedClass>(app, "disagreed");
                Map<FormDisagreedRecord>(app, "disagreed-record");
                Map<FormBareRecord>(app, "bare");
                Map<FormOptionalRecord>(app, "optional");
            }
        );

    private static void Map<T>(WebApplication app, string shape) =>
        app.MapPost($"/form-parts/minimal/{shape}", ([FromForm] T form) => FormEcho.Of(form))
            .DisableAntiforgery();

    /// <summary>A multipart body: each text part's value is its name; each file is named for it.</summary>
    private static MultipartFormDataContent Multipart(IEnumerable<(string Name, bool IsFile)> parts)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, isFile) in parts)
        {
            if (isFile)
            {
                var file = new ByteArrayContent([1, 2, 3]);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(file, name, name);
            }
            else
            {
                content.Add(new StringContent(name), name);
            }
        }

        return content;
    }

    private static JsonElement RequestSchema(
        JsonDocument document,
        string path,
        string mediaType
    ) =>
        document
            .RootElement.GetProperty("paths")
            .GetProperty(path)
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty(mediaType)
            .GetProperty("schema");
}

/// <summary>What a form handler bound: each non-null member's value, a file as its file name.</summary>
internal static class FormEcho
{
    public static string Of(object? value) =>
        JsonSerializer.Serialize(
            value
                ?.GetType()
                .GetProperties()
                .Select(property => (property.Name, Value: property.GetValue(value)))
                .Where(pair => pair.Value is not null)
                .ToDictionary(
                    pair => pair.Name,
                    pair =>
                        pair.Value is IFormFile file
                            ? $"file:{file.FileName}"
                            : pair.Value!.ToString()
                )
        );
}

public sealed class FormAgreedClass
{
    public string? Plain { get; set; }

    [JsonPropertyName("json_name")]
    public string? Json { get; set; }

    [DataMember(Name = "both_name")]
    [FromForm(Name = "both_name")]
    public string? Both { get; set; }

    public IFormFile? Upload { get; set; }

    [DataMember(Name = "file_name")]
    [FromForm(Name = "file_name")]
    public IFormFile? Renamed { get; set; }
}

public sealed record FormAgreedInit
{
    public required string Title { get; init; }

    [JsonPropertyName("x-meta")]
    [DataMember(Name = "x-meta")]
    [FromForm(Name = "x-meta")]
    public string? Meta { get; init; }

    [JsonPropertyName("file_name")]
    [DataMember(Name = "file_name")]
    [FromForm(Name = "file_name")]
    public IFormFile? Upload { get; init; }
}

// On a positional record MVC reads the parameter's [FromForm] and the form mapper the
// property's [DataMember].
public sealed record FormAgreedRecord(
    string? Plain,
    [property: DataMember(Name = "x-meta")] [FromForm(Name = "x-meta")] string? Meta,
    IFormFile? Upload
);

public sealed class FormDisagreedClass
{
    [JsonPropertyName("json_name")]
    public string? Json { get; set; }

    [DataMember(Name = "data_only")]
    public string? DataOnly { get; set; }

    [FromForm(Name = "form_only")]
    public string? FormOnly { get; set; }
}

public sealed record FormDisagreedRecord(
    string? Plain,
    [property: DataMember(Name = "prop_form")]
    [property: FromForm(Name = "prop_form")]
        string? PropForm
);

public sealed record FormBareRecord(string? A, string? B);

public sealed record FormOptionalRecord(string Title, [property: RivetOptional] string? Note);

/// <summary>The MVC handlers for <see cref="FormPartNameTests"/>.</summary>
[ApiController]
public sealed class FormPartController : ControllerBase
{
    [HttpPost("/form-parts/mvc/class")]
    public string Class([FromForm] FormAgreedClass form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/init")]
    public string Init([FromForm] FormAgreedInit form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/record")]
    public string Record([FromForm] FormAgreedRecord form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/disagreed")]
    public string Disagreed([FromForm] FormDisagreedClass form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/disagreed-record")]
    public string DisagreedRecord([FromForm] FormDisagreedRecord form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/bare")]
    public string Bare([FromForm] FormBareRecord form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/optional")]
    public string Optional([FromForm] FormOptionalRecord form) => FormEcho.Of(form);
}
