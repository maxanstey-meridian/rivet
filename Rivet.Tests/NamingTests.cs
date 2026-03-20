using Rivet.Tool;

namespace Rivet.Tests;

public sealed class NamingTests
{
    [Theory]
    [InlineData("my_status", "MyStatus")]
    [InlineData("in-progress", "InProgress")]
    [InlineData("ACTIVE", "ACTIVE")] // All-caps preserved — avoids silent serialization mismatch with APIs expecting exact casing
    [InlineData("already_Good", "AlreadyGood")]
    [InlineData("a", "A")]
    [InlineData("", "")]
    [InlineData("PascalCase", "PascalCase")]
    [InlineData("camelCase", "CamelCase")]
    [InlineData("with spaces", "WithSpaces")]
    [InlineData("mixed_kebab-snake", "MixedKebabSnake")]
    [InlineData("repos/listForOrg", "ReposListForOrg")]
    [InlineData("io.k8s.api.core.v1.PodSpec", "IoK8sApiCoreV1PodSpec")]
    [InlineData("AI Studio", "AIStudio")]
    [InlineData("pull-request-simple", "PullRequestSimple")]
    [InlineData("waf-managed-rules_response", "WafManagedRulesResponse")]
    [InlineData("AiSingleAgentResponse--Full", "AiSingleAgentResponseFull")]
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
