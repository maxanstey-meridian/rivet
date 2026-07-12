using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Rivet;

/// <summary>
/// Preserves OpenAPI validation constraints through the C# round-trip.
/// All properties are optional — only set the ones present in the original schema.
/// <para>
/// This is a <see cref="ValidationAttribute"/>: under validating hosts (ASP.NET
/// <c>[ApiController]</c> model validation, <c>Validator.TryValidateObject</c>)
/// the declared facets are enforced at runtime. Numeric facets
/// (<see cref="ExclusiveMinimum"/>, <see cref="ExclusiveMaximum"/>,
/// <see cref="MultipleOf"/>) apply to numeric values; collection facets
/// (<see cref="MinItems"/>, <see cref="MaxItems"/>, <see cref="UniqueItems"/>)
/// apply to non-string <see cref="IEnumerable"/> values. <c>null</c> always
/// passes — pair with <c>[Required]</c> to reject nulls, matching the
/// DataAnnotations convention.
/// </para>
/// <para>
/// Behavior note: prior versions were spec-only (a plain <see cref="Attribute"/>);
/// projects using <c>[ApiController]</c> model validation now get these facets
/// enforced on request DTOs.
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

    public double ExclusiveMinimum { get; set; } = double.NaN;
    public double ExclusiveMaximum { get; set; } = double.NaN;
    public double MultipleOf { get; set; } = double.NaN;
    public int MinItems { get; set; } = -1;
    public int MaxItems { get; set; } = -1;
    public bool UniqueItems { get; set; }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        // Null is [Required]'s job — DataAnnotations convention.
        if (value is null)
        {
            return ValidationResult.Success;
        }

        var name = validationContext.DisplayName;
        var members = validationContext.MemberName is { } member ? new[] { member } : null;

        if (TryGetNumber(value, out var number))
        {
            if (!double.IsNaN(ExclusiveMinimum) && number <= ExclusiveMinimum)
            {
                return new ValidationResult(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The field {name} must be greater than {ExclusiveMinimum}."
                    ),
                    members
                );
            }

            if (!double.IsNaN(ExclusiveMaximum) && number >= ExclusiveMaximum)
            {
                return new ValidationResult(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The field {name} must be less than {ExclusiveMaximum}."
                    ),
                    members
                );
            }

            if (!double.IsNaN(MultipleOf) && !IsMultipleOf(number, MultipleOf))
            {
                return new ValidationResult(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The field {name} must be a multiple of {MultipleOf}."
                    ),
                    members
                );
            }

            return ValidationResult.Success;
        }

        // Strings are IEnumerable<char> but minItems/maxItems/uniqueItems are
        // array facets — never apply them to strings.
        if (value is IEnumerable enumerable and not string)
        {
            var items = enumerable.Cast<object?>().ToList();

            if (MinItems >= 0 && items.Count < MinItems)
            {
                return new ValidationResult(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The field {name} must contain at least {MinItems} item(s)."
                    ),
                    members
                );
            }

            if (MaxItems >= 0 && items.Count > MaxItems)
            {
                return new ValidationResult(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The field {name} must contain at most {MaxItems} item(s)."
                    ),
                    members
                );
            }

            if (UniqueItems && items.Count != items.Distinct().Count())
            {
                return new ValidationResult(
                    $"The field {name} must not contain duplicate items.",
                    members
                );
            }
        }

        return ValidationResult.Success;
    }

    private static bool TryGetNumber(object value, out double number)
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
