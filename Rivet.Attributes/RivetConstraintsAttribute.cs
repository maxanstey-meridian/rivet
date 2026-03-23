namespace Rivet;

/// <summary>
/// Preserves OpenAPI validation constraints through the C# round-trip.
/// All properties are optional — only set the ones present in the original schema.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RivetConstraintsAttribute : Attribute
{
    public int MinLength { get; set; } = -1;
    public int MaxLength { get; set; } = -1;
    public string? Pattern { get; set; }
    public double Minimum { get; set; } = double.NaN;
    public double Maximum { get; set; } = double.NaN;
    public double ExclusiveMinimum { get; set; } = double.NaN;
    public double ExclusiveMaximum { get; set; } = double.NaN;
    public double MultipleOf { get; set; } = double.NaN;
    public int MinItems { get; set; } = -1;
    public int MaxItems { get; set; } = -1;
    public bool UniqueItems { get; set; }
}
