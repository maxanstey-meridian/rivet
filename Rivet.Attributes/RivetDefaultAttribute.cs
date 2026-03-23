namespace Rivet;

/// <summary>
/// Preserves the original OpenAPI default value through the C# round-trip.
/// Value is stored as a JSON literal string.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RivetDefaultAttribute(string json) : Attribute
{
    public string Json { get; } = json;
}
