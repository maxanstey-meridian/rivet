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

    /// <summary>
    /// The spec emitted straight from the walked C#, and the one emitted after a round trip
    /// through contract JSON (what --from reads), asserting the two are the same.
    /// </summary>
    private static JsonObject EmitDirectlyAndThroughContractJson(string source)
    {
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
            "contract JSON lost something the variant carries"
        );
        return direct;
    }

    // A variant field is a member like any other: it says everything the derived record's own
    // property says ([RivetSchemaType] included, which also lifts the number-handling refusal),
    // and contract JSON carries all of it to --from. A [RivetHeader] member is a header, not a
    // field, on the variant as on the record.
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

                [RivetHeader("X-Tenant")]
                public string Tenant { get; init; } = "";
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

        var direct = EmitDirectlyAndThroughContractJson(source);

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
        Assert.False(variant.ContainsKey("tenant"));
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

        var schemas = EmitDirectlyAndThroughContractJson(source)["components"]!["schemas"]!;

        Assert.Equal(
            "#/components/schemas/CouponDiscount",
            (string?)schemas["Discount"]!["discriminator"]!["mapping"]!["coupon"]
        );
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("""{"type":"string","description":"The kind of discount."}"""),
                schemas["CouponDiscount"]!["properties"]!["type"]
            )
        );
        Assert.Null(schemas["Discount_Coupon"]);

        // Without the metadata, the tag is still the synthesized single-value enum.
        Assert.Equal(
            "voucher",
            (string?)schemas["Discount_Voucher"]!["properties"]!["type"]!["enum"]![0]
        );
    }

    // The authored tag schema must be one the tag satisfies: the spec would otherwise refuse
    // the very tag System.Text.Json writes.
    [Theory]
    [InlineData("[]", "isn't a JSON object")]
    [InlineData("{", "isn't valid JSON")]
    [InlineData(
        "{\\\"enum\\\":[\\\"voucher\\\"]}",
        "has an enum that doesn't list the tag 'coupon'"
    )]
    [InlineData("{\\\"const\\\":\\\"voucher\\\"}", "has a const other than the tag 'coupon'")]
    [InlineData(
        "{\\\"type\\\":[\\\"integer\\\",\\\"null\\\"]}",
        "has a type that doesn't include string"
    )]
    public void Authored_Tag_Schema_That_Cannot_Carry_The_Tag_Fails_With_RIV1122(
        string tagSchema,
        string problem
    )
    {
        var source = $$"""
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
            [JsonDerivedType(typeof(Coupon), "coupon")]
            public abstract record Discount;

            [RivetGeneratedType("CouponDiscount", RivetGeneratedTypeProvenance.Component, DiscriminatorSchemaJson = "{{tagSchema}}")]
            public sealed record Coupon(string Code) : Discount;
            """;

        var message = Assert
            .Throws<Rivet.Tool.RivetUserException>(() =>
                CompilationHelper.DiscoverAndWalk(CompilationHelper.CreateCompilation(source))
            )
            .Message;

        Assert.StartsWith("error RIV1122:", message);
        Assert.Contains("[RivetGeneratedType] on 'Test.Coupon'", message);
        Assert.Contains(problem, message);
        Assert.Contains("Re-import", message);
    }

    // Only a registered variant has a tag property for the authored schema to describe.
    [Fact]
    public void Authored_Tag_Schema_On_A_Type_No_Base_Registers_Fails_With_RIV1122()
    {
        var source = """
            using Rivet;

            namespace Test;

            [RivetType]
            [RivetGeneratedType("Coupon", RivetGeneratedTypeProvenance.Component, DiscriminatorSchemaJson = "{\"type\":\"string\"}")]
            public sealed record Coupon(string Code);
            """;

        var message = Assert
            .Throws<Rivet.Tool.RivetUserException>(() =>
                CompilationHelper.DiscoverAndWalk(CompilationHelper.CreateCompilation(source))
            )
            .Message;

        Assert.StartsWith("error RIV1122:", message);
        Assert.Contains("no base registers the type with [JsonDerivedType]", message);
    }

    // The registration check finds the registering base wherever it is: the type itself (a
    // self-registering base), its direct base, or a base that registers a closed generic
    // whose open definition is what the walker reaches. Each variant is also referenced on
    // its own so the walker walks it; a null component id keeps the standalone component
    // and the variant apart.
    [Fact]
    public void Authored_Tag_Schema_On_A_Registered_Variant_Walked_Directly_Is_Emitted()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic]
            [JsonDerivedType(typeof(Shape), "shape")]
            [JsonDerivedType(typeof(Circle), "circle")]
            [JsonDerivedType(typeof(Tagged<int>), "tagged")]
            [RivetGeneratedType(null, RivetGeneratedTypeProvenance.Component, DiscriminatorSchemaJson = "{\"type\":\"string\",\"description\":\"shape tag\"}")]
            public record Shape(string Id);

            [RivetGeneratedType(null, RivetGeneratedTypeProvenance.Component, DiscriminatorSchemaJson = "{\"type\":\"string\",\"description\":\"circle tag\"}")]
            public sealed record Circle(string Id, double Radius) : Shape(Id);

            [RivetGeneratedType(null, RivetGeneratedTypeProvenance.Component, DiscriminatorSchemaJson = "{\"type\":\"string\",\"description\":\"tagged tag\"}")]
            public sealed record Tagged<T>(string Id, T Value) : Shape(Id);

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly RouteDefinition<Shape> GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");

                public static readonly RouteDefinition<Circle> GetCircle =
                    Define.Get<Circle>("/api/circles/{id}");

                public static readonly RouteDefinition<Tagged<int>> GetTagged =
                    Define.Get<Tagged<int>>("/api/tagged/{id}");
            }
            """;

        var schemas = EmitDirectlyAndThroughContractJson(source)["components"]!["schemas"]!;

        foreach (var tag in new[] { "shape", "circle", "tagged" })
        {
            var variant = (string)schemas["Shape"]!["discriminator"]!["mapping"]![tag]!;
            Assert.True(
                JsonNode.DeepEquals(
                    JsonNode.Parse($$"""{"type":"string","description":"{{tag}} tag"}"""),
                    schemas[variant.Split('/')[^1]]!["properties"]!["$type"]
                ),
                variant
            );
        }

        Assert.NotNull(schemas["Circle"]);
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
    // registration writes no discriminator at all, and a registered type that isn't a named
    // type has no variant schema; dropping it would describe a union the host never serves.
    // All are refused.
    [Theory]
    [InlineData("typeof(Circle), 1", "'Test.Circle' with a non-string discriminator tag")]
    [InlineData("typeof(Circle)", "'Test.Circle' with no discriminator tag")]
    [InlineData("typeof(int[]), \"ints\"", "'int[]', which isn't a named class, struct or record")]
    public void Unrepresentable_Registration_Fails_With_RIV1014(string registration, string problem)
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
        Assert.Contains($"[JsonDerivedType] on 'Shape' registers {problem}", message);
        Assert.Contains("as in [JsonDerivedType(typeof(", message);
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
    // it), so there is no payload to describe. A generic base, which otherwise flattens, is
    // no exception.
    [Theory]
    [InlineData("Shape(string Id)", "Shape")]
    [InlineData("Shape<T>(T Id)", "Shape<string>")]
    public void JsonPolymorphic_Without_Registrations_Fails_With_RIV1015(
        string declaration,
        string usage
    )
    {
        var source = $$"""
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic]
            public abstract record {{declaration}};

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly RouteDefinition<{{usage}}> GetShape =
                    Define.Get<{{usage}}>("/api/shapes/{id}");
            }
            """;

        var message = Assert
            .Throws<Rivet.Tool.RivetUserException>(() => CompilationHelper.EmitOpenApi(source))
            .Message;

        Assert.StartsWith("error RIV1015:", message);
        Assert.Contains("'Shape'", message);
        Assert.Contains("remove [JsonPolymorphic]", message);
    }

    private static string PolymorphicSettingSource(string setting, bool generic = false) =>
        $$"""
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic({{setting}})]
            [JsonDerivedType(typeof(Circle), "circle")]
            public abstract record {{(generic ? "Shape<T>(T Id)" : "Shape(string Id)")}};

            public sealed record Circle(string Id, double Radius) : {{(
                generic ? "Shape<string>" : "Shape"
            )}}(Id);

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly RouteDefinition<{{(
                generic ? "Shape<string>" : "Shape"
            )}}> GetShape =
                    Define.Get<{{(generic ? "Shape<string>" : "Shape")}}>("/api/shapes/{id}");
            }
            """;

    // A fallback writes an unregistered derived type as a base the oneOf doesn't admit;
    // ignoring unrecognized tags reads one as the base, which the oneOf rejects. A generic
    // base, which otherwise flattens, is no exception.
    [Theory]
    [InlineData(
        "UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType",
        false
    )]
    [InlineData(
        "UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor",
        false
    )]
    [InlineData("IgnoreUnrecognizedTypeDiscriminators = true", false)]
    [InlineData(
        "UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType",
        true
    )]
    [InlineData("IgnoreUnrecognizedTypeDiscriminators = true", true)]
    public void Unknown_Type_Fallback_Fails_With_RIV1016(string setting, bool generic)
    {
        var message = Assert
            .Throws<Rivet.Tool.RivetUserException>(() =>
                CompilationHelper.EmitOpenApi(PolymorphicSettingSource(setting, generic))
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

    // A sibling beside the oneOf may only restate the tag, and must admit every tag the
    // mapping names, by the walker's rule.
    [Theory]
    [InlineData(
        "\"properties\": { \"type\": { \"type\": \"string\" }, \"id\": { \"type\": \"string\" } }",
        "the schema declares sibling properties alongside oneOf"
    )]
    [InlineData(
        "\"properties\": { \"type\": { \"type\": \"string\" } }, \"required\": [\"type\", \"id\"]",
        "the schema requires 'id' beside oneOf"
    )]
    // Constrains nothing, so it admits whatever tag the mapping names.
    [InlineData("\"properties\": { \"type\": { \"description\": \"Kind\" } }", null)]
    // Self-contradictory: every keyword present must admit the tag, not just the enum.
    [InlineData(
        "\"properties\": { \"type\": { \"type\": \"integer\", \"enum\": [\"user\"] } }",
        "the sibling 'type' schema doesn't admit tag 'user'"
    )]
    public void OneOf_Sibling_Beside_The_Discriminator_Falls_Back_Loudly_Unless_It_Is_The_Tag(
        string siblings,
        string? reason
    )
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

        if (reason is null)
        {
            Assert.DoesNotContain(result.Warnings, w => w.Contains("RIV3005"));
            Assert.Contains(
                "[JsonDerivedType(typeof(UserVariable), \"user\")]",
                CompilationHelper.FindFile(result, "Variable.cs")
            );
            return;
        }

        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator dropped on 'Variable'")
                && w.Contains($"({reason})")
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
    // implicit (the component name) unless the mapping names one. Account's own `type` admits
    // only its descendants' tags, so no payload names an Account itself. The root is
    // nullable, and Envelope refers to it.
    private const string AllOfHierarchySchemas = """
        "Resource": {
            "type": ["object", "null"], "additionalProperties": true,
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
                        "type": { "type": "string", "enum": ["User", "team"] },
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
        },
        "Envelope": {
            "type": "object",
            "properties": { "owner": { "$ref": "#/components/schemas/Resource" } },
            "required": ["owner"]
        }
        """;

    /// <summary>AllOfHierarchySchemas with each (original, replacement) pair applied.</summary>
    private static string EditedHierarchySchemas(string[] edits)
    {
        var schemas = AllOfHierarchySchemas;
        for (var index = 0; index < edits.Length; index += 2)
        {
            Assert.Contains(edits[index], schemas, StringComparison.Ordinal);
            schemas = schemas.Replace(edits[index], edits[index + 1], StringComparison.Ordinal);
        }

        return schemas;
    }

    private static void AssertCompiles(Rivet.Tool.Import.ImportResult result)
    {
        var errors = CompilationHelper
            .CompileImportResult(result)
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    // The CLI's import → emit path, provenance included, as the round-trip gate runs it.
    private static JsonDocument ImportAndEmit(string schemas)
    {
        var compilation = CompilationHelper.CompileImportResult(
            CompilationHelper.Import(CompilationHelper.BuildSpec(schemas))
        );
        var (discovered, walker) = CompilationHelper.DiscoverAndWalk(compilation);
        var endpoints = CompilationHelper.WalkContracts(compilation, discovered, walker);
        return JsonDocument.Parse(
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
    }

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
        // Its `type` admits no "Account", so it registers only its descendants and is
        // abstract.
        var account = CompilationHelper.FindFile(result, "Account.cs");
        Assert.DoesNotContain("typeof(Account)", account);
        Assert.Contains("[JsonDerivedType(typeof(User), \"User\")]", account);
        Assert.Contains("[JsonDerivedType(typeof(Team), \"team\")]", account);
        Assert.Contains("public abstract record Account : Resource", account);
        Assert.DoesNotContain("[RivetType]", account);
        Assert.DoesNotContain("[JsonExtensionData]", account);
        Assert.DoesNotContain("typeof(Account)", resource);

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

        // The root is nullable, so a property referring to it is too.
        Assert.Contains("Resource? Owner", CompilationHelper.FindFile(result, "Envelope.cs"));

        AssertCompiles(result);
    }

    private const string AccountTitle = "\"title\": \"Account\",";
    private const string UserName = "\"name\": { \"type\": \"string\" },";
    private const string TeamPart =
        "{ \"type\": \"object\", \"properties\": { \"size\": { \"type\": \"integer\" } } }";

    // Each row is a property a member mentions again, by declaring it or listing it in its own
    // `required`. Every one compiles: an inherited member is either the member, or one C# can
    // redeclare with `new`.
    [Theory]
    // Restated as it is, without the `required` its ancestor gives it: still required, so the
    // same member, inherited.
    [InlineData(
        "User.cs",
        "",
        " Links {",
        AccountTitle,
        "\"title\": \"Account\", \"required\": [\"links\"],",
        "\"links\": { \"$ref\": \"#/components/schemas/UserLinks\" },",
        "\"links\": { \"$ref\": \"#/components/schemas/AccountLinks\" },"
    )]
    // Only a description differs: documentation, which the preserved schema keeps.
    [InlineData(
        "User.cs",
        "",
        " Name {",
        AccountTitle,
        "\"title\": \"Account\", \"required\": [\"name\"],",
        UserName,
        "\"name\": { \"type\": \"string\", \"description\": \"What the user goes by.\" },"
    )]
    // Only a description differs on an inline enum: the member inherits it, and no type of its
    // own is synthesized for the restatement.
    [InlineData(
        "User.cs",
        "",
        " Status {",
        AccountTitle,
        "\"title\": \"Account\", \"required\": [\"status\"],",
        "\"name\": { \"type\": \"string\" }\n",
        "\"name\": { \"type\": \"string\" }, \"status\": { \"type\": \"string\", \"enum\": [\"active\", \"closed\"] }\n",
        "\"nickname\": { \"type\": \"string\" }",
        "\"nickname\": { \"type\": \"string\" }, \"status\": { \"type\": \"string\", \"enum\": [\"active\", \"closed\"], \"description\": \"Whether the user can sign in.\" }"
    )]
    // Required by the member alone, without declaring it again.
    [InlineData(
        "Team.cs",
        "public new required string Name { get; init; }",
        "",
        TeamPart,
        "{ \"type\": \"object\", \"properties\": { \"size\": { \"type\": \"integer\" } }, \"required\": [\"name\"] }"
    )]
    // Restated and required beside the allOf.
    [InlineData(
        "User.cs",
        "public new required string Name { get; init; }",
        "",
        "\"User\": {",
        "\"User\": { \"required\": [\"name\"],"
    )]
    // Declared only below the ancestor that requires it: allOf applies every `required`.
    [InlineData(
        "User.cs",
        "public required string Nickname { get; init; }",
        "",
        "\"required\": [\"type\"]",
        "\"required\": [\"type\", \"nickname\"]"
    )]
    // An ancestor refuses extra properties, so nothing below it takes them either.
    [InlineData(
        "Account.cs",
        "",
        "[JsonExtensionData]",
        "\"type\": [\"object\", \"null\"], \"additionalProperties\": true,",
        "\"type\": [\"object\", \"null\"], \"additionalProperties\": false,"
    )]
    public void AllOf_Hierarchy_Member_Mentioning_An_Inherited_Property_Gets_The_Member_It_Means(
        string file,
        string expected,
        string absent,
        params string[] edits
    )
    {
        var result = CompilationHelper.Import(
            CompilationHelper.BuildSpec(EditedHierarchySchemas(edits))
        );

        Assert.DoesNotContain(result.Warnings, w => w.Contains("RIV3005"));
        var content = CompilationHelper.FindFile(result, file);
        if (expected.Length > 0)
        {
            Assert.Contains(expected, content);
        }

        if (absent.Length > 0)
        {
            Assert.DoesNotContain(absent, content);
        }

        AssertEverySyntheticTypeIsUsed(result);
        AssertCompiles(result);
    }

    // A type synthesized for an inline schema that nothing refers to is an orphan.
    // RivetDocument.cs names every generated file, so it is no evidence of use.
    // PipelineSelectorType is one the hierarchy doesn't make: PipelineSelector flattens its
    // allOf, the `type` it inherits wins, and its own enum is synthesized all the same.
    private static void AssertEverySyntheticTypeIsUsed(Rivet.Tool.Import.ImportResult result)
    {
        foreach (
            var synthetic in result.Files.Where(file =>
                file.Content.Contains("RivetGeneratedTypeProvenance.Synthetic")
            )
        )
        {
            var name = Path.GetFileNameWithoutExtension(synthetic.FileName);
            if (name == "PipelineSelectorType")
            {
                continue;
            }

            Assert.True(
                result.Files.Any(file =>
                    file != synthetic
                    && file.FileName != "RivetDocument.cs"
                    && System.Text.RegularExpressions.Regex.IsMatch(
                        file.Content,
                        $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b"
                    )
                ),
                $"{name} is synthesized but nothing refers to it"
            );
        }
    }

    private const string TeamMapping = "\"mapping\": { \"team\": \"#/components/schemas/Team\" }";

    [Theory]
    [InlineData(
        "mapping entry 'links' references 'AccountLinks', which doesn't extend 'Resource' through allOf",
        TeamMapping,
        "\"mapping\": { \"links\": \"#/components/schemas/AccountLinks\" }"
    )]
    // The same, on a root that declares no properties of its own.
    [InlineData(
        "mapping entry 'links' references 'AccountLinks', which doesn't extend 'Resource' through allOf",
        TeamMapping,
        "\"mapping\": { \"links\": \"#/components/schemas/AccountLinks\" }",
        "\"properties\": { \"type\": { \"type\": \"string\" } },",
        ""
    )]
    [InlineData(
        "mapping entry 'selector' names 'PipelineSelector', whose 'type' doesn't admit it",
        TeamMapping,
        "\"mapping\": { \"selector\": \"#/components/schemas/PipelineSelector\" }"
    )]
    [InlineData(
        "tag 'Team' names two schemas",
        TeamMapping,
        "\"mapping\": { \"Team\": \"#/components/schemas/User\" }"
    )]
    [InlineData(
        "no allOf descendant carries a 'type' tag it admits",
        TeamMapping,
        "\"mapping\": {}",
        "\"properties\": { \"type\": { \"type\": \"string\" } },",
        "\"properties\": { \"type\": { \"type\": \"string\", \"enum\": [\"other\"] } },"
    )]
    [InlineData(
        "'User' redeclares 'links', which 'Account' requires, and C# can't hide a required member",
        AccountTitle,
        "\"title\": \"Account\", \"required\": [\"links\"],"
    )]
    [InlineData(
        "'User' redeclares 'name', which 'Account' requires, and C# can't hide a required member",
        AccountTitle,
        "\"title\": \"Account\", \"required\": [\"name\"],",
        UserName,
        "\"name\": { \"type\": \"string\", \"maxLength\": 10 },"
    )]
    // A vendor extension's value is data, documentation keywords inside it included.
    [InlineData(
        "'User' redeclares 'name', which 'Account' requires, and C# can't hide a required member",
        AccountTitle,
        "\"title\": \"Account\", \"required\": [\"name\"],",
        "\"name\": { \"type\": \"string\" }\n",
        "\"name\": { \"type\": \"string\", \"x-meta\": { \"description\": \"a\" } }\n",
        UserName,
        "\"name\": { \"type\": \"string\", \"x-meta\": { \"description\": \"b\" } },"
    )]
    [InlineData(
        "'Team' refuses additional properties its ancestor 'Resource' accepts",
        TeamPart,
        "{ \"type\": \"object\", \"properties\": { \"size\": { \"type\": \"integer\" } }, \"additionalProperties\": false }"
    )]
    // Reached only through allOfs that $ref several schemas.
    [InlineData(
        "'Account' extends 'Resource' and other schemas through allOf, which C# single inheritance can't hold",
        "{ \"$ref\": \"#/components/schemas/Resource\" },",
        "{ \"$ref\": \"#/components/schemas/Resource\" }, { \"$ref\": \"#/components/schemas/UserLinks\" },"
    )]
    [InlineData(
        "'User' extends 'Account' and other schemas through allOf, which C# single inheritance can't hold",
        "{ \"$ref\": \"#/components/schemas/Account\" },",
        "{ \"$ref\": \"#/components/schemas/Account\" }, { \"$ref\": \"#/components/schemas/UserLinks\" },"
    )]
    [InlineData(
        "'Bot' extends 'Account' through allOf and composes a oneOf/anyOf as well, which a derived record can't hold",
        "\"AccountLinks\": {",
        "\"Bot\": { \"allOf\": [{ \"$ref\": \"#/components/schemas/Account\" }], \"oneOf\": [{ \"type\": \"object\" }] }, \"AccountLinks\": {"
    )]
    [InlineData(
        "'Account' declares a discriminator of its own, 'kind'",
        AccountTitle,
        "\"title\": \"Account\", \"discriminator\": { \"propertyName\": \"kind\" },"
    )]
    [InlineData(
        "'Account' maps tag 'links' to 'AccountLinks', which isn't in the hierarchy",
        AccountTitle,
        "\"title\": \"Account\", \"discriminator\": { \"propertyName\": \"type\", \"mapping\": { \"links\": \"#/components/schemas/AccountLinks\" } },"
    )]
    [InlineData(
        "'Account' maps tag 'member' to 'User', which the hierarchy tags 'User'",
        AccountTitle,
        "\"title\": \"Account\", \"discriminator\": { \"propertyName\": \"type\", \"mapping\": { \"member\": \"#/components/schemas/User\" } },"
    )]
    public void AllOf_Hierarchy_The_CSharp_Cannot_Hold_Falls_Back_Loudly(
        string reason,
        params string[] edits
    )
    {
        var result = CompilationHelper.Import(
            CompilationHelper.BuildSpec(EditedHierarchySchemas(edits))
        );

        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator dropped on 'Resource'")
                && w.Contains($"has no usable allOf hierarchy ({reason})")
        );
        Assert.DoesNotContain("JsonPolymorphic", CompilationHelper.FindFile(result, "Resource.cs"));
        AssertCompiles(result);
    }

    [Fact]
    public void AllOf_Hierarchy_Mapping_Entry_Outside_The_Document_Is_Dropped_Loudly()
    {
        // The entry can't resolve in the one document Rivet imports, so it goes before the
        // hierarchy is read, and the hierarchy imports without it. (Team's implicit tag,
        // "Team", is one Account's `type` doesn't admit, so Team leaves the hierarchy.)
        var result = CompilationHelper.Import(
            CompilationHelper.BuildSpec(
                EditedHierarchySchemas([
                    TeamMapping,
                    "\"mapping\": { \"team\": \"https://example.test/schemas.json#/Team\" }",
                ])
            )
        );

        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator mapping entry dropped: 'team'")
                && w.Contains("outside this document")
        );
        Assert.DoesNotContain(
            result.Warnings,
            w => w.StartsWith("RIV3005: Discriminator dropped on 'Resource'")
        );
        var resource = CompilationHelper.FindFile(result, "Resource.cs");
        Assert.Contains("[JsonDerivedType(typeof(User), \"User\")]", resource);
        Assert.DoesNotContain("typeof(Team)", resource);
    }

    [Fact]
    public void AllOf_Hierarchy_Member_With_A_Discriminator_Beside_Its_AllOf_Falls_Back_Loudly()
    {
        // OpenAPI 3.0's Pet extends a base and roots its own discriminator. The root of a
        // hierarchy has no parent of its own, so the discriminator can't be kept, and says so.
        var schemas = """
            "Base": { "type": "object", "properties": { "id": { "type": "string" } } },
            "Pet": {
                "allOf": [{ "$ref": "#/components/schemas/Base" }],
                "discriminator": { "propertyName": "petType" },
                "properties": { "petType": { "type": "string" } }
            },
            "Cat": {
                "allOf": [
                    { "$ref": "#/components/schemas/Pet" },
                    { "type": "object", "properties": { "lives": { "type": "integer" } } }
                ]
            }
            """;

        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator dropped on 'Pet'")
                && w.Contains("sits beside an allOf of its own, so it can't root a hierarchy")
        );
    }

    // A $ref to a component that discriminates over nothing Rivet can reverse still says why.
    [Theory]
    // Cloudflare's dns-record-response: the discriminator dispatches over a union its allOf
    // nests, not over descendants.
    [InlineData(
        "Record",
        "property 'type' dispatches over a oneOf/anyOf nested in its allOf, which Rivet can't reverse — imported as a flattened record.",
        """
            "Record": {
                "allOf": [
                    {
                        "type": "object",
                        "anyOf": [
                            { "$ref": "#/components/schemas/ARecord" },
                            { "$ref": "#/components/schemas/MxRecord" }
                        ]
                    },
                    { "properties": { "id": { "type": "string" } } }
                ],
                "discriminator": { "propertyName": "type" }
            },
            "ARecord": { "type": "object", "properties": { "type": { "type": "string" } } },
            "MxRecord": { "type": "object", "properties": { "type": { "type": "string" } } }
            """
    )]
    // A property-less object whose extra properties have a schema is a dictionary, which has
    // nowhere to put a tag.
    [InlineData(
        "Labels",
        "property 'kind' has no oneOf union and no allOf descendants — imported as a dictionary.",
        """
            "Labels": {
                "type": "object",
                "additionalProperties": { "type": "string" },
                "discriminator": { "propertyName": "kind" }
            }
            """
    )]
    public void Discriminator_With_Nothing_Rivet_Can_Dispatch_Over_Falls_Back_Loudly(
        string name,
        string reason,
        string schemas
    )
    {
        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        Assert.Contains($"RIV3005: Discriminator dropped on '{name}': {reason}", result.Warnings);
    }

    // The importer admits a variant's tag by the walker's own rule, so what it writes as the
    // authored tag schema never stops the next generation at RIV1122.
    [Theory]
    // A tag property that constrains nothing admits whatever tag the mapping names.
    [InlineData("{ \"description\": \"The pet kind.\" }", null)]
    // Self-contradictory: every keyword present must admit the tag, not just the first read.
    [InlineData(
        "{ \"type\": \"integer\", \"enum\": [\"cat\"] }",
        "variant 'Cat' has no conforming 'kind' tag property"
    )]
    [InlineData(
        "{ \"enum\": [\"cat\"], \"const\": \"dog\" }",
        "variant 'Cat' has no conforming 'kind' tag property"
    )]
    public void OneOf_Variant_Tag_Schema_Is_Admitted_By_The_Walkers_Rule(
        string tagSchema,
        string? rejection
    )
    {
        var schemas = $$"""
            "Pet": {
                "oneOf": [{ "$ref": "#/components/schemas/Cat" }],
                "discriminator": {
                    "propertyName": "kind",
                    "mapping": { "cat": "#/components/schemas/Cat" }
                }
            },
            "Cat": {
                "type": "object",
                "properties": { "kind": {{tagSchema}}, "lives": { "type": "integer" } },
                "required": ["kind"]
            }
            """;

        var result = CompilationHelper.Import(CompilationHelper.BuildSpec(schemas));

        if (rejection is not null)
        {
            Assert.Contains(
                result.Warnings,
                w =>
                    w.StartsWith("RIV3005: Discriminator dropped on 'Pet'") && w.Contains(rejection)
            );
            return;
        }

        Assert.DoesNotContain(result.Warnings, w => w.Contains("RIV3005"));
        using var doc = ImportAndEmit(schemas);
        var kind = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Cat")
            .GetProperty("properties")
            .GetProperty("kind");
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(tagSchema), JsonNode.Parse(kind.GetRawText())),
            $"kind came back as {kind.GetRawText()}"
        );
    }

    // An implicit tag (a component name) is decided by the whole chain: every declaration of
    // the tag property must admit it, and one of them must constrain the value. A mapped tag
    // is named explicitly, so admitting it is enough.
    [Theory]
    // Account restates `type` with documentation only. Resource's `type` still constrains it
    // to a string, so Account and User are tags a payload names, as they would be had
    // Account not restated it.
    [InlineData(
        "public record Account : Resource",
        new[] { "Resource", "Account", "User" },
        new string[0],
        "\"type\": { \"type\": \"string\", \"enum\": [\"User\", \"team\"] },",
        "\"type\": { \"description\": \"The account kind.\" },"
    )]
    // Nothing down the chain constrains `type`: no implicit tag is one a payload names, but
    // the mapping names Team's.
    [InlineData(
        "public abstract record Account : Resource",
        new string[0],
        new[] { "Resource", "Account", "User" },
        "\"type\": { \"type\": \"string\", \"enum\": [\"User\", \"team\"] },",
        "\"type\": { \"description\": \"The account kind.\" },",
        "\"properties\": { \"type\": { \"type\": \"string\" } },",
        "\"properties\": { \"type\": { \"description\": \"The resource kind.\" } },"
    )]
    public void AllOf_Hierarchy_Implicit_Tag_Is_Admitted_By_Its_Whole_Chain(
        string account,
        string[] registered,
        string[] unregistered,
        params string[] edits
    )
    {
        var result = CompilationHelper.Import(
            CompilationHelper.BuildSpec(EditedHierarchySchemas(edits))
        );

        Assert.DoesNotContain(result.Warnings, w => w.Contains("RIV3005"));
        var resource = CompilationHelper.FindFile(result, "Resource.cs");
        Assert.Contains("[JsonDerivedType(typeof(Team), \"team\")]", resource);
        foreach (var name in registered)
        {
            Assert.Contains($"[JsonDerivedType(typeof({name}), \"{name}\")]", resource);
        }

        foreach (var name in unregistered)
        {
            Assert.DoesNotContain($"typeof({name})", resource);
        }

        Assert.Contains(account, CompilationHelper.FindFile(result, "Account.cs"));
        AssertCompiles(result);
    }

    private const string PropertylessRoot =
        "\"properties\": { \"type\": { \"type\": \"string\" } },";
    private const string OwnerProperty =
        "\"properties\": { \"owner\": { \"$ref\": \"#/components/schemas/Resource\" } },";

    // A hierarchy member is a record whatever it declares, so a $ref names its record.
    [Theory]
    // A property-less root that admits null: the tag is declared only below it.
    [InlineData("Envelope.cs", "required Resource? Owner {", PropertylessRoot, "")]
    // A leaf that declares nothing under a root that declares nothing: no type, no
    // properties, still the record the tag its mapping entry names.
    [InlineData(
        "Envelope.cs",
        "public Leaf Leaf {",
        PropertylessRoot,
        "",
        TeamMapping,
        "\"mapping\": { \"team\": \"#/components/schemas/Team\", \"leaf\": \"#/components/schemas/Leaf\" }",
        OwnerProperty,
        "\"properties\": { \"owner\": { \"$ref\": \"#/components/schemas/Resource\" }, \"leaf\": { \"$ref\": \"#/components/schemas/Leaf\" } },",
        "\"AccountLinks\": {",
        "\"Leaf\": { \"allOf\": [{ \"$ref\": \"#/components/schemas/Resource\" }] }, \"AccountLinks\": {"
    )]
    // A member of one hierarchy refers to the property-less root of a hierarchy built after
    // it, whose extra properties have a schema: still that root's record, not a dictionary.
    [InlineData(
        "Team.cs",
        "public Labels Labels {",
        TeamPart,
        "{ \"type\": \"object\", \"properties\": { \"size\": { \"type\": \"integer\" }, \"labels\": { \"$ref\": \"#/components/schemas/Labels\" } } }",
        "\"AccountLinks\": {",
        "\"Labels\": { \"type\": \"object\", \"additionalProperties\": { \"type\": \"string\" }, \"discriminator\": { \"propertyName\": \"kind\" } }, \"PriorityLabels\": { \"allOf\": [{ \"$ref\": \"#/components/schemas/Labels\" }, { \"type\": \"object\", \"properties\": { \"kind\": { \"type\": \"string\" } } }] }, \"AccountLinks\": {"
    )]
    public void AllOf_Hierarchy_Member_Is_Its_Record_Wherever_A_Ref_Names_It(
        string file,
        string expected,
        params string[] edits
    )
    {
        var result = CompilationHelper.Import(
            CompilationHelper.BuildSpec(EditedHierarchySchemas(edits))
        );

        Assert.DoesNotContain(result.Warnings, w => w.Contains("RIV3005"));
        var content = CompilationHelper.FindFile(result, file);
        Assert.True(content.Contains(expected), content);
        AssertCompiles(result);
    }

    [Fact]
    public void AllOf_Hierarchy_That_Falls_Back_Leaves_No_Record_Naming_Its_Members()
    {
        // Team (built first) refers to Labels, whose hierarchy the C# can't hold: Labels
        // carries extension data that PriorityLabels refuses. It is rejected before anything
        // is registered, so Team's property is the dictionary Labels imports as.
        var result = CompilationHelper.Import(
            CompilationHelper.BuildSpec(
                EditedHierarchySchemas([
                    TeamPart,
                    "{ \"type\": \"object\", \"properties\": { \"size\": { \"type\": \"integer\" }, \"labels\": { \"$ref\": \"#/components/schemas/Labels\" } } }",
                    "\"AccountLinks\": {",
                    "\"Labels\": { \"type\": \"object\", \"additionalProperties\": { \"type\": \"string\" }, \"discriminator\": { \"propertyName\": \"kind\" } }, \"PriorityLabels\": { \"allOf\": [{ \"$ref\": \"#/components/schemas/Labels\" }, { \"type\": \"object\", \"additionalProperties\": false, \"properties\": { \"kind\": { \"type\": \"string\" } } }] }, \"AccountLinks\": {",
                ])
            )
        );

        Assert.Contains(
            result.Warnings,
            w =>
                w.StartsWith("RIV3005: Discriminator dropped on 'Labels'")
                && w.Contains(
                    "('PriorityLabels' refuses additional properties its ancestor 'Labels' accepts)"
                )
        );
        Assert.DoesNotContain(
            "public Labels Labels",
            CompilationHelper.FindFile(result, "Team.cs")
        );
        AssertCompiles(result);
    }

    [Fact]
    public void Imported_AllOf_Hierarchy_Emits_Back_Every_Member_Including_An_Abstract_One()
    {
        // User's and Team's preserved schemas $ref Account, which no payload names: its
        // component must still be there, exactly as the source wrote it.
        using var doc = ImportAndEmit(AllOfHierarchySchemas);

        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "Resource", "Account", "User", "Team" })
        {
            var source = JsonNode.Parse("{" + AllOfHierarchySchemas + "}")![name];
            Assert.True(schemas.TryGetProperty(name, out var emitted), $"{name} is missing");
            Assert.True(
                JsonNode.DeepEquals(source, JsonNode.Parse(emitted.GetRawText())),
                $"{name} came back as {emitted.GetRawText()}"
            );
        }
    }

    [Fact]
    public void Imported_Nullable_Hierarchy_Root_Admits_Null_Once_Where_It_Is_Used()
    {
        // Resource admits null itself, so Envelope's owner is a bare $ref: a null branch
        // beside it would match null twice, and oneOf would reject it.
        using var doc = ImportAndEmit(AllOfHierarchySchemas);

        var owner = doc
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Envelope")
            .GetProperty("properties")
            .GetProperty("owner");
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("""{ "$ref": "#/components/schemas/Resource" }"""),
                JsonNode.Parse(owner.GetRawText())
            ),
            $"owner came back as {owner.GetRawText()}"
        );
    }
}
