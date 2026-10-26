using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Reads Rivet contract JSON (<c>--from</c>; the shape rivet-ts produces and
/// <c>rivet-contract-schema.json</c> describes) straight into the IR records.
/// </summary>
public static class JsonContractReader
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Producers need not write "kind" first.
        AllowOutOfOrderMetadataProperties = true,
        // A property the IR doesn't know is malformed contract JSON: ignoring it is how a
        // retired shape (the old { isAnonymous, scheme } security) would silently drop what
        // it carried. Required members are marked [JsonRequired], as the schema lists them.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // [JsonRequired] is satisfied by an explicit null, so a non-nullable member written as
        // null ("responses": null) would otherwise reach the IR and crash far from the input.
        RespectNullableAnnotations = true,
        // RespectNullableAnnotations stops at generic arguments, so it lets "types": [null]
        // through; no IR collection holds nulls.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RefuseNullElements } },
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal sealed record RivetContract(
        [property: JsonRequired] IReadOnlyList<TsTypeDefinition> Types,
        [property: JsonRequired] IReadOnlyList<ContractEnum> Enums,
        [property: JsonRequired] IReadOnlyList<TsEndpointDefinition> Endpoints
    );

    /// <summary>An enum declaration: string members in <c>values</c> or integer members in <c>intValues</c>.</summary>
    internal sealed record ContractEnum(
        [property: JsonRequired] string Name,
        IReadOnlyList<string>? Values = null,
        [property: JsonConverter(typeof(IntEnumValuesJsonConverter))]
            IReadOnlyList<string>? IntValues = null,
        string? Format = null,
        string? Description = null,
        TsTypeMetadata? Metadata = null,
        TsScalarMetadata? ScalarMetadata = null,
        string? NamingPolicy = null
    );

    /// <summary>
    /// Refuses a null element in an object's list or dictionary members once the object is
    /// read. The check sits on the owner because a converter on the collection would read its
    /// elements through a nested serializer call, which loses the path of every error inside
    /// them; the owner's path comes with the exception and the message names the element.
    /// </summary>
    private static void RefuseNullElements(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var collections = typeInfo
            .Properties.Where(property =>
                property.Get is not null
                && property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() is var definition
                && (
                    definition == typeof(IReadOnlyList<>)
                    || definition == typeof(IReadOnlyDictionary<,>)
                )
                && !property.PropertyType.GetGenericArguments()[^1].IsValueType
            )
            .ToList();
        if (collections.Count == 0)
        {
            return;
        }

        var previous = typeInfo.OnDeserialized;
        typeInfo.OnDeserialized = owner =>
        {
            previous?.Invoke(owner);
            foreach (var property in collections)
            {
                var element = property.Get!(owner) switch
                {
                    IList list => Enumerable
                        .Range(0, list.Count)
                        .Where(index => list[index] is null)
                        .Select(index => $"[{index}]")
                        .FirstOrDefault(),
                    IDictionary map => map
                        .Keys.Cast<object>()
                        .Where(key => map[key] is null)
                        .Select(key => $".{key}")
                        .FirstOrDefault(),
                    _ => null,
                };
                if (element is not null)
                {
                    throw new JsonException(
                        $"'{property.Name}{element}' is null; contract lists and maps hold no nulls, so remove the entry or write its value."
                    );
                }
            }
        };
    }

    public static (
        IReadOnlyList<TsTypeDefinition> Types,
        Dictionary<string, TsType> Enums,
        IReadOnlyList<TsEndpointDefinition> Endpoints,
        Dictionary<string, TsType.Brand> Brands
    ) Read(string json)
    {
        RivetContract contract;
        try
        {
            contract =
                JsonSerializer.Deserialize<RivetContract>(json, Options)
                ?? throw new JsonException("Failed to deserialize contract JSON.");
        }
        catch (JsonException exception)
            when (exception.Path is not null && !exception.Message.Contains("Path: "))
        {
            // An unknown or missing member's message leaves out where it was; System.Text.Json
            // only appends the location to the messages it words itself.
            throw new JsonException(
                $"{exception.Message} Path: {exception.Path} | LineNumber: {exception.LineNumber} | BytePositionInLine: {exception.BytePositionInLine}.",
                exception.Path,
                exception.LineNumber,
                exception.BytePositionInLine,
                exception
            );
        }
        catch (NotSupportedException exception)
        {
            // A TsType without "kind" can only bind to the abstract base.
            throw new JsonException(exception.Message, exception);
        }
        catch (ArgumentException exception)
        {
            // A record constructor refusing a value (an example with both json and a
            // componentExampleId) is malformed contract JSON, not a crash. System.Text.Json
            // gives no location here, so the constructor's message names the element.
            throw new JsonException(exception.Message, exception);
        }

        // Types and enums share one component namespace; a repeated name would let one
        // declaration silently overwrite the other.
        var repeatedName = contract
            .Types.Select(type => type.Name)
            .Concat(contract.Enums.Select(e => e.Name))
            .GroupBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (repeatedName is not null)
        {
            throw new JsonException(
                $"'{repeatedName.Key}' is declared more than once across types and enums; give each declaration its own name"
            );
        }

        var enums = new Dictionary<string, TsType>();
        foreach (var e in contract.Enums)
        {
            if ((e.Values is null) == (e.IntValues is null))
            {
                throw new JsonException(
                    $"enum '{e.Name}' must carry exactly one of 'values' (string members) or 'intValues' (integer members)"
                );
            }

            enums[e.Name] = e.IntValues is not null
                ? new TsType.IntUnion(
                    e.IntValues,
                    e.Format,
                    e.Metadata,
                    e.Description,
                    e.ScalarMetadata
                )
                : new TsType.StringUnion(
                    e.Values!,
                    e.Metadata,
                    e.Format,
                    e.Description,
                    e.ScalarMetadata,
                    e.NamingPolicy
                );
        }

        var endpoints = contract.Endpoints.Select(NormalizeResponses).ToList();
        foreach (var endpoint in endpoints)
        {
            RejectRepeatedSecuritySchemes(endpoint);
        }

        var nodes = AllTypeNodes(contract.Types, endpoints).ToList();
        foreach (var union in nodes.OfType<TsType.TaggedUnion>())
        {
            RefuseUnplaceableVariantAnnotations(union);
        }

        // The contract JSON has no top-level brands dictionary: brands exist only as inline
        // kind:"brand" nodes, and the OpenAPI emitter $refs every brand by name.
        var brands = CollectBrands(nodes);

        return (contract.Types, enums, endpoints, brands);
    }

    /// <summary>
    /// A security alternative is one OpenAPI Security Requirement Object, keyed by scheme
    /// name, so a scheme can appear in it once; its scopes all go in that one entry.
    /// </summary>
    private static void RejectRepeatedSecuritySchemes(TsEndpointDefinition endpoint)
    {
        var repeated = (endpoint.Security?.Alternatives ?? [])
            .SelectMany(alternative =>
                alternative
                    .Schemes.GroupBy(scheme => scheme.Name, StringComparer.Ordinal)
                    .Where(group => group.Skip(1).Any())
            )
            .FirstOrDefault();
        if (repeated is not null)
        {
            throw new JsonException(
                $"endpoint '{endpoint.ControllerName}.{endpoint.Name}' ({endpoint.HttpMethod.ToUpperInvariant()} {endpoint.RouteTemplate}) "
                    + $"lists security scheme '{repeated.Key}' more than once in one security alternative; "
                    + "list each scheme once per alternative, with all its scopes in that entry"
            );
        }
    }

    private static TsEndpointDefinition NormalizeResponses(TsEndpointDefinition endpoint) =>
        endpoint with
        {
            Responses = ResponseStatusValidation.NormalizeIrAndEnsureResponse(
                endpoint.Responses.Select(response => ResolveStatus(endpoint, response)),
                endpoint.Name,
                endpoint.HttpMethod,
                endpoint.ReturnType
            ),
        };

    /// <summary>
    /// A response is keyed by its statusCode or its statusKey (default, a 1XX-5XX range or a
    /// three-digit code, as <see cref="ResponseStatusValidation.StatusKeyPattern"/> spells
    /// it); with neither it would be emitted under status "0".
    /// </summary>
    private static TsResponseType ResolveStatus(
        TsEndpointDefinition endpoint,
        TsResponseType response
    )
    {
        if (response.StatusKey is { } key && !ResponseStatusValidation.IsStatusKey(key))
        {
            throw new JsonException(
                $"endpoint '{endpoint.ControllerName}.{endpoint.Name}' has a response with statusKey '{key}'; "
                    + "a statusKey is 'default', a range like '4XX' or '4xx', or a three-digit status code"
            );
        }

        if (response.StatusCode != 0)
        {
            return response;
        }

        if (response.StatusKey is null)
        {
            throw new JsonException(
                $"endpoint '{endpoint.ControllerName}.{endpoint.Name}' has a response with neither statusCode nor statusKey; "
                    + "give each response one of them"
            );
        }

        return int.TryParse(response.StatusKey, out var code)
            ? response with
            {
                StatusCode = code,
            }
            : response;
    }

    /// <summary>Every type node the contract holds, nested ones included.</summary>
    private static IEnumerable<TsType> AllTypeNodes(
        IReadOnlyList<TsTypeDefinition> types,
        IReadOnlyList<TsEndpointDefinition> endpoints
    ) =>
        types
            .SelectMany(type =>
                type.Type is null ? type.Properties.Select(prop => prop.Type) : [type.Type]
            )
            .Concat(endpoints.SelectMany(endpoint => endpoint.AllTypes().Select(site => site.Type)))
            .SelectMany(root => root.SelfAndDescendants());

    /// <summary>
    /// A variant's description, scalarMetadata and discriminatorSchema describe the variant's
    /// own component, so they need an inline object carrying the tag property to sit on; and
    /// the tag schema must be one the variant's tag satisfies. Contract JSON that says
    /// otherwise is refused here, where it enters, rather than when it's emitted.
    /// </summary>
    private static void RefuseUnplaceableVariantAnnotations(TsType.TaggedUnion union)
    {
        foreach (var variant in union.Variants)
        {
            var site =
                $"tagged-union variant '{variant.Tag}' (discriminator '{union.Discriminator}')";
            if (
                variant.DiscriminatorSchema is { } tagSchema
                && TsType.TaggedUnionVariant.TagSchemaProblem(tagSchema, variant.Tag) is { } problem
            )
            {
                throw new RivetUserException(
                    $"error {Diagnostics.InvalidContractJson}: {site} has the discriminatorSchema '{tagSchema}', "
                        + $"which {problem}, so it can't be the schema of the tag '{variant.Tag}'. Write it as a "
                        + "JSON schema object that admits the tag, or leave it out for the synthesized enum."
                );
            }

            if (
                variant is { Description: null, ScalarMetadata: null, DiscriminatorSchema: null }
                || variant.Type is TsType.InlineObject inline
                    && inline.Fields.Any(field => field.Name == union.Discriminator)
            )
            {
                continue;
            }

            throw new RivetUserException(
                $"error {Diagnostics.InvalidContractJson}: {site} carries a description, scalarMetadata "
                    + $"or discriminatorSchema, but it isn't an inline object with a '{union.Discriminator}' "
                    + "property to put them on. Give the variant an inline object type with the tag "
                    + "property, or move the annotations to the type it refers to."
            );
        }
    }

    private static Dictionary<string, TsType.Brand> CollectBrands(IEnumerable<TsType> nodes)
    {
        var brands = new Dictionary<string, TsType.Brand>();
        foreach (var brand in nodes.OfType<TsType.Brand>())
        {
            if (brands.TryAdd(brand.Name, brand))
            {
                continue;
            }

            // Compared as wire JSON: TsType records hold lists, so record equality would
            // call two identical declarations different.
            var first = JsonSerializer.Serialize<TsType>(brands[brand.Name], Options);
            var second = JsonSerializer.Serialize<TsType>(brand, Options);
            if (first != second)
            {
                // One brand name is one component schema; picking either declaration
                // would mistype every site that used the other.
                throw new RivetUserException(
                    $"error {Diagnostics.BrandConflictingUnderlyingTypes}: brand '{brand.Name}' is declared two different ways: "
                        + $"{first} and {second}. Declare every use of the brand identically, "
                        + "or give the second shape a brand name of its own"
                );
            }
        }

        return brands;
    }
}
