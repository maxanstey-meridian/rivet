namespace Rivet;

/// <summary>
/// Preserves the JSON Schema primitive type when it cannot be recovered from
/// the CLR type and format alone, such as an unformatted integer stored as long.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RivetSchemaTypeAttribute(string type) : Attribute
{
    public string Type { get; } = type;
}
