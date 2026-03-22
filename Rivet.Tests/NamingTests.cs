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
    [InlineData("", "_")]
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
    [InlineData("DateCreated<", "DateCreated")] // Twilio-style: angle brackets stripped
    [InlineData("$ref", "Ref")] // Leading special char stripped, first letter uppercased
    [InlineData("***", "_")] // All special chars stripped → fallback
    [InlineData("#id", "Id")] // Leading # stripped, uppercased
    [InlineData("_", "_")] // Underscore-only: delimiter splits to zero parts → fallback
    [InlineData("__", "_")] // Multiple underscores → same fallback
    [InlineData(">=", "_")] // Operator chars stripped → fallback
    [InlineData("FooBarSchema_2", "FooBarSchema_2")] // Dedup suffix preserved (trailing _N)
    [InlineData("MyType_12", "MyType_12")] // Multi-digit dedup suffix preserved
    [InlineData("foo_bar_schema", "FooBarSchema")] // Normal snake_case still PascalCased
    [InlineData("Foo_", "Foo")] // Trailing underscore (no digits) → split, not dedup suffix
    [InlineData("Bar_a", "BarA")] // Trailing underscore + non-digit → split
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

    [Theory]
    [InlineData("abc123", "Abc123")] // Starts lowercase → uppercase
    [InlineData("123abc", "_123abc")] // Starts with digit → prepend _
    [InlineData("$ref", "Ref")] // Special char stripped, uppercase
    [InlineData("***", "_")] // All stripped → fallback
    [InlineData("ValidName", "ValidName")] // No change
    [InlineData("", "")]
    public void StripInvalidIdentifierChars(string input, string expected)
    {
        Assert.Equal(expected, Naming.StripInvalidIdentifierChars(input));
    }
}
