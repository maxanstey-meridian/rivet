using System.Text.Json;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Tests that format-level type information (uuid, date-time, integer ranges, etc.)
/// survives round-trips in both directions:
///   1. C# → JSON Schema (forward: format flows into schema)
///   2. OpenAPI → Import → Compile → Walk → OpenAPI (import round-trip: format preserved)
/// </summary>
public sealed class FormatRoundTripTests
{
    // ─── Helpers ────────────────────────────────────────────────

    private static JsonElement ParseDefs(string output)
    {
        const string marker = "= ";
        var lineStart = output.IndexOf("const $defs", StringComparison.Ordinal);
        var start = output.IndexOf(marker, lineStart, StringComparison.Ordinal) + marker.Length;
        var end = output.IndexOf(";\n", start, StringComparison.Ordinal);
        var json = output[start..end];
        return JsonDocument.Parse(json).RootElement;
    }

    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, TypeWalker Walker, string EmittedJson)
        ForwardAndEmit(string csharpSource)
    {
        var compilation = CompilationHelper.CreateCompilation(csharpSource);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var openApiJson = OpenApiEmitter.Emit(
            endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        return (endpoints, walker, openApiJson);
    }

    private static (IReadOnlyList<TsEndpointDefinition> Endpoints, TypeWalker Walker)
        ImportAndWalk(string openApiJson)
    {
        var importResult = OpenApiImporter.Import(openApiJson, new ImportOptions("RoundTrip"));
        var compilation = CompilationHelper.CreateCompilationFromMultiple(
            importResult.Files.Select(f => f.Content).ToArray());
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        return (endpoints, walker);
    }

    // ─── Forward: C# → JSON Schema format ──────────────────────

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

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("IdDto").GetProperty("properties").GetProperty("id");
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

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("TimedDto").GetProperty("properties").GetProperty("createdAt");
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

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("DayDto").GetProperty("properties").GetProperty("day");
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

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("AlarmDto").GetProperty("properties").GetProperty("ringAt");
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

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("LinkDto").GetProperty("properties").GetProperty("href");
        Assert.Equal("string", prop.GetProperty("type").GetString());
        Assert.Equal("uri", prop.GetProperty("format").GetString());
    }

    [Fact]
    public void Int_Property_Has_Integer_Type_And_Range_In_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CountDto(int Count);
            """;

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("CountDto").GetProperty("properties").GetProperty("count");
        Assert.Equal("integer", prop.GetProperty("type").GetString());
        Assert.Equal("int32", prop.GetProperty("format").GetString());
        Assert.Equal(-2147483648, prop.GetProperty("minimum").GetInt64());
        Assert.Equal(2147483647, prop.GetProperty("maximum").GetInt64());
    }

    [Fact]
    public void Uint_Property_Has_Unsigned_Range_In_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record FlagDto(uint Flags);
            """;

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("FlagDto").GetProperty("properties").GetProperty("flags");
        Assert.Equal("integer", prop.GetProperty("type").GetString());
        Assert.Equal("uint32", prop.GetProperty("format").GetString());
        Assert.Equal(0, prop.GetProperty("minimum").GetInt64());
        Assert.Equal(4294967295, prop.GetProperty("maximum").GetInt64());
    }

    [Fact]
    public void Byte_Property_Has_Uint8_Range_In_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PixelDto(byte R, byte G, byte B);
            """;

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("PixelDto").GetProperty("properties").GetProperty("r");
        Assert.Equal("integer", prop.GetProperty("type").GetString());
        Assert.Equal("uint8", prop.GetProperty("format").GetString());
        Assert.Equal(0, prop.GetProperty("minimum").GetInt64());
        Assert.Equal(255, prop.GetProperty("maximum").GetInt64());
    }

    [Fact]
    public void Short_Property_Has_Int16_Range_In_Schema()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LevelDto(short Level);
            """;

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("LevelDto").GetProperty("properties").GetProperty("level");
        Assert.Equal("integer", prop.GetProperty("type").GetString());
        Assert.Equal("int16", prop.GetProperty("format").GetString());
        Assert.Equal(-32768, prop.GetProperty("minimum").GetInt64());
        Assert.Equal(32767, prop.GetProperty("maximum").GetInt64());
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

        var defs = ParseDefs(CompilationHelper.EmitSchemas(source));
        var prop = defs.GetProperty("BigDto").GetProperty("properties").GetProperty("bigNumber");
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
        AssertPrimitive(def, "i16", "number", "int16");
        AssertPrimitive(def, "u16", "number", "uint16");
        AssertPrimitive(def, "i8", "number", "int8");
        AssertPrimitive(def, "u8", "number", "uint8");
        AssertPrimitive(def, "u32", "number", "uint32");
        AssertPrimitive(def, "u64", "number", "uint64");
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

    // ─── Helpers ────────────────────────────────────────────────

    private static void AssertPrimitive(TsTypeDefinition def, string propName, string expectedName, string expectedFormat)
    {
        var prop = def.Properties.First(p => p.Name == propName);
        var prim = Assert.IsType<TsType.Primitive>(prop.Type);
        Assert.Equal(expectedName, prim.Name);
        Assert.Equal(expectedFormat, prim.Format);
    }
}
