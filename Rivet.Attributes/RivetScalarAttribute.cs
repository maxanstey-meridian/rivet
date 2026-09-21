using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet;

/// <summary>
/// Serializes a non-generic wrapper as its single public Value property.
/// Requires a public constructor accepting the Value type. Property-level JSON
/// settings are unsupported; configure the inner type instead.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RivetScalarAttribute : JsonConverterAttribute
{
    public override JsonConverter? CreateConverter(Type typeToConvert)
    {
        var valueProperty = ResolveValueProperty(typeToConvert);
        return (JsonConverter?)
            Activator.CreateInstance(
                typeof(RivetScalarJsonConverter<>).MakeGenericType(typeToConvert),
                valueProperty
            );
    }

    private static PropertyInfo ResolveValueProperty(Type declaringType)
    {
        var properties = declaringType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        if (
            declaringType.IsGenericType
            || declaringType.IsAbstract
            || (
                declaringType.BaseType != typeof(object)
                && declaringType.BaseType != typeof(ValueType)
            )
            || properties.Length != 1
            || properties[0].Name != "Value"
            || properties[0].GetMethod is not { IsPublic: true, IsStatic: false }
            || properties[0].GetIndexParameters().Length != 0
            || properties[0]
                .GetCustomAttributesData()
                .Any(a =>
                    a.AttributeType.Namespace == "System.Text.Json.Serialization"
                    || typeof(JsonConverterAttribute).IsAssignableFrom(a.AttributeType)
                )
            || declaringType
                .GetCustomAttributesData()
                .Count(a => typeof(JsonConverterAttribute).IsAssignableFrom(a.AttributeType)) != 1
            || !declaringType
                .GetConstructors()
                .Any(c =>
                    c.GetParameters() is [var parameter]
                    && parameter.ParameterType == properties[0].PropertyType
                )
        )
        {
            throw new InvalidOperationException(
                $"[RivetScalar] type '{declaringType.Name}' must be a non-generic concrete wrapper with "
                    + "exactly one public readable Value property and a public constructor accepting its type. "
                    + "Inheritance, competing converters and property-level JSON settings are unsupported (RIV1103)."
            );
        }
        return properties[0];
    }
}

internal sealed class RivetScalarJsonConverter<T>(PropertyInfo valueProperty) : JsonConverter<T>
{
    private readonly ConstructorInfo _constructor = typeof(T).GetConstructor([
        valueProperty.PropertyType,
    ])!;

    public override T? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => Construct(JsonSerializer.Deserialize(ref reader, valueProperty.PropertyType, options));

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(
            writer,
            valueProperty.GetValue(value),
            valueProperty.PropertyType,
            options
        );

    public override T ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (valueProperty.PropertyType != typeof(string))
        {
            throw new NotSupportedException(
                "Only string-backed Rivet scalars support dictionary keys."
            );
        }
        var converter = (JsonConverter<string>)options.GetConverter(typeof(string));
        return Construct(converter.ReadAsPropertyName(ref reader, typeof(string), options));
    }

    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        T value,
        JsonSerializerOptions options
    )
    {
        if (valueProperty.PropertyType != typeof(string))
        {
            throw new NotSupportedException(
                "Only string-backed Rivet scalars support dictionary keys."
            );
        }
        var key =
            (string?)valueProperty.GetValue(value)
            ?? throw new JsonException("A Rivet scalar dictionary key cannot be null.");
        var converter = (JsonConverter<string>)options.GetConverter(typeof(string));
        converter.WriteAsPropertyName(writer, key, options);
    }

    private T Construct(object? value)
    {
        try
        {
            return (T)_constructor.Invoke([value]);
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is ArgumentException)
        {
            throw new JsonException(
                $"Invalid value for scalar '{typeof(T).Name}'.",
                exception.InnerException
            );
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
