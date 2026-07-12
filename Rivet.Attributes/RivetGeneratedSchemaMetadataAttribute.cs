namespace Rivet;

/// <summary>Generated provenance for metadata on a schema site below a CLR symbol.</summary>
[AttributeUsage(
    AttributeTargets.Class
        | AttributeTargets.Struct
        | AttributeTargets.Enum
        | AttributeTargets.Property,
    AllowMultiple = true,
    Inherited = false
)]
public sealed class RivetGeneratedSchemaMetadataAttribute : Attribute
{
    public RivetGeneratedSchemaMetadataAttribute(
        string pointer,
        string? title,
        string? description,
        string? defaultValue,
        string? example,
        string? examples,
        int minLength,
        int maxLength,
        string? pattern,
        double minimum,
        double maximum,
        double exclusiveMinimum,
        double exclusiveMaximum,
        double multipleOf,
        int minItems,
        int maxItems,
        bool uniqueItems,
        string? xmlName,
        string? xmlNamespace,
        string? xmlPrefix,
        bool xmlAttribute,
        bool xmlWrapped
    )
        : this(
            pointer,
            title,
            description,
            defaultValue,
            example,
            examples,
            minLength,
            maxLength,
            pattern,
            minimum,
            maximum,
            exclusiveMinimum,
            exclusiveMaximum,
            multipleOf,
            minItems,
            maxItems,
            uniqueItems,
            xmlName,
            xmlNamespace,
            xmlPrefix,
            xmlAttribute,
            xmlWrapped,
            null,
            false,
            false,
            false,
            false,
            false,
            null
        ) { }

    public RivetGeneratedSchemaMetadataAttribute(
        string pointer,
        string? title,
        string? description,
        string? defaultValue,
        string? example,
        string? examples,
        int minLength,
        int maxLength,
        string? pattern,
        double minimum,
        double maximum,
        double exclusiveMinimum,
        double exclusiveMaximum,
        double multipleOf,
        int minItems,
        int maxItems,
        bool uniqueItems,
        string? xmlName,
        string? xmlNamespace,
        string? xmlPrefix,
        bool xmlAttribute,
        bool xmlWrapped,
        string? format,
        bool formatSpecified,
        bool nullable,
        bool deprecated,
        bool readOnly,
        bool writeOnly,
        string? requiredJson
    )
    {
        Pointer = pointer;
        Title = title;
        Description = description;
        DefaultValue = defaultValue;
        Example = example;
        Examples = examples;
        MinLength = minLength;
        MaxLength = maxLength;
        Pattern = pattern;
        Minimum = minimum;
        Maximum = maximum;
        ExclusiveMinimum = exclusiveMinimum;
        ExclusiveMaximum = exclusiveMaximum;
        MultipleOf = multipleOf;
        MinItems = minItems;
        MaxItems = maxItems;
        UniqueItems = uniqueItems;
        XmlName = xmlName;
        XmlNamespace = xmlNamespace;
        XmlPrefix = xmlPrefix;
        XmlAttribute = xmlAttribute;
        XmlWrapped = xmlWrapped;
        Format = format;
        FormatSpecified = formatSpecified;
        Nullable = nullable;
        Deprecated = deprecated;
        ReadOnly = readOnly;
        WriteOnly = writeOnly;
        RequiredJson = requiredJson;
    }

    public string Pointer { get; }
    public string? Title { get; }
    public string? Description { get; }
    public string? DefaultValue { get; }
    public string? Example { get; }
    public string? Examples { get; }
    public int MinLength { get; }
    public int MaxLength { get; }
    public string? Pattern { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public double ExclusiveMinimum { get; }
    public double ExclusiveMaximum { get; }
    public double MultipleOf { get; }
    public int MinItems { get; }
    public int MaxItems { get; }
    public bool UniqueItems { get; }
    public string? XmlName { get; }
    public string? XmlNamespace { get; }
    public string? XmlPrefix { get; }
    public bool XmlAttribute { get; }
    public bool XmlWrapped { get; }
    public string? Format { get; }
    public bool FormatSpecified { get; }
    public bool Nullable { get; }
    public bool Deprecated { get; }
    public bool ReadOnly { get; }
    public bool WriteOnly { get; }
    public string? RequiredJson { get; }
}
