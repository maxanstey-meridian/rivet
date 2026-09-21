using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet.RuntimeTests;

public sealed class ScalarConverterTests
{
    [RivetScalar]
    public sealed record Key(string Value);

    [RivetScalar]
    public readonly record struct OptionalNumber(int? Value);

    [RivetScalar]
    public readonly record struct Number(int Value);

    [RivetScalar]
    public sealed record Extra(string Value, int Other);

    [RivetScalar]
    public sealed record PropertyConverter(
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] int Value
    );

    [RivetScalar]
    public sealed record Positive
    {
        public int Value { get; }

        public Positive(int value)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            Value = value;
        }
    }

    public sealed class CustomConverterAttribute : JsonConverterAttribute { }

    [RivetScalar]
    public sealed record CustomPropertyConverter([property: CustomConverter] string Value);

    [RivetScalar, CustomConverter]
    public sealed record CompetingConverter(string Value);

    [RivetScalar]
    public sealed class BroadConstructor
    {
        public string Value { get; }

        public BroadConstructor(object value) => Value = (string)value;
    }

    [RivetScalar]
    public sealed class RefConstructor
    {
        public string Value { get; }

        public RefConstructor(ref string value) => Value = value;
    }

    [Fact]
    public void Scalar_Converter_Requires_An_Exact_Constructor_And_No_Custom_Converter_Attributes()
    {
        Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(new CustomPropertyConverter("x"))
        );
        Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(new CompetingConverter("x"))
        );
        Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(new BroadConstructor("x"))
        );
        var value = "x";
        Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(new RefConstructor(ref value))
        );
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("é\"\\line\n")]
    public void String_Scalar_Dictionary_Keys_RoundTrip(string key)
    {
        var original = new Dictionary<Key, int> { [new Key(key)] = 42 };
        var json = JsonSerializer.Serialize(original);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(42, document.RootElement.GetProperty(key).GetInt32());
        Assert.Equal(42, JsonSerializer.Deserialize<Dictionary<Key, int>>(json)![new Key(key)]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    public void Nullable_Inner_Value_RoundTrips(int? value)
    {
        var original = new OptionalNumber(value);
        Assert.Equal(
            original,
            JsonSerializer.Deserialize<OptionalNumber>(JsonSerializer.Serialize(original))
        );
    }

    [Fact]
    public void Null_Reference_Wrapper_Remains_Null()
    {
        Assert.Null(JsonSerializer.Deserialize<Key>("null"));
        Assert.Equal("null", JsonSerializer.Serialize(new Key(null!)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Number>("null"));
    }

    [Fact]
    public void Unsupported_Keys_And_Shapes_Fail_Clearly()
    {
        Assert.Throws<NotSupportedException>(() =>
            JsonSerializer.Serialize(new Dictionary<Number, int> { [new Number(1)] = 2 })
        );
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Serialize(new Dictionary<Key, int> { [new Key(null!)] = 2 })
        );
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new Extra("x", 1)));
        Assert.Throws<InvalidOperationException>(() =>
            JsonSerializer.Serialize(new PropertyConverter(1))
        );
    }

    [Fact]
    public void Constructor_Validation_Is_A_Json_Error()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Positive>("0"));
        Assert.Equal(1, JsonSerializer.Deserialize<Positive>("1")!.Value);
    }
}
