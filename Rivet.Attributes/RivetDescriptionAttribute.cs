namespace Rivet;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = false)]
public sealed class RivetDescriptionAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}
