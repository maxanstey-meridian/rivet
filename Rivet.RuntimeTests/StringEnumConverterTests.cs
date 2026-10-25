using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet.RuntimeTests;

public sealed class StringEnumConverterTests
{
    [JsonConverter(typeof(RivetCamelCaseEnumConverter<Colour>))]
    public enum Colour
    {
        Unknown,
        DarkGreen,
        Level3,
        RestrictedByway2A,
    }

    [JsonConverter(typeof(RivetSnakeCaseEnumConverter<ColourSnake>))]
    public enum ColourSnake
    {
        Unknown,
        DarkGreen,
        Level3,
        RestrictedByway2A,
    }

    [JsonConverter(typeof(RivetKebabCaseEnumConverter<ColourKebab>))]
    public enum ColourKebab
    {
        Unknown,
        DarkGreen,
        Level3,
        RestrictedByway2A,
    }

    [JsonConverter(typeof(RivetLowerCaseEnumConverter<ColourLower>))]
    public enum ColourLower
    {
        Unknown,
        DarkGreen,
        Level3,
        RestrictedByway2A,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<ColourExact>))]
    public enum ColourExact
    {
        DarkGreen,
        Level3,
    }

    [Fact]
    public void CamelCase_Converter_Writes_Cased_Values_And_Reads_Them_Back()
    {
        Assert.Equal("\"darkGreen\"", JsonSerializer.Serialize(Colour.DarkGreen));
        Assert.Equal("\"level3\"", JsonSerializer.Serialize(Colour.Level3));
        Assert.Equal(Colour.DarkGreen, JsonSerializer.Deserialize<Colour>("\"darkGreen\""));
    }

    [Fact]
    public void SnakeCase_Converter_Follows_JsonNamingPolicy_SnakeCaseLower()
    {
        Assert.Equal("\"dark_green\"", JsonSerializer.Serialize(ColourSnake.DarkGreen));
        Assert.Equal(
            JsonNamingPolicy.SnakeCaseLower.ConvertName("RestrictedByway2A"),
            JsonSerializer.Serialize(ColourSnake.RestrictedByway2A).Trim('"')
        );
        Assert.Equal(
            ColourSnake.DarkGreen,
            JsonSerializer.Deserialize<ColourSnake>("\"dark_green\"")
        );
    }

    [Fact]
    public void KebabCase_Converter_Follows_JsonNamingPolicy_KebabCaseLower()
    {
        Assert.Equal("\"dark-green\"", JsonSerializer.Serialize(ColourKebab.DarkGreen));
        Assert.Equal(
            ColourKebab.DarkGreen,
            JsonSerializer.Deserialize<ColourKebab>("\"dark-green\"")
        );
    }

    [Fact]
    public void LowerCase_Converter_Lowercases_The_Whole_Member_Name()
    {
        Assert.Equal("\"darkgreen\"", JsonSerializer.Serialize(ColourLower.DarkGreen));
        Assert.Equal("\"level3\"", JsonSerializer.Serialize(ColourLower.Level3));
        Assert.Equal(
            ColourLower.DarkGreen,
            JsonSerializer.Deserialize<ColourLower>("\"darkgreen\"")
        );
    }

#if NET9_0_OR_GREATER
    [Fact]
    public void Member_Pin_Wins_Over_The_Policy()
    {
        Assert.Equal("\"L3\"", JsonSerializer.Serialize(ColourPinned.Level3));
        Assert.Equal("\"darkGreen\"", JsonSerializer.Serialize(ColourPinned.DarkGreen));
        Assert.Equal(ColourPinned.Level3, JsonSerializer.Deserialize<ColourPinned>("\"L3\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ColourPinned>("\"level3\""));
    }
#endif

    // The spec Rivet emits for these enums is a string union of the written names, so
    // reading takes exactly those names and nothing else, on every runtime.
    public static TheoryData<Type> RivetConvertedEnums =>
        [typeof(Colour), typeof(ColourSnake), typeof(ColourKebab), typeof(ColourLower)];

    [Theory]
    [MemberData(nameof(RivetConvertedEnums))]
    public void Rivet_Converters_Read_Back_Every_Name_They_Write(Type enumType)
    {
        foreach (var value in Enum.GetValues(enumType))
        {
            Assert.Equal(
                value,
                JsonSerializer.Deserialize(JsonSerializer.Serialize(value, enumType), enumType)
            );
        }
    }

    // "3" rather than "0": 0 is default(T), so reading it back wouldn't prove the string
    // was read at all. .NET 8's own converter reads both; these don't.
    [Theory]
    [MemberData(nameof(RivetConvertedEnums))]
    public void Rivet_Converters_Refuse_Anything_But_A_Written_Name(Type enumType)
    {
        foreach (
            var json in new[]
            {
                "0",
                "3",
                "\"0\"",
                "\"3\"",
                "\"RestrictedByway2A\"",
                "\"RESTRICTEDBYWAY2A\"",
                "\"unknown, level3\"",
                "null",
                "true",
            }
        )
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, enumType));
        }
    }

    [Fact]
    public void A_Refusal_Lists_The_Names_To_Send()
    {
        var refusal = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Colour>("\"DarkGreen\"")
        );

        Assert.Contains("\"DarkGreen\" is not a Colour value", refusal.Message);
        Assert.Contains(
            "\"unknown\", \"darkGreen\", \"level3\", \"restrictedByway2A\"",
            refusal.Message
        );
    }

    [Theory]
    [InlineData("{}", "A JSON object")]
    [InlineData("[]", "A JSON array")]
    [InlineData("true", "A JSON boolean")]
    [InlineData("false", "A JSON boolean")]
    [InlineData("3", "A JSON number")]
    [InlineData("null", "A JSON null")]
    public void A_Refusal_Names_The_Json_Type_Sent(string json, string named)
    {
        var refusal = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Colour>(json));

        Assert.StartsWith($"{named} is not a Colour value", refusal.Message);
    }

    [Fact]
    public void Dictionary_Keys_And_Nullable_Values_Follow_The_Same_Names()
    {
        var keyed = new Dictionary<Colour, int> { [Colour.DarkGreen] = 1 };

        Assert.Equal("{\"darkGreen\":1}", JsonSerializer.Serialize(keyed));
        Assert.Equal(
            keyed,
            JsonSerializer.Deserialize<Dictionary<Colour, int>>("{\"darkGreen\":1}")
        );
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Dictionary<Colour, int>>("{\"DarkGreen\":1}")
        );
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Dictionary<Colour, int>>("{\"1\":1}")
        );
        Assert.Null(JsonSerializer.Deserialize<Colour?>("null"));
        Assert.Equal(Colour.Level3, JsonSerializer.Deserialize<Colour?>("\"level3\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Colour?>("\"Level3\""));
    }

    [Fact]
    public void Built_In_Converter_Keeps_The_Exact_Member_Names()
    {
        Assert.Equal("\"DarkGreen\"", JsonSerializer.Serialize(ColourExact.DarkGreen));
    }

    [Fact]
    public void Built_In_Converter_Keeps_STJs_Lenient_Integer_Default()
    {
        Assert.Equal(ColourExact.Level3, JsonSerializer.Deserialize<ColourExact>("1"));
    }
}

#if NET9_0_OR_GREATER
[JsonConverter(typeof(RivetCamelCaseEnumConverter<ColourPinned>))]
public enum ColourPinned
{
    DarkGreen,

    [JsonStringEnumMemberName("L3")]
    Level3,
}

#endif
