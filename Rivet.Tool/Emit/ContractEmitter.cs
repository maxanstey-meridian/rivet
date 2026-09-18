using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

public static class ContractEmitter
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new TsTypeJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    internal sealed record RivetContract(
        IReadOnlyList<ContractTypeDefinition> Types,
        IReadOnlyList<ContractEnum> Enums,
        IReadOnlyList<ContractEndpoint>? Endpoints = null
    );

    internal sealed record ContractTypeDefinition(
        string Name,
        IReadOnlyList<string> TypeParameters,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            IReadOnlyList<TsPropertyDefinition>? Properties = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsType? Type = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? Description = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            TsTypeMetadata? Metadata = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            TsScalarMetadata? ScalarMetadata = null
    );

    internal sealed record ContractEnum(
        string Name,
        IReadOnlyList<string>? Values = null,
        // Decimal member literals carried as strings in the IR so legal enum
        // constants beyond Int32 survive without truncation; the property converter
        // below keeps the external contract JSON numeric
        // (planner-constraint:contract-intvalues-stays-numeric-json).
        [property: JsonConverter(
            typeof(ContractEnumIntValuesConverter)
        )] IReadOnlyList<string>? IntValues = null,
        string? Format = null,
        string? Description = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            TsTypeMetadata? Metadata = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            TsScalarMetadata? ScalarMetadata = null
    );

    internal sealed record ContractQueryAuth(string ParameterName);

    internal sealed record ContractEndpoint(
        string Name,
        string HttpMethod,
        string RouteTemplate,
        IReadOnlyList<TsEndpointParam> Params,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsType? ReturnType,
        string ControllerName,
        IReadOnlyList<ContractResponseType> Responses,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? Summary = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? Description = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            EndpointSecurity? Security = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? FileContentType = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? InputTypeName = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            bool IsFormEncoded = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            TsType? RequestType = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            IReadOnlyList<ContractEndpointExample>? RequestExamples = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            bool IsFileEndpoint = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            ContractQueryAuth? QueryAuth = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? BinaryRequestContentType = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? RequestContentTypeOverride = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? ResponseContentTypeOverride = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            IReadOnlyList<TsMediaTypeContent>? RequestContents = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            bool? RequestBodyRequired = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            bool RequestBodyPresent = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            SecurityRequirements? SecurityRequirements = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            OpenApiOperationProvenance? Provenance = null
    );

    internal sealed record ContractResponseType(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StatusCode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StatusKey,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TsType? DataType,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? Description = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            IReadOnlyList<ContractEndpointExample>? Examples = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            IReadOnlyList<TsResponseHeader>? Headers = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            IReadOnlyList<TsMediaTypeContent>? Contents = null
    );

    internal sealed record ContractEndpointExample(
        string MediaType,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Json = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? ComponentExampleId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            string? ResolvedJson = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            IReadOnlyDictionary<string, string>? ReferencedComponents = null
    );

    public static string Emit(
        Dictionary<string, TsTypeDefinition> definitions,
        Dictionary<string, TsType> enums,
        IReadOnlyList<TsEndpointDefinition> endpoints
    )
    {
        var contractEnums = enums
            .Select(kv =>
                kv.Value switch
                {
                    TsType.StringUnion su => new ContractEnum(
                        kv.Key,
                        Values: su.Members,
                        Format: su.Format,
                        Description: su.Description,
                        Metadata: su.Metadata,
                        ScalarMetadata: su.ScalarMetadata
                    ),
                    TsType.IntUnion iu => new ContractEnum(
                        kv.Key,
                        IntValues: iu.Members,
                        Format: iu.Format,
                        Description: iu.Description,
                        Metadata: iu.Metadata,
                        ScalarMetadata: iu.ScalarMetadata
                    ),
                    _ => throw new InvalidOperationException(
                        $"Unsupported enum type: {kv.Value.GetType().Name}"
                    ),
                }
            )
            .ToList();

        var contract = new RivetContract(
            definitions.Values.Select(ToContractTypeDefinition).ToList(),
            contractEnums,
            endpoints.Select(ToContractEndpoint).ToList()
        );

        return JsonSerializer.Serialize(contract, _options);
    }

    internal static ContractEndpoint ToContractEndpoint(TsEndpointDefinition endpoint)
    {
        return new ContractEndpoint(
            endpoint.Name,
            endpoint.HttpMethod,
            endpoint.RouteTemplate,
            endpoint.Params,
            endpoint.ReturnType,
            endpoint.ControllerName,
            ResponseStatusValidation
                .NormalizeIrAndEnsureResponse(endpoint.Responses, endpoint)
                .Select(ToContractResponseType)
                .ToList(),
            endpoint.Summary,
            endpoint.Description,
            endpoint.Security,
            endpoint.FileContentType,
            endpoint.InputTypeName,
            endpoint.IsFormEncoded,
            endpoint.RequestType,
            endpoint.RequestExamples?.Select(ToContractEndpointExample).ToList(),
            endpoint.IsFileEndpoint,
            endpoint.QueryAuth is { } qa ? new ContractQueryAuth(qa.ParameterName) : null,
            endpoint.BinaryRequestContentType,
            endpoint.RequestContentTypeOverride,
            endpoint.ResponseContentTypeOverride,
            endpoint.RequestContents,
            endpoint.RequestBodyRequired,
            endpoint.RequestBodyPresent,
            endpoint.SecurityRequirements,
            endpoint.Provenance
        );
    }

    internal static ContractResponseType ToContractResponseType(TsResponseType response)
    {
        return new ContractResponseType(
            response.StatusCode == 0 ? null : response.StatusCode,
            response.StatusKey,
            response.DataType,
            response.Description,
            response.Examples?.Select(ToContractEndpointExample).ToList(),
            response.Headers,
            response.Contents
        );
    }

    internal static ContractEndpointExample ToContractEndpointExample(TsEndpointExample example)
    {
        return new ContractEndpointExample(
            example.MediaType,
            example.Name,
            example.Json,
            example.ComponentExampleId,
            example.ResolvedJson,
            example.ReferencedComponents
        );
    }

    internal static ContractTypeDefinition ToContractTypeDefinition(TsTypeDefinition definition)
    {
        return new ContractTypeDefinition(
            definition.Name,
            definition.TypeParameters,
            definition.Type is null ? definition.Properties : null,
            definition.Type,
            definition.Description,
            definition.Metadata,
            definition.ScalarMetadata
        );
    }
}

/// <summary>
/// Property-level converter for <see cref="ContractEnum.IntValues"/>: the IR carries
/// decimal member literals as strings so values beyond Int32 survive, but the
/// external contract JSON stays numeric (rivet-contract-schema.json constrains
/// intValues items to type integer)
/// (planner-constraint:contract-intvalues-stays-numeric-json).
/// </summary>
public sealed class ContractEnumIntValuesConverter : JsonConverter<IReadOnlyList<string>?>
{
    public override IReadOnlyList<string>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("intValues must be a JSON array of integer enum values.");
        }

        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException("intValues items must be JSON integers, not strings.");
            }

            // Exact digits: Int64 covers signed/legal ranges; wider unsigned
            // constants keep their raw text (acceptance:numeric-enums-cover-all-legal-underlying-values).
            if (reader.TryGetInt64(out var signed))
            {
                values.Add(signed.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                values.Add(JsonDocument.ParseValue(ref reader).RootElement.GetRawText());
            }
        }

        return values;
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<string>? value,
        JsonSerializerOptions options
    )
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var member in value)
        {
            // The literals are decimal digits produced by the walker; emit them as
            // raw numeric values so the contract JSON keeps its integer shape.
            writer.WriteRawValue(member, skipInputValidation: true);
        }
        writer.WriteEndArray();
    }
}
