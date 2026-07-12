using System.Text.Json;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Tests that format-level type information (uuid, date-time, integer ranges, etc.)
/// survives round-trips in both directions:
///   1. C# → OpenAPI (forward: format flows into the component schema)
///   2. OpenAPI → Import → Compile → Walk → OpenAPI (import round-trip: format preserved)
/// </summary>
public sealed class FormatRoundTripTests
{
    // ─── Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Wraps a [RivetType] record source in a synthetic contract, emits the OpenAPI
    /// spec, and returns the named component schema.
    /// </summary>
    private static JsonElement EmitSchemaFor(string recordSource, string typeName)
    {
        var source =
            recordSource
            + $$"""

                [Rivet.RivetContract]
                public static class FormatRoundTripContract
                {
                    public static readonly Rivet.Define Get =
                        Rivet.Define.Get<Test.{{typeName}}>("/api/format-roundtrip");
                }
                """;
        var (endpoints, walker) = CompilationHelper.WalkContract(source);
        var json = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );
        return JsonDocument
            .Parse(json)
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(typeName);
    }

    private static (
        IReadOnlyList<TsEndpointDefinition> Endpoints,
        TypeWalker Walker,
        string EmittedJson
    ) ForwardAndEmit(string csharpSource)
    {
        var compilation = CompilationHelper.CreateCompilation(csharpSource);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var openApiJson = OpenApiEmitter.Emit(
            endpoints,
            walker.Definitions,
            walker.Brands,
            walker.Enums,
            null
        );
        return (endpoints, walker, openApiJson);
    }

    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, TypeWalker Walker) ImportAndWalk(
        string openApiJson
    )
    {
        var importResult = OpenApiImporter.Import(openApiJson, new ImportOptions("RoundTrip"));
        var compilation = CompilationHelper.CreateCompilationFromMultiple(
            importResult.Files.Select(f => f.Content).ToArray()
        );
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        return (endpoints, walker);
    }

    // ─── Forward: C# → OpenAPI schema format ───────────────────

    [Fact]
    public void Guid_Property_Has_Uuid_Format_In_Schema()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record IdDto(Guid Id);
            """;

        var prop = EmitSchemaFor(source, "IdDto").GetProperty("properties").GetProperty("id");
        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("uuid", prop.GetProperty("format").GetString());
    }

    [Fact]
    public void DateTime_Property_Has_DateTime_Format_In_Schema()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TimedDto(DateTime CreatedAt);
            """;

        var prop = EmitSchemaFor(source, "TimedDto")
            .GetProperty("properties")
            .GetProperty("createdAt");
        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("date-time", prop.GetProperty("format").GetString());
    }

    [Fact]
    public void DateOnly_Property_Has_Date_Format_In_Schema()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record DayDto(DateOnly Day);
            """;

        var prop = EmitSchemaFor(source, "DayDto").GetProperty("properties").GetProperty("day");
        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("date", prop.GetProperty("format").GetString());
    }

    [Fact]
    public void TimeOnly_Property_Has_Time_Format_In_Schema()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record AlarmDto(TimeOnly RingAt);
            """;

        var prop = EmitSchemaFor(source, "AlarmDto")
            .GetProperty("properties")
            .GetProperty("ringAt");
        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("time", prop.GetProperty("format").GetString());
    }

    [Fact]
    public void Uri_Property_Has_Uri_Format_In_Schema()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LinkDto(Uri Href);
            """;

        var prop = EmitSchemaFor(source, "LinkDto").GetProperty("properties").GetProperty("href");
        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("uri", prop.GetProperty("format").GetString());
    }

    [Fact]
    public void Int_Property_Has_Integer_Type_And_No_Implicit_Range_In_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CountDto(int Count);
            """;

        var prop = EmitSchemaFor(source, "CountDto").GetProperty("properties").GetProperty("count");
        Assert.Equal("integer", prop.GetProperty("type").GetString());
        Assert.Equal("int32", prop.GetProperty("format").GetString());
        Assert.False(prop.TryGetProperty("minimum", out _));
        Assert.False(prop.TryGetProperty("maximum", out _));
    }

    [Fact]
    public void Uint_Property_Has_Unsigned_Format_And_No_Implicit_Range_In_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FlagDto(uint Flags);
            """;

        var prop = EmitSchemaFor(source, "FlagDto").GetProperty("properties").GetProperty("flags");
        Assert.Equal("integer", prop.GetProperty("type").GetString());
        Assert.Equal("uint32", prop.GetProperty("format").GetString());
        Assert.False(prop.TryGetProperty("minimum", out _));
        Assert.False(prop.TryGetProperty("maximum", out _));
    }

    [Fact]
    public void Byte_Property_Has_Uint8_Format_And_No_Implicit_Range_In_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PixelDto(byte R, byte G, byte B);
            """;

        var prop = EmitSchemaFor(source, "PixelDto").GetProperty("properties").GetProperty("r");
        Assert.Equal("integer", prop.GetProperty("type").GetString());
        Assert.Equal("uint8", prop.GetProperty("format").GetString());
        Assert.False(prop.TryGetProperty("minimum", out _));
        Assert.False(prop.TryGetProperty("maximum", out _));
    }

    [Fact]
    public void ByteArray_Property_Emits_Base64_String_Schema()
    {
        // FABLE_GAPS spec/wire divergence: System.Text.Json serializes byte[] as a
        // base64 STRING on the wire, never as an integer array. The schema must say
        // type: string + contentEncoding: base64 (OpenAPI 3.1 idiom) — exact shape.
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record BlobDto(byte[] Payload, byte[]? Thumbnail);
            """;

        var props = EmitSchemaFor(source, "BlobDto").GetProperty("properties");

        var payload = props.GetProperty("payload");
        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.Equal("string", payload.GetProperty("type").GetString());
        Assert.Equal("base64", payload.GetProperty("contentEncoding").GetString());
        Assert.Equal("byte[]", payload.GetProperty("x-rivet-csharp-type").GetString());
        Assert.False(payload.TryGetProperty("format", out _));
        Assert.False(payload.TryGetProperty("items", out _));

        // Nullable byte[] — 3.1 type array, contentEncoding intact
        var thumbnail = props.GetProperty("thumbnail");
        Assert.Equal(3, thumbnail.EnumerateObject().Count());
        var typeArray = thumbnail
            .GetProperty("type")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(new[] { "string", "null" }, typeArray);
        Assert.Equal("base64", thumbnail.GetProperty("contentEncoding").GetString());
        Assert.Equal("byte[]", thumbnail.GetProperty("x-rivet-csharp-type").GetString());
    }

    [Fact]
    public void ByteArray_From_Contract_Json_Emits_Base64_String_Schema()
    {
        // The TS-lowered contract JSON path (JsonContractReader) must produce the same
        // schema as the Roslyn path — the primitive node round-trips through the
        // kind:"primitive" converter.
        var contractJson = """
            {
                "types": [
                    {
                        "name": "BlobDto",
                        "typeParameters": [],
                        "properties": [
                            {
                                "name": "payload",
                                "type": { "kind": "primitive", "type": "string", "format": "base64", "csharpType": "byte[]" },
                                "optional": false
                            }
                        ]
                    }
                ],
                "enums": [],
                "endpoints": [
                    {
                        "name": "getBlob",
                        "httpMethod": "GET",
                        "routeTemplate": "/api/blobs/{id}",
                        "controllerName": "blobs",
                        "params": [
                            { "name": "id", "type": { "kind": "primitive", "type": "string" }, "source": "route" }
                        ],
                        "returnType": { "kind": "ref", "name": "BlobDto" },
                        "responses": [
                            { "statusCode": 200, "dataType": { "kind": "ref", "name": "BlobDto" } }
                        ]
                    }
                ]
            }
            """;

        var openApi = CompilationHelper.EmitOpenApiFromJson(contractJson);
        var payload = JsonDocument
            .Parse(openApi)
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("BlobDto")
            .GetProperty("properties")
            .GetProperty("payload");

        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.Equal("string", payload.GetProperty("type").GetString());
        Assert.Equal("base64", payload.GetProperty("contentEncoding").GetString());
        Assert.Equal("byte[]", payload.GetProperty("x-rivet-csharp-type").GetString());
    }

    [Fact]
    public void ByteArray_Survives_OpenApi_RoundTrip()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record BlobDto(byte[] Payload);

            [RivetContract]
            public static class BlobContract
            {
                public static readonly RouteDefinition<BlobDto> Get =
                    Define.Get<BlobDto>("/api/blobs/{id}");
            }
            """;

        var (_, _, openApiJson) = ForwardAndEmit(source);
        var (_, walker) = ImportAndWalk(openApiJson);

        var def = walker.Definitions.Values.First(d => d.Name == "BlobDto");
        var payload = def.Properties.First(p => p.Name == "payload");
        var prim = Assert.IsType<TsType.Primitive>(payload.Type);
        Assert.Equal("string", prim.Name);
        Assert.Equal("base64", prim.Format);
        Assert.Equal("byte[]", prim.CSharpType);
    }

    [Fact]
    public void Char_Property_Emits_Length1_String_Schema()
    {
        // P2 wave 6: System.Text.Json serializes char as a single-character JSON
        // string on the wire. The schema must say type: string with both length
        // bounds pinned to 1, plus x-rivet-csharp-type — exact shape.
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record GradeDto(char Letter, char? Modifier);
            """;

        var props = EmitSchemaFor(source, "GradeDto").GetProperty("properties");

        var letter = props.GetProperty("letter");
        Assert.Equal(4, letter.EnumerateObject().Count());
        Assert.Equal("string", letter.GetProperty("type").GetString());
        Assert.Equal(1, letter.GetProperty("minLength").GetInt32());
        Assert.Equal(1, letter.GetProperty("maxLength").GetInt32());
        Assert.Equal("char", letter.GetProperty("x-rivet-csharp-type").GetString());
        Assert.False(letter.TryGetProperty("format", out _));

        // Nullable char — 3.1 type array, length bounds intact
        var modifier = props.GetProperty("modifier");
        Assert.Equal(4, modifier.EnumerateObject().Count());
        var typeArray = modifier
            .GetProperty("type")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(new[] { "string", "null" }, typeArray);
        Assert.Equal(1, modifier.GetProperty("minLength").GetInt32());
        Assert.Equal(1, modifier.GetProperty("maxLength").GetInt32());
        Assert.Equal("char", modifier.GetProperty("x-rivet-csharp-type").GetString());
    }

    [Fact]
    public void Char_Survives_OpenApi_RoundTrip()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record GradeDto(char Letter, char? Modifier);

            [RivetContract]
            public static class GradesContract
            {
                public static readonly RouteDefinition<GradeDto> Get =
                    Define.Get<GradeDto>("/api/grades/{id}");
            }
            """;

        var (_, _, openApiJson) = ForwardAndEmit(source);
        var (_, walker) = ImportAndWalk(openApiJson);

        var def = walker.Definitions.Values.First(d => d.Name == "GradeDto");
        var letter = def.Properties.First(p => p.Name == "letter");
        Assert.Equal(new TsType.Primitive("string", null, "char"), letter.Type);

        var modifier = def.Properties.First(p => p.Name == "modifier");
        var nullable = Assert.IsType<TsType.Nullable>(modifier.Type);
        Assert.Equal(new TsType.Primitive("string", null, "char"), nullable.Inner);
    }

    [Fact]
    public void Short_Property_Has_Int16_Format_And_No_Implicit_Range_In_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LevelDto(short Level);
            """;

        var prop = EmitSchemaFor(source, "LevelDto").GetProperty("properties").GetProperty("level");
        Assert.Equal("integer", prop.GetProperty("type").GetString());
        Assert.Equal("int16", prop.GetProperty("format").GetString());
        Assert.False(prop.TryGetProperty("minimum", out _));
        Assert.False(prop.TryGetProperty("maximum", out _));
    }

    [Fact]
    public void Long_Property_Has_Int64_No_Range_In_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record BigDto(long BigNumber);
            """;

        var prop = EmitSchemaFor(source, "BigDto")
            .GetProperty("properties")
            .GetProperty("bigNumber");
        Assert.Equal("integer", prop.GetProperty("type").GetString());
        Assert.Equal("int64", prop.GetProperty("format").GetString());
        // int64 exceeds JS safe integer — no minimum/maximum
        Assert.False(prop.TryGetProperty("minimum", out _));
        Assert.False(prop.TryGetProperty("maximum", out _));
    }

    // ─── OpenAPI round-trip: format survives import → re-emit ──

    [Fact]
    public void Uuid_Survives_OpenApi_RoundTrip()
    {
        var openApi = """
            {
                "openapi": "3.0.3",
                "info": { "title": "Test", "version": "1.0" },
                "paths": {
                    "/api/items/{id}": {
                        "get": {
                            "operationId": "getItem",
                            "parameters": [
                                { "name": "id", "in": "path", "required": true, "schema": { "type": "string", "format": "uuid" } }
                            ],
                            "responses": {
                                "200": {
                                    "description": "OK",
                                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ItemDto" } } }
                                }
                            }
                        }
                    }
                },
                "components": {
                    "schemas": {
                        "ItemDto": {
                            "type": "object",
                            "properties": {
                                "id": { "type": "string", "format": "uuid" },
                                "name": { "type": "string" }
                            },
                            "required": ["id", "name"]
                        }
                    }
                }
            }
            """;

        var (_, walker1) = ImportAndWalk(openApi);

        // Guid should survive: import recognises uuid → Guid → TypeWalker → Primitive("string", "uuid")
        var idDef = walker1.Definitions.Values.First(d => d.Name == "ItemDto");
        var idProp = idDef.Properties.First(p => p.Name == "id");
        var prim = Assert.IsType<TsType.Primitive>(idProp.Type);
        Assert.Equal("string", prim.Name);
        Assert.Equal("uuid", prim.Format);
    }

    [Fact]
    public void DateTime_Survives_OpenApi_RoundTrip()
    {
        var openApi = """
            {
                "openapi": "3.0.3",
                "info": { "title": "Test", "version": "1.0" },
                "paths": {
                    "/api/events": {
                        "get": {
                            "operationId": "listEvents",
                            "responses": {
                                "200": {
                                    "description": "OK",
                                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/EventDto" } } }
                                }
                            }
                        }
                    }
                },
                "components": {
                    "schemas": {
                        "EventDto": {
                            "type": "object",
                            "properties": {
                                "startedAt": { "type": "string", "format": "date-time" },
                                "day": { "type": "string", "format": "date" }
                            },
                            "required": ["startedAt", "day"]
                        }
                    }
                }
            }
            """;

        var (_, walker) = ImportAndWalk(openApi);
        var def = walker.Definitions.Values.First(d => d.Name == "EventDto");

        var startedAt = def.Properties.First(p => p.Name == "startedAt");
        var prim1 = Assert.IsType<TsType.Primitive>(startedAt.Type);
        Assert.Equal("date-time", prim1.Format);

        var day = def.Properties.First(p => p.Name == "day");
        var prim2 = Assert.IsType<TsType.Primitive>(day.Type);
        Assert.Equal("date", prim2.Format);
    }

    [Fact]
    public void Time_Format_Survives_OpenApi_RoundTrip()
    {
        var openApi = """
            {
                "openapi": "3.0.3",
                "info": { "title": "Test", "version": "1.0" },
                "paths": {
                    "/api/alarms": {
                        "get": {
                            "operationId": "listAlarms",
                            "responses": {
                                "200": {
                                    "description": "OK",
                                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/AlarmDto" } } }
                                }
                            }
                        }
                    }
                },
                "components": {
                    "schemas": {
                        "AlarmDto": {
                            "type": "object",
                            "properties": {
                                "ringAt": { "type": "string", "format": "time" }
                            },
                            "required": ["ringAt"]
                        }
                    }
                }
            }
            """;

        var (_, walker) = ImportAndWalk(openApi);
        var def = walker.Definitions.Values.First(d => d.Name == "AlarmDto");

        var ringAt = def.Properties.First(p => p.Name == "ringAt");
        var prim = Assert.IsType<TsType.Primitive>(ringAt.Type);
        Assert.Equal("string", prim.Name);
        Assert.Equal("time", prim.Format);
    }

    [Fact]
    public void Integer_Formats_Survive_OpenApi_RoundTrip()
    {
        var openApi = """
            {
                "openapi": "3.0.3",
                "info": { "title": "Test", "version": "1.0" },
                "paths": {
                    "/api/data": {
                        "get": {
                            "operationId": "getData",
                            "responses": {
                                "200": {
                                    "description": "OK",
                                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/IntTypesDto" } } }
                                }
                            }
                        }
                    }
                },
                "components": {
                    "schemas": {
                        "IntTypesDto": {
                            "type": "object",
                            "properties": {
                                "i16": { "type": "integer", "format": "int16" },
                                "u16": { "type": "integer", "format": "uint16" },
                                "i8": { "type": "integer", "format": "int8" },
                                "u8": { "type": "integer", "format": "uint8" },
                                "u32": { "type": "integer", "format": "uint32" },
                                "u64": { "type": "integer", "format": "uint64" }
                            },
                            "required": ["i16", "u16", "i8", "u8", "u32", "u64"]
                        }
                    }
                }
            }
            """;

        var (_, walker) = ImportAndWalk(openApi);
        var def = walker.Definitions.Values.First(d => d.Name == "IntTypesDto");

        // Each integer format should map to its specific C# type and back to the right format
        AssertPrimitive(def, "i16", "integer", "int16");
        AssertPrimitive(def, "u16", "integer", "uint16");
        AssertPrimitive(def, "i8", "integer", "int8");
        AssertPrimitive(def, "u8", "integer", "uint8");
        AssertPrimitive(def, "u32", "integer", "uint32");
        AssertPrimitive(def, "u64", "integer", "uint64");
    }

    [Fact]
    public void Uri_Format_Survives_OpenApi_RoundTrip()
    {
        var openApi = """
            {
                "openapi": "3.0.3",
                "info": { "title": "Test", "version": "1.0" },
                "paths": {
                    "/api/links": {
                        "get": {
                            "operationId": "getLink",
                            "responses": {
                                "200": {
                                    "description": "OK",
                                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/LinkDto" } } }
                                }
                            }
                        }
                    }
                },
                "components": {
                    "schemas": {
                        "LinkDto": {
                            "type": "object",
                            "properties": {
                                "href": { "type": "string", "format": "uri" }
                            },
                            "required": ["href"]
                        }
                    }
                }
            }
            """;

        var (_, walker) = ImportAndWalk(openApi);
        var def = walker.Definitions.Values.First(d => d.Name == "LinkDto");

        var href = def.Properties.First(p => p.Name == "href");
        var prim = Assert.IsType<TsType.Primitive>(href.Type);
        Assert.Equal("string", prim.Name);
        Assert.Equal("uri", prim.Format);
    }

    [Fact]
    public void Numeric_Format_Presence_And_Absence_Survive_OpenApi_RoundTrip()
    {
        var openApi = """
            {
                "openapi": "3.0.3",
                "info": { "title": "Test", "version": "1.0" },
                "paths": {
                    "/api/numbers": {
                        "get": {
                            "operationId": "getNumbers",
                            "responses": {
                                "200": {
                                    "description": "OK",
                                    "content": { "application/json": { "schema": { "$ref": "#/components/schemas/NumberDto" } } }
                                }
                            }
                        }
                    }
                },
                "components": {
                    "schemas": {
                        "NumberDto": {
                            "type": "object",
                            "properties": {
                                "snowflake": { "type": "integer", "format": "snowflake" },
                                "nullableSnowflake": {
                                    "oneOf": [
                                        { "type": "null" },
                                        { "$ref": "#/components/schemas/Snowflake" }
                                    ]
                                },
                                "mode": { "$ref": "#/components/schemas/Mode" },
                                "bareInteger": { "type": "integer" },
                                "bareNumber": { "type": "number" }
                            },
                            "required": ["snowflake", "nullableSnowflake", "mode", "bareInteger", "bareNumber"]
                        },
                        "Snowflake": { "type": "integer", "format": "snowflake" },
                        "Mode": { "type": "integer", "format": "int32", "enum": [1, 2] }
                    }
                }
            }
            """;

        var (_, walker) = ImportAndWalk(openApi);
        var def = walker.Definitions.Values.First(d => d.Name == "NumberDto");

        AssertPrimitive(def, "snowflake", "integer", "snowflake");
        var nullableSnowflake = Assert.IsType<TsType.Nullable>(
            def.Properties.First(p => p.Name == "nullableSnowflake").Type
        );
        Assert.Equal("snowflake", Assert.IsType<TsType.Primitive>(nullableSnowflake.Inner).Format);
        Assert.Equal("int32", Assert.IsType<TsType.IntUnion>(walker.Enums["Mode"]).Format);
        Assert.Null(
            Assert
                .IsType<TsType.Primitive>(def.Properties.First(p => p.Name == "bareInteger").Type)
                .Format
        );
        Assert.Equal(
            "integer",
            Assert
                .IsType<TsType.Primitive>(def.Properties.First(p => p.Name == "bareInteger").Type)
                .Name
        );
        Assert.Null(
            Assert
                .IsType<TsType.Primitive>(def.Properties.First(p => p.Name == "bareNumber").Type)
                .Format
        );
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static void AssertPrimitive(
        TsTypeDefinition def,
        string propName,
        string expectedName,
        string expectedFormat
    )
    {
        var prop = def.Properties.First(p => p.Name == propName);
        var prim = Assert.IsType<TsType.Primitive>(prop.Type);
        Assert.Equal(expectedName, prim.Name);
        Assert.Equal(expectedFormat, prim.Format);
    }
}
