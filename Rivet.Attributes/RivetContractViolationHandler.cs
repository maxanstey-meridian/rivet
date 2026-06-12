using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Rivet;

/// <summary>
/// The Rivet failure envelope: the same <c>{ code, message }</c> shape the rivet-ts
/// Hono adapter emits for its enforcement failures, so both runtimes report contract
/// violations identically on the wire.
/// </summary>
public sealed record RivetErrorEnvelope(string Code, string Message);

/// <summary>
/// Maps <see cref="RivetContractViolationException"/> to
/// <c>500 { "code": "contract_violation", "message": ... }</c> instead of ASP.NET's
/// default empty-body 500. Opt-in:
/// <code>
/// builder.Services.AddExceptionHandler&lt;RivetContractViolationHandler&gt;();
/// builder.Services.AddProblemDetails(); // fallback for everything else
/// app.UseExceptionHandler();
/// </code>
/// </summary>
public sealed class RivetContractViolationHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not RivetContractViolationException violation)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(
            new RivetErrorEnvelope("contract_violation", violation.Message), cancellationToken);
        return true;
    }
}
