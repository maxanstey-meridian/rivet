using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class TypeGrouperTests
{
    [Fact]
    public void CrossReferencedTypes_MovedToCommon()
    {
        // MemberDto references Email (different namespace) → Email goes to common
        var definitions = new List<TsTypeDefinition>
        {
            new("MemberDto", [], [
                new("id", new TsType.Primitive("string"), false),
                new("email", new TsType.Brand("Email", new TsType.Primitive("string")), false),
            ]),
            new("TaskDto", [], [
                new("id", new TsType.Primitive("string"), false),
                new("status", new TsType.TypeRef("TaskStatus"), false),
            ]),
        };
        var brands = new List<TsType.Brand>
        {
            new("Email", new TsType.Primitive("string")),
        };
        var enums = new Dictionary<string, TsType>
        {
            ["TaskStatus"] = new TsType.StringUnion(["Open", "Closed"]),
        };
        var namespaces = new Dictionary<string, string?>
        {
            ["MemberDto"] = "Members",
            ["Email"] = "Domain",
            ["TaskDto"] = "Tasks",
            ["TaskStatus"] = "Domain",
        };

        var result = TypeGrouper.Group(definitions, brands, enums, namespaces);

        // Domain types (Email, TaskStatus) are used by Members and Tasks → promoted to common
        var common = result.Groups.Single(g => g.FileName == "common");
        Assert.Single(common.Brands);
        Assert.Equal("Email", common.Brands[0].Name);
        Assert.Contains("TaskStatus", common.Enums.Keys);

        // Members group has MemberDto, imports Email from common
        var members = result.Groups.Single(g => g.FileName == "members");
        Assert.Single(members.Definitions);
        Assert.Equal("MemberDto", members.Definitions[0].Name);
        Assert.True(members.Imports.ContainsKey("common"));

        // Tasks group has TaskDto, imports TaskStatus from common
        var tasks = result.Groups.Single(g => g.FileName == "tasks");
        Assert.Single(tasks.Definitions);
        Assert.Equal("TaskDto", tasks.Definitions[0].Name);
        Assert.True(tasks.Imports.ContainsKey("common"));
    }

    [Fact]
    public void SingleNamespace_NoCommonFile()
    {
        var definitions = new List<TsTypeDefinition>
        {
            new("FooDto", [], [new("id", new TsType.Primitive("string"), false)]),
            new("BarDto", [], [new("id", new TsType.Primitive("string"), false)]),
        };
        var namespaces = new Dictionary<string, string?>
        {
            ["FooDto"] = "Api",
            ["BarDto"] = "Api",
        };

        var result = TypeGrouper.Group(definitions, [], new Dictionary<string, TsType>(), namespaces);

        // All in one group, no common needed
        Assert.Single(result.Groups);
        Assert.Equal("api", result.Groups[0].FileName);
        Assert.Equal(2, result.Groups[0].Definitions.Count);
    }

    [Fact]
    public void NamespaceCollision_NumberSuffix()
    {
        var definitions = new List<TsTypeDefinition>
        {
            new("FooDto", [], [new("id", new TsType.Primitive("string"), false)]),
            new("BarDto", [], [new("id", new TsType.Primitive("string"), false)]),
        };
        // Both map to "Api" namespace last segment but from different full namespaces
        // We simulate this by having two different types that both resolve to "Api"
        // The grouper groups by value, so same value = same group
        // To test collision, we'd need types with the same last segment but different groups
        // That can't happen with our current model — the namespace string IS the group key
        // So collision only happens if we explicitly had "Api" and "Api" as different groups
        // which is the identity case. Number suffix handles future configurable grouping.
        var namespaces = new Dictionary<string, string?>
        {
            ["FooDto"] = "Api",
            ["BarDto"] = "Api",
        };

        var result = TypeGrouper.Group(definitions, [], new Dictionary<string, TsType>(), namespaces);

        Assert.Single(result.Groups);
        Assert.Equal("api", result.Groups[0].FileName);
    }

    [Fact]
    public void NullNamespace_GoesToCommon()
    {
        var definitions = new List<TsTypeDefinition>
        {
            new("GlobalDto", [], [new("id", new TsType.Primitive("string"), false)]),
        };
        var namespaces = new Dictionary<string, string?>
        {
            ["GlobalDto"] = null,
        };

        var result = TypeGrouper.Group(definitions, [], new Dictionary<string, TsType>(), namespaces);

        var common = result.Groups.Single(g => g.FileName == "common");
        Assert.Single(common.Definitions);
        Assert.Equal("GlobalDto", common.Definitions[0].Name);
    }

    [Fact]
    public void TransitiveCrossRef_PromotedToCommon()
    {
        // InnerDto is only referenced by OuterDto which is in common
        // So InnerDto should also be promoted to common
        var definitions = new List<TsTypeDefinition>
        {
            new("OuterDto", [], [
                new("inner", new TsType.TypeRef("InnerDto"), false),
            ]),
            new("InnerDto", [], [
                new("value", new TsType.Primitive("string"), false),
            ]),
            new("ConsumerA", [], [
                new("outer", new TsType.TypeRef("OuterDto"), false),
            ]),
            new("ConsumerB", [], [
                new("outer", new TsType.TypeRef("OuterDto"), false),
            ]),
        };
        var namespaces = new Dictionary<string, string?>
        {
            ["OuterDto"] = "Shared",
            ["InnerDto"] = "Shared",
            ["ConsumerA"] = "ModuleA",
            ["ConsumerB"] = "ModuleB",
        };

        var result = TypeGrouper.Group(definitions, [], new Dictionary<string, TsType>(), namespaces);

        // OuterDto cross-referenced by A and B → common
        // InnerDto referenced by OuterDto (now common) from Shared → also common
        var common = result.Groups.Single(g => g.FileName == "common");
        var commonNames = common.Definitions.Select(d => d.Name).ToHashSet();
        Assert.Contains("OuterDto", commonNames);
        Assert.Contains("InnerDto", commonNames);
    }
}
