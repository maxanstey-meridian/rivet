using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
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
    // an AOT app puts its own there; both follow the host options at runtime.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_generated_contexts_and_problem_details_are_allowed(bool mvc)
    {
        var response = await ExecuteAsync(
            Define.Get<Payload>("/items").Success(new Payload("item_1")),
            mvc,
            services: services =>
            {
                services.AddProblemDetails();
                Configure(
                    services,
                    options => options.TypeInfoResolverChain.Insert(0, PayloadJsonContext.Default)
                );
            }
        );

        Assert.Equal(
            "item_1",
            JsonDocument.Parse(response.Body).RootElement.GetProperty("id").GetString()
        );
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

    private sealed class RenamingResolver : DefaultJsonTypeInfoResolver;
}

[JsonSerializable(typeof(HostJsonOptionsTests.Payload))]
internal sealed partial class PayloadJsonContext : JsonSerializerContext;
