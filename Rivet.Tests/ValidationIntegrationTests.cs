using System.ComponentModel.DataAnnotations;

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
        [property: Required, MinLength(1), MaxLength(200)] string Title,
        [property: RegularExpression(@"^REF-\d+$")] string Reference,
        [property: Range(1, 100)] int Priority,
        [property: StringLength(500, MinimumLength = 10)] string Description,
        [property: RivetConstraints(ExclusiveMinimum = 0, MultipleOf = 0.5)] double Score
    );

    private static ConstrainedDto ValidInstance =>
        new(
            Title: "Valid Title",
            Reference: "REF-123",
            Priority: 50,
            Description: "A valid description that is long enough",
            Score: 2.5
        );

    private static (bool IsValid, List<ValidationResult> Results) Validate(ConstrainedDto instance)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(instance);
        var isValid = Validator.TryValidateObject(
            instance,
            context,
            results,
            validateAllProperties: true
        );
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
    public void Exotic_RivetConstraints_Enforced_By_Validator()
    {
        // Score = -5 violates ExclusiveMinimum = 0. RivetConstraintsAttribute
        // is a ValidationAttribute, so Validator.TryValidateObject() enforces it.
        var dto = ValidInstance with
        {
            Score = -5,
        };
        var (isValid, results) = Validate(dto);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Score"));
    }

    // ========== Per-facet RivetConstraints enforcement ==========

    private sealed record FacetDto(
        [property: RivetConstraints(ExclusiveMinimum = 0)] double AboveZero,
        [property: RivetConstraints(ExclusiveMaximum = 100)] int BelowHundred,
        [property: RivetConstraints(MultipleOf = 0.5)] double HalfSteps,
        [property: RivetConstraints(MinItems = 1, MaxItems = 3)] IReadOnlyList<string>? Items,
        [property: RivetConstraints(UniqueItems = true)] IReadOnlyList<int>? Distinct
    );

    private static FacetDto ValidFacets =>
        new(AboveZero: 0.1, BelowHundred: 99, HalfSteps: 2.5, Items: ["one"], Distinct: [1, 2, 3]);

    private static (bool IsValid, List<ValidationResult> Results) ValidateFacets(FacetDto instance)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(instance);
        var isValid = Validator.TryValidateObject(
            instance,
            context,
            results,
            validateAllProperties: true
        );
        return (isValid, results);
    }

    [Fact]
    public void Facets_Valid_Instance_Passes()
    {
        var (isValid, results) = ValidateFacets(ValidFacets);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Fact]
    public void ExclusiveMinimum_Boundary_Value_Fails()
    {
        // Exclusive: the boundary itself is a violation.
        var (isValid, results) = ValidateFacets(ValidFacets with { AboveZero = 0 });

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("AboveZero"));
    }

    [Fact]
    public void ExclusiveMaximum_Boundary_Value_Fails()
    {
        var (isValid, results) = ValidateFacets(ValidFacets with { BelowHundred = 100 });

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("BelowHundred"));
    }

    [Fact]
    public void ExclusiveMaximum_Value_Below_Passes()
    {
        var (isValid, _) = ValidateFacets(ValidFacets with { BelowHundred = -50 });

        Assert.True(isValid);
    }

    [Fact]
    public void MultipleOf_NonMultiple_Fails()
    {
        var (isValid, results) = ValidateFacets(ValidFacets with { HalfSteps = 2.3 });

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("HalfSteps"));
    }

    [Fact]
    public void MultipleOf_Tolerates_FloatingPoint_Accumulation()
    {
        // 0.1 + 0.2 + 0.3 + 0.4 + 0.5 is mathematically 1.5 (a multiple of 0.5)
        // but accumulates to 1.5000000000000002 in doubles — must still pass.
        var noisy = 0.1 + 0.2 + 0.3 + 0.4 + 0.5;
        var (isValid, _) = ValidateFacets(ValidFacets with { HalfSteps = noisy });

        Assert.True(isValid);
    }

    [Fact]
    public void MinItems_Violation_Fails()
    {
        var (isValid, results) = ValidateFacets(ValidFacets with { Items = [] });

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Items"));
    }

    [Fact]
    public void MaxItems_Violation_Fails()
    {
        var (isValid, results) = ValidateFacets(ValidFacets with { Items = ["a", "b", "c", "d"] });

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Items"));
    }

    [Fact]
    public void UniqueItems_Duplicate_Fails()
    {
        var (isValid, results) = ValidateFacets(ValidFacets with { Distinct = [1, 2, 2] });

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Distinct"));
    }

    [Fact]
    public void Null_Collections_Pass_Constraint_Validation()
    {
        // Null is [Required]'s job — RivetConstraints lets nulls through,
        // matching the DataAnnotations convention.
        var (isValid, results) = ValidateFacets(ValidFacets with { Items = null, Distinct = null });

        Assert.True(isValid);
        Assert.Empty(results);
    }
}
