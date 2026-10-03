namespace Rivet;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

/// <summary>A declared response media type, parsed once when the contract is published.</summary>
internal sealed record ResponseRepresentation(
    string MediaType,
    bool IsBinary,
    bool IsWellFormed,
    bool IsJson,
    bool IsUtf8OrUnspecified
)
{
    public static readonly ResponseRepresentation Json = Create("application/json", false);

    /// <summary>Representations are matched on the media type without parameters.</summary>
    public string Key { get; } = KeyOf(MediaType);

    /// <summary>A declared range such as <c>image/*</c> or <c>*/*</c>, not one concrete type.</summary>
    public bool IsRange { get; } =
        MediaTypeHeaderValue.TryParse(MediaType, out var parsed)
        && (parsed.MatchesAllTypes || parsed.MatchesAllSubTypes);

    /// <summary>Whether this declared range admits the concrete runtime type.</summary>
    public bool Covers(string mediaType) =>
        IsRange
        && IsConcrete(mediaType)
        && MediaTypeHeaderValue.Parse(mediaType).IsSubsetOf(MediaTypeHeaderValue.Parse(MediaType));

    public static bool IsConcrete(string? mediaType) =>
        MediaTypeHeaderValue.TryParse(mediaType, out var parsed)
        && !parsed.MatchesAllTypes
        && !parsed.MatchesAllSubTypes;

    public static string KeyOf(string mediaType) =>
        MediaTypeHeaderValue.TryParse(mediaType, out var parsed)
            ? parsed.MediaType.Value ?? mediaType
            : mediaType;

    public static ResponseRepresentation Create(string mediaType, bool isBinary)
    {
        if (!MediaTypeHeaderValue.TryParse(mediaType, out var parsed))
        {
            return new(mediaType, isBinary, false, false, false);
        }

        var isJson =
            parsed.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase)
            || parsed.Type.Equals("application", StringComparison.OrdinalIgnoreCase)
                && parsed.SubTypeWithoutSuffix.Equals("json", StringComparison.OrdinalIgnoreCase);
        var isUtf8OrUnspecified =
            !parsed.Charset.HasValue
            || parsed.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase);
        return new(mediaType, isBinary, true, isJson, isUtf8OrUnspecified);
    }
}

/// <summary>
/// One declared response. <see cref="Body"/> is the representation Success/Error write
/// with (null when ambiguous); <see cref="Binary"/> holds the File(...) representations.
/// </summary>
internal sealed record ResponseContract(
    string StatusKey,
    int? StatusCode,
    Type? PayloadType,
    int RepresentationCount,
    ResponseRepresentation? Body,
    IReadOnlyList<ResponseRepresentation> Binary
);

internal sealed record ResponseSet(
    IReadOnlyDictionary<int, ResponseContract> Exact,
    IReadOnlyDictionary<int, ResponseContract> Ranges,
    ResponseContract? Default
);

internal sealed record EndpointContract(
    string Method,
    string Route,
    ResponseContract? Success,
    ResponseSet AlternateResponses
);

internal sealed record RouteResponseContent(
    string StatusKey,
    string MediaType,
    Type? PayloadType,
    bool IsBinary
);

/// <summary>The terminals every bound definition shares: Error and File.</summary>
public abstract class BoundRouteDefinitionBase
{
    private protected BoundRouteDefinitionBase(EndpointContract contract) => Contract = contract;

    private protected EndpointContract Contract { get; }

    public RivetResult Error(int statusCode) => RivetTerminal.Error(Contract, statusCode);

    public RivetResult Error<TError>(int statusCode, TError payload) =>
        RivetTerminal.Error(Contract, statusCode, payload);

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null,
        bool inline = false
    ) =>
        RivetTerminal.File(
            Contract,
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType,
            inline
        );

    public RivetResult File(
        Stream content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null,
        bool inline = false
    ) =>
        RivetTerminal.File(
            Contract,
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType,
            inline
        );

    public RivetResult File(
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null,
        string? contentType = null,
        bool inline = false
    ) =>
        RivetTerminal.PhysicalFile(
            Contract,
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType,
            inline
        );
}

public sealed class BoundRouteDefinition<TOutput> : BoundRouteDefinitionBase
{
    internal BoundRouteDefinition(EndpointContract contract)
        : base(contract) { }

    public RivetResult Success(TOutput payload) => RivetTerminal.Success(Contract, payload);
}

public sealed class BoundRouteDefinition : BoundRouteDefinitionBase
{
    internal BoundRouteDefinition(EndpointContract contract)
        : base(contract) { }

    public RivetResult Success() => RivetTerminal.Success(Contract);
}

public sealed class BoundFileRouteDefinition : BoundRouteDefinitionBase
{
    internal BoundFileRouteDefinition(EndpointContract contract)
        : base(contract) { }
}

internal static class RivetTerminal
{
    internal static RivetResult Success(EndpointContract contract)
    {
        var response = RequireSuccess(contract);
        if (response.PayloadType is not null)
        {
            throw Violation(
                contract,
                $"declares payload type '{response.PayloadType.FullName}' for success status {response.StatusCode}"
            );
        }

        EnsureBodyless(contract, response);
        return new RivetBodyResult(contract, response.StatusCode!.Value, null, null, null, false);
    }

    internal static RivetResult Success<T>(EndpointContract contract, T payload)
    {
        var response = RequireSuccess(contract);
        ValidatePayload(contract, response, payload);
        return Body(contract, response, payload);
    }

    internal static RivetResult Error(EndpointContract contract, int statusCode)
    {
        var response = ResolveError(contract, statusCode);
        EnsureErrorIsNotBinary(contract, response);
        if (response.PayloadType is not null)
        {
            throw Violation(
                contract,
                $"declares payload type '{response.PayloadType.FullName}' for status {statusCode}, but no payload was supplied"
            );
        }

        EnsureBodyless(contract, response);
        return new RivetBodyResult(contract, statusCode, null, null, null, false);
    }

    internal static RivetResult Error<T>(EndpointContract contract, int statusCode, T payload)
    {
        var response = ResolveError(contract, statusCode);
        EnsureErrorIsNotBinary(contract, response);
        ValidatePayload(contract, response, payload);
        return Body(contract, response with { StatusCode = statusCode }, payload);
    }

    internal static RivetResult File(
        EndpointContract contract,
        byte[] content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag,
        string? contentType,
        bool inline
    )
    {
        if (content is null)
        {
            throw Violation(contract, "received a null byte-array file source");
        }

        return CreateFile(
            contract,
            new RivetFileBytes(content),
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType,
            inline
        );
    }

    internal static RivetResult File(
        EndpointContract contract,
        Stream content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag,
        string? contentType,
        bool inline
    )
    {
        if (content is null)
        {
            throw Violation(contract, "received a null stream file source");
        }

        if (!content.CanRead)
        {
            throw Violation(contract, "received an unreadable file stream");
        }

        if (enableRangeProcessing && !content.CanSeek)
        {
            throw Violation(contract, "cannot enable range processing for a non-seekable stream");
        }

        return CreateFile(
            contract,
            new RivetFileStream(content),
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType,
            inline
        );
    }

    internal static RivetResult PhysicalFile(
        EndpointContract contract,
        string physicalPath,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag,
        string? contentType,
        bool inline
    )
    {
        if (string.IsNullOrWhiteSpace(physicalPath))
        {
            throw Violation(contract, "received an empty physical file path");
        }

        if (!Path.IsPathRooted(physicalPath))
        {
            throw Violation(contract, "requires an absolute physical file path");
        }

        return CreateFile(
            contract,
            new RivetPhysicalFile(physicalPath),
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag,
            contentType,
            inline
        );
    }

    private static RivetResult CreateFile(
        EndpointContract contract,
        RivetFileSource source,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag,
        string? contentType,
        bool inline
    )
    {
        var response = RequireSuccess(contract);
        if (!AllowsBody(response.StatusCode!.Value))
        {
            throw Violation(
                contract,
                $"cannot attach a file body to status {response.StatusCode.Value}"
            );
        }

        if (response.Binary.Count == 0)
        {
            throw Violation(contract, "does not declare a binary/file success response");
        }

        if (contentType is null && response.Binary.Count > 1)
        {
            throw Violation(
                contract,
                "declares multiple binary/file success representations; File(...) is ambiguous between "
                    + string.Join(", ", response.Binary.Select(item => $"'{item.MediaType}'"))
            );
        }

        // An exact declaration wins; otherwise a declared media range (image/*, */*) admits
        // any concrete type inside it, for files whose type is only known at runtime.
        var representation = contentType is null
            ? response.Binary[0]
            : response.Binary.FirstOrDefault(item =>
                item.Key.Equals(
                    ResponseRepresentation.KeyOf(contentType),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                ?? response.Binary.FirstOrDefault(item => item.Covers(contentType))
                ?? throw Violation(
                    contract,
                    $"does not declare binary/file success content type '{contentType}'"
                );
        if (representation.IsRange && !ResponseRepresentation.IsConcrete(contentType))
        {
            throw Violation(
                contract,
                $"declares the media range '{representation.MediaType}'; File(...) needs the concrete contentType it serves"
            );
        }

        if (!representation.IsWellFormed)
        {
            throw Violation(
                contract,
                $"declares malformed binary/file success content type '{representation.MediaType}'"
            );
        }

        EntityTagHeaderValue? parsedEntityTag = null;
        if (
            entityTag is not null
            && (
                !EntityTagHeaderValue.TryParse(entityTag, out parsedEntityTag)
                || parsedEntityTag.Equals(EntityTagHeaderValue.Any)
            )
        )
        {
            throw Violation(contract, $"received malformed entity tag '{entityTag}'");
        }

        return new RivetFileResult(
            response.StatusCode!.Value,
            source,
            contentType ?? representation.MediaType,
            downloadName,
            enableRangeProcessing,
            lastModified,
            parsedEntityTag,
            inline
        );
    }

    private static RivetBodyResult Body<T>(
        EndpointContract contract,
        ResponseContract response,
        T payload
    )
    {
        if (!AllowsBody(response.StatusCode!.Value))
        {
            throw Violation(
                contract,
                $"cannot attach a body to status {response.StatusCode.Value}"
            );
        }

        EnsureNotFile(contract, response);
        var representation =
            response.Body
            ?? throw Violation(
                contract,
                $"declares multiple non-JSON representations for status '{response.StatusKey}' without an explicit primary runtime content type"
            );
        if (!representation.IsWellFormed)
        {
            throw Violation(
                contract,
                $"declares malformed content type '{representation.MediaType}' for status '{response.StatusKey}'"
            );
        }

        if (representation.IsJson && !representation.IsUtf8OrUnspecified)
        {
            throw Violation(
                contract,
                $"declares unsupported JSON content type '{representation.MediaType}' for status '{response.StatusKey}'; JSON responses are UTF-8"
            );
        }

        if (!representation.IsJson && payload is not string)
        {
            throw Violation(
                contract,
                $"declares textual content type '{representation.MediaType}' but payload type '{typeof(T).FullName}' is not string"
            );
        }

        return new RivetBodyResult(
            contract,
            response.StatusCode!.Value,
            payload,
            response.PayloadType,
            representation.MediaType,
            representation.IsJson
        );
    }

    private static ResponseContract RequireSuccess(EndpointContract contract)
    {
        if (contract.Success is null)
        {
            throw Violation(contract, "suppresses its implicit success response");
        }

        return contract.Success;
    }

    private static void EnsureNotFile(EndpointContract contract, ResponseContract response)
    {
        if (response.Binary.Count > 0)
        {
            throw Violation(
                contract,
                "declares a binary/file success response; use File(...) instead"
            );
        }
    }

    private static void EnsureBodyless(EndpointContract contract, ResponseContract response)
    {
        if (response.RepresentationCount > 0)
        {
            throw Violation(
                contract,
                $"declares response content for status '{response.StatusKey}', but no payload was supplied"
            );
        }
    }

    private static void EnsureErrorIsNotBinary(EndpointContract contract, ResponseContract response)
    {
        if (response.Binary.Count > 0)
        {
            throw Violation(
                contract,
                $"declares a binary alternate response for status '{response.StatusKey}', which Error(...) cannot execute"
            );
        }
    }

    private static ResponseContract ResolveError(EndpointContract contract, int statusCode)
    {
        if (statusCode is < 100 or > 599)
        {
            throw Violation(contract, $"received invalid HTTP status code {statusCode}");
        }

        if (contract.Success?.StatusCode == statusCode)
        {
            throw Violation(
                contract,
                $"cannot return success status {statusCode} through Error(...)"
            );
        }

        if (contract.AlternateResponses.Exact.TryGetValue(statusCode, out var exact))
        {
            return exact;
        }

        if (contract.AlternateResponses.Ranges.TryGetValue(statusCode / 100, out var range))
        {
            return range with { StatusCode = statusCode };
        }

        if (contract.AlternateResponses.Default is { } fallback)
        {
            return fallback with { StatusCode = statusCode };
        }

        throw Violation(contract, $"returned undeclared status code {statusCode}");
    }

    private static void ValidatePayload<T>(
        EndpointContract contract,
        ResponseContract response,
        T payload
    )
    {
        var expectedType = response.PayloadType;
        if (expectedType is null)
        {
            throw Violation(
                contract,
                $"declares no payload for status {response.StatusCode}, but a payload was supplied"
            );
        }

        if (payload is null)
        {
            throw Violation(
                contract,
                $"received a null payload for typed status {response.StatusCode}; CLR nullable-reference intent is unavailable at runtime"
            );
        }

        if (!expectedType.IsAssignableFrom(typeof(T)))
        {
            throw Violation(
                contract,
                $"declares payload type '{expectedType.FullName}' for status {response.StatusCode}, but '{typeof(T).FullName}' was supplied"
            );
        }

        var valueType = payload.GetType();
        if (IsNativeFrameworkResult(expectedType) || IsNativeFrameworkResult(valueType))
        {
            throw Violation(
                contract,
                $"cannot carry native ASP.NET result type '{valueType.FullName}'; use a contract-owned terminal payload"
            );
        }
    }

    /// <summary>
    /// A subtype instance where a type is declared would serialize undeclared members, unless
    /// the serializer treats the declared type polymorphically (attributes or a resolver),
    /// so this runs where the adapter has the host's serializer options.
    /// </summary>
    internal static void EnsureDeclaredRuntimeType(
        RivetBodyResult result,
        JsonSerializerOptions options
    )
    {
        var expectedType = result.PayloadType!;
        var valueType = result.Value!.GetType();
        if (
            valueType == expectedType
            || expectedType == typeof(object)
            || Nullable.GetUnderlyingType(expectedType) == valueType
            || !expectedType.IsAssignableFrom(valueType)
        )
        {
            return;
        }

        var polymorphism = options.GetTypeInfo(expectedType).PolymorphismOptions;
        var declared = polymorphism is null
            ? expectedType.IsInterface || expectedType.IsAbstract
            : polymorphism.DerivedTypes.Any(derived => derived.DerivedType == valueType);
        if (!declared)
        {
            throw Violation(
                result.Contract,
                $"received runtime payload type '{valueType.FullName}' where '{expectedType.FullName}' is declared; undeclared members could reach the wire"
            );
        }
    }

    private static RivetContractViolationException Violation(
        EndpointContract contract,
        string detail
    ) => new($"Route '{contract.Method} {contract.Route}' {detail}.");

    private static bool AllowsBody(int statusCode) =>
        statusCode is >= 200 and not 204 and not 205 and not 304;

    private static bool IsNativeFrameworkResult(Type type) =>
        typeof(IResult).IsAssignableFrom(type) || typeof(IActionResult).IsAssignableFrom(type);
}
