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
        IReadOnlyList<ContractEndpoint>? Endpoints = null);

    internal sealed record ContractEnum(
        string Name,
        IReadOnlyList<string>? Values = null,
        IReadOnlyList<int>? IntValues = null);

    internal sealed record ContractQueryAuth(string ParameterName);

    internal sealed record ContractEndpoint(
        string Name,
        string HttpMethod,
        string RouteTemplate,
        IReadOnlyList<TsEndpointParam> Params,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsType? ReturnType,
        string ControllerName,
        IReadOnlyList<ContractResponseType> Responses,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EndpointSecurity? Security = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FileContentType = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputTypeName = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsFormEncoded = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsType? RequestType = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ContractEndpointExample>? RequestExamples = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsFileEndpoint = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentType = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ContractQueryAuth? QueryAuth = null);

    internal sealed record ContractResponseType(
        int StatusCode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsType? DataType,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ContractEndpointExample>? Examples = null);

    internal sealed record ContractEndpointExample(
        string MediaType,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Json = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComponentExampleId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResolvedJson = null);

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
            endpoints.Select(ToContractEndpoint).ToList());

        return JsonSerializer.Serialize(contract, Options);
    }

    internal static ContractEndpoint ToContractEndpoint(TsEndpointDefinition endpoint)
    {
        return new ContractEndpoint(
            endpoint.Name,
            endpoint.HttpMethod,
            endpoint.RouteTemplate,
            endpoint.Params,
            endpoint.ReturnType,
            endpoint.ControllerName,
            endpoint.Responses.Select(ToContractResponseType).ToList(),
            endpoint.Summary,
            endpoint.Description,
            endpoint.Security,
            endpoint.FileContentType,
            endpoint.InputTypeName,
            endpoint.IsFormEncoded,
            endpoint.RequestType,
            endpoint.RequestExamples?.Select(ToContractEndpointExample).ToList(),
            endpoint.IsFileEndpoint,
            endpoint.FileContentType,
            endpoint.QueryAuth is { } qa ? new ContractQueryAuth(qa.ParameterName) : null);
    }

    internal static ContractResponseType ToContractResponseType(TsResponseType response)
    {
        return new ContractResponseType(
            response.StatusCode,
            response.DataType,
            response.Description,
            response.Examples?.Select(ToContractEndpointExample).ToList());
    }

    internal static ContractEndpointExample ToContractEndpointExample(TsEndpointExample example)
    {
        return new ContractEndpointExample(
            example.MediaType,
            example.Name,
            example.Json,
            example.ComponentExampleId,
            example.ResolvedJson);
    }
}
