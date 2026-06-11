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

    [Fact]
    public void Char_Property_Emits_RIV1011_Naming_Type_And_Property()
    {
        var stderr = WalkDtoCapturingStdErr("char");

        Assert.Contains("warning RIV1011:", stderr);
        Assert.Contains("'char'", stderr);
        Assert.Contains("'HolderDto.Value'", stderr);
    }

    [Fact]
    public void Object_Property_Emits_RIV1012_Naming_Type_And_Property()
    {
        var stderr = WalkDtoCapturingStdErr("object");

        Assert.Contains("warning RIV1012:", stderr);
        Assert.Contains("'object'", stderr);
        Assert.Contains("'HolderDto.Value'", stderr);
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
}
