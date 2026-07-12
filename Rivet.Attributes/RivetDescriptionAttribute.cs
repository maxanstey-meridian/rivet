namespace Rivet;

[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Enum,
    Inherited = false
)]
public sealed class RivetDescriptionAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}
