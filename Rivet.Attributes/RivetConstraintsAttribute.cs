using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Rivet;

/// <summary>
/// The two JSON Schema facets DataAnnotations has no attribute for: <c>multipleOf</c>
/// and <c>uniqueItems</c>. Use <c>[Range]</c> (with <c>MinimumIsExclusive</c>/
/// <c>MaximumIsExclusive</c>) for bounds and <c>[MinLength]</c>/<c>[MaxLength]</c>/
/// <c>[Length]</c> for item counts.
/// <para>
/// This is a <see cref="ValidationAttribute"/>, so validating hosts enforce it.
/// <see cref="MultipleOf"/> applies to numeric values and <see cref="UniqueItems"/> to
/// non-string <see cref="IEnumerable"/> values. <c>null</c> always passes; pair with
/// <c>[Required]</c> to reject nulls, as with DataAnnotations.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RivetConstraintsAttribute : ValidationAttribute
{
    /// <summary>
    /// Relative tolerance for <see cref="MultipleOf"/> on floating-point values:
    /// value/multipleOf must be within this distance of an integer.
    /// </summary>
    private const double MultipleOfTolerance = 1e-9;

    public double MultipleOf { get; set; } = double.NaN;
    public bool UniqueItems { get; set; }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var name = validationContext.DisplayName;
        var members = validationContext.MemberName is { } member ? new[] { member } : null;

        if (
            !double.IsNaN(MultipleOf)
            && TryGetNumber(value, out var number)
            && !IsMultipleOf(number, MultipleOf)
        )
        {
            return new ValidationResult(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The field {name} must be a multiple of {MultipleOf}."
                ),
                members
            );
        }

        // Strings are IEnumerable<char>, but uniqueItems is an array facet.
        if (UniqueItems && value is IEnumerable enumerable and not string)
        {
            var items = enumerable.Cast<object?>().ToList();
            if (items.Count != items.Distinct().Count())
            {
                return new ValidationResult(
                    $"The field {name} must not contain duplicate items.",
                    members
                );
            }
        }

        return ValidationResult.Success;
    }

    private static bool TryGetNumber(object? value, out double number)
    {
        switch (value)
        {
            case sbyte
            or byte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong
            or float
            or double
            or decimal:
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            default:
                number = double.NaN;
                return false;
        }
    }

    private static bool IsMultipleOf(double value, double multipleOf)
    {
        if (multipleOf == 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        var ratio = value / multipleOf;
        return Math.Abs(ratio - Math.Round(ratio))
            <= MultipleOfTolerance * Math.Max(1, Math.Abs(ratio));
    }
}
