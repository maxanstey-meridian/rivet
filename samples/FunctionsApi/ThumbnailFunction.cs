using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Rivet;

namespace FunctionsApi;

public sealed class ThumbnailFunction
{
    private static readonly Dictionary<ThumbnailSize, int> _widths = new()
    {
        [ThumbnailSize.Small] = 64,
        [ThumbnailSize.Medium] = 128,
        [ThumbnailSize.Large] = 256,
    };

    // The worker fills format and size from the route values and fit from the query, each by
    // its parameter's name; --check holds those names to the contract's inputs (RIV4006).
    [Function("GetThumbnail")]
    public IActionResult Run(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = ImagesContract.ThumbnailTriggerRoute
        )]
            HttpRequest _,
        string format,
        ThumbnailSize size,
        ThumbnailFit fit
    )
    {
        var endpoint = ImagesContract.Thumbnail.Bind(new ThumbnailInput(format, size, fit));
        if (format is not ("png" or "jpeg") || !_widths.TryGetValue(size, out var width))
        {
            return endpoint
                .Error(
                    404,
                    new ErrorResponse("not_found", "Choose png or jpeg, and size 0, 1 or 2.")
                )
                .ToActionResult();
        }
        return endpoint.Success(new ThumbnailDto(format, size, fit, width)).ToActionResult();
    }
}
