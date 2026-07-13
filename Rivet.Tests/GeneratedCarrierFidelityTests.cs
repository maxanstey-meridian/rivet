using System.Text.Json;

namespace Rivet.Tests;

public sealed class GeneratedCarrierFidelityTests
{
    [Fact]
    public void Explicit_open_object_preserves_additional_members_at_runtime()
    {
        var requestType = GeneratedCarrierFixture.ImportCompileAndLoad(
            """
            "OpenRequest": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "additionalProperties": true,
                "required": ["id"]
            }
            """,
            "OpenRequest"
        );

        AssertAdditionalMemberSurvives(requestType);
    }

    [Fact]
    public void Implicit_open_object_preserves_additional_members_at_runtime()
    {
        var requestType = GeneratedCarrierFixture.ImportCompileAndLoad(
            """
            "ImplicitOpenRequest": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "required": ["id"]
            }
            """,
            "ImplicitOpenRequest"
        );

        AssertAdditionalMemberSurvives(requestType);
    }

    [Fact]
    public void Inline_open_and_closed_objects_with_the_same_properties_keep_distinct_carriers()
    {
        var envelopeType = GeneratedCarrierFixture.ImportCompileAndLoad(
            """
            "Envelope": {
                "type": "object",
                "properties": {
                    "open": {
                        "type": "object",
                        "properties": { "id": { "type": "string" } }
                    },
                    "closed": {
                        "type": "object",
                        "properties": { "id": { "type": "string" } },
                        "additionalProperties": false
                    }
                },
                "required": ["open", "closed"]
            }
            """,
            "Envelope"
        );
        const string payload =
            """{"open":{"id":"one","extra":true},"closed":{"id":"two","extra":true}}""";

        var envelope = JsonSerializer.Deserialize(payload, envelopeType, JsonSerializerOptions.Web);
        Assert.NotNull(envelope);
        using var serialized = JsonDocument.Parse(
            JsonSerializer.Serialize(envelope, envelopeType, JsonSerializerOptions.Web)
        );

        Assert.True(serialized.RootElement.GetProperty("open").GetProperty("extra").GetBoolean());
        Assert.False(serialized.RootElement.GetProperty("closed").TryGetProperty("extra", out _));
    }

    [Fact]
    public void Box_nullable_enum_union_preserves_valid_values_at_runtime()
    {
        var requestType = ImportCompileAndLoadBoxUpdateRequestType();

        foreach (
            var payload in new[]
            {
                """{"disposition_action":null}""",
                """{"disposition_action":"permanently_delete"}""",
                """{"disposition_action":"remove_retention"}""",
            }
        )
        {
            var request = JsonSerializer.Deserialize(
                payload,
                requestType,
                JsonSerializerOptions.Web
            );
            Assert.NotNull(request);
            using var expected = JsonDocument.Parse(payload);
            using var serialized = JsonDocument.Parse(
                JsonSerializer.Serialize(request, requestType, JsonSerializerOptions.Web)
            );
            Assert.True(JsonElement.DeepEquals(expected.RootElement, serialized.RootElement));
        }
    }

    [Fact]
    public void Box_nullable_enum_union_rejects_arbitrary_strings_at_runtime()
    {
        var requestType = ImportCompileAndLoadBoxUpdateRequestType();

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                """{"disposition_action":"not-a-disposition"}""",
                requestType,
                JsonSerializerOptions.Web
            )
        );
    }

    [Fact]
    public void Nullable_all_of_record_and_scalar_round_trip_at_runtime()
    {
        var envelopeType = GeneratedCarrierFixture.ImportCompileAndLoad(
            """
            "User": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "required": ["id"]
            },
            "NullableEnvelope": {
                "type": "object",
                "properties": {
                    "owner": {
                        "allOf": [
                            { "$ref": "#/components/schemas/User" },
                            { "type": "object", "nullable": true }
                        ]
                    },
                    "sequence": {
                        "allOf": [
                            { "type": "string", "nullable": true },
                            { "nullable": false }
                        ]
                    },
                    "constrained": {
                        "allOf": [
                            { "type": "string" },
                            { "maxLength": 10 }
                        ]
                    },
                    "status": {
                        "allOf": [
                            { "type": "string" },
                            { "enum": ["ready", "done"] }
                        ]
                    },
                    "kind": {
                        "allOf": [
                            { "type": "string" },
                            { "const": "fixed" }
                        ]
                    }
                },
                "required": ["owner", "sequence", "constrained", "status", "kind"]
            }
            """,
            "NullableEnvelope"
        );
        foreach (
            var payload in new[]
            {
                """{"owner":null,"sequence":null,"constrained":"short","status":"ready","kind":"fixed"}""",
                """{"owner":{"id":"known"},"sequence":"3","constrained":"value","status":"done","kind":"fixed"}""",
            }
        )
        {
            var envelope = JsonSerializer.Deserialize(
                payload,
                envelopeType,
                JsonSerializerOptions.Web
            );
            Assert.NotNull(envelope);
            using var expected = JsonDocument.Parse(payload);
            using var serialized = JsonDocument.Parse(
                JsonSerializer.Serialize(envelope, envelopeType, JsonSerializerOptions.Web)
            );
            Assert.True(JsonElement.DeepEquals(expected.RootElement, serialized.RootElement));
        }
    }

    [Fact]
    public void Outer_closed_all_of_removes_unknown_members_at_runtime()
    {
        var requestType = GeneratedCarrierFixture.ImportCompileAndLoad(
            """
            "OpenBase": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "required": ["id"]
            },
            "ClosedRequest": {
                "allOf": [{ "$ref": "#/components/schemas/OpenBase" }],
                "additionalProperties": false
            }
            """,
            "ClosedRequest"
        );

        var request = JsonSerializer.Deserialize(
            """{"id":"known","extra":true}""",
            requestType,
            JsonSerializerOptions.Web
        );
        Assert.NotNull(request);
        using var serialized = JsonDocument.Parse(
            JsonSerializer.Serialize(request, requestType, JsonSerializerOptions.Web)
        );
        Assert.Equal("known", serialized.RootElement.GetProperty("id").GetString());
        Assert.False(serialized.RootElement.TryGetProperty("extra", out _));
    }

    [Fact]
    public void Inline_closed_all_of_removes_unknown_members_at_runtime()
    {
        var requestType = GeneratedCarrierFixture.ImportCompileAndLoad(
            """
            "OpenBase": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "required": ["id"]
            },
            "Envelope": {
                "type": "object",
                "properties": {
                    "closed": {
                        "allOf": [{ "$ref": "#/components/schemas/OpenBase" }],
                        "additionalProperties": false
                    }
                },
                "required": ["closed"]
            }
            """,
            "Envelope"
        );

        var request = JsonSerializer.Deserialize(
            """{"closed":{"id":"known","extra":true}}""",
            requestType,
            JsonSerializerOptions.Web
        );
        Assert.NotNull(request);
        using var serialized = JsonDocument.Parse(
            JsonSerializer.Serialize(request, requestType, JsonSerializerOptions.Web)
        );
        var closed = serialized.RootElement.GetProperty("closed");
        Assert.Equal("known", closed.GetProperty("id").GetString());
        Assert.False(closed.TryGetProperty("extra", out _));
    }

    [Fact]
    public void Spotify_nested_track_episode_discriminator_dispatches_episode_at_runtime()
    {
        var playlistTrackType = ImportCompileAndLoadSpotifyPlaylistTrackType();
        const string payload =
            """{"track":{"type":"episode","description":"Episode","show":"Show"}}""";

        var playlistTrack = JsonSerializer.Deserialize(
            payload,
            playlistTrackType,
            JsonSerializerOptions.Web
        );
        Assert.NotNull(playlistTrack);
        var union = playlistTrackType.GetProperty("Track")!.GetValue(playlistTrack);
        Assert.NotNull(union);
        Assert.Null(union.GetType().GetProperty("AsTrackObject")!.GetValue(union));
        Assert.NotNull(union.GetType().GetProperty("AsEpisodeObject")!.GetValue(union));
        using var serialized = JsonDocument.Parse(
            JsonSerializer.Serialize(playlistTrack, playlistTrackType, JsonSerializerOptions.Web)
        );
        var track = serialized.RootElement.GetProperty("track");
        Assert.Equal("episode", track.GetProperty("type").GetString());
        Assert.Equal("Show", track.GetProperty("show").GetString());
    }

    private static Type ImportCompileAndLoadSpotifyPlaylistTrackType() =>
        GeneratedCarrierFixture.ImportCompileAndLoad(
            """
            "TrackObject": {
                "type": "object",
                "properties": {
                    "type": { "type": "string", "enum": ["track"] },
                    "track_number": { "type": "integer" }
                }
            },
            "EpisodeBase": {
                "type": "object",
                "properties": {
                    "type": { "type": "string", "enum": ["episode"] },
                    "description": { "type": "string" }
                },
                "required": ["type", "description"]
            },
            "EpisodeObject": {
                "type": "object",
                "allOf": [
                    { "$ref": "#/components/schemas/EpisodeBase" },
                    {
                        "type": "object",
                        "properties": { "show": { "type": "string" } },
                        "required": ["show"]
                    }
                ]
            },
            "PlaylistTrackObject": {
                "type": "object",
                "properties": {
                    "track": {
                        "discriminator": { "propertyName": "type" },
                        "oneOf": [
                            { "$ref": "#/components/schemas/TrackObject" },
                            { "$ref": "#/components/schemas/EpisodeObject" }
                        ]
                    }
                }
            }
            """,
            "PlaylistTrackObject"
        );

    private static Type ImportCompileAndLoadBoxUpdateRequestType() =>
        GeneratedCarrierFixture.ImportCompileAndLoad(
            """
            "BoxUpdateRequest": {
                "type": "object",
                "properties": {
                    "disposition_action": {
                        "anyOf": [
                            {
                                "type": "string",
                                "enum": ["permanently_delete", "remove_retention"]
                            },
                            {
                                "type": "string",
                                "pattern": ".^",
                                "nullable": true
                            }
                        ]
                    }
                }
            }
            """,
            "BoxUpdateRequest"
        );

    private static void AssertAdditionalMemberSurvives(Type requestType)
    {
        const string payload = """{"id":"known","extra":{"nested":true}}""";
        var request = JsonSerializer.Deserialize(payload, requestType, JsonSerializerOptions.Web);
        Assert.NotNull(request);
        using var serialized = JsonDocument.Parse(
            JsonSerializer.Serialize(request, requestType, JsonSerializerOptions.Web)
        );

        Assert.Equal("known", serialized.RootElement.GetProperty("id").GetString());
        Assert.True(serialized.RootElement.GetProperty("extra").GetProperty("nested").GetBoolean());
    }
}
