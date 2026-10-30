using System.Collections.Immutable;
using System.Collections.ObjectModel;
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

    [Theory]
    // A second public constructor: MVC calls the parameterless one and sets the properties;
    // the form mapper binds nothing at all.
    [InlineData(true, "two-ctor", HttpStatusCode.OK, "{\"Title\":\"t\"}")]
    [InlineData(false, "two-ctor", HttpStatusCode.OK, "null")]
    // A class whose one constructor takes parameters: the form mapper calls it; MVC can't
    // create it (it calls a constructor with parameters only for a record).
    [InlineData(true, "ctor-only", HttpStatusCode.InternalServerError, "")]
    [InlineData(false, "ctor-only", HttpStatusCode.OK, "{\"Title\":\"Title\",\"Plain\":\"Plain\"}")]
    // A record whose constructor parameter matches its property only ignoring case: MVC
    // wants the exact name, so it can't create it either.
    [InlineData(true, "record-mismatch", HttpStatusCode.InternalServerError, "")]
    [InlineData(
        false,
        "record-mismatch",
        HttpStatusCode.OK,
        "{\"Title\":\"Title\",\"Plain\":\"Plain\"}"
    )]
    // A struct or a record struct: MVC needs a public parameterless constructor, which
    // neither declares (reflection lists no implicit one), and a record struct has no
    // record constructor for it either; the form mapper binds both.
    [InlineData(true, "struct", HttpStatusCode.InternalServerError, "")]
    [InlineData(false, "struct", HttpStatusCode.OK, "{\"Title\":\"Title\",\"Plain\":\"Plain\"}")]
    [InlineData(true, "record-struct", HttpStatusCode.InternalServerError, "")]
    [InlineData(
        false,
        "record-struct",
        HttpStatusCode.OK,
        "{\"Title\":\"Title\",\"Plain\":\"Plain\"}"
    )]
    // An abstract class, parameterless constructor or not, is one MVC can't create, and the
    // form mapper binds nothing on it.
    [InlineData(true, "abstract", HttpStatusCode.InternalServerError, "")]
    [InlineData(false, "abstract", HttpStatusCode.OK, "null")]
    // A get-only immutable file collection: MVC can't add to it and can't create a
    // collection of its type, so it fails the request, file sent or not; the form mapper
    // leaves the member alone.
    [InlineData(true, "immutable-files", HttpStatusCode.InternalServerError, "")]
    [InlineData(false, "immutable-files", HttpStatusCode.OK, "{\"Plain\":\"Plain\"}")]
    public async Task The_hosts_construct_these_types_differently(
        bool mvc,
        string shape,
        HttpStatusCode status,
        string bound
    )
    {
        await using var host = await StartAsync(mvc);
        var (actual, body) = await host.PostAsync(
            $"/form-parts/{(mvc ? "mvc" : "minimal")}/{shape}",
            Multipart([("t", false), ("Title", false), ("Plain", false)])
        );

        Assert.Equal(status, actual);
        if (status == HttpStatusCode.OK)
        {
            Assert.Equal(bound, body);
        }
    }

    [Theory]
    [InlineData(typeof(FormTwoCtor), "Any", "more than one public constructor")]
    [InlineData(typeof(FormTwoCtor), "MinimalApi", "more than one public constructor")]
    [InlineData(typeof(FormCtorOnly), "Any", "MVC's model binder can't create it")]
    [InlineData(typeof(FormCtorOnly), "Mvc", "MVC's model binder can't create it")]
    [InlineData(typeof(FormRecordMismatch), "Any", "exact name and type")]
    [InlineData(typeof(FormStruct), "Mvc", "public parameterless constructor")]
    [InlineData(typeof(FormRecordStruct), "Any", "record class's one public constructor")]
    [InlineData(typeof(FormAbstract), "Mvc", "MVC's model binder can't create an abstract type")]
    [InlineData(
        typeof(FormAbstract),
        "MinimalApi",
        "form mapper binds nothing on an abstract type"
    )]
    public void Generation_refuses_a_form_input_a_host_can_t_create(
        Type type,
        string hosts,
        string why
    )
    {
        var error = Assert.Throws<RivetUserException>(() =>
            GenerationParts(type, Enum.Parse<FormBinding.FormHosts>(hosts))
        );

        Assert.Contains("RIV1100", error.Message);
        Assert.Contains("binds no part", error.Message);
        Assert.Contains(why, error.Message);
    }

    [Fact]
    public void Each_host_alone_names_the_parts_of_a_type_it_can_create()
    {
        Assert.Equal(
            ["t"],
            GenerationParts(typeof(FormTwoCtor), FormBinding.FormHosts.Mvc)
                .Select(part => part.Name)
        );
        // The form mapper calls the constructor, so its parameter's part is required.
        Assert.Equal(
            [("title", true), ("plain", false)],
            GenerationParts(typeof(FormCtorOnly), FormBinding.FormHosts.MinimalApi)
                .Select(part => (part.Name, part.Required))
        );
        // A struct it sets property by property; a record struct it calls the constructor of.
        Assert.Equal(
            [("title", false), ("plain", false)],
            GenerationParts(typeof(FormStruct), FormBinding.FormHosts.MinimalApi)
                .Select(part => (part.Name, part.Required))
        );
        Assert.Equal(
            [("title", true), ("plain", true)],
            GenerationParts(typeof(FormRecordStruct), FormBinding.FormHosts.MinimalApi)
                .Select(part => (part.Name, part.Required))
        );
    }

    [Theory]
    // MVC skips [BindNever] and binds [IgnoreDataMember]; the form mapper the reverse.
    // Neither binds a file nothing can set.
    [InlineData(
        true,
        "{\"Skipped\":\"Skipped\",\"Plain\":\"Plain\",\"Files\":\"file:Files\",\"NewFiles\":\"file:NewFiles\"}"
    )]
    [InlineData(false, "{\"Never\":\"Never\",\"Plain\":\"Plain\"}")]
    public async Task The_hosts_skip_these_members_differently(bool mvc, string bound)
    {
        await using var host = await StartAsync(mvc);
        var (status, body) = await host.PostAsync(
            $"/form-parts/{(mvc ? "mvc" : "minimal")}/skips",
            Multipart([
                ("Skipped", false),
                ("Never", false),
                ("Both", false),
                ("Plain", false),
                ("ReadOnlyFile", true),
                ("PrivateSet", true),
                ("Files", true),
                ("NewFiles", true),
                ("Unset", true),
                ("Computed", true),
                ("ReadOnlyFiles", true),
            ])
        );

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(bound, body);
    }

    [Fact]
    public void Each_host_alone_drops_the_members_it_skips()
    {
        string[] members = ["Skipped", "Never", "Both", "Plain"];
        IEnumerable<string> Names(FormBinding.FormHosts hosts) =>
            members.SelectMany(member =>
                GenerationParts(typeof(FormSkips), hosts, member).Select(part => part.Name)
            );

        Assert.Equal(["skipped", "plain"], Names(FormBinding.FormHosts.Mvc));
        Assert.Equal(["never", "plain"], Names(FormBinding.FormHosts.MinimalApi));
    }

    [Theory]
    [InlineData("Skipped", "skipped by the minimal-API form mapper ([IgnoreDataMember])")]
    [InlineData("Never", "skipped by MVC's model binder ([BindNever])")]
    public void Generation_refuses_a_member_one_host_skips(string member, string why)
    {
        var error = Assert.Throws<RivetUserException>(() =>
            GenerationParts(typeof(FormSkips), FormBinding.FormHosts.Any, member)
        );

        Assert.Contains("RIV1100", error.Message);
        Assert.Contains($"'{member}'", error.Message);
        Assert.Contains(why, error.Message);
    }

    [Theory]
    [InlineData(typeof(FormSkips), "ReadOnlyFile", false)]
    [InlineData(typeof(FormSkips), "PrivateSet", false)]
    // Get-only collections MVC can't fill in place (the host rows above).
    [InlineData(typeof(FormSkips), "Unset", true)]
    [InlineData(typeof(FormSkips), "Computed", true)]
    [InlineData(typeof(FormSkips), "ReadOnlyFiles", true)]
    public void Generation_refuses_a_file_nothing_can_set(Type type, string member, bool collection)
    {
        foreach (var hosts in Enum.GetValues<FormBinding.FormHosts>())
        {
            var error = Assert.Throws<RivetUserException>(() =>
                GenerationParts(type, hosts, member)
            );

            Assert.Contains("RIV1100", error.Message);
            Assert.Contains($"'{member}' on", error.Message);
            Assert.Contains("nothing can set it", error.Message);
            Assert.Equal(
                collection && hosts != FormBinding.FormHosts.MinimalApi,
                error.Message.Contains("initializer creates a writable one")
            );
        }

        // A member both hosts skip is no part, and refused by neither.
        Assert.Empty(GenerationParts(typeof(FormSkips), FormBinding.FormHosts.Any, "Both"));
    }

    [Theory]
    [InlineData(true, "{\"Home\":\"city:Home.City\",\"Computed\":\"city:\"}")]
    // The form mapper sets no member here, so it hands the handler no form at all.
    [InlineData(false, "null")]
    public async Task Only_MVC_binds_into_a_get_only_complex_member(bool mvc, string bound)
    {
        await using var host = await StartAsync(mvc);
        var (status, body) = await host.PostAsync(
            $"/form-parts/{(mvc ? "mvc" : "minimal")}/in-place",
            Multipart([("Home.City", false), ("Unset.City", false), ("Computed.City", false)])
        );

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(bound, body);
    }

    [Fact]
    public void A_get_only_complex_member_is_a_part_only_MVC_fills()
    {
        // The initialised member is MVC's part; one left null or made on each read is no
        // one's (the host rows above).
        Assert.Equal(
            "home",
            Assert
                .Single(GenerationParts(typeof(FormInPlace), FormBinding.FormHosts.Mvc, "Home"))
                .Name
        );
        Assert.Empty(GenerationParts(typeof(FormInPlace), FormBinding.FormHosts.Mvc, "Unset"));
        Assert.Empty(GenerationParts(typeof(FormInPlace), FormBinding.FormHosts.Mvc, "Computed"));
        Assert.Empty(
            GenerationParts(typeof(FormInPlace), FormBinding.FormHosts.MinimalApi, "Home")
        );

        var error = Assert.Throws<RivetUserException>(() =>
            GenerationParts(typeof(FormInPlace), FormBinding.FormHosts.Any, "Home")
        );
        Assert.Contains("RIV1100", error.Message);
        Assert.Contains(
            "bound by MVC's model binder, which fills the object its getter returns, but left "
                + "unset by the minimal-API form mapper",
            error.Message
        );
    }

    [Theory]
    [InlineData("Files", "files")]
    [InlineData("NewFiles", "newFiles")]
    public void A_get_only_file_collection_is_a_part_only_MVC_fills(string member, string name)
    {
        // MVC adds the files to the collection the getter returns, when its initializer
        // creates a writable one; the form mapper can't set it.
        var part = Assert.Single(
            GenerationParts(typeof(FormSkips), FormBinding.FormHosts.Mvc, member)
        );
        Assert.Equal((name, true), (part.Name, part.IsFile));

        Assert.Contains(
            "nothing can set it",
            Assert
                .Throws<RivetUserException>(() =>
                    GenerationParts(typeof(FormSkips), FormBinding.FormHosts.MinimalApi, member)
                )
                .Message
        );
        var error = Assert.Throws<RivetUserException>(() =>
            GenerationParts(typeof(FormSkips), FormBinding.FormHosts.Any, member)
        );
        Assert.Contains("RIV1100", error.Message);
        Assert.Contains(
            "bound by MVC's model binder, which fills the collection its getter returns, but left "
                + "unset by the minimal-API form mapper",
            error.Message
        );
    }

    [Fact]
    public void An_immutable_collection_is_no_part_MVC_fills()
    {
        Assert.Equal(
            ["plain"],
            GenerationParts(typeof(FormImmutableFiles), FormBinding.FormHosts.Mvc)
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
        // SignupForm is a component, whose name import gives the component, so the inline
        // body's record is pinned by the importer's {fieldName}Request convention instead.
        Assert.Equal("SignupRequest", schema.GetProperty("x-rivet-input-type").GetString());
    }

    [Fact]
    public void An_inline_form_body_pins_a_name_no_component_holds_when_its_record_is_named_by_convention()
    {
        // The house input record {Op}Request is a component and holds the convention's name
        // too, so the pin moves on to the first free candidate, the one import synthesizes.
        var source = """
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AddCommentRequest
            {
                public required string Text { get; init; }

                [JsonExtensionData]
                public Dictionary<string, JsonElement>? Extra { get; init; }
            }

            [RivetType]
            public sealed record CommentDto(string Id);

            [RivetContract]
            public static class CommentsContract
            {
                public static readonly RouteDefinition<AddCommentRequest, CommentDto> AddComment =
                    Define.Post<AddCommentRequest, CommentDto>("/api/comments").FormEncoded();
            }
            """;

        using var document = CompilationHelper.EmitOpenApi(source);
        var schema = RequestSchema(document, "/api/comments", "application/x-www-form-urlencoded");
        Assert.False(schema.TryGetProperty("$ref", out _));
        var pinned = schema.GetProperty("x-rivet-input-type").GetString();
        Assert.Equal("AddCommentRequest_2", pinned);

        var imported = CompilationHelper.Import(document.RootElement.GetRawText());
        var contract = CompilationHelper.FindFile(imported, "CommentsContract.cs");
        Assert.Contains($"<{pinned}, CommentDto>", contract);
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
        // An instance method is an MVC action.
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

            public sealed class UploadsController : ControllerBase
            {
                [RivetEndpoint]
                [HttpPost("/api/uploads")]
                [ProducesResponseType(typeof(UploadResult), 200)]
                public Task<IActionResult> Upload([FromForm] UploadForm form)
                    => throw new NotImplementedException();

                [RivetEndpoint]
                [HttpPost("/api/uploads/prefixed")]
                [ProducesResponseType(typeof(UploadResult), 200)]
                public Task<IActionResult> Prefixed([FromForm(Name = "p")] UploadForm form)
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

    [Fact]
    public void A_FromForm_record_on_a_static_endpoint_is_its_parts_named_as_the_form_mapper_reads_them()
    {
        // A static method is a minimal-API handler: the form mapper reads [DataMember] names,
        // ignores a member's [FromForm(Name)], and never prefixes by the parameter's Name =.
        var source = """
            using System;
            using System.Runtime.Serialization;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed class UploadForm
            {
                [FromForm(Name = "ignored")]
                public string? Renamed { get; set; }

                [DataMember(Name = "alias")]
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
                [HttpPost("/api/uploads/named")]
                [ProducesResponseType(typeof(UploadResult), 200)]
                public static Task<IActionResult> Named([FromForm(Name = "p")] UploadForm form)
                    => throw new NotImplementedException();
            }
            """;

        var endpoints = CompilationHelper.WalkMerged(source).Endpoints;
        foreach (var route in new[] { "/api/uploads", "/api/uploads/named" })
        {
            Assert.Equal(
                [
                    ("renamed", ParamSource.FormField),
                    ("alias", ParamSource.FormField),
                    ("file", ParamSource.File),
                ],
                endpoints
                    .Single(endpoint => endpoint.RouteTemplate == route)
                    .Params.Select(param => (param.Name, param.Source))
            );
        }
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
                Map<FormTwoCtor>(app, "two-ctor");
                Map<FormCtorOnly>(app, "ctor-only");
                Map<FormRecordMismatch>(app, "record-mismatch");
                Map<FormStruct>(app, "struct");
                Map<FormRecordStruct>(app, "record-struct");
                Map<FormAbstract>(app, "abstract");
                Map<FormSkips>(app, "skips");
                Map<FormImmutableFiles>(app, "immutable-files");
                Map<FormInPlace>(app, "in-place");
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

/// <summary>
/// What a form handler bound: each non-null, non-empty member's value, a file as its file
/// name and a collection of files as theirs.
/// </summary>
internal static class FormEcho
{
    public static string Of(object? value) =>
        JsonSerializer.Serialize(
            value
                ?.GetType()
                .GetProperties()
                .Select(property => (property.Name, Value: property.GetValue(value)))
                .Where(pair => pair.Value is not (null or ICollection<IFormFile> { Count: 0 }))
                .ToDictionary(
                    pair => pair.Name,
                    pair =>
                        pair.Value switch
                        {
                            IFormFile file => $"file:{file.FileName}",
                            IEnumerable<IFormFile> files => string.Join(
                                ',',
                                files.Select(file => $"file:{file.FileName}")
                            ),
                            var other => other!.ToString(),
                        }
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

public sealed class FormTwoCtor
{
    public FormTwoCtor() { }

    public FormTwoCtor(string title) => Title = title;

    [FromForm(Name = "t")]
    [DataMember(Name = "t")]
    public string? Title { get; set; }
}

public sealed class FormCtorOnly(string title)
{
    public string Title { get; } = title;

    public string? Plain { get; set; }
}

public sealed record FormRecordMismatch
{
    public FormRecordMismatch(string title) => Title = title;

    public string Title { get; }

    public string? Plain { get; set; }
}

public struct FormStruct
{
    public string? Title { get; set; }

    public string? Plain { get; set; }
}

public record struct FormRecordStruct(string? Title, string? Plain);

public abstract class FormAbstract
{
    public string? Title { get; set; }

    public string? Plain { get; set; }
}

public sealed class FormSkips
{
    [IgnoreDataMember]
    public string? Skipped { get; set; }

    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public string? Never { get; set; }

    [IgnoreDataMember]
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    public string? Both { get; set; }

    public string? Plain { get; set; }

    public IFormFile? ReadOnlyFile { get; }

    public IFormFile? PrivateSet { get; private set; }

    public List<IFormFile> Files { get; } = [];

    // MVC adds to the collection a getter returns only when it's a live, writable one:
    // a new List<T> is; null, a fresh copy on each read, and a read-only collection aren't,
    // and the new list MVC builds instead has nowhere to go.
    public List<IFormFile> NewFiles { get; } = new();

    public List<IFormFile>? Unset { get; }

    public List<IFormFile> Computed => [];

    public ICollection<IFormFile> ReadOnlyFiles { get; } = new ReadOnlyCollection<IFormFile>([]);
}

// MVC can't fill an immutable collection in place, and building one of its own fails every
// request: ImmutableList<T> has no parameterless constructor.
public sealed class FormImmutableFiles
{
    public string? Plain { get; set; }

    public ImmutableList<IFormFile> Files { get; } = [];
}

// MVC binds a get-only complex member into the object its getter returns, and creates one
// only when that's null, which a get-only property throws away; the form mapper needs a setter.
public sealed class FormInPlace
{
    public FormAddress Home { get; } = new();

    public FormAddress? Unset { get; }

    public FormAddress Computed => new();
}

public sealed class FormAddress
{
    public string? City { get; set; }

    public override string ToString() => $"city:{City}";
}

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

    [HttpPost("/form-parts/mvc/two-ctor")]
    public string TwoCtor([FromForm] FormTwoCtor form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/ctor-only")]
    public string CtorOnly([FromForm] FormCtorOnly form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/record-mismatch")]
    public string RecordMismatch([FromForm] FormRecordMismatch form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/struct")]
    public string Struct([FromForm] FormStruct form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/record-struct")]
    public string RecordStruct([FromForm] FormRecordStruct form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/abstract")]
    public string Abstract([FromForm] FormAbstract form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/skips")]
    public string Skips([FromForm] FormSkips form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/immutable-files")]
    public string ImmutableFiles([FromForm] FormImmutableFiles form) => FormEcho.Of(form);

    [HttpPost("/form-parts/mvc/in-place")]
    public string InPlace([FromForm] FormInPlace form) => FormEcho.Of(form);
}
