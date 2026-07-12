namespace Rivet;

/// <summary>
/// Preserves the original OpenAPI format string through the C# round-trip.
/// A value preserves an explicit format. No value preserves the explicit
/// absence of a format when the CLR type would otherwise infer one.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Enum, Inherited = false)]
public sealed class RivetFormatAttribute : Attribute
{
    public RivetFormatAttribute() { }

    public RivetFormatAttribute(string format)
    {
        Format = format;
    }

    public string? Format { get; }
}
