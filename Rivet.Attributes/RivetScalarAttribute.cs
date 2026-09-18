using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet;

/// <summary>
/// Declares that the annotated class/struct/record has a scalar Rivet contract
/// representation derived from its single <c>Value</c> property: the Value type
/// determines the wire/schema inner type and the type is emitted as a branded
/// scalar instead of an object. The attribute is the explicit opt-in for the
/// scalar decision — an unannotated one-property type is an ordinary object
/// schema.
/// Being a <see cref="JsonConverterAttribute"/>, the annotation also makes
/// ordinary System.Text.Json serialize/deserialize the type as its Value's
/// scalar representation, so the emitted contract and the runtime wire shape
/// agree without any application-wide serializer registration
/// (acceptance:scalar-attribute-makes-stj-wire-shape-true).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RivetScalarAttribute : JsonConverterAttribute
{
    public override JsonConverter? CreateConverter(Type typeToConvert)
    {
        // Shape validation runs at converter-creation time so an attributed
        // invalid-shape type fails clearly the first time System.Text.Json
        // creates the converter, matching the tool-side RIV1103 shape rules
        // (planner-constraint:converter-shape-validation-at-creation).
        var valueProperty = ResolveValueProperty(typeToConvert);

        return (JsonConverter?)
            Activator.CreateInstance(
                typeof(RivetScalarJsonConverter<>).MakeGenericType(typeToConvert),
                valueProperty
            );
    }

    private static PropertyInfo ResolveValueProperty(Type declaringType)
    {
        var candidates = declaringType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property =>
                property.Name == "Value"
                && property.CanRead
                && property.GetMethod is { IsStatic: false }
            )
            .ToArray();

        return candidates.Length == 1
            ? candidates[0]
            : throw new InvalidOperationException(
                $"[RivetScalar] type '{declaringType.Name}' must declare exactly one eligible "
                    + "non-static, non-indexer property named 'Value' — the converter cannot "
                    + "derive the scalar representation without it (RIV1103 shape rules)."
            );
    }
}

/// <summary>
/// Translates between a [RivetScalar] wrapper and its declared Value type using
/// normal System.Text.Json: the Value member is read and written with the active
/// serializer options (so per-property converters and naming policy keep
/// applying), the wrapper is constructed through its single-argument constructor,
/// and an invalid [RivetScalar] shape fails clearly instead of guessing. No
/// registry, no host-specific branches — the attribute-local seam is the whole
/// mechanism (packet-constraint:no-general-serializer-layer,
/// acceptance:scalar-runtime-is-bounded).
/// </summary>
public sealed class RivetScalarJsonConverter<T>(PropertyInfo valueProperty) : JsonConverter<T>
{
    public override T? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            // STJ invokes the converter for null only on reference-type wrappers;
            // non-nullable value-type wrappers refuse null like their Value type.
            if (typeToConvert.IsValueType && Nullable.GetUnderlyingType(typeToConvert) is null)
            {
                throw new JsonException($"null is not valid for scalar '{typeof(T).Name}'.");
            }

            return default;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement.Deserialize(valueProperty.PropertyType, options);
        return Construct(value);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // The Value member is delegated to System.Text.Json with the active options —
        // null Value (nullable or reference type) serializes to JSON null.
        var propertyValue = valueProperty.GetValue(value);
        JsonSerializer.Serialize(writer, propertyValue, valueProperty.PropertyType, options);
    }

    private static T Construct(object? value)
    {
        try
        {
            return (T)Activator.CreateInstance(typeof(T), value)!;
        }
        catch (MissingMethodException exception)
        {
            throw new InvalidOperationException(
                $"[RivetScalar] type '{typeof(T).Name}' has no single-parameter constructor "
                    + "accepting the Value type — declare a primary constructor taking Value.",
                exception
            );
        }
    }
}
