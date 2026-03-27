using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Tests that all 4 emitters (Type, Client, Zod, OpenApi) produce correct output
/// from a hand-authored contract JSON file — validating the full JSON → emitter path.
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
    public void TypeEmitter_FromJson_EmitsTypes()
    {
        var ts = CompilationHelper.EmitTypesFromJson(ContractJson);

        Assert.Contains("export type ProductDto = {", ts);
        Assert.Contains("  id: number;", ts);
        Assert.Contains("  title: string;", ts);
        Assert.Contains("  status: ProductStatus;", ts);
        Assert.Contains("export type ProductStatus = \"active\" | \"draft\" | \"archived\";", ts);
        Assert.Contains("export type CreateProductRequest = {", ts);
    }

    [Fact]
    public void ClientEmitter_FromJson_EmitsClientFunctions()
    {
        var client = CompilationHelper.EmitClientFromJson(ContractJson);

        Assert.Contains("getProduct", client);
        Assert.Contains("createProduct", client);
        Assert.Contains("listProducts", client);
        Assert.Contains("/products/", client);
        Assert.Contains("GET", client);
        Assert.Contains("POST", client);
    }

    [Fact]
    public void OpenApiEmitter_FromJson_EmitsSpec()
    {
        var spec = CompilationHelper.EmitOpenApiFromJson(ContractJson);

        Assert.Contains("\"openapi\"", spec);
        Assert.Contains("\"3.0.3\"", spec);
        Assert.Contains("/products/{id}", spec);
        Assert.Contains("/products", spec);
        Assert.Contains("ProductDto", spec);
        Assert.Contains("CreateProductRequest", spec);
    }

    [Fact]
    public void ZodValidatorEmitter_FromJson_EmitsValidators()
    {
        var validators = CompilationHelper.EmitZodFromJson(ContractJson);

        Assert.Contains("assertProductDto", validators);
    }
}
