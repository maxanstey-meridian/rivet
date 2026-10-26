using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Analysis;
using Rivet.Tool.Emit;
using Rivet.Tool.Model;

namespace Rivet.Tests;

/// <summary>
/// P2 wave 4: C# polymorphism — [JsonPolymorphic]/[JsonDerivedType] base types lower
/// to a TaggedUnion (oneOf + discriminator + mapping) instead of silently flattening,
/// and the importer reverses oneOf + discriminator + mapping back to the attributes.
///
/// The spec must match System.Text.Json's wire semantics: serializing AS the base
/// type writes the discriminator first with the registration's tag; serializing a
/// derived type directly (as its own static type) writes NO discriminator.
/// </summary>
public sealed class PolymorphismTests
{
    // ════════════════════════ Walker / emitter pins ════════════════════════

    private const string ShapesSource = """
        using System.Text.Json.Serialization;
        using Rivet;

        namespace Test;

        [RivetType]
        [JsonPolymorphic]
        [JsonDerivedType(typeof(Circle), "circle")]
        [JsonDerivedType(typeof(Square), "square")]
        public abstract record Shape(string Id);

        public sealed record Circle(string Id, double Radius) : Shape(Id);
        public sealed record Square(string Id, double Side) : Shape(Id);

        [RivetContract]
        public static class ShapesContract
        {
            public static readonly RouteDefinition<Shape> GetShape =
                Define.Get<Shape>("/api/shapes/{id}");
        }
        """;

    [Fact]
    public void Polymorphic_Base_Emits_OneOf_Discriminator_And_Mapping()
    {
        using var doc = CompilationHelper.EmitOpenApi(ShapesSource);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // Base = oneOf of $ref'd named variant components, no flattened properties
        var shape = schemas.GetProperty("Shape");
        Assert.False(
            shape.TryGetProperty("properties", out _),
            "the base must not flatten to its own property surface"
        );
        var oneOf = shape.GetProperty("oneOf");
        Assert.Equal(2, oneOf.GetArrayLength());
        Assert.Equal("#/components/schemas/Shape_Circle", oneOf[0].GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/Shape_Square", oneOf[1].GetProperty("$ref").GetString());

        // Discriminator: default $type, complete tag → $ref mapping
        var discriminator = shape.GetProperty("discriminator");
        Assert.Equal("$type", discriminator.GetProperty("propertyName").GetString());
        var mapping = discriminator.GetProperty("mapping");
        Assert.Equal(
            "#/components/schemas/Shape_Circle",
            mapping.GetProperty("circle").GetString()
        );
        Assert.Equal(
            "#/components/schemas/Shape_Square",
            mapping.GetProperty("square").GetString()
        );

        // Variant component: tag property first (single-member enum, required),
        // then the derived type's full flattened surface (inherited Id included)
        var circle = schemas.GetProperty("Shape_Circle");
        var properties = circle.GetProperty("properties");
        var tag = properties.GetProperty("$type");
        Assert.Equal("string", tag.GetProperty("type").GetString());
        var tagEnum = tag.GetProperty("enum");
        Assert.Equal(1, tagEnum.GetArrayLength());
        Assert.Equal("circle", tagEnum[0].GetString());
        Assert.Equal("string", properties.GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("number", properties.GetProperty("radius").GetProperty("type").GetString());

        var required = circle
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("$type", required);
        Assert.Contains("id", required);
        Assert.Contains("radius", required);
    }

    [Fact]
    public void Polymorphic_Custom_Discriminator_Property_Name_Is_Honoured()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
            [JsonDerivedType(typeof(EmailChannel), "email")]
            [JsonDerivedType(typeof(SmsChannel), "sms")]
            public abstract record Channel;

            public sealed record EmailChannel(string Address) : Channel;
            public sealed record SmsChannel(string Number) : Channel;

            [RivetContract]
            public static class ChannelsContract
            {
                public static readonly RouteDefinition<Channel> GetChannel =
                    Define.Get<Channel>("/api/channels/{id}");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var channel = schemas.GetProperty("Channel");

        Assert.Equal(
            "kind",
            channel.GetProperty("discriminator").GetProperty("propertyName").GetString()
        );
        var email = schemas.GetProperty("Channel_Email");
        var tagEnum = email.GetProperty("properties").GetProperty("kind").GetProperty("enum");
        Assert.Equal("email", tagEnum[0].GetString());
    }

    // A variant field is a member like any other: it says everything the derived record's own
    // property says ([RivetSchemaType] included, which also lifts the number-handling refusal),
    // and contract JSON carries all of it to --from.
    [Fact]
    public void Variant_Field_Carries_What_Its_Record_Property_Carries()
    {
        var source = """
            using System;
            using System.ComponentModel.DataAnnotations;
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic]
            [JsonDerivedType(typeof(Coupon), "coupon")]
            public abstract record Discount;

            [RivetDescription("A code taken off the total.")]
            public sealed record Coupon(
                [property: JsonNumberHandling(JsonNumberHandling.WriteAsString), RivetSchemaType("string")]
                long Cents,
                [property: RivetDescription("Shown at checkout."), StringLength(12, MinimumLength = 3), RivetExample("\"SPRING\"")]
                string Code,
                [property: Range(1, 100), RivetDefault("10")]
                int Percent,
                [property: Obsolete, RivetFormat("date")]
                string? ExpiresOn) : Discount
            {
                public string Label => Code;
            }

            [RivetContract]
            public static class DiscountsContract
            {
                public static readonly RouteDefinition<Discount> GetDiscount =
                    Define.Get<Discount>("/api/discounts/{id}");

                public static readonly RouteDefinition<Coupon> GetCoupon =
                    Define.Get<Coupon>("/api/coupons/{id}");
            }
            """;

        var (endpoints, walker) = CompilationHelper.WalkMerged(source);
        var direct = JsonNode
            .Parse(
                OpenApiEmitter.Emit(
                    endpoints,
                    walker.Definitions,
                    walker.Brands,
                    walker.Enums,
                    security: null
                )
            )!
            .AsObject();
        var read = JsonContractReader.Read(
            ContractEmitter.Emit(
                walker.Definitions.ToDictionary(),
                walker.Enums.ToDictionary(),
                endpoints
            )
        );
        var throughContractJson = JsonNode.Parse(
            OpenApiEmitter.Emit(
                read.Endpoints,
                read.Types.ToDictionary(type => type.Name),
                read.Brands,
                read.Enums,
                security: null
            )
        );
        Assert.True(
            JsonNode.DeepEquals(direct, throughContractJson),
            "contract JSON lost something a variant field carries"
        );

        // The variant is the derived record's own schema with the tag prepended.
        var schemas = direct["components"]!["schemas"]!;
        var untagged = schemas["Discount_Coupon"]!.DeepClone().AsObject();
        Assert.True(untagged["properties"]!.AsObject().Remove("$type"));
        Assert.Equal("$type", (string?)untagged["required"]![0]);
        untagged["required"]!.AsArray().RemoveAt(0);
        Assert.True(
            JsonNode.DeepEquals(schemas["Coupon"], untagged),
            $"the variant without its tag is {untagged.ToJsonString()}, the record {schemas["Coupon"]!.ToJsonString()}"
        );

        var variant = schemas["Discount_Coupon"]!["properties"]!.AsObject();
        Assert.Equal("$type", variant.First().Key);
        Assert.Equal(
            "A code taken off the total.",
            (string?)schemas["Discount_Coupon"]!["description"]
        );

        Assert.Equal("string", (string?)variant["cents"]!["type"]);
        Assert.Equal("Shown at checkout.", (string?)variant["code"]!["description"]);
        Assert.Equal(3, (int?)variant["code"]!["minLength"]);
        Assert.Equal("SPRING", (string?)variant["code"]!["examples"]![0]);
        Assert.Equal(10, (int?)variant["percent"]!["default"]);
        Assert.Equal(100, (int?)variant["percent"]!["maximum"]);
        Assert.True((bool?)variant["expiresOn"]!["deprecated"]);
        Assert.Equal("date", (string?)variant["expiresOn"]!["format"]);
        Assert.True((bool?)variant["label"]!["readOnly"]);
    }

    // An import records the source's own tag schema and component id on the variant; the
    // variant is emitted under that id with that tag schema instead of {Base}_{Tag} + enum.
    [Fact]
    public void Variant_Keeps_Its_Pinned_Id_And_Authored_Tag_Schema()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
            [JsonDerivedType(typeof(Coupon), "coupon")]
            [JsonDerivedType(typeof(Voucher), "voucher")]
            public abstract record Discount;

            [RivetGeneratedType("CouponDiscount", RivetGeneratedTypeProvenance.Component, DiscriminatorSchemaJson = "{\"type\":\"string\",\"description\":\"The kind of discount.\"}")]
            public sealed record Coupon(string Code) : Discount;

            public sealed record Voucher(string Code) : Discount;

            [RivetContract]
            public static class DiscountsContract
            {
                public static readonly RouteDefinition<Discount> GetDiscount =
                    Define.Get<Discount>("/api/discounts/{id}");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var mapping = schemas
            .GetProperty("Discount")
            .GetProperty("discriminator")
            .GetProperty("mapping");

        Assert.Equal(
            "#/components/schemas/CouponDiscount",
            mapping.GetProperty("coupon").GetString()
        );
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("""{"type":"string","description":"The kind of discount."}"""),
                JsonNode.Parse(
                    schemas
                        .GetProperty("CouponDiscount")
                        .GetProperty("properties")
                        .GetProperty("type")
                        .GetRawText()
                )
            )
        );
        Assert.False(schemas.TryGetProperty("Discount_Coupon", out _));

        // Without the metadata, the tag is still the synthesized single-value enum.
        var voucherTag = schemas
            .GetProperty("Discount_Voucher")
            .GetProperty("properties")
            .GetProperty("type");
        Assert.Equal("voucher", voucherTag.GetProperty("enum")[0].GetString());
    }

    [Fact]
    public void Derived_Type_Referenced_Directly_Stays_Untagged()
    {
        // STJ wire semantics: serializing a derived type as its own static type
        // writes NO discriminator — its standalone schema must not carry the tag.
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic]
            [JsonDerivedType(typeof(Circle), "circle")]
            [JsonDerivedType(typeof(Square), "square")]
            public abstract record Shape(string Id);

            public sealed record Circle(string Id, double Radius) : Shape(Id);
            public sealed record Square(string Id, double Side) : Shape(Id);

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly RouteDefinition<Shape> GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");

                public static readonly RouteDefinition<Circle> GetCircle =
                    Define.Get<Circle>("/api/circles/{id}");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        // The standalone Circle component: flattened surface, no discriminator
        var circle = schemas.GetProperty("Circle");
        var properties = circle.GetProperty("properties");
        Assert.False(
            properties.TryGetProperty("$type", out _),
            "a derived type serialized as its own static type writes no discriminator"
        );
        Assert.True(properties.TryGetProperty("radius", out _));
        Assert.False(circle.TryGetProperty("oneOf", out _));

        // The tagged variant component still exists separately
        Assert.True(schemas.TryGetProperty("Shape_Circle", out var variant));
        Assert.True(variant.GetProperty("properties").TryGetProperty("$type", out _));
    }

    // Do NOT stringify int tags: a spec validating string tags against an int wire value
    // is a lie, and flattening the base describes a payload STJ never writes. A tagless
    // registration writes no discriminator at all. Both are refused.
    [Theory]
    [InlineData("typeof(Circle), 1", "a non-string")]
    [InlineData("typeof(Circle)", "no")]
    public void Non_String_Discriminator_Tag_Fails_With_RIV1014(string registration, string kind)
    {
        var source = $$"""
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic]
            [JsonDerivedType({{registration}})]
            [JsonDerivedType(typeof(Square), "square")]
            public abstract record Shape(string Id);

            public sealed record Circle(string Id, double Radius) : Shape(Id);
            public sealed record Square(string Id, double Side) : Shape(Id);

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly RouteDefinition<Shape> GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");
            }
            """;

        var message = Assert
            .Throws<Rivet.Tool.RivetUserException>(() => CompilationHelper.EmitOpenApi(source))
            .Message;

        Assert.StartsWith("error RIV1014:", message);
        Assert.Contains($"with {kind} discriminator tag", message);
        Assert.Contains("'Test.Circle'", message);
        Assert.Contains("[JsonDerivedType(typeof(Circle), \"circle\")]", message);
    }

    [Fact]
    public void RivetUnion_Without_Variants_Fails_With_RIV1018()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetUnion]
            public sealed record Nothing;

            [RivetType]
            public sealed record Holder(Nothing Value);
            """;

        var message = Assert
            .Throws<Rivet.Tool.RivetUserException>(() =>
                CompilationHelper.DiscoverAndWalk(CompilationHelper.CreateCompilation(source))
            )
            .Message;

        Assert.StartsWith("error RIV1018:", message);
        Assert.Contains("'Nothing'", message);
        Assert.Contains("remove [RivetUnion]", message);
    }

    // System.Text.Json throws on such a type once options are frozen (Rivet.RuntimeTests pins
    // it), so there is no payload to describe.
    [Fact]
    public void JsonPolymorphic_Without_Registrations_Fails_With_RIV1015()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic]
            public abstract record Shape(string Id);

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly RouteDefinition<Shape> GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");
            }
            """;

        var message = Assert
            .Throws<Rivet.Tool.RivetUserException>(() => CompilationHelper.EmitOpenApi(source))
            .Message;

        Assert.StartsWith("error RIV1015:", message);
        Assert.Contains("'Shape'", message);
        Assert.Contains("remove [JsonPolymorphic]", message);
    }

    private static string PolymorphicSettingSource(string setting) =>
        $$"""
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic({{setting}})]
            [JsonDerivedType(typeof(Circle), "circle")]
            public abstract record Shape(string Id);

            public sealed record Circle(string Id, double Radius) : Shape(Id);

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly RouteDefinition<Shape> GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");
            }
            """;

    // A fallback writes an unregistered derived type as a base the oneOf doesn't admit;
    // ignoring unrecognized tags reads one as the base, which the oneOf rejects.
    [Theory]
    [InlineData("UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType")]
    [InlineData(
        "UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor"
    )]
    [InlineData("IgnoreUnrecognizedTypeDiscriminators = true")]
    public void Unknown_Type_Fallback_Fails_With_RIV1016(string setting)
    {
        var message = Assert
            .Throws<Rivet.Tool.RivetUserException>(() =>
                CompilationHelper.EmitOpenApi(PolymorphicSettingSource(setting))
            )
            .Message;

        Assert.StartsWith("error RIV1016:", message);
        Assert.Contains("'Shape'", message);
        Assert.Contains("Remove the setting", message);
    }

    [Fact]
    public void UnknownDerivedTypeHandling_FailSerialization_Is_The_Default_And_Emits()
    {
        using var doc = CompilationHelper.EmitOpenApi(
            PolymorphicSettingSource(
                "UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization"
            )
        );

        var shape = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Shape");
        Assert.True(shape.TryGetProperty("oneOf", out _));
        Assert.True(shape.TryGetProperty("discriminator", out _));
    }

    [Fact]
    public void Base_Registered_As_Its_Own_Variant_Is_Included()
    {
        // STJ: the base itself is a variant only if explicitly registered.
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic]
            [JsonDerivedType(typeof(Shape), "shape")]
            [JsonDerivedType(typeof(Circle), "circle")]
            public record Shape(string Id);

            public sealed record Circle(string Id, double Radius) : Shape(Id);

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly RouteDefinition<Shape> GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");
            }
            """;

        using var doc = CompilationHelper.EmitOpenApi(source);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        var mapping = schemas
            .GetProperty("Shape")
            .GetProperty("discriminator")
            .GetProperty("mapping");
        Assert.Equal("#/components/schemas/Shape_Shape", mapping.GetProperty("shape").GetString());
        Assert.Equal(
            "#/components/schemas/Shape_Circle",
            mapping.GetProperty("circle").GetString()
        );

        var baseVariant = schemas.GetProperty("Shape_Shape").GetProperty("properties");
        Assert.True(baseVariant.TryGetProperty("$type", out _));
        Assert.True(baseVariant.TryGetProperty("id", out _));
        Assert.False(baseVariant.TryGetProperty("radius", out _));
    }

    // ════════════════════════════ Importer pins ════════════════════════════

    private const string PolymorphicSpecSchemas = """
        "Shape": {
            "oneOf": [
                { "$ref": "#/components/schemas/Shape_Circle" },
                { "$ref": "#/components/schemas/Shape_Square" }
            ],
            "discriminator": {
                "propertyName": "$type",
                "mapping": {
                    "circle": "#/components/schemas/Shape_Circle",
                    "square": "#/components/schemas/Shape_Square"
                }
            }
        },
        "Shape_Circle": {
            "type": "object",
            "properties": {
                "$type": { "type": "string", "enum": ["circle"] },
                "id": { "type": "string" },
                "radius": { "type": "number", "format": "double" }
            },
            "required": ["$type", "id", "radius"]
        },
        "Shape_Square": {
            "type": "object",
            "properties": {
                "$type": { "type": "string", "enum": ["square"] },
                "id": { "type": "string" },
                "side": { "type": "number", "format": "double" }
            },
            "required": ["$type", "id", "side"]
        }
        """;

    private const string ShapePath = """
        "/api/shapes/{id}": {
            "get": {
                "operationId": "getShape",
                "parameters": [
                    { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                ],
                "responses": {
                    "200": {
                        "description": "OK",
                        "content": {
                            "application/json": { "schema": { "$ref": "#/components/schemas/Shape" } }
                        }
                    }
                }
            }
        }
        """;

    [Fact]
    public void OneOf_With_Discriminator_Mapping_Imports_As_JsonPolymorphic_Hierarchy()
    {
        var spec = CompilationHelper.BuildSpec(PolymorphicSpecSchemas, ShapePath);
        var result = CompilationHelper.Import(spec);

        // Abstract base with the polymorphism attributes
        var baseContent = CompilationHelper.FindFile(result, "Shape.cs");
        Assert.Contains(
            "[JsonPolymorphic(TypeDiscriminatorPropertyName = \"$type\")]",
            baseContent
        );
        Assert.Contains("[JsonDerivedType(typeof(ShapeCircle), \"circle\")]", baseContent);
        Assert.Contains("[JsonDerivedType(typeof(ShapeSquare), \"square\")]", baseContent);
        Assert.Contains("public abstract record Shape", baseContent);
        Assert.Contains("[RivetType]", baseContent);
        Assert.Contains("using System.Text.Json.Serialization;", baseContent);

        // Derived records: discriminator property STRIPPED (STJ re-adds it on the
        // wire — keeping it would double-emit), base clause present, no [RivetType]
        // (the walker reaches them through the base's registrations).
        var circleContent = CompilationHelper.FindFile(result, "ShapeCircle.cs");
        Assert.Contains(") : Shape", circleContent);
        Assert.Contains("[JsonExtensionData]", circleContent);
        Assert.Contains("double Radius", circleContent);
        Assert.Contains("string Id", circleContent);
        Assert.DoesNotContain("string Type", circleContent.Split("record ShapeCircle")[1]);
        Assert.DoesNotContain("JsonPropertyName(\"$type\")", circleContent);
        Assert.DoesNotContain("[RivetType]", circleContent);

        // No union-wrapper fallback, no discriminator-dropped warning
        Assert.DoesNotContain("AsShapeCircle", baseContent);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("RIV3005"));

        // The whole import compiles
        var compilation = CompilationHelper.CompileImportResult(result);
        var errors = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            $"Imported polymorphic hierarchy does not compile:\n{string.Join("\n", errors)}"
        );
    }

    // The variants say what a source variant can: a type-level description, member
    // descriptions and constraints.
    // PolymorphicSpecSchemas with what a source variant can say beyond its shape: a
    // type-level description, a member description and a constraint.
    private const string AnnotatedSpecSchemas = """
        "Shape": {
            "oneOf": [
                { "$ref": "#/components/schemas/Shape_Circle" },
                { "$ref": "#/components/schemas/Shape_Square" }
            ],
            "discriminator": {
                "propertyName": "$type",
                "mapping": {
                    "circle": "#/components/schemas/Shape_Circle",
                    "square": "#/components/schemas/Shape_Square"
                }
            }
        },
        "Shape_Circle": {
            "type": "object",
            "description": "A round shape.",
            "properties": {
                "$type": { "type": "string", "enum": ["circle"] },
                "id": { "type": "string" },
                "radius": {
                    "type": "number",
                    "format": "double",
                    "description": "In centimetres.",
                    "minimum": 0
                }
            },
            "required": ["$type", "id", "radius"]
        },
        "Shape_Square": {
            "type": "object",
            "properties": {
                "$type": { "type": "string", "enum": ["square"] },
                "id": { "type": "string" },
                "side": { "type": "number", "format": "double" }
            },
            "required": ["$type", "id", "side"]
        }
        """;

    [Fact]
    public void Imported_Polymorphic_Hierarchy_Emits_Back_Every_Component_It_Refs()
    {
        // The CLI's import → emit path, provenance included: the base's oneOf is preserved
        // verbatim, and the variants it refs must still be there.
        var compilation = CompilationHelper.CompileImportResult(
            CompilationHelper.Import(CompilationHelper.BuildSpec(AnnotatedSpecSchemas, ShapePath))
        );
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        using var doc = JsonDocument.Parse(
            OpenApiEmitter.Emit(
                endpoints,
                walker.Definitions,
                walker.Brands,
                walker.Enums,
                security: null,
                new OpenApiDocumentInfo(
                    Provenance: OpenApiProvenanceWalker.Walk(compilation, walker)
                )
            )
        );

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(
            ["Shape", "Shape_Circle", "Shape_Square"],
            schemas.EnumerateObject().Select(schema => schema.Name).Order(StringComparer.Ordinal)
        );
        foreach (var name in new[] { "Shape", "Shape_Circle", "Shape_Square" })
        {
            var source = JsonNode.Parse("{" + AnnotatedSpecSchemas + "}")![name];
            Assert.True(
                JsonNode.DeepEquals(source, JsonNode.Parse(schemas.GetProperty(name).GetRawText())),
                $"{name} came back as {schemas.GetProperty(name).GetRawText()}"
            );
        }
    }

    [Fact]
    public void OneOf_Discriminator_Without_Mapping_Falls_Back_Loudly()
    {
        var schemas = """
            "Shape": {
                "oneOf": [
                    { "$ref": "#/components/schemas/Circle" },
                    { "$ref": "#/components/schemas/Square" }
                ],
                "discriminator": { "propertyName": "$type" }
            },
            "Circle": {
                "type": "object",
                "properties": { "radius": { "type": "number" } }
            },
            "Square": {
                "type": "object",
                "properties": { "side": { "type": "number" } }
            }
            """;

        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        // ResolveUnionRecord fallback, loudly with the reason
        var shape = CompilationHelper.FindFile(result, "Shape.cs");
        Assert.Contains("Circle? AsCircle", shape);
        Assert.Contains("Square? AsSquare", shape);
        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator dropped on 'Shape'")
                && w.Contains("mapping absent")
        );
    }

    [Fact]
    public void OneOf_Mapping_With_NonConforming_Variant_Falls_Back_Loudly()
    {
        // Circle has no "$type" tag property — the mapping is unusable: importing it
        // as [JsonPolymorphic] would change what payloads validate.
        var schemas = """
            "Shape": {
                "oneOf": [
                    { "$ref": "#/components/schemas/Circle" },
                    { "$ref": "#/components/schemas/Square" }
                ],
                "discriminator": {
                    "propertyName": "$type",
                    "mapping": {
                        "circle": "#/components/schemas/Circle",
                        "square": "#/components/schemas/Square"
                    }
                }
            },
            "Circle": {
                "type": "object",
                "properties": { "radius": { "type": "number" } }
            },
            "Square": {
                "type": "object",
                "properties": {
                    "$type": { "type": "string", "enum": ["square"] },
                    "side": { "type": "number" }
                }
            }
            """;

        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        var shape = CompilationHelper.FindFile(result, "Shape.cs");
        Assert.Contains("Circle? AsCircle", shape);
        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator dropped on 'Shape'")
                && w.Contains("no conforming '$type' tag property")
        );

        // The variants stay plain records (tag property kept where present)
        var square = CompilationHelper.FindFile(result, "Square.cs");
        Assert.DoesNotContain(": Shape", square);
    }

    [Fact]
    public void OneOf_Sibling_That_Only_Restates_The_Discriminator_Imports_With_Authored_Tag_Schemas()
    {
        // Jira's CustomContextVariable: the sibling `type` is the tag every variant carries
        // anyway. A variant that authors its tag as a described string keeps that schema;
        // one that authors the single-value enum the emitter writes anyway carries nothing.
        var schemas = """
            "Variable": {
                "type": "object",
                "additionalProperties": false,
                "oneOf": [
                    { "$ref": "#/components/schemas/UserVariable" },
                    { "$ref": "#/components/schemas/IssueVariable" }
                ],
                "discriminator": {
                    "propertyName": "type",
                    "mapping": {
                        "user": "#/components/schemas/UserVariable",
                        "issue": "#/components/schemas/IssueVariable"
                    }
                },
                "properties": { "type": { "type": "string", "description": "Type of variable." } },
                "required": ["type"]
            },
            "UserVariable": {
                "type": "object",
                "properties": {
                    "accountId": { "type": "string" },
                    "type": { "type": "string", "description": "Type of variable." }
                },
                "required": ["accountId", "type"]
            },
            "IssueVariable": {
                "type": "object",
                "properties": {
                    "id": { "type": "integer", "format": "int64" },
                    "type": { "type": "string", "enum": ["issue"] }
                },
                "required": ["type"]
            }
            """;

        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        Assert.DoesNotContain(result.Warnings, w => w.Contains("RIV3005"));
        var variable = CompilationHelper.FindFile(result, "Variable.cs");
        Assert.Contains("[JsonDerivedType(typeof(UserVariable), \"user\")]", variable);
        Assert.Contains("public abstract record Variable", variable);

        var user = CompilationHelper.FindFile(result, "UserVariable.cs");
        Assert.Contains(
            "DiscriminatorSchemaJson = \"{\\\"type\\\":\\\"string\\\",\\\"description\\\":\\\"Type of variable.\\\"}\"",
            user
        );
        Assert.DoesNotContain("string Type", user);
        Assert.DoesNotContain(
            "DiscriminatorSchemaJson",
            CompilationHelper.FindFile(result, "IssueVariable.cs")
        );

        var errors = CompilationHelper
            .CompileImportResult(result)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Theory]
    [InlineData(
        "\"properties\": { \"type\": { \"type\": \"string\" }, \"id\": { \"type\": \"string\" } }"
    )]
    [InlineData(
        "\"properties\": { \"type\": { \"type\": \"string\" } }, \"required\": [\"type\", \"id\"]"
    )]
    public void OneOf_Sibling_Beyond_The_Discriminator_Falls_Back_Loudly(string siblings)
    {
        var schemas = $$"""
            "Variable": {
                "oneOf": [{ "$ref": "#/components/schemas/UserVariable" }],
                "discriminator": {
                    "propertyName": "type",
                    "mapping": { "user": "#/components/schemas/UserVariable" }
                },
                {{siblings}}
            },
            "UserVariable": {
                "type": "object",
                "properties": { "id": { "type": "string" }, "type": { "type": "string" } }
            }
            """;

        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator dropped on 'Variable'")
                && w.Contains("sibling properties alongside oneOf")
        );
    }

    [Fact]
    public void OneOf_AllOf_Variant_Flattens_Into_A_Derived_Record()
    {
        // Cloudflare's rulesets: each variant extends a shared base through allOf and pins
        // its tag in its own part. The base's parts are copied in; the union base is the
        // record's only parent. Part order doesn't matter.
        var schemas = """
            "Rule": {
                "oneOf": [
                    { "$ref": "#/components/schemas/BlockRule" },
                    { "$ref": "#/components/schemas/LogRule" }
                ],
                "discriminator": {
                    "propertyName": "action",
                    "mapping": {
                        "block": "#/components/schemas/BlockRule",
                        "log": "#/components/schemas/LogRule"
                    }
                }
            },
            "RuleBase": {
                "type": "object",
                "properties": {
                    "action": { "type": "string" },
                    "id": { "type": "string" }
                },
                "required": ["id"]
            },
            "BlockRule": {
                "allOf": [
                    { "$ref": "#/components/schemas/RuleBase" },
                    {
                        "properties": {
                            "action": { "enum": ["block"] },
                            "reason": { "type": "string" }
                        }
                    }
                ]
            },
            "LogRule": {
                "allOf": [
                    { "properties": { "action": { "enum": ["log"] } } },
                    { "$ref": "#/components/schemas/RuleBase" }
                ]
            }
            """;

        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        Assert.DoesNotContain(result.Warnings, w => w.Contains("RIV3005"));
        Assert.Contains(
            "[JsonDerivedType(typeof(BlockRule), \"block\")]",
            CompilationHelper.FindFile(result, "Rule.cs")
        );
        var block = CompilationHelper.FindFile(result, "BlockRule.cs");
        Assert.Contains(": Rule", block);
        Assert.Contains("string Id", block);
        Assert.Contains("string Reason", block);
        Assert.DoesNotContain("Action", block);
        Assert.Contains(": Rule", CompilationHelper.FindFile(result, "LogRule.cs"));

        var errors = CompilationHelper
            .CompileImportResult(result)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void OneOf_AllOf_Variant_Whose_Parts_Contradict_The_Tag_Falls_Back_Loudly()
    {
        // allOf means every part applies: a base that only admits other tags leaves the
        // variant no payload (two of Cloudflare's resource-tagging unions).
        var schemas = """
            "Rule": {
                "oneOf": [{ "$ref": "#/components/schemas/BlockRule" }],
                "discriminator": {
                    "propertyName": "action",
                    "mapping": { "block": "#/components/schemas/BlockRule" }
                }
            },
            "RuleBase": {
                "type": "object",
                "properties": { "action": { "type": "string", "enum": ["log", "skip"] } }
            },
            "BlockRule": {
                "allOf": [
                    { "$ref": "#/components/schemas/RuleBase" },
                    { "properties": { "action": { "enum": ["block"] } } }
                ]
            }
            """;

        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator dropped on 'Rule'")
                && w.Contains("no conforming 'action' tag property")
        );
    }

    // Bitbucket's shape: a discriminator on a base with no oneOf, descendants by allOf, tags
    // implicit (the component name) unless the mapping names one.
    private const string AllOfHierarchySchemas = """
        "Resource": {
            "type": "object",
            "additionalProperties": true,
            "discriminator": {
                "propertyName": "type",
                "mapping": { "team": "#/components/schemas/Team" }
            },
            "properties": { "type": { "type": "string" } },
            "required": ["type"]
        },
        "Account": {
            "allOf": [
                { "$ref": "#/components/schemas/Resource" },
                {
                    "type": "object",
                    "title": "Account",
                    "additionalProperties": true,
                    "properties": {
                        "links": { "$ref": "#/components/schemas/AccountLinks" },
                        "name": { "type": "string" }
                    }
                }
            ]
        },
        "User": {
            "allOf": [
                { "$ref": "#/components/schemas/Account" },
                {
                    "type": "object",
                    "additionalProperties": true,
                    "properties": {
                        "links": { "$ref": "#/components/schemas/UserLinks" },
                        "name": { "type": "string" },
                        "nickname": { "type": "string" }
                    }
                }
            ]
        },
        "Team": {
            "allOf": [
                { "$ref": "#/components/schemas/Account" },
                { "type": "object", "properties": { "size": { "type": "integer" } } }
            ]
        },
        "PipelineSelector": {
            "allOf": [
                { "$ref": "#/components/schemas/Resource" },
                {
                    "type": "object",
                    "properties": {
                        "type": { "type": "string", "enum": ["branches", "tags"] },
                        "pattern": { "type": "string" }
                    }
                }
            ]
        },
        "AccountLinks": {
            "type": "object",
            "properties": { "avatar": { "type": "string" } }
        },
        "UserLinks": {
            "type": "object",
            "properties": { "html": { "type": "string" } }
        }
        """;

    [Fact]
    public void AllOf_Hierarchy_Under_A_Discriminator_Imports_As_JsonPolymorphic_Records()
    {
        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(AllOfHierarchySchemas));

        Assert.DoesNotContain(result.Warnings, w => w.Contains("RIV3005"));

        // The root is concrete: it registers itself and every descendant, and isn't abstract.
        var resource = CompilationHelper.FindFile(result, "Resource.cs");
        Assert.Contains("[JsonPolymorphic(TypeDiscriminatorPropertyName = \"type\")]", resource);
        Assert.Contains("[JsonDerivedType(typeof(Resource), \"Resource\")]", resource);
        Assert.Contains("[JsonDerivedType(typeof(User), \"User\")]", resource);
        Assert.Contains("[JsonDerivedType(typeof(Team), \"team\")]", resource);
        Assert.Contains("public record Resource", resource);
        Assert.DoesNotContain("string Type", resource);
        Assert.DoesNotContain("PipelineSelector", resource);

        // An intermediate base registers its own subtree, and declares only its own members.
        var account = CompilationHelper.FindFile(result, "Account.cs");
        Assert.Contains("[JsonDerivedType(typeof(Account), \"Account\")]", account);
        Assert.Contains("[JsonDerivedType(typeof(User), \"User\")]", account);
        Assert.Contains("public record Account : Resource", account);
        Assert.DoesNotContain("[RivetType]", account);
        Assert.DoesNotContain("[JsonExtensionData]", account);

        // A leaf is polymorphic too, so it writes its tag when it's the declared type, and it
        // redeclares a member its parent carries with another type.
        var user = CompilationHelper.FindFile(result, "User.cs");
        Assert.Contains("[JsonDerivedType(typeof(User), \"User\")]", user);
        Assert.Contains("public record User : Account", user);
        Assert.Contains("public new UserLinks Links", user);
        Assert.DoesNotContain("string Name", user);

        // pipeline_selector's own `type` is a selector kind that can never be its tag, so no
        // payload names it: it stays a plain record outside the hierarchy.
        var selector = CompilationHelper.FindFile(result, "PipelineSelector.cs");
        Assert.Contains("public sealed record PipelineSelector", selector);
        Assert.DoesNotContain(": Resource", selector);

        var errors = CompilationHelper
            .CompileImportResult(result)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private const string TeamMapping = "\"mapping\": { \"team\": \"#/components/schemas/Team\" }";

    [Theory]
    [InlineData(
        TeamMapping,
        "\"mapping\": { \"links\": \"#/components/schemas/AccountLinks\" }",
        "mapping entry 'links' references 'AccountLinks', which doesn't extend 'Resource' through allOf"
    )]
    [InlineData(
        TeamMapping,
        "\"mapping\": { \"selector\": \"#/components/schemas/PipelineSelector\" }",
        "mapping entry 'selector' names 'PipelineSelector', whose 'type' doesn't admit it"
    )]
    [InlineData(
        TeamMapping,
        "\"mapping\": { \"Team\": \"#/components/schemas/User\" }",
        "tag 'Team' names two schemas"
    )]
    [InlineData(
        "\"title\": \"Account\",",
        "\"title\": \"Account\", \"required\": [\"links\"],",
        "'User' redeclares 'links', which 'Account' requires, and C# can't hide a required member"
    )]
    public void AllOf_Hierarchy_The_CSharp_Cannot_Hold_Falls_Back_Loudly(
        string original,
        string replacement,
        string reason
    )
    {
        Assert.Contains(original, AllOfHierarchySchemas, StringComparison.Ordinal);
        var schemas = AllOfHierarchySchemas.Replace(
            original,
            replacement,
            StringComparison.Ordinal
        );

        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator dropped on 'Resource'")
                && w.Contains($"has no usable allOf hierarchy ({reason})")
        );
        Assert.DoesNotContain("JsonPolymorphic", CompilationHelper.FindFile(result, "Resource.cs"));
    }
}
