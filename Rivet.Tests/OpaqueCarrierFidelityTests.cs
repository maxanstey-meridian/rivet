using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet.Tests;

public sealed class OpaqueCarrierFidelityTests
{
    [Fact]
    public void Closed_object_refuses_unknown_members_unless_its_schema_contradicts_itself()
    {
        const string schemas = """
            "ClosedEnvelope": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                    "id": { "type": "string" },
                    "active": { "type": "boolean" }
                },
                "required": ["id", "active"]
            },
            "Pet": {
                "type": "object",
                "additionalProperties": false,
                "discriminator": { "propertyName": "kind" },
                "properties": { "kind": { "type": "string" }, "name": { "type": "string" } },
                "required": ["kind"]
            },
            "Cat": {
                "allOf": [
                    { "$ref": "#/components/schemas/Pet" },
                    { "type": "object", "properties": { "lives": { "type": "integer" } } }
                ]
            },
            "ClosedPart": {
                "type": "object",
                "additionalProperties": false,
                "properties": { "a": { "type": "string" } }
            },
            "Composite": {
                "allOf": [
                    { "$ref": "#/components/schemas/ClosedPart" },
                    { "type": "object", "properties": { "b": { "type": "string" } } }
                ]
            },
            "Item": { "type": "object", "properties": { "id": { "type": "string" } } },
            "Page_Item": {
                "type": "object",
                "additionalProperties": false,
                "properties": { "items": { "type": "array", "items": { "$ref": "#/components/schemas/Item" } } },
                "x-rivet-generic": { "name": "Page", "typeParams": ["T"], "args": { "T": "Item" } }
            },
            "Page_ClosedPart": {
                "type": "object",
                "properties": { "items": { "type": "array", "items": { "$ref": "#/components/schemas/ClosedPart" } } },
                "x-rivet-generic": { "name": "Page", "typeParams": ["T"], "args": { "T": "ClosedPart" } }
            },
            "Shape": {
                "oneOf": [
                    { "$ref": "#/components/schemas/Circle" },
                    { "$ref": "#/components/schemas/Square" }
                ],
                "discriminator": {
                    "propertyName": "kind",
                    "mapping": {
                        "circle": "#/components/schemas/Circle",
                        "square": "#/components/schemas/Square"
                    }
                }
            },
            "Circle": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                    "kind": { "type": "string", "enum": ["circle"] },
                    "radius": { "type": "number" }
                },
                "required": ["kind"]
            },
            "Square": {
                "type": "object",
                "properties": {
                    "kind": { "type": "string", "enum": ["square"] },
                    "side": { "type": "number" }
                },
                "required": ["kind"]
            }
            """;
        var fixture = GeneratedCarrierFixture.ImportCompileLoadAndEmit(schemas, "ClosedEnvelope");

        // A closed record nothing contradicts refuses an unknown member, as its schema does.
        AssertRuntimeRoundTrip(fixture.RequestType, """{"id":"known","active":true}""");
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                """{"id":"known","active":true,"invalid":"refused"}""",
                fixture.RequestType,
                JsonSerializerOptions.Web
            )
        );
        var emitted = fixture.EmittedRoot.GetProperty("components").GetProperty("schemas");
        Assert.False(
            emitted.GetProperty("ClosedEnvelope").GetProperty("additionalProperties").GetBoolean()
        );

        // A closed schema that contradicts itself stays open, and says why. Each spec still
        // emits its closure.
        Assert.Equal(
            [
                "RIV3033: Closed schema imported open: 'Circle' says additionalProperties: false, but it is a polymorphic variant of 'Shape', and System.Text.Json would refuse its 'kind' discriminator as an unknown member wherever the variant is read on its own. Its record accepts and drops an unknown member instead of refusing it.",
                "RIV3033: Closed schema imported open: 'ClosedPart' says additionalProperties: false, but 'Composite' composes it in an allOf, adding members its closure refuses. Its record accepts and drops an unknown member instead of refusing it.",
                "RIV3033: Closed schema imported open: 'Page_Item' says additionalProperties: false, but it is an instance of the generic template 'Page', which serves 'Page_ClosedPart' too, and that doesn't say so. Its record accepts and drops an unknown member instead of refusing it.",
                "RIV3033: Closed schema imported open: 'Pet' says additionalProperties: false, but 'Cat' extends it, adding members its closure refuses. Its record accepts and drops an unknown member instead of refusing it.",
            ],
            CompilationHelper
                .Import(CompilationHelper.BuildSpec(schemas: schemas))
                .Warnings.Order(StringComparer.Ordinal)
        );
        var generated = fixture.RequestType.Assembly;
        Type Generated(string name) =>
            Assert.IsAssignableFrom<Type>(generated.GetType($"Generated.{name}"));
        Assert.Equal(
            "x",
            RuntimeRoundTrip(Generated("ClosedPart"), """{"a":"x","invalid":"dropped"}""")
                .GetProperty("a")
                .GetString()
        );
        Assert.False(
            RuntimeRoundTrip(Generated("Pet"), """{"kind":"Cat","lives":9,"invalid":1}""")
                .TryGetProperty("invalid", out _)
        );
        foreach (var closed in new[] { "Pet", "ClosedPart", "Page_Item", "Circle" })
        {
            Assert.False(
                emitted.GetProperty(closed).GetProperty("additionalProperties").GetBoolean()
            );
        }

        // A variant stays open: closed, System.Text.Json refuses an unknown member when it
        // reads the variant through its base, but also refuses the variant's own tag, which
        // the importer strips from the record, whenever the variant is read on its own.
        Assert.False(
            RuntimeRoundTrip(Generated("Shape"), """{"kind":"circle","radius":1,"invalid":1}""")
                .TryGetProperty("invalid", out _)
        );
        Assert.Equal(
            1,
            RuntimeRoundTrip(Generated("Circle"), """{"kind":"circle","radius":1}""")
                .GetProperty("radius")
                .GetInt32()
        );
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DisallowShape>(
                """{"kind":"circle","radius":1,"invalid":1}""",
                JsonSerializerOptions.Web
            )
        );
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DisallowCircle>(
                """{"kind":"circle","radius":1}""",
                JsonSerializerOptions.Web
            )
        );
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(DisallowCircle), "circle")]
    private abstract record DisallowShape;

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record DisallowCircle(double Radius) : DisallowShape;

    [Fact]
    public void Open_record_preserves_typed_named_and_schema_valued_additional_properties()
    {
        var fixture = GeneratedCarrierFixture.ImportCompileLoadAndEmit(
            """
            "MixedEnvelope": {
                "type": "object",
                "properties": { "alpha": { "type": "string" } },
                "additionalProperties": { "type": "string" },
                "required": ["alpha"]
            }
            """,
            "MixedEnvelope"
        );
        const string payload = """{"alpha":"known","dynamic":"retained"}""";

        AssertRuntimeRoundTrip(fixture.RequestType, payload);
        Assert.NotNull(fixture.RequestType.GetProperty("Alpha"));

        var schema = fixture
            .EmittedRoot.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("MixedEnvelope");
        Assert.Equal(
            "string",
            schema.GetProperty("properties").GetProperty("alpha").GetProperty("type").GetString()
        );
        Assert.Equal(
            "string",
            schema.GetProperty("additionalProperties").GetProperty("type").GetString()
        );
    }

    [Fact]
    public void Inline_free_form_carriers_preserve_valid_runtime_values_and_emitted_openness()
    {
        var fixture = GeneratedCarrierFixture.ImportCompileLoadAndEmit(
            """
            "FreeFormEnvelope": {
                "type": "object",
                "properties": {
                    "data": { "type": "object" },
                    "payload": {}
                },
                "required": ["data", "payload"]
            }
            """,
            "FreeFormEnvelope"
        );
        const string payload =
            """{"data":{"alpha":{"nested":true},"count":3},"payload":["x",{"y":2}]}""";

        AssertRuntimeRoundTrip(fixture.RequestType, payload);

        var properties = fixture
            .EmittedRoot.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("FreeFormEnvelope")
            .GetProperty("properties");
        var data = properties.GetProperty("data");
        Assert.Equal("object", data.GetProperty("type").GetString());
        var additionalProperties = data.GetProperty("additionalProperties");
        Assert.Equal(JsonValueKind.Object, additionalProperties.ValueKind);
        Assert.Empty(additionalProperties.EnumerateObject());
        Assert.Empty(properties.GetProperty("payload").EnumerateObject());
    }

    [Fact]
    public void Propertyless_named_objects_keep_open_and_closed_runtime_carriers()
    {
        var open = GeneratedCarrierFixture.ImportCompileLoadAndEmit(
            """
            "OpenEmpty": { "type": "object" }
            """,
            "OpenEmpty"
        );
        var closed = GeneratedCarrierFixture.ImportCompileLoadAndEmit(
            """
            "ClosedEmpty": { "type": "object", "additionalProperties": false }
            """,
            "ClosedEmpty"
        );

        Assert.True(
            RuntimeRoundTrip(open.RequestType, """{"extra":{"nested":true}}""")
                .GetProperty("extra")
                .GetProperty("nested")
                .GetBoolean()
        );
        Assert.Empty(RuntimeRoundTrip(closed.RequestType, "{}").EnumerateObject());
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                """{"extra":{"nested":true}}""",
                closed.RequestType,
                JsonSerializerOptions.Web
            )
        );
        Assert.False(
            closed
                .EmittedRoot.GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ClosedEmpty")
                .GetProperty("additionalProperties")
                .GetBoolean()
        );
    }

    [Fact]
    public void Inline_propertyless_closed_object_preserves_empty_value_and_emitted_closure()
    {
        var fixture = GeneratedCarrierFixture.ImportCompileLoadAndEmit(
            """
            "Envelope": {
                "type": "object",
                "properties": {
                    "closed": { "type": "object", "additionalProperties": false }
                },
                "required": ["closed"]
            }
            """,
            "Envelope"
        );

        AssertRuntimeRoundTrip(fixture.RequestType, """{"closed":{}}""");
        Assert.False(
            fixture
                .EmittedRoot.GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("Envelope")
                .GetProperty("properties")
                .GetProperty("closed")
                .GetProperty("additionalProperties")
                .GetBoolean()
        );
    }

    [Fact]
    public void SendGrid_style_pattern_dictionary_preserves_a_valid_entry_and_opaque_schema()
    {
        var fixture = GeneratedCarrierFixture.ImportCompileLoadAndEmit(
            """
            "PatternEnvelope": {
                "type": "object",
                "properties": {
                    "result": {
                        "additionalProperties": false,
                        "type": "object",
                        "x-patternProperties": {
                            "^[A-Za-z0-9\\._%\\+-]+@[A-Za-z0-9\\.-]+\\.[A-Za-z]{2,6}$": {
                                "type": "object",
                                "properties": {
                                    "contact": {
                                        "type": "object",
                                        "properties": { "id": { "type": "string" } },
                                        "required": ["id"]
                                    },
                                    "error": { "type": "string" }
                                }
                            }
                        }
                    }
                },
                "required": ["result"]
            }
            """,
            "PatternEnvelope"
        );
        const string payload =
            """{"result":{"person@example.com":{"contact":{"id":"42"},"error":"none"}}}""";

        AssertRuntimeRoundTrip(fixture.RequestType, payload);

        var resultSchema = fixture
            .EmittedRoot.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("PatternEnvelope")
            .GetProperty("properties")
            .GetProperty("result");
        Assert.False(resultSchema.GetProperty("additionalProperties").GetBoolean());
        var patterns = resultSchema.GetProperty("x-patternProperties");
        const string emailPattern = @"^[A-Za-z0-9\._%\+-]+@[A-Za-z0-9\.-]+\.[A-Za-z]{2,6}$";
        var patternSchema = patterns.GetProperty(emailPattern);
        Assert.Equal("object", patternSchema.GetProperty("type").GetString());
        Assert.Equal(
            "string",
            patternSchema
                .GetProperty("properties")
                .GetProperty("error")
                .GetProperty("type")
                .GetString()
        );
    }

    [Fact]
    public void MaxProperties_dictionary_preserves_a_valid_value_and_emits_the_constraint()
    {
        var fixture = GeneratedCarrierFixture.ImportCompileLoadAndEmit(
            """
            "LabelsEnvelope": {
                "type": "object",
                "properties": {
                    "labels": {
                        "type": "object",
                        "minProperties": 1,
                        "maxProperties": 5,
                        "additionalProperties": {
                            "type": "string",
                            "maxLength": 20
                        }
                    }
                },
                "required": ["labels"]
            }
            """,
            "LabelsEnvelope"
        );
        const string payload = """{"labels":{"first":"one","second":"two"}}""";

        AssertRuntimeRoundTrip(fixture.RequestType, payload);

        var labels = fixture
            .EmittedRoot.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("LabelsEnvelope")
            .GetProperty("properties")
            .GetProperty("labels");
        Assert.Equal(1, labels.GetProperty("minProperties").GetInt32());
        Assert.Equal(5, labels.GetProperty("maxProperties").GetInt32());
        Assert.Equal(
            20,
            labels.GetProperty("additionalProperties").GetProperty("maxLength").GetInt32()
        );
    }

    private static void AssertRuntimeRoundTrip(Type requestType, string payload)
    {
        using var expected = JsonDocument.Parse(payload);
        Assert.True(
            JsonElement.DeepEquals(expected.RootElement, RuntimeRoundTrip(requestType, payload))
        );
    }

    private static JsonElement RuntimeRoundTrip(Type requestType, string payload)
    {
        var request = JsonSerializer.Deserialize(payload, requestType, JsonSerializerOptions.Web);
        Assert.NotNull(request);
        using var actual = JsonDocument.Parse(
            JsonSerializer.Serialize(request, requestType, JsonSerializerOptions.Web)
        );
        return actual.RootElement.Clone();
    }
}
