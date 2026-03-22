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
        IReadOnlyDictionary<string, TsType.StringUnion> enums,
        SecurityConfig? security)
    {
        // Collect all referenced type names transitively
        var referencedTypes = CollectReferencedTypes(endpoints, definitions);

        var paths = BuildPaths(endpoints, security);
        var schemas = BuildSchemas(endpoints, definitions, brands, enums, referencedTypes);

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

        if (ep.Description is not null)
        {
            operation["summary"] = ep.Description;
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

        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        // Request body
        if (fileParams.Count > 0)
        {
            var multipartProps = new Dictionary<string, object>();
            foreach (var fp in fileParams)
            {
                var filePropSchema = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["format"] = "binary",
                    ["x-rivet-file"] = true,
                };
                multipartProps[fp.Name] = filePropSchema;
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

            var multipartSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = multipartProps,
            };

            if (requiredFields.Count > 0)
            {
                multipartSchema["required"] = requiredFields;
            }

            if (ep.InputTypeName is not null)
            {
                multipartSchema["x-rivet-input-type"] = ep.InputTypeName;
            }

            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = true,
                ["content"] = new Dictionary<string, object>
                {
                    ["multipart/form-data"] = new Dictionary<string, object>
                    {
                        ["schema"] = multipartSchema,
                    },
                },
            };
        }
        else if (bodyParam is not null)
        {
            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = true,
                ["content"] = new Dictionary<string, object>
                {
                    ["application/json"] = new Dictionary<string, object>
                    {
                        ["schema"] = MapTsTypeToJsonSchema(bodyParam.Type),
                    },
                },
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
                respObj["content"] = new Dictionary<string, object>
                {
                    ["application/json"] = new Dictionary<string, object>
                    {
                        ["schema"] = MapTsTypeToJsonSchema(resp.DataType),
                    },
                };
            }
            else if (ep.FileContentType is not null && resp.StatusCode is >= 200 and < 300)
            {
                respObj["content"] = new Dictionary<string, object>
                {
                    [ep.FileContentType] = new Dictionary<string, object>
                    {
                        ["schema"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["format"] = "binary",
                        },
                    },
                };
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

        return operation;
    }

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

        // OpenAPI uses "integer" for int32/int64, not "number"
        var type = p.Format is "int32" or "int64" or "int16" or "uint16" or "int8" or "uint8" ? "integer" : p.Name;

        var schema = new Dictionary<string, object>
        {
            ["type"] = type,
        };

        if (p.Format is not null)
        {
            schema["format"] = p.Format;
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
        IReadOnlyDictionary<string, TsType.StringUnion> enums,
        HashSet<string> referencedTypes)
    {
        var schemas = new Dictionary<string, object>();

        foreach (var (name, def) in definitions)
        {
            if (!referencedTypes.Contains(name))
            {
                continue;
            }

            if (def.TypeParameters.Count > 0)
            {
                // Generic definitions are emitted as monomorphised variants — skip the template
                continue;
            }

            schemas[name] = BuildObjectSchema(def);
        }

        // Monomorphised generics: find all Generic type refs used by endpoints
        var genericInstances = new Dictionary<string, TsType.Generic>();
        CollectGenericInstances(endpoints, definitions, referencedTypes, genericInstances);

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
            if (!referencedTypes.Contains(name))
            {
                continue;
            }

            var brandSchema = MapTsTypeToJsonSchema(brand.Inner);
            brandSchema["x-rivet-brand"] = name;
            schemas[name] = brandSchema;
        }

        // Enums as string schemas
        foreach (var (name, su) in enums)
        {
            if (!referencedTypes.Contains(name))
            {
                continue;
            }

            schemas[name] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = su.Members.ToList(),
            };
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

            if (prop.IsDeprecated)
            {
                propSchema["deprecated"] = true;
            }

            properties[prop.Name] = propSchema;

            if (!prop.IsOptional && prop.Type is not TsType.Nullable)
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

            if (prop.IsDeprecated)
            {
                propSchema["deprecated"] = true;
            }

            properties[prop.Name] = propSchema;

            if (!prop.IsOptional && resolvedType is not TsType.Nullable)
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

    private static HashSet<string> CollectReferencedTypes(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions)
    {
        var names = new HashSet<string>();

        foreach (var ep in endpoints)
        {
            foreach (var param in ep.Params)
            {
                TsType.CollectTypeRefs(param.Type, names);
            }

            foreach (var resp in ep.Responses)
            {
                if (resp.DataType is not null)
                {
                    TsType.CollectTypeRefs(resp.DataType, names);
                }
            }
        }

        // Transitively collect refs from referenced definitions
        var queue = new Queue<string>(names);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();

            if (!definitions.TryGetValue(name, out var def))
            {
                continue;
            }

            var newRefs = new HashSet<string>();
            foreach (var prop in def.Properties)
            {
                TsType.CollectTypeRefs(prop.Type, newRefs);
            }

            foreach (var n in newRefs)
            {
                if (names.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }

        return names;
    }

    private static void CollectGenericInstances(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        HashSet<string> referencedTypes,
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
        }

        // Walk definitions' properties
        foreach (var (name, def) in definitions)
        {
            if (!referencedTypes.Contains(name))
            {
                continue;
            }

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
