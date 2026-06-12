using System.Text.Json;
using Rivet;

namespace Rivet.Tests;

/// <summary>
/// Runtime half of the D2 oneOf story: a [RivetUnion] wrapper must serialize
/// as its bare variant value (matching the oneOf the spec declares), never as
/// the wrapper object — and deserialize by matching the JSON kind.
/// </summary>
public sealed class RivetUnionConverterTests
{
    [RivetUnion]
    public sealed record StringOrLong(
        string? AsString,
        long? AsLong);

    [RivetUnion]
    public sealed record StringOrShape(
        string? AsString,
        Shape? AsShape);

    public sealed record Shape(string Kind, int Sides);

    [Fact]
    public void Serializes_The_Set_Variant_As_Bare_Value()
    {
        Assert.Equal("\"hello\"", JsonSerializer.Serialize(new StringOrLong("hello", null)));
        Assert.Equal("42", JsonSerializer.Serialize(new StringOrLong(null, 42)));
    }

    [Fact]
    public void Deserializes_By_Json_Kind()
    {
        var fromString = JsonSerializer.Deserialize<StringOrLong>("\"hello\"")!;
        Assert.Equal("hello", fromString.AsString);
        Assert.Null(fromString.AsLong);

        var fromNumber = JsonSerializer.Deserialize<StringOrLong>("42")!;
        Assert.Equal(42, fromNumber.AsLong);
        Assert.Null(fromNumber.AsString);
    }

    [Fact]
    public void Handles_Complex_Variants()
    {
        var json = JsonSerializer.Serialize(
            new StringOrShape(null, new Shape("triangle", 3)));
        Assert.Contains("triangle", json);
        Assert.DoesNotContain("AsShape", json);
        Assert.DoesNotContain("asShape", json);

        var roundTripped = JsonSerializer.Deserialize<StringOrShape>(json)!;
        Assert.Equal(3, roundTripped.AsShape!.Sides);
        Assert.Null(roundTripped.AsString);
    }

    [Fact]
    public void Rejects_Values_Matching_No_Variant()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StringOrLong>("true"));
    }

    [Fact]
    public void Round_Trips_Through_Wrapper()
    {
        var original = new StringOrLong("only-this", null);
        var roundTripped = JsonSerializer.Deserialize<StringOrLong>(
            JsonSerializer.Serialize(original))!;
        Assert.Equal(original, roundTripped);
    }
}
