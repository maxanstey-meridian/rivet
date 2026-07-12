using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Brand (value-object) detection is TypeWalker behavior — asserted against the walker's
/// brand model and the brand representation in emitted OpenAPI (both survive the pivot).
/// </summary>
public sealed class ValueObjectTests
{
    private static JsonElement GetSchema(JsonDocument doc, string name) =>
        doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);

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

        var (_, walker) = CompilationHelper.WalkContract(source);

        // Email is detected as a brand with string inner
        var brand = Assert.Contains("Email", walker.Brands);
        var inner = Assert.IsType<TsType.Primitive>(brand.Inner);
        Assert.Equal("string", inner.Name);

        // Email is NOT walked as an object type definition
        Assert.False(walker.Definitions.ContainsKey("Email"));

        // The consuming property references the brand
        var userDto = walker.Definitions["UserDto"];
        var emailProp = Assert.Single(userDto.Properties, p => p.Name == "email");
        var propBrand = Assert.IsType<TsType.Brand>(emailProp.Type);
        Assert.Equal("Email", propBrand.Name);

        // OpenAPI: brand becomes a string schema tagged x-rivet-brand, referenced via $ref
        using var doc = CompilationHelper.EmitOpenApi(source);
        var emailSchema = GetSchema(doc, "Email");
        Assert.Equal("string", emailSchema.GetProperty("type").GetString());
        Assert.Equal("Email", emailSchema.GetProperty("x-rivet-brand").GetString());
        Assert.False(emailSchema.TryGetProperty("properties", out _));

        var userSchema = GetSchema(doc, "UserDto");
        Assert.Equal(
            "#/components/schemas/Email",
            userSchema
                .GetProperty("properties")
                .GetProperty("email")
                .GetProperty("$ref")
                .GetString()
        );
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

        var (_, walker) = CompilationHelper.WalkContract(source);

        var brand = Assert.Contains("Quantity", walker.Brands);
        var inner = Assert.IsType<TsType.Primitive>(brand.Inner);
        Assert.Equal("number", inner.Name);

        var orderDto = walker.Definitions["OrderDto"];
        var qtyProp = Assert.Single(orderDto.Properties, p => p.Name == "qty");
        var propBrand = Assert.IsType<TsType.Brand>(qtyProp.Type);
        Assert.Equal("Quantity", propBrand.Name);

        using var doc = CompilationHelper.EmitOpenApi(source);
        var quantitySchema = GetSchema(doc, "Quantity");
        Assert.Equal("integer", quantitySchema.GetProperty("type").GetString());
        Assert.Equal("Quantity", quantitySchema.GetProperty("x-rivet-brand").GetString());

        var orderSchema = GetSchema(doc, "OrderDto");
        Assert.Equal(
            "#/components/schemas/Quantity",
            orderSchema.GetProperty("properties").GetProperty("qty").GetProperty("$ref").GetString()
        );
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

        var (_, walker) = CompilationHelper.WalkContract(source);

        // ToString override does not defeat VO detection
        var brand = Assert.Contains("Uprn", walker.Brands);
        var inner = Assert.IsType<TsType.Primitive>(brand.Inner);
        Assert.Equal("string", inner.Name);

        var propertyDto = walker.Definitions["PropertyDto"];
        var uprnProp = Assert.Single(propertyDto.Properties, p => p.Name == "uprn");
        var propBrand = Assert.IsType<TsType.Brand>(uprnProp.Type);
        Assert.Equal("Uprn", propBrand.Name);

        using var doc = CompilationHelper.EmitOpenApi(source);
        var uprnSchema = GetSchema(doc, "Uprn");
        Assert.Equal("string", uprnSchema.GetProperty("type").GetString());
        Assert.Equal("Uprn", uprnSchema.GetProperty("x-rivet-brand").GetString());
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

        var (_, walker) = CompilationHelper.WalkContract(source);

        // Money is a full object type definition, not a brand
        Assert.Empty(walker.Brands);
        var money = walker.Definitions["Money"];
        var amountProp = Assert.Single(money.Properties, p => p.Name == "amount");
        Assert.Equal("number", Assert.IsType<TsType.Primitive>(amountProp.Type).Name);
        var currencyProp = Assert.Single(money.Properties, p => p.Name == "currency");
        Assert.Equal("string", Assert.IsType<TsType.Primitive>(currencyProp.Type).Name);

        var productDto = walker.Definitions["ProductDto"];
        var priceProp = Assert.Single(productDto.Properties, p => p.Name == "price");
        Assert.Equal("Money", Assert.IsType<TsType.TypeRef>(priceProp.Type).Name);

        using var doc = CompilationHelper.EmitOpenApi(source);
        var moneySchema = GetSchema(doc, "Money");
        Assert.Equal("object", moneySchema.GetProperty("type").GetString());
        Assert.True(moneySchema.GetProperty("properties").TryGetProperty("amount", out _));
        Assert.True(moneySchema.GetProperty("properties").TryGetProperty("currency", out _));
        Assert.False(moneySchema.TryGetProperty("x-rivet-brand", out _));
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

        var (_, walker) = CompilationHelper.WalkContract(source);

        // Single property but not named "Value" — object type, not a brand
        Assert.Empty(walker.Brands);
        var wrapper = walker.Definitions["Wrapper"];
        var contentProp = Assert.Single(wrapper.Properties, p => p.Name == "content");
        Assert.Equal("string", Assert.IsType<TsType.Primitive>(contentProp.Type).Name);

        using var doc = CompilationHelper.EmitOpenApi(source);
        var wrapperSchema = GetSchema(doc, "Wrapper");
        Assert.Equal("object", wrapperSchema.GetProperty("type").GetString());
        Assert.True(wrapperSchema.GetProperty("properties").TryGetProperty("content", out _));
        Assert.False(wrapperSchema.TryGetProperty("x-rivet-brand", out _));
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

        var (_, walker) = CompilationHelper.WalkContract(source);

        var brand = Assert.Contains("Email", walker.Brands);
        Assert.Equal("string", Assert.IsType<TsType.Primitive>(brand.Inner).Name);

        // Nullable wrapping preserved around the brand in the model
        var contactDto = walker.Definitions["ContactDto"];
        var emailProp = Assert.Single(contactDto.Properties, p => p.Name == "email");
        var nullable = Assert.IsType<TsType.Nullable>(emailProp.Type);
        var propBrand = Assert.IsType<TsType.Brand>(nullable.Inner);
        Assert.Equal("Email", propBrand.Name);

        // OpenAPI 3.1: nullable $ref → oneOf [$ref, { type: "null" }]; not required
        using var doc = CompilationHelper.EmitOpenApi(source);
        var contactSchema = GetSchema(doc, "ContactDto");
        var emailSchema = contactSchema.GetProperty("properties").GetProperty("email");
        var oneOf = emailSchema.GetProperty("oneOf");
        Assert.Equal(2, oneOf.GetArrayLength());
        Assert.Equal("#/components/schemas/Email", oneOf[0].GetProperty("$ref").GetString());
        Assert.Equal("null", oneOf[1].GetProperty("type").GetString());

        var required = contactSchema
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("name", required);
        Assert.DoesNotContain("email", required);
    }
}
