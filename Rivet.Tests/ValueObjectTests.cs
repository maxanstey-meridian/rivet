using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

public sealed class ValueObjectTests
{
    private static string Generate(string source)
    {
        var compilation = CompilationHelper.CreateCompilation(source);
        var walker = TypeWalker.Create(compilation);
        var definitions = walker.Definitions.Values.ToList();
        var brands = walker.Brands.Values.ToList();
        return TypeEmitter.Emit(definitions, brands, walker.Enums);
    }

    [Fact]
    public void SingleValueProperty_EmitsAsBrand()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public sealed record Email(string Value);

            [RivetType]
            public sealed record UserDto(Guid Id, Email Email);
            """;

        var result = Generate(source);

        Assert.Contains("""export type Email = string & { readonly __brand: "Email" };""", result);
        Assert.Contains("email: Email;", result);
        // Email should NOT be emitted as an object type
        Assert.DoesNotContain("value: string;", result);
    }

    [Fact]
    public void SingleValueProperty_NumericInner()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record Quantity(int Value);

            [RivetType]
            public sealed record OrderDto(string Name, Quantity Qty);
            """;

        var result = Generate(source);

        Assert.Contains("""export type Quantity = number & { readonly __brand: "Quantity" };""", result);
        Assert.Contains("qty: Quantity;", result);
    }

    [Fact]
    public void SingleValueProperty_GuidInner()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            public sealed record Uprn(string Value)
            {
                public override string ToString() => Value;
            }

            [RivetType]
            public sealed record PropertyDto(Guid Id, Uprn Uprn);
            """;

        var result = Generate(source);

        Assert.Contains("""export type Uprn = string & { readonly __brand: "Uprn" };""", result);
        Assert.Contains("uprn: Uprn;", result);
    }

    [Fact]
    public void MultipleProperties_NotAVO()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record Money(decimal Amount, string Currency);

            [RivetType]
            public sealed record ProductDto(string Name, Money Price);
            """;

        var result = Generate(source);

        // Money should be a full object type, not a brand
        Assert.Contains("export type Money = {", result);
        Assert.Contains("amount: number;", result);
        Assert.Contains("currency: string;", result);
        Assert.DoesNotContain("__brand", result);
    }

    [Fact]
    public void SinglePropertyNotNamedValue_NotAVO()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record Wrapper(string Content);

            [RivetType]
            public sealed record ThingDto(Wrapper Data);
            """;

        var result = Generate(source);

        // Single property but not named "Value" — emit as object type
        Assert.Contains("export type Wrapper = {", result);
        Assert.Contains("content: string;", result);
        Assert.DoesNotContain("__brand", result);
    }

    [Fact]
    public void NullableVO_EmitsBrandOrNull()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record Email(string Value);

            [RivetType]
            public sealed record ContactDto(string Name, Email? Email);
            """;

        var result = Generate(source);

        Assert.Contains("""export type Email = string & { readonly __brand: "Email" };""", result);
        Assert.Contains("email: Email | null;", result);
    }
}
