namespace Rivet;

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

/// <summary>
/// System.Text.Json writes a <c>[JsonExtensionData]</c> bag's entries as members of the
/// object that holds it, after the declared ones and without looking at them, so a key equal
/// to a declared member's wire name (or to the polymorphic discriminator the object is
/// written with) puts that name in the JSON twice. The spec can't describe such an object,
/// and readers disagree about which value wins, so the adapter refuses the response.
/// The payload's graph is read from the host's type info, as the serializer will write it;
/// a graph that reaches no bag (decided once per options and type) is never walked, and a
/// graph nested deeper than the serializer writes (a cycle, say) is left for it to refuse.
/// </summary>
internal static class RivetExtensionDataKeys
{
    private sealed record ObjectShape(
        HashSet<string> Names,
        JsonPropertyInfo[] Members,
        JsonPropertyInfo? Bag
    );

    // Visit's answer for a graph nested past the serializer's MaxDepth: every caller hands a
    // non-null answer straight back, so the whole walk stops at the first such path.
    private static readonly string _tooDeep = new("too deep");

    private static readonly ConditionalWeakTable<
        JsonSerializerOptions,
        ConcurrentDictionary<Type, bool>
    > _reachesBag = new();

    private static readonly ConditionalWeakTable<
        JsonSerializerOptions,
        ConcurrentDictionary<Type, ObjectShape>
    > _shapes = new();

    /// <summary>
    /// Where <paramref name="value"/>, written as <paramref name="declaredType"/>, has a bag key
    /// that repeats a member's name, described for a violation message; null when none does.
    /// </summary>
    internal static string? FindRepeatedKey(
        object value,
        Type declaredType,
        JsonSerializerOptions options
    )
    {
        var path = new List<string>();
        var maxDepth = options.MaxDepth == 0 ? 64 : options.MaxDepth;
        var found = Visit(value, declaredType, null, options, path, maxDepth);
        return ReferenceEquals(found, _tooDeep) ? null : found;
    }

    /// <summary>
    /// <paramref name="depthLeft"/> is how many more objects and arrays the serializer opens,
    /// nested, before it refuses the payload. Past that it throws on its own, so the walk
    /// stops there (<see cref="_tooDeep"/>) rather than backing off and trying the next
    /// sibling, which on a cyclic graph means trying every path down to the cap.
    /// </summary>
    private static string? Visit(
        object? value,
        Type declaredType,
        string? discriminator,
        JsonSerializerOptions options,
        List<string> path,
        int depthLeft
    )
    {
        if (value is null)
        {
            return null;
        }

        var info = WrittenTypeInfo(value, declaredType, options, ref discriminator);
        if (!ReachesBag(info.Type, options))
        {
            return null;
        }

        if (
            depthLeft == 0
            && info.Kind
                is JsonTypeInfoKind.Object
                    or JsonTypeInfoKind.Enumerable
                    or JsonTypeInfoKind.Dictionary
        )
        {
            return _tooDeep;
        }

        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
                var shape = Shape(info, options);
                foreach (var member in shape.Members)
                {
                    path.Add($".{member.Name}");
                    var found = Visit(
                        member.Get!(value),
                        member.PropertyType,
                        null,
                        options,
                        path,
                        depthLeft - 1
                    );
                    path.RemoveAt(path.Count - 1);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                return shape.Bag is { Get: { } read } bag
                    ? CheckBag(
                        info.Type,
                        bag,
                        read(value),
                        shape,
                        discriminator,
                        options,
                        path,
                        depthLeft
                    )
                    : null;
            case JsonTypeInfoKind.Enumerable:
            case JsonTypeInfoKind.Dictionary:
                var itemType = RivetHostJsonOptions.ItemTypes(info).Last();
                foreach (var (segment, item) in Items(value, info.Kind))
                {
                    path.Add(segment);
                    var found = Visit(item, itemType, null, options, path, depthLeft - 1);
                    path.RemoveAt(path.Count - 1);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                return null;
            // A [RivetUnion] wrapper is written as its variant, in its place.
            case JsonTypeInfoKind.None when UnionVariant(info, value) is { } variant:
                return Visit(variant.Value, variant.DeclaredType, null, options, path, depthLeft);
            default:
                return null;
        }
    }

    private static string? CheckBag(
        Type owner,
        JsonPropertyInfo bag,
        object? contents,
        ObjectShape shape,
        string? discriminator,
        JsonSerializerOptions options,
        List<string> path,
        int depthLeft
    )
    {
        foreach (var (key, entry) in Entries(contents))
        {
            if (shape.Names.Contains(key) || key == discriminator)
            {
                var at = path.Count == 0 ? "" : $" at '{string.Concat(path).TrimStart('.')}'";
                var what = key == discriminator ? "discriminator" : "member";
                var member = (bag.AttributeProvider as MemberInfo)?.Name ?? bag.Name;
                return $"returns a '{RivetHostJsonOptions.Name(owner)}'{at} whose [JsonExtensionData] "
                    + $"'{member}' holds the key '{key}', which is also its declared {what} '{key}', so "
                    + $"System.Text.Json would write '{key}' twice and the spec can't describe the object. "
                    + "Keep the bag's keys apart from the type's members";
            }

            // An object-valued bag entry is written as whatever it holds, as a member of the
            // object that holds the bag.
            if (entry is not null and not JsonElement and not JsonNode)
            {
                path.Add($".{key}");
                var found = Visit(entry, typeof(object), null, options, path, depthLeft - 1);
                path.RemoveAt(path.Count - 1);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The type info the serializer writes <paramref name="value"/> with where
    /// <paramref name="declaredType"/> is declared: the runtime type for <c>object</c>, and for
    /// a polymorphic type the registered type the value is (or derives from), whose
    /// discriminator it then carries.
    /// </summary>
    private static JsonTypeInfo WrittenTypeInfo(
        object value,
        Type declaredType,
        JsonSerializerOptions options,
        ref string? discriminator
    )
    {
        var declared = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        var info = options.GetTypeInfo(declared == typeof(object) ? value.GetType() : declared);
        if (info.PolymorphismOptions is not { } polymorphism)
        {
            return info;
        }

        for (var type = value.GetType(); type is not null; type = type.BaseType)
        {
            if (polymorphism.DerivedTypes.Any(derived => derived.DerivedType == type))
            {
                discriminator = polymorphism.TypeDiscriminatorPropertyName;
                return options.GetTypeInfo(type);
            }

            if (type == declared)
            {
                break;
            }
        }

        return info;
    }

    private static ObjectShape Shape(JsonTypeInfo info, JsonSerializerOptions options) =>
        _shapes
            .GetValue(options, static _ => new())
            .GetOrAdd(
                info.Type,
                static (_, info) =>
                {
                    var bag = info.Properties.FirstOrDefault(property => property.IsExtensionData);
                    var members = info
                        .Properties.Where(property => !property.IsExtensionData)
                        .ToArray();
                    return new(
                        [.. members.Select(property => property.Name)],
                        [.. members.Where(property => property.Get is not null)],
                        bag
                    );
                },
                info
            );

    /// <summary>
    /// Whether a value written as <paramref name="root"/> can hold a bag anywhere: one of the
    /// objects it reaches has one, or it reaches <c>object</c>, which can hold anything.
    /// </summary>
    private static bool ReachesBag(Type root, JsonSerializerOptions options) =>
        _reachesBag
            .GetValue(options, static _ => new())
            .GetOrAdd(
                root,
                static (root, options) =>
                {
                    var seen = new HashSet<Type>();
                    var pending = new Stack<Type>();
                    pending.Push(root);
                    while (pending.TryPop(out var next))
                    {
                        var type = Nullable.GetUnderlyingType(next) ?? next;
                        if (type == typeof(object))
                        {
                            return true;
                        }

                        if (!seen.Add(type))
                        {
                            continue;
                        }

                        var info = options.GetTypeInfo(type);
                        foreach (var derived in info.PolymorphismOptions?.DerivedTypes ?? [])
                        {
                            pending.Push(derived.DerivedType);
                        }

                        if (IsUnion(info))
                        {
                            foreach (var variant in RivetUnionVariants.Of(type))
                            {
                                pending.Push(variant.PropertyType);
                            }
                        }

                        switch (info.Kind)
                        {
                            case JsonTypeInfoKind.Object:
                                foreach (var property in info.Properties)
                                {
                                    if (property.IsExtensionData)
                                    {
                                        return true;
                                    }

                                    pending.Push(property.PropertyType);
                                }

                                break;
                            case JsonTypeInfoKind.Enumerable:
                            case JsonTypeInfoKind.Dictionary:
                                foreach (var item in RivetHostJsonOptions.ItemTypes(info))
                                {
                                    pending.Push(item);
                                }

                                break;
                        }
                    }

                    return false;
                },
                options
            );

    /// <summary>
    /// Whether <paramref name="info"/> is written by <see cref="RivetUnionJsonConverter{T}"/>:
    /// the converter the serializer picked, not the attribute, so a host converter that
    /// replaces it is taken at its word like any other.
    /// </summary>
    private static bool IsUnion(JsonTypeInfo info) =>
        info.Converter.GetType() is { IsGenericType: true } converter
        && converter.GetGenericTypeDefinition() == typeof(RivetUnionJsonConverter<>);

    /// <summary>
    /// The variant a [RivetUnion] wrapper is written as, and the type it's written as: its
    /// first non-null variant property, as <see cref="RivetUnionJsonConverter{T}"/> writes it.
    /// Null for any other converter, or a wrapper with no variant set (written as null).
    /// </summary>
    private static (object Value, Type DeclaredType)? UnionVariant(
        JsonTypeInfo info,
        object value
    ) => IsUnion(info) ? RivetUnionVariants.Written(RivetUnionVariants.Of(info.Type), value) : null;

    /// <summary>
    /// A sequence's items or a dictionary's values, each with where it sits on the wire: a
    /// list item at its index, a dictionary value under its key.
    /// </summary>
    private static IEnumerable<(string Segment, object? Value)> Items(
        object value,
        JsonTypeInfoKind kind
    )
    {
        var index = 0;
        foreach (var item in (IEnumerable)value)
        {
            if (kind != JsonTypeInfoKind.Dictionary)
            {
                yield return ($"[{index++}]", item);
                continue;
            }

            // A dictionary enumerates KeyValuePair<TKey, TValue>, or DictionaryEntry when it
            // isn't generic.
            var (key, entry) = item is DictionaryEntry pair
                ? (pair.Key, pair.Value)
                : (Read(item!, "Key"), Read(item!, "Value"));
            yield return ($".{Convert.ToString(key, CultureInfo.InvariantCulture)}", entry);
        }
    }

    private static IEnumerable<(string Key, object? Value)> Entries(object? bag) =>
        bag switch
        {
            null => [],
            JsonObject node => node.Select(entry => (entry.Key, (object?)entry.Value)),
            IEnumerable<KeyValuePair<string, JsonElement>> elements => elements.Select(entry =>
                (entry.Key, (object?)entry.Value)
            ),
            IEnumerable<KeyValuePair<string, object?>> objects => objects.Select(entry =>
                (entry.Key, entry.Value)
            ),
            _ => throw new InvalidOperationException(
                $"Extension data of type '{bag.GetType()}' is neither a JsonObject nor a string-keyed dictionary."
            ),
        };

    private static object? Read(object pair, string property) =>
        pair.GetType().GetProperty(property)!.GetValue(pair);
}
