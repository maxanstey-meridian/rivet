using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Deserializes a Rivet contract JSON string into typed definitions and enums.
/// Reuses TsTypeJsonConverter for all TsType variant handling.
/// </summary>
public static class JsonContractReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new TsTypeJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static (IReadOnlyList<TsTypeDefinition> Types, Dictionary<string, TsType> Enums, IReadOnlyList<TsEndpointDefinition> Endpoints, Dictionary<string, TsType.Brand> Brands) Read(string json)
    {
        var contract = JsonSerializer.Deserialize<ContractEmitter.RivetContract>(json, Options)
            ?? throw new JsonException("Failed to deserialize contract JSON.");

        var enums = new Dictionary<string, TsType>();
        foreach (var e in contract.Enums)
        {
            if (e.IntValues is not null)
                enums[e.Name] = new TsType.IntUnion(e.IntValues);
            else
                enums[e.Name] = new TsType.StringUnion(e.Values!);
        }

        var endpoints = contract.Endpoints?.Select(ToEndpointDefinition).ToList() ?? [];
        var types = contract.Types.Select(ToTypeDefinition).ToList();

        // BUG-1: the contract JSON has no top-level brands dictionary — brands exist
        // only as inline kind:"brand" nodes (the TS lowerer emits them that way). The
        // OpenAPI emitter $refs every brand by name, so dropping them here produced
        // dangling $refs. Collect every inline Brand node into the brands registry.
        var brands = CollectBrands(types, endpoints);

        return (types, enums, endpoints, brands);
    }

    private static Dictionary<string, TsType.Brand> CollectBrands(
        IReadOnlyList<TsTypeDefinition> types,
        IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        var brands = new Dictionary<string, TsType.Brand>();

        foreach (var type in types)
        {
            if (type.Type is not null)
            {
                WalkForBrands(type.Type, brands);
            }

            foreach (var prop in type.Properties)
            {
                WalkForBrands(prop.Type, brands);
            }
        }

        foreach (var endpoint in endpoints)
        {
            foreach (var param in endpoint.Params)
            {
                WalkForBrands(param.Type, brands);
            }

            if (endpoint.ReturnType is not null)
            {
                WalkForBrands(endpoint.ReturnType, brands);
            }

            if (endpoint.RequestType is not null)
            {
                WalkForBrands(endpoint.RequestType, brands);
            }

            foreach (var response in endpoint.Responses)
            {
                if (response.DataType is not null)
                {
                    WalkForBrands(response.DataType, brands);
                }
            }
        }

        return brands;
    }

    private static void WalkForBrands(TsType type, Dictionary<string, TsType.Brand> brands)
    {
        switch (type)
        {
            case TsType.Brand b:
                if (brands.TryGetValue(b.Name, out var existing))
                {
                    if (existing != b)
                    {
                        Diagnostics.Warn(
                            Diagnostics.BrandConflictingUnderlyingTypes,
                            $"brand '{b.Name}' declared with conflicting underlying types — first declaration wins");
                    }
                }
                else
                {
                    brands[b.Name] = b;
                }

                WalkForBrands(b.Inner, brands);
                break;
            case TsType.Nullable n:
                WalkForBrands(n.Inner, brands);
                break;
            case TsType.Array a:
                WalkForBrands(a.Element, brands);
                break;
            case TsType.Dictionary d:
                WalkForBrands(d.Value, brands);
                break;
            case TsType.Generic g:
                foreach (var arg in g.TypeArguments)
                {
                    WalkForBrands(arg, brands);
                }

                break;
            case TsType.InlineObject obj:
                foreach (var (_, fieldType) in obj.Fields)
                {
                    WalkForBrands(fieldType, brands);
                }

                break;
            case TsType.TaggedUnion tu:
                foreach (var variant in tu.Variants)
                {
                    WalkForBrands(variant.Type, brands);
                }

                break;
        }
    }

    private static TsEndpointDefinition ToEndpointDefinition(ContractEmitter.ContractEndpoint endpoint)
    {
        return new TsEndpointDefinition(
            endpoint.Name,
            endpoint.HttpMethod,
            endpoint.RouteTemplate,
            endpoint.Params,
            endpoint.ReturnType,
            endpoint.ControllerName,
            endpoint.Responses.Select(ToResponseType).ToList(),
            endpoint.Summary,
            endpoint.Description,
            endpoint.Security,
            endpoint.FileContentType,
            endpoint.InputTypeName,
            endpoint.IsFormEncoded,
            endpoint.RequestType,
            endpoint.RequestExamples?.Select(ToEndpointExample).ToList(),
            // E5/N3: these were serialized by ContractEmitter but silently dropped on read —
            // file endpoints and query-auth must survive the JSON contract round-trip.
            endpoint.IsFileEndpoint,
            endpoint.QueryAuth is { } qa ? new QueryAuthMetadata(qa.ParameterName) : null);
    }

    private static TsResponseType ToResponseType(ContractEmitter.ContractResponseType response)
    {
        return new TsResponseType(
            response.StatusCode,
            response.DataType,
            response.Description,
            response.Examples?.Select(ToEndpointExample).ToList());
    }

    private static TsEndpointExample ToEndpointExample(ContractEmitter.ContractEndpointExample example)
    {
        return new TsEndpointExample(
            example.MediaType,
            example.Name,
            example.Json,
            example.ComponentExampleId,
            example.ResolvedJson);
    }

    private static TsTypeDefinition ToTypeDefinition(ContractEmitter.ContractTypeDefinition definition)
    {
        return definition.Type is not null
            ? new TsTypeDefinition(definition.Name, definition.TypeParameters, definition.Type, definition.Description)
            : new TsTypeDefinition(definition.Name, definition.TypeParameters, definition.Properties ?? [], definition.Description);
    }
}
