using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// The types the walker maps to a primitive leaf: the C# keyword types by Roslyn's
/// <c>SpecialType</c>, <c>byte[]</c>, and the framework types by metadata name. The walker
/// maps through these tables, and the importer reads them to decide which members keep a
/// <c>[RivetSchemaType]</c> (the walker refuses one anywhere else, RIV1117), so the two can't
/// drift.
/// </summary>
internal static class BuiltInScalars
{
    /// <summary>
    /// The C# keyword types, by the <c>SpecialType</c> Roslyn classifies them as and the
    /// keyword the importer writes. A primitive's CSharpType is set only when the type can't
    /// be recovered from its name and format alone.
    /// </summary>
    public static readonly IReadOnlyList<(
        SpecialType Type,
        string Keyword,
        TsType.Primitive Primitive
    )> KeywordTypes =
    [
        (SpecialType.System_String, "string", new TsType.Primitive("string")),
        // char: System.Text.Json writes char as a single-character JSON string (and char
        // dictionary keys as single-character property names). The emitter pins both length
        // bounds to 1, and CSharpType recovers the exact type on import.
        (SpecialType.System_Char, "char", new TsType.Primitive("string", null, "char")),
        // object: "any JSON value". The untyped (empty) schema is the honest spec for it,
        // deliberately and silently. CSharpType "object" tells the emitter the untyped
        // emission is intentional (no RIV2005); the wire schema stays a bare {} with no
        // sidecar.
        (SpecialType.System_Object, "object", new TsType.Primitive("unknown", null, "object")),
        (SpecialType.System_Boolean, "bool", new TsType.Primitive("boolean")),
        (SpecialType.System_Int32, "int", new TsType.Primitive("number", "int32")),
        (SpecialType.System_UInt32, "uint", new TsType.Primitive("number", "uint32", "uint")),
        (SpecialType.System_Int64, "long", new TsType.Primitive("number", "int64")),
        (SpecialType.System_UInt64, "ulong", new TsType.Primitive("number", "uint64", "ulong")),
        (SpecialType.System_Single, "float", new TsType.Primitive("number", "float")),
        (SpecialType.System_Double, "double", new TsType.Primitive("number", "double")),
        (SpecialType.System_Decimal, "decimal", new TsType.Primitive("number", "decimal")),
        (SpecialType.System_Int16, "short", new TsType.Primitive("number", "int16", "short")),
        (SpecialType.System_UInt16, "ushort", new TsType.Primitive("number", "uint16", "ushort")),
        (SpecialType.System_Byte, "byte", new TsType.Primitive("number", "uint8", "byte")),
        (SpecialType.System_SByte, "sbyte", new TsType.Primitive("number", "int8", "sbyte")),
    ];

    private static readonly HashSet<string> _implicitFormatKeywords =
    [
        .. KeywordTypes.Where(row => row.Primitive.Format is not null).Select(row => row.Keyword),
    ];

    /// <summary>
    /// Whether the walker gives <paramref name="keyword"/> a format of its own (<c>int</c> is
    /// int32, <c>double</c> is double), so a member the importer writes with that keyword has
    /// to pin a schema's absent format or the round trip invents one.
    /// </summary>
    public static bool HasImplicitFormat(string keyword) =>
        _implicitFormatKeywords.Contains(keyword);

    /// <summary>
    /// <c>byte[]</c>, the walker's one array scalar: System.Text.Json writes it as a base64
    /// string, never as an integer array, and the spec must match the wire. It's a string
    /// with format "base64" (emitted as <c>contentEncoding: base64</c>, the OpenAPI 3.1
    /// idiom), and CSharpType pins the exact type for import round-trips.
    /// </summary>
    public static readonly (string Keyword, TsType.Primitive Primitive) ByteArray = (
        "byte[]",
        new TsType.Primitive("string", "base64", "byte[]")
    );

    public static readonly IReadOnlyList<(
        string MetadataName,
        TsType.Primitive Primitive
    )> FrameworkTypes =
    [
        ("System.Guid", new TsType.Primitive("string", "uuid")),
        ("System.DateTime", new TsType.Primitive("string", "date-time")),
        ("System.DateTimeOffset", new TsType.Primitive("string", "date-time", "DateTimeOffset")),
        ("System.DateOnly", new TsType.Primitive("string", "date")),
        ("System.TimeOnly", new TsType.Primitive("string", "time")),
        ("System.Uri", new TsType.Primitive("string", "uri")),
        // TimeSpan: System.Text.Json writes the invariant "c" format
        // ([-][d.]hh:mm:ss[.fffffff]) as a JSON string, property names included. No JSON
        // Schema format names it ("duration" is ISO 8601, which STJ refuses), so the
        // exact type rides on x-rivet-csharp-type.
        ("System.TimeSpan", new TsType.Primitive("string", CSharpType: "TimeSpan")),
        // Int128/UInt128/Half: System.Text.Json (.NET 8+) writes them as JSON numbers. No
        // OpenAPI format names them, so the exact type rides on x-rivet-csharp-type.
        ("System.Int128", new TsType.Primitive("number", CSharpType: "Int128")),
        ("System.UInt128", new TsType.Primitive("number", CSharpType: "UInt128")),
        ("System.Half", new TsType.Primitive("number", CSharpType: "Half")),
        (
            "System.Text.Json.JsonElement",
            new TsType.Primitive("unknown", CSharpType: "JsonElement")
        ),
        (
            "System.Text.Json.Nodes.JsonNode",
            new TsType.Primitive("unknown", CSharpType: "JsonNode")
        ),
        (
            "System.Text.Json.Nodes.JsonValue",
            new TsType.Primitive("unknown", CSharpType: "JsonValue")
        ),
        (
            "System.Text.Json.JsonDocument",
            new TsType.Primitive("unknown", CSharpType: "JsonDocument")
        ),
        ("Microsoft.AspNetCore.Http.IFormFile", new TsType.Primitive("File")),
    ];
}
