using System.Text.Json;

namespace Rivet.Tests;

/// <summary>
/// Diagnostic tests that measure actual data loss when importing real-world OpenAPI specs.
/// Not structural round-trip checks — these count what the input spec had vs what Rivet captured.
/// </summary>
public sealed class GapAnalysisTests
{
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
            """
        );
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
            """
        );

        var fidelity = AnalyzeEndpointExampleFidelity(originalDoc, emittedDoc);

        Assert.Equal(1, fidelity.RequestExampleLoss);
        Assert.Equal(1, fidelity.ResponseExampleLoss);
        Assert.DoesNotContain(
            fidelity.Failures,
            failure => failure.StartsWith("EXAMPLES LOST:", StringComparison.Ordinal)
        );
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
            """
        );
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
            """
        );

        var fidelity = AnalyzeEndpointExampleFidelity(originalDoc, emittedDoc);

        Assert.Equal(0, CountPropertyExampleLoss(originalDoc, emittedDoc));
        Assert.Equal(1, fidelity.NamedExampleLoss);
        Assert.Equal(1, fidelity.RefBackedExampleLoss);
        Assert.DoesNotContain(
            fidelity.Failures,
            failure => failure.StartsWith("EXAMPLES LOST:", StringComparison.Ordinal)
        );
        Assert.Contains("NAMED EXAMPLE LOSS: 1", fidelity.Failures);
        Assert.Contains("REF-BACKED EXAMPLE LOSS: 1", fidelity.Failures);
    }

    private static Dictionary<string, JsonElement> ExtractOperations(JsonElement doc)
    {
        var result = new Dictionary<string, JsonElement>();
        if (!doc.TryGetProperty("paths", out var paths))
        {
            return result;
        }

        foreach (var path in paths.EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject())
            {
                if (IsHttpMethod(method.Name))
                {
                    result[$"{method.Name.ToUpperInvariant()} {path.Name}"] = method.Value;
                }
            }
        }
        return result;
    }

    private static EndpointExampleFidelity AnalyzeEndpointExampleFidelity(
        JsonElement originalDoc,
        JsonElement emittedDoc
    )
    {
        var emittedExamples = ExtractEndpointExamples(emittedDoc)
            .ToDictionary(example => example.Key, example => example, StringComparer.Ordinal);

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
            failures
        );
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

                if (
                    HasField(originalProperty, "example")
                    && !HasField(emittedProperties[emittedPropertyName], "example")
                )
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
                CollectEndpointExamples(
                    examples,
                    operationKey,
                    EndpointExampleLocation.Request,
                    statusCode: null,
                    requestBody
                );
            }

            if (operation.TryGetProperty("responses", out var responses))
            {
                foreach (var response in responses.EnumerateObject())
                {
                    CollectEndpointExamples(
                        examples,
                        operationKey,
                        EndpointExampleLocation.Response,
                        response.Name,
                        response.Value
                    );
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
        JsonElement container
    )
    {
        if (!container.TryGetProperty("content", out var content))
        {
            return;
        }

        foreach (var mediaType in content.EnumerateObject())
        {
            if (mediaType.Value.TryGetProperty("example", out _))
            {
                examples.Add(
                    new EndpointExampleOccurrence(
                        $"{operationKey}|{location}|{statusCode}|{mediaType.Name}|__single__",
                        location,
                        statusCode,
                        mediaType.Name,
                        Name: null,
                        IsRefBacked: false
                    )
                );
            }

            if (!mediaType.Value.TryGetProperty("examples", out var namedExamples))
            {
                continue;
            }

            foreach (var example in namedExamples.EnumerateObject())
            {
                examples.Add(
                    new EndpointExampleOccurrence(
                        $"{operationKey}|{location}|{statusCode}|{mediaType.Name}|{example.Name}",
                        location,
                        statusCode,
                        mediaType.Name,
                        example.Name,
                        example.Value.TryGetProperty("$ref", out _)
                    )
                );
            }
        }
    }

    private sealed record EndpointExampleFidelity(
        int RequestExampleLoss,
        int ResponseExampleLoss,
        int NamedExampleLoss,
        int RefBackedExampleLoss,
        IReadOnlyList<string> Failures
    );

    private sealed record EndpointExampleOccurrence(
        string Key,
        EndpointExampleLocation Location,
        string? StatusCode,
        string MediaType,
        string? Name,
        bool IsRefBacked
    );

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
                    {
                        refs.Add(prop.Value.GetString()!);
                    }
                    else
                    {
                        CollectAllRefs(prop.Value, refs);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAllRefs(item, refs);
                }

                break;
        }
    }

    private static Dictionary<string, JsonElement> ExtractSchemas(JsonElement doc)
    {
        var result = new Dictionary<string, JsonElement>();
        if (
            doc.TryGetProperty("components", out var comps)
            && comps.TryGetProperty("schemas", out var schemas)
        )
        {
            foreach (var s in schemas.EnumerateObject())
            {
                result[s.Name] = s.Value;
            }
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
            {
                result[p.Name] = p.Value;
            }
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

    private static bool HasField(JsonElement schema, string fieldName) =>
        schema.TryGetProperty(fieldName, out _);

    private static bool IsHttpMethod(string name) =>
        name is "get" or "post" or "put" or "delete" or "patch" or "head" or "options";
}
