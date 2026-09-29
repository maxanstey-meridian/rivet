using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Writes the IR as contract JSON (the <c>--from</c> input rivet-ts produces), so tests can
/// round-trip through <see cref="JsonContractReader"/> and validate against the schema.
/// </summary>
internal static class ContractEmitter
{
    private static readonly JsonSerializerOptions _options = new(JsonContractReader.Options)
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitAliasProperties } },
    };

    public static string Emit(
        Dictionary<string, TsTypeDefinition> definitions,
        Dictionary<string, TsType> enums,
        IReadOnlyList<TsEndpointDefinition> endpoints
    )
    {
        var contractEnums = enums
            .Select(kv =>
                kv.Value switch
                {
                    TsType.StringUnion su => new JsonContractReader.ContractEnum(
                        kv.Key,
                        Values: su.Members,
                        Format: su.Format,
                        Description: su.Description,
                        Metadata: su.Metadata,
                        ScalarMetadata: su.ScalarMetadata,
                        NamingPolicy: su.NamingPolicy
                    ),
                    TsType.IntUnion iu => new JsonContractReader.ContractEnum(
                        kv.Key,
                        IntValues: iu.Members,
                        Format: iu.Format,
                        Description: iu.Description,
                        Metadata: iu.Metadata,
                        ScalarMetadata: iu.ScalarMetadata
                    ),
                    _ => throw new InvalidOperationException(
                        $"Unsupported enum type: {kv.Value.GetType().Name}"
                    ),
                }
            )
            .ToList();

        var contract = new JsonContractReader.RivetContract(
            definitions.Values.ToList(),
            contractEnums,
            endpoints
                .Select(endpoint =>
                    endpoint with
                    {
                        Responses = ResponseStatusValidation.NormalizeIrAndEnsureResponse(
                            endpoint.Responses,
                            endpoint.Name,
                            endpoint.HttpMethod,
                            endpoint.ReturnType
                        ),
                    }
                )
                .ToList()
        );

        return JsonSerializer.Serialize(contract, _options);
    }

    /// <summary>The schema allows either <c>properties</c> or <c>type</c> on a type definition.</summary>
    private static void OmitAliasProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(TsTypeDefinition))
        {
            return;
        }

        var properties = typeInfo.Properties.Single(property => property.Name == "properties");
        properties.ShouldSerialize = (definition, _) => ((TsTypeDefinition)definition).Type is null;
    }
}
