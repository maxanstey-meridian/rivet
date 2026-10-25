namespace Rivet;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// The MVC result <see cref="RivetResultExtensions.ToActionResult"/> returns. A result filter
/// can read its <see cref="StatusCode"/> (also through <see cref="IStatusCodeActionResult"/>)
/// and its <see cref="Value"/>; Rivet writes the response itself, so neither can be changed.
/// </summary>
public sealed class RivetActionResult : IActionResult, IStatusCodeActionResult
{
    private readonly RivetResult _result;

    internal RivetActionResult(RivetResult result)
    {
        _result = result;
        StatusCode = result.StatusCode;
        Value = result is RivetBodyResult body ? body.Value : null;
    }

    /// <summary>
    /// The status the contract declared for this response. For a file, range and conditional
    /// requests can make the host answer 206, 304, 412 or 416 instead; a filter that needs the
    /// status actually sent reads <c>HttpContext.Response.StatusCode</c> in
    /// <c>OnResultExecuted</c>.
    /// </summary>
    public int StatusCode { get; }

    int? IStatusCodeActionResult.StatusCode => StatusCode;

    /// <summary>
    /// The payload passed to <c>Success(...)</c> or <c>Error(...)</c>, before serialization.
    /// <see langword="null"/> for a bodyless response and for a file.
    /// </summary>
    public object? Value { get; }

    public Task ExecuteResultAsync(ActionContext context)
    {
        var httpContext = context.HttpContext;
        var options = httpContext
            .RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;
        if (
            !RivetResultExtensions.BeginResponse(
                httpContext,
                _result,
                options,
                RivetHostJsonOptions.Mvc
            )
        )
        {
            return Task.CompletedTask;
        }

        return _result switch
        {
            RivetBodyResult { HasBody: false } => Task.CompletedTask,
            RivetBodyResult { IsJson: true } body => RivetResultExtensions.WriteJsonAsync(
                httpContext,
                body,
                options
            ),
            RivetBodyResult body => new ContentResult
            {
                ContentType = body.ContentType,
                Content = (string?)body.Value,
            }.ExecuteResultAsync(context),
            RivetFileResult file => RivetResultExtensions
                .ToFileResult(file)
                .ExecuteAsync(httpContext),
            _ => throw new ArgumentOutOfRangeException(nameof(context)),
        };
    }
}
