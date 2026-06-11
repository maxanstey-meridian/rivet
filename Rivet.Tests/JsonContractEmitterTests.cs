using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// Tests that the OpenAPI emitter produces a correct spec from a hand-authored
/// contract JSON file — validating the full JSON → OpenAPI path (the --from pipeline).
/// </summary>
public sealed class JsonContractEmitterTests
{
    private const string ContractJson = """
        {
            "types": [
                {
                    "name": "ProductDto",
                    "typeParameters": [],
                    "properties": [
                        { "name": "id", "type": { "kind": "primitive", "type": "number", "format": "int32" }, "optional": false },
                        { "name": "title", "type": { "kind": "primitive", "type": "string" }, "optional": false },
                        { "name": "price", "type": { "kind": "primitive", "type": "number", "format": "double" }, "optional": false },
                        { "name": "status", "type": { "kind": "ref", "name": "ProductStatus" }, "optional": false }
                    ]
                },
                {
                    "name": "CreateProductRequest",
                    "typeParameters": [],
                    "properties": [
                        { "name": "title", "type": { "kind": "primitive", "type": "string" }, "optional": false },
                        { "name": "price", "type": { "kind": "primitive", "type": "number", "format": "double" }, "optional": false }
                    ]
                }
            ],
            "enums": [
                { "name": "ProductStatus", "values": ["active", "draft", "archived"] }
            ],
            "endpoints": [
                {
                    "name": "getProduct",
                    "httpMethod": "GET",
                    "routeTemplate": "/products/{id}",
                    "controllerName": "product",
                    "params": [
                        {
                            "name": "id",
                            "type": { "kind": "primitive", "type": "number", "format": "int32" },
                            "source": "route"
                        }
                    ],
                    "returnType": { "kind": "ref", "name": "ProductDto" },
                    "responses": [
                        { "statusCode": 200, "dataType": { "kind": "ref", "name": "ProductDto" } }
                    ]
                },
                {
                    "name": "createProduct",
                    "httpMethod": "POST",
                    "routeTemplate": "/products",
                    "controllerName": "product",
                    "params": [
                        {
                            "name": "body",
                            "type": { "kind": "ref", "name": "CreateProductRequest" },
                            "source": "body"
                        }
                    ],
                    "returnType": { "kind": "ref", "name": "ProductDto" },
                    "responses": [
                        { "statusCode": 201, "dataType": { "kind": "ref", "name": "ProductDto" } }
                    ]
                },
                {
                    "name": "listProducts",
                    "httpMethod": "GET",
                    "routeTemplate": "/products",
                    "controllerName": "product",
                    "params": [
                        {
                            "name": "status",
                            "type": { "kind": "primitive", "type": "string" },
                            "source": "query"
                        }
                    ],
                    "returnType": { "kind": "array", "element": { "kind": "ref", "name": "ProductDto" } },
                    "responses": [
                        { "statusCode": 200, "dataType": { "kind": "array", "element": { "kind": "ref", "name": "ProductDto" } } }
                    ]
                }
            ]
        }
        """;

    [Fact]
    public void OpenApiEmitter_FromJson_EmitsSpec()
    {
        var spec = CompilationHelper.EmitOpenApiFromJson(ContractJson);

        using var doc = JsonDocument.Parse(spec);
        var root = doc.RootElement;

        Assert.Equal("3.1.0", root.GetProperty("openapi").GetString());

        var paths = root.GetProperty("paths");

        // GET /products/{id} with an int32 route param and a 200 ProductDto response
        var getOp = paths.GetProperty("/products/{id}").GetProperty("get");
        var idParam = Assert.Single(getOp.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "id");
        Assert.Equal("path", idParam.GetProperty("in").GetString());
        Assert.True(idParam.GetProperty("required").GetBoolean());
        var get200 = getOp.GetProperty("responses").GetProperty("200");
        Assert.Equal("#/components/schemas/ProductDto",
            get200.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());

        // POST /products with a CreateProductRequest body and 201 response
        var postOp = paths.GetProperty("/products").GetProperty("post");
        Assert.Equal("#/components/schemas/CreateProductRequest",
            postOp.GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema")
                .GetProperty("$ref").GetString());
        Assert.True(postOp.GetProperty("responses").TryGetProperty("201", out _));

        // GET /products with a query param and an array-of-ProductDto response
        var listOp = paths.GetProperty("/products").GetProperty("get");
        var statusParam = Assert.Single(listOp.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "status");
        Assert.Equal("query", statusParam.GetProperty("in").GetString());
        var list200Schema = listOp.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("array", list200Schema.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/ProductDto",
            list200Schema.GetProperty("items").GetProperty("$ref").GetString());

        // Component schemas: DTOs and the enum
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var productDto = schemas.GetProperty("ProductDto");
        Assert.Equal("integer", productDto.GetProperty("properties").GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("string", productDto.GetProperty("properties").GetProperty("title").GetProperty("type").GetString());
        Assert.True(schemas.TryGetProperty("CreateProductRequest", out _));
        var statusSchema = schemas.GetProperty("ProductStatus");
        Assert.Equal(new[] { "active", "draft", "archived" },
            statusSchema.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
    }
}
