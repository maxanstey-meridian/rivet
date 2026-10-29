using Rivet;

namespace FunctionsApi;

[RivetContract]
public static class ImagesContract
{
    public const string Prefix = "/api";
    public const string TriggerRoute = "images/{format}";
    public const string Route = Prefix + "/" + TriggerRoute;
    public const string ThumbnailTriggerRoute = "images/{format}/thumbnails/{size}";

    public static readonly FileRouteDefinition<ImageInput> Download = Define
        .File<ImageInput>(Route)
        .ProducesFile("image/png")
        .ResponseBinaryContent(200, "image/jpeg")
        .Returns<ErrorResponse>(404)
        .Anonymous();

    // A route value (size) and a query parameter (?fit=) the worker binds by name.
    public static readonly RouteDefinition<ThumbnailInput, ThumbnailDto> Thumbnail = Define
        .Get<ThumbnailInput, ThumbnailDto>(Prefix + "/" + ThumbnailTriggerRoute)
        .Returns<ErrorResponse>(404)
        .Anonymous();
}

public sealed record ImageInput(string Format);

public sealed record ErrorResponse(string Code, string Message);

// Plain enums, integers on the wire: the worker parses a route or query value as JSON, so a
// string enum's member names never bind (RIV4005).
public enum ThumbnailSize
{
    Small,
    Medium,
    Large,
}

public enum ThumbnailFit
{
    Cover,
    Contain,
}

public sealed record ThumbnailInput(string Format, ThumbnailSize Size, ThumbnailFit Fit);

public sealed record ThumbnailDto(string Format, ThumbnailSize Size, ThumbnailFit Fit, int Width);
