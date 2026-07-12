using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// FABLE_ROUNDTRIP #6/#11b — the required/nullable axes are independent and
/// both survive import→emit. A component carrying its own nullability
/// (3.0 `nullable: true` / 3.1 null type) makes every bare $ref use-site
/// nullable; a property that is required AND nullable scaffolds as
/// `required T?` (non-positional), the one C# form expressing both.
/// </summary>
public sealed class NullableComponentImportTests
{
    private const string Schemas = """
        "nullable-owner": {
            "type": "object",
            "nullable": true,
            "properties": { "id": { "type": "integer" } }
        },
        "ItemDto": {
            "type": "object",
            "required": ["owner", "label"],
            "properties": {
                "owner": { "$ref": "#/components/schemas/nullable-owner" },
                "label": { "type": "string", "nullable": true },
                "note": { "$ref": "#/components/schemas/nullable-owner" }
            }
        }
        """;

    private const string Paths = """
        "/api/items": {
            "get": {
                "operationId": "ListItems",
                "responses": {
                    "200": {
                        "description": "Success",
                        "content": {
                            "application/json": {
                                "schema": { "$ref": "#/components/schemas/ItemDto" }
                            }
                        }
                    }
                }
            }
        }
        """;

    [Fact]
    public void Nullable_Component_Refs_And_Required_Nullable_Props_Scaffold_Faithfully()
    {
        var result = CompilationHelper.Import(
            CompilationHelper.BuildSpec(schemas: Schemas, paths: Paths, title: "API")
        );
        var item = CompilationHelper.FindFile(result, "ItemDto.cs");

        // required + nullable — only `required T?` says both
        Assert.Contains("required NullableOwner? Owner", item);
        Assert.Contains("required string? Label", item);
        // optional + nullable-component ref — nullable, not required
        Assert.Contains("NullableOwner? Note", item);
        Assert.DoesNotContain("required NullableOwner? Note", item);
    }

    [Fact]
    public void Required_And_Nullable_Both_Survive_To_The_Emitted_Spec()
    {
        var result = CompilationHelper.Import(
            CompilationHelper.BuildSpec(schemas: Schemas, paths: Paths, title: "API")
        );
        var compilation = CompilationHelper.CompileImportResult(result);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var emitted = JsonSerializer.Deserialize<JsonElement>(
            Rivet.Tool.Emit.OpenApiEmitter.Emit(
                endpoints,
                walker.Definitions,
                walker.Brands,
                walker.Enums,
                null
            )
        );

        var item = emitted.GetProperty("components").GetProperty("schemas").GetProperty("ItemDto");
        var required = item.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("owner", required);
        Assert.Contains("label", required);
        Assert.DoesNotContain("note", required);

        // owner: nullable $ref — a null branch beside the component ref
        var owner = item.GetProperty("properties").GetProperty("owner");
        Assert.True(owner.TryGetProperty("oneOf", out var oneOf));
        Assert.Contains(
            oneOf.EnumerateArray(),
            b => b.TryGetProperty("type", out var t) && t.GetString() == "null"
        );
    }
}
