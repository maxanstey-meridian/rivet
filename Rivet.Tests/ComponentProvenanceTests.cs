using System.Text.Json;
using Rivet.Tool;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

public sealed class ComponentProvenanceTests
{
    [Fact]
    public void Imported_component_keys_and_refs_preserve_exact_punctuation_and_case()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "order.item": {
                "type": "object",
                "properties": { "value": { "type": "string" } },
                "required": ["value"]
            },
            "Order-Item": {
                "type": "object",
                "properties": { "nested": { "$ref": "#/components/schemas/order.item" } },
                "required": ["nested"]
            },
            "ORDER_ITEM": {
                "type": "object",
                "properties": {
                    "nested": { "$ref": "#/components/schemas/Order-Item" },
                    "slash": { "$ref": "#/components/schemas/order~1item" }
                },
                "required": ["nested"]
            },
            "order/item": {
                "type": "object",
                "properties": { "value": { "type": "string" } },
                "required": ["value"]
            }
            """,
            paths: """
            "/orders": {
                "get": {
                    "operationId": "getOrders",
                    "tags": ["Orders"],
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/ORDER_ITEM" }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        using var emitted = ImportCompileWalkContractJsonAndEmit(spec);
        var schemas = emitted.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.True(schemas.TryGetProperty("order.item", out _));
        Assert.True(schemas.TryGetProperty("Order-Item", out var orderItem));
        Assert.True(schemas.TryGetProperty("ORDER_ITEM", out var upperOrderItem));
        Assert.True(schemas.TryGetProperty("order/item", out _));
        Assert.Equal(
            "#/components/schemas/order.item",
            orderItem
                .GetProperty("properties")
                .GetProperty("nested")
                .GetProperty("$ref")
                .GetString()
        );
        Assert.Equal(
            "#/components/schemas/Order-Item",
            upperOrderItem
                .GetProperty("properties")
                .GetProperty("nested")
                .GetProperty("$ref")
                .GetString()
        );
        Assert.Equal(
            "#/components/schemas/order~1item",
            upperOrderItem
                .GetProperty("properties")
                .GetProperty("slash")
                .GetProperty("$ref")
                .GetString()
        );
        Assert.Equal(
            "#/components/schemas/ORDER_ITEM",
            emitted
                .RootElement.GetProperty("paths")
                .GetProperty("/orders")
                .GetProperty("get")
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
    }

    [Fact]
    public void Imported_enums_brands_and_composition_wrappers_preserve_component_identity()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "pet.kind": {
                "type": "string",
                "enum": ["cat", "dog"]
            },
            "pet-id": {
                "type": "string",
                "x-rivet-brand": "PetId"
            },
            "pet.choice": {
                "oneOf": [
                    { "$ref": "#/components/schemas/pet.kind" },
                    { "$ref": "#/components/schemas/pet-id" }
                ]
            },
            "PetEnvelope": {
                "type": "object",
                "properties": {
                    "choice": { "$ref": "#/components/schemas/pet.choice" }
                },
                "required": ["choice"]
            }
            """,
            paths: """
            "/pets": {
                "get": {
                    "operationId": "getPets",
                    "tags": ["Pets"],
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": {
                                    "schema": { "$ref": "#/components/schemas/PetEnvelope" }
                                }
                            }
                        }
                    }
                }
            }
            """
        );

        using var emitted = ImportCompileWalkAndEmit(spec);
        var schemas = emitted.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.True(schemas.TryGetProperty("pet.kind", out _));
        Assert.True(schemas.TryGetProperty("pet-id", out _));
        Assert.True(schemas.TryGetProperty("pet.choice", out var choice));
        Assert.Equal(
            ["#/components/schemas/pet.kind", "#/components/schemas/pet-id"],
            choice
                .GetProperty("oneOf")
                .EnumerateArray()
                .Select(branch => branch.GetProperty("$ref").GetString()!)
                .ToArray()
        );
    }

    [Fact]
    public void Imported_parameter_carrier_is_synthetic_and_not_emitted_as_a_component()
    {
        var spec = CompilationHelper.BuildSpec(
            paths: """
            "/pets": {
                "get": {
                    "operationId": "searchPets",
                    "tags": ["Pets"],
                    "parameters": [
                        { "name": "kind", "in": "query", "required": true, "schema": { "type": "string" } },
                        { "name": "limit", "in": "query", "required": false, "schema": { "type": "integer", "format": "int32" } }
                    ],
                    "responses": { "204": { "description": "No Content" } }
                }
            }
            """
        );

        using var emitted = ImportCompileWalkAndEmit(spec);

        Assert.False(emitted.RootElement.TryGetProperty("components", out _));
        var parameters = emitted
            .RootElement.GetProperty("paths")
            .GetProperty("/pets")
            .GetProperty("get")
            .GetProperty("parameters");
        Assert.Equal(
            ["kind", "limit"],
            parameters.EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray()
        );
    }

    [Fact]
    public void Imported_inline_object_and_enum_helpers_are_inlined_without_component_leaks()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "Pet": {
                "type": "object",
                "properties": {
                    "details": {
                        "type": "object",
                        "properties": { "age": { "type": "integer", "format": "int32" } },
                        "required": ["age"]
                    },
                    "kind": {
                        "type": "string",
                        "enum": ["cat", "dog"]
                    }
                },
                "required": ["details", "kind"]
            }
            """,
            paths: """
            "/pets": {
                "get": {
                    "operationId": "getPet",
                    "tags": ["Pets"],
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } }
                            }
                        }
                    }
                }
            }
            """
        );

        using var emitted = ImportCompileWalkAndEmit(spec);
        var schemas = emitted.RootElement.GetProperty("components").GetProperty("schemas");
        var petProperties = schemas.GetProperty("Pet").GetProperty("properties");

        Assert.Single(schemas.EnumerateObject());
        Assert.Equal(
            "object",
            petProperties.GetProperty("details").GetProperty("type").GetString()
        );
        Assert.Equal(
            ["cat", "dog"],
            petProperties
                .GetProperty("kind")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray()
        );
    }

    [Fact]
    public void Authored_rivet_types_remain_named_components()
    {
        const string source = """
            using Rivet;

            namespace Authored;

            [RivetType] public sealed record Pet(string Name);
            [RivetType] public enum PetKind { Cat, Dog }
            [RivetType] public sealed record PetId(string Value);
            """;

        using var emitted = CompilationHelper.EmitOpenApi(source);
        var schemas = emitted.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.True(schemas.TryGetProperty("Pet", out _));
        Assert.True(schemas.TryGetProperty("PetKind", out _));
        Assert.True(schemas.TryGetProperty("PetId", out _));
    }

    [Fact]
    public void Generated_component_metadata_survives_contract_json()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "pet.record": {
                "type": "object",
                "properties": { "kind": { "$ref": "#/components/schemas/pet-kind" } },
                "required": ["kind"]
            },
            "pet-kind": {
                "type": "string",
                "enum": ["cat", "dog"]
            }
            """
        );
        var imported = CompilationHelper.Import(spec, "ContractMetadata");
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);

        var json = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            endpoints
        );
        var read = JsonContractReader.Read(json);

        Assert.Equal("pet.record", Assert.Single(read.Types).Metadata?.ComponentId);
        Assert.Equal(
            "pet-kind",
            Assert.IsType<TsType.StringUnion>(Assert.Single(read.Enums).Value).Metadata?.ComponentId
        );
    }

    [Fact]
    public void Imported_named_empty_object_remains_an_exact_component()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "empty.group": {
                "type": "object",
                "properties": {}
            },
            "Holder": {
                "type": "object",
                "properties": { "group": { "$ref": "#/components/schemas/empty.group" } },
                "required": ["group"]
            }
            """
        );

        using var emitted = ImportCompileWalkAndEmit(spec);
        var schemas = emitted.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.True(schemas.TryGetProperty("empty.group", out var empty));
        Assert.Equal("object", empty.GetProperty("type").GetString());
        Assert.Empty(empty.GetProperty("properties").EnumerateObject());
        Assert.Equal(
            "#/components/schemas/empty.group",
            schemas
                .GetProperty("Holder")
                .GetProperty("properties")
                .GetProperty("group")
                .GetProperty("$ref")
                .GetString()
        );
    }

    [Fact]
    public void Imported_named_empty_object_preserves_description_without_runtime_shape()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "AcceptDisputeRequest": {
                "type": "object",
                "properties": {},
                "description": "Defines the request parameters."
            }
            """
        );

        var imported = CompilationHelper.Import(spec, "EmptyDescription");
        var source = CompilationHelper.FindFile(imported, "AcceptDisputeRequest.cs");
        Assert.Contains("[RivetDescription(\"Defines the request parameters.\")]", source);
        Assert.Contains("record AcceptDisputeRequest()", source);
        Assert.Contains("[JsonExtensionData]", source);

        using var emitted = ImportCompileWalkContractJsonAndEmit(spec);
        var schema = emitted
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("AcceptDisputeRequest");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
        Assert.Equal(
            "Defines the request parameters.",
            schema.GetProperty("description").GetString()
        );
    }

    [Fact]
    public void Imported_typeless_named_component_remains_intentionally_untyped_with_description()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "ReservedDomains": {},
            "TemplateBulkRecipients": {
                "description": "Template bulk recipients"
            }
            """
        );

        var imported = CompilationHelper.Import(spec, "TypelessComponents");
        Assert.DoesNotContain(
            imported.Files,
            file =>
                file.FileName.EndsWith("ReservedDomains.cs")
                || file.FileName.EndsWith("TemplateBulkRecipients.cs")
        );
        Assert.Contains(
            "RivetGeneratedSchema",
            CompilationHelper.FindFile(imported, "RivetScalarSchemas.cs")
        );

        using var emitted = ImportCompileWalkContractJsonAndEmit(spec);
        var schemas = emitted.RootElement.GetProperty("components").GetProperty("schemas");
        var reserved = schemas.GetProperty("ReservedDomains");
        var recipients = schemas.GetProperty("TemplateBulkRecipients");

        Assert.Empty(reserved.EnumerateObject());
        Assert.Single(recipients.EnumerateObject());
        Assert.Equal("Template bulk recipients", recipients.GetProperty("description").GetString());
        Assert.False(recipients.TryGetProperty("type", out _));
    }

    [Fact]
    public void Imported_named_string_scalar_remains_primitive_and_preserves_property_ref()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "session.token": {
                "type": "string",
                "description": "Opaque session token",
                "default": "pending",
                "examples": ["active"],
                "deprecated": true,
                "readOnly": true,
                "writeOnly": true,
                "minLength": 3,
                "maxLength": 40,
                "pattern": "^[a-z]+$"
            },
            "Session": {
                "type": "object",
                "properties": {
                    "token": { "$ref": "#/components/schemas/session.token" }
                },
                "required": ["token"]
            }
            """
        );

        var imported = CompilationHelper.Import(spec, "NamedScalar");
        var holder = CompilationHelper.FindFile(imported, "Session.cs");
        Assert.Contains("string Token", holder);
        Assert.DoesNotContain("record SessionToken", imported.Files.Select(file => file.Content));

        using var emitted = ImportCompileWalkAndEmit(spec);
        var schemas = emitted.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.True(
            JsonElement.DeepEquals(
                JsonDocument
                    .Parse(spec)
                    .RootElement.GetProperty("components")
                    .GetProperty("schemas")
                    .GetProperty("session.token"),
                schemas.GetProperty("session.token")
            )
        );
        Assert.Equal(
            "#/components/schemas/session.token",
            schemas
                .GetProperty("Session")
                .GetProperty("properties")
                .GetProperty("token")
                .GetProperty("$ref")
                .GetString()
        );
    }

    [Fact]
    public void Imported_named_scalars_preserve_refs_on_every_operation_surface()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "slug-id": { "type": "string", "minLength": 2, "pattern": "^[a-z-]+$" },
            "PageCount": { "type": "integer", "minimum": 1, "maximum": 100 },
            "event.time": { "type": "string", "format": "date-time" },
            "nullable-code": { "type": ["string", "null"], "description": "Optional code" },
            "state-code": {
                "type": ["string", "null"],
                "format": "state",
                "description": "Lifecycle state",
                "enum": ["ready", "held"],
                "default": "ready",
                "examples": ["held"],
                "deprecated": true,
                "readOnly": true,
                "writeOnly": true,
                "minLength": 4
            },
            "state-alias": { "$ref": "#/components/schemas/state-code" },
            "ScalarHolder": {
                "type": "object",
                "properties": {
                    "slug": { "$ref": "#/components/schemas/slug-id" },
                    "count": { "$ref": "#/components/schemas/PageCount" },
                    "time": { "$ref": "#/components/schemas/event.time" },
                    "nullableCode": { "$ref": "#/components/schemas/nullable-code" },
                    "state": { "$ref": "#/components/schemas/state-code" },
                    "stateAlias": { "$ref": "#/components/schemas/state-alias" }
                },
                "required": ["slug", "count", "time", "nullableCode", "state", "stateAlias"]
            }
            """,
            paths: """
            "/events/{eventId}": {
                "post": {
                    "operationId": "createEvent",
                    "parameters": [
                        { "name": "eventId", "in": "path", "required": true, "schema": { "$ref": "#/components/schemas/slug-id" } },
                        { "name": "slug", "in": "query", "required": true, "schema": { "$ref": "#/components/schemas/slug-id" } },
                        { "name": "page", "in": "header", "required": false, "schema": { "$ref": "#/components/schemas/PageCount" } },
                        { "name": "state", "in": "cookie", "required": true, "schema": { "$ref": "#/components/schemas/state-alias" } }
                    ],
                    "requestBody": {
                        "required": true,
                        "content": {
                            "application/json": { "schema": { "$ref": "#/components/schemas/nullable-code" } }
                        }
                    },
                    "responses": {
                        "201": {
                            "description": "Created",
                            "content": {
                                "application/json": { "schema": { "$ref": "#/components/schemas/event.time" } }
                            }
                        },
                        "409": {
                            "description": "Conflict",
                            "content": {
                                "application/json": { "schema": { "$ref": "#/components/schemas/state-code" } }
                            }
                        }
                    }
                }
            }
            """
        );

        using var emitted = ImportCompileWalkContractJsonAndEmit(spec);
        var root = emitted.RootElement;
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var sourceSchemas = JsonDocument
            .Parse(spec)
            .RootElement.GetProperty("components")
            .GetProperty("schemas");
        foreach (var source in sourceSchemas.EnumerateObject())
        {
            Assert.True(
                JsonElement.DeepEquals(source.Value, schemas.GetProperty(source.Name)),
                $"Scalar component '{source.Name}' changed.\nExpected: {source.Value}\nActual: {schemas.GetProperty(source.Name)}"
            );
        }

        var operation = root.GetProperty("paths")
            .GetProperty("/events/{eventId}")
            .GetProperty("post");
        Assert.Equal(
            new string?[]
            {
                "#/components/schemas/slug-id",
                "#/components/schemas/slug-id",
                "#/components/schemas/PageCount",
                "#/components/schemas/state-alias",
            },
            operation
                .GetProperty("parameters")
                .EnumerateArray()
                .Select(parameter =>
                    parameter.GetProperty("schema").GetProperty("$ref").GetString()
                )
                .ToArray()
        );
        Assert.All(
            operation.GetProperty("parameters").EnumerateArray(),
            parameter => Assert.Single(parameter.GetProperty("schema").EnumerateObject())
        );
        Assert.Equal(
            "#/components/schemas/nullable-code",
            operation
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
        Assert.Equal(
            "#/components/schemas/event.time",
            operation
                .GetProperty("responses")
                .GetProperty("201")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
        Assert.Equal(
            "#/components/schemas/state-code",
            operation
                .GetProperty("responses")
                .GetProperty("409")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()
        );
    }

    [Theory]
    [InlineData("{ \"type\": \"string\", \"const\": \"fixed\" }")]
    [InlineData("{ \"type\": [\"string\", \"integer\"], \"enum\": [\"one\", 2] }")]
    public void Unsupported_named_scalar_algebra_fails_loudly(string schema)
    {
        var spec = CompilationHelper.BuildSpec(schemas: $"\"Unsupported\": {schema}");

        var imported = CompilationHelper.Import(spec, "UnsupportedScalar");

        Assert.Contains(
            imported.Warnings,
            warning =>
                warning.Contains(Diagnostics.ImportNamedScalarAlgebraUnsupported)
                && warning.Contains("Named scalar component 'Unsupported'")
        );
    }

    [Fact]
    public void Imported_discriminated_composition_preserves_exact_variant_component_ids()
    {
        var spec = CompilationHelper.BuildSpec(
            schemas: """
            "shape.base": {
                "oneOf": [
                    { "$ref": "#/components/schemas/shape-circle" },
                    { "$ref": "#/components/schemas/shape.square" }
                ],
                "discriminator": {
                    "propertyName": "kind",
                    "mapping": {
                        "circle": "#/components/schemas/shape-circle",
                        "square": "#/components/schemas/shape.square"
                    }
                }
            },
            "shape-circle": {
                "type": "object",
                "properties": {
                    "kind": { "type": "string", "enum": ["circle"] },
                    "radius": { "type": "number", "format": "double" }
                },
                "required": ["kind", "radius"]
            },
            "shape.square": {
                "type": "object",
                "properties": {
                    "kind": { "type": "string", "enum": ["square"] },
                    "side": { "type": "number", "format": "double" }
                },
                "required": ["kind", "side"]
            }
            """,
            paths: """
            "/shape": {
                "get": {
                    "operationId": "getShape",
                    "tags": ["Shapes"],
                    "responses": {
                        "200": {
                            "description": "OK",
                            "content": {
                                "application/json": { "schema": { "$ref": "#/components/schemas/shape.base" } }
                            }
                        }
                    }
                }
            }
            """
        );

        using var emitted = ImportCompileWalkAndEmit(spec);
        var schemas = emitted.RootElement.GetProperty("components").GetProperty("schemas");
        var shape = schemas.GetProperty("shape.base");

        Assert.True(schemas.TryGetProperty("shape-circle", out _));
        Assert.True(schemas.TryGetProperty("shape.square", out _));
        Assert.Equal(
            ["#/components/schemas/shape-circle", "#/components/schemas/shape.square"],
            shape
                .GetProperty("oneOf")
                .EnumerateArray()
                .Select(branch => branch.GetProperty("$ref").GetString()!)
                .ToArray()
        );
        Assert.Equal(
            "#/components/schemas/shape-circle",
            shape
                .GetProperty("discriminator")
                .GetProperty("mapping")
                .GetProperty("circle")
                .GetString()
        );
    }

    private static JsonDocument ImportCompileWalkAndEmit(string spec)
    {
        var imported = CompilationHelper.Import(spec, "ComponentIdentity");
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        return JsonDocument.Parse(
            OpenApiEmitter.Emit(
                endpoints,
                walker.Definitions,
                walker.Brands,
                walker.Enums,
                security: null
            )
        );
    }

    private static JsonDocument ImportCompileWalkContractJsonAndEmit(string spec)
    {
        var imported = CompilationHelper.Import(spec, "ScalarContractJson");
        var compilation = CompilationHelper.CompileImportResult(imported);
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        var contractJson = ContractEmitter.Emit(
            walker.Definitions.ToDictionary(),
            walker.Enums.ToDictionary(),
            endpoints
        );
        var read = JsonContractReader.Read(contractJson);
        return JsonDocument.Parse(
            OpenApiEmitter.Emit(
                read.Endpoints,
                read.Types.ToDictionary(type => type.Name),
                read.Brands,
                read.Enums,
                security: null
            )
        );
    }
}
