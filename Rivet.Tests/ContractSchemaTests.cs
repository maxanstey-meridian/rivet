using System.Text.Json;
using Json.Schema;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ContractSchemaTests
{
    private static readonly JsonSchema Schema = LoadSchema();

    private static JsonSchema LoadSchema()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "../../../../rivet-contract-schema.json");
        var schemaJson = File.ReadAllText(schemaPath);
        return JsonSchema.FromText(schemaJson);
    }

    private static EvaluationResults Validate(string json)
    {
        var node = JsonDocument.Parse(json).RootElement;
        return Schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
    }

    [Fact]
    public void Empty_Contract_Validates()
    {
        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            []);

        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Contract_With_Enum_Validates()
    {
        var enums = new Dictionary<string, TsType>
        {
            ["Status"] = new TsType.StringUnion(["Active", "Inactive"]),
        };

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), enums, []);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void All_TsType_Kinds_Validate()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["AllKinds"] = new("AllKinds", ["T"], [
                new("prim", new TsType.Primitive("string"), false),
                new("primFmt", new TsType.Primitive("string", Format: "uuid", CSharpType: "Guid"), false),
                new("nullable", new TsType.Nullable(new TsType.Primitive("number")), true),
                new("arr", new TsType.Array(new TsType.TypeRef("ItemDto")), false),
                new("dict", new TsType.Dictionary(new TsType.Primitive("string")), false),
                new("strUnion", new TsType.StringUnion(["a", "b"]), false),
                new("typeRef", new TsType.TypeRef("Other"), false),
                new("generic", new TsType.Generic("Page", [new TsType.TypeRef("ItemDto")]), false),
                new("typeParam", new TsType.TypeParam("T"), false),
                new("brand", new TsType.Brand("Email", new TsType.Primitive("string")), false),
                new("inline", new TsType.InlineObject([("key", new TsType.Primitive("string")), ("val", new TsType.Primitive("number"))]), false),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Endpoint_With_Params_And_Responses_Validates()
    {
        var endpoint = new TsEndpointDefinition(
            "createOrder",
            "POST",
            "/api/orders",
            [
                new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route),
                new TsEndpointParam("body", new TsType.TypeRef("CreateOrderRequest"), ParamSource.Body),
            ],
            new TsType.TypeRef("CreateOrderResponse"),
            "OrdersController",
            [
                new TsResponseType(201, new TsType.TypeRef("CreateOrderResponse")),
                new TsResponseType(422, new TsType.TypeRef("ValidationProblem"), "Validation failed"),
            ],
            Summary: "Create an order");

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Endpoint_With_Request_And_Response_Examples_Validates()
    {
        var endpoint = new TsEndpointDefinition(
            "createOrder",
            "POST",
            "/api/orders",
            [new TsEndpointParam("body", new TsType.TypeRef("CreateOrderRequest"), ParamSource.Body)],
            new TsType.TypeRef("CreateOrderResponse"),
            "OrdersController",
            [
                new TsResponseType(
                    201,
                    new TsType.TypeRef("CreateOrderResponse"),
                    Examples:
                    [
                        new TsEndpointExample("application/json", "created", """{"id":"ord_123"}"""),
                    ]),
                new TsResponseType(
                    422,
                    new TsType.TypeRef("ValidationProblem"),
                    "Validation failed",
                    [
                        new TsEndpointExample(
                            "application/json",
                            "validationProblem",
                            ComponentExampleId: "validation-problem",
                            ResolvedJson: """{"message":"Validation failed"}"""),
                    ]),
            ],
            RequestExamples:
            [
                new TsEndpointExample("application/json", Json: """{"customerId":"cus_123"}"""),
            ]);

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Endpoint_With_RequestType_Validates()
    {
        var endpoint = new TsEndpointDefinition(
            "createBuyer",
            "POST",
            "/buyers",
            [],
            null,
            "BuyersController",
            [new TsResponseType(202, null)],
            RequestType: new TsType.TypeRef("CreateBuyerRequest"));

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Full_Contract_Golden_Validates()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["CreateOrderRequest"] = new("CreateOrderRequest", [], [
                new("customerId", new TsType.Primitive("string", Format: "uuid"), false),
                new("items", new TsType.Array(new TsType.TypeRef("OrderItemDto")), false),
                new("notes", new TsType.Nullable(new TsType.Primitive("string")), true),
                new("priority", new TsType.StringUnion(["low", "normal", "urgent"]), false),
            ], Description: "Request to create an order"),
        };

        var enums = new Dictionary<string, TsType>
        {
            ["OrderStatus"] = new TsType.StringUnion(["Pending", "Confirmed", "Shipped", "Cancelled"]),
        };

        var endpoints = new List<TsEndpointDefinition>
        {
            new("createOrder", "POST", "/api/orders",
                [new TsEndpointParam("body", new TsType.TypeRef("CreateOrderRequest"), ParamSource.Body)],
                new TsType.TypeRef("CreateOrderResponse"),
                "OrdersController",
                [
                    new TsResponseType(201, new TsType.TypeRef("CreateOrderResponse")),
                    new TsResponseType(422, new TsType.TypeRef("ValidationProblem")),
                ],
                Summary: "Create an order"),
        };

        var json = ContractEmitter.Emit(definitions, enums, endpoints);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Property_With_Constraints_Validates()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["Constrained"] = new("Constrained", [], [
                new("name", new TsType.Primitive("string"), false,
                    Constraints: new TsPropertyConstraints(MinLength: 1, MaxLength: 100)),
                new("age", new TsType.Primitive("number"), false,
                    Constraints: new TsPropertyConstraints(Minimum: 0, Maximum: 150)),
                new("tags", new TsType.Array(new TsType.Primitive("string")), false,
                    Constraints: new TsPropertyConstraints(MinItems: 1, MaxItems: 10, UniqueItems: true)),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Enum_Missing_Name_Rejected()
    {
        var json = """{"types":[],"enums":[{"values":["A","B"]}],"endpoints":[]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Unknown_TsType_Kind_Rejected()
    {
        var json = """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","type":{"kind":"invalid"},"optional":false}]}],"enums":[],"endpoints":[]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Extra_Root_Property_Rejected()
    {
        var json = """{"types":[],"enums":[],"endpoints":[],"extra":true}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Missing_Required_Endpoint_Field_Rejected()
    {
        var json = """{"types":[],"enums":[],"endpoints":[{"name":"foo","routeTemplate":"/","params":[],"controllerName":"C","responses":[]}]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Deeply_Nested_Types_Validate()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["Dto"] = new("Dto", [], [
                new("scores", new TsType.Array(new TsType.Nullable(new TsType.TypeRef("Score"))), false),
                new("tags", new TsType.Dictionary(new TsType.Array(new TsType.Primitive("string"))), false),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Property_Missing_Required_Fields_Rejected()
    {
        // Missing "optional" field
        var json = """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","type":{"kind":"primitive","type":"string"}}]}],"enums":[],"endpoints":[]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Property_Missing_Type_Rejected()
    {
        // Missing "type" field
        var json = """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","optional":false}]}],"enums":[],"endpoints":[]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Empty_StringUnion_Values_Rejected()
    {
        var json = """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","type":{"kind":"stringUnion","values":[]},"optional":false}]}],"enums":[],"endpoints":[]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Invalid_ParamSource_Rejected()
    {
        var json = """{"types":[],"enums":[],"endpoints":[{"name":"foo","httpMethod":"GET","routeTemplate":"/","params":[{"name":"x","type":{"kind":"primitive","type":"string"},"source":"header"}],"controllerName":"C","responses":[]}]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Endpoint_Example_Missing_Json_And_ComponentExampleId_Rejected()
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
                                "statusCode": 201,
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

        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    private static string FormatErrors(EvaluationResults results) =>
        string.Join("\n", results.Details
            .Where(d => !d.IsValid && d.Errors is not null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")));
}
