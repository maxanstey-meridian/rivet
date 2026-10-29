using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

/// <summary>
/// The framework types the walker maps to a primitive leaf, by metadata name. The walker
/// builds its symbol table from this list, and the importer reads it to decide which members
/// keep a <c>[RivetSchemaType]</c> (the walker refuses one anywhere else, RIV1117), so the two
/// can't drift. The C# keyword scalars (<c>string</c>, <c>int</c>, <c>char</c>, <c>object</c>,
/// <c>byte[]</c>, and friends) map through Roslyn's <c>SpecialType</c> in
/// <c>TypeWalker.MapPrimitive</c> instead.
/// </summary>
internal static class BuiltInScalars
{
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
