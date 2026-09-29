using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Deserializes a Rivet contract JSON string into typed definitions and enums.
/// Reuses TsTypeJsonConverter for all TsType variant handling.
/// </summary>
public static class JsonContractReader
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new TsTypeJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    public static (
        IReadOnlyList<TsTypeDefinition> Types,
        Dictionary<string, TsType> Enums,
        IReadOnlyList<TsEndpointDefinition> Endpoints,
        Dictionary<string, TsType.Brand> Brands
    ) Read(string json)
    {
        var contract =
            JsonSerializer.Deserialize<ContractEmitter.RivetContract>(json, _options)
            ?? throw new JsonException("Failed to deserialize contract JSON.");

        var enums = new Dictionary<string, TsType>();
        foreach (var e in contract.Enums)
        {
            if (e.IntValues is not null)
            {
                enums[e.Name] = new TsType.IntUnion(
                    e.IntValues,
                    e.Format,
                    e.Metadata,
                    e.Description,
                    e.ScalarMetadata
                );
            }
            else
            {
                enums[e.Name] = new TsType.StringUnion(
                    e.Values!,
                    e.Metadata,
                    e.Format,
                    e.Description,
                    e.ScalarMetadata,
                    e.NamingPolicy
                );
            }
        }

        var endpoints = contract.Endpoints?.Select(ToEndpointDefinition).ToList() ?? [];
        var types = contract.Types.Select(ToTypeDefinition).ToList();
        RequireTypes(types, endpoints);

        // BUG-1: the contract JSON has no top-level brands dictionary — brands exist
        // only as inline kind:"brand" nodes (the TS lowerer emits them that way). The
        // OpenAPI emitter $refs every brand by name, so dropping them here produced
        // dangling $refs. Collect every inline Brand node into the brands registry.
        var brands = CollectBrands(types, endpoints);

        return (types, enums, endpoints, brands);
    }

    /// <summary>
    /// The schema requires <c>type</c> on params, response headers and properties; the
    /// deserializer leaves a missing one null behind a non-nullable member.
    /// </summary>
    private static void RequireTypes(
        IReadOnlyList<TsTypeDefinition> types,
        IReadOnlyList<TsEndpointDefinition> endpoints
    )
    {
        var missing = types
            .SelectMany(type =>
                type.Properties.Where(prop => prop.Type is null)
                    .Select(prop => $"property '{type.Name}.{prop.Name}'")
            )
            .Concat(
                endpoints.SelectMany(endpoint =>
                    endpoint
                        .Params.Where(param => param.Type is null)
                        .Select(param => $"param '{param.Name}' on endpoint '{endpoint.Name}'")
                        .Concat(
                            endpoint
                                .Responses.SelectMany(response => response.Headers ?? [])
                                .Where(header => header.Type is null)
                                .Select(header =>
                                    $"response header '{header.Name}' on endpoint '{endpoint.Name}'"
                                )
                        )
                )
            )
            .FirstOrDefault();
        if (missing is not null)
        {
            throw new JsonException($"{missing} has no type.");
        }
    }

    private static Dictionary<string, TsType.Brand> CollectBrands(
        IReadOnlyList<TsTypeDefinition> types,
        IReadOnlyList<TsEndpointDefinition> endpoints
    )
    {
        var roots = types
            .SelectMany(type =>
                type.Type is null
                    ? type.Properties.Select(prop => prop.Type)
                    : type.Properties.Select(prop => prop.Type).Prepend(type.Type)
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

    private static TsEndpointDefinition ToEndpointDefinition(
        ContractEmitter.ContractEndpoint endpoint
    )
    {
        var responses = ResponseStatusValidation.NormalizeIrAndEnsureResponse(
            endpoint.Responses.Select(ToResponseType),
            endpoint.Name,
            endpoint.HttpMethod,
            endpoint.ReturnType
        );

        return new TsEndpointDefinition(
            endpoint.Name,
            endpoint.HttpMethod,
            endpoint.RouteTemplate,
            endpoint.Params,
            endpoint.ReturnType,
            endpoint.ControllerName,
            responses,
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
            endpoint.QueryAuth is { } qa ? new QueryAuthMetadata(qa.ParameterName) : null,
            // Raw-binary request bodies (rivet-ts pipeline) must survive the
            // contract-JSON round-trip like IsFileEndpoint/QueryAuth above.
            endpoint.BinaryRequestContentType,
            endpoint.RequestContentTypeOverride,
            endpoint.ResponseContentTypeOverride,
            SecurityRequirements: endpoint.SecurityRequirements,
            RequestContents: endpoint.RequestContents,
            RequestBodyRequired: endpoint.RequestBodyRequired,
            RequestBodyPresent: endpoint.RequestBodyPresent,
            Provenance: endpoint.Provenance
        );
    }

    private static TsResponseType ToResponseType(ContractEmitter.ContractResponseType response)
    {
        return new TsResponseType(
            response.StatusCode ?? ParseStatusCode(response.StatusKey),
            response.DataType,
            response.Description,
            response.Examples?.Select(ToEndpointExample).ToList(),
            // P2 wave 5: headers are optional in contract JSON — absence (old contracts,
            // TS lowerer output) deserializes to null and is tolerated everywhere.
            response.Headers,
            response.Contents,
            response.StatusKey
        );
    }

    private static int ParseStatusCode(string? statusKey) =>
        int.TryParse(statusKey, out var statusCode) ? statusCode : 0;

    private static TsEndpointExample ToEndpointExample(
        ContractEmitter.ContractEndpointExample example
    )
    {
        return new TsEndpointExample(
            example.MediaType,
            example.Name,
            example.Json,
            example.ComponentExampleId,
            example.ResolvedJson,
            example.ReferencedComponents
        );
    }

    private static TsTypeDefinition ToTypeDefinition(
        ContractEmitter.ContractTypeDefinition definition
    )
    {
        return definition.Type is not null
            ? new TsTypeDefinition(
                definition.Name,
                definition.TypeParameters,
                definition.Type,
                definition.Description,
                definition.Metadata,
                definition.ScalarMetadata
            )
            : new TsTypeDefinition(
                definition.Name,
                definition.TypeParameters,
                definition.Properties ?? [],
                definition.Description,
                definition.Metadata,
                definition.ScalarMetadata
            );
    }
}
