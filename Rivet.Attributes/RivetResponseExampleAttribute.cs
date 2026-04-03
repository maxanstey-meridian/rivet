namespace Rivet;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
public sealed class RivetResponseExampleAttribute(
    int statusCode,
    string json,
    string? componentExampleId = null,
    string? name = null,
    string? mediaType = null) : Attribute
{
    public int StatusCode { get; } = statusCode;

    public string Json { get; } = json;

    public string? ComponentExampleId { get; } = componentExampleId;

    public string? Name { get; } = name;

    public string? MediaType { get; } = mediaType;
}
