namespace Rivet;

using Microsoft.Net.Http.Headers;

/// <summary>A contract-owned HTTP response that can be adapted at the host boundary.</summary>
public abstract class RivetResult
{
    private protected RivetResult(EndpointContract contract, ResponseContract response)
    {
        Contract = contract;
        StatusCode = response.StatusCode!.Value;
        RequiredHeaders = response.RequiredHeaders;
    }

    internal EndpointContract Contract { get; }

    internal int StatusCode { get; }

    /// <summary>The headers the contract declares <c>required: true</c> for this status.</summary>
    internal IReadOnlyList<string> RequiredHeaders { get; }
}

internal sealed class RivetBodyResult(
    EndpointContract contract,
    ResponseContract response,
    object? value,
    Type? payloadType,
    string? contentType,
    bool isJson
) : RivetResult(contract, response)
{
    internal object? Value { get; } = value;
    internal Type? PayloadType { get; } = payloadType;
    internal string? ContentType { get; } = contentType;
    internal bool IsJson { get; } = isJson;
    internal bool HasBody => ContentType is not null;
}

internal abstract record RivetFileSource;

internal sealed record RivetFileBytes(byte[] Content) : RivetFileSource;

internal sealed record RivetFileStream(Stream Content) : RivetFileSource;

internal sealed record RivetPhysicalFile(string Path) : RivetFileSource;

/// <summary>A body the handler writes into the response as it goes.</summary>
internal sealed record RivetFileWriter(Func<Stream, Task> Writer) : RivetFileSource;

internal sealed class RivetFileResult(
    EndpointContract contract,
    ResponseContract response,
    RivetFileSource source,
    string contentType,
    string? downloadName,
    bool enableRangeProcessing,
    DateTimeOffset? lastModified,
    EntityTagHeaderValue? entityTag,
    bool inline
) : RivetResult(contract, response)
{
    internal RivetFileSource Source { get; } = source;
    internal string ContentType { get; } = contentType;
    internal string? DownloadName { get; } = downloadName;
    internal bool EnableRangeProcessing { get; } = enableRangeProcessing;
    internal DateTimeOffset? LastModified { get; } = lastModified;
    internal EntityTagHeaderValue? EntityTag { get; } = entityTag;
    internal bool Inline { get; } = inline;
}
