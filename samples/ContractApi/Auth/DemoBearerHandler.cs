using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ContractApi.Auth;

/// <summary>
/// The sample's stand-in for a real bearer scheme (JwtBearer, say): any non-empty token
/// authenticates, so the sample runs without an identity provider. It reads the
/// <c>Authorization: Bearer</c> header, or the <c>token</c> query parameter a
/// <c>.QueryAuth()</c> endpoint takes for clients that can't set headers. Swap it for a
/// real handler and nothing else changes: the [Authorize] attributes and the contract's
/// <c>.Secure("bearer")</c> are what <c>--check</c> compares.
/// </summary>
public sealed class DemoBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "bearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : Request.Query["token"].ToString();

        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, token)], SchemeName);
        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)
            )
        );
    }
}
