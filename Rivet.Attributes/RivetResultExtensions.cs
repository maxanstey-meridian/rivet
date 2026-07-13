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
        return result switch
        {
            RivetBodyResult body => new RivetActionResult(
                body.StatusCode,
                body.HasBody,
                ToMvc(body)
            ),
            RivetFileResult file => new RivetActionResult(file.StatusCode, true, ToMvc(file)),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
    }

    public static IResult ToResult(this RivetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result switch
        {
            RivetBodyResult body => new RivetMinimalResult(
                body.StatusCode,
                body.HasBody,
                ToMinimal(body)
            ),
            RivetFileResult file => new RivetMinimalResult(file.StatusCode, true, ToMinimal(file)),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
    }

    private static IActionResult ToMvc(RivetBodyResult result)
    {
        if (!result.HasBody)
        {
            return new StatusCodeResult(result.StatusCode);
        }

        if (IsJson(result.ContentType))
        {
            return new RivetMvcJsonResult(
                result.Value,
                result.PayloadType!,
                result.ContentType!,
                result.StatusCode
            );
        }

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            ContentType = result.ContentType,
            Content = (string?)result.Value,
        };
    }

    private static IResult ToMinimal(RivetBodyResult result)
    {
        if (!result.HasBody)
        {
            return Results.StatusCode(result.StatusCode);
        }

        if (IsJson(result.ContentType))
        {
            return new RivetJsonResult(
                result.Value,
                result.PayloadType!,
                result.ContentType!,
                result.StatusCode
            );
        }

        return Results.Text(
            (string?)result.Value,
            contentType: result.ContentType,
            statusCode: result.StatusCode
        );
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

    private sealed class RivetJsonResult(
        object? value,
        Type payloadType,
        string contentType,
        int statusCode
    ) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var options = httpContext
                .RequestServices.GetRequiredService<
                    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>
                >()
                .Value.SerializerOptions;
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = contentType;
            return JsonSerializer.SerializeAsync(
                httpContext.Response.Body,
                value,
                payloadType,
                options,
                httpContext.RequestAborted
            );
        }
    }

    private sealed class RivetMvcJsonResult(
        object? value,
        Type payloadType,
        string contentType,
        int statusCode
    ) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            var options = context
                .HttpContext.RequestServices.GetRequiredService<
                    IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>
                >()
                .Value.JsonSerializerOptions;
            context.HttpContext.Response.StatusCode = statusCode;
            context.HttpContext.Response.ContentType = contentType;
            return JsonSerializer.SerializeAsync(
                context.HttpContext.Response.Body,
                value,
                payloadType,
                options,
                context.HttpContext.RequestAborted
            );
        }
    }

    private static bool IsJson(string? contentType) =>
        contentType is not null
        && (
            contentType
                .Split(';', 2)[0]
                .Trim()
                .Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType
                .Split(';', 2)[0]
                .Trim()
                .EndsWith("+json", StringComparison.OrdinalIgnoreCase)
        );

    private static void ValidateResponseState(HttpResponse response, int statusCode, bool hasBody)
    {
        if (response.HasStarted)
        {
            if (!hasBody && response.StatusCode == statusCode)
            {
                return;
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
    }

    private sealed class RivetActionResult(int statusCode, bool hasBody, IActionResult inner)
        : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            ValidateResponseState(context.HttpContext.Response, statusCode, hasBody);
            if (context.HttpContext.Response.HasStarted)
            {
                return Task.CompletedTask;
            }

            context.HttpContext.Response.StatusCode = statusCode;
            return inner.ExecuteResultAsync(context);
        }
    }

    private sealed class RivetMinimalResult(int statusCode, bool hasBody, IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ValidateResponseState(httpContext.Response, statusCode, hasBody);
            if (httpContext.Response.HasStarted)
            {
                return Task.CompletedTask;
            }

            httpContext.Response.StatusCode = statusCode;
            return inner.ExecuteAsync(httpContext);
        }
    }
}
