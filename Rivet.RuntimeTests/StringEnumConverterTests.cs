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
    }
#endif

    // The spec Rivet emits for these enums is a string union, so the converters
    // refuse integers: a bare 0 always, and a numeric string "0" (which STJ would
    // otherwise read as an integer value) from .NET 9 on. They still read the C#
    // member name in any casing; that's STJ, and the spec doesn't advertise it.
    public static TheoryData<Type> RivetConvertedEnums =>
        [typeof(Colour), typeof(ColourSnake), typeof(ColourKebab), typeof(ColourLower)];

    [Theory]
    [MemberData(nameof(RivetConvertedEnums))]
    public void Rivet_Converters_Refuse_A_Json_Integer(Type enumType)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("0", enumType));
    }

#if NET9_0_OR_GREATER
    [Theory]
    [MemberData(nameof(RivetConvertedEnums))]
    public void Rivet_Converters_Refuse_A_Numeric_String(Type enumType)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"0\"", enumType));
    }
#else
    // .NET 8's System.Text.Json reads a numeric string as the enum value even with
    // allowIntegerValues: false; .NET 9 fixed it. Pinned rather than patched with a
    // newer STJ package, so moving off .NET 8 shows up here. "3" rather than "0": 0 is
    // default(T), so reading it back wouldn't prove the string was read at all.
    [Theory]
    [MemberData(nameof(RivetConvertedEnums))]
    public void Rivet_Converters_Still_Read_A_Numeric_String_On_Net8(Type enumType)
    {
        Assert.Equal(
            Enum.Parse(enumType, "RestrictedByway2A"),
            JsonSerializer.Deserialize("\"3\"", enumType)
        );
    }
#endif

    [Theory]
    [MemberData(nameof(RivetConvertedEnums))]
    public void Rivet_Converters_Read_The_Member_Name_In_Any_Casing(Type enumType)
    {
        foreach (var json in new[] { "\"RestrictedByway2A\"", "\"RESTRICTEDBYWAY2A\"" })
        {
            Assert.Equal(3, Convert.ToInt32(JsonSerializer.Deserialize(json, enumType)));
        }
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
