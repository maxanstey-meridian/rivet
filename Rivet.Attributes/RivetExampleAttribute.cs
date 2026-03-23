namespace Rivet;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RivetExampleAttribute(string json) : Attribute
{
    public string Json { get; } = json;
}
