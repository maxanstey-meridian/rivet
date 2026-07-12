namespace Rivet;

/// <summary>Describes whether an imported C# symbol represents an OpenAPI component or a helper.</summary>
public enum RivetGeneratedTypeProvenance
{
    Component,
    Synthetic,
}

/// <summary>
/// Generated-code metadata that keeps the safe C# symbol separate from its exact OpenAPI identity.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum,
    Inherited = false
)]
public sealed class RivetGeneratedTypeAttribute : Attribute
{
    public RivetGeneratedTypeAttribute(string? componentId, RivetGeneratedTypeProvenance provenance)
        : this(componentId, provenance, false) { }

    public RivetGeneratedTypeAttribute(
        string? componentId,
        RivetGeneratedTypeProvenance provenance,
        bool valueObject
    )
    {
        ComponentId = componentId;
        Provenance = provenance;
        ValueObject = valueObject;
    }

    public string? ComponentId { get; }

    public RivetGeneratedTypeProvenance Provenance { get; }

    public bool ValueObject { get; }
}
