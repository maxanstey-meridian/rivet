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

    internal sealed record ContractEnum(
        string Name,
        IReadOnlyList<string>? Values = null,
        IReadOnlyList<int>? IntValues = null);

    public static string Emit(
        Dictionary<string, TsTypeDefinition> definitions,
        Dictionary<string, TsType> enums,
        IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        var contractEnums = enums.Select(kv => kv.Value switch
        {
            TsType.StringUnion su => new ContractEnum(kv.Key, Values: su.Members),
            TsType.IntUnion iu => new ContractEnum(kv.Key, IntValues: iu.Members),
            _ => throw new InvalidOperationException($"Unsupported enum type: {kv.Value.GetType().Name}"),
        }).ToList();

        var contract = new RivetContract(
            definitions.Values.ToList(),
            contractEnums,
            endpoints);

        return JsonSerializer.Serialize(contract, Options);
    }
}
