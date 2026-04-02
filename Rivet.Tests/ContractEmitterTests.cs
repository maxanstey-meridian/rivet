using System.Text.Json;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ContractEmitterTests
{
    [Fact]
    public void Empty_Contract_Has_Three_Top_Level_Arrays()
    {
        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            []);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("types").ValueKind);
        Assert.Equal(0, root.GetProperty("types").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("enums").ValueKind);
        Assert.Equal(0, root.GetProperty("enums").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("endpoints").ValueKind);
        Assert.Equal(0, root.GetProperty("endpoints").GetArrayLength());
    }

    [Fact]
    public void Types_Contains_Serialized_TypeDefinition()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["UserDto"] = new("UserDto", [], [
                new("id", new TsType.Primitive("number"), false),
                new("name", new TsType.Primitive("string"), false),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        using var doc = JsonDocument.Parse(json);
        var types = doc.RootElement.GetProperty("types");

        Assert.Equal(1, types.GetArrayLength());
        var t = types[0];
        Assert.Equal("UserDto", t.GetProperty("name").GetString());
        var props = t.GetProperty("properties");
        Assert.Equal(2, props.GetArrayLength());
        Assert.Equal("primitive", props[0].GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("number", props[0].GetProperty("type").GetProperty("type").GetString());
    }

    [Fact]
    public void Enums_Projected_With_Name_And_Values()
    {
        var enums = new Dictionary<string, TsType>
        {
            ["Status"] = new TsType.StringUnion(["Active", "Inactive", "Pending"]),
        };

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), enums, []);
        using var doc = JsonDocument.Parse(json);
        var enumArr = doc.RootElement.GetProperty("enums");

        Assert.Equal(1, enumArr.GetArrayLength());
        Assert.Equal("Status", enumArr[0].GetProperty("name").GetString());
        var values = enumArr[0].GetProperty("values");
        Assert.Equal(3, values.GetArrayLength());
        Assert.Equal("Active", values[0].GetString());
        Assert.Equal("Inactive", values[1].GetString());
        Assert.Equal("Pending", values[2].GetString());
    }

    [Fact]
    public void Endpoint_Serializes_Core_Fields_As_CamelCase()
    {
        var endpoint = new TsEndpointDefinition(
            "getUser",
            "GET",
            "/api/users/{id}",
            [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
            new TsType.TypeRef("UserDto"),
            "UsersController",
            [new TsResponseType(200, new TsType.TypeRef("UserDto"))]);

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var ep = doc.RootElement.GetProperty("endpoints")[0];

        Assert.Equal("getUser", ep.GetProperty("name").GetString());
        Assert.Equal("GET", ep.GetProperty("httpMethod").GetString());
        Assert.Equal("/api/users/{id}", ep.GetProperty("routeTemplate").GetString());
        Assert.Equal("UsersController", ep.GetProperty("controllerName").GetString());
        Assert.Equal("ref", ep.GetProperty("returnType").GetProperty("kind").GetString());
    }

    [Fact]
    public void ParamSource_Serializes_As_CamelCase_String()
    {
        var endpoint = new TsEndpointDefinition(
            "upload",
            "POST",
            "/api/upload",
            [
                new TsEndpointParam("data", new TsType.TypeRef("Payload"), ParamSource.Body),
                new TsEndpointParam("file", new TsType.Primitive("unknown"), ParamSource.File),
                new TsEndpointParam("field", new TsType.Primitive("string"), ParamSource.FormField),
            ],
            null,
            "UploadController",
            [new TsResponseType(204, null)]);

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var pars = doc.RootElement.GetProperty("endpoints")[0].GetProperty("params");

        Assert.Equal("body", pars[0].GetProperty("source").GetString());
        Assert.Equal("file", pars[1].GetProperty("source").GetString());
        Assert.Equal("formField", pars[2].GetProperty("source").GetString());
    }

    [Fact]
    public void Null_And_Default_Fields_Omitted()
    {
        var endpoint = new TsEndpointDefinition(
            "list",
            "GET",
            "/api/items",
            [],
            null,
            "ItemsController",
            [new TsResponseType(200, null)]);

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var ep = doc.RootElement.GetProperty("endpoints")[0];

        Assert.False(ep.TryGetProperty("returnType", out _));
        Assert.False(ep.TryGetProperty("summary", out _));
        Assert.False(ep.TryGetProperty("description", out _));
        Assert.False(ep.TryGetProperty("security", out _));
        Assert.False(ep.TryGetProperty("fileContentType", out _));
        Assert.False(ep.TryGetProperty("inputTypeName", out _));
        Assert.False(ep.TryGetProperty("isFormEncoded", out _));
    }

    [Fact]
    public void Responses_Serialize_With_StatusCode_And_DataType()
    {
        var endpoint = new TsEndpointDefinition(
            "getUser",
            "GET",
            "/api/users/{id}",
            [],
            new TsType.TypeRef("UserDto"),
            "UsersController",
            [
                new TsResponseType(200, new TsType.TypeRef("UserDto")),
                new TsResponseType(404, null, "Not found"),
            ]);

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var responses = doc.RootElement.GetProperty("endpoints")[0].GetProperty("responses");

        Assert.Equal(2, responses.GetArrayLength());
        Assert.Equal(200, responses[0].GetProperty("statusCode").GetInt32());
        Assert.Equal("ref", responses[0].GetProperty("dataType").GetProperty("kind").GetString());
        Assert.Equal(404, responses[1].GetProperty("statusCode").GetInt32());
        Assert.False(responses[1].TryGetProperty("dataType", out _));
        Assert.Equal("Not found", responses[1].GetProperty("description").GetString());

        // 200 response has no description — omitted
        Assert.False(responses[0].TryGetProperty("description", out _));
    }

    [Fact]
    public void Endpoint_Examples_Serialize_With_Request_And_Response_Metadata()
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
                        new TsEndpointExample("application/json", "created", """{"id":"ord_123","status":"created"}"""),
                        new TsEndpointExample("application/json", "queued", """{"id":"ord_124","status":"queued"}"""),
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
        using var doc = JsonDocument.Parse(json);
        var ep = doc.RootElement.GetProperty("endpoints")[0];

        var requestExamples = ep.GetProperty("requestExamples");
        Assert.Single(requestExamples.EnumerateArray());
        var requestExample = requestExamples[0];
        Assert.Equal("application/json", requestExample.GetProperty("mediaType").GetString());
        Assert.Equal("""{"customerId":"cus_123"}""", requestExample.GetProperty("json").GetString());
        Assert.False(requestExample.TryGetProperty("name", out _));
        Assert.False(requestExample.TryGetProperty("componentExampleId", out _));
        Assert.False(requestExample.TryGetProperty("resolvedJson", out _));

        var responses = ep.GetProperty("responses");
        var createdExamples = responses[0].GetProperty("examples");
        Assert.Equal(2, createdExamples.GetArrayLength());
        Assert.Equal("created", createdExamples[0].GetProperty("name").GetString());
        Assert.Equal("""{"id":"ord_123","status":"created"}""", createdExamples[0].GetProperty("json").GetString());
        Assert.Equal("queued", createdExamples[1].GetProperty("name").GetString());
        Assert.Equal("""{"id":"ord_124","status":"queued"}""", createdExamples[1].GetProperty("json").GetString());

        var validationExamples = responses[1].GetProperty("examples");
        Assert.Single(validationExamples.EnumerateArray());
        var refBackedExample = validationExamples[0];
        Assert.Equal("validationProblem", refBackedExample.GetProperty("name").GetString());
        Assert.Equal("validation-problem", refBackedExample.GetProperty("componentExampleId").GetString());
        Assert.Equal("""{"message":"Validation failed"}""", refBackedExample.GetProperty("resolvedJson").GetString());
        Assert.False(refBackedExample.TryGetProperty("json", out _));
    }

    [Fact]
    public void Security_Serializes_As_CamelCase()
    {
        var endpoint = new TsEndpointDefinition(
            "secure",
            "GET",
            "/api/secure",
            [],
            null,
            "SecureController",
            [],
            Security: new EndpointSecurity(false, "Bearer"));

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var sec = doc.RootElement.GetProperty("endpoints")[0].GetProperty("security");

        Assert.False(sec.GetProperty("isAnonymous").GetBoolean());
        Assert.Equal("Bearer", sec.GetProperty("scheme").GetString());
    }

    [Fact]
    public void Property_Optional_Field_Serializes()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["Dto"] = new("Dto", [], [
                new("required", new TsType.Primitive("string"), false),
                new("optional", new TsType.Primitive("string"), true),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("types")[0].GetProperty("properties");

        Assert.False(props[0].GetProperty("optional").GetBoolean());
        Assert.True(props[1].GetProperty("optional").GetBoolean());
    }

    [Fact]
    public void Property_Types_Serialize_All_Kinds()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["CreateOrderRequest"] = new("CreateOrderRequest", [], [
                new("customerId", new TsType.Primitive("string", Format: "uuid"), false),
                new("items", new TsType.Array(new TsType.TypeRef("OrderItemDto")), false),
                new("notes", new TsType.Nullable(new TsType.Primitive("string")), true),
                new("priority", new TsType.StringUnion(["low", "normal", "urgent"]), false),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("types")[0].GetProperty("properties");

        // customerId: primitive with format
        var customerIdType = props[0].GetProperty("type");
        Assert.Equal("primitive", customerIdType.GetProperty("kind").GetString());
        Assert.Equal("string", customerIdType.GetProperty("type").GetString());
        Assert.Equal("uuid", customerIdType.GetProperty("format").GetString());

        // items: array of ref
        var itemsType = props[1].GetProperty("type");
        Assert.Equal("array", itemsType.GetProperty("kind").GetString());
        Assert.Equal("ref", itemsType.GetProperty("element").GetProperty("kind").GetString());
        Assert.Equal("OrderItemDto", itemsType.GetProperty("element").GetProperty("name").GetString());

        // notes: nullable inner primitive
        var notesType = props[2].GetProperty("type");
        Assert.Equal("nullable", notesType.GetProperty("kind").GetString());
        Assert.Equal("primitive", notesType.GetProperty("inner").GetProperty("kind").GetString());
        Assert.Equal("string", notesType.GetProperty("inner").GetProperty("type").GetString());

        // priority: stringUnion
        var priorityType = props[3].GetProperty("type");
        Assert.Equal("stringUnion", priorityType.GetProperty("kind").GetString());
        var values = priorityType.GetProperty("values");
        Assert.Equal(3, values.GetArrayLength());
        Assert.Equal("low", values[0].GetString());
        Assert.Equal("normal", values[1].GetString());
        Assert.Equal("urgent", values[2].GetString());
    }

    [Fact]
    public void Type_Description_Included_When_Present()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["CreateOrderRequest"] = new("CreateOrderRequest", [], [
                new("amount", new TsType.Primitive("number"), false),
            ], Description: "Request to create an order"),
            ["SimpleDto"] = new("SimpleDto", [], [
                new("id", new TsType.Primitive("number"), false),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        using var doc = JsonDocument.Parse(json);
        var types = doc.RootElement.GetProperty("types");

        var withDesc = types.EnumerateArray().First(t => t.GetProperty("name").GetString() == "CreateOrderRequest");
        Assert.Equal("Request to create an order", withDesc.GetProperty("description").GetString());

        var withoutDesc = types.EnumerateArray().First(t => t.GetProperty("name").GetString() == "SimpleDto");
        Assert.False(withoutDesc.TryGetProperty("description", out _));
    }

    [Fact]
    public void Endpoint_Body_Param_And_Multiple_Responses()
    {
        var endpoint = new TsEndpointDefinition(
            "createOrder",
            "POST",
            "/api/orders",
            [new TsEndpointParam("body", new TsType.TypeRef("CreateOrderRequest"), ParamSource.Body)],
            new TsType.TypeRef("CreateOrderResponse"),
            "OrdersController",
            [
                new TsResponseType(201, new TsType.TypeRef("CreateOrderResponse")),
                new TsResponseType(422, new TsType.TypeRef("ValidationProblem")),
            ],
            Summary: "Create an order");

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var ep = doc.RootElement.GetProperty("endpoints")[0];

        // body param
        var param = ep.GetProperty("params")[0];
        Assert.Equal("body", param.GetProperty("source").GetString());
        Assert.Equal("ref", param.GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("CreateOrderRequest", param.GetProperty("type").GetProperty("name").GetString());

        // responses
        var responses = ep.GetProperty("responses");
        Assert.Equal(2, responses.GetArrayLength());
        Assert.Equal(201, responses[0].GetProperty("statusCode").GetInt32());
        Assert.Equal("ref", responses[0].GetProperty("dataType").GetProperty("kind").GetString());
        Assert.Equal(422, responses[1].GetProperty("statusCode").GetInt32());
        Assert.Equal("ref", responses[1].GetProperty("dataType").GetProperty("kind").GetString());

        // summary present, description and security absent
        Assert.Equal("Create an order", ep.GetProperty("summary").GetString());
        Assert.False(ep.TryGetProperty("description", out _));
        Assert.False(ep.TryGetProperty("security", out _));
    }

    [Fact]
    public void Deeply_Nested_Type_Compositions_Serialize()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["Dto"] = new("Dto", [], [
                // List<int?> → Array(Nullable(Primitive))
                new("scores", new TsType.Array(new TsType.Nullable(new TsType.TypeRef("Score"))), false),
                // Dictionary<string, string[]> → Dictionary(Array(Primitive))
                new("tags", new TsType.Dictionary(new TsType.Array(new TsType.Primitive("string"))), false),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("types")[0].GetProperty("properties");

        // Array(Nullable(TypeRef))
        var scores = props[0].GetProperty("type");
        Assert.Equal("array", scores.GetProperty("kind").GetString());
        var inner = scores.GetProperty("element");
        Assert.Equal("nullable", inner.GetProperty("kind").GetString());
        Assert.Equal("ref", inner.GetProperty("inner").GetProperty("kind").GetString());
        Assert.Equal("Score", inner.GetProperty("inner").GetProperty("name").GetString());

        // Dictionary(Array(Primitive))
        var tags = props[1].GetProperty("type");
        Assert.Equal("dictionary", tags.GetProperty("kind").GetString());
        var dictVal = tags.GetProperty("value");
        Assert.Equal("array", dictVal.GetProperty("kind").GetString());
        Assert.Equal("primitive", dictVal.GetProperty("element").GetProperty("kind").GetString());
        Assert.Equal("string", dictVal.GetProperty("element").GetProperty("type").GetString());
    }

    [Fact]
    public void Property_Constraint_Values_Serialize()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["Dto"] = new("Dto", [], [
                new("name", new TsType.Primitive("string"), false,
                    Constraints: new TsPropertyConstraints(MinLength: 1, MaxLength: 100, Pattern: "^[a-z]+$")),
                new("score", new TsType.Primitive("number"), false,
                    Constraints: new TsPropertyConstraints(Minimum: 0, Maximum: 999.5,
                        ExclusiveMinimum: -1, ExclusiveMaximum: 1000, MultipleOf: 0.5)),
                new("tags", new TsType.Array(new TsType.Primitive("string")), false,
                    Constraints: new TsPropertyConstraints(MinItems: 1, MaxItems: 10, UniqueItems: true)),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("types")[0].GetProperty("properties");

        // String constraints
        var nameConstraints = props[0].GetProperty("constraints");
        Assert.Equal(1, nameConstraints.GetProperty("minLength").GetInt32());
        Assert.Equal(100, nameConstraints.GetProperty("maxLength").GetInt32());
        Assert.Equal("^[a-z]+$", nameConstraints.GetProperty("pattern").GetString());

        // Numeric constraints
        var scoreConstraints = props[1].GetProperty("constraints");
        Assert.Equal(0, scoreConstraints.GetProperty("minimum").GetDouble());
        Assert.Equal(999.5, scoreConstraints.GetProperty("maximum").GetDouble());
        Assert.Equal(-1, scoreConstraints.GetProperty("exclusiveMinimum").GetDouble());
        Assert.Equal(1000, scoreConstraints.GetProperty("exclusiveMaximum").GetDouble());
        Assert.Equal(0.5, scoreConstraints.GetProperty("multipleOf").GetDouble());

        // Array constraints
        var tagConstraints = props[2].GetProperty("constraints");
        Assert.Equal(1, tagConstraints.GetProperty("minItems").GetInt32());
        Assert.Equal(10, tagConstraints.GetProperty("maxItems").GetInt32());
        Assert.True(tagConstraints.GetProperty("uniqueItems").GetBoolean());
    }

    [Fact]
    public void Endpoint_Summary_And_Description_Both_Emit()
    {
        var endpoint = new TsEndpointDefinition(
            "getUser",
            "GET",
            "/api/users/{id}",
            [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
            new TsType.TypeRef("UserDto"),
            "UsersController",
            [new TsResponseType(200, new TsType.TypeRef("UserDto"))],
            Summary: "Get a user by ID",
            Description: "Returns the full user profile including nested address data.");

        var json = ContractEmitter.Emit(new Dictionary<string, TsTypeDefinition>(), new Dictionary<string, TsType>(), [endpoint]);
        using var doc = JsonDocument.Parse(json);
        var ep = doc.RootElement.GetProperty("endpoints")[0];

        Assert.Equal("Get a user by ID", ep.GetProperty("summary").GetString());
        Assert.Equal("Returns the full user profile including nested address data.", ep.GetProperty("description").GetString());
    }

    [Fact]
    public void TypeParameters_Emitted_On_Type_Definition()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["PagedResult"] = new("PagedResult", ["T", "U"], [
                new("items", new TsType.Array(new TsType.TypeParam("T")), false),
                new("meta", new TsType.TypeParam("U"), false),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        using var doc = JsonDocument.Parse(json);
        var typeParams = doc.RootElement.GetProperty("types")[0].GetProperty("typeParameters");

        Assert.Equal(2, typeParams.GetArrayLength());
        Assert.Equal("T", typeParams[0].GetString());
        Assert.Equal("U", typeParams[1].GetString());
    }

    [Fact]
    public void Property_Metadata_Fields_Serialize()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["Dto"] = new("Dto", [], [
                new("allMeta", new TsType.Primitive("string"), false,
                    IsDeprecated: true,
                    Format: "email",
                    DefaultValue: "user@example.com",
                    Description: "The user email",
                    Example: "alice@example.com",
                    IsReadOnly: true,
                    IsWriteOnly: false),
                new("bare", new TsType.Primitive("string"), false),
            ]),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("types")[0].GetProperty("properties");

        // Property with all metadata
        var full = props[0];
        Assert.True(full.GetProperty("deprecated").GetBoolean());
        Assert.Equal("email", full.GetProperty("format").GetString());
        Assert.Equal("user@example.com", full.GetProperty("defaultValue").GetString());
        Assert.Equal("The user email", full.GetProperty("description").GetString());
        Assert.Equal("alice@example.com", full.GetProperty("example").GetString());
        Assert.True(full.GetProperty("readOnly").GetBoolean());

        // writeOnly: false should be omitted (WhenWritingDefault)
        Assert.False(full.TryGetProperty("writeOnly", out _));

        // Bare property: default-valued booleans omitted
        var bare = props[1];
        Assert.False(bare.TryGetProperty("deprecated", out _));
        Assert.False(bare.TryGetProperty("format", out _));
        Assert.False(bare.TryGetProperty("defaultValue", out _));
        Assert.False(bare.TryGetProperty("description", out _));
        Assert.False(bare.TryGetProperty("example", out _));
        Assert.False(bare.TryGetProperty("readOnly", out _));
        Assert.False(bare.TryGetProperty("writeOnly", out _));
    }

    [Fact]
    public void Full_Contract_Matches_Slice_Spec()
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
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Top-level counts
        Assert.Equal(1, root.GetProperty("types").GetArrayLength());
        Assert.Equal(1, root.GetProperty("enums").GetArrayLength());
        Assert.Equal(1, root.GetProperty("endpoints").GetArrayLength());

        // Type section
        var type = root.GetProperty("types")[0];
        Assert.Equal("CreateOrderRequest", type.GetProperty("name").GetString());
        Assert.Equal("Request to create an order", type.GetProperty("description").GetString());
        Assert.Equal(4, type.GetProperty("properties").GetArrayLength());
        Assert.Equal("customerId", type.GetProperty("properties")[0].GetProperty("name").GetString());
        Assert.False(type.GetProperty("properties")[0].GetProperty("optional").GetBoolean());
        Assert.True(type.GetProperty("properties")[2].GetProperty("optional").GetBoolean());

        // Enum section
        var enumDef = root.GetProperty("enums")[0];
        Assert.Equal("OrderStatus", enumDef.GetProperty("name").GetString());
        var enumValues = enumDef.GetProperty("values");
        Assert.Equal(4, enumValues.GetArrayLength());
        Assert.Equal("Pending", enumValues[0].GetString());
        Assert.Equal("Cancelled", enumValues[3].GetString());

        // Endpoint section
        var ep = root.GetProperty("endpoints")[0];
        Assert.Equal("createOrder", ep.GetProperty("name").GetString());
        Assert.Equal("POST", ep.GetProperty("httpMethod").GetString());
        Assert.Equal("/api/orders", ep.GetProperty("routeTemplate").GetString());
        Assert.Equal("Create an order", ep.GetProperty("summary").GetString());
        Assert.False(ep.TryGetProperty("description", out _));
        Assert.False(ep.TryGetProperty("security", out _));

        // Endpoint param
        var param = ep.GetProperty("params")[0];
        Assert.Equal("body", param.GetProperty("name").GetString());
        Assert.Equal("body", param.GetProperty("source").GetString());
        Assert.Equal("ref", param.GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("CreateOrderRequest", param.GetProperty("type").GetProperty("name").GetString());

        // Return type
        Assert.Equal("ref", ep.GetProperty("returnType").GetProperty("kind").GetString());
        Assert.Equal("CreateOrderResponse", ep.GetProperty("returnType").GetProperty("name").GetString());

        // Responses
        var responses = ep.GetProperty("responses");
        Assert.Equal(2, responses.GetArrayLength());
        Assert.Equal(201, responses[0].GetProperty("statusCode").GetInt32());
        Assert.Equal("CreateOrderResponse", responses[0].GetProperty("dataType").GetProperty("name").GetString());
        Assert.Equal(422, responses[1].GetProperty("statusCode").GetInt32());
        Assert.Equal("ValidationProblem", responses[1].GetProperty("dataType").GetProperty("name").GetString());
    }

    [Fact]
    public void Full_Contract_Contains_All_Sections()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["UserDto"] = new("UserDto", [], [
                new("id", new TsType.Primitive("number"), false),
                new("status", new TsType.TypeRef("Status"), false),
            ]),
        };

        var enums = new Dictionary<string, TsType>
        {
            ["Status"] = new TsType.StringUnion(["Active", "Inactive"]),
        };

        var endpoints = new List<TsEndpointDefinition>
        {
            new("getUser", "GET", "/api/users/{id}",
                [new TsEndpointParam("id", new TsType.Primitive("number"), ParamSource.Route)],
                new TsType.TypeRef("UserDto"),
                "UsersController",
                [new TsResponseType(200, new TsType.TypeRef("UserDto"))]),
        };

        var json = ContractEmitter.Emit(definitions, enums, endpoints);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("types").GetArrayLength());
        Assert.Equal("UserDto", root.GetProperty("types")[0].GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("enums").GetArrayLength());
        Assert.Equal("Status", root.GetProperty("enums")[0].GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("endpoints").GetArrayLength());
        Assert.Equal("getUser", root.GetProperty("endpoints")[0].GetProperty("name").GetString());
    }
}
