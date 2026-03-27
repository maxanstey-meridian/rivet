using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

public static class ContractEmitter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new TsTypeJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal sealed record RivetContract(
        IReadOnlyList<TsTypeDefinition> Types,
        IReadOnlyList<ContractEnum> Enums,
        IReadOnlyList<TsEndpointDefinition>? Endpoints = null);

    internal sealed record ContractEnum(string Name, IReadOnlyList<string> Values);

    public static string Emit(
        Dictionary<string, TsTypeDefinition> definitions,
        Dictionary<string, TsType.StringUnion> enums,
        IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        var contract = new RivetContract(
            definitions.Values.ToList(),
            enums.Select(kv => new ContractEnum(kv.Key, kv.Value.Members)).ToList(),
            endpoints);

        return JsonSerializer.Serialize(contract, Options);
    }
}
