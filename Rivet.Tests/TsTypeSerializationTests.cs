using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class TsTypeSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new TsTypeJsonConverter() },
    };

    [Fact]
    public void Primitive_Serializes_With_Kind_And_Type()
    {
        var type = new TsType.Primitive("string");
        var json = JsonSerializer.Serialize<TsType>(type, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("primitive", root.GetProperty("kind").GetString());
        Assert.Equal("string", root.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("format", out _));
        Assert.False(root.TryGetProperty("csharpType", out _));
    }

    [Fact]
    public void Primitive_With_Format_And_CSharpType_Includes_All_Fields()
    {
        var type = new TsType.Primitive("string", "uuid", "Guid");
        var json = JsonSerializer.Serialize<TsType>(type, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("primitive", root.GetProperty("kind").GetString());
        Assert.Equal("string", root.GetProperty("type").GetString());
        Assert.Equal("uuid", root.GetProperty("format").GetString());
        Assert.Equal("Guid", root.GetProperty("csharpType").GetString());
    }

    [Fact]
    public void Primitive_RoundTrips()
    {
        var original = new TsType.Primitive("string", "uuid", "Guid");
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        var result = JsonSerializer.Deserialize<TsType>(json, Options);
        Assert.Equal(original, result);
    }

    [Fact]
    public void Nullable_Serializes_With_Inner()
    {
        var type = new TsType.Nullable(new TsType.Primitive("string"));
        var json = JsonSerializer.Serialize<TsType>(type, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("nullable", root.GetProperty("kind").GetString());
        var inner = root.GetProperty("inner");
        Assert.Equal("primitive", inner.GetProperty("kind").GetString());
        Assert.Equal("string", inner.GetProperty("type").GetString());
    }

    [Fact]
    public void Nullable_RoundTrips()
    {
        var original = new TsType.Nullable(new TsType.Primitive("number"));
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        var result = JsonSerializer.Deserialize<TsType>(json, Options);
        Assert.Equal(original, result);
    }

    [Fact]
    public void Array_Serializes_With_Element()
    {
        var type = new TsType.Array(new TsType.Primitive("number"));
        var json = JsonSerializer.Serialize<TsType>(type, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("array", root.GetProperty("kind").GetString());
        var element = root.GetProperty("element");
        Assert.Equal("primitive", element.GetProperty("kind").GetString());
    }

    [Fact]
    public void Array_RoundTrips()
    {
        var original = new TsType.Array(new TsType.Primitive("number"));
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        var result = JsonSerializer.Deserialize<TsType>(json, Options);
        Assert.Equal(original, result);
    }

    [Fact]
    public void Dictionary_RoundTrips()
    {
        var original = new TsType.Dictionary(new TsType.Primitive("boolean"));
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("dictionary", doc.RootElement.GetProperty("kind").GetString());
        Assert.True(doc.RootElement.TryGetProperty("value", out _));

        var result = JsonSerializer.Deserialize<TsType>(json, Options);
        Assert.Equal(original, result);
    }

    [Fact]
    public void StringUnion_Serializes_Members_As_Values()
    {
        var type = new TsType.StringUnion(["active", "inactive", "pending"]);
        var json = JsonSerializer.Serialize<TsType>(type, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("stringUnion", root.GetProperty("kind").GetString());
        Assert.False(root.TryGetProperty("members", out _));
        var values = root.GetProperty("values");
        Assert.Equal(3, values.GetArrayLength());
        Assert.Equal("active", values[0].GetString());
    }

    [Fact]
    public void StringUnion_RoundTrips()
    {
        var original = new TsType.StringUnion(["a", "b", "c"]);
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        var result = Assert.IsType<TsType.StringUnion>(JsonSerializer.Deserialize<TsType>(json, Options));
        Assert.Equal(original.Members, result.Members);
    }

    [Fact]
    public void TypeRef_RoundTrips()
    {
        var original = new TsType.TypeRef("UserDto");
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ref", doc.RootElement.GetProperty("kind").GetString());

        var result = JsonSerializer.Deserialize<TsType>(json, Options);
        Assert.Equal(original, result);
    }

    [Fact]
    public void Generic_Serializes_TypeArguments_As_TypeArgs()
    {
        var type = new TsType.Generic("PagedResult", [new TsType.TypeRef("UserDto")]);
        var json = JsonSerializer.Serialize<TsType>(type, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("generic", root.GetProperty("kind").GetString());
        Assert.Equal("PagedResult", root.GetProperty("name").GetString());
        Assert.False(root.TryGetProperty("typeArguments", out _));
        var typeArgs = root.GetProperty("typeArgs");
        Assert.Equal(1, typeArgs.GetArrayLength());
    }

    [Fact]
    public void Generic_RoundTrips()
    {
        var original = new TsType.Generic("Result", [new TsType.Primitive("string"), new TsType.TypeRef("Error")]);
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        var result = Assert.IsType<TsType.Generic>(JsonSerializer.Deserialize<TsType>(json, Options));
        Assert.Equal(original.Name, result.Name);
        Assert.Equal(original.TypeArguments, result.TypeArguments);
    }

    [Fact]
    public void TypeParam_RoundTrips()
    {
        var original = new TsType.TypeParam("T");
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("typeParam", doc.RootElement.GetProperty("kind").GetString());

        var result = JsonSerializer.Deserialize<TsType>(json, Options);
        Assert.Equal(original, result);
    }

    [Fact]
    public void Brand_Serializes_Inner_As_Underlying()
    {
        var type = new TsType.Brand("Email", new TsType.Primitive("string"));
        var json = JsonSerializer.Serialize<TsType>(type, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("brand", root.GetProperty("kind").GetString());
        Assert.Equal("Email", root.GetProperty("name").GetString());
        Assert.False(root.TryGetProperty("inner", out _));
        Assert.True(root.TryGetProperty("underlying", out _));
    }

    [Fact]
    public void Brand_RoundTrips()
    {
        var original = new TsType.Brand("Email", new TsType.Primitive("string"));
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        var result = JsonSerializer.Deserialize<TsType>(json, Options);
        Assert.Equal(original, result);
    }

    [Fact]
    public void InlineObject_Serializes_Fields_As_Properties()
    {
        var type = new TsType.InlineObject([("key", new TsType.Primitive("string")), ("value", new TsType.Primitive("number"))]);
        var json = JsonSerializer.Serialize<TsType>(type, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("inlineObject", root.GetProperty("kind").GetString());
        Assert.False(root.TryGetProperty("fields", out _));
        var props = root.GetProperty("properties");
        Assert.Equal(2, props.GetArrayLength());
        Assert.Equal("key", props[0].GetProperty("name").GetString());
        Assert.Equal("primitive", props[0].GetProperty("type").GetProperty("kind").GetString());
    }

    [Fact]
    public void InlineObject_RoundTrips()
    {
        var original = new TsType.InlineObject([("key", new TsType.Primitive("string")), ("value", new TsType.Primitive("number"))]);
        var json = JsonSerializer.Serialize<TsType>(original, Options);
        var result = Assert.IsType<TsType.InlineObject>(JsonSerializer.Deserialize<TsType>(json, Options));
        Assert.Equal(original.Fields, result.Fields);
    }

    [Fact]
    public void DeeplyNested_Type_RoundTrips()
    {
        var original = new TsType.Nullable(
            new TsType.Array(
                new TsType.Dictionary(
                    new TsType.Generic("Result", [
                        new TsType.Brand("Email", new TsType.Primitive("string")),
                        new TsType.TypeRef("Error"),
                    ]))));

        var json = JsonSerializer.Serialize<TsType>(original, Options);
        var result = JsonSerializer.Deserialize<TsType>(json, Options);

        // Unwrap and verify structure
        var nullable = Assert.IsType<TsType.Nullable>(result);
        var array = Assert.IsType<TsType.Array>(nullable.Inner);
        var dict = Assert.IsType<TsType.Dictionary>(array.Element);
        var generic = Assert.IsType<TsType.Generic>(dict.Value);
        Assert.Equal("Result", generic.Name);
        Assert.Equal(2, generic.TypeArguments.Count);
        var brand = Assert.IsType<TsType.Brand>(generic.TypeArguments[0]);
        Assert.Equal("Email", brand.Name);
        Assert.IsType<TsType.Primitive>(brand.Inner);
        Assert.IsType<TsType.TypeRef>(generic.TypeArguments[1]);
    }

    [Fact]
    public void PropertyDefinition_Serializes_With_CamelCase_And_Omits_Nulls()
    {
        var prop = new TsPropertyDefinition(
            "email",
            new TsType.Primitive("string"),
            IsOptional: true,
            Description: "User email",
            Constraints: new TsPropertyConstraints(MinLength: 1));

        var json = JsonSerializer.Serialize(prop, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("email", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("optional").GetBoolean());
        Assert.Equal("User email", root.GetProperty("description").GetString());
        Assert.Equal(1, root.GetProperty("constraints").GetProperty("minLength").GetInt32());
        // Boolean defaults should be omitted
        Assert.False(root.TryGetProperty("deprecated", out _));
        Assert.False(root.TryGetProperty("readOnly", out _));
        Assert.False(root.TryGetProperty("writeOnly", out _));
        // Null fields omitted
        Assert.False(root.TryGetProperty("format", out _));
        Assert.False(root.TryGetProperty("defaultValue", out _));
        Assert.False(root.TryGetProperty("example", out _));
    }

    [Fact]
    public void Constraints_Omits_Null_Fields()
    {
        var constraints = new TsPropertyConstraints(MinLength: 1, MaxLength: 100);
        var json = JsonSerializer.Serialize(constraints, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("minLength").GetInt32());
        Assert.Equal(100, root.GetProperty("maxLength").GetInt32());
        Assert.False(root.TryGetProperty("pattern", out _));
        Assert.False(root.TryGetProperty("minimum", out _));
        Assert.False(root.TryGetProperty("maximum", out _));
        Assert.False(root.TryGetProperty("exclusiveMinimum", out _));
        Assert.False(root.TryGetProperty("exclusiveMaximum", out _));
        Assert.False(root.TryGetProperty("multipleOf", out _));
        Assert.False(root.TryGetProperty("minItems", out _));
        Assert.False(root.TryGetProperty("maxItems", out _));
        Assert.False(root.TryGetProperty("uniqueItems", out _));
    }

    [Fact]
    public void TypeDefinition_Serializes_With_Properties_Containing_Types()
    {
        var def = new TsTypeDefinition("UserDto", [], [
            new TsPropertyDefinition("id", new TsType.Primitive("number"), false),
            new TsPropertyDefinition("name", new TsType.Primitive("string"), false),
        ]);

        var json = JsonSerializer.Serialize(def, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("UserDto", root.GetProperty("name").GetString());
        var props = root.GetProperty("properties");
        Assert.Equal(2, props.GetArrayLength());
        // Nested type should have kind discriminator
        Assert.Equal("primitive", props[0].GetProperty("type").GetProperty("kind").GetString());
    }

    [Fact]
    public void Deserialize_UnknownKind_Throws()
    {
        var json = """{"kind":"bogus"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TsType>(json, Options));
    }

    [Fact]
    public void Deserialize_MissingKind_Throws()
    {
        var json = """{"type":"string"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TsType>(json, Options));
    }
}
