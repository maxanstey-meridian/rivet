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

    public static (IReadOnlyList<TsTypeDefinition> Types, Dictionary<string, TsType> Enums, IReadOnlyList<TsEndpointDefinition> Endpoints) Read(string json)
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

        var endpoints = contract.Endpoints ?? [];

        return (contract.Types, enums, endpoints);
    }
}
