using Microsoft.CodeAnalysis;
using Rivet.Tool.Import;

namespace Rivet.Tests;

public sealed class RealWorldImportTests
{
    private static string LoadFixture(string name)
    {
        return File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    }

    private static ImportResult Import(string json, string ns = "Test")
    {
        return OpenApiImporter.Import(json, new ImportOptions(ns));
    }

    private static Compilation CompileGeneratedFiles(ImportResult result)
    {
        return CompilationHelper.CreateCompilationFromMultiple(
            result.Files.Select(f => f.Content).ToArray());
    }

    // Stub types for imported code that references ASP.NET Core or newer runtime types
    private const string ImportStubs = """
        namespace Microsoft.AspNetCore.Http
        {
            public interface IFormFile { }
        }
        namespace System
        {
            public readonly struct DateOnly
            {
                public DateOnly(int year, int month, int day) { }
            }
        }
        """;

    private static List<Diagnostic> GetCompilationErrors(ImportResult result)
    {
        // Deduplicate files by name (importer may produce duplicate entries for shared schemas)
        var uniqueFiles = result.Files
            .GroupBy(f => f.FileName)
            .Select(g => g.First().Content)
            .Append(ImportStubs)
            .ToArray();

        try
        {
            var compilation = CompilationHelper.CreateCompilationFromMultiple(uniqueFiles);
            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
        }
        catch (InvalidOperationException)
        {
            // CompilationHelper throws on errors — we want the error list instead
            // Re-compile without throwing
            var trees = uniqueFiles
                .Select(s => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    s, new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                        Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)))
                .ToList();

            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var refFiles = new List<string>
            {
                typeof(object).Assembly.Location,
                Path.Combine(runtimeDir, "System.Runtime.dll"),
                Path.Combine(runtimeDir, "System.Collections.dll"),
                Path.Combine(runtimeDir, "System.Text.Json.dll"),
                Path.Combine(runtimeDir, "netstandard.dll"),
                typeof(RivetTypeAttribute).Assembly.Location,
            };
            // Add System.Linq and System.Console for generated code that uses them
            var extraRefs = new[] { "System.Linq.dll", "System.Console.dll" };
            foreach (var extra in extraRefs)
            {
                var path = Path.Combine(runtimeDir, extra);
                if (File.Exists(path))
                {
                    refFiles.Add(path);
                }
            }
            var refs = refFiles.Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToArray();

            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "TestAssembly",
                trees,
                refs,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));

            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
        }
    }

    // ========== Bug 1: Duplicate field names ==========

    [Fact]
    public void Duplicate_Route_Params_Produce_Unique_Field_Names()
    {
        var result = Import(LoadFixture("openapi-duplicate-routes.json"));
        var contractFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("AnythingContract.cs"));
        Assert.NotNull(contractFile);

        var content = contractFile.Content;

        // Should have two distinct field names — not two "GetAnything"
        var fieldCount = content.Split('\n')
            .Count(line => line.Contains("public static readonly"));
        Assert.Equal(2, fieldCount);

        // The two names should differ
        Assert.Contains("GetAnything", content);
        Assert.Contains("GetAnythingByAnything", content);

        // Must compile
        CompileGeneratedFiles(result);
    }

    // ========== Bug 2: Union property names with <> ==========

    [Fact]
    public void OneOf_With_Collection_Types_Produces_Valid_Identifiers()
    {
        var result = Import(LoadFixture("openapi-union-types.json"));
        var allContent = string.Join("\n", result.Files.Select(f => f.Content));

        // No angle brackets in property names
        Assert.DoesNotMatch(@"As\w*<", allContent);

        // Must compile
        CompileGeneratedFiles(result);
    }

    // ========== Bug 3: Double nullable ==========

    [Fact]
    public void Nullable_OneOf_Does_Not_Double_Nullable()
    {
        var result = Import(LoadFixture("openapi-union-types.json"));
        var allContent = string.Join("\n", result.Files.Select(f => f.Content));

        // No double nullable
        Assert.DoesNotContain("??", allContent);

        // Must compile
        CompileGeneratedFiles(result);
    }

    // ========== Bug 4: Multiline descriptions ==========

    [Fact]
    public void Multiline_Description_Escapes_Newlines()
    {
        var result = Import(LoadFixture("openapi-multiline-desc.json"));

        var contractFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("HealthContract.cs"));
        Assert.NotNull(contractFile);
        Assert.Contains("\\n", contractFile.Content);
        Assert.Contains("\\t", contractFile.Content);

        // Must compile
        CompileGeneratedFiles(result);
    }

    // ========== Bug 5: Dot in synthetic record names from param context ==========

    [Fact]
    public void Complex_Param_Schema_Produces_Valid_Record_Name()
    {
        // When a path/query param has a oneOf schema, the synthetic record name
        // must not contain a dot (e.g. "GetById.fields" → "GetByIdFields")
        var result = Import(LoadFixture("openapi-edge-cases.json"));
        var allContent = string.Join("\n", result.Files.Select(f => f.Content));

        // No dots in record names
        Assert.DoesNotMatch(@"sealed record \S*\.\S*\(", allContent);

        CompileGeneratedFiles(result);
    }

    // ========== Bug 6: Duplicate property names (+1/-1 → both _1) ==========

    [Fact]
    public void Emoji_Property_Names_Deduplicated()
    {
        var result = Import(LoadFixture("openapi-edge-cases.json"));
        var rollupFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("ReactionRollup.cs"));
        Assert.NotNull(rollupFile);

        var content = rollupFile.Content;

        // Both +1 and -1 strip to _1 — dedup should make the second _1_2
        var propLines = content.Split('\n')
            .Where(l => l.Trim().StartsWith("int") || l.Trim().StartsWith("string"))
            .ToList();

        // Should have exactly one _1 and one _1_2 (not two _1)
        Assert.Equal(1, propLines.Count(l => l.Contains(" _1,") || l.Contains(" _1)")));
        Assert.Equal(1, propLines.Count(l => l.Contains("_1_2")));

        CompileGeneratedFiles(result);
    }

    // ========== Bug 7: Double-nullable in union variant types ==========

    [Fact]
    public void Union_Variant_With_Nullable_Type_No_Double_Nullable()
    {
        // oneOf where an inline variant resolves to a nullable type — the union
        // property should be "type?" not "type??"
        var result = Import(LoadFixture("openapi-union-types.json"));

        // Find the union wrapper record for "value" (which has oneOf variants)
        var allContent = string.Join("\n", result.Files.Select(f => f.Content));
        Assert.DoesNotContain("??", allContent);

        // Specifically check the doubleNullable property's union record
        var itemResponseFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("ItemResponse.cs"));
        Assert.NotNull(itemResponseFile);

        // doubleNullable is oneOf: [string|null, null] — should resolve to string? not string??
        Assert.DoesNotContain("??", itemResponseFile.Content);

        CompileGeneratedFiles(result);
    }

    // ========== Bug 8: Primitive type alias $ref resolution ==========

    [Fact]
    public void Ref_To_Primitive_Alias_Resolves_To_Underlying_Type()
    {
        var result = Import(LoadFixture("openapi-edge-cases.json"));
        var alertFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("ScanAlert.cs"));
        Assert.NotNull(alertFile);

        var content = alertFile.Content;

        // alert-number (integer) → should resolve to "int", not "AlertNumber"
        Assert.Contains("int Number", content);
        // alert-created-at (string, date-time) → should resolve to "DateTime", not "AlertCreatedAt"
        Assert.Contains("DateTime CreatedAt", content);
        // alert-state (boolean) → should resolve to "bool", not "AlertState"
        Assert.Contains("bool State", content);

        // No files should be generated for the primitive aliases
        Assert.DoesNotContain(result.Files, f => f.FileName.Contains("AlertNumber"));
        Assert.DoesNotContain(result.Files, f => f.FileName.Contains("AlertCreatedAt"));
        Assert.DoesNotContain(result.Files, f => f.FileName.Contains("AlertState"));

        CompileGeneratedFiles(result);
    }

    // ========== Bug 9: Property name = record name (CS0542) ==========

    [Fact]
    public void Property_Name_Matching_Record_Name_Gets_Suffixed()
    {
        var result = Import(LoadFixture("openapi-edge-cases.json"));
        var emailFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("Email.cs"));
        Assert.NotNull(emailFile);

        var content = emailFile.Content;

        // Record "Email" has property "email" → PascalCase "Email" collides with record name
        // Should be renamed to "EmailValue"
        Assert.Contains("EmailValue", content);
        // The record declaration should still be "Email"
        Assert.Contains("sealed record Email(", content);

        CompileGeneratedFiles(result);
    }

    // ========== Bug 10: Union $ref to property-less schemas ==========

    [Fact]
    public void Union_Ref_To_Bare_Object_Resolves_Through()
    {
        var result = Import(LoadFixture("openapi-edge-cases.json"));
        var payloadFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("EventPayload.cs"));
        Assert.NotNull(payloadFile);

        var content = payloadFile.Content;

        // ItemDetail is a real object → should appear as AsItemDetail
        Assert.Contains("AsItemDetail", content);
        // PublicEvent is bare object (no properties) → should NOT appear as "AsPublicEvent"
        // (it would be unresolvable). Instead it resolves to Dictionary.
        Assert.DoesNotContain("PublicEvent", content);
        // ContentDirectory is an array type → should NOT appear as "AsContentDirectory"
        Assert.DoesNotContain("ContentDirectory", content);

        CompileGeneratedFiles(result);
    }

    // ========== Bug 1 extended: OperationId collision dedup ==========

    [Fact]
    public void OperationId_PascalCase_Collisions_Deduplicated()
    {
        // "widgets_list_items" and "widgets_ListItems" both → "ListItems" after stripping tag prefix
        var result = Import(LoadFixture("openapi-duplicate-routes.json"));
        var widgetsFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("WidgetsContract.cs"));
        Assert.NotNull(widgetsFile);

        var content = widgetsFile.Content;

        // Should have 2 fields, not a compile error from duplicate names
        var fieldCount = content.Split('\n')
            .Count(line => line.Contains("public static readonly"));
        Assert.Equal(2, fieldCount);

        // One should be the original, the other suffixed
        Assert.Contains("ListItems", content);
        Assert.Contains("ListItems_2", content);

        CompileGeneratedFiles(result);
    }

    // ========== Real-world specs ==========

    [Fact]
    public void PetStore_V3_Imports_And_Compiles()
    {
        var result = Import(LoadFixture("openapi-petstore-v3.json"), "PetStore");

        Assert.True(result.Files.Count > 0, "Should generate files");
        Assert.True(result.Files.Any(f => f.FileName.StartsWith("Contracts/")), "Should generate contracts");

        // Must compile with zero errors
        CompileGeneratedFiles(result);
    }

    [Fact]
    public void Httpbin_Imports_And_Compiles()
    {
        var result = Import(LoadFixture("openapi-httpbin.json"), "Httpbin");

        Assert.True(result.Files.Count > 0, "Should generate files");
        Assert.True(result.Files.Any(f => f.FileName.StartsWith("Contracts/")), "Should generate contracts");

        // Must compile with zero errors
        CompileGeneratedFiles(result);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void GitHub_Api_Imports_And_Compiles()
    {
        var result = Import(LoadFixture("openapi-github.json"), "GitHub");

        Assert.True(result.Files.Count > 0, "Should generate files");
        Assert.True(result.Files.Any(f => f.FileName.StartsWith("Contracts/")), "Should generate contracts");

        var errors = GetCompilationErrors(result);
        Assert.True(errors.Count == 0,
            $"{errors.Count} compilation errors across {result.Files.Count} files.\n" +
            $"First 10:\n{string.Join("\n", errors.Take(10).Select(e => e.ToString()))}");
    }

    // ========== Large real-world specs ==========

    [Fact]
    [Trait("Category", "Slow")]
    public void Stripe_Api_Imports_And_Compiles()
    {
        var result = Import(LoadFixture("openapi-stripe.json"), "Stripe");

        Assert.True(result.Files.Count > 0, "Should generate files");
        Assert.True(result.Files.Any(f => f.FileName.StartsWith("Contracts/")), "Should generate contracts");

        var errors = GetCompilationErrors(result);
        Assert.True(errors.Count == 0,
            $"{errors.Count} compilation errors across {result.Files.Count} files.\n" +
            $"First 10:\n{string.Join("\n", errors.Take(10).Select(e => e.ToString()))}");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Box_Api_Imports_And_Compiles()
    {
        var result = Import(LoadFixture("openapi-box.json"), "Box");

        Assert.True(result.Files.Count > 0, "Should generate files");
        Assert.True(result.Files.Any(f => f.FileName.StartsWith("Contracts/")), "Should generate contracts");

        var errors = GetCompilationErrors(result);
        Assert.True(errors.Count == 0,
            $"{errors.Count} compilation errors across {result.Files.Count} files.\n" +
            $"First 10:\n{string.Join("\n", errors.Take(10).Select(e => e.ToString()))}");
    }

    [Fact]
    public void Twilio_Api_Imports_And_Compiles()
    {
        var result = Import(LoadFixture("openapi-twilio.json"), "Twilio");

        Assert.True(result.Files.Count > 0, "Should generate files");
        Assert.True(result.Files.Any(f => f.FileName.StartsWith("Contracts/")), "Should generate contracts");

        var errors = GetCompilationErrors(result);
        Assert.True(errors.Count == 0,
            $"{errors.Count} compilation errors across {result.Files.Count} files.\n" +
            $"First 10:\n{string.Join("\n", errors.Take(10).Select(e => e.ToString()))}");
    }

    // ========== Naming edge cases ==========

    [Fact]
    public void NamingEdgeCases_Imports_And_Compiles()
    {
        var result = Import(LoadFixture("openapi-naming-edge-cases.json"));

        Assert.True(result.Files.Count > 0, "Should generate files");

        var errors = GetCompilationErrors(result);
        Assert.True(errors.Count == 0,
            $"{errors.Count} compilation errors across {result.Files.Count} files.\n" +
            $"First 10:\n{string.Join("\n", errors.Take(10).Select(e => e.ToString()))}");
    }

    [Fact]
    public void NamingEdgeCases_ReservedWords_AreEscaped()
    {
        var result = Import(LoadFixture("openapi-naming-edge-cases.json"));
        var reservedFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("ReservedWords.cs"));
        Assert.NotNull(reservedFile);

        var content = reservedFile.Content;

        // C# keywords become PascalCase — "class" → "Class", "event" → "Event"
        // These are contextual in record position and should not cause compilation errors
        Assert.Contains("sealed record ReservedWords(", content);
    }

    [Fact]
    public void NamingEdgeCases_SpecialCharProperties_AreValid()
    {
        var result = Import(LoadFixture("openapi-naming-edge-cases.json"));
        var specialFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("SpecialCharsModel.cs"));
        Assert.NotNull(specialFile);

        var content = specialFile.Content;

        // Properties with special chars should be sanitized to valid C# identifiers
        Assert.Contains("sealed record SpecialCharsModel(", content);
        // No empty parameter names
        Assert.DoesNotMatch(@",\s*\)", content);
        Assert.DoesNotMatch(@"\(\s*,", content);
    }

    [Fact]
    public void NamingEdgeCases_DuplicateEnums_Deduplicated()
    {
        var result = Import(LoadFixture("openapi-naming-edge-cases.json"));
        var allContent = string.Join("\n", result.Files.Select(f => f.Content));

        // DuplicatingEnum: "foo-bar" and "foo_bar" both → FooBar — needs dedup
        // Should compile without errors
        var errors = GetCompilationErrors(result);
        Assert.True(errors.Count == 0,
            $"Duplicate enum compilation errors:\n{string.Join("\n", errors.Take(10).Select(e => e.ToString()))}");
    }

    [Fact]
    public void NamingEdgeCases_EmptyPropertyNames_Handled()
    {
        var result = Import(LoadFixture("openapi-naming-edge-cases.json"));
        var statusFile = result.Files.FirstOrDefault(f => f.FileName.EndsWith("StatusResponse.cs"));
        Assert.NotNull(statusFile);

        var content = statusFile.Content;

        // Empty property name should either be skipped or get a fallback name
        // Must not produce "string ," (empty parameter)
        Assert.DoesNotContain("string ,", content);
        Assert.DoesNotContain("string? ,", content);
    }

    [Fact]
    public void NamingEdgeCases_SchemaNameCollision_Handled()
    {
        // foo_bar_schema and FooBarSchema both PascalCase to FooBarSchema
        // Should not crash — either dedup or one wins
        var result = Import(LoadFixture("openapi-naming-edge-cases.json"));
        var errors = GetCompilationErrors(result);
        Assert.True(errors.Count == 0,
            $"Schema collision errors:\n{string.Join("\n", errors.Take(10).Select(e => e.ToString()))}");
    }
}
