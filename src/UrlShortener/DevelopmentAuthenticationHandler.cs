using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace UrlShortener;

public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var requestedRole = Request.Headers["X-Dev-Role"].ToString();
        if (string.IsNullOrWhiteSpace(requestedRole))
            requestedRole = "Reviewer";

        if (requestedRole is not ("User" or "Reviewer"))
        {
            var fallbackRole = "Reviewer";
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "local-developer"),
                new Claim(ClaimTypes.Name, "Local Developer"),
                new Claim(ClaimTypes.Role, fallbackRole),
                new Claim("roles", fallbackRole)
            ], Scheme.Name, ClaimTypes.Name, "roles");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }

        var finalIdentity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "local-developer"),
            new Claim(ClaimTypes.Name, "Local Developer"),
            new Claim(ClaimTypes.Role, requestedRole),
            new Claim("roles", requestedRole)
        ], Scheme.Name, ClaimTypes.Name, "roles");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(finalIdentity), Scheme.Name)));
    }
}
