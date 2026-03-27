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
}
