using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rivet.Tool;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Guards the diagnostic ID scheme: every ID Rivet can emit is registered in
/// Rivet.Tool/Diagnostics.cs, unique, in its stage's range, and has a row in
/// docs/reference/diagnostics.md. Also pins the diagnosed-unsupported
/// TypeWalker holes and the symbol-naming unknown-type emission warning folded
/// into this wave (FABLE_GAPS §7 item 12).
/// </summary>
public sealed class DiagnosticsTests
{
    private static readonly Regex _idPattern = new(@"^RIV[1-4]\d{3}$", RegexOptions.Compiled);

    /// <summary>All public const string ID fields declared on Diagnostics.</summary>
    private static IReadOnlyList<(string FieldName, string Id)> DeclaredIds() =>
        typeof(Diagnostics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .ToList();

    // ---------------------------------------------------------------
    // Registry invariants
    // ---------------------------------------------------------------

    [Fact]
    public void Every_Declared_Id_Is_Canonical_And_In_A_Known_Range()
    {
        var ids = DeclaredIds();
        Assert.NotEmpty(ids);

        foreach (var (field, id) in ids)
        {
            Assert.True(
                _idPattern.IsMatch(id),
                $"{field} = '{id}' is not a canonical RIV[1-4]xxx ID"
            );
        }
    }

    [Fact]
    public void Declared_Ids_Are_Unique()
    {
        var duplicates = DeclaredIds()
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.FieldName))})")
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"Duplicate diagnostic IDs:\n  {string.Join("\n  ", duplicates)}"
        );
    }

    [Fact]
    public void Native_Rivet_Does_Not_Allocate_The_Php_Sibling_Runtime_Block()
    {
        var allocations = DeclaredIds()
            .Where(x =>
                int.TryParse(x.Id.AsSpan(3), out var number) && number is >= 1024 and <= 1099
            )
            .Select(x => $"{x.FieldName} = {x.Id}")
            .ToList();

        Assert.True(
            allocations.Count == 0,
            $"RIV1024-RIV1099 are reserved for rivet/php: {string.Join(", ", allocations)}"
        );
    }

    [Fact]
    public void Registry_And_Declared_Ids_Match_Exactly()
    {
        var declared = DeclaredIds().Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var registered = Diagnostics.Registry.Keys.ToHashSet(StringComparer.Ordinal);

        var unregistered = declared.Except(registered).Order(StringComparer.Ordinal).ToList();
        var undeclared = registered.Except(declared).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unregistered.Count == 0,
            $"Declared IDs missing a Registry entry: {string.Join(", ", unregistered)}"
        );
        Assert.True(
            undeclared.Count == 0,
            $"Registry entries with no declared const: {string.Join(", ", undeclared)}"
        );
    }

    [Fact]
    public void Registry_And_Docs_Table_List_The_Same_Ids()
    {
        // Descriptions differ on purpose (the page explains, the registry is one line), so
        // only the IDs are compared: a row per ID, and no row for an unregistered ID.
        var page = File.ReadAllText(CliRunner.RepoPath("docs", "reference", "diagnostics.md"));
        var documented = Regex
            .Matches(page, @"^\| `(RIV\d{4})`", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var registered = Diagnostics.Registry.Keys.ToHashSet(StringComparer.Ordinal);

        var undocumented = registered.Except(documented).Order(StringComparer.Ordinal).ToList();
        var unknown = documented.Except(registered).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            undocumented.Count == 0,
            $"Registry IDs with no row in docs/reference/diagnostics.md: {string.Join(", ", undocumented)}"
        );
        Assert.True(
            unknown.Count == 0,
            $"docs/reference/diagnostics.md rows for unregistered IDs: {string.Join(", ", unknown)}"
        );
    }

    [Fact]
    public void Warn_Writes_The_Canonical_Stderr_Line()
    {
        var stderr = CompilationHelper.CaptureStdErr(() =>
            Diagnostics.Warn(Diagnostics.DictionaryKeyTypeDropped, "test message")
        );

        Assert.Contains("warning RIV1013: test message", stderr);
    }

    [Fact]
    public void Prefix_Produces_The_Canonical_Line_When_Printed_By_Program()
    {
        // Program.cs prints import warnings as $"warning {warning}" — the Prefix
        // form must therefore compose into the same canonical line as Warn.
        Assert.Equal(
            "RIV3001: test message",
            Diagnostics.Prefix(Diagnostics.ImportAliasCycleBroken, "test message")
        );
    }

    // ---------------------------------------------------------------
    // TypeWalker holes (FABLE_GAPS §7 item 12): TimeSpan, BigInteger, char and
    // object used to produce empty schemas. char, object and TimeSpan now map to
    // what System.Text.Json writes; BigInteger, which STJ cannot round-trip, is refused.
    // ---------------------------------------------------------------

    private static string WalkDtoCapturingStdErr(string propertyType, string extraUsing = "")
    {
        var source = $$"""
            using System;
            {{extraUsing}}
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record HolderDto({{propertyType}} Value);
            """;

        var compilation = CompilationHelper.CreateCompilation(source);
        return CompilationHelper.CaptureStdErr(() =>
        {
            var discovered = Rivet.Tool.Analysis.SymbolDiscovery.Discover(compilation);
            _ = Rivet.Tool.Analysis.TypeWalker.Create(
                compilation,
                new Rivet.Tool.Analysis.WellKnownTypes(compilation),
                discovered.RivetTypes
            );
        });
    }

    [Fact]
    public void Int128_UInt128_And_Half_Map_To_The_Numbers_System_Text_Json_Writes()
    {
        var source = """
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record HolderDto(Int128 Big, UInt128 Bigger, Half Small);

            [RivetContract]
            public static class HolderContract
            {
                public static readonly RouteDefinition<HolderDto> Get = Define.Get<HolderDto>("/api/holder");
            }
            """;

        using var emitted = CompilationHelper.EmitOpenApi(source);
        var props = emitted
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("HolderDto")
            .GetProperty("properties");
        foreach (
            var (name, type, csharpType) in new[]
            {
                ("big", "integer", "Int128"),
                ("bigger", "integer", "UInt128"),
                ("small", "number", "Half"),
            }
        )
        {
            Assert.Equal(type, props.GetProperty(name).GetProperty("type").GetString());
            Assert.Equal(
                csharpType,
                props.GetProperty(name).GetProperty("x-rivet-csharp-type").GetString()
            );
        }

        Assert.Equal(
            "{\"big\":170141183460469231731687303715884105727,\"bigger\":1,\"small\":0.5}",
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    big = Int128.MaxValue,
                    bigger = (UInt128)1,
                    small = (Half)0.5,
                }
            )
        );
    }

    [Fact]
    public void TimeSpan_Maps_To_The_String_System_Text_Json_Writes()
    {
        var source = """
            using System;
            using System.Collections.Generic;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record HolderDto(TimeSpan Value, TimeSpan? Maybe, Dictionary<TimeSpan, int> ByDuration);

            [RivetContract]
            public static class HolderContract
            {
                public static readonly RouteDefinition<HolderDto> Get = Define.Get<HolderDto>("/api/holder");
            }
            """;

        JsonDocument? doc = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            doc = CompilationHelper.EmitOpenApi(source)
        );
        using var emitted = doc!;
        Assert.DoesNotContain("'HolderDto.value'", stderr);

        var props = emitted
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("HolderDto")
            .GetProperty("properties");
        var value = props.GetProperty("value");
        Assert.Equal("string", value.GetProperty("type").GetString());
        Assert.Equal("TimeSpan", value.GetProperty("x-rivet-csharp-type").GetString());
        Assert.False(value.TryGetProperty("format", out _));
        Assert.Equal(
            @"^-?(\d{1,8}\.)?([01]\d|2[0-3]):[0-5]\d:[0-5]\d(\.\d{1,7})?$",
            value.GetProperty("pattern").GetString()
        );
        Assert.Equal(JsonValueKind.Array, props.GetProperty("maybe").GetProperty("type").ValueKind);
        Assert.Equal(
            "TimeSpan",
            props
                .GetProperty("byDuration")
                .GetProperty("propertyNames")
                .GetProperty("x-rivet-csharp-type")
                .GetString()
        );

        // The wire side: STJ writes the invariant "c" format as a JSON string, days and
        // sign included, and uses the same text for dictionary keys.
        foreach (
            var (span, wire) in new[]
            {
                (TimeSpan.Zero, "\"00:00:00\""),
                (new TimeSpan(1, 2, 3, 4, 500), "\"1.02:03:04.5000000\""),
                (TimeSpan.FromDays(-3.5), "\"-3.12:00:00\""),
            }
        )
        {
            Assert.Equal(wire, JsonSerializer.Serialize(span, JsonSerializerOptions.Web));
        }
        Assert.Equal(
            """{"01:00:00":1}""",
            JsonSerializer.Serialize(
                new Dictionary<TimeSpan, int> { [TimeSpan.FromHours(1)] = 1 },
                JsonSerializerOptions.Web
            )
        );
    }

    [Fact]
    public void BigInteger_Property_Fails_With_RIV1010_Naming_Type_And_Property()
    {
        // STJ has no converter: it writes the struct's properties and cannot read them back.
        Assert.Contains(
            "\"isZero\"",
            JsonSerializer.Serialize(new System.Numerics.BigInteger(5), JsonSerializerOptions.Web)
        );

        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record HolderDto(System.Numerics.BigInteger Value);
            """;
        var compilation = CompilationHelper.CreateCompilation(source);

        var message = Assert
            .Throws<RivetUserException>(() => CompilationHelper.DiscoverAndWalk(compilation))
            .Message;

        Assert.StartsWith("error RIV1010:", message);
        Assert.Contains("BigInteger", message);
        Assert.Contains("'HolderDto.Value'", message);
        Assert.Contains("string of digits", message);
    }

    // char and object graduated to supported mappings in P2 wave 6 — RIV1011 and
    // RIV1012 are retired (tombstoned in Diagnostics.cs and diagnostics.md) and the
    // walk must be silent for both.

    [Fact]
    public void Char_Property_Is_Supported_And_Walks_Silently()
    {
        var stderr = WalkDtoCapturingStdErr("char");

        Assert.DoesNotContain("RIV1011", stderr);
        Assert.DoesNotContain("has no schema mapping", stderr);
    }

    [Fact]
    public void Object_Property_Is_Supported_And_Walks_Silently()
    {
        var stderr = WalkDtoCapturingStdErr("object");

        Assert.DoesNotContain("RIV1012", stderr);
        Assert.DoesNotContain("has no schema mapping", stderr);
    }

    [Fact]
    public void Supported_Scalars_Emit_No_Unsupported_Type_Diagnostic()
    {
        var stderr = WalkDtoCapturingStdErr("Guid");

        Assert.DoesNotContain("has no schema mapping", stderr);
    }

    // ---------------------------------------------------------------
    // RIV2005 — a type with no schema mapping fails generation; "any JSON
    // value" (JsonElement, object, the JsonNode family) is a mapping, not a gap.
    // ---------------------------------------------------------------

    [Fact]
    public void Explicit_JsonElement_Emits_An_Untyped_Schema_Without_A_Diagnostic()
    {
        var source = """
            using System.Text.Json;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record PayloadDto(JsonElement Data);

            [RivetContract]
            public static class PayloadContract
            {
                public static readonly RouteDefinition<PayloadDto> GetPayload =
                    Define.Get<PayloadDto>("/api/payload");
            }
            """;

        // RIV2005 is an error now, so a regression would throw here rather than warn.
        using var doc = CompilationHelper.EmitOpenApi(source);

        var data = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("PayloadDto")
            .GetProperty("properties")
            .GetProperty("data");
        Assert.Empty(data.EnumerateObject());
    }

    [Fact]
    public void Unmapped_CSharpType_Fails_With_RIV2005_Naming_The_Site()
    {
        var definitions = new Dictionary<string, TsTypeDefinition>
        {
            ["SlotDto"] = new(
                "SlotDto",
                [],
                [
                    new TsPropertyDefinition(
                        "window",
                        new TsType.Primitive("unknown", CSharpType: "System.TimeSpan"),
                        false
                    ),
                ]
            ),
        };

        var exception = Assert.Throws<RivetUserException>(() =>
            OpenApiEmitter.Emit(
                [],
                definitions,
                new Dictionary<string, TsType.Brand>(),
                new Dictionary<string, TsType>(),
                null
            )
        );

        Assert.StartsWith("error RIV2005:", exception.Message);
        Assert.Contains("'System.TimeSpan'", exception.Message);
        Assert.Contains("SlotDto", exception.Message);
    }

    // ---------------------------------------------------------------
    // RIV1021 — a status carries one response shape. The runtime builder throws
    // on a duplicate .Returns(); the generator (a syntax walker, which never runs
    // the throwing initializer) must reject the same contradiction at generation time.
    // ---------------------------------------------------------------

    [Fact]
    public void Duplicate_Response_Status_Fails_With_RIV1021()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType] public sealed record ThingRequest(string Id);
            [RivetType] public sealed record FirstError(string Message);
            [RivetType] public sealed record SecondError(string Message);

            [RivetContract]
            public static class DupContract
            {
                public static readonly RouteDefinition<ThingRequest, ThingRequest> MakeThing =
                    Define.Post<ThingRequest, ThingRequest>("/api/things")
                        .Returns<FirstError>(422, "first")
                        .Returns<SecondError>(422, "second");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("error RIV1021:", exception.Message);
        Assert.Contains("422", exception.Message);
    }

    [Fact]
    public void Duplicate_Success_Status_Fails_With_RIV1021()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetContract]
            public static class DupContract
            {
                public static readonly RouteDefinition MakeThing =
                    Define.Post("/api/things")
                        .Status(202)
                        .Status(203);
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("error RIV1021:", exception.Message);
        Assert.Contains("calls .Status() more than once", exception.Message);
    }

    [Fact]
    public void Default_Success_And_Returns_Collision_Fails_With_RIV1021()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record Item(string Id);
            public sealed record Error(string Message);

            [RivetContract]
            public static class ItemsContract
            {
                public static readonly RouteDefinition<Item> Create =
                    Define.Post<Item>("/api/items")
                        .Returns<Error>(201);
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("error RIV1021:", exception.Message);
        Assert.Contains("201", exception.Message);
    }

    [Fact]
    public void Abstract_Contract_Duplicate_Response_Status_Fails_With_RIV1021()
    {
        var source = """
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;
            using Rivet;

            namespace Test;

            public sealed record FirstError(string Message);
            public sealed record SecondError(string Message);

            [RivetContract]
            [Route("api/items")]
            public abstract class ItemsContract : ControllerBase
            {
                [HttpGet]
                [ProducesResponseType(typeof(FirstError), 422)]
                [ProducesResponseType(typeof(SecondError), 422)]
                public abstract Task<IActionResult> Get();
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("error RIV1021:", exception.Message);
        Assert.Contains("422", exception.Message);
    }

    [Fact]
    public void Request_Body_Provenance_Must_Name_An_Input_Property()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record UpdateInput(string Id, string Role);
            public sealed record Unrelated(string Value);

            [RivetContract]
            public static class UpdateContract
            {
                [RivetRequestBody(typeof(Unrelated))]
                public static readonly RouteDefinition<UpdateInput> Update =
                    Define.Put<UpdateInput>("/api/items/{id}");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("error RIV1022:", exception.Message);
        Assert.Contains("Unrelated", exception.Message);
    }

    [Fact]
    public void Request_Body_Provenance_Holder_Cannot_Also_Be_Route_Bound()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record Body(string Value);
            public sealed record UpdateInput(Body Body);

            [RivetContract]
            public static class UpdateContract
            {
                [RivetRequestBody(typeof(Body))]
                public static readonly RouteDefinition<UpdateInput> Update =
                    Define.Put<UpdateInput>("/api/items/{body}");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("error RIV1022:", exception.Message);
    }

    [Fact]
    public void Request_Body_Provenance_Uses_Serialized_Property_Names()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            public sealed record UpdateInput(
                string Id,
                [property: JsonPropertyName("input_value")] string Value);
            public sealed record Body(
                [property: JsonPropertyName("body_value")] string Value);

            [RivetContract]
            public static class UpdateContract
            {
                [RivetRequestBody(typeof(Body))]
                public static readonly RouteDefinition<UpdateInput> Update =
                    Define.Put<UpdateInput>("/api/items/{id}");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("error RIV1022:", exception.Message);
    }

    [Fact]
    public void Empty_Request_Body_Type_Is_Not_A_Valid_Flattened_Projection()
    {
        var source = """
            using Rivet;

            namespace Test;

            public sealed record UpdateInput(string Id, string Value);
            public sealed record EmptyBody;

            [RivetContract]
            public static class UpdateContract
            {
                [RivetRequestBody(typeof(EmptyBody))]
                public static readonly RouteDefinition<UpdateInput> Update =
                    Define.Put<UpdateInput>("/api/items/{id}");
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("error RIV1022:", exception.Message);
    }
}
