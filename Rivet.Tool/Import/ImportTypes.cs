using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

internal sealed record GenericTemplateInfo(
    string Name,
    IReadOnlyList<string> TypeParams,
    Dictionary<string, string> Args);

internal sealed record SchemaMapResult(
    IReadOnlyList<GeneratedRecord> Records,
    IReadOnlyList<GeneratedEnum> Enums,
    IReadOnlyList<GeneratedBrand> Brands);

internal sealed record GeneratedRecord(
    string Name,
    IReadOnlyList<RecordProperty> Properties,
    IReadOnlyList<string>? TypeParameters = null,
    string? Description = null);

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
    bool IsWriteOnly = false);

internal sealed record GeneratedEnumMember(
    string CSharpName,
    string? OriginalName,
    int? IntValue = null);

internal sealed record GeneratedEnum(
    string Name,
    IReadOnlyList<GeneratedEnumMember> Members);

internal sealed record GeneratedBrand(
    string Name,
    string InnerType);

internal sealed record GeneratedContract(
    string ClassName,
    IReadOnlyList<GeneratedEndpointField> Fields);

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
    bool IsFormEncoded = false)
{
    public IReadOnlyList<string> UnsupportedMarkers { get; init; } = UnsupportedMarkers ?? [];
}

internal sealed record GeneratedErrorResponse(
    int StatusCode,
    string? TypeName,
    string? Description);
