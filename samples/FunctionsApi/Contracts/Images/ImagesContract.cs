using Rivet;

namespace FunctionsApi;

[RivetContract]
public static class ImagesContract
{
    public const string Prefix = "/api";
    public const string TriggerRoute = "images/{format}";
    public const string Route = Prefix + "/" + TriggerRoute;

    public static readonly FileRouteDefinition<ImageInput> Download = Define
        .File<ImageInput>(Route)
        .ContentType("image/png")
        .ResponseBinaryContent(200, "image/jpeg")
        .Returns<ErrorResponse>(404)
        .Anonymous();
}

public sealed record ImageInput(string Format);

public sealed record ErrorResponse(string Code, string Message);
