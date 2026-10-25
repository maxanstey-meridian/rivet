using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Rivet.Tool;

/// <summary>
/// What a JSON object inside an OpenAPI document is, as far as a walk over the raw JSON needs
/// to know: an OpenAPI or JSON Schema object whose keys are fields, or a map whose keys are
/// names the author chose. A property named <c>default</c> holds a schema while the
/// <c>default</c> keyword holds a value, and only the shape of the owner tells them apart.
/// </summary>
internal enum OpenApiShape
{
    /// <summary>An OpenAPI or JSON Schema object: keys are fields, x- keys are extensions.</summary>
    Object,

    /// <summary>An Example object: an <see cref="Object"/> whose <c>value</c> is literal data.</summary>
    Example,

    /// <summary>A Link object: an <see cref="Object"/> whose <c>requestBody</c> and <c>parameters</c> are literal data.</summary>
    Link,

    /// <summary>The components object: maps by component kind, plus x- extensions.</summary>
    Components,

    /// <summary>Names to objects (properties, headers, media types…), where x- is just a name.</summary>
    ObjectMap,

    /// <summary>Names to objects where x- is still an extension: paths, responses, a callback.</summary>
    ExtensibleObjectMap,

    /// <summary>Names to Example objects.</summary>
    ExampleMap,

    /// <summary>Names to Link objects.</summary>
    LinkMap,

    /// <summary>Names to callback objects.</summary>
    CallbackMap,
}

/// <summary>
/// The one set of rules for telling OpenAPI structure from data in a raw document (OpenAPI
/// 3.x and Swagger 2). Literal values (<c>example</c>, <c>default</c>, <c>const</c>,
/// <c>enum</c>, a schema's or a Swagger 2 response's <c>examples</c>, an Example's
/// <c>value</c>, a Link's <c>requestBody</c> and <c>parameters</c>) and extensions are data,
/// so nothing inside them is structure, even a key spelled <c>$ref</c>.
/// tools/roundtrip_diff.py mirrors these rules.
/// </summary>
internal static class OpenApiStructure
{
    /// <summary>
    /// Specification extensions are the patterned fields <c>^x-</c>, and OpenAPI field names
    /// are case-sensitive, so <c>X-Rate</c> is not an extension.
    /// </summary>
    public static bool IsExtension(string name) => name.StartsWith("x-", StringComparison.Ordinal);

    /// <summary>Whether an object of this shape is an OpenAPI or JSON Schema object rather than a name map.</summary>
    public static bool IsStructuralObject(OpenApiShape shape) =>
        shape is OpenApiShape.Object or OpenApiShape.Example or OpenApiShape.Link;

    /// <summary>The shape of an array's items: arrays in OpenAPI hold objects or literals, never maps.</summary>
    public const OpenApiShape ArrayItem = OpenApiShape.Object;

    private static readonly HashSet<string> _literalFields = new(StringComparer.Ordinal)
    {
        "example",
        "default",
        "const",
        "enum",
    };

    /// <summary>Fields whose object value maps author-chosen names to OpenAPI objects or schemas.</summary>
    private static readonly HashSet<string> _objectMapFields = new(StringComparer.Ordinal)
    {
        "$defs",
        "content",
        "definitions",
        "dependentSchemas",
        "encoding",
        "headers",
        "mapping",
        "parameters",
        "pathItems",
        "patternProperties",
        "properties",
        "requestBodies",
        "schemas",
        "securityDefinitions",
        "securitySchemes",
        "variables",
        "webhooks",
    };

    /// <summary>
    /// The shape of the value under <paramref name="key"/> in an object of shape
    /// <paramref name="owner"/>, or null when the value is data (a literal or an extension).
    /// </summary>
    public static OpenApiShape? ChildShape(
        OpenApiShape owner,
        string key,
        bool objectValue,
        bool swagger2
    ) =>
        owner switch
        {
            OpenApiShape.Object or OpenApiShape.Example or OpenApiShape.Link => FieldShape(
                owner,
                key,
                objectValue,
                swagger2
            ),
            OpenApiShape.Components => IsExtension(key)
                ? null
                : key switch
                {
                    "examples" => OpenApiShape.ExampleMap,
                    "links" => OpenApiShape.LinkMap,
                    "callbacks" => OpenApiShape.CallbackMap,
                    _ => OpenApiShape.ObjectMap,
                },
            OpenApiShape.ExtensibleObjectMap => IsExtension(key) ? null : OpenApiShape.Object,
            OpenApiShape.ObjectMap => OpenApiShape.Object,
            OpenApiShape.ExampleMap => OpenApiShape.Example,
            OpenApiShape.LinkMap => OpenApiShape.Link,
            OpenApiShape.CallbackMap => OpenApiShape.ExtensibleObjectMap,
            _ => throw new UnreachableException($"Unknown OpenAPI shape {owner}."),
        };

    private static OpenApiShape? FieldShape(
        OpenApiShape owner,
        string key,
        bool objectValue,
        bool swagger2
    )
    {
        if (
            IsExtension(key)
            || _literalFields.Contains(key)
            || owner is OpenApiShape.Example && key == "value"
            || owner is OpenApiShape.Link && key is "requestBody" or "parameters"
        )
        {
            return null;
        }

        return key switch
        {
            // OpenAPI 3's examples is a map of Example objects; a schema's is an array of
            // values, and Swagger 2's maps media types to values.
            "examples" => !swagger2 && objectValue ? OpenApiShape.ExampleMap : null,
            "components" => OpenApiShape.Components,
            "paths" or "responses" => OpenApiShape.ExtensibleObjectMap,
            "callbacks" => OpenApiShape.CallbackMap,
            "links" => OpenApiShape.LinkMap,
            _ when _objectMapFields.Contains(key) => OpenApiShape.ObjectMap,
            _ => OpenApiShape.Object,
        };
    }

    /// <summary>
    /// Visits every OpenAPI or JSON Schema object in <paramref name="document"/>, parent
    /// before children, with its JSON pointer. Name maps are crossed without being visited,
    /// and nothing inside data is visited. A visitor may change the object it is handed.
    /// </summary>
    public static void VisitObjects(
        JsonNode document,
        bool swagger2,
        Action<JsonObject, string> visit
    ) => Visit(document, "#", OpenApiShape.Object, swagger2, visit);

    private static void Visit(
        JsonNode node,
        string pointer,
        OpenApiShape shape,
        bool swagger2,
        Action<JsonObject, string> visit
    )
    {
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is { } item)
                {
                    Visit(item, $"{pointer}/{index}", ArrayItem, swagger2, visit);
                }
            }
            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        if (IsStructuralObject(shape))
        {
            visit(obj, pointer);
        }
        foreach (var (key, child) in obj.ToList())
        {
            if (
                child is not null
                && ChildShape(shape, key, child is JsonObject, swagger2) is { } childShape
            )
            {
                Visit(child, $"{pointer}/{JsonPointer.Escape(key)}", childShape, swagger2, visit);
            }
        }
    }
}
