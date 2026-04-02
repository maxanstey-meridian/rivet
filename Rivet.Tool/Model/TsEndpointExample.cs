using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

public sealed record TsEndpointExample
{
    public TsEndpointExample(
        string MediaType,
        string? Name = null,
        string? Json = null,
        string? ComponentExampleId = null,
        string? ResolvedJson = null)
    {
        var hasJson = Json is not null;
        var hasComponentExampleId = ComponentExampleId is not null;

        if (hasJson == hasComponentExampleId)
            throw new ArgumentException("Exactly one of json or componentExampleId must be provided.");

        if (ResolvedJson is not null && !hasComponentExampleId)
            throw new ArgumentException("resolvedJson is only valid for ref-backed examples.");

        this.MediaType = MediaType;
        this.Name = Name;
        this.Json = Json;
        this.ComponentExampleId = ComponentExampleId;
        this.ResolvedJson = ResolvedJson;
    }

    public string MediaType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Json { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComponentExampleId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolvedJson { get; init; }
}
