namespace Rivet;

public sealed record EndpointExample(
    string? MediaType = null,
    string? Name = null,
    string? Json = null,
    string? ComponentExampleId = null,
    string? ResolvedJson = null)
{
    public static EndpointExample JsonExample(string json, string? name = null, string? mediaType = null) =>
        new(mediaType, name, json);

    public static EndpointExample RefExample(
        string componentExampleId,
        string resolvedJson,
        string? name = null,
        string? mediaType = null) =>
        new(mediaType, name, ComponentExampleId: componentExampleId, ResolvedJson: resolvedJson);
}
