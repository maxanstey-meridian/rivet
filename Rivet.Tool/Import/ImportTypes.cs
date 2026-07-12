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
    IReadOnlyList<GeneratedBrand> Brands
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
    bool IsUnion = false
);

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
    string? WireName = null
);

internal sealed record GeneratedEnumMember(
    string CSharpName,
    string? OriginalName,
    int? IntValue = null
);

internal sealed record GeneratedEnum(string Name, IReadOnlyList<GeneratedEnumMember> Members);

internal sealed record GeneratedBrand(string Name, string InnerType);

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
    bool? RequestBodyRequired = null
)
{
    public IReadOnlyList<string> UnsupportedMarkers { get; init; } = UnsupportedMarkers ?? [];
    public IReadOnlyList<TsEndpointExample> RequestExamples { get; init; } = RequestExamples ?? [];
    public IReadOnlyList<GeneratedEndpointResponseExample> ResponseExamples { get; init; } =
        ResponseExamples ?? [];
    public IReadOnlyList<GeneratedResponseHeader> ResponseHeaders { get; init; } =
        ResponseHeaders ?? [];
}

internal sealed record GeneratedErrorResponse(
    int StatusCode,
    string? TypeName,
    string? Description
);

internal sealed record GeneratedEndpointResponseExample(int StatusCode, TsEndpointExample Example);

/// <summary>P2 wave 5: a response header re-emitted as a .WithResponseHeader(...) chain call.</summary>
internal sealed record GeneratedResponseHeader(
    int StatusCode,
    string Name,
    string? Description,
    bool Required
);
