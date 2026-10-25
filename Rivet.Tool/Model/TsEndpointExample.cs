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

        // An Example Object may carry only a summary or description: OpenAPI makes the
        // value optional, and github documents request variants that way.
        if (
            hasJson && hasComponentExampleId
            || !hasJson && !hasComponentExampleId && Summary is null && Description is null
        )
        {
            throw new ArgumentException(
                "Exactly one of json or componentExampleId must be provided, unless the example only has a summary or description."
            );
        }

        if (ResolvedJson is not null && !hasComponentExampleId)
        {
            throw new ArgumentException("resolvedJson is only valid for ref-backed examples.");
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

    public string MediaType { get; }

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
