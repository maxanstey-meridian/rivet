namespace Rivet;

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

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

    // Minimal APIs read their JSON options from the Http.Json registration, not MVC's.
    private sealed class RivetMinimalResult(RivetResult result) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var options = httpContext
                .RequestServices.GetRequiredService<
                    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>
                >()
                .Value.SerializerOptions;
            if (!BeginResponse(httpContext, result, options, RivetHostJsonOptions.MinimalApi))
            {
                return Task.CompletedTask;
            }

            return result switch
            {
                RivetBodyResult { HasBody: false } => Task.CompletedTask,
                RivetBodyResult { IsJson: true } body => WriteJsonAsync(httpContext, body, options),
                RivetBodyResult body => Results
                    .Text((string?)body.Value, body.ContentType)
                    .ExecuteAsync(httpContext),
                RivetFileResult file => ToFileResult(file).ExecuteAsync(httpContext),
                _ => throw new ArgumentOutOfRangeException(nameof(httpContext)),
            };
        }
    }

    internal static Task WriteJsonAsync(
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
    /// Everything an adapter checks before it writes: the host's JSON options match the spec,
    /// the host hasn't committed a conflicting response, and the response carries the headers
    /// the contract declares required. Sets the declared status and Rivet's file headers.
    /// Returns false when the response has started and there is nothing left to write. A
    /// refused stream file is disposed: the host never takes it over.
    /// </summary>
    internal static bool BeginResponse(
        HttpContext httpContext,
        RivetResult result,
        JsonSerializerOptions options,
        string registration
    )
    {
        try
        {
            RivetHostJsonOptions.Ensure(options, registration);
            var response = httpContext.Response;
            var writes = SetStatus(response, result);
            if (writes && result is RivetFileResult file)
            {
                WriteFileHeaders(response, file);
            }

            EnsureRequiredHeaders(response, result);
            return writes;
        }
        catch when (result is RivetFileResult { Source: RivetFileStream stream })
        {
            stream.Content.Dispose();
            throw;
        }
    }

    private static bool SetStatus(HttpResponse response, RivetResult result)
    {
        var statusCode = result.StatusCode;
        var hasBody = result is not RivetBodyResult { HasBody: false };

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

    /// <summary>
    /// Headers the handler sets on the response before returning count, and so do the ones
    /// the result itself writes: the content type, and for a file the validators,
    /// disposition, range support and length its arguments ask for. A header a middleware
    /// adds in <c>OnStarting</c> arrives after this check and doesn't count.
    /// </summary>
    private static void EnsureRequiredHeaders(HttpResponse response, RivetResult result)
    {
        var missing = result
            .RequiredHeaders.Where(name =>
                StringValues.IsNullOrEmpty(response.Headers[name]) && !Writes(result, name)
            )
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var (header, pronoun) = missing.Length == 1 ? ("header", "it") : ("headers", "them");
        throw new RivetContractViolationException(
            $"Route '{result.Contract.Method} {result.Contract.Route}' declares required response {header} "
                + string.Join(", ", missing.Select(name => $"'{name}'"))
                + $" for status {result.StatusCode}, but the response doesn't carry {pronoun}. "
                + $"Set {pronoun} on HttpContext.Response.Headers before returning the result"
                + (
                    result is RivetFileResult
                        ? " (a file's ETag, Last-Modified, Content-Disposition and Accept-Ranges come from File(...)'s entityTag:, lastModified:, downloadName: and enableRangeProcessing:)"
                        : ""
                )
                + ", or drop required: true from .WithResponseHeader(...) if the header is optional."
        );
    }

    private static bool Writes(RivetResult result, string name) =>
        result switch
        {
            RivetBodyResult body => body.HasBody && Is(name, HeaderNames.ContentType),
            RivetFileResult file => Is(name, HeaderNames.ContentType)
                || Is(name, HeaderNames.ETag) && file.EntityTag is not null
                || Is(name, HeaderNames.LastModified) && file.LastModified is not null
                || Is(name, HeaderNames.ContentDisposition) && file.DownloadName is not null
                || Is(name, HeaderNames.AcceptRanges) && file.EnableRangeProcessing
                || Is(name, HeaderNames.ContentLength)
                    && file.Source
                        is RivetFileBytes
                            or RivetPhysicalFile
                            or RivetFileStream { Content.CanSeek: true },
            _ => false,
        };

    private static bool Is(string name, string header) =>
        name.Equals(header, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The contract owns the file's type, so the browser must not sniff another one. An
    /// inline file keeps its name in an inline disposition; ASP.NET's download name would
    /// force an attachment.
    /// </summary>
    private static void WriteFileHeaders(HttpResponse response, RivetFileResult file)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        if (file.Inline)
        {
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue(
                "inline"
            );
            if (file.DownloadName is not null)
            {
                disposition.SetHttpFileName(file.DownloadName);
            }

            response.Headers.ContentDisposition = disposition.ToString();
        }
    }

    private static string? AttachmentName(RivetFileResult result) =>
        result.Inline ? null : result.DownloadName;

    /// <summary>
    /// The one file path for both hosts: <see cref="RivetActionResult"/> runs this result on
    /// MVC too, so ranges, validators and disposition can't drift between them. MVC has no
    /// callback file result, and its replaceable file executors are bypassed.
    /// </summary>
    internal static IResult ToFileResult(RivetFileResult result) =>
        result.Source switch
        {
            RivetFileBytes bytes => Results.Bytes(
                bytes.Content,
                result.ContentType,
                AttachmentName(result),
                result.EnableRangeProcessing,
                result.LastModified,
                result.EntityTag
            ),
            RivetFileStream stream => Results.Stream(
                stream.Content,
                result.ContentType,
                AttachmentName(result),
                result.LastModified,
                result.EntityTag,
                result.EnableRangeProcessing
            ),
            RivetPhysicalFile physical => TypedResults.PhysicalFile(
                physical.Path,
                result.ContentType,
                AttachmentName(result),
                result.LastModified,
                result.EntityTag,
                result.EnableRangeProcessing
            ),
            RivetFileWriter writer => Results.Stream(
                writer.Writer,
                result.ContentType,
                AttachmentName(result),
                result.LastModified,
                result.EntityTag
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
}
