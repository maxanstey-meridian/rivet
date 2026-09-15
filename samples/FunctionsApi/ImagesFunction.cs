using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Rivet;

namespace FunctionsApi;

public sealed class ImagesFunction
{
    [Function("DownloadImage")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = ImagesContract.TriggerRoute)]
            HttpRequest _,
        string format
    )
    {
        var endpoint = ImagesContract.Download.Bind(new ImageInput(format));
        // Tiny sample payloads demonstrate transport; no external storage is required.
        if (format == "png")
        {
            return endpoint.File([137, 80, 78, 71], contentType: "image/png").ToActionResult();
        }
        if (format == "jpeg")
        {
            return endpoint.File([255, 216, 255, 217], contentType: "image/jpeg").ToActionResult();
        }
        return endpoint
            .Error(404, new ErrorResponse("not_found", "Choose png or jpeg."))
            .ToActionResult();
    }
}
