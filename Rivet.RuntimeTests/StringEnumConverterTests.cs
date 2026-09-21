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

    [Fact]
    public void Built_In_Converter_Keeps_The_Exact_Member_Names()
    {
        Assert.Equal("\"DarkGreen\"", JsonSerializer.Serialize(ColourExact.DarkGreen));
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
