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

    /// <summary>
    /// Per-Emit-call naming state. Component names must be unique per shape — the pure
    /// name suffixes are lossy in places ("Enum", "Object"), so a pre-pass assigns every
    /// distinct shape a distinct deterministic name (numeric suffix on residual collisions)
    /// that all $ref emission sites then consult. Also accumulates tagged-union variant
    /// component schemas discovered while mapping types.
    /// </summary>
    private sealed class EmitContext
    {
        /// <summary>Canonical shape hash → assigned component name for monomorphised generics.</summary>
        public Dictionary<string, string> GenericNames { get; } = [];

        /// <summary>Canonical shape hash → base component name for tagged unions.</summary>
        public Dictionary<string, string> TaggedUnionNames { get; } = [];

        /// <summary>Variant component schemas synthesized for tagged unions.</summary>
        public Dictionary<string, object> ExtraComponents { get; } = [];
    }

    [ThreadStatic]
    private static EmitContext? _ctx;

    public static string Emit(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        SecurityConfig? security,
        OpenApiDocumentInfo? documentInfo = null)
    {
        _ctx = new EmitContext();
        try
        {
            AssignComponentNames(endpoints, definitions, brands, enums, _ctx);
            return EmitCore(endpoints, definitions, brands, enums, security, documentInfo ?? new OpenApiDocumentInfo());
        }
        finally
        {
            _ctx = null;
        }
    }

    private static string EmitCore(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        SecurityConfig? security,
        OpenApiDocumentInfo documentInfo)
    {
        var paths = BuildPaths(endpoints, definitions, security);
        var schemas = BuildSchemas(endpoints, definitions, brands, enums);
        var examples = BuildComponentExamples(endpoints);

        // Tagged-union variant components synthesized while mapping types above
        if (_ctx is not null)
        {
            foreach (var (name, schema) in _ctx.ExtraComponents)
            {
                if (!schemas.TryAdd(name, schema))
                {
                    Diagnostics.Warn(
                        Diagnostics.TaggedUnionComponentCollision,
                        $"tagged-union variant component '{name}' collides with an existing schema — existing schema wins");
                }
            }
        }

        var doc = new Dictionary<string, object>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = documentInfo.Title,
                ["version"] = documentInfo.Version,
            },
        };

        if (documentInfo.Servers is { Count: > 0 })
        {
            doc["servers"] = documentInfo.Servers
                .Select(object (url) => new Dictionary<string, object> { ["url"] = url })
                .ToList();
        }

        // W4: operations carry tags — declare them in the global tags array
        // (operation-tag-defined; docs-UI consumers use it for grouping/ordering).
        var tags = endpoints
            .Select(ep => UpperFirst(ep.ControllerName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();

        if (tags.Count > 0)
        {
            doc["tags"] = tags
                .Select(object (tag) => new Dictionary<string, object> { ["name"] = tag })
                .ToList();
        }

        doc["paths"] = paths;

        var components = new Dictionary<string, object>();

        if (schemas.Count > 0)
        {
            components["schemas"] = schemas;
        }

        if (examples.Count > 0)
        {
            components["examples"] = examples;
        }

        var securitySchemes = new Dictionary<string, object>();

        if (security is not null)
        {
            securitySchemes[security.SchemeName] = security.SchemeDefinition;

            doc["security"] = new List<object>
            {
                new Dictionary<string, object> { [security.SchemeName] = Array.Empty<string>() },
            };
        }

        // W1: every scheme referenced by an endpoint-level .Secure(name) override must have a
        // matching securitySchemes component — a security requirement naming an undefined
        // scheme is rejected by consumers (oas3-operation-security-defined).
        var endpointSchemes = endpoints
            .Select(ep => ep.Security?.Scheme)
            .Where(scheme => scheme is not null)
            .Distinct()
            .OrderBy(scheme => scheme, StringComparer.Ordinal);

        foreach (var scheme in endpointSchemes)
        {
            if (securitySchemes.ContainsKey(scheme!))
            {
                continue;
            }

            Diagnostics.Warn(
                Diagnostics.UndefinedSecurityScheme,
                $"security scheme '{scheme}' is referenced by an endpoint's .Secure(\"{scheme}\") but has no definition — emitting a default bearer securityScheme component");

            securitySchemes[scheme!] = new Dictionary<string, object>
            {
                ["type"] = "http",
                ["scheme"] = "bearer",
            };
        }

        if (securitySchemes.Count > 0)
        {
            components["securitySchemes"] = securitySchemes;
        }

        if (components.Count > 0)
        {
            doc["components"] = components;
        }

        return JsonSerializer.Serialize(doc, JsonOptions);
    }

    /// <summary>
    /// OpenAPI 3.x rule: Accept, Content-Type and Authorization must not be declared
    /// as header parameters.
    /// </summary>
    private static bool IsReservedHeaderName(string name) =>
        name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object> BuildPaths(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
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
                Diagnostics.Warn(
                    Diagnostics.DuplicateEndpoint,
                    $"duplicate endpoint {ep.HttpMethod} {pathKey} — later definition wins");
            }
            var operation = BuildOperation(ep, definitions, security);
            pathItem[methodKey] = operation;
        }

        return paths;
    }

    private static Dictionary<string, object> BuildOperation(
        TsEndpointDefinition ep,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        SecurityConfig? security)
    {
        var operation = new Dictionary<string, object>
        {
            ["operationId"] = $"{ep.ControllerName}_{ep.Name}",
            ["tags"] = new List<string> { UpperFirst(ep.ControllerName) },
            // WP-1.1: carry contract/endpoint identity explicitly — the operationId/tag
            // convention is lossy for unusual casing (and breaks under hand-edits). The
            // importer prefers these extensions, with the convention as fallback.
            ["x-rivet-contract"] = ep.ControllerName,
            ["x-rivet-endpoint"] = ep.Name,
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
                        ["schema"] = MapTsTypeToJsonSchema(param.Type, $"param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"),
                    });
                    break;

                case ParamSource.Query:
                    var queryParam = new Dictionary<string, object>
                    {
                        ["name"] = param.Name,
                        ["in"] = "query",
                        // N1: honour explicit optionality (e.g. C# default values, rivet-ts
                        // isOptional) as well as type-level nullability.
                        ["required"] = param.Type is not TsType.Nullable && !param.IsOptional,
                        ["schema"] = MapTsTypeToJsonSchema(param.Type, $"param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"),
                    };
                    parameters.Add(queryParam);
                    break;

                case ParamSource.Header:
                    // OpenAPI 3.x: Accept/Content-Type/Authorization are not legal header
                    // parameters (they belong to content negotiation / securitySchemes) —
                    // diagnose and skip rather than emit an invalid spec.
                    if (IsReservedHeaderName(param.Name))
                    {
                        Diagnostics.Warn(
                            Diagnostics.ReservedHeaderParameterSkipped,
                            $"header param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}' is reserved by OpenAPI " +
                            "(Accept/Content-Type/Authorization are described by content/securitySchemes) — omitted from the spec");
                        break;
                    }

                    parameters.Add(new Dictionary<string, object>
                    {
                        ["name"] = param.Name,
                        ["in"] = "header",
                        ["required"] = param.Type is not TsType.Nullable && !param.IsOptional,
                        ["schema"] = MapTsTypeToJsonSchema(param.Type, $"param '{param.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'"),
                    });
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

            if (ep.InputTypeName is not null && definitions.ContainsKey(ep.InputTypeName))
            {
                // Named input type — emit as $ref so the schema appears once in components
                multipartSchema = new Dictionary<string, object>
                {
                    ["$ref"] = $"#/components/schemas/{ep.InputTypeName}",
                };
            }
            else
            {
                // BUG-2 (mirrors the E6 generic fallback): the TS lowerer decomposes the
                // multipart input into params and never ships the input type definition, so
                // a $ref to ep.InputTypeName would dangle — every consumer rejects that.
                // Build the multipart request schema inline from the endpoint's params instead.
                if (ep.InputTypeName is not null)
                {
                    Diagnostics.Warn(
                        Diagnostics.MultipartInputTypeMissing,
                        $"multipart input type '{ep.InputTypeName}' on endpoint '{ep.ControllerName}.{ep.Name}' " +
                        "is not present in the contract's type definitions — building the multipart request schema " +
                        "inline from the endpoint's params; fix the upstream producer to include the input type definition");
                }

                // Anonymous file upload — inline the schema. Single files emit the
                // binary File schema; collection-of-file params (List<IFormFile>,
                // FABLE_GAPS §7 item 12) emit array-of-binary — both via the File
                // primitive mapping.
                var multipartProps = new Dictionary<string, object>();
                foreach (var fp in fileParams)
                {
                    multipartProps[fp.Name] = MapTsTypeToJsonSchema(
                        fp.Type, $"file param '{fp.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'");
                }
                foreach (var ff in formFieldParams)
                {
                    multipartProps[ff.Name] = MapTsTypeToJsonSchema(ff.Type, $"form field '{ff.Name}' on endpoint '{ep.ControllerName}.{ep.Name}'");
                }

                var requiredFields = new List<string>();
                foreach (var fp in fileParams)
                {
                    // N1/E8: honour explicit optionality (rivet-ts `file?: Blob`)
                    if (!fp.IsOptional)
                    {
                        requiredFields.Add(fp.Name);
                    }
                }
                foreach (var ff in formFieldParams)
                {
                    if (ff.Type is not TsType.Nullable && !ff.IsOptional)
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

                // WP-1.1: pin the record name the importer synthesizes for this inline
                // body — without the extension it falls back to the operationId-derived
                // {fieldName}Request convention, which breaks under hand-edited ids.
                // A declared-but-undefined input type name (TS lowerer) takes precedence.
                multipartSchema["x-rivet-input-type"] = ep.InputTypeName ?? SynthesizedInputTypeName(ep);
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
                // E11: a Nullable body type means the request body is optional
                ["required"] = bodyParam.Type is not TsType.Nullable,
                ["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        [bodyContentType] = new Dictionary<string, object>
                        {
                            ["schema"] = BuildBodySchema(bodyParam.Type, ep),
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
                // E11: a Nullable body type means the request body is optional
                ["required"] = ep.RequestType is not TsType.Nullable,
                ["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        [requestTypeContentType] = new Dictionary<string, object>
                        {
                            ["schema"] = BuildBodySchema(ep.RequestType, ep),
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

            // P2 wave 5: declared response headers (string-typed v1; spec-only at runtime).
            // required is emitted only on explicit opt-in — Rivet cannot enforce presence,
            // so defaulting it would over-promise.
            if (resp.Headers is { Count: > 0 })
            {
                var headerObjs = new Dictionary<string, object>();
                foreach (var header in resp.Headers)
                {
                    var headerObj = new Dictionary<string, object>();
                    if (header.Description is not null)
                    {
                        headerObj["description"] = header.Description;
                    }

                    if (header.Required)
                    {
                        headerObj["required"] = true;
                    }

                    headerObj["schema"] = new Dictionary<string, object> { ["type"] = "string" };
                    headerObjs[header.Name] = headerObj;
                }

                respObj["headers"] = headerObjs;
            }

            if (resp.DataType is not null)
            {
                respObj["content"] = WithExamples(
                    new Dictionary<string, object>
                    {
                        ["application/json"] = new Dictionary<string, object>
                        {
                            ["schema"] = MapTsTypeToJsonSchema(resp.DataType, $"response {resp.StatusCode} on endpoint '{ep.ControllerName}.{ep.Name}'"),
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

    /// <summary>
    /// WP-1.1: the record name the importer should synthesize for an inline request-body
    /// schema (emitted as <c>x-rivet-input-type</c>). Mirrors the importer's
    /// <c>{fieldName}Request</c> convention but pins it explicitly, so the name survives
    /// operationId/tag hand-edits.
    /// </summary>
    private static string SynthesizedInputTypeName(TsEndpointDefinition ep)
        => ep.InputTypeName ?? Naming.ToPascalCaseFromSegments(ep.Name) + "Request";

    /// <summary>
    /// Maps a request-body type to its schema; inline object bodies get
    /// <c>x-rivet-input-type</c> so the importer synthesizes the same record name
    /// every loop ($ref bodies carry their name in the reference itself).
    /// </summary>
    private static Dictionary<string, object> BuildBodySchema(TsType bodyType, TsEndpointDefinition ep)
    {
        var schema = MapTsTypeToJsonSchema(bodyType, $"request body on endpoint '{ep.ControllerName}.{ep.Name}'");
        if (bodyType is TsType.InlineObject && !schema.ContainsKey("$ref"))
        {
            schema["x-rivet-input-type"] = SynthesizedInputTypeName(ep);
        }

        return schema;
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

    public static Dictionary<string, object> MapTsTypeToJsonSchema(TsType type, string? context = null)
    {
        return type switch
        {
            TsType.Primitive p => MapPrimitive(p, context),

            TsType.Nullable n => MapNullable(n, context),

            TsType.Array a => BuildArraySchema(a, context),

            TsType.Dictionary d => BuildDictionarySchema(d, context),

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

            TsType.TypeParam tp => FallbackTypeParam(tp, context),

            TsType.InlineObject obj => BuildInlineObjectSchema(obj, context),

            TsType.TaggedUnion tu => BuildTaggedUnionSchema(tu, context),

            // Undiscriminated union ([RivetUnion] wrapper): a plain oneOf — no
            // discriminator, variants may be inline primitive schemas.
            TsType.Union u => new Dictionary<string, object>
            {
                ["oneOf"] = u.Variants
                    .Select(object (variant) => MapTsTypeToJsonSchema(variant, context))
                    .ToList(),
            },

            _ => new Dictionary<string, object> { ["type"] = "object" },
        };
    }

    private static Dictionary<string, object> BuildInlineObjectSchema(TsType.InlineObject obj, string? context = null)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var (name, fieldType) in obj.Fields)
        {
            properties[name] = MapTsTypeToJsonSchema(fieldType, context);
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

    private static Dictionary<string, object> BuildTaggedUnionSchema(TsType.TaggedUnion tu, string? context = null)
    {
        // OpenAPI `discriminator` is only meaningful on a oneOf of $ref'd named schemas with
        // a tag→$ref mapping — consumers reject or ignore a discriminator over inline schemas
        // (E11). Each inline variant becomes a named component schema referenced via $ref.
        var baseName = _ctx?.TaggedUnionNames.GetValueOrDefault(InlineTypeExtractor.CanonicalHash(tu))
            ?? TsType.GetNameSuffix(tu);

        var oneOf = new List<object>();
        var mapping = new Dictionary<string, object>();

        foreach (var variant in tu.Variants)
        {
            var variantSchema = MapTsTypeToJsonSchema(variant.Type, context);

            string refPath;
            if (variantSchema.Count == 1 && variantSchema.TryGetValue("$ref", out var existingRef))
            {
                // Variant is already a named schema (TypeRef/Generic/Brand) — ref it directly.
                refPath = (string)existingRef;
            }
            else
            {
                var componentName = $"{baseName}_{UpperFirst(variant.Tag)}";
                _ctx?.ExtraComponents.TryAdd(componentName, variantSchema);
                refPath = $"#/components/schemas/{componentName}";
            }

            oneOf.Add(new Dictionary<string, object> { ["$ref"] = refPath });
            mapping[variant.Tag] = refPath;
        }

        return new Dictionary<string, object>
        {
            ["oneOf"] = oneOf,
            ["discriminator"] = new Dictionary<string, object>
            {
                ["propertyName"] = tu.Discriminator,
                ["mapping"] = mapping,
            },
        };
    }

    private static Dictionary<string, object> MapPrimitive(TsType.Primitive p, string? context = null)
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
                // The catch-all used to name no symbol at all (FABLE_GAPS §7 item 12) —
                // context threads the offending type/property or endpoint site through.
                Diagnostics.Warn(
                    Diagnostics.UnknownTypeUntypedSchema,
                    $"'unknown' type (JsonElement/JsonNode or an unmapped C# type) in OpenAPI schema{AtContext(context)} — emitting as untyped");
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

        // byte[] (FABLE_GAPS spec/wire divergence): System.Text.Json serializes byte[]
        // as a base64 string on the wire, so the schema is type: string with
        // contentEncoding: base64 — the OpenAPI 3.1 idiom (`format: byte` is the
        // deprecated 3.0 spelling). x-rivet-csharp-type carries the exact C# type
        // for lossless import round-trips.
        if (p is { Name: "string", Format: "base64" })
        {
            var base64Schema = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["contentEncoding"] = "base64",
            };
            if (p.CSharpType is not null)
            {
                base64Schema["x-rivet-csharp-type"] = p.CSharpType;
            }
            return base64Schema;
        }

        // char (P2 wave 6): System.Text.Json serializes char as a single-character
        // JSON string on the wire, so the schema is a string with both length bounds
        // pinned to 1. x-rivet-csharp-type carries the exact C# type for lossless
        // import round-trips (a plain length-1 string stays a C# string).
        if (p is { Name: "string", CSharpType: "char" })
        {
            return new Dictionary<string, object>
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = 1,
                ["x-rivet-csharp-type"] = "char",
            };
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

    private static Dictionary<string, object> MapNullable(TsType.Nullable n, string? context = null)
    {
        var inner = MapTsTypeToJsonSchema(n.Inner, context);

        // OpenAPI 3.1 / JSON Schema 2020-12: null is a type. Schemas with a single
        // type become a type array; everything else gets an explicit null branch.
        if (inner.TryGetValue("type", out var typeValue) && typeValue is string typeName)
        {
            inner["type"] = new List<string> { typeName, "null" };
            return inner;
        }

        // $ref: a sibling `type: "null"` would be ANDed with the referenced schema in
        // 2020-12, so the null alternative must be a oneOf branch instead.
        if (inner.ContainsKey("$ref"))
        {
            return new Dictionary<string, object>
            {
                ["oneOf"] = new List<object>
                {
                    inner,
                    new Dictionary<string, object> { ["type"] = "null" },
                },
            };
        }

        // Untyped schema (unknown/JsonElement) — already admits null; nothing to add.
        if (inner.Count == 0)
        {
            return inner;
        }

        // Remaining typeless composites (tagged-union oneOf, x-rivet-csharp-type
        // untyped schemas): anyOf, because an untyped branch also matches null and
        // would make a oneOf ambiguous.
        return new Dictionary<string, object>
        {
            ["anyOf"] = new List<object>
            {
                inner,
                new Dictionary<string, object> { ["type"] = "null" },
            },
        };
    }

    private static Dictionary<string, object> FallbackTypeParam(TsType.TypeParam tp, string? context = null)
    {
        Diagnostics.Warn(
            Diagnostics.UnresolvedTypeParameter,
            $"unresolved type parameter '{tp.Name}' in OpenAPI schema{AtContext(context)} — emitting as object");
        return new Dictionary<string, object> { ["type"] = "object" };
    }

    private static string AtContext(string? context)
        => context is null ? "" : $" at {context}";

    private static string MonomorphisedName(TsType.Generic g)
    {
        // The pure suffix scheme is lossy in places (4+-member unions → "Enum", 4+-field
        // inline objects → "Object"), so distinct instantiations can share a pure name.
        // The per-emit registry assigns each distinct shape a distinct deterministic name.
        if (_ctx is not null
            && _ctx.GenericNames.TryGetValue(InlineTypeExtractor.CanonicalHash(g), out var assigned))
        {
            return assigned;
        }

        return TsType.MonomorphisedName(g);
    }

    /// <summary>
    /// Pre-pass: walks every type reachable from endpoints and definitions and assigns each
    /// distinct generic instantiation / tagged union a unique component (base) name.
    /// Identical shapes share a name; distinct shapes whose pure names collide get a
    /// deterministic numeric suffix (discovery order).
    /// </summary>
    private static void AssignComponentNames(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyDictionary<string, TsTypeDefinition> definitions,
        IReadOnlyDictionary<string, TsType.Brand> brands,
        IReadOnlyDictionary<string, TsType> enums,
        EmitContext ctx)
    {
        var generics = new List<TsType.Generic>();
        var taggedUnions = new List<TsType.TaggedUnion>();

        void Walk(TsType? type)
        {
            switch (type)
            {
                case TsType.Generic g:
                    generics.Add(g);
                    foreach (var arg in g.TypeArguments) Walk(arg);
                    break;
                case TsType.TaggedUnion tu:
                    taggedUnions.Add(tu);
                    foreach (var v in tu.Variants) Walk(v.Type);
                    break;
                case TsType.Array a: Walk(a.Element); break;
                case TsType.Nullable n: Walk(n.Inner); break;
                case TsType.Dictionary d:
                    Walk(d.Value);
                    if (d.Key is not null) Walk(d.Key);
                    break;
                case TsType.Brand b: Walk(b.Inner); break;
                case TsType.InlineObject obj:
                    foreach (var (_, fieldType) in obj.Fields) Walk(fieldType);
                    break;
            }
        }

        foreach (var ep in endpoints)
        {
            foreach (var p in ep.Params) Walk(p.Type);
            Walk(ep.ReturnType);
            foreach (var r in ep.Responses) Walk(r.DataType);
            Walk(ep.RequestType);
        }

        foreach (var (_, def) in definitions)
        {
            if (def.Type is not null)
            {
                Walk(def.Type);
                continue;
            }

            foreach (var prop in def.Properties) Walk(prop.Type);
        }

        foreach (var (_, brand) in brands) Walk(brand.Inner);
        foreach (var (_, enumType) in enums) Walk(enumType);

        // Names already claimed by emitted definition/brand/enum schemas
        var usedNames = new HashSet<string>(definitions
            .Where(kv => kv.Value.TypeParameters.Count == 0)
            .Select(kv => kv.Key));
        usedNames.UnionWith(brands.Keys);
        usedNames.UnionWith(enums.Keys);

        foreach (var g in generics)
        {
            var hash = InlineTypeExtractor.CanonicalHash(g);
            if (ctx.GenericNames.ContainsKey(hash)) continue;
            ctx.GenericNames[hash] = ClaimName(TsType.MonomorphisedName(g), usedNames);
        }

        // Tagged unions that ARE a named type alias keep the alias as base name
        foreach (var (name, def) in definitions)
        {
            if (def.Type is TsType.TaggedUnion aliased)
            {
                ctx.TaggedUnionNames.TryAdd(InlineTypeExtractor.CanonicalHash(aliased), name);
            }
        }

        foreach (var tu in taggedUnions)
        {
            var hash = InlineTypeExtractor.CanonicalHash(tu);
            if (ctx.TaggedUnionNames.ContainsKey(hash)) continue;
            ctx.TaggedUnionNames[hash] = ClaimName(TsType.GetNameSuffix(tu), usedNames);
        }
    }

    private static string ClaimName(string pureName, HashSet<string> usedNames)
    {
        var name = pureName;
        var i = 2;
        while (!usedNames.Add(name))
        {
            name = pureName + i;
            i++;
        }

        return name;
    }

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

            schemas[name] = BuildDefinitionSchema(def);
        }

        // Monomorphised generics: find all Generic type refs used across definitions and endpoints
        var genericInstances = new Dictionary<string, TsType.Generic>();
        CollectGenericInstances(endpoints, definitions, genericInstances);

        // E6: templates are skipped during collection (their unresolved Generic refs are
        // garbage like PagedResult_T), so nested instantiations only surface when a
        // template's properties are resolved against concrete type args. Iterate to a
        // fixpoint so e.g. Wrapper<X> { PagedResult<X> } registers PagedResult_X too.
        var pending = new Queue<TsType.Generic>(genericInstances.Values);
        while (pending.Count > 0)
        {
            var instance = pending.Dequeue();
            if (!definitions.TryGetValue(instance.Name, out var template))
            {
                continue;
            }

            var instanceMap = new Dictionary<string, TsType>();
            for (var i = 0; i < Math.Min(template.TypeParameters.Count, instance.TypeArguments.Count); i++)
            {
                instanceMap[template.TypeParameters[i]] = instance.TypeArguments[i];
            }

            var discovered = new Dictionary<string, TsType.Generic>();
            if (template.Type is not null)
            {
                CollectGenericsFromType(ResolveTypeParams(template.Type, instanceMap), discovered);
            }
            else
            {
                foreach (var prop in template.Properties)
                {
                    CollectGenericsFromType(ResolveTypeParams(prop.Type, instanceMap), discovered);
                }
            }

            foreach (var (discoveredName, discoveredInstance) in discovered)
            {
                if (genericInstances.TryAdd(discoveredName, discoveredInstance))
                {
                    pending.Enqueue(discoveredInstance);
                }
            }
        }

        foreach (var (monoName, generic) in genericInstances)
        {
            if (!definitions.TryGetValue(generic.Name, out var genericDef))
            {
                // E6: a generic instantiation whose template is absent from definitions used
                // to emit a $ref with no matching component — a dangling reference every
                // consumer rejects (GAP-1). Never emit a dangling $ref: warn loudly and
                // synthesize a valid free-form fallback component under the $ref'd name.
                Diagnostics.Warn(
                    Diagnostics.GenericTemplateMissing,
                    $"generic template '{generic.Name}' (instantiated as '{monoName}') is not present in the contract's type definitions — emitting a free-form object schema; fix the upstream producer to include the template definition");

                schemas[monoName] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["description"] =
                        $"Unresolved generic instantiation of '{generic.Name}' — template definition missing from source contract",
                };
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
            var brandSchema = MapTsTypeToJsonSchema(brand.Inner, $"brand '{name}'");
            brandSchema["x-rivet-brand"] = name;
            schemas[name] = brandSchema;
        }

        // Enums as schemas
        foreach (var (name, enumType) in enums)
        {
            schemas[name] = MapTsTypeToJsonSchema(enumType, $"enum '{name}'");
        }

        return schemas;
    }

    private static Dictionary<string, object> BuildDefinitionSchema(TsTypeDefinition def)
    {
        if (def.Type is not null)
        {
            var schema = MapTsTypeToJsonSchema(def.Type, $"type '{def.Name}'");
            if (def.Description is not null)
            {
                schema["description"] = def.Description;
            }

            return schema;
        }

        return BuildObjectSchema(def.Properties, def.Description, def.Name);
    }

    private static Dictionary<string, object> BuildObjectSchema(
        IReadOnlyList<TsPropertyDefinition> propertiesDefinition,
        string? description = null,
        string? typeName = null)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in propertiesDefinition)
        {
            var propSchema = MapTsTypeToJsonSchema(
                prop.Type,
                typeName is null ? $"property '{prop.Name}'" : $"property '{typeName}.{prop.Name}'");
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

        if (description is not null)
        {
            schema["description"] = description;
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

    private static Dictionary<string, object> BuildArraySchema(TsType.Array a, string? context = null)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "array",
            ["items"] = MapTsTypeToJsonSchema(a.Element, context),
        };

        // Propagate CSharpType from inner unknown (JsonArray) to parent schema.
        // "object" elements are excluded: their untyped emission is deliberate and
        // carries no sidecar — stamping the parent would re-type the whole array.
        if (a.Element is TsType.Primitive { Name: "unknown", CSharpType: not (null or "object") } p)
        {
            schema["x-rivet-csharp-type"] = p.CSharpType;
        }

        return schema;
    }

    private static Dictionary<string, object> BuildDictionarySchema(TsType.Dictionary d, string? context = null)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = MapTsTypeToJsonSchema(d.Value, context),
        };

        // Non-string key types constrain the keys via propertyNames (OpenAPI 3.1 /
        // JSON Schema 2020-12): enum/brand keys $ref their component schema; primitive
        // keys stay string-typed with the original format, x-rivet-csharp-type pinning
        // the exact C# key type for import round-trips.
        if (d.Key is not null)
        {
            schema["propertyNames"] = BuildDictionaryKeySchema(d.Key, context);
        }

        // Propagate CSharpType from inner unknown (JsonObject) to parent schema.
        // "object" values are excluded: their untyped emission is deliberate and
        // carries no sidecar — stamping the parent would re-type the whole dictionary.
        if (d.Value is TsType.Primitive { Name: "unknown", CSharpType: not (null or "object") } p)
        {
            schema["x-rivet-csharp-type"] = p.CSharpType;
        }

        return schema;
    }

    private static Dictionary<string, object> BuildDictionaryKeySchema(TsType key, string? context)
    {
        // Primitive keys are built inline rather than via MapPrimitive: property names
        // are always strings, but a numeric format (int32, …) would flip MapPrimitive's
        // emitted type to integer — invalid under propertyNames.
        if (key is TsType.Primitive p)
        {
            var schema = new Dictionary<string, object> { ["type"] = "string" };
            if (p.Format is not null)
            {
                schema["format"] = p.Format;
            }
            // char keys (P2 wave 6): single-character property names on the wire —
            // same length-1 shape as the char property schema.
            if (p.CSharpType is "char")
            {
                schema["minLength"] = 1;
                schema["maxLength"] = 1;
            }
            if (p.CSharpType is not null)
            {
                schema["x-rivet-csharp-type"] = p.CSharpType;
            }
            return schema;
        }

        // TypeRef (enum) / Brand → $ref to the named component schema
        return MapTsTypeToJsonSchema(key, context);
    }

    private static Dictionary<string, object> BuildMonomorphisedSchema(
        TsTypeDefinition genericDef,
        Dictionary<string, TsType> typeParamMap)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        if (genericDef.Type is not null)
        {
            return MapTsTypeToJsonSchema(ResolveTypeParams(genericDef.Type, typeParamMap), $"type '{genericDef.Name}'");
        }

        foreach (var prop in genericDef.Properties)
        {
            var resolvedType = ResolveTypeParams(prop.Type, typeParamMap);
            var propSchema = MapTsTypeToJsonSchema(resolvedType, $"property '{genericDef.Name}.{prop.Name}'");
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
            // E6: skip generic TEMPLATE definitions — their Generic refs still contain
            // unresolved TypeParams and used to register garbage Foo_T instances. Only
            // concrete instantiations monomorphise (nested ones via the fixpoint pass).
            if (def.TypeParameters.Count > 0)
            {
                continue;
            }

            if (def.Type is not null)
            {
                CollectGenericsFromType(def.Type, genericInstances);
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
                if (d.Key is not null)
                {
                    CollectGenericsFromType(d.Key, instances);
                }
                break;
            case TsType.InlineObject obj:
                foreach (var (_, fieldType) in obj.Fields)
                {
                    CollectGenericsFromType(fieldType, instances);
                }
                break;
            case TsType.TaggedUnion tu:
                foreach (var variant in tu.Variants)
                {
                    CollectGenericsFromType(variant.Type, instances);
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
            TsType.TaggedUnion => "object",
            _ => "object",
        };
    }

    private static string UpperFirst(string s) => Naming.ToPascalCase(s);
}
