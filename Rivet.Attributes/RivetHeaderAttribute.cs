namespace Rivet;

/// <summary>
/// Marks an input-record property as a request HEADER parameter (contract concept).
/// The header name keeps its original casing ("Notion-Version") while the record
/// property stays PascalCase; when no name is given the property name is the header name.
/// Spec-only at runtime: Rivet never binds or validates headers — header binding
/// remains the host's job (e.g. ASP.NET [FromHeader] on the controller side).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RivetHeaderAttribute : Attribute
{
    public RivetHeaderAttribute()
    {
    }

    public RivetHeaderAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The wire header name with its original casing; null = use the property name.</summary>
    public string? Name { get; }
}
