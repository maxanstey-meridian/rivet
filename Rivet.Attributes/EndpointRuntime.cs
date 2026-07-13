namespace Rivet;

using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

internal sealed record ResponseRepresentation(string MediaType, bool IsBinary);

internal sealed record ResponseContract(
    string StatusKey,
    int? StatusCode,
    Type? PayloadType,
    string? PreferredContentType,
    IReadOnlyDictionary<string, ResponseRepresentation> Content
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

public sealed class BoundRouteDefinition<TOutput>
{
    private readonly EndpointContract _contract;

    internal BoundRouteDefinition(EndpointContract contract) => _contract = contract;

    public RivetResult Success(TOutput payload) => RivetTerminal.Success(_contract, payload);

    public RivetResult Error(int statusCode) => RivetTerminal.Error(_contract, statusCode);

    public RivetResult Error<TError>(int statusCode, TError payload) =>
        RivetTerminal.Error(_contract, statusCode, payload);

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.File(
            _contract,
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        Stream content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.File(
            _contract,
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.PhysicalFile(
            _contract,
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );
}

public sealed class BoundRouteDefinition
{
    private readonly EndpointContract _contract;

    internal BoundRouteDefinition(EndpointContract contract) => _contract = contract;

    public RivetResult Success() => RivetTerminal.Success(_contract);

    public RivetResult Error(int statusCode) => RivetTerminal.Error(_contract, statusCode);

    public RivetResult Error<TError>(int statusCode, TError payload) =>
        RivetTerminal.Error(_contract, statusCode, payload);

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.File(
            _contract,
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        Stream content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.File(
            _contract,
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.PhysicalFile(
            _contract,
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );
}

public sealed class BoundFileRouteDefinition
{
    private readonly EndpointContract _contract;

    internal BoundFileRouteDefinition(EndpointContract contract) => _contract = contract;

    public RivetResult Error(int statusCode) => RivetTerminal.Error(_contract, statusCode);

    public RivetResult Error<TError>(int statusCode, TError payload) =>
        RivetTerminal.Error(_contract, statusCode, payload);

    public RivetResult File(
        byte[] content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.File(
            _contract,
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        Stream content,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.File(
            _contract,
            content,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );

    public RivetResult File(
        string physicalPath,
        string? downloadName = null,
        bool enableRangeProcessing = false,
        DateTimeOffset? lastModified = null,
        string? entityTag = null
    ) =>
        RivetTerminal.PhysicalFile(
            _contract,
            physicalPath,
            downloadName,
            enableRangeProcessing,
            lastModified,
            entityTag
        );
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
        EnsureNotFile(contract, response);
        return new RivetBodyResult(response.StatusCode!.Value, null, null, null, false);
    }

    internal static RivetResult Success<T>(EndpointContract contract, T payload)
    {
        var response = RequireSuccess(contract);
        ValidatePayload(contract, response, typeof(T), payload);
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
        return new RivetBodyResult(statusCode, null, null, null, false);
    }

    internal static RivetResult Error<T>(EndpointContract contract, int statusCode, T payload)
    {
        var response = ResolveError(contract, statusCode);
        EnsureErrorIsNotBinary(contract, response);
        ValidatePayload(contract, response, typeof(T), payload);
        return Body(contract, response with { StatusCode = statusCode }, payload);
    }

    internal static RivetResult File(
        EndpointContract contract,
        byte[] content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
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
            entityTag
        );
    }

    internal static RivetResult File(
        EndpointContract contract,
        Stream content,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
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
            entityTag
        );
    }

    internal static RivetResult PhysicalFile(
        EndpointContract contract,
        string physicalPath,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
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
            entityTag
        );
    }

    private static RivetResult CreateFile(
        EndpointContract contract,
        RivetFileSource source,
        string? downloadName,
        bool enableRangeProcessing,
        DateTimeOffset? lastModified,
        string? entityTag
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

        var binaryRepresentations = response
            .Content.Values.Where(representation => representation.IsBinary)
            .ToArray();
        if (binaryRepresentations.Length == 0)
        {
            throw Violation(contract, "does not declare a binary/file success response");
        }

        if (binaryRepresentations.Length > 1)
        {
            throw Violation(
                contract,
                "declares multiple binary/file success representations; File(...) is ambiguous between "
                    + string.Join(", ", binaryRepresentations.Select(item => $"'{item.MediaType}'"))
            );
        }

        var contentType = binaryRepresentations[0].MediaType;
        if (
            string.IsNullOrWhiteSpace(contentType)
            || !MediaTypeHeaderValue.TryParse(contentType, out _)
        )
        {
            throw Violation(
                contract,
                $"declares malformed binary/file success content type '{contentType}'"
            );
        }

        EntityTagHeaderValue? parsedEntityTag = null;
        if (entityTag is not null)
        {
            try
            {
                parsedEntityTag = EntityTagHeaderValue.Parse(entityTag);
                if (parsedEntityTag == EntityTagHeaderValue.Any)
                {
                    throw new FormatException();
                }
            }
            catch (FormatException)
            {
                throw Violation(contract, $"received malformed entity tag '{entityTag}'");
            }
        }

        return new RivetFileResult(
            response.StatusCode!.Value,
            source,
            contentType,
            downloadName,
            enableRangeProcessing,
            lastModified,
            parsedEntityTag
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
        var representation = SelectRepresentation(contract, response);
        EnsureSupportedJsonCharset(contract, response, representation);
        if (!IsJson(representation.MediaType) && payload is not string)
        {
            throw Violation(
                contract,
                $"declares textual content type '{representation.MediaType}' but payload type '{typeof(T).FullName}' is not string"
            );
        }

        return new RivetBodyResult(
            response.StatusCode!.Value,
            payload,
            response.PayloadType,
            representation.MediaType,
            true
        );
    }

    private static ResponseRepresentation SelectRepresentation(
        EndpointContract contract,
        ResponseContract response
    )
    {
        if (
            response.PreferredContentType is { } preferred
            && response.Content.TryGetValue(preferred, out var preferredRepresentation)
        )
        {
            return preferredRepresentation;
        }

        if (response.Content.Count == 0)
        {
            return new ResponseRepresentation("application/json", false);
        }

        var json = response.Content.Values.FirstOrDefault(item => IsJson(item.MediaType));
        if (json is not null)
        {
            return json;
        }

        if (response.Content.Count == 1)
        {
            return response.Content.Values.Single();
        }

        throw Violation(
            contract,
            $"declares multiple non-JSON representations for status '{response.StatusKey}' without an explicit primary runtime content type"
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
        if (response.Content.Values.Any(representation => representation.IsBinary))
        {
            throw Violation(
                contract,
                "declares a binary/file success response; use File(...) instead"
            );
        }
    }

    private static void EnsureBodyless(EndpointContract contract, ResponseContract response)
    {
        if (response.Content.Count > 0)
        {
            throw Violation(
                contract,
                $"declares response content for status '{response.StatusKey}', but no payload was supplied"
            );
        }
    }

    private static void EnsureErrorIsNotBinary(EndpointContract contract, ResponseContract response)
    {
        if (response.Content.Values.Any(representation => representation.IsBinary))
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
        Type suppliedType,
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

        if (!expectedType.IsAssignableFrom(suppliedType))
        {
            throw Violation(
                contract,
                $"declares payload type '{expectedType.FullName}' for status {response.StatusCode}, but '{suppliedType.FullName}' was supplied"
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

        var polymorphic = expectedType.GetCustomAttribute<JsonPolymorphicAttribute>();
        var registeredDerivedType = expectedType
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Any(attribute => attribute.DerivedType == valueType);
        if (
            valueType == expectedType
            || polymorphic is null && (expectedType.IsInterface || expectedType.IsAbstract)
            || expectedType == typeof(object)
            || Nullable.GetUnderlyingType(expectedType) == valueType
            || polymorphic is not null && registeredDerivedType
        )
        {
            return;
        }

        if (expectedType.IsAssignableFrom(valueType))
        {
            throw Violation(
                contract,
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

    private static bool IsJson(string mediaType)
    {
        var value = mediaType.Split(';', 2)[0].Trim();
        return value.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNativeFrameworkResult(Type type) =>
        typeof(IResult).IsAssignableFrom(type) || typeof(IActionResult).IsAssignableFrom(type);

    private static void EnsureSupportedJsonCharset(
        EndpointContract contract,
        ResponseContract response,
        ResponseRepresentation representation
    )
    {
        if (!IsJson(representation.MediaType))
        {
            return;
        }

        if (
            !MediaTypeHeaderValue.TryParse(representation.MediaType, out var mediaType)
            || mediaType.Charset.HasValue
                && !mediaType.Charset.Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw Violation(
                contract,
                $"declares unsupported JSON content type '{representation.MediaType}' for status '{response.StatusKey}'; JSON responses are UTF-8"
            );
        }
    }
}
