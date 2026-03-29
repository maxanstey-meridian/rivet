using System.Text.Json;

namespace Rivet.Tests;

public sealed class PhpLaravelE2ETests
{
    private static readonly string GoldenJson = File.ReadAllText(
        Path.Combine("..", "..", "..", "..", "php-reflector", "tests", "Integration", "SampleApp", "golden-contract.json"));

    private static readonly string Ts = CompilationHelper.EmitTypesFromJson(GoldenJson);

    [Fact]
    public void ProductDto_Scalars()
    {
        Assert.Contains("export type ProductDto = {", Ts);
        Assert.Contains("  title: string;", Ts);
        Assert.Contains("  id: number;", Ts);
        Assert.Contains("  price: number;", Ts);
        Assert.Contains("  active: boolean;", Ts);
    }

    [Fact]
    public void ProductDto_Nullable()
    {
        Assert.Contains("  description: string | null;", Ts);
    }

    [Fact]
    public void ProductDto_EnumRefs()
    {
        Assert.Contains("  status: ProductStatus;", Ts);
        Assert.Contains("  priority: Priority;", Ts);
    }

    [Fact]
    public void ProductDto_NestedRef()
    {
        Assert.Contains("  author: UserDto;", Ts);
    }

    [Fact]
    public void ProductDto_Array()
    {
        Assert.Contains("  tags: string[];", Ts);
    }

    [Fact]
    public void ProductDto_Dictionary()
    {
        Assert.Contains("  metadata: Record<string, number>;", Ts);
    }

    [Fact]
    public void ProductDto_InlineObject()
    {
        Assert.Contains("dimensions: { width: number; height: number; };", Ts);
    }

    [Fact]
    public void ProductDto_StringUnion()
    {
        Assert.Contains("  size: \"small\" | \"medium\" | \"large\";", Ts);
    }

    [Fact]
    public void ProductDto_IntUnion()
    {
        Assert.Contains("  rating: 1 | 2 | 3;", Ts);
    }

    [Fact]
    public void StringEnum_Emits_Union()
    {
        Assert.Contains("export type ProductStatus = \"active\" | \"draft\" | \"archived\";", Ts);
    }

    [Fact]
    public void IntEnum_Emits_Union()
    {
        Assert.Contains("export type Priority = 1 | 2 | 3;", Ts);
    }

    [Fact]
    public void UserDto_Emits()
    {
        Assert.Contains("export type UserDto = {", Ts);
        Assert.Contains("  name: string;", Ts);
        Assert.Contains("  email: string | null;", Ts);
        Assert.Contains("  address: AddressDto;", Ts);
    }

    [Fact]
    public void AddressDto_Emits()
    {
        Assert.Contains("export type AddressDto = {", Ts);
        Assert.Contains("  street: string;", Ts);
        Assert.Contains("  city: string;", Ts);
    }

    [Fact]
    public void ProductFilterDto_Emits()
    {
        Assert.Contains("export type ProductFilterDto = {", Ts);
    }

    [Fact]
    public void ProductFilterDto_ArrayOfEnum_Emits()
    {
        Assert.Contains("  priorities: Priority[];", Ts);
    }

    [Fact]
    public void Endpoints_RoundTrip_FromGoldenJson()
    {
        using var doc = JsonDocument.Parse(GoldenJson);
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
        using var doc = JsonDocument.Parse(GoldenJson);
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
        using var doc = JsonDocument.Parse(GoldenJson);
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
