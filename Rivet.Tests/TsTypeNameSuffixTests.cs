using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Model-level coverage for the shared <see cref="TsType.GetNameSuffix"/> /
/// <see cref="TsType.MonomorphisedName"/> naming helper. This helper survives the
/// OpenAPI pivot (it names monomorphised component schemas), so its coverage lives
/// here rather than in the doomed TypeEmitter test file.
/// </summary>
public sealed class TsTypeNameSuffixTests
{
    [Fact]
    public void GetNameSuffix_Covers_Simple_Branches()
    {
        Assert.Equal("Foo", TsType.GetNameSuffix(new TsType.TypeRef("Foo")));
        Assert.Equal("T", TsType.GetNameSuffix(new TsType.TypeParam("T")));
        Assert.Equal("String", TsType.GetNameSuffix(new TsType.Primitive("string")));
        Assert.Equal("Email", TsType.GetNameSuffix(new TsType.Brand("Email", new TsType.Primitive("string"))));
        Assert.Equal("RecordString", TsType.GetNameSuffix(new TsType.Dictionary(new TsType.Primitive("string"))));
        Assert.Equal("FooArray", TsType.GetNameSuffix(new TsType.Array(new TsType.TypeRef("Foo"))));
        Assert.Equal("FooNullable", TsType.GetNameSuffix(new TsType.Nullable(new TsType.TypeRef("Foo"))));
        Assert.Equal("KindUnion", TsType.GetNameSuffix(new TsType.TaggedUnion("kind",
            [new TsType.TaggedUnionVariant("a", new TsType.TypeRef("A"))])));
    }

    [Fact]
    public void GetNameSuffix_StringUnion_Branches()
    {
        // <= 3 members: concatenated member names
        Assert.Equal("AB", TsType.GetNameSuffix(new TsType.StringUnion(["A", "B"])));
        // > 3 members: collapses to "Enum" (residual collisions are disambiguated
        // per-emit by OpenApiEmitter's name registry)
        Assert.Equal("Enum", TsType.GetNameSuffix(new TsType.StringUnion(["A", "B", "C", "D"])));
        Assert.Equal("Enum", TsType.GetNameSuffix(new TsType.IntUnion([1, 2, 3])));
    }

    [Fact]
    public void GetNameSuffix_InlineObject_Includes_Field_Types()
    {
        // E2 root cause: the suffix must incorporate field TYPES, not just field names —
        // {value: string} and {value: number} are distinct shapes and must get distinct suffixes.
        var ofString = new TsType.InlineObject([("value", new TsType.Primitive("string"))]);
        var ofNumber = new TsType.InlineObject([("value", new TsType.Primitive("number"))]);

        Assert.Equal("Value_String", TsType.GetNameSuffix(ofString));
        Assert.Equal("Value_Number", TsType.GetNameSuffix(ofNumber));
        Assert.NotEqual(TsType.GetNameSuffix(ofString), TsType.GetNameSuffix(ofNumber));

        // Multiple fields join name/type pairs
        Assert.Equal("Key_String_Value_Number", TsType.GetNameSuffix(new TsType.InlineObject([
            ("key", new TsType.Primitive("string")),
            ("value", new TsType.Primitive("number")),
        ])));

        // > 3 fields collapse to "Object" (registry-disambiguated downstream)
        Assert.Equal("Object", TsType.GetNameSuffix(new TsType.InlineObject([
            ("a", new TsType.Primitive("string")),
            ("b", new TsType.Primitive("string")),
            ("c", new TsType.Primitive("string")),
            ("d", new TsType.Primitive("string")),
        ])));
    }

    [Fact]
    public void GetNameSuffix_Is_Deterministic()
    {
        var make = () => (TsType)new TsType.Generic("Wrapper",
            [new TsType.InlineObject([("value", new TsType.Primitive("string"))])]);

        Assert.Equal(TsType.GetNameSuffix(make()), TsType.GetNameSuffix(make()));
    }

    [Fact]
    public void MonomorphisedName_Distinct_Instantiations_Differ()
    {
        var ofString = new TsType.Generic("Wrapper",
            [new TsType.InlineObject([("value", new TsType.Primitive("string"))])]);
        var ofNumber = new TsType.Generic("Wrapper",
            [new TsType.InlineObject([("value", new TsType.Primitive("number"))])]);

        Assert.Equal("Wrapper_Value_String", TsType.MonomorphisedName(ofString));
        Assert.Equal("Wrapper_Value_Number", TsType.MonomorphisedName(ofNumber));

        // Multiple type arguments are underscore-joined
        Assert.Equal("Pair_String_Number", TsType.MonomorphisedName(new TsType.Generic("Pair",
            [new TsType.Primitive("string"), new TsType.Primitive("number")])));
    }
}
