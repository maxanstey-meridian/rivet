using System.Text.Json;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Import;

namespace Rivet.Tests;

/// <summary>
/// Diagnostic tests that measure actual data loss when importing real-world OpenAPI specs.
/// Not structural round-trip checks — these count what the input spec had vs what Rivet captured.
/// </summary>
public sealed class GapAnalysisTests
{
    private static string LoadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Endpoint_Example_Fidelity_Distinguishes_Request_And_Response_Loss()
    {
        var originalDoc = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "openapi": "3.0.3",
              "paths": {
                "/widgets": {
                  "post": {
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": { "type": "object" },
                          "example": { "name": "starter-widget" }
                        }
                      }
                    },
                    "responses": {
                      "201": {
                        "description": "Created",
                        "content": {
                          "application/json": {
                            "schema": { "type": "object" },
                            "example": { "id": "wid_123" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);
        var emittedDoc = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "openapi": "3.0.3",
              "paths": {
                "/widgets": {
                  "post": {
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": { "type": "object" }
                        }
                      }
                    },
                    "responses": {
                      "201": {
                        "description": "Created",
                        "content": {
                          "application/json": {
                            "schema": { "type": "object" }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        var fidelity = AnalyzeEndpointExampleFidelity(originalDoc, emittedDoc);

        Assert.Equal(1, fidelity.RequestExampleLoss);
        Assert.Equal(1, fidelity.ResponseExampleLoss);
        Assert.DoesNotContain(fidelity.Failures, failure => failure.StartsWith("EXAMPLES LOST:", StringComparison.Ordinal));
        Assert.Contains("REQUEST EXAMPLE LOSS: 1", fidelity.Failures);
        Assert.Contains("RESPONSE EXAMPLE LOSS: 1", fidelity.Failures);
    }

    [Fact]
    public void Endpoint_Example_Fidelity_Splits_Named_And_RefBacked_Loss_From_Property_Examples()
    {
        var originalDoc = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "openapi": "3.0.3",
              "paths": {
                "/widgets": {
                  "post": {
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": { "$ref": "#/components/schemas/CreateWidgetRequest" },
                          "examples": {
                            "starter": {
                              "value": { "name": "starter-widget" }
                            }
                          }
                        }
                      }
                    },
                    "responses": {
                      "201": {
                        "description": "Created",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/WidgetResponse" },
                            "examples": {
                              "createdFromTemplate": {
                                "$ref": "#/components/examples/widget-created"
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "CreateWidgetRequest": {
                    "type": "object",
                    "properties": {
                      "name": {
                        "type": "string",
                        "example": "starter-widget"
                      }
                    }
                  },
                  "WidgetResponse": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string" }
                    }
                  }
                },
                "examples": {
                  "widget-created": {
                    "value": { "id": "wid_123" }
                  }
                }
              }
            }
            """);
        var emittedDoc = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "openapi": "3.0.3",
              "paths": {
                "/widgets": {
                  "post": {
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": { "$ref": "#/components/schemas/CreateWidgetRequest" }
                        }
                      }
                    },
                    "responses": {
                      "201": {
                        "description": "Created",
                        "content": {
                          "application/json": {
                            "schema": { "$ref": "#/components/schemas/WidgetResponse" },
                            "examples": {
                              "createdFromTemplate": {
                                "value": { "id": "wid_123" }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "CreateWidgetRequest": {
                    "type": "object",
                    "properties": {
                      "name": {
                        "type": "string",
                        "example": "starter-widget"
                      }
                    }
                  },
                  "WidgetResponse": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string" }
                    }
                  }
                }
              }
            }
            """);

        var fidelity = AnalyzeEndpointExampleFidelity(originalDoc, emittedDoc);

        Assert.Equal(0, CountPropertyExampleLoss(originalDoc, emittedDoc));
        Assert.Equal(1, fidelity.NamedExampleLoss);
        Assert.Equal(1, fidelity.RefBackedExampleLoss);
        Assert.DoesNotContain(fidelity.Failures, failure => failure.StartsWith("EXAMPLES LOST:", StringComparison.Ordinal));
        Assert.Contains("NAMED EXAMPLE LOSS: 1", fidelity.Failures);
        Assert.Contains("REF-BACKED EXAMPLE LOSS: 1", fidelity.Failures);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Full_Gap_Analysis_Report_Includes_Endpoint_Example_Fidelity_Block()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            Report_Full_Gap_Analysis("openapi-github.json", "GitHub");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Assert.Contains("ENDPOINT EXAMPLE FIDELITY:", output);
        Assert.Contains("Request example loss:", output);
        Assert.Contains("Response example loss:", output);
        Assert.Contains("Named example loss:", output);
        Assert.Contains("Ref-backed example loss:", output);
    }

    /// <summary>
    /// Walks the raw OpenAPI JSON and counts inline enums on properties (type: string + enum: [...]),
    /// then checks how many the importer actually captured vs dropped to string.
    /// Breaks down: multi-value (should become enums), single-value (discriminators → string),
    /// and deduplicated (same fingerprint → reused type).
    /// </summary>
    [Theory]
    [InlineData("openapi-stripe.json", "Stripe")]
    [InlineData("openapi-github.json", "GitHub")]
    [InlineData("openapi-box.json", "Box")]
    [InlineData("openapi-twilio.json", "Twilio")]
    [InlineData("openapi-petstore-v3.json", "PetStore")]
    [InlineData("openapi-httpbin.json", "Httpbin")]
    [Trait("Category", "Slow")]
    public void Report_Inline_Enum_Coverage(string fixture, string ns)
    {
        var json = LoadFixture(fixture);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);

        // Count inline enums in raw spec, split by value count
        var multiValueEnums = new List<string>();   // 2+ values → should become enum types
        var singleValueEnums = new List<string>();   // 1 value → discriminators, mapped to string by design
        var uniqueFingerprints = new HashSet<string>(); // deduplicated by value set

        if (doc.TryGetProperty("components", out var components) &&
            components.TryGetProperty("schemas", out var schemas))
        {
            foreach (var schema in schemas.EnumerateObject())
            {
                CountInlineEnumsDetailed(schema.Name, schema.Value,
                    multiValueEnums, singleValueEnums, uniqueFingerprints);
            }
        }

        // Import and check what Rivet produced
        var result = OpenApiImporter.Import(json, new ImportOptions(ns, null));

        // Count generated enum types (both file-level and inline)
        var generatedEnumCount = result.Files.Count(f => f.Content.Contains("public enum "));

        var output = new System.Text.StringBuilder();
        output.AppendLine($"\n=== {ns} Inline Enum Analysis ===");
        output.AppendLine($"  Multi-value inline enums (2+):  {multiValueEnums.Count}");
        output.AppendLine($"  Single-value discriminators (1): {singleValueEnums.Count}");
        output.AppendLine($"  Unique fingerprints (deduped):   {uniqueFingerprints.Count}");
        output.AppendLine($"  Generated enum types:            {generatedEnumCount}");

        var multiValueUnique = multiValueEnums
            .Select(e => e.Split('[')[1].TrimEnd(']'))
            .Distinct()
            .Count();
        output.AppendLine($"  Unique multi-value enum shapes:  {multiValueUnique}");

        if (multiValueEnums.Count > 0)
        {
            output.AppendLine($"\n  First 15 multi-value inline enums:");
            foreach (var e in multiValueEnums.Take(15))
                output.AppendLine($"    {e}");
        }

        Console.WriteLine(output.ToString());
    }

    /// <summary>
    /// Full gap analysis: import → compile → walk → emit OpenAPI → diff against original.
    /// Reports every category of data loss.
    /// </summary>
    [Theory]
    [InlineData("openapi-stripe.json", "Stripe")]
    [InlineData("openapi-github.json", "GitHub")]
    [InlineData("openapi-box.json", "Box")]
    [InlineData("openapi-twilio.json", "Twilio")]
    [InlineData("openapi-petstore-v3.json", "PetStore")]
    [InlineData("openapi-httpbin.json", "Httpbin")]
    [Trait("Category", "Slow")]
    public void Report_Full_Gap_Analysis(string fixture, string ns)
    {
        var json = LoadFixture(fixture);
        var originalDoc = JsonSerializer.Deserialize<JsonElement>(json);

        // Count original spec features
        var originalSchemaCount = 0;
        var originalPathCount = 0;
        var originalOperationCount = 0;
        var originalInlineEnums = new List<string>();
        var originalTopLevelEnums = 0;
        var originalOneOfs = new List<string>();
        var originalAnyOfs = new List<string>();
        var originalAllOfs = new List<string>();

        if (originalDoc.TryGetProperty("components", out var comps) &&
            comps.TryGetProperty("schemas", out var schemas))
        {
            foreach (var schema in schemas.EnumerateObject())
            {
                originalSchemaCount++;
                CountInlineEnums(schema.Name, schema.Value, originalInlineEnums);

                if (HasStringEnum(schema.Value))
                    originalTopLevelEnums++;

                if (schema.Value.TryGetProperty("oneOf", out _))
                    originalOneOfs.Add(schema.Name);
                if (schema.Value.TryGetProperty("anyOf", out _))
                    originalAnyOfs.Add(schema.Name);
                if (schema.Value.TryGetProperty("allOf", out _))
                    originalAllOfs.Add(schema.Name);
            }
        }

        if (originalDoc.TryGetProperty("paths", out var paths))
        {
            foreach (var path in paths.EnumerateObject())
            {
                originalPathCount++;
                foreach (var op in path.Value.EnumerateObject())
                {
                    if (IsHttpMethod(op.Name))
                        originalOperationCount++;
                }
            }
        }

        // Import
        var result = OpenApiImporter.Import(json, new ImportOptions(ns, null));

        // Count what we generated
        var recordFiles = result.Files.Count(f => f.Content.Contains("sealed record "));
        var enumFiles = result.Files.Count(f => f.Content.Contains("public enum "));
        var brandFiles = result.Files.Count(f => f.Content.Contains("[RivetType(Brand"));
        var contractFiles = result.Files.Count(f => f.Content.Contains("[RivetContract]"));
        var endpointFields = result.Files
            .Where(f => f.Content.Contains("[RivetContract]"))
            .Sum(f => f.Content.Split('\n').Count(l => l.Contains("public static readonly")));

        // Check compilation
        var compileErrors = GetCompilationErrorCount(result);
        EndpointExampleFidelity? endpointExampleFidelity = null;
        try
        {
            var sources = result.Files
                .GroupBy(f => f.FileName)
                .Select(g => g.First().Content)
                .ToArray();

            Compilation roundTripCompilation;
            try
            {
                roundTripCompilation = CompilationHelper.CreateCompilationFromMultiple(sources);
            }
            catch
            {
                roundTripCompilation = CreateCompilationLenient(sources);
            }

            var (discovered, walker) = CompilationHelper.DiscoverAndWalk(roundTripCompilation);
            var endpoints = CompilationHelper.WalkContracts(roundTripCompilation, discovered, walker);
            var emittedJson = Rivet.Tool.Emit.OpenApiEmitter.Emit(
                endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
            var emittedDoc = JsonSerializer.Deserialize<JsonElement>(emittedJson);
            endpointExampleFidelity = AnalyzeEndpointExampleFidelity(originalDoc, emittedDoc);
        }
        catch
        {
            endpointExampleFidelity = null;
        }

        // Count unsupported markers in contracts
        var unsupportedLines = result.Files
            .SelectMany(f => f.Content.Split('\n'))
            .Where(l => l.Contains("[rivet:unsupported"))
            .ToList();
        var unsupportedMarkers = unsupportedLines.Count;

        var output = new System.Text.StringBuilder();
        output.AppendLine($"\n{'=',-60}");
        output.AppendLine($"  {ns} — Full Gap Analysis");
        output.AppendLine($"{'=',-60}");
        output.AppendLine();
        output.AppendLine($"  ORIGINAL SPEC:");
        output.AppendLine($"    Schemas:          {originalSchemaCount}");
        output.AppendLine($"    Paths:            {originalPathCount}");
        output.AppendLine($"    Operations:       {originalOperationCount}");
        output.AppendLine($"    Top-level enums:  {originalTopLevelEnums}");
        output.AppendLine($"    Inline enums:     {originalInlineEnums.Count}");
        output.AppendLine($"    oneOf schemas:    {originalOneOfs.Count}");
        output.AppendLine($"    anyOf schemas:    {originalAnyOfs.Count}");
        output.AppendLine($"    allOf schemas:    {originalAllOfs.Count}");
        output.AppendLine();
        output.AppendLine($"  GENERATED:");
        output.AppendLine($"    Total files:      {result.Files.Count}");
        output.AppendLine($"    Records:          {recordFiles}");
        output.AppendLine($"    Enums:            {enumFiles}");
        output.AppendLine($"    Brands:           {brandFiles}");
        output.AppendLine($"    Contracts:        {contractFiles}");
        output.AppendLine($"    Endpoints:        {endpointFields}");
        output.AppendLine($"    Compile errors:   {compileErrors}");
        output.AppendLine($"    Unsupported:      {unsupportedMarkers}");
        output.AppendLine();
        output.AppendLine($"  COVERAGE:");
        output.AppendLine($"    Endpoint coverage: {endpointFields}/{originalOperationCount} ({(originalOperationCount > 0 ? 100.0 * endpointFields / originalOperationCount : 0):F1}%)");
        output.AppendLine($"    Schema coverage:   {recordFiles + enumFiles + brandFiles}/{originalSchemaCount} ({(originalSchemaCount > 0 ? 100.0 * (recordFiles + enumFiles + brandFiles) / originalSchemaCount : 0):F1}%)");
        if (endpointExampleFidelity is not null)
        {
            output.AppendLine();
            output.AppendLine($"  ENDPOINT EXAMPLE FIDELITY:");
            output.AppendLine($"    Request example loss:    {endpointExampleFidelity.RequestExampleLoss}");
            output.AppendLine($"    Response example loss:   {endpointExampleFidelity.ResponseExampleLoss}");
            output.AppendLine($"    Named example loss:      {endpointExampleFidelity.NamedExampleLoss}");
            output.AppendLine($"    Ref-backed example loss: {endpointExampleFidelity.RefBackedExampleLoss}");
        }
        output.AppendLine();
        if (unsupportedLines.Count > 0)
        {
            output.AppendLine($"\n  UNSUPPORTED MARKERS ({unsupportedLines.Count}):");
            var unsupportedGroups = unsupportedLines
                .Select(l => l.Trim())
                .GroupBy(l => l)
                .OrderByDescending(g => g.Count())
                .Take(15);
            foreach (var g in unsupportedGroups)
            {
                output.AppendLine($"    [{g.Count()}x] {g.First()}");
            }
        }

        if (result.Warnings.Count > 0)
        {
            output.AppendLine($"\n  WARNINGS ({result.Warnings.Count}):");
            var warningGroups = result.Warnings
                .GroupBy(w => w.Length > 50 ? w[..50] : w)
                .OrderByDescending(g => g.Count())
                .Take(15);
            foreach (var g in warningGroups)
            {
                output.AppendLine($"    [{g.Count()}x] {g.First()}");
            }
        }

        Console.WriteLine(output.ToString());
    }

    /// <summary>
    /// Find exactly which endpoints have unsupported markers and why.
    /// </summary>
    [Theory]
    [InlineData("openapi-github.json", "GitHub")]
    [InlineData("openapi-box.json", "Box")]
    [Trait("Category", "Slow")]
    public void Report_Unsupported_Endpoint_Details(string fixture, string ns)
    {
        var json = LoadFixture(fixture);
        var result = OpenApiImporter.Import(json, new ImportOptions(ns, null));

        var output = new System.Text.StringBuilder();
        output.AppendLine($"\n=== {ns} Unsupported Endpoint Details ===");

        foreach (var f in result.Files.Where(f => f.Content.Contains("[rivet:unsupported")))
        {
            var lines = f.Content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("[rivet:unsupported"))
                {
                    // Find the next field declaration
                    for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                    {
                        if (lines[j].Contains("public static readonly"))
                        {
                            output.AppendLine($"  {f.FileName}:");
                            output.AppendLine($"    marker: {lines[i].Trim()}");
                            output.AppendLine($"    field:  {lines[j].Trim().Split('=')[0].Trim()}");
                            break;
                        }
                    }
                }
            }
        }

        Console.WriteLine(output.ToString());
    }

    /// <summary>
    /// Comprehensive property-level fidelity test: diffs original OpenAPI spec against round-tripped output.
    /// Collects ALL failures across every category and reports them in a single assertion.
    /// Checks: schemas, properties, types, formats, descriptions, defaults, examples, constraints,
    /// readOnly, required/nullable, enum values, $ref targets, endpoint routes, response types,
    /// error responses, security, operation summaries.
    /// </summary>
    // Skip: known failures — real-world specs expose gaps (inline enums, additionalProperties,
    // composition flattening, etc.) that are tracked as future work.
    [Theory(Skip = "Fidelity gaps in real-world specs — future work")]
    [InlineData("openapi-petstore-v3.json", "PetStore")]
    [InlineData("openapi-stripe.json", "Stripe")]
    [InlineData("openapi-github.json", "GitHub")]
    [InlineData("openapi-box.json", "Box")]
    [InlineData("openapi-twilio.json", "Twilio")]
    [InlineData("openapi-httpbin.json", "Httpbin")]
    [Trait("Category", "Slow")]
    public void Report_Property_Level_Fidelity(string fixture, string ns)
    {
        var originalJson = LoadFixture(fixture);
        var originalDoc = JsonSerializer.Deserialize<JsonElement>(originalJson);

        // Run the full pipeline: import → compile → walk → emit OpenAPI
        var import1 = OpenApiImporter.Import(originalJson, new ImportOptions(ns, null));
        var sources = import1.Files
            .GroupBy(f => f.FileName).Select(g => g.First().Content).ToArray();

        Compilation comp;
        try
        {
            comp = CompilationHelper.CreateCompilationFromMultiple(sources);
        }
        catch
        {
            comp = CreateCompilationLenient(sources);
        }

        var (disc, walker) = CompilationHelper.DiscoverAndWalk(comp);
        var endpoints = CompilationHelper.WalkContracts(comp, disc, walker);
        var emittedJson = Rivet.Tool.Emit.OpenApiEmitter.Emit(
            endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var emittedDoc = JsonSerializer.Deserialize<JsonElement>(emittedJson);

        // All failures go into one list. Every category gets checked. Nothing bails early.
        var failures = new List<string>();

        // ===== 1. SCHEMA-LEVEL CHECKS =====

        var originalSchemas = ExtractSchemas(originalDoc);
        var emittedSchemas = ExtractSchemas(emittedDoc);

        var nameMap = new Dictionary<string, string>();
        foreach (var key in originalSchemas.Keys)
            nameMap[key] = Rivet.Tool.Naming.ToPascalCaseFromSegments(key);

        var schemasLost = new List<string>();
        foreach (var (originalName, _) in originalSchemas)
        {
            var mappedName = nameMap.GetValueOrDefault(originalName, originalName);
            if (!emittedSchemas.ContainsKey(mappedName))
                schemasLost.Add(originalName);
        }

        if (schemasLost.Count > 0)
            failures.Add($"SCHEMAS LOST: {schemasLost.Count} schemas not found in emitted output " +
                $"(first 10: {string.Join(", ", schemasLost.Take(10))})");

        // ===== 2. SCHEMA DESCRIPTION CHECKS =====

        var schemaDescLost = 0;
        foreach (var (originalName, originalSchema) in originalSchemas)
        {
            var mappedName = nameMap.GetValueOrDefault(originalName, originalName);
            if (!emittedSchemas.ContainsKey(mappedName)) continue;
            var emittedSchema = emittedSchemas[mappedName];

            if (HasField(originalSchema, "description") && !HasField(emittedSchema, "description"))
                schemaDescLost++;
        }

        if (schemaDescLost > 0)
            failures.Add($"SCHEMA DESCRIPTIONS LOST: {schemaDescLost}");

        // ===== 3. PROPERTY-LEVEL CHECKS =====

        var propertiesLost = new List<string>();
        var typeDrifts = new List<string>();
        var propDescLost = 0;
        var defaultsLost = 0;
        var examplesLost = 0;
        var readOnlyLost = 0;
        var writeOnlyLost = 0;
        var deprecatedLost = 0;
        var constraintsLost = new Dictionary<string, int>();
        var requiredNullableBugs = 0; // required + nullable in original, not required in emitted
        var inlineEnumLost = 0;
        var inlineEnumDrifted = 0;
        var additionalPropsLost = 0;

        foreach (var (originalName, originalSchema) in originalSchemas)
        {
            var mappedName = nameMap.GetValueOrDefault(originalName, originalName);
            if (!emittedSchemas.ContainsKey(mappedName)) continue;

            var emittedSchema = emittedSchemas[mappedName];
            var origProps = ExtractProperties(originalSchema);
            var emitProps = ExtractProperties(emittedSchema);
            var origRequired = ExtractRequired(originalSchema);
            var emitRequired = ExtractRequired(emittedSchema);

            foreach (var (propName, origProp) in origProps)
            {
                // Find matching property
                var emitPropName = propName;
                if (!emitProps.ContainsKey(propName))
                {
                    var pascal = Rivet.Tool.Naming.ToPascalCaseFromSegments(propName);
                    var camel = Rivet.Tool.Naming.ToCamelCase(pascal);
                    if (emitProps.ContainsKey(camel)) emitPropName = camel;
                    else if (emitProps.ContainsKey(pascal)) emitPropName = pascal;
                    else { propertiesLost.Add($"{originalName}.{propName}"); continue; }
                }

                var emitProp = emitProps[emitPropName];

                // 3a. Type + format drift
                var origType = GetSchemaType(origProp);
                var emitType = GetSchemaType(emitProp);
                if (origType != emitType && origType != "unknown" && emitType != "unknown"
                    && origType != "mixed" && emitType != "mixed"
                    && !(origType.StartsWith("$ref") && emitType == "object"))
                {
                    typeDrifts.Add($"{originalName}.{propName}: {origType} → {emitType}");
                }

                // 3a-ii. Array item type drift
                if (origProp.TryGetProperty("items", out var origItems) &&
                    emitProp.TryGetProperty("items", out var emitItems))
                {
                    var origItemType = GetSchemaType(origItems);
                    var emitItemType = GetSchemaType(emitItems);
                    if (origItemType != emitItemType && origItemType != "unknown" && emitItemType != "unknown"
                        && origItemType != "mixed" && emitItemType != "mixed")
                    {
                        typeDrifts.Add($"{originalName}.{propName}[items]: {origItemType} → {emitItemType}");
                    }
                }

                // 3a-iii. Inline enum values on properties
                // Enum values may be preserved inline OR moved to a $ref target — both are valid
                if (origProp.TryGetProperty("enum", out var origPropEnum) && origPropEnum.GetArrayLength() > 0)
                {
                    var emitEnumValues = GetEnumValuesFollowingRef(emitProp, emittedSchemas);
                    if (emitEnumValues is null)
                    {
                        inlineEnumLost++;
                    }
                    else
                    {
                        var origVals = origPropEnum.EnumerateArray().Select(v => v.ToString()).OrderBy(v => v).ToList();
                        var emitVals = emitEnumValues.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                        if (!origVals.SequenceEqual(emitVals, StringComparer.OrdinalIgnoreCase))
                            inlineEnumDrifted++;
                    }
                }

                // 3a-iv. additionalProperties
                if (HasField(origProp, "additionalProperties") && !HasField(emitProp, "additionalProperties"))
                    additionalPropsLost++;

                // 3b. Required + nullable
                var wasRequired = origRequired.Contains(propName);
                var isRequired = emitRequired.Contains(emitPropName);
                var wasNullable = IsNullable(origProp);
                if (wasRequired && wasNullable && !isRequired)
                    requiredNullableBugs++;

                // 3c. Metadata
                if (HasField(origProp, "description") && !HasField(emitProp, "description"))
                    propDescLost++;
                if (HasField(origProp, "default") && !HasField(emitProp, "default"))
                    defaultsLost++;
                if (HasField(origProp, "example") && !HasField(emitProp, "example"))
                    examplesLost++;
                if (HasField(origProp, "readOnly") && !HasField(emitProp, "readOnly"))
                    readOnlyLost++;
                if (HasField(origProp, "writeOnly") && !HasField(emitProp, "writeOnly"))
                    writeOnlyLost++;
                if (HasField(origProp, "deprecated") && !HasField(emitProp, "deprecated"))
                    deprecatedLost++;

                // 3d. Constraints
                foreach (var c in new[] { "minLength", "maxLength", "pattern", "minimum", "maximum",
                    "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minItems", "maxItems", "uniqueItems" })
                {
                    if (HasField(origProp, c) && !HasField(emitProp, c))
                    {
                        constraintsLost.TryGetValue(c, out var n);
                        constraintsLost[c] = n + 1;
                    }
                }
            }
        }

        if (propertiesLost.Count > 0)
            failures.Add($"PROPERTIES LOST: {propertiesLost.Count} " +
                $"(first 10: {string.Join(", ", propertiesLost.Take(10))})");

        // Split type drifts into real losses vs representation changes (→ allOf)
        var driftPatterns = typeDrifts
            .Select(d => d[(d.LastIndexOf(": ", StringComparison.Ordinal) + 2)..])
            .GroupBy(p => p).OrderByDescending(g => g.Count()).ToList();
        var realDrifts = driftPatterns.Where(g => !g.Key.EndsWith("→ allOf")).ToList();
        var realDriftCount = realDrifts.Sum(g => g.Count());

        if (realDriftCount > 0)
            failures.Add($"TYPE/FORMAT DRIFT: {realDriftCount} properties changed type " +
                $"(top patterns: {string.Join("; ", realDrifts.Take(5).Select(g => $"[{g.Count()}x] {g.Key}"))})");


        if (requiredNullableBugs > 0)
            failures.Add($"REQUIRED+NULLABLE BUG: {requiredNullableBugs} properties are required+nullable " +
                "in original but emitter drops them from required[]");

        if (propDescLost > 0)
            failures.Add($"PROPERTY DESCRIPTIONS LOST: {propDescLost}");
        if (defaultsLost > 0)
            failures.Add($"DEFAULTS LOST: {defaultsLost}");
        if (examplesLost > 0)
            failures.Add($"EXAMPLES LOST: {examplesLost}");
        if (readOnlyLost > 0)
            failures.Add($"READONLY LOST: {readOnlyLost}");
        if (writeOnlyLost > 0)
            failures.Add($"WRITEONLY LOST: {writeOnlyLost}");
        if (deprecatedLost > 0)
            failures.Add($"DEPRECATED FLAGS LOST: {deprecatedLost}");
        if (constraintsLost.Count > 0)
            failures.Add($"CONSTRAINTS LOST: {constraintsLost.Values.Sum()} total " +
                $"({string.Join(", ", constraintsLost.Select(kv => $"{kv.Key}:{kv.Value}"))})");
        if (inlineEnumLost > 0)
            failures.Add($"INLINE ENUM LOST: {inlineEnumLost} properties had enum values in original but not in output");
        if (inlineEnumDrifted > 0)
            failures.Add($"INLINE ENUM DRIFTED: {inlineEnumDrifted} properties have different enum values after round-trip");
        if (additionalPropsLost > 0)
            failures.Add($"ADDITIONAL_PROPERTIES LOST: {additionalPropsLost}");

        // ===== 4. ENUM VALUE CHECKS (top-level enum schemas) =====

        var enumsLost = 0;
        var enumValuesDrifted = new List<string>();
        foreach (var (originalName, originalSchema) in originalSchemas)
        {
            var mappedName = nameMap.GetValueOrDefault(originalName, originalName);
            if (!emittedSchemas.ContainsKey(mappedName)) continue;

            if (!HasStringEnum(originalSchema)) continue;
            var emittedSchema = emittedSchemas[mappedName];
            if (!HasField(emittedSchema, "enum")) { enumsLost++; continue; }

            var origValues = originalSchema.GetProperty("enum").EnumerateArray()
                .Select(v => v.GetString()!).OrderBy(v => v).ToList();
            var emitValues = emittedSchema.GetProperty("enum").EnumerateArray()
                .Select(v => v.GetString()!).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            // Compare actual values case-insensitively, not just counts
            var origNorm = origValues.Select(v => v.ToLowerInvariant()).ToHashSet();
            var emitNorm = emitValues.Select(v => v.ToLowerInvariant()).ToHashSet();
            var missing = origNorm.Except(emitNorm).ToList();
            var added = emitNorm.Except(origNorm).ToList();
            if (missing.Count > 0 || added.Count > 0)
                enumValuesDrifted.Add($"{originalName}: -{missing.Count}/+{added.Count}");
        }

        if (enumsLost > 0)
            failures.Add($"ENUM SCHEMAS LOST: {enumsLost} top-level enums lost their enum values entirely");
        if (enumValuesDrifted.Count > 0)
            failures.Add($"ENUM VALUE DRIFT: {enumValuesDrifted.Count} enum schemas have different members " +
                $"(first 5: {string.Join("; ", enumValuesDrifted.Take(5))})");

        // ===== 5. $ref TARGET CHECKS =====

        var brokenRefs = new List<string>();
        CollectAllRefs(emittedDoc, brokenRefs);
        var emittedSchemaNames = new HashSet<string>(emittedSchemas.Keys);
        var broken = brokenRefs
            .Where(r => r.StartsWith("#/components/schemas/"))
            .Where(r => !emittedSchemaNames.Contains(r["#/components/schemas/".Length..]))
            .Distinct().ToList();

        if (broken.Count > 0)
            failures.Add($"BROKEN $REFS: {broken.Count} $ref targets don't exist " +
                $"(first 5: {string.Join(", ", broken.Take(5))})");

        // ===== 6. ENDPOINT CHECKS =====

        var originalOps = ExtractOperations(originalDoc);
        var emittedOps = ExtractOperations(emittedDoc);

        var opsLost = new List<string>();
        var summariesLost = 0;
        var descriptionsLost = 0;
        var responseStatusCodesLost = new List<string>();
        var responseSchemasDrifted = new List<string>();
        var errorResponsesLost = 0;
        var securityLost = 0;
        var requestBodyLost = 0;
        var requestBodySchemaDrifted = new List<string>();
        var parametersLost = 0;
        var parameterTypeDrifts = 0;
        var tagsLost = 0;

        foreach (var (key, origOp) in originalOps)
        {
            // Skip HEAD/OPTIONS — intentionally excluded
            if (key.StartsWith("HEAD ") || key.StartsWith("OPTIONS ")) continue;

            if (!emittedOps.TryGetValue(key, out var emitOp))
            {
                opsLost.Add(key);
                continue;
            }

            // 6a. Summary and description
            if (HasField(origOp, "summary") && !HasField(emitOp, "summary"))
                summariesLost++;
            if (HasField(origOp, "description") && !HasField(emitOp, "description"))
                descriptionsLost++;

            // 6b. Tags
            if (origOp.TryGetProperty("tags", out var origTags) && origTags.GetArrayLength() > 0)
            {
                if (!emitOp.TryGetProperty("tags", out var emitTags) || emitTags.GetArrayLength() == 0)
                    tagsLost++;
            }

            // 6c. Parameters (query, path, header, cookie)
            if (origOp.TryGetProperty("parameters", out var origParams))
            {
                var emitParamMap = new Dictionary<string, JsonElement>();
                if (emitOp.TryGetProperty("parameters", out var emitParams))
                {
                    foreach (var p in emitParams.EnumerateArray())
                    {
                        if (p.TryGetProperty("name", out var n))
                            emitParamMap[n.GetString()!] = p;
                    }
                }

                foreach (var origParam in origParams.EnumerateArray())
                {
                    if (!origParam.TryGetProperty("name", out var paramName)) continue;
                    var name = paramName.GetString()!;
                    if (!emitParamMap.TryGetValue(name, out var emitParam))
                    {
                        parametersLost++;
                        continue;
                    }

                    // Check parameter schema type
                    if (origParam.TryGetProperty("schema", out var origParamSchema) &&
                        emitParam.TryGetProperty("schema", out var emitParamSchema))
                    {
                        var ot = GetSchemaType(origParamSchema);
                        var et = GetSchemaType(emitParamSchema);
                        if (ot != et && ot != "unknown" && et != "unknown" && ot != "mixed" && et != "mixed")
                            parameterTypeDrifts++;
                    }
                }
            }

            // 6d. Request body
            if (origOp.TryGetProperty("requestBody", out var origBody))
            {
                if (!emitOp.TryGetProperty("requestBody", out var emitBody))
                {
                    requestBodyLost++;
                }
                else
                {
                    // Compare the JSON schema ref/type of the request body
                    var origRef = GetRequestBodySchemaRef(origBody);
                    var emitRef = GetRequestBodySchemaRef(emitBody);
                    if (origRef != null && emitRef != null && origRef != emitRef)
                        requestBodySchemaDrifted.Add($"{key}: {origRef} → {emitRef}");
                }
            }

            // 6e. Response status codes and schema types
            if (origOp.TryGetProperty("responses", out var origResps) &&
                emitOp.TryGetProperty("responses", out var emitResps))
            {
                foreach (var origResp in origResps.EnumerateObject())
                {
                    if (!emitResps.TryGetProperty(origResp.Name, out var emitResp))
                    {
                        if (int.TryParse(origResp.Name, out var code) && code >= 400)
                            errorResponsesLost++;
                        else if (origResp.Name == "default")
                            errorResponsesLost++;
                        else
                            responseStatusCodesLost.Add($"{key} missing response {origResp.Name}");
                    }
                    else
                    {
                        // Response exists — check the schema matches
                        var origRespRef = GetResponseSchemaRef(origResp.Value);
                        var emitRespRef = GetResponseSchemaRef(emitResp);
                        if (origRespRef != null && emitRespRef != null && origRespRef != emitRespRef)
                            responseSchemasDrifted.Add($"{key} [{origResp.Name}]: {origRespRef} → {emitRespRef}");
                    }
                }
            }

            // 6f. Security
            var origHasSec = HasField(origOp, "security");
            var emitHasSec = HasField(emitOp, "security");
            if (origHasSec && !emitHasSec)
                securityLost++;
        }

        if (opsLost.Count > 0)
            failures.Add($"ENDPOINTS LOST: {opsLost.Count} operations missing " +
                $"(first 5: {string.Join(", ", opsLost.Take(5))})");
        if (summariesLost > 0)
            failures.Add($"OPERATION SUMMARIES LOST: {summariesLost}");
        if (descriptionsLost > 0)
            failures.Add($"OPERATION DESCRIPTIONS LOST: {descriptionsLost}");
        if (tagsLost > 0)
            failures.Add($"OPERATION TAGS LOST: {tagsLost}");
        if (parametersLost > 0)
            failures.Add($"PARAMETERS LOST: {parametersLost}");
        if (parameterTypeDrifts > 0)
            failures.Add($"PARAMETER TYPE DRIFT: {parameterTypeDrifts}");
        if (requestBodyLost > 0)
            failures.Add($"REQUEST BODIES LOST: {requestBodyLost}");
        if (requestBodySchemaDrifted.Count > 0)
            failures.Add($"REQUEST BODY SCHEMA DRIFT: {requestBodySchemaDrifted.Count} " +
                $"(first 5: {string.Join("; ", requestBodySchemaDrifted.Take(5))})");
        if (responseStatusCodesLost.Count > 0)
            failures.Add($"RESPONSE STATUS CODES LOST: {responseStatusCodesLost.Count} " +
                $"(first 5: {string.Join(", ", responseStatusCodesLost.Take(5))})");
        if (responseSchemasDrifted.Count > 0)
            failures.Add($"RESPONSE SCHEMA DRIFT: {responseSchemasDrifted.Count} " +
                $"(first 5: {string.Join("; ", responseSchemasDrifted.Take(5))})");
        if (errorResponsesLost > 0)
            failures.Add($"ERROR RESPONSES LOST: {errorResponsesLost}");
        if (securityLost > 0)
            failures.Add($"SECURITY ANNOTATIONS LOST: {securityLost}");

        var endpointExampleFidelity = AnalyzeEndpointExampleFidelity(originalDoc, emittedDoc);
        failures.AddRange(endpointExampleFidelity.Failures);

        // ===== FINAL ASSERT =====
        Assert.True(failures.Count == 0,
            $"{ns}: {failures.Count} fidelity failures:\n  " +
            string.Join("\n  ", failures));
    }

    private static HashSet<string> ExtractRequired(JsonElement schema)
    {
        var result = new HashSet<string>();
        if (schema.TryGetProperty("required", out var req))
        {
            foreach (var item in req.EnumerateArray())
                if (item.GetString() is { } s) result.Add(s);
        }
        return result;
    }

    private static bool IsNullable(JsonElement prop)
    {
        // type: ["string", "null"]
        if (prop.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in type.EnumerateArray())
                if (t.GetString() == "null") return true;
        }

        // nullable: true
        if (prop.TryGetProperty("nullable", out var n) && n.GetBoolean())
            return true;

        // oneOf with null variant
        if (prop.TryGetProperty("oneOf", out var oneOf))
        {
            foreach (var v in oneOf.EnumerateArray())
                if (v.TryGetProperty("type", out var t) && t.GetString() == "null") return true;
        }

        return false;
    }

    private static Dictionary<string, JsonElement> ExtractOperations(JsonElement doc)
    {
        var result = new Dictionary<string, JsonElement>();
        if (!doc.TryGetProperty("paths", out var paths)) return result;

        foreach (var path in paths.EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject())
            {
                if (IsHttpMethod(method.Name))
                    result[$"{method.Name.ToUpperInvariant()} {path.Name}"] = method.Value;
            }
        }
        return result;
    }

    private static EndpointExampleFidelity AnalyzeEndpointExampleFidelity(JsonElement originalDoc, JsonElement emittedDoc)
    {
        var emittedExamples = ExtractEndpointExamples(emittedDoc).ToDictionary(
            example => example.Key,
            example => example,
            StringComparer.Ordinal);

        var requestExampleLoss = 0;
        var responseExampleLoss = 0;
        var namedExampleLoss = 0;
        var refBackedExampleLoss = 0;

        foreach (var originalExample in ExtractEndpointExamples(originalDoc))
        {
            if (!emittedExamples.TryGetValue(originalExample.Key, out var emittedExample))
            {
                if (originalExample.Location == EndpointExampleLocation.Request)
                {
                    requestExampleLoss++;
                }
                else
                {
                    responseExampleLoss++;
                }

                if (originalExample.Name is not null)
                {
                    namedExampleLoss++;
                }

                if (originalExample.IsRefBacked)
                {
                    refBackedExampleLoss++;
                }

                continue;
            }

            if (originalExample.IsRefBacked && !emittedExample.IsRefBacked)
            {
                refBackedExampleLoss++;
            }
        }

        var failures = new List<string>();
        if (requestExampleLoss > 0)
        {
            failures.Add($"REQUEST EXAMPLE LOSS: {requestExampleLoss}");
        }

        if (responseExampleLoss > 0)
        {
            failures.Add($"RESPONSE EXAMPLE LOSS: {responseExampleLoss}");
        }

        if (namedExampleLoss > 0)
        {
            failures.Add($"NAMED EXAMPLE LOSS: {namedExampleLoss}");
        }

        if (refBackedExampleLoss > 0)
        {
            failures.Add($"REF-BACKED EXAMPLE LOSS: {refBackedExampleLoss}");
        }

        return new EndpointExampleFidelity(
            requestExampleLoss,
            responseExampleLoss,
            namedExampleLoss,
            refBackedExampleLoss,
            failures);
    }

    private static int CountPropertyExampleLoss(JsonElement originalDoc, JsonElement emittedDoc)
    {
        var originalSchemas = ExtractSchemas(originalDoc);
        var emittedSchemas = ExtractSchemas(emittedDoc);

        var nameMap = new Dictionary<string, string>();
        foreach (var key in originalSchemas.Keys)
        {
            nameMap[key] = Rivet.Tool.Naming.ToPascalCaseFromSegments(key);
        }

        var examplesLost = 0;
        foreach (var (originalName, originalSchema) in originalSchemas)
        {
            var mappedName = nameMap.GetValueOrDefault(originalName, originalName);
            if (!emittedSchemas.ContainsKey(mappedName))
            {
                continue;
            }

            var emittedSchema = emittedSchemas[mappedName];
            var originalProperties = ExtractProperties(originalSchema);
            var emittedProperties = ExtractProperties(emittedSchema);

            foreach (var (propertyName, originalProperty) in originalProperties)
            {
                var emittedPropertyName = propertyName;
                if (!emittedProperties.ContainsKey(propertyName))
                {
                    var pascal = Rivet.Tool.Naming.ToPascalCaseFromSegments(propertyName);
                    var camel = Rivet.Tool.Naming.ToCamelCase(pascal);
                    if (emittedProperties.ContainsKey(camel))
                    {
                        emittedPropertyName = camel;
                    }
                    else if (emittedProperties.ContainsKey(pascal))
                    {
                        emittedPropertyName = pascal;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (HasField(originalProperty, "example") && !HasField(emittedProperties[emittedPropertyName], "example"))
                {
                    examplesLost++;
                }
            }
        }

        return examplesLost;
    }

    private static List<EndpointExampleOccurrence> ExtractEndpointExamples(JsonElement doc)
    {
        var examples = new List<EndpointExampleOccurrence>();
        foreach (var (operationKey, operation) in ExtractOperations(doc))
        {
            if (operation.TryGetProperty("requestBody", out var requestBody))
            {
                CollectEndpointExamples(examples, operationKey, EndpointExampleLocation.Request, statusCode: null, requestBody);
            }

            if (operation.TryGetProperty("responses", out var responses))
            {
                foreach (var response in responses.EnumerateObject())
                {
                    CollectEndpointExamples(examples, operationKey, EndpointExampleLocation.Response, response.Name, response.Value);
                }
            }
        }

        return examples;
    }

    private static void CollectEndpointExamples(
        List<EndpointExampleOccurrence> examples,
        string operationKey,
        EndpointExampleLocation location,
        string? statusCode,
        JsonElement container)
    {
        if (!container.TryGetProperty("content", out var content))
        {
            return;
        }

        foreach (var mediaType in content.EnumerateObject())
        {
            if (mediaType.Value.TryGetProperty("example", out _))
            {
                examples.Add(new EndpointExampleOccurrence(
                    $"{operationKey}|{location}|{statusCode}|{mediaType.Name}|__single__",
                    location,
                    statusCode,
                    mediaType.Name,
                    Name: null,
                    IsRefBacked: false));
            }

            if (!mediaType.Value.TryGetProperty("examples", out var namedExamples))
            {
                continue;
            }

            foreach (var example in namedExamples.EnumerateObject())
            {
                examples.Add(new EndpointExampleOccurrence(
                    $"{operationKey}|{location}|{statusCode}|{mediaType.Name}|{example.Name}",
                    location,
                    statusCode,
                    mediaType.Name,
                    example.Name,
                    example.Value.TryGetProperty("$ref", out _)));
            }
        }
    }

    private sealed record EndpointExampleFidelity(
        int RequestExampleLoss,
        int ResponseExampleLoss,
        int NamedExampleLoss,
        int RefBackedExampleLoss,
        IReadOnlyList<string> Failures);

    private sealed record EndpointExampleOccurrence(
        string Key,
        EndpointExampleLocation Location,
        string? StatusCode,
        string MediaType,
        string? Name,
        bool IsRefBacked);

    private enum EndpointExampleLocation
    {
        Request,
        Response,
    }

    private static void CollectAllRefs(JsonElement element, List<string> refs)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "$ref" && prop.Value.ValueKind == JsonValueKind.String)
                        refs.Add(prop.Value.GetString()!);
                    else
                        CollectAllRefs(prop.Value, refs);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectAllRefs(item, refs);
                break;
        }
    }

    private static Dictionary<string, JsonElement> ExtractSchemas(JsonElement doc)
    {
        var result = new Dictionary<string, JsonElement>();
        if (doc.TryGetProperty("components", out var comps) &&
            comps.TryGetProperty("schemas", out var schemas))
        {
            foreach (var s in schemas.EnumerateObject())
                result[s.Name] = s.Value;
        }
        return result;
    }

    private static Dictionary<string, JsonElement> ExtractProperties(JsonElement schema)
    {
        var result = new Dictionary<string, JsonElement>();

        // Direct properties
        if (schema.TryGetProperty("properties", out var props))
        {
            foreach (var p in props.EnumerateObject())
                result[p.Name] = p.Value;
        }

        // allOf: merge properties from all items
        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var item in allOf.EnumerateArray())
            {
                if (item.TryGetProperty("properties", out var allOfProps))
                {
                    foreach (var p in allOfProps.EnumerateObject())
                    {
                        result.TryAdd(p.Name, p.Value);
                    }
                }
            }
        }

        return result;
    }

    private static string GetSchemaType(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var refVal))
            return $"$ref:{refVal.GetString()}";

        if (schema.TryGetProperty("type", out var type))
        {
            string t;
            if (type.ValueKind == JsonValueKind.String)
            {
                t = type.GetString()!;
            }
            else if (type.ValueKind == JsonValueKind.Array)
            {
                // OAS 3.1 array-style type, e.g. ["string", "null"] — pick the non-null type
                var types = type.EnumerateArray()
                    .Select(v => v.GetString()!)
                    .Where(v => v != "null")
                    .ToList();
                t = types.Count == 1 ? types[0] : "mixed";
            }
            else
            {
                t = "mixed";
            }
            if (schema.TryGetProperty("format", out var fmt))
                return $"{t}:{fmt.GetString()}";
            return t;
        }

        if (schema.TryGetProperty("oneOf", out _))
            return "oneOf";
        if (schema.TryGetProperty("anyOf", out _))
            return "anyOf";
        if (schema.TryGetProperty("allOf", out _))
            return "allOf";

        return "unknown";
    }

    private static bool HasField(JsonElement schema, string fieldName)
        => schema.TryGetProperty(fieldName, out _);

    /// <summary>
    /// Gets enum values from a property schema, following $ref targets if the values
    /// have been moved to a named enum type (which is a valid representation change, not data loss).
    /// Handles: direct inline enum, $ref, nullable oneOf/anyOf wrappers, allOf wrappers.
    /// </summary>
    private static List<string>? GetEnumValuesFollowingRef(
        JsonElement propSchema, Dictionary<string, JsonElement> allSchemas)
    {
        // Direct inline enum
        if (propSchema.TryGetProperty("enum", out var enumValues) && enumValues.GetArrayLength() > 0)
        {
            return enumValues.EnumerateArray().Select(v => v.ToString()).ToList();
        }

        // Follow $ref to named schema
        var refResult = FollowRefForEnum(propSchema, allSchemas);
        if (refResult is not null) return refResult;

        // oneOf/anyOf: look for a $ref or enum inside the variants (skip null type)
        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (propSchema.TryGetProperty(keyword, out var variants))
            {
                foreach (var variant in variants.EnumerateArray())
                {
                    // Skip null type variants
                    if (variant.TryGetProperty("type", out var t) && t.GetString() == "null")
                        continue;

                    // Try inline enum on the variant
                    if (variant.TryGetProperty("enum", out var vEnum) && vEnum.GetArrayLength() > 0)
                        return vEnum.EnumerateArray().Select(v => v.ToString()).ToList();

                    // Try following $ref on the variant
                    var vRefResult = FollowRefForEnum(variant, allSchemas);
                    if (vRefResult is not null) return vRefResult;
                }
            }
        }

        // allOf: check if any item has a $ref to an enum
        if (propSchema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var item in allOf.EnumerateArray())
            {
                if (item.TryGetProperty("enum", out var aEnum) && aEnum.GetArrayLength() > 0)
                    return aEnum.EnumerateArray().Select(v => v.ToString()).ToList();

                var aRefResult = FollowRefForEnum(item, allSchemas);
                if (aRefResult is not null) return aRefResult;
            }
        }

        return null;
    }

    private static List<string>? FollowRefForEnum(JsonElement schema, Dictionary<string, JsonElement> allSchemas)
    {
        if (schema.TryGetProperty("$ref", out var refVal))
        {
            var refStr = refVal.GetString();
            if (refStr?.StartsWith("#/components/schemas/") == true)
            {
                var refName = refStr["#/components/schemas/".Length..];
                if (allSchemas.TryGetValue(refName, out var refSchema)
                    && refSchema.TryGetProperty("enum", out var refEnum) && refEnum.GetArrayLength() > 0)
                {
                    return refEnum.EnumerateArray().Select(v => v.ToString()).ToList();
                }
            }
        }
        return null;
    }

    private static string? GetRequestBodySchemaRef(JsonElement requestBody)
    {
        if (!requestBody.TryGetProperty("content", out var content)) return null;
        // Try application/json first, then any content type
        if (content.TryGetProperty("application/json", out var json) &&
            json.TryGetProperty("schema", out var schema))
            return GetSchemaType(schema);
        foreach (var ct in content.EnumerateObject())
        {
            if (ct.Value.TryGetProperty("schema", out var s))
                return GetSchemaType(s);
        }
        return null;
    }

    private static string? GetResponseSchemaRef(JsonElement response)
    {
        if (!response.TryGetProperty("content", out var content)) return null;
        if (content.TryGetProperty("application/json", out var json) &&
            json.TryGetProperty("schema", out var schema))
            return GetSchemaType(schema);
        foreach (var ct in content.EnumerateObject())
        {
            if (ct.Value.TryGetProperty("schema", out var s))
                return GetSchemaType(s);
        }
        return null;
    }

    private static Compilation CreateCompilationLenient(string[] sources)
    {
        var importStubs = """
            namespace Microsoft.AspNetCore.Http { public interface IFormFile { } }
            namespace System { public readonly struct DateOnly { public DateOnly(int year, int month, int day) { } } }
            """;

        var trees = sources.Append(importStubs)
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
            Path.Combine(runtimeDir, "System.Memory.dll"),
            Path.Combine(runtimeDir, "netstandard.dll"),
            Path.Combine(runtimeDir, "System.Private.Uri.dll"),
            typeof(RivetTypeAttribute).Assembly.Location,
        };
        foreach (var extra in new[] { "System.Linq.dll", "System.Console.dll" })
        {
            var path = Path.Combine(runtimeDir, extra);
            if (File.Exists(path)) refFiles.Add(path);
        }

        return Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "TestAssembly",
            trees,
            refFiles.Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToArray(),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    // --- Helpers ---

    private static void CountInlineEnumsDetailed(
        string schemaName, JsonElement schema,
        List<string> multiValue, List<string> singleValue,
        HashSet<string> uniqueFingerprints)
    {
        if (schema.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("enum", out var enumValues) && enumValues.GetArrayLength() > 0)
                {
                    var isString = true;
                    if (prop.Value.TryGetProperty("type", out var type))
                        isString = type.ValueKind == JsonValueKind.String && type.GetString() == "string";

                    if (isString)
                    {
                        var values = enumValues.EnumerateArray()
                            .Select(v => v.ToString()).OrderBy(v => v).ToList();
                        var fingerprint = string.Join("|", values);
                        uniqueFingerprints.Add(fingerprint);

                        var display = string.Join("|", values.Take(5));
                        if (values.Count > 5) display += "|...";
                        var desc = $"{schemaName}.{prop.Name} [{values.Count} values: {display}]";

                        if (enumValues.GetArrayLength() > 1)
                            multiValue.Add(desc);
                        else
                            singleValue.Add(desc);
                    }
                }
            }
        }
    }

    private static void CountInlineEnums(string schemaName, JsonElement schema, List<string> results)
    {
        if (!schema.TryGetProperty("properties", out var props))
            return;

        foreach (var prop in props.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("enum", out var enumValues) && enumValues.GetArrayLength() > 0)
            {
                // Check it's a string enum (most common), not integer enum
                var isString = true;
                if (prop.Value.TryGetProperty("type", out var type))
                {
                    isString = type.ValueKind == JsonValueKind.String && type.GetString() == "string";
                }

                if (isString)
                {
                    var values = string.Join("|", enumValues.EnumerateArray()
                        .Take(5).Select(v => v.ToString()));
                    if (enumValues.GetArrayLength() > 5) values += "|...";
                    results.Add($"{schemaName}.{prop.Name} [{enumValues.GetArrayLength()} values: {values}]");
                }
            }

            // Also check nested allOf/oneOf/anyOf items for inline enums
            CheckCompositionForInlineEnums($"{schemaName}.{prop.Name}", prop.Value, results);
        }
    }

    private static void CheckCompositionForInlineEnums(string path, JsonElement schema, List<string> results)
    {
        foreach (var keyword in new[] { "allOf", "oneOf", "anyOf" })
        {
            if (schema.TryGetProperty(keyword, out var items))
            {
                var idx = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("properties", out _))
                    {
                        CountInlineEnums($"{path}[{keyword}#{idx}]", item, results);
                    }
                    idx++;
                }
            }
        }
    }

    private static bool HasStringEnum(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            type.GetString() == "string" &&
            schema.TryGetProperty("enum", out var e) &&
            e.GetArrayLength() > 0)
        {
            return true;
        }

        return false;
    }

    private static bool IsHttpMethod(string name)
        => name is "get" or "post" or "put" or "delete" or "patch" or "head" or "options";

    private static int GetCompilationErrorCount(ImportResult result)
    {
        var uniqueFiles = result.Files
            .GroupBy(f => f.FileName)
            .Select(g => g.First().Content)
            .Append("""
                namespace Microsoft.AspNetCore.Http { public interface IFormFile { } }
                namespace System { public readonly struct DateOnly { public DateOnly(int year, int month, int day) { } } }
                """)
            .ToArray();

        try
        {
            CompilationHelper.CreateCompilationFromMultiple(uniqueFiles);
            return 0;
        }
        catch
        {
            // Re-compile without throwing to count errors
            var trees = uniqueFiles
                .Select(s => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    s, new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                        Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)))
                .ToList();

            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var refs = new[]
            {
                typeof(object).Assembly.Location,
                Path.Combine(runtimeDir, "System.Runtime.dll"),
                Path.Combine(runtimeDir, "System.Collections.dll"),
                Path.Combine(runtimeDir, "System.Text.Json.dll"),
                Path.Combine(runtimeDir, "System.Memory.dll"),
                Path.Combine(runtimeDir, "netstandard.dll"),
                Path.Combine(runtimeDir, "System.Private.Uri.dll"),
                typeof(RivetTypeAttribute).Assembly.Location,
            }.Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToArray();

            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "TestAssembly", trees, refs,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));

            return compilation.GetDiagnostics()
                .Count(d => d.Severity == DiagnosticSeverity.Error);
        }
    }
}
