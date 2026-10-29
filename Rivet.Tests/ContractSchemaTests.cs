using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ContractSchemaTests
{
    private static readonly string _schemaPath = Path.Combine(
        AppContext.BaseDirectory,
        "../../../../rivet-contract-schema.json"
    );

    private static readonly JsonSchema _schema = LoadSchema();

    private static readonly JsonSerializerOptions _modelOptions = new(JsonContractReader.Options)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static JsonSchema LoadSchema() => JsonSchema.FromText(File.ReadAllText(_schemaPath));

    private static EvaluationResults Validate(string json)
    {
        var node = JsonDocument.Parse(json).RootElement;
        return _schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.List });
    }

    [Fact]
    public void Empty_Contract_Validates()
    {
        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            []
        );

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
            ["AllKinds"] = new(
                "AllKinds",
                ["T"],
                [
                    new("prim", new TsType.Primitive("string"), false),
                    new(
                        "primFmt",
                        new TsType.Primitive("string", Format: "uuid", CSharpType: "Guid"),
                        false
                    ),
                    new("nullable", new TsType.Nullable(new TsType.Primitive("number")), true),
                    new("arr", new TsType.Array(new TsType.TypeRef("ItemDto")), false),
                    new(
                        "dict",
                        new TsType.Dictionary(
                            new TsType.Primitive("string"),
                            new TsType.Primitive("number")
                        ),
                        false
                    ),
                    new("strUnion", new TsType.StringUnion(["a", "b"]), false),
                    new("typeRef", new TsType.TypeRef("Other"), false),
                    new(
                        "generic",
                        new TsType.Generic("Page", [new TsType.TypeRef("ItemDto")]),
                        false
                    ),
                    new("typeParam", new TsType.TypeParam("T"), false),
                    new("brand", new TsType.Brand("Email", new TsType.Primitive("string")), false),
                    new(
                        "inline",
                        new TsType.InlineObject([
                            new("key", new TsType.Primitive("string")),
                            new("val", new TsType.Primitive("number")),
                        ]),
                        false
                    ),
                    new(
                        "tagged",
                        new TsType.TaggedUnion(
                            "kind",
                            [
                                new TsType.TaggedUnionVariant(
                                    "hidden",
                                    new TsType.InlineObject([
                                        new("kind", new TsType.StringUnion(["hidden"])),
                                        new("message", new TsType.Primitive("string")),
                                    ])
                                ),
                                new TsType.TaggedUnionVariant(
                                    "shown",
                                    new TsType.InlineObject([
                                        new("kind", new TsType.StringUnion(["shown"])),
                                        new("summary", new TsType.TypeRef("Summary")),
                                    ])
                                ),
                            ]
                        ),
                        false
                    ),
                ]
            ),
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
                new TsEndpointParam(
                    "body",
                    new TsType.TypeRef("CreateOrderRequest"),
                    ParamSource.Body
                ),
            ],
            new TsType.TypeRef("CreateOrderResponse"),
            "OrdersController",
            [
                new TsResponseType(201, new TsType.TypeRef("CreateOrderResponse")),
                new TsResponseType(
                    422,
                    new TsType.TypeRef("ValidationProblem"),
                    "Validation failed"
                ),
            ],
            Summary: "Create an order"
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );
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
            [
                new TsEndpointParam(
                    "body",
                    new TsType.TypeRef("CreateOrderRequest"),
                    ParamSource.Body
                ),
            ],
            new TsType.TypeRef("CreateOrderResponse"),
            "OrdersController",
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
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Endpoint_Example_With_ComponentExampleId_Only_Validates()
    {
        var endpoint = new TsEndpointDefinition(
            "createOrder",
            "POST",
            "/api/orders",
            [],
            null,
            "OrdersController",
            [
                new TsResponseType(
                    202,
                    null,
                    Examples:
                    [
                        new TsEndpointExample(
                            "application/json",
                            "accepted",
                            ComponentExampleId: "order-accepted"
                        ),
                    ]
                ),
            ]
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );
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
            RequestType: new TsType.TypeRef("CreateBuyerRequest")
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Endpoint_With_Binary_And_ContentType_Metadata_Validates()
    {
        var endpoint = new TsEndpointDefinition(
            "render",
            "POST",
            "/render",
            [],
            new TsType.Primitive("string"),
            "RenderController",
            [new TsResponseType(200, new TsType.Primitive("string"))],
            BinaryRequestContentType: "application/pdf",
            RequestContentTypeOverride: "text/plain",
            ResponseContentTypeOverride: "text/html"
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Full_Contract_Golden_Validates()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["CreateOrderRequest"] = new(
                "CreateOrderRequest",
                [],
                [
                    new("customerId", new TsType.Primitive("string", Format: "uuid"), false),
                    new("items", new TsType.Array(new TsType.TypeRef("OrderItemDto")), false),
                    new("notes", new TsType.Nullable(new TsType.Primitive("string")), true),
                    new("priority", new TsType.StringUnion(["low", "normal", "urgent"]), false),
                ],
                Description: "Request to create an order"
            ),
        };

        var enums = new Dictionary<string, TsType>
        {
            ["OrderStatus"] = new TsType.StringUnion([
                "Pending",
                "Confirmed",
                "Shipped",
                "Cancelled",
            ]),
        };

        var endpoints = new List<TsEndpointDefinition>
        {
            new(
                "createOrder",
                "POST",
                "/api/orders",
                [
                    new TsEndpointParam(
                        "body",
                        new TsType.TypeRef("CreateOrderRequest"),
                        ParamSource.Body
                    ),
                ],
                new TsType.TypeRef("CreateOrderResponse"),
                "OrdersController",
                [
                    new TsResponseType(201, new TsType.TypeRef("CreateOrderResponse")),
                    new TsResponseType(422, new TsType.TypeRef("ValidationProblem")),
                ],
                Summary: "Create an order"
            ),
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
            ["Constrained"] = new(
                "Constrained",
                [],
                [
                    new(
                        "name",
                        new TsType.Primitive("string"),
                        false,
                        Constraints: new TsPropertyConstraints(MinLength: 1, MaxLength: 100)
                    ),
                    new(
                        "age",
                        new TsType.Primitive("number"),
                        false,
                        Constraints: new TsPropertyConstraints(Minimum: 0, Maximum: 150)
                    ),
                    new(
                        "tags",
                        new TsType.Array(new TsType.Primitive("string")),
                        false,
                        Constraints: new TsPropertyConstraints(
                            MinItems: 1,
                            MaxItems: 10,
                            UniqueItems: true
                        )
                    ),
                ]
            ),
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
        var json =
            """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","type":{"kind":"invalid"},"optional":false}]}],"enums":[],"endpoints":[]}""";
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
    public void Document_Security_Validates_And_Reads()
    {
        // G.7: the document's own requirement, the C# path's [assembly: RivetGlobalSecurity*].
        var json = """
            {"types":[],"enums":[],"endpoints":[],
             "security":{"alternatives":[{"schemes":[{"name":"oauth","scopes":["read"]}]}]}}
            """;

        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
        var scheme = Assert.Single(
            Assert.Single(JsonContractReader.Read(json).Security!.Alternatives).Schemes
        );
        Assert.Equal("oauth", scheme.Name);
        Assert.Equal(["read"], scheme.Scopes);
    }

    [Fact]
    public void Document_Security_In_The_Retired_Shape_Is_Rejected()
    {
        var json = """
            {"types":[],"enums":[],"endpoints":[],"security":{"isAnonymous":false,"scheme":"oauth"}}
            """;

        Assert.False(Validate(json).IsValid);
        Assert.Throws<JsonException>(() => JsonContractReader.Read(json));
    }

    [Fact]
    public void Missing_Required_Endpoint_Field_Rejected()
    {
        var json =
            """{"types":[],"enums":[],"endpoints":[{"name":"foo","routeTemplate":"/","params":[],"controllerName":"C","responses":[]}]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    /// <summary>
    /// The reader's [JsonRequired] members are the schema's required ones, so neither side can
    /// let a producer leave out what the other insists on. The property sets match too, since
    /// the reader refuses a property it doesn't map, and a member the schema lets be null is
    /// nullable in the reader, which respects nullable annotations. contractEnum has its own
    /// check (exactly one of values or intValues).
    /// </summary>
    [Theory]
    [InlineData("", typeof(JsonContractReader.RivetContract))]
    [InlineData("$defs/typeDefinition", typeof(TsTypeDefinition))]
    [InlineData("$defs/typeMetadata", typeof(TsTypeMetadata))]
    [InlineData("$defs/scalarMetadata", typeof(TsScalarMetadata))]
    [InlineData("$defs/schemaXmlMetadata", typeof(TsSchemaXmlMetadata))]
    [InlineData("$defs/tsPropertyConstraints", typeof(TsPropertyConstraints))]
    [InlineData("$defs/tsPropertyDefinition", typeof(TsPropertyDefinition))]
    [InlineData("$defs/tsType/oneOf/0", typeof(TsType.Primitive))]
    [InlineData("$defs/tsType/oneOf/1", typeof(TsType.Nullable))]
    [InlineData("$defs/tsType/oneOf/2", typeof(TsType.Array))]
    [InlineData("$defs/tsType/oneOf/3", typeof(TsType.Dictionary))]
    [InlineData("$defs/tsType/oneOf/4", typeof(TsType.StringUnion))]
    [InlineData("$defs/tsType/oneOf/5", typeof(TsType.TypeRef))]
    [InlineData("$defs/tsType/oneOf/6", typeof(TsType.Generic))]
    [InlineData("$defs/tsType/oneOf/7", typeof(TsType.TypeParam))]
    [InlineData("$defs/tsType/oneOf/8", typeof(TsType.Brand))]
    [InlineData("$defs/tsType/oneOf/9", typeof(TsType.IntUnion))]
    [InlineData("$defs/tsType/oneOf/10", typeof(TsType.Literal))]
    [InlineData("$defs/tsType/oneOf/11", typeof(TsType.InlineObject))]
    [InlineData("$defs/tsType/oneOf/12", typeof(TsType.TaggedUnion))]
    [InlineData(
        "$defs/tsType/oneOf/12/properties/variants/items",
        typeof(TsType.TaggedUnionVariant)
    )]
    [InlineData("$defs/tsType/oneOf/13", typeof(TsType.Union))]
    [InlineData("$defs/endpointDefinition", typeof(TsEndpointDefinition))]
    [InlineData("$defs/endpointParam", typeof(TsEndpointParam))]
    [InlineData("$defs/endpointExample", typeof(TsEndpointExample))]
    [InlineData("$defs/responseType", typeof(TsResponseType))]
    [InlineData("$defs/responseHeader", typeof(TsResponseHeader))]
    [InlineData("$defs/mediaTypeContent", typeof(TsMediaTypeContent))]
    [InlineData("$defs/queryAuth", typeof(QueryAuthMetadata))]
    [InlineData("$defs/securityRequirements", typeof(SecurityRequirements))]
    [InlineData(
        "$defs/securityRequirements/properties/alternatives/items",
        typeof(SecurityRequirement)
    )]
    [InlineData(
        "$defs/securityRequirements/properties/alternatives/items/properties/schemes/items",
        typeof(SecurityRequirementScheme)
    )]
    [InlineData("$defs/operationProvenance", typeof(OpenApiOperationProvenance))]
    [InlineData(
        "$defs/operationProvenance/properties/serverOverride/items",
        typeof(OpenApiServerProvenance)
    )]
    [InlineData(
        "$defs/operationProvenance/properties/serverOverride/items/properties/variables/items",
        typeof(OpenApiServerVariableProvenance)
    )]
    [InlineData(
        "$defs/operationProvenance/properties/rivetIdentity",
        typeof(OpenApiRivetIdentityProvenance)
    )]
    [InlineData(
        "$defs/operationProvenance/properties/parameterComponentReferences/items",
        typeof(OpenApiParameterComponentReference)
    )]
    [InlineData(
        "$defs/operationProvenance/properties/responseComponentReferences/items",
        typeof(OpenApiResponseComponentReference)
    )]
    [InlineData(
        "$defs/operationProvenance/properties/responseHeaderComponentReferences/items",
        typeof(OpenApiResponseHeaderComponentReference)
    )]
    [InlineData(
        "$defs/operationProvenance/properties/schemas",
        typeof(OpenApiOperationSchemaProvenance)
    )]
    [InlineData(
        "$defs/operationProvenance/properties/schemas/properties/parameters/items",
        typeof(OpenApiParameterSchemaProvenance)
    )]
    [InlineData(
        "$defs/operationProvenance/properties/schemas/properties/requests/items",
        typeof(OpenApiRequestSchemaProvenance)
    )]
    [InlineData(
        "$defs/operationProvenance/properties/schemas/properties/responses/items",
        typeof(OpenApiResponseSchemaProvenance)
    )]
    public void Reader_Requires_What_The_Schema_Requires(string pointer, Type type)
    {
        var node = pointer
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(
                JsonNode.Parse(File.ReadAllText(_schemaPath))!,
                (node, key) => (node is JsonArray items ? items[int.Parse(key)] : node[key])!
            );
        var schemaProperties = node["properties"]!.AsObject();
        // A [JsonIgnore]d member is never read or written, but the resolver still lists it.
        var readerProperties = _modelOptions
            .GetTypeInfo(type)
            .Properties.Where(property =>
                property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), false)
                    is not [JsonIgnoreAttribute { Condition: JsonIgnoreCondition.Always }]
            )
            .ToList();
        // "kind" is the polymorphic discriminator, not a member.
        var schemaRequired = (node["required"]?.AsArray() ?? [])
            .Select(name => name!.GetValue<string>())
            .Where(name => name != "kind")
            .Order();
        var readerRequired = readerProperties
            .Where(property => property.IsRequired)
            .Select(property => property.Name)
            .Order();

        Assert.Equal(schemaRequired, readerRequired);
        Assert.Equal(
            schemaProperties.Select(property => property.Key).Where(name => name != "kind").Order(),
            readerProperties.Select(property => property.Name).Order()
        );
        Assert.Empty(
            readerProperties
                .Where(property => AdmitsNull(schemaProperties[property.Name]!))
                .Where(property => !property.IsSetNullable)
                .Select(property => property.Name)
        );
    }

    /// <summary>
    /// The verdicts on one response, from the schema (over the whole contract, by Json.Schema,
    /// whose <c>$</c>, unlike ECMA's, matches before a final newline) and from the reader. The rest of the contract is valid for both, so the
    /// response decides each verdict.
    /// </summary>
    private static (bool Schema, bool Reader) Verdicts(JsonObject response)
    {
        var contract = new JsonObject
        {
            ["types"] = new JsonArray(),
            ["enums"] = new JsonArray(),
            ["endpoints"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "get",
                    ["httpMethod"] = "GET",
                    ["routeTemplate"] = "/things",
                    ["controllerName"] = "things",
                    ["params"] = new JsonArray(),
                    ["responses"] = new JsonArray(response),
                }
            ),
        };
        var json = contract.ToJsonString();

        bool reader;
        try
        {
            JsonContractReader.Read(json);
            reader = true;
        }
        catch (JsonException)
        {
            reader = false;
        }

        return (Validate(json).IsValid, reader);
    }

    [Theory]
    [InlineData("404", true)]
    [InlineData("100", true)]
    [InlineData("599", true)]
    [InlineData("099", false)]
    [InlineData("600", false)]
    [InlineData("4XX", true)]
    [InlineData("4xx", true)]
    [InlineData("4Xx", true)]
    [InlineData("6XX", false)]
    [InlineData("default", true)]
    [InlineData("DEFAULT", false)]
    [InlineData("Default", false)]
    // `$` would match before a final newline in .NET's and Json.Schema's dialect, so the
    // schema's pattern ends in a lookahead that means end of string in every dialect.
    [InlineData("404\n", false)]
    [InlineData("default\n", false)]
    [InlineData(" 404", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void Schema_And_Reader_Agree_On_A_StatusKey(string key, bool valid)
    {
        Assert.Equal((valid, valid), Verdicts(new JsonObject { ["statusKey"] = key }));
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(404, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    public void Schema_And_Reader_Agree_On_A_StatusCode(int code, bool valid) =>
        Assert.Equal((valid, valid), Verdicts(new JsonObject { ["statusCode"] = code }));

    [Fact]
    public void Deeply_Nested_Types_Validate()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["Dto"] = new(
                "Dto",
                [],
                [
                    new(
                        "scores",
                        new TsType.Array(new TsType.Nullable(new TsType.TypeRef("Score"))),
                        false
                    ),
                    new(
                        "tags",
                        new TsType.Dictionary(new TsType.Array(new TsType.Primitive("string"))),
                        false
                    ),
                ]
            ),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void TaggedUnion_TypeAlias_Definition_Validates()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["DisplayState"] = new(
                "DisplayState",
                [],
                new TsType.TaggedUnion(
                    "kind",
                    [
                        new TsType.TaggedUnionVariant(
                            "hidden",
                            new TsType.InlineObject([
                                new("kind", new TsType.StringUnion(["hidden"])),
                                new(
                                    "workspaceKey",
                                    new TsType.Nullable(new TsType.TypeRef("WorkspaceKey"))
                                ),
                            ])
                        ),
                        new TsType.TaggedUnionVariant(
                            "shown",
                            new TsType.InlineObject([
                                new("kind", new TsType.StringUnion(["shown"])),
                                new("summary", new TsType.TypeRef("Summary")),
                            ])
                        ),
                    ]
                )
            ),
        };

        var json = ContractEmitter.Emit(definitions, new Dictionary<string, TsType>(), []);
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Property_Missing_Name_Rejected()
    {
        var json =
            """{"types":[{"name":"T","typeParameters":[],"properties":[{"type":{"kind":"primitive","type":"string"},"optional":false}]}],"enums":[],"endpoints":[]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    // One property definition serves a type and an inline object, and rivet-ts leaves
    // "optional" off an inline field: absent, it means required, in the schema and the reader.
    [Fact]
    public void Property_Without_Optional_Is_Required()
    {
        var json =
            """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","type":{"kind":"inlineObject","properties":[{"name":"y","type":{"kind":"primitive","type":"string"}}]}}]}],"enums":[],"endpoints":[]}""";
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));

        var property = Assert.Single(Assert.Single(JsonContractReader.Read(json).Types).Properties);
        Assert.False(property.IsOptional);
        Assert.False(
            Assert.Single(Assert.IsType<TsType.InlineObject>(property.Type).Properties).IsOptional
        );
    }

    // additionalProperties says whether an object type's properties are all its members; an
    // alias has none, so the schema and the reader both refuse it there.
    [Fact]
    public void AdditionalProperties_On_An_Alias_Is_Rejected()
    {
        var json =
            """{"types":[{"name":"T","typeParameters":[],"type":{"kind":"primitive","type":"string"},"additionalProperties":true}],"enums":[],"endpoints":[]}""";

        Assert.False(Validate(json).IsValid);
        var refusal = Assert.Throws<JsonException>(() => JsonContractReader.Read(json));
        Assert.Contains("'additionalProperties'", refusal.Message);
    }

    [Fact]
    public void Property_Missing_Type_Rejected()
    {
        // Missing "type" field
        var json =
            """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","optional":false}]}],"enums":[],"endpoints":[]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Empty_StringUnion_Values_Rejected()
    {
        var json =
            """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","type":{"kind":"stringUnion","values":[]},"optional":false}]}],"enums":[],"endpoints":[]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Heterogeneous_Scalar_Union_Is_Accepted()
    {
        var json =
            """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","type":{"kind":"union","variants":[{"kind":"primitive","type":"number"},{"kind":"literal","value":false}]},"optional":false}]}],"enums":[],"endpoints":[]}""";
        Assert.True(Validate(json).IsValid);
    }

    [Fact]
    public void Empty_Generic_Union_Is_Rejected()
    {
        var json =
            """{"types":[{"name":"T","typeParameters":[],"properties":[{"name":"x","type":{"kind":"union","variants":[]},"optional":false}]}],"enums":[],"endpoints":[]}""";
        Assert.False(Validate(json).IsValid);
    }

    [Fact]
    public void Invalid_ParamSource_Rejected()
    {
        var json =
            """{"types":[],"enums":[],"endpoints":[{"name":"foo","httpMethod":"GET","routeTemplate":"/","params":[{"name":"x","type":{"kind":"primitive","type":"string"},"source":"path"}],"controllerName":"C","responses":[]}]}""";
        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Header_ParamSource_And_Response_Headers_Accepted() // P2 wave 5
    {
        var json =
            """{"types":[],"enums":[],"endpoints":[{"name":"foo","httpMethod":"GET","routeTemplate":"/","params":[{"name":"Notion-Version","type":{"kind":"primitive","type":"string"},"source":"header"}],"controllerName":"C","responses":[{"statusCode":200,"headers":[{"name":"ETag","type":{"kind":"primitive","type":"string"},"description":"Entity tag","required":true}]}]}]}""";
        var result = Validate(json);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Query_AllowEmptyValue_Is_Accepted()
    {
        var json =
            """{"types":[],"enums":[],"endpoints":[{"name":"foo","httpMethod":"GET","routeTemplate":"/","params":[{"name":"option","type":{"kind":"primitive","type":"string"},"source":"query","allowEmptyValue":true}],"controllerName":"C","responses":[]}]}""";
        Assert.True(Validate(json).IsValid);
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

    [Fact]
    public void Endpoint_Example_Missing_MediaType_Rejected()
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
                                        "json": "{\"id\":\"ord_123\"}"
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

    [Fact]
    public void Endpoint_Example_With_Json_And_ResolvedJson_But_No_ComponentExampleId_Rejected()
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
                                        "json": "{\"id\":\"ord_123\"}",
                                        "resolvedJson": "{\"id\":\"ord_123\"}"
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

    [Fact]
    public void Endpoint_Example_With_Json_And_ComponentExampleId_Rejected()
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
                                        "json": "{\"id\":\"ord_123\"}",
                                        "componentExampleId": "order-created"
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

    [Fact]
    public void Request_Example_Missing_MediaType_Rejected()
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
                        "requestExamples": [
                            {
                                "json": "{\"id\":\"ord_123\"}"
                            }
                        ],
                        "responses": []
                    }
                ]
            }
            """;

        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Request_Example_With_ResolvedJson_But_No_ComponentExampleId_Rejected()
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
                        "requestExamples": [
                            {
                                "mediaType": "application/json",
                                "resolvedJson": "{\"id\":\"ord_123\"}"
                            }
                        ],
                        "responses": []
                    }
                ]
            }
            """;

        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Request_Example_Missing_Json_And_ComponentExampleId_Rejected()
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
                        "requestExamples": [
                            {
                                "mediaType": "application/json",
                                "name": "broken"
                            }
                        ],
                        "responses": []
                    }
                ]
            }
            """;

        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Request_Example_With_Json_And_ComponentExampleId_Rejected()
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
                        "requestExamples": [
                            {
                                "mediaType": "application/json",
                                "json": "{\"id\":\"ord_123\"}",
                                "componentExampleId": "order-created"
                            }
                        ],
                        "responses": []
                    }
                ]
            }
            """;

        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Endpoint_With_QueryAuth_Validates()
    {
        var endpoint = new TsEndpointDefinition(
            "streamVideo",
            "GET",
            "/api/media/{id}/stream",
            [new TsEndpointParam("id", new TsType.Primitive("string"), ParamSource.Route)],
            null,
            "MediaController",
            [new TsResponseType(200, null)],
            FileContentType: "video/mp4",
            IsFileEndpoint: true,
            QueryAuth: new QueryAuthMetadata("token")
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Endpoint_With_IsFileEndpoint_Only_Validates()
    {
        var endpoint = new TsEndpointDefinition(
            "downloadFile",
            "GET",
            "/api/files/{id}",
            [],
            null,
            "FilesController",
            [new TsResponseType(200, null)],
            IsFileEndpoint: true
        );

        var json = ContractEmitter.Emit(
            new Dictionary<string, TsTypeDefinition>(),
            new Dictionary<string, TsType>(),
            [endpoint]
        );
        var result = Validate(json);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void QueryAuth_Missing_ParameterName_Rejected()
    {
        var json = """
            {
                "types": [],
                "enums": [],
                "endpoints": [
                    {
                        "name": "stream",
                        "httpMethod": "GET",
                        "routeTemplate": "/stream",
                        "controllerName": "media",
                        "params": [],
                        "responses": [],
                        "queryAuth": {}
                    }
                ]
            }
            """;

        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void QueryAuth_Extra_Property_Rejected()
    {
        var json = """
            {
                "types": [],
                "enums": [],
                "endpoints": [
                    {
                        "name": "stream",
                        "httpMethod": "GET",
                        "routeTemplate": "/stream",
                        "controllerName": "media",
                        "params": [],
                        "responses": [],
                        "queryAuth": { "parameterName": "token", "extra": true }
                    }
                ]
            }
            """;

        var result = Validate(json);
        Assert.False(result.IsValid);
    }

    /// <summary>
    /// The schema has additionalProperties:false throughout, so a field the C# model
    /// serializes but the schema doesn't list makes valid --from JSON invalid. The fixture
    /// sets every field the model serializes (the coverage assertion fails when a new one
    /// is added and left unset), and the schema must accept the result.
    /// </summary>
    [Fact]
    public void Fully_Populated_Model_Validates()
    {
        var scalar = new TsScalarMetadata(
            Description: "d",
            DefaultValue: "\"x\"",
            Example: "\"x\"",
            Examples: "[\"x\"]",
            IsDeprecated: true,
            IsReadOnly: true,
            IsWriteOnly: true,
            Constraints: new TsPropertyConstraints(
                MinLength: 1,
                MaxLength: 2,
                Pattern: "^x$",
                Minimum: 1,
                Maximum: 2,
                ExclusiveMinimum: 0,
                ExclusiveMaximum: 3,
                MultipleOf: 1,
                MinItems: 1,
                MaxItems: 2,
                UniqueItems: true,
                MinProperties: 1,
                MaxProperties: 2
            ),
            IsNullable: true,
            Title: "t",
            Xml: new TsSchemaXmlMetadata("n", "urn:x", "p", IsAttribute: true, IsWrapped: true),
            Format: "f",
            IsFormatSpecified: true,
            Required: ["a"],
            Enum: "[true]",
            EnumListsNull: true
        );
        var component = new TsTypeMetadata("Id", TsTypeProvenance.Component);
        var synthetic = new TsTypeMetadata(null, TsTypeProvenance.Synthetic);
        var everyKind = new TsType.Union([
            new TsType.Primitive("string", "uuid", "Guid"),
            new TsType.Nullable(new TsType.TypeRef("Thing")),
            new TsType.Array(new TsType.TypeParam("T"), scalar),
            new TsType.Dictionary(
                new TsType.Primitive("number"),
                new TsType.Primitive("string"),
                scalar
            ),
            new TsType.StringUnion(["a"], component, "f", "d", scalar, "camelCase"),
            new TsType.IntUnion(["1"], "int32", synthetic, "d", scalar),
            new TsType.Literal(JsonDocument.Parse("\"x\"").RootElement.Clone()),
            new TsType.Generic("Page", [new TsType.Primitive("string")]),
            new TsType.Brand("Email", new TsType.Primitive("string"), component, "d"),
            new TsType.InlineObject(
                [
                    new TsPropertyDefinition(
                        "f",
                        new TsType.Primitive("string"),
                        IsOptional: true,
                        IsDeprecated: true,
                        Format: "f",
                        DefaultValue: "\"x\"",
                        Constraints: new TsPropertyConstraints(MinLength: 1),
                        Description: "d",
                        Example: "\"x\"",
                        IsReadOnly: true,
                        IsWriteOnly: true,
                        ScalarMetadata: scalar
                    ),
                ],
                AdditionalProperties: false
            ),
            new TsType.TaggedUnion(
                "kind",
                [
                    new TsType.TaggedUnionVariant(
                        "a",
                        new TsType.InlineObject([new("kind", new TsType.StringUnion(["a"]))]),
                        component,
                        "d",
                        scalar,
                        "{\"type\":\"string\"}"
                    ),
                ]
            ),
        ]);
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["Page"] = new(
                "Page",
                ["T"],
                [
                    new TsPropertyDefinition(
                        "items",
                        everyKind,
                        IsOptional: true,
                        IsDeprecated: true,
                        Format: "f",
                        DefaultValue: "\"x\"",
                        Constraints: new TsPropertyConstraints(MinItems: 1),
                        Description: "d",
                        Example: "\"x\"",
                        IsReadOnly: true,
                        IsWriteOnly: true,
                        ScalarMetadata: scalar
                    ),
                ],
                "d",
                component,
                scalar,
                AdditionalProperties: true
            ),
            ["Alias"] = new("Alias", [], new TsType.Primitive("string"), "d", component, scalar),
        };
        var json = JsonDocument.Parse("\"x\"").RootElement.Clone();
        var schemaExamples = JsonDocument.Parse("[\"x\"]").RootElement.Clone();
        var exampleMap = JsonDocument.Parse("{\"a\":{\"value\":\"x\"}}").RootElement.Clone();
        var examples = new List<TsEndpointExample>
        {
            new("application/json", "inline", Json: "\"x\"", Summary: "s", Description: "d"),
            new(
                "application/json",
                "shared",
                ComponentExampleId: "ex",
                ResolvedJson: "\"x\"",
                ReferencedComponents: new Dictionary<string, string> { ["Thing"] = "{}" }
            ),
        };
        var content = new TsMediaTypeContent(
            "text/plain",
            new TsType.Primitive("string"),
            IsBinary: true,
            SchemaType: "string",
            Format: "f",
            IsFormatSpecified: true,
            SchemaDescription: "d"
        );
        var server = new OpenApiServerProvenance(
            "https://{host}",
            "d",
            [new OpenApiServerVariableProvenance("host", "a", ["a"], "d")]
        );
        var endpoint = new TsEndpointDefinition(
            "get",
            "POST",
            "/things/{id}",
            [
                new TsEndpointParam(
                    "id",
                    new TsType.Primitive("string"),
                    ParamSource.Cookie,
                    IsOptional: true,
                    BodyPropertyName: "id",
                    Description: "d",
                    IsDeprecated: true,
                    DefaultValue: "\"x\"",
                    Constraints: new TsPropertyConstraints(MinLength: 1),
                    SchemaExamples: schemaExamples,
                    Example: json,
                    Examples: exampleMap,
                    Style: "form",
                    Explode: true,
                    SchemaType: "string",
                    Format: "f",
                    IsFormatSpecified: true,
                    AllowEmptyValue: true,
                    AllowReserved: true
                ),
            ],
            new TsType.TypeRef("Thing"),
            "things",
            [
                new TsResponseType(
                    200,
                    new TsType.TypeRef("Thing"),
                    "d",
                    examples,
                    [
                        new TsResponseHeader(
                            "X-Thing",
                            new TsType.Primitive("string"),
                            "d",
                            Required: true,
                            IsDeprecated: true,
                            SchemaExamples: schemaExamples,
                            Example: json,
                            Examples: exampleMap,
                            Style: "simple",
                            Explode: true,
                            AllowReserved: true,
                            AllowEmptyValue: true,
                            ContentType: "text/plain"
                        ),
                    ],
                    [content]
                ),
                new TsResponseType(0, null, StatusKey: "4XX"),
            ],
            Summary: "s",
            Description: "d",
            Security: new SecurityRequirements([
                new SecurityRequirement([new SecurityRequirementScheme("oauth", ["read"])]),
            ]),
            FileContentType: "application/pdf",
            InputTypeName: "Thing",
            IsFormEncoded: true,
            RequestType: new TsType.TypeRef("Thing"),
            RequestExamples: examples,
            IsFileEndpoint: true,
            QueryAuth: new QueryAuthMetadata("token"),
            BinaryRequestContentType: "application/octet-stream",
            RequestContentTypeOverride: "text/plain",
            ResponseContentTypeOverride: "text/plain",
            RequestContents: [content],
            RequestBodyRequired: true,
            RequestBodyPresent: true,
            Provenance: new OpenApiOperationProvenance(
                true,
                "things.get",
                ["things"],
                true,
                [server],
                "d",
                new OpenApiRivetIdentityProvenance("ThingsContract", "Get"),
                "ThingBody",
                [new OpenApiParameterComponentReference("id", "cookie", "Id")],
                [new OpenApiResponseComponentReference("200", "Ok")],
                new OpenApiOperationSchemaProvenance(
                    [new OpenApiParameterSchemaProvenance("id", "cookie", "{}")],
                    [new OpenApiRequestSchemaProvenance("text/plain", "{}")],
                    [new OpenApiResponseSchemaProvenance("200", "text/plain", "{}")]
                ),
                [new OpenApiResponseHeaderComponentReference("200", "X-Thing", "Header")]
            )
        );

        var declared = new SortedSet<string>(StringComparer.Ordinal);
        var populated = new HashSet<string>(StringComparer.Ordinal);
        CollectSerializedFields(definitions, declared, populated);
        CollectSerializedFields(endpoint, declared, populated);
        var unset = declared.Except(populated).ToList();
        Assert.True(unset.Count == 0, "Fixture leaves unset: " + string.Join(", ", unset));

        var contract = ContractEmitter.Emit(
            definitions,
            new Dictionary<string, TsType>
            {
                ["Colour"] = new TsType.StringUnion(
                    ["red"],
                    component,
                    "f",
                    "d",
                    scalar,
                    "camelCase"
                ),
                ["Level"] = new TsType.IntUnion(["1"], "int32", component, "d", scalar),
            },
            [endpoint]
        );
        var result = Validate(contract);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    /// <summary>
    /// Records every serialized field of each model record reached from
    /// <paramref name="value"/> as "Type.jsonName" in <paramref name="declared"/>, and in
    /// <paramref name="populated"/> once some instance gives it a value that is written.
    /// </summary>
    private static void CollectSerializedFields(
        object? value,
        ISet<string> declared,
        ISet<string> populated
    )
    {
        switch (value)
        {
            case null or string or JsonElement:
                return;
            case System.Collections.IDictionary dictionary:
                foreach (var entry in dictionary.Values)
                {
                    CollectSerializedFields(entry, declared, populated);
                }
                return;
            case System.Collections.IEnumerable items:
                foreach (var item in items)
                {
                    CollectSerializedFields(item, declared, populated);
                }
                return;
        }

        var type = value.GetType();
        if (type.Namespace != typeof(TsType).Namespace || type.IsEnum)
        {
            return;
        }

        foreach (var property in _modelOptions.GetTypeInfo(type).Properties)
        {
            // [JsonIgnore] properties stay in the metadata without an accessor.
            if (property.Get is null)
            {
                continue;
            }

            var field = $"{type.Name}.{property.Name}";
            declared.Add(field);
            var fieldValue = property.Get?.Invoke(value);
            if (IsWritten(property.PropertyType, fieldValue))
            {
                populated.Add(field);
            }
            CollectSerializedFields(fieldValue, declared, populated);
        }
    }

    private static bool IsWritten(Type declaredType, object? value) =>
        value switch
        {
            null => false,
            string => true,
            JsonElement element => element.ValueKind != JsonValueKind.Undefined,
            System.Collections.IEnumerable items => items.Cast<object?>().Any(),
            _ when Nullable.GetUnderlyingType(declaredType) is not null => true,
            _ when declaredType.IsValueType => !value.Equals(
                Activator.CreateInstance(declaredType)
            ),
            _ => true,
        };

    private static bool AdmitsNull(JsonNode property) =>
        property["type"] is JsonArray types
        && types.Any(type => type!.GetValue<string>() == "null");

    private static string FormatErrors(EvaluationResults results) =>
        string.Join(
            "\n",
            results
                .Details.Where(d => !d.IsValid && d.Errors is not null)
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
        );
}
