using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Rivet.RuntimeTests.TerminalAndAdapterTests;

namespace Rivet.RuntimeTests;

/// <summary>
/// The spec describes System.Text.Json's web defaults. Host JSON options that change what
/// goes over the wire are refused when a result runs; the ones that only loosen reading or
/// change formatting are left alone. The ASP.NET defaults themselves pass on every runtime,
/// which every other adapter test proves by running at all.
/// </summary>
public sealed class HostJsonOptionsTests
{
    private static readonly Dictionary<
        string,
        (Action<JsonSerializerOptions> Change, string Named)
    > _refused = new()
    {
        ["naming"] = (
            options => options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            "PropertyNamingPolicy is JsonNamingPolicy.SnakeCaseLower, not JsonNamingPolicy.CamelCase"
        ),
        ["no naming"] = (
            options => options.PropertyNamingPolicy = null,
            "PropertyNamingPolicy is none"
        ),
        ["dictionary keys"] = (
            options => options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            "DictionaryKeyPolicy"
        ),
        ["converter"] = (
            options => options.Converters.Add(new JsonStringEnumConverter()),
            "Converters registers JsonStringEnumConverter"
        ),
        ["rivet converter"] = (
            options => options.Converters.Add(new RivetCamelCaseEnumConverter<DayOfWeek>()),
            "Converters registers RivetCamelCaseEnumConverter"
        ),
        ["numbers as strings"] = (
            options => options.NumberHandling = JsonNumberHandling.WriteAsString,
            "NumberHandling is WriteAsString"
        ),
        ["named literals"] = (
            options => options.NumberHandling |= JsonNumberHandling.AllowNamedFloatingPointLiterals,
            "AllowNamedFloatingPointLiterals"
        ),
        ["ignore condition"] = (
            options => options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            "DefaultIgnoreCondition is WhenWritingNull"
        ),
#pragma warning disable SYSLIB0020
        ["null values"] = (options => options.IgnoreNullValues = true, "IgnoreNullValues is on"),
#pragma warning restore SYSLIB0020
        ["read-only properties"] = (
            options => options.IgnoreReadOnlyProperties = true,
            "IgnoreReadOnlyProperties"
        ),
        ["read-only fields"] = (
            options => options.IgnoreReadOnlyFields = true,
            "IgnoreReadOnlyFields"
        ),
        ["fields"] = (options => options.IncludeFields = true, "IncludeFields"),
        ["references"] = (
            options => options.ReferenceHandler = ReferenceHandler.Preserve,
            "ReferenceHandler"
        ),
        ["unmapped members"] = (
            options => options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            "UnmappedMemberHandling is Disallow"
        ),
        ["custom resolver"] = (
            options => options.TypeInfoResolver = new RenamingResolver(),
            "TypeInfoResolver uses RenamingResolver"
        ),
#if NET9_0_OR_GREATER
        ["required constructor parameters"] = (
            options => options.RespectRequiredConstructorParameters = true,
            "RespectRequiredConstructorParameters"
        ),
#endif
    };

    public static TheoryData<string, bool> RefusedSettings()
    {
        var data = new TheoryData<string, bool>();
        foreach (var name in _refused.Keys)
        {
            data.Add(name, false);
            data.Add(name, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedSettings))]
    public async Task A_wire_changing_setting_is_refused(string setting, bool mvc)
    {
        var (change, named) = _refused[setting];
        var result = Define.Get<Payload>("/items").Success(new Payload("item_1"));

        var refusal = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(result, mvc, services: services => Configure(services, change))
        );

        Assert.Contains(named, refusal.Message);
        Assert.Contains(
            mvc
                ? "Microsoft.AspNetCore.Mvc.JsonOptions"
                : "Microsoft.AspNetCore.Http.Json.JsonOptions",
            refusal.Message
        );
        Assert.Contains("declare the behaviour on the types instead", refusal.Message);
    }

    // Request bodies on these endpoints are read with the same options, so the check doesn't
    // wait for a JSON response.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bodyless_and_file_results_are_checked_too(bool mvc)
    {
        static void Change(JsonSerializerOptions options) =>
            options.PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower;

        await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(
                Define.Delete("/items/1").Success(),
                mvc,
                services: s => Configure(s, Change)
            )
        );
        await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(Define.File("/report").File([1]), mvc, services: s => Configure(s, Change))
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_difference_is_named_at_once(bool mvc)
    {
        static void Change(JsonSerializerOptions options)
        {
            options.PropertyNamingPolicy = null;
            options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;
            options.Converters.Add(new JsonStringEnumConverter());
        }

        var refusal = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(
                Define.Get<Payload>("/items").Success(new Payload("item_1")),
                mvc,
                services: s => Configure(s, Change)
            )
        );

        Assert.Contains("PropertyNamingPolicy", refusal.Message);
        Assert.Contains("DefaultIgnoreCondition", refusal.Message);
        Assert.Contains("Converters", refusal.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reading_leniency_and_formatting_are_left_alone(bool mvc)
    {
        static void Change(JsonSerializerOptions options)
        {
            options.WriteIndented = true;
            options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            options.MaxDepth = 8;
            options.PropertyNameCaseInsensitive = false;
            options.AllowTrailingCommas = true;
            options.ReadCommentHandling = JsonCommentHandling.Skip;
            options.NumberHandling = JsonNumberHandling.Strict;
            options.PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate;
            options.UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode;
#if NET9_0_OR_GREATER
            options.RespectNullableAnnotations = true;
            options.AllowOutOfOrderMetadataProperties = true;
#endif
        }

        var response = await ExecuteAsync(
            Define.Get<Payload>("/items").Success(new Payload("item_1")),
            mvc,
            services: s => Configure(s, Change)
        );

        Assert.Equal(
            "item_1",
            JsonDocument.Parse(response.Body).RootElement.GetProperty("id").GetString()
        );
    }

    // ProblemDetails puts ASP.NET's own source-generated context in the minimal-API chain, and
    // an AOT app puts its own there; both follow the host options at runtime. So do the naming,
    // ignore and converter settings on a context's own [JsonSourceGenerationOptions].
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_generated_contexts_and_problem_details_are_allowed(bool mvc)
    {
        var response = await ExecuteAsync(
            Define.Get<Painted>("/items").Success(new Painted("item_1", Shade.Dark, null)),
            mvc,
            services: services =>
            {
                services.AddProblemDetails();
                Configure(
                    services,
                    options =>
                    {
                        options.TypeInfoResolverChain.Insert(0, PayloadJsonContext.Default);
                        options.TypeInfoResolverChain.Insert(
                            0,
                            GeneratedOptionsJsonContext.Default
                        );
                    }
                );
            }
        );

        Assert.Equal(
            """{"id":"item_1","shade":1,"note":null}""",
            Encoding.UTF8.GetString(response.Body)
        );
    }

    // UseStringEnumConverter is the one context setting the generator bakes into the type
    // infos it emits. The host options never see it, and "Dark" goes out where the spec says 1.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_context_generated_with_string_enums_is_refused(bool mvc)
    {
        var refusal = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(
                Define.Get<Painted>("/items").Success(new Painted("item_1", Shade.Dark, null)),
                mvc,
                services: services =>
                {
                    services.AddProblemDetails();
                    Configure(
                        services,
                        options =>
                            options.TypeInfoResolverChain.Insert(0, StringEnumJsonContext.Default)
                    );
                }
            )
        );

        Assert.Contains(
            "StringEnumJsonContext is generated with UseStringEnumConverter = true",
            refusal.Message
        );
        Assert.DoesNotContain("ProblemDetails", refusal.Message);
    }

    // The options freeze when first checked, so one verdict per instance can't go stale, and a
    // refusal for one instance never reaches another.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_options_instance_gets_its_own_verdict(bool mvc)
    {
        var result = Define.Get<Payload>("/items").Success(new Payload("item_1"));
        var seen = new List<JsonSerializerOptions>();
        Task Capture(HttpContext context)
        {
            var services = context.RequestServices;
            seen.Add(
                mvc
                    ? services
                        .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
                        .Value.JsonSerializerOptions
                    : services
                        .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                        .Value.SerializerOptions
            );
            return Task.CompletedTask;
        }

        static void Change(JsonSerializerOptions options) => options.PropertyNamingPolicy = null;

        for (var round = 0; round < 2; round++)
        {
            await Assert.ThrowsAsync<RivetContractViolationException>(() =>
                ExecuteAsync(result, mvc, Capture, s => Configure(s, Change))
            );
            Assert.Equal(200, (await ExecuteAsync(result, mvc, Capture)).StatusCode);
        }

        Assert.Equal(4, seen.Distinct().Count());
        Assert.All(seen, options => Assert.True(options.IsReadOnly));
    }

    // Why polymorphic inputs need the setting: the spec's objects are unordered, but the
    // serializer only takes the discriminator as the first property unless told otherwise.
    [Fact]
    public void A_discriminator_after_other_properties_is_read_only_out_of_order()
    {
        const string late = """{"radius":2,"kind":"circle"}""";
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Read as the abstract base, which it can't construct.
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<Shape>(late, web));
#if NET9_0_OR_GREATER
        var outOfOrder = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowOutOfOrderMetadataProperties = true,
        };
        Assert.Equal(new Circle(2), JsonSerializer.Deserialize<Shape>(late, outOfOrder));
#endif
    }

    public static TheoryData<string, bool> PolymorphicInputs()
    {
        var data = new TheoryData<string, bool>();
        foreach (var input in _polymorphicInputs.Keys)
        {
            data.Add(input, false);
            data.Add(input, true);
        }

        return data;
    }

    // BoundFrom is null for a JSON body, which the setting fixes on .NET 9+; a form,
    // multipart or binary contract binds TInput without the serializer, so nothing fixes it.
    private static readonly Dictionary<
        string,
        (Func<RivetResult> Result, string Reaches, string? BoundFrom)
    > _polymorphicInputs = new()
    {
        ["body"] = (
            () => Define.Put("/shapes/1").Accepts<Shape>().Bind(new Circle(1)).Success(),
            $"is the polymorphic type '{typeof(Shape).FullName}'",
            null
        ),
        ["list member"] = (
            () =>
                Define
                    .Post<Drawing, Payload>("/drawings")
                    .Bind(new Drawing("d", [new Circle(1)], new()))
                    .Success(new Payload("d")),
            $"reaches the polymorphic type '{typeof(Shape).FullName}' at shapes[]",
            null
        ),
        ["dictionary value"] = (
            () =>
                Define
                    .Post<Gallery, Payload>("/galleries")
                    .Bind(new Gallery(new()))
                    .Success(new Payload("g")),
            $"reaches the polymorphic type '{typeof(Shape).FullName}' at rooms[].shapes[]",
            null
        ),
        ["form"] = (
            () =>
                Define
                    .Post<Drawing, Payload>("/drawings")
                    .FormEncoded()
                    .Bind(new Drawing("d", [], new()))
                    .Success(new Payload("d")),
            "at shapes[]",
            "a form, not a JSON body"
        ),
        ["multipart"] = (
            () =>
                Define
                    .Put("/drawings/1")
                    .AcceptsFile()
                    .Accepts<Drawing>()
                    .Bind(new Drawing("d", [], new()))
                    .Success(),
            "at shapes[]",
            "a form, not a JSON body"
        ),
        ["binary"] = (
            () =>
                Define
                    .Post<Drawing, Payload>("/drawings")
                    .AcceptsBinary()
                    .Bind(new Drawing("d", [], new()))
                    .Success(new Payload("d")),
            "at shapes[]",
            "the route and query, since the body is raw bytes"
        ),
    };

    [Theory]
    [MemberData(nameof(PolymorphicInputs))]
    public async Task A_polymorphic_input_needs_a_json_body_read_out_of_order(
        string input,
        bool mvc
    )
    {
        var (result, reaches, boundFrom) = _polymorphicInputs[input];

        var refusal = await Assert.ThrowsAsync<RivetContractViolationException>(() =>
            ExecuteAsync(result(), mvc)
        );

        Assert.Contains(reaches, refusal.Message);
        if (boundFrom is not null)
        {
            Assert.Contains($"binds it from {boundFrom}", refusal.Message);
            Assert.Contains("No JSON setting changes that", refusal.Message);
#if NET9_0_OR_GREATER
            await Assert.ThrowsAsync<RivetContractViolationException>(() =>
                ExecuteAsync(
                    result(),
                    mvc,
                    services: s =>
                        Configure(s, options => options.AllowOutOfOrderMetadataProperties = true)
                )
            );
#endif
            return;
        }

        Assert.Contains("'kind' discriminator only as the first property", refusal.Message);
        Assert.Contains(
            "set AllowOutOfOrderMetadataProperties = true in",
            refusal.Message,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains(
            mvc
                ? "Microsoft.AspNetCore.Mvc.JsonOptions"
                : "Microsoft.AspNetCore.Http.Json.JsonOptions",
            refusal.Message
        );
#if NET9_0_OR_GREATER
        var response = await ExecuteAsync(
            result(),
            mvc,
            services: s => Configure(s, options => options.AllowOutOfOrderMetadataProperties = true)
        );
        Assert.True(response.StatusCode is 200 or 201 or 204);
#else
        Assert.Contains(".NET 8 has no setting that changes it", refusal.Message);
#endif
    }

    // A response is written discriminator first, so a polymorphic output is fine, and the walk
    // follows collections, dictionaries and cycles without inventing polymorphism.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plain_inputs_and_polymorphic_outputs_are_served(bool mvc)
    {
        var response = await ExecuteAsync(
            Define
                .Post<Folder, Shape>("/folders")
                .Bind(new Folder("root", [], new()))
                .Success(new Circle(3)),
            mvc
        );

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("""{"kind":"circle","radius":3}""", Encoding.UTF8.GetString(response.Body));
    }

    private static void Configure(IServiceCollection services, Action<JsonSerializerOptions> change)
    {
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            change(options.SerializerOptions)
        );
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
            change(options.JsonSerializerOptions)
        );
    }

    public sealed record Payload(string Id);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Circle), "circle")]
    public abstract record Shape;

    public sealed record Circle(double Radius) : Shape;

    public sealed record Drawing(string Name, List<Shape> Shapes, Dictionary<string, Shape> ByName);

    public sealed record Gallery(Dictionary<string, Drawing> Rooms);

    public sealed record Folder(
        string Name,
        List<Folder> Children,
        Dictionary<string, string[]> Tags
    );

    public enum Shade
    {
        Light,
        Dark,
    }

    public sealed record Painted(string Id, Shade Shade, string? Note);

    private sealed class RenamingResolver : DefaultJsonTypeInfoResolver;
}

[JsonSerializable(typeof(HostJsonOptionsTests.Payload))]
internal sealed partial class PayloadJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<HostJsonOptionsTests.Shade>)]
)]
[JsonSerializable(typeof(HostJsonOptionsTests.Painted))]
internal sealed partial class GeneratedOptionsJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(HostJsonOptionsTests.Painted))]
internal sealed partial class StringEnumJsonContext : JsonSerializerContext;
