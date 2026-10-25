namespace Rivet;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

/// <summary>
/// The spec describes System.Text.Json's web defaults (<see cref="JsonSerializerDefaults.Web"/>)
/// plus what the types themselves declare. Host options that change what the server writes, or
/// make it refuse a request the spec admits, would ship a spec that disagrees with the wire, so
/// the adapters refuse them. Settings that only loosen reading (case-insensitive names, trailing
/// commas, comments, numbers in strings) or only change formatting (indenting, escaping, depth)
/// leave every spec-conforming exchange unchanged and are not compared.
/// </summary>
internal static class RivetHostJsonOptions
{
    internal const string MinimalApi =
        "the minimal-API JSON options (Microsoft.AspNetCore.Http.Json.JsonOptions, set by "
        + "ConfigureHttpJsonOptions or Configure<JsonOptions>)";

    internal const string Mvc =
        "MVC's JSON options (Microsoft.AspNetCore.Mvc.JsonOptions, set by AddJsonOptions or "
        + "Configure<JsonOptions>)";

    // Options become read-only on first use, so a verdict is cached only once it can't change.
    private static readonly ConditionalWeakTable<JsonSerializerOptions, string[]> _verdicts = new();

    internal static void Ensure(JsonSerializerOptions options, string registration)
    {
        if (!_verdicts.TryGetValue(options, out var differences))
        {
            differences = Compare(options);
            if (options.IsReadOnly)
            {
                _verdicts.AddOrUpdate(options, differences);
            }
        }

        if (differences.Length > 0)
        {
            throw new RivetContractViolationException(
                $"Rivet's spec describes System.Text.Json's web defaults, but {registration} change the wire: "
                    + string.Join("; ", differences)
                    + ". Remove these settings and declare the behaviour on the types instead: "
                    + "[JsonConverter], [JsonPropertyName], [JsonIgnore] and [JsonPolymorphic] are read into the spec."
            );
        }
    }

    internal static string[] Compare(JsonSerializerOptions options)
    {
        var differences = new List<string>();
        if (!ReferenceEquals(options.PropertyNamingPolicy, JsonNamingPolicy.CamelCase))
        {
            differences.Add(
                $"PropertyNamingPolicy is {Describe(options.PropertyNamingPolicy)}, not JsonNamingPolicy.CamelCase"
            );
        }

        if (options.DictionaryKeyPolicy is not null)
        {
            differences.Add(
                $"DictionaryKeyPolicy is {Describe(options.DictionaryKeyPolicy)}, so dictionary keys are renamed"
            );
        }

        if (options.Converters.Count > 0)
        {
            differences.Add(
                "Converters registers "
                    + string.Join(
                        ", ",
                        options.Converters.Select(converter => Name(converter.GetType()))
                    )
                    + " for every type, which the spec can't see"
            );
        }

        const JsonNumberHandling writeChanging =
            JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowNamedFloatingPointLiterals;
        if ((options.NumberHandling & writeChanging) != 0)
        {
            differences.Add(
                $"NumberHandling is {options.NumberHandling}, so numbers can be written as strings"
            );
        }

        if (options.DefaultIgnoreCondition != JsonIgnoreCondition.Never)
        {
            differences.Add(
                $"DefaultIgnoreCondition is {options.DefaultIgnoreCondition}, so required members can go missing"
            );
        }

#pragma warning disable SYSLIB0020
        if (options.IgnoreNullValues)
#pragma warning restore SYSLIB0020
        {
            differences.Add("IgnoreNullValues is on, so null members go missing");
        }

        if (options.IgnoreReadOnlyProperties)
        {
            differences.Add("IgnoreReadOnlyProperties is on, so get-only members go missing");
        }

        if (options.IgnoreReadOnlyFields)
        {
            differences.Add("IgnoreReadOnlyFields is on, so readonly fields go missing");
        }

        if (options.IncludeFields)
        {
            differences.Add("IncludeFields is on, so public fields reach the wire");
        }

        if (options.ReferenceHandler is not null)
        {
            differences.Add(
                $"ReferenceHandler is {Name(options.ReferenceHandler.GetType())}, which writes $id/$ref metadata or nulls"
            );
        }

        if (options.UnmappedMemberHandling != JsonUnmappedMemberHandling.Skip)
        {
            differences.Add(
                $"UnmappedMemberHandling is {options.UnmappedMemberHandling}, so a request with a member the spec allows is refused"
            );
        }

#if NET9_0_OR_GREATER
        if (options.RespectRequiredConstructorParameters)
        {
            differences.Add(
                "RespectRequiredConstructorParameters is on, so a request leaving out a [RivetOptional] constructor parameter is refused"
            );
        }
#endif

        // A source-generated context reads the same attributes as reflection and follows these
        // options at runtime (ASP.NET adds its own for ProblemDetails). Anything else, or a
        // resolver with modifiers, can rename, drop or add members.
        var resolvers = options
            .TypeInfoResolverChain.Where(resolver =>
                resolver is not JsonSerializerContext
                && !(
                    resolver.GetType() == typeof(DefaultJsonTypeInfoResolver)
                    && ((DefaultJsonTypeInfoResolver)resolver).Modifiers.Count == 0
                )
            )
            .ToArray();
        if (resolvers.Length > 0)
        {
            differences.Add(
                "TypeInfoResolver uses "
                    + string.Join(
                        ", ",
                        resolvers.Select(resolver =>
                            resolver is DefaultJsonTypeInfoResolver { Modifiers.Count: > 0 }
                                ? $"{Name(resolver.GetType())} with modifiers"
                                : Name(resolver.GetType())
                        )
                    )
                    + ", which can rename, drop or add members"
            );
        }

        return [.. differences];
    }

    private static string Describe(JsonNamingPolicy? policy) =>
        policy switch
        {
            null => "none",
            _ when policy == JsonNamingPolicy.CamelCase => "JsonNamingPolicy.CamelCase",
            _ when policy == JsonNamingPolicy.SnakeCaseLower => "JsonNamingPolicy.SnakeCaseLower",
            _ when policy == JsonNamingPolicy.SnakeCaseUpper => "JsonNamingPolicy.SnakeCaseUpper",
            _ when policy == JsonNamingPolicy.KebabCaseLower => "JsonNamingPolicy.KebabCaseLower",
            _ when policy == JsonNamingPolicy.KebabCaseUpper => "JsonNamingPolicy.KebabCaseUpper",
            _ => Name(policy.GetType()),
        };

    private static string Name(Type type) =>
        type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] : type.Name;
}
