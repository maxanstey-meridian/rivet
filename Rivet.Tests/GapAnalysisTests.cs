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
        var output = CompilationHelper.CaptureStdOut(() =>
            RunFullGapAnalysis("openapi-github.json", "GitHub"));
        Assert.Contains("ENDPOINT EXAMPLE FIDELITY:", output);
        Assert.Contains("Request example loss:", output);
        Assert.Contains("Response example loss:", output);
        Assert.Contains("Named example loss:", output);
        Assert.Contains("Ref-backed example loss:", output);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Full_Gap_Analysis_Report_Prints_Exact_Endpoint_Example_Counts()
    {
        var originalJson = LoadFixture("openapi-github.json");
        var originalDoc = JsonSerializer.Deserialize<JsonElement>(originalJson);
        var import = OpenApiImporter.Import(originalJson, new ImportOptions("GitHub", null));
        var sources = import.Files
            .GroupBy(file => file.FileName)
            .Select(group => group.First().Content)
            .ToArray();

        Compilation compilation;
        try
        {
            compilation = CompilationHelper.CreateCompilationFromMultiple(sources);
        }
        catch
        {
            compilation = CreateCompilationLenient(sources);
        }

        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var emittedJson = Rivet.Tool.Emit.OpenApiEmitter.Emit(
            endpoints, walker.Definitions, walker.Brands, walker.Enums, null);
        var emittedDoc = JsonSerializer.Deserialize<JsonElement>(emittedJson);
        var fidelity = AnalyzeEndpointExampleFidelity(originalDoc, emittedDoc);

        var output = CompilationHelper.CaptureStdOut(() =>
            RunFullGapAnalysis("openapi-github.json", "GitHub"));
        Assert.Contains($"Request example loss:    {fidelity.RequestExampleLoss}", output);
        Assert.Contains($"Response example loss:   {fidelity.ResponseExampleLoss}", output);
        Assert.Contains($"Named example loss:      {fidelity.NamedExampleLoss}", output);
        Assert.Contains($"Ref-backed example loss: {fidelity.RefBackedExampleLoss}", output);
    }

    /// <summary>
    /// Full gap analysis: import → compile → walk → emit OpenAPI → diff against original.
    /// Diagnostic report helper (not a test — it asserts nothing itself); exercised by the
    /// Full_Gap_Analysis_Report_* tests above, which assert on its output.
    /// </summary>
    private static void RunFullGapAnalysis(string fixture, string ns)
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


    private static bool HasField(JsonElement schema, string fieldName)
        => schema.TryGetProperty(fieldName, out _);





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
