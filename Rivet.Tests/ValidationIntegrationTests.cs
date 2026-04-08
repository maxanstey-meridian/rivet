using System.ComponentModel.DataAnnotations;
using Rivet;

namespace Rivet.Tests;

/// <summary>
/// Proves that the same DataAnnotation attributes driving Rivet codegen
/// also drive runtime validation via Validator.TryValidateObject().
/// </summary>
public sealed class ValidationIntegrationTests
{
    // Real compiled record — mirrors the AnnotationRoundTripTests fixture.
    // [property:] target ensures attributes land on properties, not constructor params.
    private sealed record ConstrainedDto(
        [property: Required, MinLength(1), MaxLength(200)]
        string Title,

        [property: RegularExpression(@"^REF-\d+$")]
        string Reference,

        [property: Range(1, 100)]
        int Priority,

        [property: StringLength(500, MinimumLength = 10)]
        string Description,

        [property: RivetConstraints(ExclusiveMinimum = 0, MultipleOf = 0.5)]
        double Score);

    private static ConstrainedDto ValidInstance => new(
        Title: "Valid Title",
        Reference: "REF-123",
        Priority: 50,
        Description: "A valid description that is long enough",
        Score: 2.5);

    private static (bool IsValid, List<ValidationResult> Results) Validate(ConstrainedDto instance)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(instance);
        var isValid = Validator.TryValidateObject(instance, context, results, validateAllProperties: true);
        return (isValid, results);
    }

    [Fact]
    public void Valid_Instance_Passes()
    {
        var (isValid, results) = Validate(ValidInstance);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Fact]
    public void MinLength_Violation_On_Title()
    {
        var dto = ValidInstance with { Title = "" };
        var (isValid, results) = Validate(dto);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Title"));
    }

    [Fact]
    public void RegularExpression_Violation_On_Reference()
    {
        var dto = ValidInstance with { Reference = "INVALID" };
        var (isValid, results) = Validate(dto);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Reference"));
    }

    [Fact]
    public void Range_Violation_On_Priority()
    {
        var dto = ValidInstance with { Priority = 0 };
        var (isValid, results) = Validate(dto);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Priority"));
    }

    [Fact]
    public void StringLength_Too_Short_On_Description()
    {
        var dto = ValidInstance with { Description = "short" };
        var (isValid, results) = Validate(dto);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Description"));
    }

    [Fact]
    public void StringLength_Too_Long_On_Description()
    {
        var dto = ValidInstance with { Description = new string('x', 501) };
        var (isValid, results) = Validate(dto);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Description"));
    }

    [Fact]
    public void Exotic_RivetConstraints_Ignored_By_Validator()
    {
        // Score = -5 violates ExclusiveMinimum = 0 conceptually,
        // but RivetConstraintsAttribute is not a ValidationAttribute,
        // so Validator.TryValidateObject() ignores it entirely.
        var dto = ValidInstance with { Score = -5 };
        var (isValid, results) = Validate(dto);

        Assert.True(isValid);
        Assert.Empty(results);
    }
}
