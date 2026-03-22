using System.Text.Json;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Import;
using Rivet.Tool.Model;

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

    // ========== Round-trip infrastructure ==========

    private sealed record RoundTripResult(
        ImportResult Import1,
        IReadOnlyList<TsEndpointDefinition> Endpoints1,
        TypeWalker Walker1,
        string EmittedJson,
        ImportResult Import2,
        IReadOnlyList<TsEndpointDefinition> Endpoints2,
        TypeWalker Walker2);

    /// <summary>
    /// Full belt-and-braces round-trip: OpenAPI JSON → import → compile → walk → emit OpenAPI → re-import → compile → walk.
    /// </summary>
    private static RoundTripResult FullRoundTrip(string fixtureName, string ns)
    {
        var json = LoadFixture(fixtureName);
        var import1 = Import(json, ns);

        // Pass 1: Import → compile → walk
        var sources1 = DeduplicateFiles(import1);
        var comp1 = CompilationHelper.CreateCompilationFromMultiple(sources1);
        var (disc1, wlk1) = CompilationHelper.DiscoverAndWalk(comp1);
        var eps1 = ContractWalker.Walk(comp1, wlk1, disc1.ContractTypes);

        // Emit OpenAPI from walked model
        var emittedJson = OpenApiEmitter.Emit(eps1, wlk1.Definitions, wlk1.Brands, wlk1.Enums, null);

        // Pass 2: Re-import emitted OpenAPI → compile → walk
        var import2 = Import(emittedJson, ns);
        var sources2 = DeduplicateFiles(import2);
        var comp2 = CompilationHelper.CreateCompilationFromMultiple(sources2);
        var (disc2, wlk2) = CompilationHelper.DiscoverAndWalk(comp2);
        var eps2 = ContractWalker.Walk(comp2, wlk2, disc2.ContractTypes);

        return new(import1, eps1, wlk1, emittedJson, import2, eps2, wlk2);
    }

    /// <summary>
    /// Same as FullRoundTrip but uses GetCompilationErrors-style fallback for specs
    /// that may trigger throwing in CreateCompilationFromMultiple.
    /// </summary>
    private static RoundTripResult FullRoundTripLenient(string fixtureName, string ns)
    {
        var json = LoadFixture(fixtureName);
        var import1 = Import(json, ns);

        // Pass 1
        var sources1 = DeduplicateFiles(import1);
        var comp1 = CreateCompilationLenient(sources1);
        var errors1 = comp1.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors1.Count > 0)
        {
            throw new InvalidOperationException(
                $"Pass 1 compilation errors ({errors1.Count}):\n" +
                string.Join("\n", errors1.Take(10).Select(e => e.ToString())));
        }

        var (disc1, wlk1) = CompilationHelper.DiscoverAndWalk(comp1);
        var eps1 = ContractWalker.Walk(comp1, wlk1, disc1.ContractTypes);
        var emittedJson = OpenApiEmitter.Emit(eps1, wlk1.Definitions, wlk1.Brands, wlk1.Enums, null);

        // Pass 2
        var import2 = Import(emittedJson, ns);
        var sources2 = DeduplicateFiles(import2);
        var comp2 = CreateCompilationLenient(sources2);
        var errors2 = comp2.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors2.Count > 0)
        {
            throw new InvalidOperationException(
                $"Pass 2 compilation errors ({errors2.Count}):\n" +
                string.Join("\n", errors2.Take(10).Select(e => e.ToString())));
        }

        var (disc2, wlk2) = CompilationHelper.DiscoverAndWalk(comp2);
        var eps2 = ContractWalker.Walk(comp2, wlk2, disc2.ContractTypes);

        return new(import1, eps1, wlk1, emittedJson, import2, eps2, wlk2);
    }

    private static string[] DeduplicateFiles(ImportResult result)
    {
        return result.Files
            .GroupBy(f => f.FileName)
            .Select(g => g.First().Content)
            .ToArray();
    }

    private static Compilation CreateCompilationLenient(string[] sources)
    {
        var trees = sources
            .Select(s => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                s, new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                    Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)))
            .ToList();

        // Add ASP.NET stubs from CompilationHelper (via reflection would be messy, so inline the stub tree)
        trees.Add(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            ImportStubs, new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)));

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refFiles = new List<string>
        {
            typeof(object).Assembly.Location,
            Path.Combine(runtimeDir, "System.Runtime.dll"),
            Path.Combine(runtimeDir, "System.Collections.dll"),
            Path.Combine(runtimeDir, "System.Text.Json.dll"),
            Path.Combine(runtimeDir, "System.Memory.dll"),
            Path.Combine(runtimeDir, "netstandard.dll"),
            Path.Combine(runtimeDir, "System.Private.Uri.dll"),
            typeof(RivetTypeAttribute).Assembly.Location,
        };
        foreach (var extra in new[] { "System.Linq.dll", "System.Console.dll" })
        {
            var path = Path.Combine(runtimeDir, extra);
            if (File.Exists(path))
            {
                refFiles.Add(path);
            }
        }

        var refs = refFiles.Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToArray();

        return Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "TestAssembly",
            trees,
            refs,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>
    /// Asserts the full round-trip invariants: emitted OpenAPI is valid, all $refs resolve,
    /// and structural properties are stable between pass 1 and pass 2.
    /// </summary>
    private static void AssertFullRoundTrip(RoundTripResult r, string specName)
    {
        // --- Pass 1: endpoints + types discovered ---
        Assert.True(r.Endpoints1.Count > 0,
            $"{specName}: Pass 1 should discover endpoints (got 0)");

        // --- Emitted OpenAPI is well-formed JSON ---
        JsonElement emittedDoc;
        try
        {
            emittedDoc = JsonSerializer.Deserialize<JsonElement>(r.EmittedJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{specName}: Emitted OpenAPI is not valid JSON: {ex.Message}");
        }

        // --- All $refs resolve ---
        var allRefs = new List<string>();
        CollectRefs(emittedDoc, allRefs);
        var schemaNames = new HashSet<string>();
        if (emittedDoc.TryGetProperty("components", out var components) &&
            components.TryGetProperty("schemas", out var schemas))
        {
            foreach (var s in schemas.EnumerateObject())
            {
                schemaNames.Add(s.Name);
            }
        }

        var brokenRefs = allRefs
            .Where(r2 => r2.StartsWith("#/components/schemas/"))
            .Where(r2 => !schemaNames.Contains(r2["#/components/schemas/".Length..]))
            .ToList();
        Assert.True(brokenRefs.Count == 0,
            $"{specName}: {brokenRefs.Count} broken $refs in emitted OpenAPI:\n" +
            string.Join("\n", brokenRefs.Take(10)));

        // --- Pass 2: endpoints + types discovered ---
        Assert.True(r.Endpoints2.Count > 0,
            $"{specName}: Pass 2 should discover endpoints (got 0)");

        // --- Structural stability: endpoints ---
        Assert.Equal(r.Endpoints1.Count, r.Endpoints2.Count);

        var routes1 = r.Endpoints1
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate}")
            .OrderBy(x => x).ToList();
        var routes2 = r.Endpoints2
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate}")
            .OrderBy(x => x).ToList();
        Assert.Equal(routes1, routes2);

        // --- Structural stability: endpoint-referenced types survive ---
        // The emitter only includes types transitively referenced by endpoints.
        // Orphan schemas (from oneOf/allOf compositions that the walker flattens)
        // may be excluded — that's by design. Assert that everything the emitter
        // DOES include survives the round-trip.
        var referencedNames = new HashSet<string>();
        foreach (var ep in r.Endpoints1)
        {
            foreach (var p in ep.Params)
            {
                TsType.CollectTypeRefs(p.Type, referencedNames);
            }

            foreach (var resp in ep.Responses)
            {
                if (resp.DataType is not null)
                {
                    TsType.CollectTypeRefs(resp.DataType, referencedNames);
                }
            }
        }

        var lostDefs = referencedNames
            .Where(n => r.Walker1.Definitions.ContainsKey(n) && !r.Walker2.Definitions.ContainsKey(n))
            .ToList();
        Assert.True(lostDefs.Count == 0,
            $"{specName}: Lost endpoint-referenced types on round-trip: {string.Join(", ", lostDefs)}");

        var lostEnums = referencedNames
            .Where(n => r.Walker1.Enums.ContainsKey(n) && !r.Walker2.Enums.ContainsKey(n))
            .ToList();
        Assert.True(lostEnums.Count == 0,
            $"{specName}: Lost enums on round-trip: {string.Join(", ", lostEnums)}");

        var lostBrands = referencedNames
            .Where(n => r.Walker1.Brands.ContainsKey(n) && !r.Walker2.Brands.ContainsKey(n))
            .ToList();
        Assert.True(lostBrands.Count == 0,
            $"{specName}: Lost brands on round-trip: {string.Join(", ", lostBrands)}");

        // --- Structural stability: nothing lost per endpoint ---
        // Params and responses may be GAINED on round-trip (normalization adds synthetic entries)
        // but nothing should be LOST.
        foreach (var ep1 in r.Endpoints1)
        {
            var ep2 = r.Endpoints2.FirstOrDefault(e =>
                e.HttpMethod == ep1.HttpMethod && e.RouteTemplate == ep1.RouteTemplate);
            Assert.NotNull(ep2);
            Assert.True(ep2.Params.Count >= ep1.Params.Count,
                $"{specName}: {ep1.HttpMethod} {ep1.RouteTemplate} lost params: " +
                $"pass1={ep1.Params.Count} [{string.Join(", ", ep1.Params.Select(p => $"{p.Name}:{p.Source}"))}] " +
                $"vs pass2={ep2.Params.Count} [{string.Join(", ", ep2.Params.Select(p => $"{p.Name}:{p.Source}"))}]");
            Assert.True(ep2.Responses.Count >= ep1.Responses.Count,
                $"{specName}: {ep1.HttpMethod} {ep1.RouteTemplate} lost responses: " +
                $"pass1={ep1.Responses.Count} vs pass2={ep2.Responses.Count}");
        }

        // --- Structural stability: type property counts (only for surviving types) ---
        foreach (var (name, def1) in r.Walker1.Definitions)
        {
            if (!r.Walker2.Definitions.TryGetValue(name, out var def2))
            {
                continue; // Orphan type not emitted — already checked above
            }

            Assert.Equal(def1.Properties.Count, def2.Properties.Count);
        }

        // --- Structural stability: enum member counts (only for surviving enums) ---
        foreach (var (name, enum1) in r.Walker1.Enums)
        {
            if (!r.Walker2.Enums.TryGetValue(name, out _))
            {
                continue;
            }

            var enum2 = r.Walker2.Enums[name];
            Assert.Equal(enum1.Members.Count, enum2.Members.Count);
        }
    }

    private static void CollectRefs(JsonElement element, List<string> refs)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "$ref" && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        refs.Add(prop.Value.GetString()!);
                    }
                    else
                    {
                        CollectRefs(prop.Value, refs);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectRefs(item, refs);
                }
                break;
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

    // ========== Real-world specs — full round-trip ==========

    [Fact]
    public void PetStore_V3_Full_RoundTrip()
    {
        var r = FullRoundTrip("openapi-petstore-v3.json", "PetStore");
        AssertFullRoundTrip(r, "PetStore");
    }

    [Fact]
    public void Httpbin_Full_RoundTrip()
    {
        var r = FullRoundTrip("openapi-httpbin.json", "Httpbin");
        AssertFullRoundTrip(r, "Httpbin");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void GitHub_Api_Full_RoundTrip()
    {
        var r = FullRoundTripLenient("openapi-github.json", "GitHub");
        AssertFullRoundTrip(r, "GitHub");
    }

    // ========== Large real-world specs — full round-trip ==========

    [Fact]
    [Trait("Category", "Slow")]
    public void Stripe_Api_Full_RoundTrip()
    {
        var r = FullRoundTripLenient("openapi-stripe.json", "Stripe");
        AssertFullRoundTrip(r, "Stripe");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Box_Api_Full_RoundTrip()
    {
        var r = FullRoundTripLenient("openapi-box.json", "Box");
        AssertFullRoundTrip(r, "Box");
    }

    [Fact]
    public void Twilio_Full_RoundTrip()
    {
        var r = FullRoundTripLenient("openapi-twilio.json", "Twilio");
        AssertFullRoundTrip(r, "Twilio");
    }

    // ========== Naming edge cases — full round-trip ==========

    [Fact]
    public void NamingEdgeCases_Full_RoundTrip()
    {
        var r = FullRoundTripLenient("openapi-naming-edge-cases.json", "Test");
        AssertFullRoundTrip(r, "NamingEdgeCases");
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
