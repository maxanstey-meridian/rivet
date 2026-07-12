using System.Text.Json;
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

        var error = Assert.Throws<ArgumentException>(() => JsonContractReader.Read(json));

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

        var error = Assert.Throws<ArgumentException>(() => JsonContractReader.Read(json));

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
    public void Duplicate_Response_Status_In_Contract_Json_Keeps_First_Metadata_And_Sorts()
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
                  { "statusCode": 500, "description": "server error" },
                  {
                    "statusCode": 404,
                    "dataType": { "kind": "primitive", "type": "string" },
                    "description": "first",
                    "examples": [{ "mediaType": "application/json", "name": "first", "json": "{}" }],
                    "headers": [{ "name": "X-First", "description": "first header", "required": true }]
                  },
                  { "statusCode": 404, "dataType": { "kind": "primitive", "type": "number" }, "description": "second" },
                  { "statusCode": 200, "description": "success" }
                ]
              }]
            }
            """;

        IReadOnlyList<TsEndpointDefinition>? endpoints = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            var result = JsonContractReader.Read(json);
            endpoints = result.Endpoints;
            ContractEmitter.Emit(
                result.Types.ToDictionary(type => type.Name),
                result.Enums,
                result.Endpoints
            );
        });

        var responses = Assert.Single(endpoints!).Responses;
        Assert.Equal([200, 404, 500], responses.Select(response => response.StatusCode));
        var response404 = responses[1];
        Assert.Equal(new TsType.Primitive("string"), response404.DataType);
        Assert.Equal("first", response404.Description);
        Assert.Equal("first", Assert.Single(response404.Examples!).Name);
        Assert.Equal("X-First", Assert.Single(response404.Headers!).Name);
        Assert.Equal(1, stderr.Split("warning RIV2010:").Length - 1);
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
            SecurityRequirements: new SecurityRequirements([
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

        Assert.Equal("oauth", roundTripped.SecurityRequirements!.Alternatives[0].Schemes[0].Name);
        Assert.Equal(
            ["items:write"],
            roundTripped.SecurityRequirements.Alternatives[0].Schemes[0].Scopes
        );
        Assert.Equal("items.create", roundTripped.Provenance!.OperationId);
        Assert.Equal("Exact request body", roundTripped.Provenance.RequestBodyDescription);
        Assert.Equal("Created exactly", response.Description);
        Assert.Equal("Item header", Assert.Single(response.Headers!).Description);
        var content = Assert.Single(response.Contents!);
        Assert.Equal("application/custom+json", content.MediaType);
        Assert.Equal("item-id", content.Format);
        Assert.True(content.IsFormatSpecified);
    }
}
