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
    {
        ComponentId = componentId;
        Provenance = provenance;
    }

    public string? ComponentId { get; }

    public RivetGeneratedTypeProvenance Provenance { get; }

    /// <summary>
    /// On a polymorphic variant: the JSON of the tag property's schema as the source authored
    /// it, emitted in place of the synthesized single-value <c>enum</c>. It must be valid JSON,
    /// a schema object that admits the variant's tag (an <c>enum</c> or <c>const</c> listing
    /// it, a <c>type</c> including <c>string</c>), on a type some base registers with
    /// <c>[JsonDerivedType]</c>; anything else stops generation (RIV1122).
    /// </summary>
    public string? DiscriminatorSchemaJson { get; set; }
}
