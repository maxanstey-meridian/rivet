namespace Rivet;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static class RivetResultExtensions
{
    public static IActionResult ToActionResult(this RivetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RivetActionResult(result);
    }

    public static IResult ToResult(this RivetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RivetMinimalResult(result);
    }

    // MVC and Minimal API resolve their JSON options from different registrations.
    private sealed class RivetActionResult(RivetResult result) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            var httpContext = context.HttpContext;
            if (!BeginResponse(httpContext.Response, result))
            {
                return Task.CompletedTask;
            }

            return result switch
            {
                RivetBodyResult { HasBody: false } => Task.CompletedTask,
                RivetBodyResult { IsJson: true } body => WriteJsonAsync(
                    httpContext,
                    body,
                    httpContext
                        .RequestServices.GetRequiredService<
                            IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>
                        >()
                        .Value.JsonSerializerOptions
                ),
                RivetBodyResult body => new ContentResult
                {
                    ContentType = body.ContentType,
                    Content = (string?)body.Value,
                }.ExecuteResultAsync(context),
                RivetFileResult file => ToMvc(file).ExecuteResultAsync(context),
                _ => throw new ArgumentOutOfRangeException(nameof(context)),
            };
        }
    }

    private sealed class RivetMinimalResult(RivetResult result) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (!BeginResponse(httpContext.Response, result))
            {
                return Task.CompletedTask;
            }

            return result switch
            {
                RivetBodyResult { HasBody: false } => Task.CompletedTask,
                RivetBodyResult { IsJson: true } body => WriteJsonAsync(
                    httpContext,
                    body,
                    httpContext
                        .RequestServices.GetRequiredService<
                            IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>
                        >()
                        .Value.SerializerOptions
                ),
                RivetBodyResult body => Results
                    .Text((string?)body.Value, body.ContentType)
                    .ExecuteAsync(httpContext),
                RivetFileResult file => ToMinimal(file).ExecuteAsync(httpContext),
                _ => throw new ArgumentOutOfRangeException(nameof(httpContext)),
            };
        }
    }

    private static Task WriteJsonAsync(
        HttpContext httpContext,
        RivetBodyResult body,
        JsonSerializerOptions options
    )
    {
        RivetTerminal.EnsureDeclaredRuntimeType(body, options);
        return httpContext.Response.WriteAsJsonAsync(
            body.Value,
            body.PayloadType!,
            options,
            body.ContentType,
            httpContext.RequestAborted
        );
    }

    /// <summary>
    /// Checks the host has not already committed a conflicting response, then sets the
    /// declared status. Returns false when the response has started and there is nothing
    /// left to write.
    /// </summary>
    private static bool BeginResponse(HttpResponse response, RivetResult result)
    {
        var (statusCode, hasBody) = result switch
        {
            RivetBodyResult body => (body.StatusCode, body.HasBody),
            RivetFileResult file => (file.StatusCode, true),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };

        if (response.HasStarted)
        {
            if (!hasBody && response.StatusCode == statusCode)
            {
                return false;
            }

            throw new RivetContractViolationException(
                $"The host response has already started with status {response.StatusCode}; "
                    + $"Rivet cannot execute status {statusCode}{(hasBody ? " with a body" : "")}."
            );
        }

        if (response.StatusCode != StatusCodes.Status200OK && response.StatusCode != statusCode)
        {
            throw new RivetContractViolationException(
                $"The host established status {response.StatusCode}, but the Rivet result declares {statusCode}."
            );
        }

        response.StatusCode = statusCode;
        return true;
    }

    private static IActionResult ToMvc(RivetFileResult result)
    {
        FileResult file = result.Source switch
        {
            RivetFileBytes bytes => new FileContentResult(bytes.Content, result.ContentType),
            RivetFileStream stream => new FileStreamResult(stream.Content, result.ContentType),
            RivetPhysicalFile physical => new PhysicalFileResult(physical.Path, result.ContentType),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };

        file.FileDownloadName = result.DownloadName ?? string.Empty;
        file.EnableRangeProcessing = result.EnableRangeProcessing;
        file.LastModified = result.LastModified;
        file.EntityTag = result.EntityTag;
        return file;
    }

    private static IResult ToMinimal(RivetFileResult result) =>
        result.Source switch
        {
            RivetFileBytes bytes => Results.Bytes(
                bytes.Content,
                result.ContentType,
                result.DownloadName,
                result.EnableRangeProcessing,
                result.LastModified,
                result.EntityTag
            ),
            RivetFileStream stream => Results.Stream(
                stream.Content,
                result.ContentType,
                result.DownloadName,
                result.LastModified,
                result.EntityTag,
                result.EnableRangeProcessing
            ),
            RivetPhysicalFile physical => TypedResults.PhysicalFile(
                physical.Path,
                result.ContentType,
                result.DownloadName,
                result.LastModified,
                result.EntityTag,
                result.EnableRangeProcessing
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
}
