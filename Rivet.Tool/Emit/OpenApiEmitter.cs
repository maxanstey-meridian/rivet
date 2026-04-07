using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Emits an OpenAPI 3.0 JSON spec from the Rivet model.
/// </summary>
public static class OpenApiEmitter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Emit(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        SecurityConfig? security)
    {
        var paths = BuildPaths(endpoints, security);
        var schemas = BuildSchemas(endpoints, definitions, brands, enums);
        var examples = BuildComponentExamples(endpoints);

        var doc = new Dictionary<string, object>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = "API",
                ["version"] = "1.0.0",
            },
            ["paths"] = paths,
        };

        var components = new Dictionary<string, object>();

        if (schemas.Count > 0)
        {
            components["schemas"] = schemas;
        }

        if (examples.Count > 0)
        {
            components["examples"] = examples;
        }

        if (security is not null)
        {
            components["securitySchemes"] = new Dictionary<string, object>
            {
                [security.SchemeName] = security.SchemeDefinition,
            };

            doc["security"] = new List<object>
            {
                new Dictionary<string, object> { [security.SchemeName] = Array.Empty<string>() },
            };
        }

        if (components.Count > 0)
        {
            doc["components"] = components;
        }

        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    private static Dictionary<string, object> BuildPaths(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        SecurityConfig? security)
    {
        var paths = new Dictionary<string, object>();

        foreach (var ep in endpoints)
        {
            var pathKey = ep.RouteTemplate;

            if (!paths.TryGetValue(pathKey, out var existing))
            {
                existing = new Dictionary<string, object>();
                paths[pathKey] = existing;
            }

            var pathItem = (Dictionary<string, object>)existing;
            var methodKey = ep.HttpMethod.ToLowerInvariant();
            if (pathItem.ContainsKey(methodKey))
            {
                Console.Error.WriteLine($"warning: duplicate endpoint {ep.HttpMethod} {pathKey} — later definition wins");
            }
            var operation = BuildOperation(ep, security);
            pathItem[methodKey] = operation;
        }

        return paths;
    }

    private static Dictionary<string, object> BuildOperation(
        TsEndpointDefinition ep,
        SecurityConfig? security)
    {
        var operation = new Dictionary<string, object>
        {
            ["operationId"] = $"{ep.ControllerName}_{ep.Name}",
            ["tags"] = new List<string> { UpperFirst(ep.ControllerName) },
        };

        if (ep.Summary is not null)
        {
            operation["summary"] = ep.Summary;
        }

        if (ep.Description is not null)
        {
            operation["description"] = ep.Description;
        }

        // Parameters (route + query)
        var parameters = new List<object>();
        TsEndpointParam? bodyParam = null;
        var fileParams = new List<TsEndpointParam>();
        var formFieldParams = new List<TsEndpointParam>();

        foreach (var param in ep.Params)
        {
            switch (param.Source)
            {
                case ParamSource.Route:
                    parameters.Add(new Dictionary<string, object>
                    {
                        ["name"] = param.Name,
                        ["in"] = "path",
                        ["required"] = true,
                        ["schema"] = MapTsTypeToJsonSchema(param.Type),
                    });
                    break;

                case ParamSource.Query:
                    var queryParam = new Dictionary<string, object>
                    {
                        ["name"] = param.Name,
                        ["in"] = "query",
                        ["required"] = param.Type is not TsType.Nullable,
                        ["schema"] = MapTsTypeToJsonSchema(param.Type),
                    };
                    parameters.Add(queryParam);
                    break;

                case ParamSource.Body:
                    bodyParam = param;
                    break;

                case ParamSource.File:
                    fileParams.Add(param);
                    break;

                case ParamSource.FormField:
                    formFieldParams.Add(param);
                    break;
            }
        }

        // QueryAuth: emit auth token as a required query parameter
        if (ep.QueryAuth is not null)
        {
            parameters.Add(new Dictionary<string, object>
            {
                ["name"] = ep.QueryAuth.ParameterName,
                ["in"] = "query",
                ["required"] = true,
                ["schema"] = new Dictionary<string, object> { ["type"] = "string" },
            });
        }

        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        // Request body
        if (fileParams.Count > 0)
        {
            Dictionary<string, object> multipartSchema;

            if (ep.InputTypeName is not null)
            {
                // Named input type — emit as $ref so the schema appears once in components
                multipartSchema = new Dictionary<string, object>
                {
                    ["$ref"] = $"#/components/schemas/{ep.InputTypeName}",
                };
            }
            else
            {
                // Anonymous file upload — inline the schema
                var multipartProps = new Dictionary<string, object>();
                foreach (var fp in fileParams)
                {
                    multipartProps[fp.Name] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["format"] = "binary",
                        ["x-rivet-file"] = true,
                    };
                }
                foreach (var ff in formFieldParams)
                {
                    multipartProps[ff.Name] = MapTsTypeToJsonSchema(ff.Type);
                }

                var requiredFields = new List<string>();
                foreach (var fp in fileParams)
                {
                    requiredFields.Add(fp.Name);
                }
                foreach (var ff in formFieldParams)
                {
                    if (ff.Type is not TsType.Nullable)
                    {
                        requiredFields.Add(ff.Name);
                    }
                }

                multipartSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = multipartProps,
                };

                if (requiredFields.Count > 0)
                {
                    multipartSchema["required"] = requiredFields;
                }
            }

            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = true,
                ["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        ["multipart/form-data"] = new Dictionary<string, object>
                        {
                            ["schema"] = multipartSchema,
                        },
                    },
                    ep.RequestExamples)
            };
        }
        else if (bodyParam is not null)
        {
            var bodyContentType = ep.IsFormEncoded
                ? "application/x-www-form-urlencoded"
                : "application/json";
            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = true,
                ["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        [bodyContentType] = new Dictionary<string, object>
                        {
                            ["schema"] = MapTsTypeToJsonSchema(bodyParam.Type),
                        },
                    },
                    ep.RequestExamples)
            };
        }
        else if (ep.RequestType is not null)
        {
            var requestTypeContentType = ep.IsFormEncoded
                ? "application/x-www-form-urlencoded"
                : "application/json";
            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = true,
                ["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        [requestTypeContentType] = new Dictionary<string, object>
                        {
                            ["schema"] = MapTsTypeToJsonSchema(ep.RequestType),
                        },
                    },
                    ep.RequestExamples)
            };
        }

        // Responses
        var responses = new Dictionary<string, object>();

        foreach (var resp in ep.Responses)
        {
            var respObj = new Dictionary<string, object>();

            respObj["description"] = resp.Description ?? DefaultStatusDescription(resp.StatusCode);

            if (resp.DataType is not null)
            {
                respObj["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        ["application/json"] = new Dictionary<string, object>
                        {
                            ["schema"] = MapTsTypeToJsonSchema(resp.DataType),
                        },
                    },
                    resp.Examples);
            }
            else if (ep.FileContentType is not null && resp.StatusCode is >= 200 and < 300)
            {
                respObj["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        [ep.FileContentType] = new Dictionary<string, object>
                        {
                            ["schema"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["format"] = "binary",
                            },
                        },
                    },
                    resp.Examples);
            }
            else if (resp.Examples is not null)
            {
                var content = WithExamples(new Dictionary<string, object>(), resp.Examples);
                if (content.Count > 0)
                {
                    respObj["content"] = content;
                }
            }

            responses[resp.StatusCode.ToString()] = respObj;
        }

        if (responses.Count == 0)
        {
            responses["204"] = new Dictionary<string, object> { ["description"] = "No Content" };
        }

        operation["responses"] = responses;

        // Security
        if (ep.Security is not null)
        {
            if (ep.Security.IsAnonymous)
            {
                operation["security"] = new List<object>();
            }
            else if (ep.Security.Scheme is not null)
            {
                operation["security"] = new List<object>
                {
                    new Dictionary<string, object> { [ep.Security.Scheme] = Array.Empty<string>() },
                };
            }
        }

        // QueryAuth: emit extension for round-trip fidelity
        if (ep.QueryAuth is not null)
        {
            operation["x-rivet-query-auth"] = new Dictionary<string, object>
            {
                ["parameterName"] = ep.QueryAuth.ParameterName,
            };
        }

        return operation;
    }

    private static Dictionary<string, object> BuildComponentExamples(IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        var examples = new Dictionary<string, object>();

        foreach (var endpoint in endpoints)
        {
            AddComponentExamples(examples, endpoint.RequestExamples);

            foreach (var response in endpoint.Responses)
            {
                AddComponentExamples(examples, response.Examples);
            }
        }

        return examples;
    }

    private static void AddComponentExamples(
        Dictionary<string, object> target,
        IReadOnlyList<TsEndpointExample>? examples)
    {
        if (examples is null)
        {
            return;
        }

        foreach (var example in examples)
        {
            if (example.ComponentExampleId is null || example.ResolvedJson is null || target.ContainsKey(example.ComponentExampleId))
            {
                continue;
            }

            target[example.ComponentExampleId] = new Dictionary<string, object>
            {
                ["value"] = ParseJson(example.ResolvedJson),
            };
        }
    }

    private static Dictionary<string, object> WithExamples(
        Dictionary<string, object> content,
        IReadOnlyList<TsEndpointExample>? examples)
    {
        if (examples is null || examples.Count == 0)
        {
            return content;
        }

        var templateSchema = content.Values
            .OfType<Dictionary<string, object>>()
            .Select(entry => entry.TryGetValue("schema", out var schema) ? schema : null)
            .FirstOrDefault(schema => schema is not null);

        foreach (var group in examples.GroupBy(example => example.MediaType))
        {
            var createdMediaContent = false;
            if (!content.TryGetValue(group.Key, out var mediaContentObj))
            {
                var mediaContent = new Dictionary<string, object>();
                if (templateSchema is not null)
                {
                    mediaContent["schema"] = templateSchema;
                }

                content[group.Key] = mediaContent;
                mediaContentObj = mediaContent;
                createdMediaContent = true;
            }

            var mediaContentDict = (Dictionary<string, object>)mediaContentObj;
            var groupedExamples = group.ToList();

            if (groupedExamples.Count == 1
                && groupedExamples[0].Name is null
                && groupedExamples[0].Json is not null
                && groupedExamples[0].ComponentExampleId is null)
            {
                var inlineExampleJson = groupedExamples[0].Json;
                mediaContentDict["example"] = ParseJson(inlineExampleJson!);
                continue;
            }

            var examplesDict = new Dictionary<string, object>();
            for (var index = 0; index < groupedExamples.Count; index++)
            {
                var example = groupedExamples[index];
                var key = example.Name ?? $"example{index + 1}";
                var renderedExample = ToOpenApiExample(example);
                if (renderedExample is not null)
                {
                    examplesDict[key] = renderedExample;
                }
            }

            if (examplesDict.Count == 0)
            {
                if (createdMediaContent && mediaContentDict.Count == 0)
                {
                    content.Remove(group.Key);
                }

                continue;
            }

            mediaContentDict["examples"] = examplesDict;
        }

        return content;
    }

    private static object? ToOpenApiExample(TsEndpointExample example)
    {
        if (example.ComponentExampleId is not null && example.ResolvedJson is not null)
        {
            return new Dictionary<string, object>
            {
                ["$ref"] = $"#/components/examples/{example.ComponentExampleId}",
            };
        }

        var json = example.Json ?? example.ResolvedJson;
        if (json is null)
        {
            return null;
        }

        return new Dictionary<string, object>
        {
            ["value"] = ParseJson(json),
        };
    }

    private static object ParseJson(string json) =>
        JsonSerializer.Deserialize<object>(json)
        ?? throw new InvalidOperationException("Expected example JSON to deserialize.");

    public static Dictionary<string, object> MapTsTypeToJsonSchema(TsType type)
    {
        return type switch
        {
            TsType.Primitive p => MapPrimitive(p),

            TsType.Nullable n => MapNullable(n),

            TsType.Array a => BuildArraySchema(a),

            TsType.Dictionary d => BuildDictionarySchema(d),

            TsType.StringUnion su => new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = su.Members.ToList(),
            },

            TsType.IntUnion iu => new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["enum"] = iu.Members.ToList(),
            },

            TsType.TypeRef r => new Dictionary<string, object>
            {
                ["$ref"] = $"#/components/schemas/{r.Name}",
            },

            TsType.Generic g => new Dictionary<string, object>
            {
                ["$ref"] = $"#/components/schemas/{MonomorphisedName(g)}",
            },

            TsType.Brand b => new Dictionary<string, object>
            {
                ["$ref"] = $"#/components/schemas/{b.Name}",
            },

            TsType.TypeParam tp => FallbackTypeParam(tp),

            TsType.InlineObject obj => BuildInlineObjectSchema(obj),

            _ => new Dictionary<string, object> { ["type"] = "object" },
        };
    }

    private static Dictionary<string, object> BuildInlineObjectSchema(TsType.InlineObject obj)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var (name, fieldType) in obj.Fields)
        {
            properties[name] = MapTsTypeToJsonSchema(fieldType);
            if (fieldType is not TsType.Nullable)
            {
                required.Add(name);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static Dictionary<string, object> MapPrimitive(TsType.Primitive p)
    {
        if (p.Name == "File")
        {
            return new Dictionary<string, object>
            {
                ["x-rivet-file"] = true,
                ["type"] = "string",
                ["format"] = "binary",
            };
        }

        if (p.Name == "unknown")
        {
            if (p.CSharpType is null)
            {
                Console.Error.WriteLine("warning: 'unknown' type (JsonElement/JsonNode) in OpenAPI schema — emitting as untyped");
            }

            var unknownSchema = new Dictionary<string, object>();
            // JsonNode gets x-rivet-csharp-type on the primitive itself.
            // JsonObject/JsonArray are handled by BuildDictionarySchema/BuildArraySchema on the parent.
            if (p.CSharpType is "JsonNode")
            {
                unknownSchema["x-rivet-csharp-type"] = p.CSharpType;
            }
            return unknownSchema;
        }

        // OpenAPI uses "integer" for all integer formats, not "number"
        var type = p.Format is "int32" or "int64" or "int16" or "uint16" or "int8" or "uint8"
            or "uint32" or "uint64"
            ? "integer" : p.Name;

        var schema = new Dictionary<string, object>
        {
            ["type"] = type,
        };

        if (p.Format is not null)
        {
            schema["format"] = p.Format;
        }

        // Integer range constraints
        var (min, max) = p.Format switch
        {
            "int8" => ((long?)-128, (long?)127),
            "uint8" => (0L, (long?)255),
            "int16" => (-32768L, (long?)32767),
            "uint16" => (0L, (long?)65535),
            "int32" => (-2147483648L, (long?)2147483647),
            "uint32" => (0L, (long?)4294967295),
            "uint64" => (0L, (long?)null),
            _ => ((long?)null, (long?)null),
        };
        if (min is not null)
        {
            schema["minimum"] = min.Value;
        }
        if (max is not null)
        {
            schema["maximum"] = max.Value;
        }

        if (p.CSharpType is not null)
        {
            schema["x-rivet-csharp-type"] = p.CSharpType;
        }

        return schema;
    }

    private static Dictionary<string, object> MapNullable(TsType.Nullable n)
    {
        // OpenAPI 3.0: nullable is a property, not a type
        if (n.Inner is TsType.Primitive p)
        {
            var schema = MapPrimitive(p);
            schema["nullable"] = true;
            return schema;
        }

        var inner = MapTsTypeToJsonSchema(n.Inner);

        // $ref siblings are ignored in 3.0 — wrap in allOf
        if (inner.ContainsKey("$ref"))
        {
            return new Dictionary<string, object>
            {
                ["allOf"] = new List<object> { inner },
                ["nullable"] = true,
            };
        }

        // Inline schema — add nullable directly
        inner["nullable"] = true;
        return inner;
    }

    private static Dictionary<string, object> FallbackTypeParam(TsType.TypeParam tp)
    {
        Console.Error.WriteLine($"warning: unresolved type parameter '{tp.Name}' in OpenAPI schema — emitting as object");
        return new Dictionary<string, object> { ["type"] = "object" };
    }

    private static string MonomorphisedName(TsType.Generic g)
        => TsType.MonomorphisedName(g);

    private static Dictionary<string, object> BuildSchemas(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums)
    {
        var schemas = new Dictionary<string, object>();

        foreach (var (name, def) in definitions)
        {
            if (def.TypeParameters.Count > 0)
            {
                // Generic definitions are emitted as monomorphised variants — skip the template
                continue;
            }

            schemas[name] = BuildObjectSchema(def);
        }

        // Monomorphised generics: find all Generic type refs used across definitions and endpoints
        var genericInstances = new Dictionary<string, TsType.Generic>();
        CollectGenericInstances(endpoints, definitions, genericInstances);

        foreach (var (monoName, generic) in genericInstances)
        {
            if (!definitions.TryGetValue(generic.Name, out var genericDef))
            {
                continue;
            }

            // Build a type parameter → concrete type mapping
            var typeParamMap = new Dictionary<string, TsType>();
            for (var i = 0; i < Math.Min(genericDef.TypeParameters.Count, generic.TypeArguments.Count); i++)
            {
                typeParamMap[genericDef.TypeParameters[i]] = generic.TypeArguments[i];
            }

            var monoSchema = BuildMonomorphisedSchema(genericDef, typeParamMap);
            monoSchema["x-rivet-generic"] = new Dictionary<string, object>
            {
                ["name"] = generic.Name,
                ["typeParams"] = genericDef.TypeParameters.ToList(),
                ["args"] = typeParamMap.ToDictionary(
                    kv => kv.Key,
                    kv => (object)GetCSharpTypeName(kv.Value)),
            };
            schemas[monoName] = monoSchema;
        }

        // Brands as schemas with x-rivet-brand extension
        foreach (var (name, brand) in brands)
        {
            var brandSchema = MapTsTypeToJsonSchema(brand.Inner);
            brandSchema["x-rivet-brand"] = name;
            schemas[name] = brandSchema;
        }

        // Enums as schemas
        foreach (var (name, enumType) in enums)
        {
            schemas[name] = MapTsTypeToJsonSchema(enumType);
        }

        return schemas;
    }

    private static Dictionary<string, object> BuildObjectSchema(TsTypeDefinition def)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in def.Properties)
        {
            var propSchema = MapTsTypeToJsonSchema(prop.Type);
            SchemaEnricher.EnrichPropertySchema(propSchema, prop);
            properties[prop.Name] = propSchema;

            if (!prop.IsOptional)
            {
                required.Add(prop.Name);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (def.Description is not null)
        {
            schema["description"] = def.Description;
        }

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        if (properties.Count == 0)
        {
            schema["x-rivet-empty-record"] = true;
        }

        return schema;
    }

    private static Dictionary<string, object> BuildArraySchema(TsType.Array a)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "array",
            ["items"] = MapTsTypeToJsonSchema(a.Element),
        };

        // Propagate CSharpType from inner unknown (JsonArray) to parent schema
        if (a.Element is TsType.Primitive { Name: "unknown", CSharpType: not null } p)
        {
            schema["x-rivet-csharp-type"] = p.CSharpType;
        }

        return schema;
    }

    private static Dictionary<string, object> BuildDictionarySchema(TsType.Dictionary d)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = MapTsTypeToJsonSchema(d.Value),
        };

        // Propagate CSharpType from inner unknown (JsonObject) to parent schema
        if (d.Value is TsType.Primitive { Name: "unknown", CSharpType: not null } p)
        {
            schema["x-rivet-csharp-type"] = p.CSharpType;
        }

        return schema;
    }

    private static Dictionary<string, object> BuildMonomorphisedSchema(
        TsTypeDefinition genericDef,
        Dictionary<string, TsType> typeParamMap)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in genericDef.Properties)
        {
            var resolvedType = ResolveTypeParams(prop.Type, typeParamMap);
            var propSchema = MapTsTypeToJsonSchema(resolvedType);
            SchemaEnricher.EnrichPropertySchema(propSchema, prop);
            properties[prop.Name] = propSchema;

            if (!prop.IsOptional)
            {
                required.Add(prop.Name);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static TsType ResolveTypeParams(TsType type, Dictionary<string, TsType> map)
        => TsType.ResolveTypeParams(type, map);

    private static void CollectGenericInstances(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        Dictionary<string, TsType.Generic> genericInstances)
    {
        // Walk endpoint params and responses for Generic type usages
        foreach (var ep in endpoints)
        {
            foreach (var param in ep.Params)
            {
                CollectGenericsFromType(param.Type, genericInstances);
            }

            foreach (var resp in ep.Responses)
            {
                if (resp.DataType is not null)
                {
                    CollectGenericsFromType(resp.DataType, genericInstances);
                }
            }

            if (ep.RequestType is not null)
            {
                CollectGenericsFromType(ep.RequestType, genericInstances);
            }
        }

        // Walk all definitions' properties (all schemas are emitted, so all generics must be monomorphised)
        foreach (var (_, def) in definitions)
        {
            foreach (var prop in def.Properties)
            {
                CollectGenericsFromType(prop.Type, genericInstances);
            }
        }
    }

    private static void CollectGenericsFromType(TsType type, Dictionary<string, TsType.Generic> instances)
    {
        switch (type)
        {
            case TsType.Generic g:
                instances.TryAdd(MonomorphisedName(g), g);
                foreach (var arg in g.TypeArguments)
                {
                    CollectGenericsFromType(arg, instances);
                }
                break;
            case TsType.Array a:
                CollectGenericsFromType(a.Element, instances);
                break;
            case TsType.Nullable n:
                CollectGenericsFromType(n.Inner, instances);
                break;
            case TsType.Dictionary d:
                CollectGenericsFromType(d.Value, instances);
                break;
            case TsType.InlineObject obj:
                foreach (var (_, fieldType) in obj.Fields)
                {
                    CollectGenericsFromType(fieldType, instances);
                }
                break;
            case TsType.Brand b:
                CollectGenericsFromType(b.Inner, instances);
                break;
        }
    }

    private static string DefaultStatusDescription(int statusCode)
    {
        return statusCode switch
        {
            200 => "Success",
            201 => "Created",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            422 => "Unprocessable Entity",
            500 => "Internal Server Error",
            _ => $"Status {statusCode}",
        };
    }

    /// <summary>
    /// Converts a TsType to a C# type name string for x-rivet-generic args.
    /// </summary>
    private static string GetCSharpTypeName(TsType type)
    {
        return type switch
        {
            TsType.Primitive p => p.CSharpType ?? (p.Format switch
            {
                "int32" => "int",
                "int64" => "long",
                "float" => "float",
                "double" => "double",
                "decimal" => "decimal",
                "uuid" => "Guid",
                "date-time" => "DateTime",
                "date" => "DateOnly",
                "time" => "TimeOnly",
                "uri" => "Uri",
                _ => p.Name switch
                {
                    "string" => "string",
                    "number" => "int",
                    "boolean" => "bool",
                    _ => p.Name,
                },
            }),
            TsType.TypeRef r => r.Name,
            TsType.Array a => $"List<{GetCSharpTypeName(a.Element)}>",
            TsType.Nullable n => $"{GetCSharpTypeName(n.Inner)}?",
            TsType.Dictionary d => $"Dictionary<string, {GetCSharpTypeName(d.Value)}>",
            TsType.Generic g => $"{g.Name}<{string.Join(", ", g.TypeArguments.Select(GetCSharpTypeName))}>",
            TsType.Brand b => b.Name,
            _ => "object",
        };
    }

    private static string UpperFirst(string s) => Naming.ToPascalCase(s);
}
