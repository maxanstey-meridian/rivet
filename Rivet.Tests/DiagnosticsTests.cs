using System.Reflection;
using System.Text.RegularExpressions;
using Rivet.Tool;

namespace Rivet.Tests;

/// <summary>
/// Guards the diagnostic ID scheme: every ID Rivet can emit is registered in
/// Rivet.Tool/Diagnostics.cs, unique, in its stage's range, and documented in
/// docs/reference/diagnostics.md — cross-checked in BOTH directions so neither
/// the registry nor the doc page can rot. Also pins the diagnosed-unsupported
/// TypeWalker holes and the symbol-naming unknown-type emission warning folded
/// into this wave (FABLE_GAPS §7 item 12).
/// </summary>
public sealed class DiagnosticsTests
{
    private static readonly Regex IdPattern = new(@"^RIV[1-4]\d{3}$", RegexOptions.Compiled);

    /// <summary>All public const string ID fields declared on Diagnostics.</summary>
    private static IReadOnlyList<(string FieldName, string Id)> DeclaredIds() =>
        typeof(Diagnostics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .ToList();

    private static string RepoPath(params string[] segments) =>
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", .. segments]);

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
            Assert.True(IdPattern.IsMatch(id), $"{field} = '{id}' is not a canonical RIV[1-4]xxx ID");
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

        Assert.True(duplicates.Count == 0, $"Duplicate diagnostic IDs:\n  {string.Join("\n  ", duplicates)}");
    }

    [Fact]
    public void Registry_And_Declared_Ids_Match_Exactly()
    {
        var declared = DeclaredIds().Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var registered = Diagnostics.Registry.Keys.ToHashSet(StringComparer.Ordinal);

        var unregistered = declared.Except(registered).Order(StringComparer.Ordinal).ToList();
        var undeclared = registered.Except(declared).Order(StringComparer.Ordinal).ToList();

        Assert.True(unregistered.Count == 0,
            $"Declared IDs missing a Registry entry: {string.Join(", ", unregistered)}");
        Assert.True(undeclared.Count == 0,
            $"Registry entries with no declared const: {string.Join(", ", undeclared)}");
    }

    [Fact]
    public void Warn_Writes_The_Canonical_Stderr_Line()
    {
        var stderr = CompilationHelper.CaptureStdErr(
            () => Diagnostics.Warn(Diagnostics.TypeNameCollision, "test message"));

        Assert.Contains("warning RIV1007: test message", stderr);
    }

    [Fact]
    public void Prefix_Produces_The_Canonical_Line_When_Printed_By_Program()
    {
        // Program.cs prints import warnings as $"warning {warning}" — the Prefix
        // form must therefore compose into the same canonical line as Warn.
        Assert.Equal("RIV3001: test message", Diagnostics.Prefix(Diagnostics.ImportAliasCycleBroken, "test message"));
    }

    // ---------------------------------------------------------------
    // Doc page cross-check (docs/reference/diagnostics.md)
    // ---------------------------------------------------------------

    /// <summary>IDs that have a table row (a line starting "| `RIVnnnn`") on the doc page.</summary>
    private static List<string> DocumentedIds()
    {
        var docPath = RepoPath("docs", "reference", "diagnostics.md");
        Assert.True(File.Exists(docPath), $"Doc page not found: {Path.GetFullPath(docPath)}");

        return File.ReadAllLines(docPath)
            .Select(line => Regex.Match(line, @"^\| `(RIV\d{4})` \|"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    [Fact]
    public void Every_Registered_Id_Has_A_Doc_Row()
    {
        var documented = DocumentedIds().ToHashSet(StringComparer.Ordinal);
        var missing = Diagnostics.Registry.Keys
            .Where(id => !documented.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            $"Registered IDs missing a row in docs/reference/diagnostics.md: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_Doc_Row_Is_A_Registered_Id()
    {
        var rogue = DocumentedIds()
            .Where(id => !Diagnostics.Registry.ContainsKey(id))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(rogue.Count == 0,
            $"docs/reference/diagnostics.md rows with no registered ID: {string.Join(", ", rogue)}");
    }

    [Fact]
    public void Doc_Rows_Are_Unique()
    {
        var duplicates = DocumentedIds()
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"docs/reference/diagnostics.md has duplicate rows for: {string.Join(", ", duplicates)}");
    }

    // ---------------------------------------------------------------
    // Diagnosed-unsupported TypeWalker holes (FABLE_GAPS §7 item 12):
    // TimeSpan, BigInteger, char and object used to produce empty/wrong
    // schemas with zero diagnostics. The fallback schema is unchanged —
    // each hole now emits its RIV1xxx warning naming type and property.
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
            _ = Rivet.Tool.Analysis.TypeWalker.Create(compilation, discovered.RivetTypes);
        });
    }

    [Fact]
    public void TimeSpan_Property_Emits_RIV1009_Naming_Type_And_Property()
    {
        var stderr = WalkDtoCapturingStdErr("TimeSpan");

        Assert.Contains("warning RIV1009:", stderr);
        Assert.Contains("TimeSpan", stderr);
        Assert.Contains("'HolderDto.Value'", stderr);
    }

    [Fact]
    public void BigInteger_Property_Emits_RIV1010_Naming_Type_And_Property()
    {
        var stderr = WalkDtoCapturingStdErr(
            "System.Numerics.BigInteger");

        Assert.Contains("warning RIV1010:", stderr);
        Assert.Contains("BigInteger", stderr);
        Assert.Contains("'HolderDto.Value'", stderr);
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
    // RIV2005 — the emitter's catch-all unknown-type warning used to name
    // no symbol at all; it must name the offending type/property site.
    // ---------------------------------------------------------------

    [Fact]
    public void Unknown_Type_Emission_Warning_Names_The_Offending_Property()
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
                public static readonly Define GetPayload =
                    Define.Get<PayloadDto>("/api/payload");
            }
            """;

        var stderr = CompilationHelper.CaptureStdErr(
            () => CompilationHelper.EmitOpenApi(source).Dispose());

        Assert.Contains("warning RIV2005:", stderr);
        // Property names are camelCased in the contract model — the site names the
        // emitted property, not the C# member.
        Assert.Contains("'PayloadDto.data'", stderr);
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

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => CompilationHelper.WalkContract(source));

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

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => CompilationHelper.WalkContract(source));

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

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => CompilationHelper.WalkContract(source));

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

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => CompilationHelper.WalkContract(source));

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

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => CompilationHelper.WalkContract(source));

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

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => CompilationHelper.WalkContract(source));

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

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => CompilationHelper.WalkContract(source));

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

        var exception = Assert.ThrowsAny<InvalidOperationException>(
            () => CompilationHelper.WalkContract(source));

        Assert.Contains("error RIV1022:", exception.Message);
    }
}
