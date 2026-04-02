using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class JsonContractReaderTests
{
    [Fact]
    public void Read_Returns_Endpoints_From_Contract_Json()
    {
        var json = """
            {
                "types": [
                    {
                        "name": "ProductDto",
                        "typeParameters": [],
                        "properties": [
                            { "name": "id", "type": { "kind": "primitive", "type": "number", "format": "int32" }, "optional": false },
                            { "name": "title", "type": { "kind": "primitive", "type": "string" }, "optional": false }
                        ]
                    }
                ],
                "enums": [],
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
                    }
                ]
            }
            """;

        var result = JsonContractReader.Read(json);

        Assert.Single(result.Endpoints);
        var ep = result.Endpoints[0];
        Assert.Equal("getProduct", ep.Name);
        Assert.Equal("GET", ep.HttpMethod);
        Assert.Equal("/products/{id}", ep.RouteTemplate);
        Assert.Equal("product", ep.ControllerName);
        Assert.Single(ep.Params);
        Assert.Equal("id", ep.Params[0].Name);
        Assert.Equal(ParamSource.Route, ep.Params[0].Source);
        Assert.IsType<TsType.TypeRef>(ep.ReturnType);
        Assert.Single(ep.Responses);
        Assert.Equal(200, ep.Responses[0].StatusCode);
    }

    [Fact]
    public void Read_Returns_Empty_Endpoints_When_None_Present()
    {
        var json = """
            {
                "types": [],
                "enums": [],
                "endpoints": []
            }
            """;

        var result = JsonContractReader.Read(json);

        Assert.Empty(result.Endpoints);
    }

    [Fact]
    public void RequestType_TypeRef_Deserializes()
    {
        var json = """
            {
                "types": [],
                "enums": [],
                "endpoints": [
                    {
                        "name": "createBuyer",
                        "httpMethod": "POST",
                        "routeTemplate": "/buyers",
                        "controllerName": "buyer",
                        "params": [],
                        "returnType": null,
                        "responses": [],
                        "requestType": { "kind": "ref", "name": "CreateBuyerRequest" }
                    }
                ]
            }
            """;

        var result = JsonContractReader.Read(json);

        var ep = Assert.Single(result.Endpoints);
        var typeRef = Assert.IsType<TsType.TypeRef>(ep.RequestType);
        Assert.Equal("CreateBuyerRequest", typeRef.Name);
    }

    [Fact]
    public void RequestType_InlineObject_Deserializes()
    {
        var json = """
            {
                "types": [],
                "enums": [],
                "endpoints": [
                    {
                        "name": "createBuyer",
                        "httpMethod": "POST",
                        "routeTemplate": "/buyers",
                        "controllerName": "buyer",
                        "params": [],
                        "returnType": null,
                        "responses": [],
                        "requestType": {
                            "kind": "inlineObject",
                            "properties": [
                                { "name": "id", "type": { "kind": "primitive", "type": "number", "format": "int32" }, "optional": false },
                                { "name": "name", "type": { "kind": "primitive", "type": "string" }, "optional": false }
                            ]
                        }
                    }
                ]
            }
            """;

        var result = JsonContractReader.Read(json);

        var ep = Assert.Single(result.Endpoints);
        var inlineObj = Assert.IsType<TsType.InlineObject>(ep.RequestType);
        Assert.Equal(2, inlineObj.Fields.Count);
        Assert.Equal("id", inlineObj.Fields[0].Name);
        Assert.IsType<TsType.Primitive>(inlineObj.Fields[0].Type);
        Assert.Equal("name", inlineObj.Fields[1].Name);
        Assert.IsType<TsType.Primitive>(inlineObj.Fields[1].Type);
    }

    [Fact]
    public void RequestType_Absent_IsNull()
    {
        var json = """
            {
                "types": [],
                "enums": [],
                "endpoints": [
                    {
                        "name": "getProduct",
                        "httpMethod": "GET",
                        "routeTemplate": "/products/{id}",
                        "controllerName": "product",
                        "params": [],
                        "returnType": null,
                        "responses": []
                    }
                ]
            }
            """;

        var result = JsonContractReader.Read(json);

        var ep = Assert.Single(result.Endpoints);
        Assert.Null(ep.RequestType);
    }

    [Fact]
    public void RequestType_Survives_RoundTrip()
    {
        var withRequestType = new TsEndpointDefinition(
            "createBuyer", "POST", "/buyers",
            Params: [],
            ReturnType: null,
            ControllerName: "buyer",
            Responses: [],
            RequestType: new TsType.TypeRef("CreateBuyerRequest"));

        var withoutRequestType = new TsEndpointDefinition(
            "getProduct", "GET", "/products/{id}",
            Params: [],
            ReturnType: null,
            ControllerName: "product",
            Responses: []);

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [withRequestType, withoutRequestType]);

        var result = JsonContractReader.Read(json);

        Assert.Equal(2, result.Endpoints.Count);

        var rt = Assert.IsType<TsType.TypeRef>(result.Endpoints[0].RequestType);
        Assert.Equal("CreateBuyerRequest", rt.Name);

        Assert.Null(result.Endpoints[1].RequestType);
    }

    [Fact]
    public void RequestType_InlineObject_Survives_RoundTrip()
    {
        var endpoint = new TsEndpointDefinition(
            "createBuyer", "POST", "/buyers",
            Params: [],
            ReturnType: null,
            ControllerName: "buyer",
            Responses: [],
            RequestType: new TsType.InlineObject([("id", new TsType.Primitive("number", "int32")), ("name", new TsType.Primitive("string"))]));

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]);

        var result = JsonContractReader.Read(json);

        var ep = Assert.Single(result.Endpoints);
        var inlineObj = Assert.IsType<TsType.InlineObject>(ep.RequestType);
        Assert.Equal(2, inlineObj.Fields.Count);
        Assert.Equal("id", inlineObj.Fields[0].Name);
        Assert.IsType<TsType.Primitive>(inlineObj.Fields[0].Type);
        Assert.Equal("name", inlineObj.Fields[1].Name);
        Assert.IsType<TsType.Primitive>(inlineObj.Fields[1].Type);
    }

    [Fact]
    public void Endpoint_Examples_Deserialize_With_Request_And_Response_Metadata()
    {
        var json = """
            {
                "types": [],
                "enums": [],
                "endpoints": [
                    {
                        "name": "createOrder",
                        "httpMethod": "POST",
                        "routeTemplate": "/orders",
                        "controllerName": "orders",
                        "params": [],
                        "returnType": null,
                        "requestExamples": [
                            {
                                "mediaType": "application/json",
                                "json": "{\"customerId\":\"cus_123\"}"
                            }
                        ],
                        "responses": [
                            {
                                "statusCode": 201,
                                "dataType": { "kind": "ref", "name": "CreateOrderResponse" },
                                "examples": [
                                    {
                                        "mediaType": "application/json",
                                        "name": "created",
                                        "json": "{\"id\":\"ord_123\"}"
                                    },
                                    {
                                        "mediaType": "application/json",
                                        "name": "queued",
                                        "json": "{\"id\":\"ord_124\"}"
                                    }
                                ]
                            },
                            {
                                "statusCode": 422,
                                "dataType": { "kind": "ref", "name": "ValidationProblem" },
                                "description": "Validation failed",
                                "examples": [
                                    {
                                        "mediaType": "application/json",
                                        "name": "validationProblem",
                                        "componentExampleId": "validation-problem",
                                        "resolvedJson": "{\"message\":\"Validation failed\"}"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
            """;

        var result = JsonContractReader.Read(json);

        var endpoint = Assert.Single(result.Endpoints);
        var requestExample = Assert.Single(endpoint.RequestExamples!);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Null(requestExample.Name);
        Assert.Equal("""{"customerId":"cus_123"}""", requestExample.Json);
        Assert.Null(requestExample.ComponentExampleId);
        Assert.Null(requestExample.ResolvedJson);

        Assert.Equal(2, endpoint.Responses.Count);

        var createdResponse = endpoint.Responses[0];
        Assert.Equal(201, createdResponse.StatusCode);
        var createdExamples = Assert.IsAssignableFrom<IReadOnlyList<TsEndpointExample>>(createdResponse.Examples);
        Assert.Equal(2, createdExamples.Count);
        Assert.Equal("created", createdExamples[0].Name);
        Assert.Equal("""{"id":"ord_123"}""", createdExamples[0].Json);
        Assert.Equal("queued", createdExamples[1].Name);
        Assert.Equal("""{"id":"ord_124"}""", createdExamples[1].Json);

        var validationResponse = endpoint.Responses[1];
        Assert.Equal("Validation failed", validationResponse.Description);
        var refBackedExample = Assert.Single(validationResponse.Examples!);
        Assert.Equal("validationProblem", refBackedExample.Name);
        Assert.Equal("validation-problem", refBackedExample.ComponentExampleId);
        Assert.Null(refBackedExample.Json);
        Assert.Equal("""{"message":"Validation failed"}""", refBackedExample.ResolvedJson);
    }

    [Fact]
    public void RequestType_ExplicitNull_IsNull()
    {
        var json = """
            {
                "types": [],
                "enums": [],
                "endpoints": [
                    {
                        "name": "getProduct",
                        "httpMethod": "GET",
                        "routeTemplate": "/products/{id}",
                        "controllerName": "product",
                        "params": [],
                        "returnType": null,
                        "responses": [],
                        "requestType": null
                    }
                ]
            }
            """;

        var result = JsonContractReader.Read(json);

        var ep = Assert.Single(result.Endpoints);
        Assert.Null(ep.RequestType);
    }

    [Fact]
    public void RequestType_Null_Omitted_From_Serialized_Json()
    {
        var endpoint = new TsEndpointDefinition(
            "getProduct", "GET", "/products/{id}",
            Params: [],
            ReturnType: null,
            ControllerName: "product",
            Responses: []);

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]);

        Assert.DoesNotContain("requestType", json);
    }

    [Fact]
    public void RequestType_With_IsFormEncoded_Survives_RoundTrip()
    {
        var endpoint = new TsEndpointDefinition(
            "submitForm", "POST", "/forms",
            Params: [],
            ReturnType: null,
            ControllerName: "form",
            Responses: [],
            IsFormEncoded: true,
            RequestType: new TsType.TypeRef("SubmitFormRequest"));

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]);

        var result = JsonContractReader.Read(json);

        var ep = Assert.Single(result.Endpoints);
        Assert.True(ep.IsFormEncoded);
        var typeRef = Assert.IsType<TsType.TypeRef>(ep.RequestType);
        Assert.Equal("SubmitFormRequest", typeRef.Name);
    }

    [Fact]
    public void Read_Handles_Multiple_ParamSources()
    {
        var json = """
            {
                "types": [],
                "enums": [],
                "endpoints": [
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
                        "responses": []
                    },
                    {
                        "name": "listProducts",
                        "httpMethod": "GET",
                        "routeTemplate": "/products",
                        "controllerName": "product",
                        "params": [
                            {
                                "name": "page",
                                "type": { "kind": "primitive", "type": "number", "format": "int32" },
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

        var result = JsonContractReader.Read(json);

        Assert.Equal(2, result.Endpoints.Count);
        Assert.Equal(ParamSource.Body, result.Endpoints[0].Params[0].Source);
        Assert.Equal(ParamSource.Query, result.Endpoints[1].Params[0].Source);
    }
}
