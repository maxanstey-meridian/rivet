namespace Rivet;

/// <summary>
/// Preserves the original OpenAPI format string through the C# round-trip.
/// Used for custom formats (uri-template, currency, phone-number, etc.)
/// that have no dedicated C# type.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RivetFormatAttribute(string format) : Attribute
{
    public string Format { get; } = format;
}
