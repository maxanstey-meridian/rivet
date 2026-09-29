using System.Text.Json;
using System.Text.Json.Nodes;
using Rivet.Tool;

namespace Rivet.Tests;

/// <summary>
/// RFC 6901 pointer handling across import and emit (BUG-10): component names with
/// '/', '~' or percent-encoded characters, and pointers through array indices.
/// </summary>
public sealed class JsonPointerTests
{
    [Theory]
    [InlineData("a/b~c", "a~1b~0c")]
    [InlineData("~1", "~01")]
    [InlineData("plain", "plain")]
    public void Escape_And_Unescape_Round_Trip(string token, string escaped)
    {
        Assert.Equal(escaped, JsonPointer.Escape(token));
        Assert.Equal(token, JsonPointer.Unescape(escaped));
    }

    [Fact]
    public void Fragment_Is_Percent_Decoded_Before_Splitting()
    {
        Assert.Equal(
            ["components", "schemas", "a/b c~d"],
            JsonPointer.FromUriFragment("#/components/schemas/a~1b%20c~0d")
        );
        Assert.Equal(
            ["components", "schemas", "a", "b"],
            JsonPointer.FromUriFragment("#/components/schemas/a%2Fb")
        );
        Assert.Empty(JsonPointer.FromUriFragment("#")!);
        Assert.Null(JsonPointer.FromUriFragment("other.json#/a"));
    }

    [Theory]
    [InlineData("#/components/schemas/a~1b", "schemas", "a/b")]
    [InlineData("#/components/schemas/a%2Fb", "schemas", null)]
    [InlineData("#/components/schemas/a/b", "schemas", null)]
    [InlineData("#/components/examples/a", "schemas", null)]
    public void Component_Name_Is_A_Single_Decoded_Token(
        string reference,
        string kind,
        string? expected
    )
    {
        Assert.Equal(
            expected is not null,
            JsonPointer.TryGetComponentName(reference, kind, out var name)
        );
        Assert.Equal(expected, name);
    }

    [Fact]
    public void Resolves_Through_Array_Indices_In_Nodes_And_Elements()
    {
        const string json = """{ "a": [ { "b~/": 1 }, { "c": 2 } ] }""";
        var node = JsonNode.Parse(json)!;
        using var document = JsonDocument.Parse(json);

        Assert.True(JsonPointer.TryResolve(node, "#/a/1/c", out var fromNode));
        Assert.Equal(2, fromNode.GetValue<int>());
        Assert.True(
            JsonPointer.TryResolve(document.RootElement, "#/a/0/b~0~1", out var fromElement)
        );
        Assert.Equal(1, fromElement.GetInt32());
        Assert.False(JsonPointer.TryResolve(node, "#/a/2", out _));
        Assert.False(JsonPointer.TryResolve(node, "#/a/01", out _));
        Assert.False(JsonPointer.TryResolve(document.RootElement, "#/a/c", out _));
    }

    [Fact]
    public void Component_Example_Ref_Escapes_Slash_And_Tilde()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record ItemDto(string Id);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly Define GetItem =
                    Define.Get<ItemDto>("/api/items")
                        .ResponseExampleRef(200, "ex/one~two", "{\"id\":\"1\"}", name: "default");
            }
            """;

        using var document = CompilationHelper.EmitOpenApi(source);
        var root = document.RootElement;

        Assert.Equal(
            "#/components/examples/ex~1one~0two",
            root.GetProperty("paths")
                .GetProperty("/api/items")
                .GetProperty("get")
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("examples")
                .GetProperty("default")
                .GetProperty("$ref")
                .GetString()
        );
        Assert.True(
            root.GetProperty("components")
                .GetProperty("examples")
                .TryGetProperty("ex/one~two", out _)
        );
    }

    [Fact]
    public void Percent_Encoded_Schema_Ref_Resolves_To_The_Named_Component()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Pet Name": { "type": "object", "properties": { "value": { "type": "string" } } },
            "Holder": {
              "type": "object",
              "required": ["pet"],
              "properties": { "pet": { "$ref": "#/components/schemas/Pet%20Name" } }
            }
            """
        );

        var result = CompilationHelper.Import(spec);
        var holder = CompilationHelper.FindFile(result, "Holder.cs");

        Assert.Contains("PetName Pet", holder);
    }

    [Fact]
    public void Swagger_Body_Parameter_Ref_Through_An_Array_Index_Resolves()
    {
        var spec = """
            {
              "swagger": "2.0",
              "info": { "title": "t", "version": "1" },
              "paths": {
                "/a": {
                  "post": {
                    "operationId": "postA",
                    "parameters": [
                      { "in": "body", "name": "body", "description": "the A body", "schema": { "type": "string" } }
                    ],
                    "responses": { "204": { "description": "ok" } }
                  }
                },
                "/b": {
                  "post": {
                    "operationId": "postB",
                    "parameters": [ { "$ref": "#/paths/~1a/post/parameters/0" } ],
                    "responses": { "204": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var result = CompilationHelper.Import(spec);

        Assert.NotEmpty(result.Files);
    }
}
