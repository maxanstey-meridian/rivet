using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Dictionary key types (FABLE_GAPS §7 item 12, P2 wave 3): non-string keys carry
/// their contract representation on the Dictionary node and emit as propertyNames.
/// Pins the walker mapping (incl. the headline "vanishing key-enum" fix — the key
/// enum's schema is registered), the emitted propertyNames shapes, the importer's
/// reverse mapping, and the RIV1013 fallback for the genuinely unsupported remainder.
/// </summary>
public sealed class DictionaryKeyTests
{
    // ---------------------------------------------------------------
    // Walker pins
    // ---------------------------------------------------------------

    [Fact]
    public void EnumKeyedDictionary_SetsKey_AndRegistersTheEnum()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            public enum Color { Red, Green, Blue }

            [RivetType]
            public sealed record PaletteDto(Dictionary<Color, int> Usage);
            """;

        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (_, walker) = CompilationHelper.WalkContract(source);
        });

        var usage = Assert.Single(walker.Definitions["PaletteDto"].Properties);
        var dict = Assert.IsType<TsType.Dictionary>(usage.Type);
        Assert.True(
            dict.Key is TsType.TypeRef { Name: "Color" },
            $"Expected TypeRef(Color) key but got {dict.Key}"
        );
        Assert.True(dict.Value is TsType.Primitive { Name: "number", Format: "int32" });

        // Headline fix: the key enum's schema is registered, not vanished
        Assert.True(
            walker.Enums.ContainsKey("Color"),
            $"Color enum vanished. Enums: [{string.Join(", ", walker.Enums.Keys)}]"
        );
        var color = Assert.IsType<TsType.StringUnion>(walker.Enums["Color"]);
        Assert.Equal(["red", "green", "blue"], color.Members);

        // Supported keys no longer diagnose
        Assert.DoesNotContain("RIV1013", stderr);
    }

    [Fact]
    public void StringBackedBrandKeyedDictionary_SetsBrandKey()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record Sku(string Value);

            [RivetType]
            public sealed record StockDto(Dictionary<Sku, int> Levels);
            """;

        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (_, walker) = CompilationHelper.WalkContract(source);
        });

        var levels = Assert.Single(walker.Definitions["StockDto"].Properties);
        var dict = Assert.IsType<TsType.Dictionary>(levels.Type);
        var brand = Assert.IsType<TsType.Brand>(dict.Key);
        Assert.Equal("Sku", brand.Name);
        Assert.True(walker.Brands.ContainsKey("Sku"));
        Assert.DoesNotContain("RIV1013", stderr);
    }

    [Fact]
    public void IntKeyedDictionary_SetsStringTypedKey_WithFormatAndCSharpType()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record CountsDto(Dictionary<int, string> ByYear, Dictionary<long, string> ById);
            """;

        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (_, walker) = CompilationHelper.WalkContract(source);
        });

        var byYear = walker.Definitions["CountsDto"].Properties.First(p => p.Name == "byYear");
        var intKey = Assert.IsType<TsType.Primitive>(
            Assert.IsType<TsType.Dictionary>(byYear.Type).Key
        );
        Assert.Equal(new TsType.Primitive("string", "int32", "int"), intKey);

        var byId = walker.Definitions["CountsDto"].Properties.First(p => p.Name == "byId");
        var longKey = Assert.IsType<TsType.Primitive>(
            Assert.IsType<TsType.Dictionary>(byId.Type).Key
        );
        Assert.Equal(new TsType.Primitive("string", "int64", "long"), longKey);

        Assert.DoesNotContain("RIV1013", stderr);
    }

    [Fact]
    public void GuidAndDateKeyedDictionaries_KeepStringFormats()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record LookupDto(
                Dictionary<Guid, string> ByGuid,
                Dictionary<DateOnly, string> ByDate,
                Dictionary<DateTimeOffset, string> ByStamp);
            """;

        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (_, walker) = CompilationHelper.WalkContract(source);
        });

        TsType? KeyOf(string name) =>
            Assert
                .IsType<TsType.Dictionary>(
                    walker.Definitions["LookupDto"].Properties.First(p => p.Name == name).Type
                )
                .Key;

        Assert.Equal(new TsType.Primitive("string", "uuid"), KeyOf("byGuid"));
        Assert.Equal(new TsType.Primitive("string", "date"), KeyOf("byDate"));
        Assert.Equal(
            new TsType.Primitive("string", "date-time", "DateTimeOffset"),
            KeyOf("byStamp")
        );
        Assert.DoesNotContain("RIV1013", stderr);
    }

    [Fact]
    public void CharKeyedDictionary_SetsStringTypedKey_WithCSharpType()
    {
        // P2 wave 6: System.Text.Json writes char dictionary keys as single-character
        // property names — char joined the supported key matrix (formerly RIV1013).
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record TalliesDto(Dictionary<char, int> ByInitial);
            """;

        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (_, walker) = CompilationHelper.WalkContract(source);
        });

        var byInitial = Assert.Single(walker.Definitions["TalliesDto"].Properties);
        var dict = Assert.IsType<TsType.Dictionary>(byInitial.Type);
        var key = Assert.IsType<TsType.Primitive>(dict.Key);
        Assert.Equal(new TsType.Primitive("string", null, "char"), key);

        Assert.DoesNotContain("RIV1013", stderr);
    }

    [Fact]
    public void UnsupportedKeyTypes_StillEmitRIV1013_AndFallBackToStringKeys()
    {
        var source = """
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            // Non-string-backed brand — not a representable key
            public sealed record Quantity(int Value);

            [RivetType]
            public sealed record HolderDto(
                Dictionary<bool, string> ByFlag,
                Dictionary<Quantity, string> ByQuantity);
            """;

        Rivet.Tool.Analysis.TypeWalker walker = null!;
        var stderr = CompilationHelper.CaptureStdErr(() =>
        {
            (_, walker) = CompilationHelper.WalkContract(source);
        });

        foreach (var prop in walker.Definitions["HolderDto"].Properties)
        {
            var dict = Assert.IsType<TsType.Dictionary>(prop.Type);
            Assert.Null(dict.Key);
        }

        Assert.Contains("warning RIV1013:", stderr);
        Assert.Contains("'bool'", stderr);
        Assert.Contains("'Test.Quantity'", stderr);

        // The unsupported-key probe must not register the brand as a side effect
        Assert.False(
            walker.Brands.ContainsKey("Quantity"),
            "non-string-backed brand key probe must not register the brand"
        );
    }

    // ---------------------------------------------------------------
    // Emitter pins — propertyNames shapes
    // ---------------------------------------------------------------

    private const string EmitterSource = """
        using System.Collections.Generic;
        using Rivet;

        namespace Test;

        public enum Color { Red, Green, Blue }

        [RivetType]
        public sealed record Sku(string Value);

        [RivetType]
        public sealed record MapsDto(
            Dictionary<Color, int> ByColor,
            Dictionary<Sku, int> BySku,
            Dictionary<int, string> ByYear,
            Dictionary<char, int> ByInitial,
            Dictionary<string, string> Plain);

        [RivetContract]
        public static class MapsContract
        {
            public static readonly Define Get = Define.Get<MapsDto>("/api/maps");
        }
        """;

    [Fact]
    public void Emitter_AddsPropertyNames_NextToAdditionalProperties()
    {
        using var doc = CompilationHelper.EmitOpenApi(EmitterSource);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var props = schemas.GetProperty("MapsDto").GetProperty("properties");

        // Enum key → $ref, and the enum component schema exists
        var byColor = props.GetProperty("byColor");
        Assert.Equal("object", byColor.GetProperty("type").GetString());
        Assert.Equal(
            "integer",
            byColor.GetProperty("additionalProperties").GetProperty("type").GetString()
        );
        Assert.Equal(
            "#/components/schemas/Color",
            byColor.GetProperty("propertyNames").GetProperty("$ref").GetString()
        );
        var colorValues = schemas
            .GetProperty("Color")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Equal(["red", "green", "blue"], colorValues);

        // Brand key → $ref to the brand schema
        Assert.Equal(
            "#/components/schemas/Sku",
            props.GetProperty("bySku").GetProperty("propertyNames").GetProperty("$ref").GetString()
        );

        // Numeric key → string-typed with format + x-rivet-csharp-type
        var byYearKeys = props.GetProperty("byYear").GetProperty("propertyNames");
        Assert.Equal("string", byYearKeys.GetProperty("type").GetString());
        Assert.Equal("int32", byYearKeys.GetProperty("format").GetString());
        Assert.Equal("int", byYearKeys.GetProperty("x-rivet-csharp-type").GetString());

        // char key → string-typed with length-1 bounds + x-rivet-csharp-type (P2 wave 6)
        var byInitialKeys = props.GetProperty("byInitial").GetProperty("propertyNames");
        Assert.Equal("string", byInitialKeys.GetProperty("type").GetString());
        Assert.Equal(1, byInitialKeys.GetProperty("minLength").GetInt32());
        Assert.Equal(1, byInitialKeys.GetProperty("maxLength").GetInt32());
        Assert.Equal("char", byInitialKeys.GetProperty("x-rivet-csharp-type").GetString());
        Assert.False(byInitialKeys.TryGetProperty("format", out _));

        // String keys stay propertyNames-less (status quo)
        Assert.False(props.GetProperty("plain").TryGetProperty("propertyNames", out _));
    }

    // ---------------------------------------------------------------
    // Importer pins — propertyNames read-back
    // ---------------------------------------------------------------

    [Fact]
    public void Importer_ReadsPropertyNames_EnumRef_AsEnumKey()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Color": { "type": "string", "enum": ["red", "green"] },
            "PaletteDto": {
                "type": "object",
                "properties": {
                    "usage": {
                        "type": "object",
                        "additionalProperties": { "type": "integer", "format": "int32" },
                        "propertyNames": { "$ref": "#/components/schemas/Color" }
                    }
                },
                "required": ["usage"]
            }
            """
        );

        var result = CompilationHelper.Import(spec);

        var palette = CompilationHelper.FindFile(result, "PaletteDto.cs");
        Assert.Contains("Dictionary<Color, int>", palette);
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("RIV3014"));
        CompilationHelper.CompileImportResult(result);
    }

    [Fact]
    public void Importer_ReadsPropertyNames_FormatAndCSharpType_AsKeyTypes()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "LookupDto": {
                "type": "object",
                "properties": {
                    "byGuid": {
                        "type": "object",
                        "additionalProperties": { "type": "string" },
                        "propertyNames": { "type": "string", "format": "uuid" }
                    },
                    "byYear": {
                        "type": "object",
                        "additionalProperties": { "type": "string" },
                        "propertyNames": { "type": "string", "format": "int32", "x-rivet-csharp-type": "int" }
                    },
                    "byInitial": {
                        "type": "object",
                        "additionalProperties": { "type": "string" },
                        "propertyNames": { "type": "string", "minLength": 1, "maxLength": 1, "x-rivet-csharp-type": "char" }
                    }
                },
                "required": ["byGuid", "byYear", "byInitial"]
            }
            """
        );

        var result = CompilationHelper.Import(spec);

        var lookup = CompilationHelper.FindFile(result, "LookupDto.cs");
        Assert.Contains("Dictionary<Guid, string>", lookup);
        Assert.Contains("Dictionary<int, string>", lookup);
        Assert.Contains("Dictionary<char, string>", lookup);
        CompilationHelper.CompileImportResult(result);
    }

    [Fact]
    public void Importer_UnsupportedPropertyNames_DegradesToStringKeys_WithRIV3014()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "RecordDto": {
                "type": "object",
                "properties": { "value": { "type": "string" } }
            },
            "HolderDto": {
                "type": "object",
                "properties": {
                    "byNumber": {
                        "type": "object",
                        "additionalProperties": { "type": "string" },
                        "propertyNames": { "type": "integer" }
                    },
                    "byRecord": {
                        "type": "object",
                        "additionalProperties": { "type": "string" },
                        "propertyNames": { "$ref": "#/components/schemas/RecordDto" }
                    }
                }
            }
            """
        );

        var result = CompilationHelper.Import(spec);

        var holder = CompilationHelper.FindFile(result, "HolderDto.cs");
        Assert.Contains("Dictionary<string, string>", holder);
        Assert.DoesNotContain("Dictionary<RecordDto", holder);

        // Named diagnostic, once per degraded key — never silent
        Assert.Equal(2, result.Warnings.Count(w => w.StartsWith("RIV3014")));
        CompilationHelper.CompileImportResult(result);
    }

    // ---------------------------------------------------------------
    // Contract-JSON wire format — "key" is optional (TS lowerer never emits it)
    // ---------------------------------------------------------------

    [Fact]
    public void TsTypeJson_DictionaryKey_RoundTrips_AndAbsenceDeserializes()
    {
        var withKey = new TsType.Dictionary(
            new TsType.Primitive("number", "int32"),
            new TsType.TypeRef("Color")
        );

        var json = JsonSerializer.Serialize<TsType>(withKey);
        Assert.Contains("\"key\"", json);
        var back = Assert.IsType<TsType.Dictionary>(JsonSerializer.Deserialize<TsType>(json));
        Assert.Equal(withKey, back);

        // Old contract JSON (TS lowerer) never carries "key" — must keep deserializing
        var legacy = """{"kind":"dictionary","value":{"kind":"primitive","type":"string"}}""";
        var dict = Assert.IsType<TsType.Dictionary>(JsonSerializer.Deserialize<TsType>(legacy));
        Assert.Null(dict.Key);
        Assert.DoesNotContain("\"key\"", JsonSerializer.Serialize<TsType>(dict));
    }
}
