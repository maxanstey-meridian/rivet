using System.Text.Json;
using Microsoft.CodeAnalysis;

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
            public static readonly Define GetShape =
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
                public static readonly Define GetChannel =
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
                public static readonly Define GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");

                public static readonly Define GetCircle =
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

    [Fact]
    public void Int_Discriminator_Tag_Is_Diagnosed_And_Falls_Back_To_Flattening()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic]
            [JsonDerivedType(typeof(Circle), 1)]
            [JsonDerivedType(typeof(Square), 2)]
            public abstract record Shape(string Id);

            public sealed record Circle(string Id, double Radius) : Shape(Id);
            public sealed record Square(string Id, double Side) : Shape(Id);

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly Define GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");
            }
            """;

        JsonDocument? doc = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            doc = CompilationHelper.EmitOpenApi(source)
        );
        using var emitted = doc;

        // Do NOT stringify int tags — a spec validating string tags against an int
        // wire value is a lie. The base falls back to today's flattening, loudly.
        Assert.Contains("RIV1014", stderr);
        Assert.Contains("non-string discriminator tag", stderr);

        var shape = doc!
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Shape");
        Assert.False(shape.TryGetProperty("oneOf", out _));
        Assert.True(shape.GetProperty("properties").TryGetProperty("id", out _));
    }

    [Fact]
    public void JsonPolymorphic_Without_Registrations_Is_Diagnosed_And_Falls_Back()
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
                public static readonly Define GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");
            }
            """;

        JsonDocument? doc = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            doc = CompilationHelper.EmitOpenApi(source)
        );
        using var emitted = doc;

        Assert.Contains("RIV1015", stderr);
        Assert.Contains("no [JsonDerivedType] registrations", stderr);

        var shape = doc!
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("Shape");
        Assert.False(shape.TryGetProperty("oneOf", out _));
        Assert.True(shape.GetProperty("properties").TryGetProperty("id", out _));
    }

    [Fact]
    public void UnknownDerivedTypeHandling_Is_Diagnosed_But_Union_Still_Emits()
    {
        var source = """
            using System.Text.Json.Serialization;
            using Rivet;

            namespace Test;

            [RivetType]
            [JsonPolymorphic(UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
            [JsonDerivedType(typeof(Circle), "circle")]
            public abstract record Shape(string Id);

            public sealed record Circle(string Id, double Radius) : Shape(Id);

            [RivetContract]
            public static class ShapesContract
            {
                public static readonly Define GetShape =
                    Define.Get<Shape>("/api/shapes/{id}");
            }
            """;

        JsonDocument? doc = null;
        var stderr = CompilationHelper.CaptureStdErr(() =>
            doc = CompilationHelper.EmitOpenApi(source)
        );
        using var emitted = doc;

        Assert.Contains("RIV1016", stderr);
        Assert.Contains("UnknownDerivedTypeHandling", stderr);

        // The union itself still emits — only the unknown-type fallback is invisible
        var shape = doc!
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
                public static readonly Define GetShape =
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
        Assert.Contains(": Shape;", circleContent);
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
}
