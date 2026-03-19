using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Emits an OpenAPI 3.1 JSON spec from the Rivet model.
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
            ["openapi"] = "3.1.0",
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
            var operation = BuildOperation(ep, security);
            pathItem[ep.HttpMethod.ToLowerInvariant()] = operation;
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
        TsEndpointParam? fileParam = null;

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
                    parameters.Add(new Dictionary<string, object>
                    {
                        ["name"] = param.Name,
                        ["in"] = "query",
                        ["required"] = true,
                        ["schema"] = MapTsTypeToJsonSchema(param.Type),
                    });
                    break;

                case ParamSource.Body:
                    bodyParam = param;
                    break;

                case ParamSource.File:
                    fileParam = param;
                    break;
            }
        }

        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        // Request body
        if (fileParam is not null)
        {
            operation["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = true,
                ["content"] = new Dictionary<string, object>
                {
                    ["multipart/form-data"] = new Dictionary<string, object>
                    {
                        ["schema"] = new Dictionary<string, object>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object>
                            {
                                [fileParam.Name] = new Dictionary<string, object>
                                {
                                    ["type"] = "string",
                                    ["format"] = "binary",
                                },
                            },
                        },
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

            responses[resp.StatusCode.ToString()] = respObj;
        }

        if (responses.Count > 0)
        {
            operation["responses"] = responses;
        }

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

            TsType.Array a => new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = MapTsTypeToJsonSchema(a.Element),
            },

            TsType.Dictionary d => new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = MapTsTypeToJsonSchema(d.Value),
            },

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

            TsType.Brand b => MapTsTypeToJsonSchema(b.Inner),

            // TypeParam should be resolved before reaching here; fallback to object
            _ => new Dictionary<string, object> { ["type"] = "object" },
        };
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

        return new Dictionary<string, object>
        {
            ["type"] = p.Name,
        };
    }

    private static Dictionary<string, object> MapNullable(TsType.Nullable n)
    {
        // Primitive inner → type array: ["string", "null"]
        if (n.Inner is TsType.Primitive p)
        {
            return new Dictionary<string, object>
            {
                ["type"] = new List<string> { p.Name, "null" },
            };
        }

        // Ref / complex inner → oneOf
        return new Dictionary<string, object>
        {
            ["oneOf"] = new List<object>
            {
                MapTsTypeToJsonSchema(n.Inner),
                new Dictionary<string, object> { ["type"] = "null" },
            },
        };
    }

    private static string MonomorphisedName(TsType.Generic g)
    {
        return g.Name + string.Concat(g.TypeArguments.Select(GetTypeNameSuffix));
    }

    private static string GetTypeNameSuffix(TsType type)
    {
        return type switch
        {
            TsType.TypeRef r => r.Name,
            TsType.TypeParam tp => tp.Name,
            TsType.Primitive p => UpperFirst(p.Name),
            TsType.Generic g => MonomorphisedName(g),
            TsType.Array a => GetTypeNameSuffix(a.Element) + "Array",
            TsType.Nullable n => GetTypeNameSuffix(n.Inner) + "Nullable",
            TsType.Brand b => b.Name,
            _ => "Unknown",
        };
    }

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

            schemas[monoName] = BuildMonomorphisedSchema(genericDef, typeParamMap);
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
            properties[prop.Name] = MapTsTypeToJsonSchema(prop.Type);

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

    private static Dictionary<string, object> BuildMonomorphisedSchema(
        TsTypeDefinition genericDef,
        Dictionary<string, TsType> typeParamMap)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in genericDef.Properties)
        {
            var resolvedType = ResolveTypeParams(prop.Type, typeParamMap);
            properties[prop.Name] = MapTsTypeToJsonSchema(resolvedType);

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
    {
        return type switch
        {
            TsType.TypeParam tp when map.TryGetValue(tp.Name, out var resolved) => resolved,
            TsType.Array a => new TsType.Array(ResolveTypeParams(a.Element, map)),
            TsType.Nullable n => new TsType.Nullable(ResolveTypeParams(n.Inner, map)),
            TsType.Dictionary d => new TsType.Dictionary(ResolveTypeParams(d.Value, map)),
            _ => type,
        };
    }

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

            foreach (var prop in def.Properties)
            {
                var before = names.Count;
                TsType.CollectTypeRefs(prop.Type, names);

                if (names.Count > before)
                {
                    foreach (var n in names)
                    {
                        if (!queue.Contains(n))
                        {
                            queue.Enqueue(n);
                        }
                    }
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

    private static string UpperFirst(string s)
    {
        if (string.IsNullOrEmpty(s) || char.IsUpper(s[0]))
        {
            return s;
        }

        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}
