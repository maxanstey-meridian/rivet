using System.Text.Json;

namespace Rivet.Tests;

public sealed class OpaqueCarrierFidelityTests
{
    [Fact]
    public void Closed_object_preserves_every_valid_member_at_runtime()
    {
        var fixture = GeneratedCarrierFixture.ImportCompileLoadAndEmit(
            """
            "ClosedEnvelope": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                    "id": { "type": "string" },
                    "active": { "type": "boolean" }
                },
                "required": ["id", "active"]
            }
            """,
            "ClosedEnvelope"
        );
        const string payload = """{"id":"known","active":true,"invalid":"dropped"}""";

        var serialized = RuntimeRoundTrip(fixture.RequestType, payload);
        Assert.Equal("known", serialized.GetProperty("id").GetString());
        Assert.True(serialized.GetProperty("active").GetBoolean());
        Assert.False(serialized.TryGetProperty("invalid", out _));
        Assert.False(
            fixture
                .EmittedRoot.GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ClosedEnvelope")
                .GetProperty("additionalProperties")
                .GetBoolean()
        );
    }

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
