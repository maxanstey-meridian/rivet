using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rivet.Tool;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// Guards the diagnostic ID scheme: every ID Rivet can emit is registered in
/// Rivet.Tool/Diagnostics.cs, unique, in its stage's range, and has a row in
/// docs/reference/diagnostics.md with the severity the registry gives it. Also pins the diagnosed-unsupported
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
    public void Nothing_Reuses_The_Dropped_Php_Runtime_Block()
    {
        var allocations = DeclaredIds()
            .Where(x =>
                int.TryParse(x.Id.AsSpan(3), out var number) && number is >= 1024 and <= 1099
            )
            .Select(x => $"{x.FieldName} = {x.Id}")
            .ToList();

        Assert.True(
            allocations.Count == 0,
            $"RIV1024-RIV1099 once belonged to the dropped rivet/php runtime and are never reused: {string.Join(", ", allocations)}"
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
    public void Docs_Severity_Column_Matches_The_Registry()
    {
        // The registry decides each ID's severity (Warn and Error refuse the other kind), so
        // the page's Severity column must say the same thing for every row.
        var page = File.ReadAllText(CliRunner.RepoPath("docs", "reference", "diagnostics.md"));
        var rows = Regex
            .Matches(page, @"^\| `(RIV\d{4})` \| (\w+) \|", RegexOptions.Multiline)
            .Select(match => (Id: match.Groups[1].Value, Column: match.Groups[2].Value))
            .ToList();

        // A row whose Severity cell this pattern can't read (bold, say) would otherwise be
        // skipped silently, so every registered ID must have a readable one.
        Assert.Equal(
            Diagnostics.Registry.Keys.Order(StringComparer.Ordinal),
            rows.Select(row => row.Id).Order(StringComparer.Ordinal)
        );
        var mismatches = rows.Where(row =>
                Diagnostics.Registry.TryGetValue(row.Id, out var entry)
                && row.Column != entry.Severity.ToString()
            )
            .Select(row =>
                $"{row.Id}: page says {row.Column}, registry says {Diagnostics.Registry[row.Id].Severity}"
            )
            .ToList();

        Assert.True(
            mismatches.Count == 0,
            $"Severity column disagrees with the registry:\n  {string.Join("\n  ", mismatches)}"
        );
    }

    [Fact]
    public void Docs_Intro_Lists_Exactly_The_Error_Rows()
    {
        var page = File.ReadAllText(CliRunner.RepoPath("docs", "reference", "diagnostics.md"));
        var intro = Regex.Match(page, @"The errors are\s+(.*?)\.\s", RegexOptions.Singleline);
        Assert.True(intro.Success, "docs/reference/diagnostics.md lost its list of errors.");
        var listed = Regex
            .Matches(intro.Groups[1].Value, @"`(RIV\d{4})`")
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal);
        var errorRows = Regex
            .Matches(page, @"^\| `(RIV\d{4})` \| Error \|", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal);

        Assert.Equal(errorRows, listed);
    }

    [Fact]
    public void Every_Retired_Id_Has_A_Tombstone_And_No_Other()
    {
        // A retired ID keeps a comment in Diagnostics.cs and a tombstone on the page, so its
        // number is never reused and a reader who meets it in an old log learns what it was.
        var source = File.ReadAllText(CliRunner.RepoPath("Rivet.Tool", "Diagnostics.cs"));
        var retired = Regex
            .Matches(source, @"//\s*(RIV\d{4}) \(\w+\) retired")
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal);
        var page = File.ReadAllText(CliRunner.RepoPath("docs", "reference", "diagnostics.md"));
        var tombstones = Regex
            .Matches(page, @"^- ~~`(RIV\d{4})`~~", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal);

        Assert.Equal(retired, tombstones);
    }

    [Fact]
    public void Every_Call_Naming_Its_Id_Uses_The_Registry_Severity()
    {
        // Warn, Prefix and Error refuse an ID of the other severity, but only when that path
        // runs, which may first be in a user's run. Every call that names its ID as
        // Diagnostics.X is held to the registry here instead. (RivetUserException's private
        // constructor already makes Diagnostics.Error the one way to build a user error.)
        var ids = DeclaredIds().ToDictionary(x => x.FieldName, x => x.Id, StringComparer.Ordinal);
        var toolRoot = CliRunner.RepoPath("Rivet.Tool");
        var calls = 0;
        var mismatches = new List<string>();
        foreach (
            var path in Directory
                .EnumerateFiles(toolRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    && !path.Contains(
                        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"
                    )
                )
        )
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
            foreach (
                var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            )
            {
                if (
                    call.Expression
                        is not MemberAccessExpressionSyntax
                        {
                            Expression: IdentifierNameSyntax { Identifier.Text: "Diagnostics" },
                            Name.Identifier.Text: ("Warn" or "Prefix" or "Error") and var method,
                        }
                    || call.ArgumentList.Arguments.FirstOrDefault()?.Expression
                        is not MemberAccessExpressionSyntax
                        {
                            Expression: IdentifierNameSyntax { Identifier.Text: "Diagnostics" },
                            Name.Identifier.Text: var field,
                        }
                )
                {
                    continue;
                }

                calls++;
                var expected =
                    method == "Error" ? Diagnostics.Severity.Error : Diagnostics.Severity.Warning;
                if (
                    !ids.TryGetValue(field, out var id)
                    || Diagnostics.Registry[id].Severity != expected
                )
                {
                    var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    mismatches.Add(
                        $"{Path.GetRelativePath(toolRoot, path)}:{line}: Diagnostics.{method}(Diagnostics.{field})"
                    );
                }
            }
        }

        // A pattern that stopped matching would pass vacuously.
        Assert.True(calls > 100, $"Only {calls} Diagnostics.Warn/Prefix/Error calls found.");
        Assert.True(
            mismatches.Count == 0,
            $"Calls whose ID is registered with the other severity:\n  {string.Join("\n  ", mismatches)}"
        );
    }

    [Theory]
    [InlineData(
        """[assembly: RivetSecurityScheme("key", "apiKey", parameterName: "X-Key", location: "body")]""",
        "unsupported location 'body'"
    )]
    [InlineData(
        """[assembly: RivetSecurityScheme("key", "apiKey", parameterName: "X-Key", location: "7")]""",
        "unsupported location '7'"
    )]
    [InlineData(
        """[assembly: RivetSecurityScheme("key", "apiKey", parameterName: "X-Key", location: "header,query")]""",
        "unsupported location 'header,query'"
    )]
    [InlineData("""[assembly: RivetSecurityScheme("sso", "saml")]""", "unsupported type 'saml'")]
    [InlineData(
        """[assembly: RivetSecurityScheme("bearer", "http", scheme: "bearer")] [assembly: RivetSecurityScheme("bearer", "http", scheme: "basic")]""",
        "duplicate Rivet security scheme metadata for 'bearer'"
    )]
    [InlineData(
        """[assembly: RivetSecurityScheme("oauth", "oauth2")] [assembly: RivetOAuthFlow("oauth", "deviceCode", null, "https://t", null, new string[0], new string[0])]""",
        "unsupported flow 'deviceCode'"
    )]
    // An attribute of ours the walker can't read used to be skipped, so its scheme, flow, or
    // requirement left the spec without a word.
    [InlineData(
        """[assembly: RivetSecurityScheme(null!, "http", scheme: "bearer")]""",
        "[RivetSecurityScheme] at "
    )]
    [InlineData(
        """[assembly: RivetSecurityScheme("oauth", "oauth2")] [assembly: RivetOAuthFlow("oauth", null!, null, "https://t", null, new string[0], new string[0])]""",
        "[RivetOAuthFlow] at "
    )]
    [InlineData(
        """[assembly: RivetSecurityScheme("bearer", "http", scheme: "bearer")] [assembly: RivetGlobalSecurity(0)] [assembly: RivetGlobalSecurityScheme(0, null!, new string[0])]""",
        "[RivetGlobalSecurityScheme] at "
    )]
    public void Invalid_Security_Scheme_Metadata_Fails_With_RIV1121(
        string attributes,
        string expected
    )
    {
        var compilation = CompilationHelper.CreateCompilation($"using Rivet;\n\n{attributes}\n");

        var exception = Assert.Throws<RivetUserException>(() =>
            Rivet.Tool.Analysis.SecurityMetadataWalker.Walk(compilation)
        );

        Assert.StartsWith("error RIV1121: ", exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    // Rivet reads provenance attributes by name, so a project built against another
    // Rivet.Attributes version can carry one whose constructor takes different arguments:
    // RIV1122 names the version mismatch, where an index used to crash. Re-importing alone
    // can't cure it, since the tool writes its own arity.
    [Theory]
    [InlineData(
        "RivetDocumentTag(int order, string name)",
        "RivetDocumentTag(0, \"a\")",
        "Rivet document tag metadata has 2 arguments where this rivet tool reads 5"
    )]
    [InlineData(
        "RivetDocumentExternalDocs(string url)",
        "RivetDocumentExternalDocs(\"https://x\")",
        "Rivet document external-docs metadata has 1 arguments where this rivet tool reads 2"
    )]
    [InlineData(
        "RivetDocumentServer(int order, string url)",
        "RivetDocumentServer(0, \"https://x\")",
        "Rivet server metadata has 2 arguments where this rivet tool reads 3"
    )]
    [InlineData(
        "RivetDocumentServerVariable(int serverOrder, string name)",
        "RivetDocumentServerVariable(0, \"v\")",
        "Rivet server variable metadata has 2 arguments where this rivet tool reads 6"
    )]
    [InlineData(
        "RivetDocumentSchema(string name, string json)",
        "RivetDocumentSchema(\"Thing\", \"{}\")",
        "Rivet document component schema metadata has 2 arguments where this rivet tool reads 3"
    )]
    [InlineData(
        "RivetDocumentExample(int order, string name)",
        "RivetDocumentExample(0, \"Sample\")",
        "Rivet document component-example metadata has 2 arguments where this rivet tool reads 6"
    )]
    public void Provenance_Attribute_From_Another_Version_Fails_With_RIV1122(
        string constructor,
        string usage,
        string expected
    )
    {
        var name = constructor[..constructor.IndexOf('(')];
        var source = $$"""
            using System;

            [assembly: Rivet.RivetDocumentInfo("T", "1")]
            [assembly: Rivet.{{usage}}]

            namespace Rivet
            {
                public sealed class RivetDocumentInfoAttribute(
                    string title, string version, string? description = null,
                    string? termsOfService = null, string? contactName = null,
                    string? contactUrl = null, string? contactEmail = null,
                    bool contactPresent = false, string? licenseName = null,
                    string? licenseUrl = null, string? licenseIdentifier = null) : Attribute;

                [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class {{name}}Attribute : Attribute
                {
                    public {{constructor.Replace(name, name + "Attribute")}} { }
                }
            }
            """;
        // The project's own Rivet.Attributes stands in for the one Rivet ships with.
        var baseline = CompilationHelper.CreateCompilation("namespace Placeholder;");
        var shipped = baseline.References.Single(reference =>
            reference.Display?.Contains("Rivet.Attributes", StringComparison.Ordinal) == true
        );
        var compilation = baseline
            .RemoveReferences(shipped)
            .RemoveAllSyntaxTrees()
            .AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))
            );
        Assert.Empty(
            compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
        );
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var exception = Assert.Throws<RivetUserException>(() =>
            Rivet.Tool.Analysis.OpenApiProvenanceWalker.Walk(compilation, walker)
        );

        Assert.StartsWith($"error RIV1122: {expected}", exception.Message);
        Assert.Contains("different versions", exception.Message);
    }

    // Import preserves a response's Content-Type header under the operation response's own
    // headers, or as its whole headers; a "headers" field on any other owner is malformed.
    [Theory]
    [InlineData("#/paths/~1items/get/responses/200", "headers", true)]
    [InlineData("#/paths/~1items/get/responses/200/headers", "Content-Type", true)]
    [InlineData("#/paths/~1items/get/responses/200/headers", "X-Other", false)]
    [InlineData(
        "#/paths/~1items/get/responses/200/content/multipart~1form-data/encoding/file",
        "headers",
        false
    )]
    [InlineData(
        "#/paths/~1items/get/requestBody/content/multipart~1form-data/encoding/file",
        "headers",
        false
    )]
    [InlineData("#/components/responses/Thing", "headers", false)]
    public void Preserved_Headers_Are_Accepted_Only_On_An_Operation_Response(
        string owner,
        string name,
        bool accepted
    )
    {
        var compilation = CompilationHelper.CreateCompilation(
            $$"""
            using Rivet;

            [assembly: RivetDocumentInfo("T", "1")]
            [assembly: RivetPreservedField("{{owner}}", "{{name}}", "{}")]
            """
        );
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var walk = () => Rivet.Tool.Analysis.OpenApiProvenanceWalker.Walk(compilation, walker);

        if (accepted)
        {
            Assert.NotNull(walk());
        }
        else
        {
            var exception = Assert.Throws<RivetUserException>(walk);
            Assert.StartsWith(
                $"error RIV1122: Invalid Rivet preserved-field name '{name}'.",
                exception.Message
            );
        }
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
    public void Error_Builds_The_Canonical_Message()
    {
        var exception = Diagnostics.Error(Diagnostics.UnsupportedBigInteger, "test message");

        Assert.Equal("error RIV1010: test message", exception.Message);
    }

    [Fact]
    public void Format_Takes_Its_Word_From_The_Registry()
    {
        Assert.Equal(
            "warning RIV4001: test message",
            Diagnostics.Format(Diagnostics.CoverageMissingImplementation, "test message")
        );
        Assert.Equal(
            "error RIV4006: test message",
            Diagnostics.Format(Diagnostics.CoverageInputBindingMismatch, "test message")
        );
    }

    [Fact]
    public void Warn_Error_And_Prefix_Refuse_The_Other_Severity()
    {
        // An error printed as a warning lets a run carry on that should have stopped, and a
        // warning thrown as an error stops one that shouldn't, so a mismatch is a Rivet bug.
        var stderr = CompilationHelper.CaptureStdErr(() =>
            Assert.Throws<InvalidOperationException>(() =>
                Diagnostics.Warn(Diagnostics.UnsupportedBigInteger, "test message")
            )
        );
        Assert.Empty(stderr);
        Assert.Throws<InvalidOperationException>(() =>
            Diagnostics.Prefix(Diagnostics.UnsupportedBigInteger, "test message")
        );
        Assert.Throws<InvalidOperationException>(() =>
            Diagnostics.Error(Diagnostics.DictionaryKeyTypeDropped, "test message")
        );
        Assert.Throws<InvalidOperationException>(() =>
            Diagnostics.Format("RIV9999", "test message")
        );
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

    // Emits the whole pipeline, so a type that loses its mapping fails with RIV2005.
    private static JsonDocument EmitHolder(string propertyType) =>
        CompilationHelper.EmitOpenApi(
            $$"""
            using System;
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record HolderDto({{propertyType}} Value);

            [RivetContract]
            public static class HolderContract
            {
                public static readonly RouteDefinition<HolderDto> Get = Define.Get<HolderDto>("/api/holder");
            }
            """
        );

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

        using var emitted = CompilationHelper.EmitOpenApi(source);
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
    // RIV1012 are retired (tombstoned in Diagnostics.cs and diagnostics.md). Each emits
    // its mapping; object is any JSON, the empty schema.
    [Theory]
    [InlineData("char", "string")]
    [InlineData("Guid", "string")]
    [InlineData("object", null)]
    public void Supported_Scalars_Emit_Their_Mapping(string propertyType, string? jsonType)
    {
        using var emitted = EmitHolder(propertyType);
        var value = emitted
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("HolderDto")
            .GetProperty("properties")
            .GetProperty("value");

        Assert.Equal(
            jsonType,
            value.TryGetProperty("type", out var type) ? type.GetString() : null
        );
    }

    // STJ writes JsonValue and JsonDocument as arbitrary JSON, like JsonNode and
    // JsonElement, so they share the "any JSON" mapping.
    [Theory]
    [InlineData("System.Text.Json.Nodes.JsonValue", "JsonValue")]
    [InlineData("System.Text.Json.JsonDocument", "JsonDocument")]
    public void JsonValue_And_JsonDocument_Map_To_Any_Json(string propertyType, string csharpType)
    {
        var compilation = CompilationHelper.CreateCompilation(
            $$"""
            using Rivet;

            namespace Test;

            [RivetType]
            public sealed record HolderDto({{propertyType}} Value);
            """
        );
        var (_, walker) = CompilationHelper.DiscoverAndWalk(compilation);

        var value = Assert.Single(walker.Definitions["HolderDto"].Properties);
        Assert.Equal(
            new Rivet.Tool.Model.TsType.Primitive("unknown", CSharpType: csharpType),
            value.Type
        );
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
                public static readonly InputRouteDefinition<UpdateInput> Update =
                    Define.Put("/api/items/{id}").Accepts<UpdateInput>();
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
                public static readonly InputRouteDefinition<UpdateInput> Update =
                    Define.Put("/api/items/{body}").Accepts<UpdateInput>();
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
                public static readonly InputRouteDefinition<UpdateInput> Update =
                    Define.Put("/api/items/{id}").Accepts<UpdateInput>();
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
                public static readonly InputRouteDefinition<UpdateInput> Update =
                    Define.Put("/api/items/{id}").Accepts<UpdateInput>();
            }
            """;

        var exception = Assert.Throws<RivetUserException>(() =>
            CompilationHelper.WalkContract(source)
        );

        Assert.Contains("error RIV1022:", exception.Message);
    }
}
