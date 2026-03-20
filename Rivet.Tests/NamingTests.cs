using Rivet.Tool;

namespace Rivet.Tests;

public sealed class NamingTests
{
    [Theory]
    [InlineData("my_status", "MyStatus")]
    [InlineData("in-progress", "InProgress")]
    [InlineData("ACTIVE", "ACTIVE")]
    [InlineData("already_Good", "AlreadyGood")]
    [InlineData("a", "A")]
    [InlineData("", "")]
    [InlineData("PascalCase", "PascalCase")]
    [InlineData("camelCase", "CamelCase")]
    [InlineData("with spaces", "WithSpaces")]
    [InlineData("mixed_kebab-snake", "MixedKebabSnake")]
    public void ToPascalCaseFromSegments(string input, string expected)
    {
        Assert.Equal(expected, Naming.ToPascalCaseFromSegments(input));
    }

    [Theory]
    [InlineData("Hello", "hello")]
    [InlineData("hello", "hello")]
    [InlineData("", "")]
    public void ToCamelCase(string input, string expected)
    {
        Assert.Equal(expected, Naming.ToCamelCase(input));
    }

    [Theory]
    [InlineData("hello", "Hello")]
    [InlineData("Hello", "Hello")]
    [InlineData("", "")]
    public void ToPascalCase(string input, string expected)
    {
        Assert.Equal(expected, Naming.ToPascalCase(input));
    }
}
