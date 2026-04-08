namespace Rivet;

/// <summary>
/// Preserves OpenAPI validation constraints through the C# round-trip.
/// All properties are optional — only set the ones present in the original schema.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RivetConstraintsAttribute : Attribute
{
    public double ExclusiveMinimum { get; set; } = double.NaN;
    public double ExclusiveMaximum { get; set; } = double.NaN;
    public double MultipleOf { get; set; } = double.NaN;
    public int MinItems { get; set; } = -1;
    public int MaxItems { get; set; } = -1;
    public bool UniqueItems { get; set; }
}
