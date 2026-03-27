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

    internal sealed record RivetContract(
        IReadOnlyList<TsTypeDefinition> Types,
        IReadOnlyList<ContractEnum> Enums);

    internal sealed record ContractEnum(string Name, IReadOnlyList<string> Values);

    public static (IReadOnlyList<TsTypeDefinition> Types, Dictionary<string, TsType.StringUnion> Enums) Read(string json)
    {
        var contract = JsonSerializer.Deserialize<RivetContract>(json, Options)
            ?? throw new JsonException("Failed to deserialize contract JSON.");

        var enums = new Dictionary<string, TsType.StringUnion>();
        foreach (var e in contract.Enums)
        {
            enums[e.Name] = new TsType.StringUnion(e.Values);
        }

        return (contract.Types, enums);
    }
}
