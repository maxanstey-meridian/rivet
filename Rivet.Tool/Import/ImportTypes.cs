using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

internal sealed record GenericTemplateInfo(
    string Name,
    IReadOnlyList<string> TypeParams,
    Dictionary<string, string> Args
);

internal sealed record SchemaMapResult(
    IReadOnlyList<GeneratedRecord> Records,
    IReadOnlyList<GeneratedEnum> Enums,
    IReadOnlyList<GeneratedBrand> Brands,
    IReadOnlyList<GeneratedScalarSchema> ScalarSchemas
);

internal sealed record GeneratedScalarSchema(
    string Name,
    string ComponentId,
    string? SchemaType,
    string? Format,
    bool IsNullable,
    TsScalarMetadata Metadata,
    bool IsEnum = false,
    string? SchemaRef = null,
    bool IsArray = false,
    string? ItemSchemaRef = null
);

internal sealed record GeneratedRecord(
    string Name,
    IReadOnlyList<RecordProperty> Properties,
    IReadOnlyList<string>? TypeParameters = null,
    string? Description = null,
    PolymorphismInfo? Polymorphism = null,
    string? BaseTypeName = null,
    // Undiscriminated-oneOf wrapper (As* properties) — emitted with [RivetUnion]
    // so the walker re-emits oneOf and the runtime serializes the bare variant.
    bool IsUnion = false,
    string? ComponentId = null,
    bool IsSynthetic = true,
    IReadOnlyList<GeneratedSchemaMetadata>? SchemaMetadata = null
);

internal sealed record GeneratedSchemaMetadata(string Pointer, TsScalarMetadata Metadata);

/// <summary>
/// Reversal of an emitted oneOf + discriminator + mapping union: the record becomes an
/// abstract base carrying [JsonPolymorphic(TypeDiscriminatorPropertyName = ...)] plus one
/// [JsonDerivedType(typeof(Variant), "tag")] per mapping entry.
/// </summary>
internal sealed record PolymorphismInfo(
    string DiscriminatorPropertyName,
    IReadOnlyList<PolymorphicVariantRef> Variants
);

internal sealed record PolymorphicVariantRef(string TypeName, string Tag);

internal sealed record RecordProperty(
    string Name,
    string CSharpType,
    bool IsRequired,
    bool IsDeprecated = false,
    string? Format = null,
    string? DefaultValue = null,
    TsPropertyConstraints? Constraints = null,
    string? Description = null,
    string? Example = null,
    bool IsReadOnly = false,
    bool IsWriteOnly = false,
    // P2 wave 5: non-null for request-header properties — the wire header name with its
    // original casing. Written as [property: RivetHeader("...")], never part of JSON.
    string? HeaderName = null,
    // Non-null when camelCase(Name) is not the spec's property key (snake_case keys,
    // already-PascalCase keys, reserved-member renames). Written as
    // [property: JsonPropertyName("...")] so both the runtime serializer and the
    // walker's re-emit keep the original wire name.
    string? WireName = null,
    // Imported properties pin format presence because CLR primitives infer defaults.
    bool IsFormatSpecified = false,
    string? SchemaType = null,
    string? SchemaRef = null,
    IReadOnlyList<GeneratedSchemaMetadata>? SchemaMetadata = null
);

internal sealed record GeneratedEnumMember(
    string CSharpName,
    string? OriginalName,
    int? IntValue = null
);

internal sealed record GeneratedEnum(
    string Name,
    IReadOnlyList<GeneratedEnumMember> Members,
    string? Format = null,
    string? Description = null,
    string? ComponentId = null,
    bool IsSynthetic = true
);

internal sealed record GeneratedBrand(
    string Name,
    string InnerType,
    string? Format = null,
    string? Description = null,
    string? ComponentId = null,
    bool IsSynthetic = true
);

internal sealed record GeneratedContract(
    string ModuleName,
    string ClassName,
    IReadOnlyList<GeneratedEndpointField> Fields
);

internal sealed record GeneratedEndpointField(
    string FieldName,
    string HttpMethod,
    string Route,
    string? InputType,
    string? OutputType,
    string? Summary,
    string? Description,
    int? SuccessStatus,
    string? SuccessStatusKey,
    string? SuccessResponseDescription,
    IReadOnlyList<GeneratedErrorResponse> ErrorResponses,
    bool IsAnonymous,
    string? SecurityScheme,
    IReadOnlyList<string> UnsupportedMarkers = null!,
    string? FileContentType = null,
    bool IsFormEncoded = false,
    IReadOnlyList<TsEndpointExample>? RequestExamples = null,
    IReadOnlyList<GeneratedEndpointResponseExample>? ResponseExamples = null,
    bool IsFileEndpoint = false,
    string? QueryAuthParameterName = null,
    IReadOnlyList<GeneratedResponseHeader>? ResponseHeaders = null,
    string? BinaryRequestContentType = null,
    string? RequestContentType = null,
    string? ResponseContentType = null,
    string? RequestBodyType = null,
    bool? RequestBodyRequired = null,
    bool RequestBodyPresent = false,
    SecurityRequirements? SecurityRequirements = null,
    IReadOnlyList<GeneratedMediaTypeContent>? RequestContents = null,
    IReadOnlyList<GeneratedResponseMediaTypeContent>? ResponseContents = null,
    IReadOnlyList<GeneratedEndpointParameter>? Parameters = null,
    bool SuppressImplicitResponse = false,
    OpenApiOperationProvenance? Provenance = null
)
{
    public IReadOnlyList<string> UnsupportedMarkers { get; init; } = UnsupportedMarkers ?? [];
    public IReadOnlyList<TsEndpointExample> RequestExamples { get; init; } = RequestExamples ?? [];
    public IReadOnlyList<GeneratedEndpointResponseExample> ResponseExamples { get; init; } =
        ResponseExamples ?? [];
    public IReadOnlyList<GeneratedResponseHeader> ResponseHeaders { get; init; } =
        ResponseHeaders ?? [];
    public IReadOnlyList<GeneratedMediaTypeContent> RequestContents { get; init; } =
        RequestContents ?? [];
    public IReadOnlyList<GeneratedResponseMediaTypeContent> ResponseContents { get; init; } =
        ResponseContents ?? [];
    public IReadOnlyList<GeneratedEndpointParameter> Parameters { get; init; } = Parameters ?? [];
}

internal sealed record GeneratedMediaTypeContent(
    string MediaType,
    string? TypeName,
    bool IsBinary = false,
    string? SchemaRef = null,
    string? SchemaType = null,
    string? Format = null,
    bool IsFormatSpecified = false
);

internal sealed record GeneratedResponseMediaTypeContent(
    int StatusCode,
    string StatusKey,
    string MediaType,
    string? TypeName,
    bool IsBinary = false,
    string? SchemaRef = null,
    string? SchemaType = null,
    string? Format = null,
    bool IsFormatSpecified = false,
    string? SchemaDescription = null
);

internal sealed record GeneratedErrorResponse(
    int StatusCode,
    string StatusKey,
    string? TypeName,
    string? Description
);

internal sealed record GeneratedEndpointResponseExample(
    int StatusCode,
    string StatusKey,
    TsEndpointExample Example
);

internal sealed record GeneratedEndpointParameter(
    string Name,
    string Location,
    string TypeName,
    bool Required,
    string? SchemaType,
    string? Format,
    bool IsFormatSpecified,
    string? MetadataJson,
    string? SchemaRef = null
);

/// <summary>P2 wave 5: a response header re-emitted as a .WithResponseHeader(...) chain call.</summary>
internal sealed record GeneratedResponseHeader(
    int StatusCode,
    string StatusKey,
    string Name,
    string TypeName,
    string? SchemaType,
    string? Format,
    bool IsFormatSpecified,
    string? Description,
    bool Required,
    string? SchemaRef = null,
    string? SchemaExamplesJson = null,
    string? ExampleJson = null,
    string? ExamplesJson = null,
    bool Deprecated = false,
    string? Style = null,
    bool? Explode = null,
    bool AllowReserved = false,
    bool AllowEmptyValue = false,
    string? ContentType = null
);
