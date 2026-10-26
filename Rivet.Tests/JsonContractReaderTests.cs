using System.Text.Json;
using Rivet.Tool;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class JsonContractReaderTests
{
    [Fact]
    public void Read_Preserves_Heterogeneous_Scalar_Union()
    {
        var json = """
            {
                "types": [{
                    "name": "Settings",
                    "typeParameters": [],
                    "properties": [{
                        "name": "timeout",
                        "type": {
                            "kind": "union",
                            "variants": [
                                { "kind": "primitive", "type": "number" },
                                { "kind": "literal", "value": false }
                            ]
                        },
                        "optional": false
                    }]
                }],
                "enums": [],
                "endpoints": []
            }
            """;

        var result = JsonContractReader.Read(json);
        var settings = Assert.Single(result.Types);
        var union = Assert.IsType<TsType.Union>(Assert.Single(settings.Properties).Type);
        Assert.IsType<TsType.Primitive>(union.Variants[0]);
        Assert.Equal(
            JsonValueKind.False,
            Assert.IsType<TsType.Literal>(union.Variants[1]).Value.ValueKind
        );
    }

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
    public void InlineObject_Field_Without_Optional_Flag_Is_Required_Even_When_Nullable()
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
                                { "name": "note", "type": { "kind": "nullable", "inner": { "kind": "primitive", "type": "string" } } },
                                { "name": "hint", "type": { "kind": "nullable", "inner": { "kind": "primitive", "type": "string" } }, "optional": true }
                            ]
                        }
                    }
                ]
            }
            """;

        var fields = Assert
            .IsType<TsType.InlineObject>(
                Assert.Single(JsonContractReader.Read(json).Endpoints).RequestType
            )
            .Fields;

        Assert.False(fields.Single(field => field.Name == "note").Optional);
        Assert.True(fields.Single(field => field.Name == "hint").Optional);
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
            "createBuyer",
            "POST",
            "/buyers",
            Params: [],
            ReturnType: null,
            ControllerName: "buyer",
            Responses: [],
            RequestType: new TsType.TypeRef("CreateBuyerRequest")
        );

        var withoutRequestType = new TsEndpointDefinition(
            "getProduct",
            "GET",
            "/products/{id}",
            Params: [],
            ReturnType: null,
            ControllerName: "product",
            Responses: []
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [withRequestType, withoutRequestType]
        );

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
            "createBuyer",
            "POST",
            "/buyers",
            Params: [],
            ReturnType: null,
            ControllerName: "buyer",
            Responses: [],
            RequestType: new TsType.InlineObject([
                ("id", new TsType.Primitive("number", "int32")),
                ("name", new TsType.Primitive("string")),
            ])
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

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
        var createdExamples = Assert.IsAssignableFrom<IReadOnlyList<TsEndpointExample>>(
            createdResponse.Examples
        );
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
    public void Read_Throws_For_Request_Example_With_Both_Json_And_ComponentExampleId()
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
                        "responses": [],
                        "requestExamples": [
                            {
                                "mediaType": "application/json",
                                "json": "{\"customerId\":\"cus_123\"}",
                                "componentExampleId": "create-order"
                            }
                        ]
                    }
                ]
            }
            """;

        var error = Assert.Throws<JsonException>(() => JsonContractReader.Read(json));

        Assert.Contains("Exactly one of json or componentExampleId", error.Message);
    }

    [Fact]
    public void Read_Throws_For_Response_Example_Missing_Both_Json_And_ComponentExampleId()
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
                        "responses": [
                            {
                                "statusCode": 422,
                                "examples": [
                                    {
                                        "mediaType": "application/json",
                                        "name": "broken"
                                    }
                                ]
                            }
                        ]
                    }
                ]
            }
            """;

        var error = Assert.Throws<JsonException>(() => JsonContractReader.Read(json));

        Assert.Contains("Exactly one of json or componentExampleId", error.Message);
    }

    [Fact]
    public void Request_Examples_Deserialize_With_Inline_And_RefBacked_Metadata_In_Order()
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
                        "responses": [],
                        "requestExamples": [
                            {
                                "mediaType": "application/json",
                                "name": "inline",
                                "json": "{\"customerId\":\"cus_123\"}"
                            },
                            {
                                "mediaType": "application/json",
                                "name": "refBacked",
                                "componentExampleId": "create-order-example",
                                "resolvedJson": "{\"customerId\":\"cus_456\"}"
                            }
                        ]
                    }
                ]
            }
            """;

        var result = JsonContractReader.Read(json);

        var endpoint = Assert.Single(result.Endpoints);
        var requestExamples = Assert.IsAssignableFrom<IReadOnlyList<TsEndpointExample>>(
            endpoint.RequestExamples
        );
        Assert.Equal(2, requestExamples.Count);

        Assert.Equal("inline", requestExamples[0].Name);
        Assert.Equal("""{"customerId":"cus_123"}""", requestExamples[0].Json);
        Assert.Null(requestExamples[0].ComponentExampleId);
        Assert.Null(requestExamples[0].ResolvedJson);

        Assert.Equal("refBacked", requestExamples[1].Name);
        Assert.Null(requestExamples[1].Json);
        Assert.Equal("create-order-example", requestExamples[1].ComponentExampleId);
        Assert.Equal("""{"customerId":"cus_456"}""", requestExamples[1].ResolvedJson);
    }

    [Fact]
    public void Endpoint_Examples_Survive_Emit_And_Read_RoundTrip()
    {
        var endpoint = new TsEndpointDefinition(
            "createOrder",
            "POST",
            "/orders",
            [
                new TsEndpointParam(
                    "body",
                    new TsType.TypeRef("CreateOrderRequest"),
                    ParamSource.Body
                ),
            ],
            new TsType.TypeRef("CreateOrderResponse"),
            "orders",
            [
                new TsResponseType(
                    201,
                    new TsType.TypeRef("CreateOrderResponse"),
                    Examples:
                    [
                        new TsEndpointExample(
                            "application/json",
                            "created",
                            """{"id":"ord_123"}"""
                        ),
                        new TsEndpointExample("application/json", "queued", """{"id":"ord_124"}"""),
                    ]
                ),
                new TsResponseType(
                    422,
                    new TsType.TypeRef("ValidationProblem"),
                    "Validation failed",
                    [
                        new TsEndpointExample(
                            "application/json",
                            "validationProblem",
                            ComponentExampleId: "validation-problem",
                            ResolvedJson: """{"message":"Validation failed"}"""
                        ),
                    ]
                ),
            ],
            RequestExamples:
            [
                new TsEndpointExample("application/json", Json: """{"customerId":"cus_123"}"""),
            ]
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

        var result = JsonContractReader.Read(json);

        var roundTripped = Assert.Single(result.Endpoints);
        var requestExample = Assert.Single(roundTripped.RequestExamples!);
        Assert.Equal("application/json", requestExample.MediaType);
        Assert.Equal("""{"customerId":"cus_123"}""", requestExample.Json);

        var createdResponse = roundTripped.Responses[0];
        var createdExamples = Assert.IsAssignableFrom<IReadOnlyList<TsEndpointExample>>(
            createdResponse.Examples
        );
        Assert.Equal(2, createdExamples.Count);
        Assert.Equal("created", createdExamples[0].Name);
        Assert.Equal("""{"id":"ord_123"}""", createdExamples[0].Json);
        Assert.Equal("queued", createdExamples[1].Name);
        Assert.Equal("""{"id":"ord_124"}""", createdExamples[1].Json);

        var refBackedExample = Assert.Single(roundTripped.Responses[1].Examples!);
        Assert.Equal("validationProblem", refBackedExample.Name);
        Assert.Equal("validation-problem", refBackedExample.ComponentExampleId);
        Assert.Null(refBackedExample.Json);
        Assert.Equal("""{"message":"Validation failed"}""", refBackedExample.ResolvedJson);
    }

    [Fact]
    public void Request_Examples_Survive_Emit_And_Read_RoundTrip_With_RefBacked_Metadata_And_Order()
    {
        var endpoint = new TsEndpointDefinition(
            "createOrder",
            "POST",
            "/orders",
            [
                new TsEndpointParam(
                    "body",
                    new TsType.TypeRef("CreateOrderRequest"),
                    ParamSource.Body
                ),
            ],
            new TsType.TypeRef("CreateOrderResponse"),
            "orders",
            [new TsResponseType(201, new TsType.TypeRef("CreateOrderResponse"))],
            RequestExamples:
            [
                new TsEndpointExample("application/json", "inline", """{"customerId":"cus_123"}"""),
                new TsEndpointExample(
                    "application/json",
                    "refBacked",
                    ComponentExampleId: "create-order-example",
                    ResolvedJson: """{"customerId":"cus_456"}"""
                ),
            ]
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

        var result = JsonContractReader.Read(json);

        var roundTripped = Assert.Single(result.Endpoints);
        var requestExamples = Assert.IsAssignableFrom<IReadOnlyList<TsEndpointExample>>(
            roundTripped.RequestExamples
        );
        Assert.Equal(2, requestExamples.Count);

        Assert.Equal("inline", requestExamples[0].Name);
        Assert.Equal("""{"customerId":"cus_123"}""", requestExamples[0].Json);
        Assert.Null(requestExamples[0].ComponentExampleId);
        Assert.Null(requestExamples[0].ResolvedJson);

        Assert.Equal("refBacked", requestExamples[1].Name);
        Assert.Null(requestExamples[1].Json);
        Assert.Equal("create-order-example", requestExamples[1].ComponentExampleId);
        Assert.Equal("""{"customerId":"cus_456"}""", requestExamples[1].ResolvedJson);
    }

    [Fact]
    public void Endpoint_Example_With_ComponentExampleId_Only_Deserializes_Without_ResolvedJson()
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
                        "responses": [
                            {
                                "statusCode": 202,
                                "examples": [
                                    {
                                        "mediaType": "application/json",
                                        "name": "accepted",
                                        "componentExampleId": "order-accepted"
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
        var response = Assert.Single(endpoint.Responses);
        var example = Assert.Single(response.Examples!);
        Assert.Equal("application/json", example.MediaType);
        Assert.Equal("accepted", example.Name);
        Assert.Null(example.Json);
        Assert.Equal("order-accepted", example.ComponentExampleId);
        Assert.Null(example.ResolvedJson);
    }

    [Fact]
    public void Endpoint_Examples_With_NonDefault_MediaTypes_Survive_Emit_And_Read_RoundTrip()
    {
        var endpoint = new TsEndpointDefinition(
            "createOrder",
            "POST",
            "/orders",
            [],
            null,
            "orders",
            [
                new TsResponseType(
                    422,
                    new TsType.TypeRef("ProblemDetails"),
                    "Bad request",
                    [
                        new TsEndpointExample(
                            "application/problem+json",
                            "problem",
                            ComponentExampleId: "problem-example",
                            ResolvedJson: """{"title":"Bad request"}"""
                        ),
                    ]
                ),
            ],
            RequestExamples:
            [
                new TsEndpointExample(
                    "application/problem+json",
                    Json: """{"title":"Bad request"}"""
                ),
            ]
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

        var result = JsonContractReader.Read(json);

        var roundTripped = Assert.Single(result.Endpoints);
        var requestExample = Assert.Single(roundTripped.RequestExamples!);
        Assert.Equal("application/problem+json", requestExample.MediaType);
        Assert.Equal("""{"title":"Bad request"}""", requestExample.Json);

        var responseExample = Assert.Single(roundTripped.Responses[0].Examples!);
        Assert.Equal("application/problem+json", responseExample.MediaType);
        Assert.Equal("problem-example", responseExample.ComponentExampleId);
        Assert.Equal("""{"title":"Bad request"}""", responseExample.ResolvedJson);
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
            "getProduct",
            "GET",
            "/products/{id}",
            Params: [],
            ReturnType: null,
            ControllerName: "product",
            Responses: []
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

        Assert.DoesNotContain("requestType", json);
    }

    [Fact]
    public void RequestType_With_IsFormEncoded_Survives_RoundTrip()
    {
        var endpoint = new TsEndpointDefinition(
            "submitForm",
            "POST",
            "/forms",
            Params: [],
            ReturnType: null,
            ControllerName: "form",
            Responses: [],
            IsFormEncoded: true,
            RequestType: new TsType.TypeRef("SubmitFormRequest")
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

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

    // ========== E5/N1/N3: reader must not drop queryAuth / isFileEndpoint / isOptional ==========
    // ContractEmitterTests pins that these SERIALIZE; these tests pin that the reader carries
    // them back into the model and that they surface in the emitted OpenAPI — the cross-repo
    // (--from) path must reach the same emitter behavior as the C# walker path.

    [Fact]
    public void QueryAuth_Survives_Emit_And_Read_RoundTrip_And_Surfaces_In_OpenApi()
    {
        var endpoint = new TsEndpointDefinition(
            "streamVideo",
            "GET",
            "/api/media/{id}/stream",
            [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
            null,
            "media",
            [new TsResponseType(200, null)],
            QueryAuth: new QueryAuthMetadata("token")
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

        var result = JsonContractReader.Read(json);

        // The model carries query-auth through the read (N3: previously silently dropped)
        var roundTripped = Assert.Single(result.Endpoints);
        Assert.NotNull(roundTripped.QueryAuth);
        Assert.Equal("token", roundTripped.QueryAuth!.ParameterName);

        // ...and it surfaces in OpenAPI exactly like the C# walker path: a required query
        // parameter plus the x-rivet-query-auth extension.
        var openApi = OpenApiEmitter.Emit(
            [roundTripped],
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>(),
            security: null
        );

        using var doc = JsonDocument.Parse(openApi);
        var operation = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/media/{id}/stream")
            .GetProperty("get");

        var queryAuth = operation.GetProperty("x-rivet-query-auth");
        Assert.Equal("token", queryAuth.GetProperty("parameterName").GetString());

        var tokenParam = operation
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "token");
        Assert.Equal("query", tokenParam.GetProperty("in").GetString());
        Assert.True(tokenParam.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void IsFileEndpoint_Survives_Emit_And_Read_RoundTrip()
    {
        var endpoint = new TsEndpointDefinition(
            "downloadFile",
            "GET",
            "/api/files/{id}",
            [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
            null,
            "files",
            [new TsResponseType(200, null)],
            FileContentType: "application/pdf",
            IsFileEndpoint: true
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

        var result = JsonContractReader.Read(json);

        // The model carries the file-endpoint flag through the read (E5: previously dropped)
        var roundTripped = Assert.Single(result.Endpoints);
        Assert.True(roundTripped.IsFileEndpoint);
        Assert.Equal("application/pdf", roundTripped.FileContentType);

        // Re-serializing is lossless — the flag does not decay across contract hops
        var reEmitted = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [roundTripped]
        );

        using var doc = JsonDocument.Parse(reEmitted);
        var ep = doc.RootElement.GetProperty("endpoints")[0];
        Assert.True(ep.GetProperty("isFileEndpoint").GetBoolean());

        // ...and the OpenAPI output keeps the binary response content type
        var openApi = OpenApiEmitter.Emit(
            [roundTripped],
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>(),
            security: null
        );

        using var openApiDoc = JsonDocument.Parse(openApi);
        var response200 = openApiDoc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/files/{id}")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200");
        Assert.True(response200.GetProperty("content").TryGetProperty("application/pdf", out _));
    }

    [Fact]
    public void Duplicate_Response_Status_In_Contract_Json_Fails_With_RIV2010()
    {
        var json = """
            {
              "types": [],
              "enums": [],
              "endpoints": [{
                "name": "getUser",
                "httpMethod": "GET",
                "routeTemplate": "/api/users/{id}",
                "params": [],
                "controllerName": "UsersController",
                "responses": [
                  { "statusCode": 404, "dataType": { "kind": "primitive", "type": "string" }, "description": "first" },
                  { "statusCode": 404, "dataType": { "kind": "primitive", "type": "number" }, "description": "second" }
                ]
              }]
            }
            """;

        // Keeping the first declaration used to drop the second's shape and metadata
        // under a warning; the producer's contradiction is now refused.
        var exception = Assert.Throws<RivetUserException>(() => JsonContractReader.Read(json));

        Assert.StartsWith("error RIV2010:", exception.Message);
        Assert.Contains("'getUser'", exception.Message);
        Assert.Contains("status 404", exception.Message);
    }

    [Fact]
    public void Brand_Declared_Two_Ways_Fails_With_RIV2008()
    {
        var json = """
            {
              "types": [{
                "name": "UserDto",
                "typeParameters": [],
                "properties": [
                  { "name": "id", "type": { "kind": "brand", "name": "UserId", "underlying": { "kind": "primitive", "type": "string" } }, "optional": false },
                  { "name": "legacyId", "type": { "kind": "brand", "name": "UserId", "underlying": { "kind": "primitive", "type": "number" } }, "optional": false }
                ]
              }],
              "enums": [],
              "endpoints": []
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() => JsonContractReader.Read(json));

        Assert.StartsWith("error RIV2008:", exception.Message);
        Assert.Contains("'UserId'", exception.Message);
        Assert.Contains("\"string\"", exception.Message);
        Assert.Contains("\"number\"", exception.Message);
    }

    [Fact]
    public void Brand_Declared_Identically_Twice_Is_One_Brand()
    {
        var json = """
            {
              "types": [{
                "name": "UserDto",
                "typeParameters": [],
                "properties": [
                  { "name": "id", "type": { "kind": "brand", "name": "Tag", "underlying": { "kind": "stringUnion", "values": ["a", "b"] } }, "optional": false },
                  { "name": "other", "type": { "kind": "brand", "name": "Tag", "underlying": { "kind": "stringUnion", "values": ["a", "b"] } }, "optional": false }
                ]
              }],
              "enums": [],
              "endpoints": []
            }
            """;

        var (_, _, _, brands) = JsonContractReader.Read(json);

        Assert.Equal("Tag", Assert.Single(brands).Key);
    }

    [Theory]
    [InlineData(
        """[{ "name": "Dto", "typeParameters": [], "properties": [] }, { "name": "Dto", "typeParameters": [], "properties": [] }]""",
        "[]",
        "'Dto' is declared more than once"
    )]
    [InlineData(
        """[{ "name": "Status", "typeParameters": [], "properties": [] }]""",
        """[{ "name": "Status", "values": ["a"] }]""",
        "'Status' is declared more than once"
    )]
    [InlineData("[]", """[{ "name": "Status" }]""", "enum 'Status' must carry exactly one of")]
    [InlineData(
        "[]",
        """[{ "name": "Status", "values": ["a"], "intValues": [1] }]""",
        "enum 'Status' must carry exactly one of"
    )]
    [InlineData(
        """[{ "name": "Id", "typeParameters": [], "type": { "kind": "primitive", "type": "string" }, "properties": [{ "name": "value", "type": { "kind": "primitive", "type": "string" }, "optional": false }] }]""",
        "[]",
        "type 'Id' has both 'type' and 'properties'"
    )]
    [InlineData(
        """[{ "name": "Id", "typeParameters": [], "type": { "kind": "primitive", "type": "string" }, "properties": [] }]""",
        "[]",
        "type 'Id' has both 'type' and 'properties'"
    )]
    [InlineData(
        """[{ "name": "Dto", "typeParameters": [] }]""",
        "[]",
        "type 'Dto' has neither 'type' nor 'properties'"
    )]
    [InlineData("[null]", "[]", "'types[0]' is null")]
    public void Malformed_Declarations_Are_Refused(string types, string enums, string expected)
    {
        var json = $$"""{ "types": {{types}}, "enums": {{enums}}, "endpoints": [] }""";

        var exception = Assert.Throws<JsonException>(() => JsonContractReader.Read(json));

        Assert.Contains(expected, exception.Message);
    }

    private const string TaggedInline = """
        { "kind": "inlineObject", "properties": [{ "name": "kind", "type": { "kind": "stringUnion", "values": ["a"] } }] }
        """;

    // A variant's annotations describe its own inline component, and its tag schema must
    // admit its tag; the reader refuses anything else where it enters, naming the variant.
    [Theory]
    [InlineData(TaggedInline, "\"discriminatorSchema\": \"[]\"", "isn't a JSON object")]
    [InlineData(TaggedInline, "\"discriminatorSchema\": \"{\\\"type\\\"\"", "isn't valid JSON")]
    [InlineData(
        TaggedInline,
        "\"discriminatorSchema\": \"{\\\"enum\\\":[\\\"b\\\"]}\"",
        "has an enum that doesn't list the tag 'a'"
    )]
    [InlineData(
        TaggedInline,
        "\"discriminatorSchema\": \"{\\\"const\\\":\\\"b\\\"}\"",
        "has a const other than the tag 'a'"
    )]
    [InlineData(
        TaggedInline,
        "\"discriminatorSchema\": \"{\\\"type\\\":\\\"integer\\\"}\"",
        "has a type that doesn't include string"
    )]
    [InlineData(
        """{ "kind": "ref", "name": "Thing" }""",
        "\"description\": \"d\"",
        "isn't an inline object with a 'kind' property"
    )]
    [InlineData(
        """{ "kind": "inlineObject", "properties": [] }""",
        "\"scalarMetadata\": { \"isNullable\": true }",
        "isn't an inline object with a 'kind' property"
    )]
    public void Variant_Annotations_The_IR_Cannot_Place_Fail_With_RIV2017(
        string variantType,
        string annotation,
        string expected
    )
    {
        var json = $$"""
            {
              "types": [
                { "name": "Thing", "typeParameters": [], "properties": [] },
                { "name": "State", "typeParameters": [], "type": { "kind": "taggedUnion", "discriminator": "kind",
                  "variants": [{ "tag": "a", "type": {{variantType}}, {{annotation}} }] } }
              ],
              "enums": [],
              "endpoints": []
            }
            """;

        var message = Assert
            .Throws<RivetUserException>(() => JsonContractReader.Read(json))
            .Message;

        Assert.StartsWith("error RIV2017:", message);
        Assert.Contains("tagged-union variant 'a' (discriminator 'kind')", message);
        Assert.Contains(expected, message);
    }

    [Theory]
    [InlineData(
        """{ "name": "X-Page", "description": "no type" }""",
        "[]",
        "[]",
        "TsResponseHeader' was missing required properties including: 'type'"
    )]
    [InlineData(
        "null",
        """[{ "name": "id", "source": "route" }]""",
        "[]",
        "TsEndpointParam' was missing required properties including: 'type'"
    )]
    [InlineData(
        "null",
        "[]",
        """[{ "name": "User", "typeParameters": [], "properties": [{ "name": "id", "optional": false }] }]""",
        "TsPropertyDefinition' was missing required properties including: 'type'"
    )]
    public void Missing_Required_Type_Is_Refused(
        string header,
        string parameters,
        string types,
        string expected
    )
    {
        var headers = header == "null" ? "" : $", \"headers\": [{header}]";
        var json = $$"""
            {
              "types": {{types}},
              "enums": [],
              "endpoints": [{
                "name": "getUser",
                "httpMethod": "GET",
                "routeTemplate": "/api/users/{id}",
                "params": {{parameters}},
                "controllerName": "users",
                "responses": [{ "statusCode": 200, "description": "ok"{{headers}} }]
              }]
            }
            """;

        var exception = Assert.Throws<JsonException>(() => JsonContractReader.Read(json));
        Assert.Contains(expected, exception.Message);
        Assert.Contains("Path: $.", exception.Message);
    }

    /// <summary>
    /// An explicit null satisfies [JsonRequired], so a non-nullable member written as null
    /// used to reach the IR and crash with a NullReferenceException; a response with no
    /// status was emitted under "0".
    /// </summary>
    [Theory]
    [InlineData($$"""{ {{GetEndpoint}}, "responses": null }""", "Path: $.endpoints[0].responses")]
    [InlineData(
        $$"""{ {{GetEndpoint}}, "responses": [{ "statusCode": 200 }], "security": { "alternatives": null } }""",
        "Path: $.endpoints[0].security.alternatives"
    )]
    [InlineData(
        $$"""{ {{GetEndpoint}}, "responses": [{ "description": "ok" }] }""",
        "endpoint 'things.get' has a response with neither statusCode nor statusKey"
    )]
    [InlineData(
        $$"""{ {{GetEndpoint}}, "responses": [{ "statusKey": "ok" }] }""",
        "endpoint 'things.get' has a response with statusKey 'ok'"
    )]
    [InlineData(
        """{ "name": "get", "httpMethod": "GET", "routeTemplate": "/api/things", "params": [null], "controllerName": "things", "responses": [{ "statusCode": 200 }] }""",
        "'params[0]' is null; contract lists and maps hold no nulls, so remove the entry or write its value. Path: $.endpoints[0]"
    )]
    [InlineData(
        $$"""{ {{GetEndpoint}}, "responses": [{ "statusCode": 200, "examples": [{ "mediaType": "application/json", "json": "{}", "referencedComponents": { "Thing": null } }] }] }""",
        "'referencedComponents.Thing' is null"
    )]
    public void Malformed_Endpoints_Are_Refused(string endpoint, string expected)
    {
        var json = $$"""{ "types": [], "enums": [], "endpoints": [{{endpoint}}] }""";

        var exception = Assert.Throws<JsonException>(() => JsonContractReader.Read(json));

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Param_IsOptional_Survives_Emit_And_Read_RoundTrip_And_Surfaces_In_OpenApi()
    {
        // N1 (reader side): a non-nullable but optional query param — optionality must not
        // depend on type-level nullability alone.
        var endpoint = new TsEndpointDefinition(
            "search",
            "GET",
            "/api/items",
            [
                new TsEndpointParam("q", new TsType.Primitive("string"), ParamSource.Query),
                new TsEndpointParam(
                    "limit",
                    new TsType.Primitive("number"),
                    ParamSource.Query,
                    IsOptional: true
                ),
            ],
            null,
            "items",
            [new TsResponseType(200, null)]
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

        // The wire key is "isOptional" (the rivet-ts interop contract shape)
        using (var contractDoc = JsonDocument.Parse(json))
        {
            var limitParam = contractDoc
                .RootElement.GetProperty("endpoints")[0]
                .GetProperty("params")[1];
            Assert.True(limitParam.GetProperty("isOptional").GetBoolean());
        }

        var result = JsonContractReader.Read(json);

        var roundTripped = Assert.Single(result.Endpoints);
        Assert.False(roundTripped.Params[0].IsOptional);
        Assert.True(roundTripped.Params[1].IsOptional);

        // OpenAPI: the optional param is NOT required; the required one is
        var openApi = OpenApiEmitter.Emit(
            [roundTripped],
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType.Brand>(),
            new Dictionary<string, TsType>(),
            security: null
        );

        using var doc = JsonDocument.Parse(openApi);
        var parameters = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/items")
            .GetProperty("get")
            .GetProperty("parameters");

        var qParam = parameters
            .EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "q");
        Assert.True(qParam.GetProperty("required").GetBoolean());

        var limitQueryParam = parameters
            .EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "limit");
        Assert.False(limitQueryParam.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void Route_Body_Property_Association_Survives_Emit_And_Read_RoundTrip()
    {
        var endpoint = new TsEndpointDefinition(
            "updateRole",
            "PUT",
            "/api/members/{id}/role",
            [
                new TsEndpointParam(
                    "id",
                    new TsType.Primitive("string"),
                    ParamSource.Route,
                    BodyPropertyName: "member_key"
                ),
                new TsEndpointParam(
                    "body",
                    new TsType.TypeRef("UpdateRoleInput"),
                    ParamSource.Body
                ),
            ],
            null,
            "members",
            []
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );

        using (var contractDoc = JsonDocument.Parse(json))
        {
            Assert.Equal(
                "member_key",
                contractDoc
                    .RootElement.GetProperty("endpoints")[0]
                    .GetProperty("params")[0]
                    .GetProperty("bodyPropertyName")
                    .GetString()
            );
        }

        var roundTripped = Assert.Single(JsonContractReader.Read(json).Endpoints);
        Assert.Equal("member_key", roundTripped.Params[0].BodyPropertyName);
    }

    [Fact]
    public void Endpoint_Security_Provenance_And_Content_Metadata_Survive_RoundTrip()
    {
        var endpoint = new TsEndpointDefinition(
            "create",
            "POST",
            "/items",
            [],
            null,
            "items",
            [
                new TsResponseType(
                    201,
                    null,
                    "Created exactly",
                    Headers:
                    [
                        new TsResponseHeader(
                            "X-Item",
                            new TsType.Primitive("string"),
                            "Item header"
                        ),
                    ],
                    Contents:
                    [
                        new TsMediaTypeContent(
                            "application/custom+json",
                            new TsType.Primitive("string"),
                            SchemaType: "string",
                            Format: "item-id",
                            IsFormatSpecified: true
                        ),
                    ]
                ),
            ],
            Security: new SecurityRequirements([
                new SecurityRequirement([new SecurityRequirementScheme("oauth", ["items:write"])]),
            ]),
            Provenance: new OpenApiOperationProvenance(
                true,
                "items.create",
                ["Items"],
                true,
                [],
                "Exact request body",
                new OpenApiRivetIdentityProvenance("items", "create")
            )
        );

        var json = ContractEmitter.Emit([], [], [endpoint]);
        var roundTripped = Assert.Single(JsonContractReader.Read(json).Endpoints);
        var response = Assert.Single(roundTripped.Responses);

        Assert.Equal("oauth", roundTripped.Security!.Alternatives[0].Schemes[0].Name);
        Assert.Equal(["items:write"], roundTripped.Security.Alternatives[0].Schemes[0].Scopes);
        Assert.Equal("items.create", roundTripped.Provenance!.OperationId);
        Assert.Equal("Exact request body", roundTripped.Provenance.RequestBodyDescription);
        Assert.Equal("Created exactly", response.Description);
        Assert.Equal("Item header", Assert.Single(response.Headers!).Description);
        var content = Assert.Single(response.Contents!);
        Assert.Equal("application/custom+json", content.MediaType);
        Assert.Equal("item-id", content.Format);
        Assert.True(content.IsFormatSpecified);
    }

    private const string BrandConflictTypes = """
        [{ "name": "UserDto", "typeParameters": [], "properties": [
          { "name": "id", "type": { "kind": "brand", "name": "UserId", "underlying": { "kind": "primitive", "type": "string" } }, "optional": false },
          { "name": "legacyId", "type": { "kind": "brand", "name": "UserId", "underlying": { "kind": "primitive", "type": "number" } }, "optional": false }
        ] }]
        """;

    private const string UnplaceableVariantTypes = """
        [{ "name": "State", "typeParameters": [], "type": { "kind": "taggedUnion", "discriminator": "kind",
          "variants": [{ "tag": "a", "type": { "kind": "primitive", "type": "string" }, "description": "d" }] } }]
        """;

    private const string PageTemplateTypes = """
        [{ "name": "Page", "typeParameters": ["T"], "properties": [
          { "name": "items", "type": { "kind": "array", "element": { "kind": "typeParam", "name": "T" } }, "optional": false }
        ] }]
        """;

    private const string GetEndpoint = """
        "name": "get", "httpMethod": "GET", "routeTemplate": "/api/things", "params": [], "controllerName": "things"
        """;

    /// <summary>
    /// Malformed contract JSON is refused by the real CLI: exit 1, the diagnostic on
    /// stderr, and no spec written. Each case used to warn and write a degraded spec.
    /// </summary>
    [Theory]
    [InlineData(
        "RIV2005",
        "[]",
        $$"""[{ {{GetEndpoint}}, "responses": [{ "statusCode": 200, "dataType": { "kind": "ref", "name": "Missing" } }] }]"""
    )]
    [InlineData(
        "RIV2005",
        "[]",
        $$"""[{ {{GetEndpoint}}, "responses": [{ "statusCode": 200, "dataType": { "kind": "primitive", "type": "unknown", "csharpType": "TimeSpan" } }] }]"""
    )]
    [InlineData(
        "RIV2006",
        "[]",
        $$"""[{ {{GetEndpoint}}, "responses": [{ "statusCode": 200, "dataType": { "kind": "typeParam", "name": "T" } }] }]"""
    )]
    [InlineData(
        "RIV2006",
        PageTemplateTypes,
        $$"""[{ {{GetEndpoint}}, "responses": [{ "statusCode": 200, "dataType": { "kind": "generic", "name": "Page", "typeArgs": [{ "kind": "primitive", "type": "string" }, { "kind": "primitive", "type": "number" }] } }] }]"""
    )]
    [InlineData(
        "RIV2007",
        "[]",
        $$"""[{ {{GetEndpoint}}, "responses": [{ "statusCode": 200, "dataType": { "kind": "generic", "name": "Page", "typeArgs": [{ "kind": "primitive", "type": "string" }] } }] }]"""
    )]
    [InlineData("RIV2008", BrandConflictTypes, "[]")]
    [InlineData("RIV2017", UnplaceableVariantTypes, "[]")]
    [InlineData(
        "RIV2010",
        "[]",
        $$"""[{ {{GetEndpoint}}, "responses": [{ "statusCode": 200 }, { "statusCode": 200 }] }]"""
    )]
    public void Cli_Refuses_Malformed_Contract_Json(string id, string types, string endpoints)
    {
        using var workDirectory = new TempDir();
        var contractPath = Path.Combine(workDirectory.FullName, "contract.json");
        File.WriteAllText(
            contractPath,
            $$"""{ "types": {{types}}, "enums": [], "endpoints": {{endpoints}} }"""
        );
        var outputDirectory = Path.Combine(workDirectory.FullName, "out");

        var (exitCode, _, stderr) = CliRunner.RunCli(
            workDirectory.FullName,
            ["--from", contractPath, "--output", outputDirectory]
        );

        Assert.Equal(1, exitCode);
        Assert.Contains($"error {id}:", stderr);
        Assert.DoesNotContain("warning RIV20", stderr);
        Assert.False(File.Exists(Path.Combine(outputDirectory, "openapi.json")));
    }

    [Fact]
    public void Cli_Refuses_A_Scheme_Listed_Twice_In_One_Security_Alternative()
    {
        // An alternative is one Security Requirement Object, keyed by scheme name; the
        // repeated entry used to crash emission with an ArgumentException.
        using var workDirectory = new TempDir();
        var contractPath = Path.Combine(workDirectory.FullName, "contract.json");
        File.WriteAllText(
            contractPath,
            $$"""
            {
              "types": [],
              "enums": [],
              "endpoints": [{ {{GetEndpoint}}, "responses": [{ "statusCode": 200 }],
                "security": { "alternatives": [{ "schemes": [
                  { "name": "oauth", "scopes": ["read"] },
                  { "name": "oauth", "scopes": ["write"] }
                ] }] } }]
            }
            """
        );
        var outputDirectory = Path.Combine(workDirectory.FullName, "out");

        var (exitCode, _, stderr) = CliRunner.RunCli(
            workDirectory.FullName,
            ["--from", contractPath, "--output", outputDirectory, "--security", "oauth=bearer"]
        );

        Assert.Equal(1, exitCode);
        Assert.Contains("error: invalid contract JSON", stderr);
        Assert.Contains("endpoint 'things.get' (GET /api/things)", stderr);
        Assert.Contains("security scheme 'oauth' more than once", stderr);
        Assert.DoesNotContain("ArgumentException", stderr);
        Assert.False(File.Exists(Path.Combine(outputDirectory, "openapi.json")));
    }

    /// <summary>
    /// A property the reader doesn't know is refused with its path, not skipped. The retired
    /// security shapes used to read as "inherit the document's security", dropping what
    /// they said.
    /// </summary>
    [Theory]
    [InlineData(
        $$"""[{ {{GetEndpoint}}, "responses": [{ "statusCode": 200 }], "security": { "isAnonymous": false, "scheme": "admin" } }]""",
        "",
        "$.endpoints[0].security.isAnonymous"
    )]
    [InlineData(
        $$"""[{ {{GetEndpoint}}, "responses": [{ "statusCode": 200 }], "securityRequirements": { "alternatives": [] } }]""",
        "",
        "$.endpoints[0].securityRequirements"
    )]
    [InlineData("[]", """, "brands": {}""", "$.brands")]
    public void Cli_Refuses_Unknown_Properties_With_Their_Path(
        string endpoints,
        string extraTopLevel,
        string path
    )
    {
        using var workDirectory = new TempDir();
        var contractPath = Path.Combine(workDirectory.FullName, "contract.json");
        File.WriteAllText(
            contractPath,
            $$"""{ "types": [], "enums": [], "endpoints": {{endpoints}}{{extraTopLevel}} }"""
        );
        var outputDirectory = Path.Combine(workDirectory.FullName, "out");

        var (exitCode, _, stderr) = CliRunner.RunCli(
            workDirectory.FullName,
            ["--from", contractPath, "--output", outputDirectory, "--security", "admin=bearer"]
        );

        Assert.Equal(1, exitCode);
        Assert.Contains("error: invalid contract JSON", stderr);
        Assert.Contains($"Path: {path}", stderr);
        Assert.False(File.Exists(Path.Combine(outputDirectory, "openapi.json")));
    }

    [Fact]
    public void Typescript_Unknown_Maps_To_The_Empty_Schema_Silently()
    {
        // rivet-ts lowers TypeScript's `unknown` to a primitive with no csharpType: "any
        // JSON value", whose honest schema is {} — the same as object/JsonElement.
        var json = $$"""
            {
              "types": [],
              "enums": [],
              "endpoints": [{ {{GetEndpoint}}, "responses": [{ "statusCode": 200, "dataType": { "kind": "primitive", "type": "unknown" } }] }]
            }
            """;

        string spec = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            spec = CompilationHelper.EmitOpenApiFromJson(json)
        );

        using var doc = JsonDocument.Parse(spec);
        var schema = doc
            .RootElement.GetProperty("paths")
            .GetProperty("/api/things")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Empty(schema.EnumerateObject());
        Assert.DoesNotContain("/api/things", stderr);
    }
}
