using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

public sealed record TsEndpointExample
{
    public TsEndpointExample(
        string MediaType,
        string? Name = null,
        string? Json = null,
        string? ComponentExampleId = null,
        string? ResolvedJson = null,
        IReadOnlyDictionary<string, string>? ReferencedComponents = null,
        string? Summary = null,
        string? Description = null
    )
    {
        var hasJson = Json is not null;
        var hasComponentExampleId = ComponentExampleId is not null;
        // The contract reader can't say where a refused example sits, so the message does.
        var example = Name is null ? $"the {MediaType} example" : $"{MediaType} example '{Name}'";

        // An Example Object may carry only a summary or description: OpenAPI makes the
        // value optional, and github documents request variants that way.
        if (
            hasJson && hasComponentExampleId
            || !hasJson && !hasComponentExampleId && Summary is null && Description is null
        )
        {
            throw new ArgumentException(
                $"Exactly one of json or componentExampleId must be provided for {example}, unless the example only has a summary or description."
            );
        }

        if (ResolvedJson is not null && !hasComponentExampleId)
        {
            throw new ArgumentException(
                $"resolvedJson is only valid for ref-backed examples, and {example} has no componentExampleId."
            );
        }

        this.MediaType = MediaType;
        this.Name = Name;
        this.Json = Json;
        this.ComponentExampleId = ComponentExampleId;
        this.ResolvedJson = ResolvedJson;
        this.ReferencedComponents = ReferencedComponents;
        this.Summary = Summary;
        this.Description = Description;
    }

    // Required members need a setter System.Text.Json can see, even when the constructor binds them.
    [JsonRequired, JsonInclude]
    public string MediaType { get; private init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Json { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComponentExampleId { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolvedJson { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? ReferencedComponents { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; }
}
