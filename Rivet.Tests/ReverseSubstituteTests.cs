using Rivet.Tool.Import;

namespace Rivet.Tests;

public sealed class ReverseSubstituteTests
{
    [Fact]
    public void SimpleReplacement_Works()
    {
        var map = new Dictionary<string, string> { ["TaskDto"] = "T" };
        var result = SchemaClassifier.ReverseSubstituteTypes("TaskDto", map);
        Assert.Equal("T", result);
    }

    [Fact]
    public void GenericWrapper_ReplacesTypeArg()
    {
        var map = new Dictionary<string, string> { ["TaskDto"] = "T" };
        var result = SchemaClassifier.ReverseSubstituteTypes("List<TaskDto>", map);
        Assert.Equal("List<T>", result);
    }

    [Fact]
    public void SubstringInTypeName_NotCorrupted()
    {
        // "Task" is a prefix of "TaskStatus" — naive Replace("Task","T") turns TaskStatus into TStatus
        var map = new Dictionary<string, string> { ["Task"] = "T" };
        var result = SchemaClassifier.ReverseSubstituteTypes("TaskStatus", map);
        Assert.Equal("TaskStatus", result);
    }

    [Fact]
    public void SubstringInGenericContext_NotCorrupted()
    {
        // Property type has both the concrete arg and a longer type containing it as prefix
        var map = new Dictionary<string, string> { ["Task"] = "T" };
        var result = SchemaClassifier.ReverseSubstituteTypes("Dictionary<TaskPriority, Task>", map);
        Assert.Equal("Dictionary<TaskPriority, T>", result);
    }

    [Fact]
    public void NullableConcreteType_Replaced()
    {
        var map = new Dictionary<string, string> { ["Task"] = "T" };
        var result = SchemaClassifier.ReverseSubstituteTypes("Task?", map);
        Assert.Equal("T?", result);
    }

    [Fact]
    public void CascadingReplacement_DoesNotCorrupt()
    {
        // First replacement produces text that contains a later key as substring
        // ListItem → TItem, then naive Item → T would corrupt TItem → TT
        var map = new Dictionary<string, string>
        {
            ["ListItem"] = "TItem",
            ["Item"] = "T"
        };
        var result = SchemaClassifier.ReverseSubstituteTypes("Pair<ListItem, Item>", map);
        Assert.Equal("Pair<TItem, T>", result);
    }

    [Fact]
    public void MultipleTypeParams_IndependentReplacement()
    {
        var map = new Dictionary<string, string>
        {
            ["UserDto"] = "TData",
            ["ErrorDto"] = "TError"
        };
        var result = SchemaClassifier.ReverseSubstituteTypes("Result<UserDto, ErrorDto>", map);
        Assert.Equal("Result<TData, TError>", result);
    }

    [Fact]
    public void ConcreteTypeMatchesInsideArraySuffix_NotCorrupted()
    {
        // "int" inside "Point" should not be replaced
        var map = new Dictionary<string, string> { ["int"] = "T" };
        var result = SchemaClassifier.ReverseSubstituteTypes("Point", map);
        Assert.Equal("Point", result);
    }

    [Fact]
    public void NestedGeneric_ReplacesCorrectly()
    {
        var map = new Dictionary<string, string> { ["TaskDto"] = "T" };
        var result = SchemaClassifier.ReverseSubstituteTypes("List<List<TaskDto>>", map);
        Assert.Equal("List<List<T>>", result);
    }
}
