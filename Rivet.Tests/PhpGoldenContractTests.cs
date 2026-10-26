using System.Text.Json;
using Rivet.Tool.Emit;

namespace Rivet.Tests;

/// <summary>
/// Pins the .NET side of the rivet-php pipeline against real rivet-php output: the fixture is
/// rivet-php's <c>samples/basic/generated/api.contract.json</c>, copied as generated (regenerate
/// it there with <c>composer rivet:generate</c>, never edit it by hand). The sample is small (two
/// DTOs, two endpoints, one secured and one anonymous), so this proves the strict reader accepts
/// what rivet-php writes for that much and that its security reaches the operations, not that
/// every property rivet-php can write gets through.
/// </summary>
public sealed class PhpGoldenContractTests
{
    private static readonly JsonElement _document = JsonDocument
        .Parse(
            CompilationHelper.EmitOpenApiFromJson(
                Fixture.Text("php-golden-contract.json"),
                security: SecurityParser.ParseMany(["bearer"])
            )
        )
        .RootElement.Clone();

    private static JsonElement Operation(string path, string method) =>
        _document.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static string? SchemaRef(JsonElement content) =>
        content
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();

    [Fact]
    public void Dtos_Emit_As_Components()
    {
        var schemas = _document.GetProperty("components").GetProperty("schemas");

        Assert.Equal(
            "string",
            schemas
                .GetProperty("CreateMember")
                .GetProperty("properties")
                .GetProperty("email")
                .GetProperty("type")
                .GetString()
        );
        Assert.Equal(
            ["id", "email"],
            schemas
                .GetProperty("Member")
                .GetProperty("required")
                .EnumerateArray()
                .Select(name => name.GetString())
        );
    }

    [Fact]
    public void Body_And_Route_Params_Reach_The_Operations()
    {
        var create = Operation("/members", "post");
        Assert.Equal(
            "#/components/schemas/CreateMember",
            SchemaRef(create.GetProperty("requestBody").GetProperty("content"))
        );
        Assert.Equal(
            "#/components/schemas/Member",
            SchemaRef(create.GetProperty("responses").GetProperty("201").GetProperty("content"))
        );

        var get = Operation("/members/{id}", "get");
        var id = Assert.Single(get.GetProperty("parameters").EnumerateArray());
        Assert.Equal("path", id.GetProperty("in").GetString());
        Assert.True(id.GetProperty("required").GetBoolean());
        Assert.True(get.GetProperty("responses").TryGetProperty("404", out _));
    }

    [Fact]
    public void Secure_And_Anonymous_Reach_The_Operations()
    {
        // #[Secure('bearer')] is one alternative needing bearer; #[Anonymous] is security: [].
        var create = Operation("/members", "post").GetProperty("security");
        var alternative = Assert.Single(create.EnumerateArray());
        Assert.Empty(alternative.GetProperty("bearer").EnumerateArray());

        Assert.Empty(Operation("/members/{id}", "get").GetProperty("security").EnumerateArray());
    }
}
