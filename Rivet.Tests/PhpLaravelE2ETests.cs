using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// Pins the .NET side of the rivet-php pipeline against the hand-copied PHP8 golden
/// contract fixture: contract JSON → OpenAPI 3.1 (the tool's only output post-Phase-3).
/// </summary>
public sealed class PhpLaravelE2ETests
{
    private static readonly string _goldenJson = File.ReadAllText(
        Path.Combine("..", "..", "..", "Fixtures", "php-golden-contract.json")
    );

    private static readonly JsonElement _schemas = JsonDocument
        .Parse(CompilationHelper.EmitOpenApiFromJson(_goldenJson))
        .RootElement.GetProperty("components")
        .GetProperty("schemas");

    private static JsonElement Prop(string type, string prop) =>
        _schemas.GetProperty(type).GetProperty("properties").GetProperty(prop);

    private static void AssertNullable(JsonElement prop, string expectedType)
    {
        var types = prop.GetProperty("type").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(expectedType, types);
        Assert.Contains("null", types);
    }

    [Fact]
    public void ProductDto_Scalars()
    {
        Assert.True(_schemas.TryGetProperty("ProductDto", out _));
        Assert.Equal("string", Prop("ProductDto", "title").GetProperty("type").GetString());
        Assert.Equal("integer", Prop("ProductDto", "id").GetProperty("type").GetString());
        Assert.Equal("number", Prop("ProductDto", "price").GetProperty("type").GetString());
        Assert.Equal("boolean", Prop("ProductDto", "active").GetProperty("type").GetString());
    }

    [Fact]
    public void ProductDto_Nullable()
    {
        AssertNullable(Prop("ProductDto", "description"), "string");
    }

    [Fact]
    public void ProductDto_EnumRefs()
    {
        Assert.Equal(
            "#/components/schemas/ProductStatus",
            Prop("ProductDto", "status").GetProperty("$ref").GetString()
        );
        Assert.Equal(
            "#/components/schemas/Priority",
            Prop("ProductDto", "priority").GetProperty("$ref").GetString()
        );
    }

    [Fact]
    public void ProductDto_NestedRef()
    {
        Assert.Equal(
            "#/components/schemas/UserDto",
            Prop("ProductDto", "author").GetProperty("$ref").GetString()
        );
    }

    [Fact]
    public void ProductDto_Array()
    {
        var tags = Prop("ProductDto", "tags");
        Assert.Equal("array", tags.GetProperty("type").GetString());
        Assert.Equal("string", tags.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void ProductDto_Dictionary()
    {
        var metadata = Prop("ProductDto", "metadata");
        Assert.Equal("object", metadata.GetProperty("type").GetString());
        Assert.Equal(
            "integer",
            metadata.GetProperty("additionalProperties").GetProperty("type").GetString()
        );
    }

    [Fact]
    public void ProductDto_InlineObject()
    {
        var dimensions = Prop("ProductDto", "dimensions");
        Assert.Equal("object", dimensions.GetProperty("type").GetString());
        // fixture declares width/height as int32 → OpenAPI "integer"
        Assert.Equal(
            "integer",
            dimensions
                .GetProperty("properties")
                .GetProperty("width")
                .GetProperty("type")
                .GetString()
        );
        Assert.Equal(
            "integer",
            dimensions
                .GetProperty("properties")
                .GetProperty("height")
                .GetProperty("type")
                .GetString()
        );
    }

    [Fact]
    public void ProductDto_StringUnion()
    {
        Assert.Equal(
            new[] { "small", "medium", "large" },
            Prop("ProductDto", "size")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(e => e.GetString())
                .ToArray()
        );
    }

    [Fact]
    public void ProductDto_IntUnion()
    {
        Assert.Equal(
            new[] { 1, 2, 3 },
            Prop("ProductDto", "rating")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(e => e.GetInt32())
                .ToArray()
        );
    }

    [Fact]
    public void StringEnum_Emits_Enum_Component()
    {
        Assert.Equal(
            new[] { "active", "draft", "archived" },
            _schemas
                .GetProperty("ProductStatus")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(e => e.GetString())
                .ToArray()
        );
    }

    [Fact]
    public void IntEnum_Emits_Enum_Component()
    {
        Assert.Equal(
            new[] { 1, 2, 3 },
            _schemas
                .GetProperty("Priority")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(e => e.GetInt32())
                .ToArray()
        );
    }

    [Fact]
    public void UserDto_Emits()
    {
        Assert.Equal("string", Prop("UserDto", "name").GetProperty("type").GetString());
        AssertNullable(Prop("UserDto", "email"), "string");
        Assert.Equal(
            "#/components/schemas/AddressDto",
            Prop("UserDto", "address").GetProperty("$ref").GetString()
        );
    }

    [Fact]
    public void AddressDto_Emits()
    {
        Assert.Equal("string", Prop("AddressDto", "street").GetProperty("type").GetString());
        Assert.Equal("string", Prop("AddressDto", "city").GetProperty("type").GetString());
    }

    [Fact]
    public void ProductFilterDto_Emits()
    {
        Assert.True(_schemas.TryGetProperty("ProductFilterDto", out _));
    }

    [Fact]
    public void ProductFilterDto_ArrayOfEnum_Emits()
    {
        var priorities = Prop("ProductFilterDto", "priorities");
        Assert.Equal("array", priorities.GetProperty("type").GetString());
        Assert.Equal(
            "#/components/schemas/Priority",
            priorities.GetProperty("items").GetProperty("$ref").GetString()
        );
    }

    [Fact]
    public void Endpoints_RoundTrip_FromGoldenJson()
    {
        using var doc = JsonDocument.Parse(_goldenJson);
        var endpoints = doc.RootElement.GetProperty("endpoints");

        Assert.Equal(6, endpoints.GetArrayLength());

        var names = new List<string>();
        var routes = new List<string>();
        var methods = new List<string>();

        foreach (var ep in endpoints.EnumerateArray())
        {
            names.Add(ep.GetProperty("name").GetString()!);
            routes.Add(ep.GetProperty("routeTemplate").GetString()!);
            methods.Add(ep.GetProperty("httpMethod").GetString()!);
        }

        Assert.Contains("show", names);
        Assert.Contains("store", names);
        Assert.Contains("index", names);
        Assert.Contains("destroy", names);
        Assert.Contains("paginated", names);

        Assert.Contains("/products/{id}", routes);
        Assert.Contains("/products", routes);
        Assert.Contains("/products/paginated", routes);
        Assert.Contains("/users/{id}", routes);

        Assert.Contains("GET", methods);
        Assert.Contains("POST", methods);
        Assert.Contains("DELETE", methods);
    }

    [Fact]
    public void Endpoints_CorrectControllerNames()
    {
        using var doc = JsonDocument.Parse(_goldenJson);
        var endpoints = doc.RootElement.GetProperty("endpoints");

        var controllers = new List<string>();
        foreach (var ep in endpoints.EnumerateArray())
        {
            controllers.Add(ep.GetProperty("controllerName").GetString()!);
        }

        Assert.Equal(5, controllers.Count(c => c == "product"));
        Assert.Equal(1, controllers.Count(c => c == "user"));
    }

    [Fact]
    public void Endpoints_ParamSources_Correct()
    {
        using var doc = JsonDocument.Parse(_goldenJson);
        var endpoints = doc.RootElement.GetProperty("endpoints");

        // Find the store endpoint (POST /products) — should have body param
        foreach (var ep in endpoints.EnumerateArray())
        {
            if (ep.GetProperty("name").GetString() == "store")
            {
                var param = ep.GetProperty("params")[0];
                Assert.Equal("body", param.GetProperty("source").GetString());
                Assert.Equal("payload", param.GetProperty("name").GetString());
                return;
            }
        }

        Assert.Fail("store endpoint not found");
    }
}
