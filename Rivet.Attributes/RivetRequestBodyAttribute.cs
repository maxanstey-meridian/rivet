namespace Rivet;

[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class RivetRequestBodyAttribute(Type bodyType, bool required = true) : Attribute
{
    public Type BodyType { get; } = bodyType;
    public bool Required { get; } = required;
}
