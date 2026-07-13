namespace Rivet;

using Microsoft.Net.Http.Headers;

/// <summary>A contract-owned HTTP response that can be adapted at the host boundary.</summary>
public abstract class RivetResult
{
    internal RivetResult() { }
}

internal sealed class RivetBodyResult(
    int statusCode,
    object? value,
    Type? payloadType,
    string? contentType,
    bool hasBody
) : RivetResult
{
    internal int StatusCode { get; } = statusCode;
    internal object? Value { get; } = value;
    internal Type? PayloadType { get; } = payloadType;
    internal string? ContentType { get; } = contentType;
    internal bool HasBody { get; } = hasBody;
}

internal abstract record RivetFileSource;

internal sealed record RivetFileBytes(byte[] Content) : RivetFileSource;

internal sealed record RivetFileStream(Stream Content) : RivetFileSource;

internal sealed record RivetPhysicalFile(string Path) : RivetFileSource;

internal sealed class RivetFileResult(
    int statusCode,
    RivetFileSource source,
    string contentType,
    string? downloadName,
    bool enableRangeProcessing,
    DateTimeOffset? lastModified,
    EntityTagHeaderValue? entityTag
) : RivetResult
{
    internal int StatusCode { get; } = statusCode;
    internal RivetFileSource Source { get; } = source;
    internal string ContentType { get; } = contentType;
    internal string? DownloadName { get; } = downloadName;
    internal bool EnableRangeProcessing { get; } = enableRangeProcessing;
    internal DateTimeOffset? LastModified { get; } = lastModified;
    internal EntityTagHeaderValue? EntityTag { get; } = entityTag;
}
