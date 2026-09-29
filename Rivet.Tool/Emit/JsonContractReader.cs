using System.Text.Json;
using System.Text.Json.Serialization;
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
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal sealed record RivetContract(
        IReadOnlyList<TsTypeDefinition> Types,
        IReadOnlyList<ContractEnum> Enums,
        IReadOnlyList<TsEndpointDefinition>? Endpoints = null
    );

    /// <summary>An enum declaration: string members in <c>values</c> or integer members in <c>intValues</c>.</summary>
    internal sealed record ContractEnum(
        string Name,
        IReadOnlyList<string>? Values = null,
        [property: JsonConverter(typeof(IntEnumValuesJsonConverter))]
            IReadOnlyList<string>? IntValues = null,
        string? Format = null,
        string? Description = null,
        TsTypeMetadata? Metadata = null,
        TsScalarMetadata? ScalarMetadata = null,
        string? NamingPolicy = null
    );

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
        catch (NotSupportedException exception)
        {
            // A TsType without "kind" can only bind to the abstract base.
            throw new JsonException(exception.Message, exception);
        }

        var enums = new Dictionary<string, TsType>();
        foreach (var e in contract.Enums)
        {
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

        var endpoints = (contract.Endpoints ?? []).Select(NormalizeResponses).ToList();

        // The contract JSON has no top-level brands dictionary: brands exist only as inline
        // kind:"brand" nodes, and the OpenAPI emitter $refs every brand by name.
        var brands = CollectBrands(contract.Types, endpoints);

        return (contract.Types, enums, endpoints, brands);
    }

    private static TsEndpointDefinition NormalizeResponses(TsEndpointDefinition endpoint) =>
        endpoint with
        {
            Responses = ResponseStatusValidation.NormalizeIrAndEnsureResponse(
                endpoint.Responses.Select(response =>
                    response.StatusCode == 0 && int.TryParse(response.StatusKey, out var code)
                        ? response with
                        {
                            StatusCode = code,
                        }
                        : response
                ),
                endpoint.Name,
                endpoint.HttpMethod,
                endpoint.ReturnType
            ),
        };

    private static Dictionary<string, TsType.Brand> CollectBrands(
        IReadOnlyList<TsTypeDefinition> types,
        IReadOnlyList<TsEndpointDefinition> endpoints
    )
    {
        var roots = types
            .SelectMany(type =>
                type.Type is null ? type.Properties.Select(prop => prop.Type) : [type.Type]
            )
            .Concat(
                endpoints.SelectMany(endpoint => endpoint.AllTypes().Select(site => site.Type))
            );

        var brands = new Dictionary<string, TsType.Brand>();
        foreach (
            var brand in roots.SelectMany(root => root.SelfAndDescendants()).OfType<TsType.Brand>()
        )
        {
            if (!brands.TryAdd(brand.Name, brand) && brands[brand.Name] != brand)
            {
                Diagnostics.Warn(
                    Diagnostics.BrandConflictingUnderlyingTypes,
                    $"brand '{brand.Name}' declared with conflicting underlying types — first declaration wins"
                );
            }
        }

        return brands;
    }
}
