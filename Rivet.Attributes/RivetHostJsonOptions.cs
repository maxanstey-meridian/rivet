namespace Rivet;

using System.Collections.Concurrent;
using System.Reflection;
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
/// leave every spec-conforming exchange unchanged and are not compared. One reading setting is
/// required rather than refused: a contract whose input is polymorphic needs the discriminator
/// read wherever it is (<see cref="EnsureInput"/>).
/// </summary>
internal static class RivetHostJsonOptions
{
    internal const string MinimalApi =
        "the minimal-API JSON options (Microsoft.AspNetCore.Http.Json.JsonOptions, set by "
        + "ConfigureHttpJsonOptions or Configure<JsonOptions>)";

    internal const string Mvc =
        "MVC's JSON options (Microsoft.AspNetCore.Mvc.JsonOptions, set by AddJsonOptions or "
        + "Configure<JsonOptions>)";

    private static readonly ConditionalWeakTable<JsonSerializerOptions, string[]> _verdicts = new();

    private static readonly ConditionalWeakTable<
        JsonSerializerOptions,
        ConcurrentDictionary<Type, PolymorphicInput?>
    > _inputVerdicts = new();

    /// <summary>
    /// The host resolves these options from <c>IOptions</c> once every <c>Configure</c> has run,
    /// so they're final by the time a result is written. Freezing them here, before comparing,
    /// means the verdict can't go stale, so each instance is compared once.
    /// </summary>
    internal static void Ensure(JsonSerializerOptions options, string registration)
    {
        var differences = _verdicts.GetValue(
            options,
            static options =>
            {
                options.MakeReadOnly(populateMissingResolver: true);
                return Compare(options);
            }
        );

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

    /// <summary>
    /// System.Text.Json recognises a polymorphic type's discriminator only as the first
    /// property of its object. The spec can't say that (JSON objects are unordered), so a
    /// client that follows it is refused whenever the tag comes later. .NET 9's
    /// <c>AllowOutOfOrderMetadataProperties</c> reads it wherever it is; .NET 8 has no such
    /// setting. A form, multipart or route-and-query input never reaches the serializer:
    /// ASP.NET's model binding has no discriminator, so no setting helps there either.
    /// Responses are unaffected: the serializer writes the discriminator first.
    /// Runs after <see cref="Ensure"/>, which froze the options. An object's members, and
    /// whether a type is polymorphic, come from the resolver's type info, so the graph is the
    /// one the host reads; see <see cref="ItemTypes"/> for collections. One verdict per
    /// options and type.
    /// </summary>
    internal static void EnsureInput(
        JsonSerializerOptions options,
        string registration,
        EndpointContract contract
    )
    {
        if (contract.InputType is not { } inputType)
        {
            return;
        }

#if NET9_0_OR_GREATER
        if (contract.InputBinding == InputBinding.Json && options.AllowOutOfOrderMetadataProperties)
        {
            return;
        }
#endif

        var found = _inputVerdicts
            .GetValue(options, static _ => new())
            .GetOrAdd(inputType, FindPolymorphicInput, options);
        if (found is null)
        {
            return;
        }

        var takes =
            $"Route '{contract.Method} {contract.Route}' takes "
            + found.Path switch
            {
                "" => $"the polymorphic input '{Name(inputType)}'. ",
                "[]" =>
                    $"input '{Name(inputType)}', which reaches the polymorphic type '{Name(found.Type)}' in its items. ",
                var path =>
                    $"input '{Name(inputType)}', which reaches the polymorphic type '{Name(found.Type)}' at '{path}'. ",
            };
        if (contract.InputBinding != InputBinding.Json)
        {
            var source =
                contract.InputBinding == InputBinding.Form
                    ? "a form, not a JSON body"
                    : "the route and query, not a JSON body";
            throw new RivetContractViolationException(
                takes
                    + $"ASP.NET binds it from {source}, and model binding has no '{found.Discriminator}' discriminator: "
                    + "it builds the declared type and never a variant, so a request that sends a variant arrives as "
                    + "the declared type, or not at all. "
                    + "No JSON setting changes that; take an input that isn't polymorphic, or a JSON body."
            );
        }

        var prefix =
            takes
            + $"System.Text.Json recognises its '{found.Discriminator}' discriminator only as the first property of the object";
#if NET9_0_OR_GREATER
        throw new RivetContractViolationException(
            prefix
                + $" unless AllowOutOfOrderMetadataProperties is on, so a request the spec admits, with '{found.Discriminator}' later, would be refused. "
                + $"Set AllowOutOfOrderMetadataProperties = true in {registration}."
        );
#else
        throw new RivetContractViolationException(
            prefix
                + $", and .NET 8 has no setting that changes it, so a request the spec admits, with '{found.Discriminator}' later, would be refused. "
                + $"Run the host on .NET 9 or later and set AllowOutOfOrderMetadataProperties = true in {registration}, "
                + "or take an input that isn't polymorphic."
        );
#endif
    }

    private sealed record PolymorphicInput(Type Type, string Discriminator, string Path);

    private static PolymorphicInput? FindPolymorphicInput(Type root, JsonSerializerOptions options)
    {
        var seen = new HashSet<Type>();
        var pending = new Queue<(Type Type, string Path)>();
        pending.Enqueue((root, ""));
        while (pending.TryDequeue(out var next))
        {
            // The serializer reads a Nullable<T> as the T inside it.
            var type = Nullable.GetUnderlyingType(next.Type) ?? next.Type;
            if (!seen.Add(type))
            {
                continue;
            }

            var info = options.GetTypeInfo(type);
            if (info.PolymorphismOptions is { } polymorphism)
            {
                return new(type, polymorphism.TypeDiscriminatorPropertyName, next.Path);
            }

            switch (info.Kind)
            {
                case JsonTypeInfoKind.Object:
                    foreach (var property in info.Properties)
                    {
                        pending.Enqueue(
                            (
                                property.PropertyType,
                                next.Path.Length == 0
                                    ? property.Name
                                    : $"{next.Path}.{property.Name}"
                            )
                        );
                    }

                    break;
                case JsonTypeInfoKind.Enumerable:
                case JsonTypeInfoKind.Dictionary:
                    foreach (var item in ItemTypes(info))
                    {
                        pending.Enqueue((item, $"{next.Path}[]"));
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// A collection's element type, or a dictionary's key and value types. .NET 9+ reads them
    /// off the type info; .NET 8 doesn't expose them there, so it reflects on the
    /// <c>IEnumerable&lt;T&gt;</c> the type implements.
    /// </summary>
    internal static Type[] ItemTypes(JsonTypeInfo info)
    {
#if NET9_0_OR_GREATER
        return [.. new[] { info.KeyType, info.ElementType }.OfType<Type>()];
#else
        var type = info.Type;
        static bool IsEnumerable(Type candidate) =>
            candidate.IsGenericType
            && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>);

        var enumerable = IsEnumerable(type)
            ? type
            : type.GetInterfaces().FirstOrDefault(IsEnumerable);
        if (enumerable is null)
        {
            return [];
        }

        var item = enumerable.GetGenericArguments()[0];
        return item.IsGenericType && item.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
            ? item.GetGenericArguments()
            : [item];
#endif
    }

    private static string[] Compare(JsonSerializerOptions options)
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
        // options at runtime (ASP.NET adds its own for ProblemDetails), naming, ignore and
        // converter settings on its [JsonSourceGenerationOptions] included. Anything else, or a
        // resolver with modifiers, can rename, drop or add members.
        var resolvers = options
            .TypeInfoResolverChain.Select(resolver =>
                resolver switch
                {
                    JsonSerializerContext => null,
                    DefaultJsonTypeInfoResolver { Modifiers.Count: 0 }
                        when resolver.GetType() == typeof(DefaultJsonTypeInfoResolver) => null,
                    DefaultJsonTypeInfoResolver { Modifiers.Count: > 0 } =>
                        $"{Name(resolver.GetType())} with modifiers",
                    _ => Name(resolver.GetType()),
                }
            )
            .OfType<string>()
            .ToArray();
        if (resolvers.Length > 0)
        {
            differences.Add(
                "TypeInfoResolver uses "
                    + string.Join(", ", resolvers)
                    + ", which can rename, drop or add members"
            );
        }

        // UseStringEnumConverter is the exception: the generator bakes the converter into each
        // enum's type info, and the context's runtime options don't carry it.
        foreach (var context in options.TypeInfoResolverChain.OfType<JsonSerializerContext>())
        {
            if (
                context
                    .GetType()
                    .GetCustomAttribute<JsonSourceGenerationOptionsAttribute>()
                    ?.UseStringEnumConverter == true
            )
            {
                differences.Add(
                    $"{Name(context.GetType())} is generated with UseStringEnumConverter = true, "
                        + "so it writes every enum it covers by name"
                );
            }
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

    /// <summary>A type as C# spells it, without namespaces: <c>Dictionary&lt;string, Shape?&gt;</c>.</summary>
    internal static string Name(Type type)
    {
        if (type.IsArray)
        {
            return $"{Name(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return $"{Name(underlying)}?";
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        // A type nested in a generic one is generic without declaring parameters of its own.
        var tick = type.Name.IndexOf('`');
        return $"{(tick < 0 ? type.Name : type.Name[..tick])}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }
}
