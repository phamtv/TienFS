// =====================================================================================
// SHARED JWT AUTHENTICATION SETUP
// -------------------------------------------------------------------------------------
// All three services validate the SAME JWT (same signing key, issuer, audience), which
// is what lets a single token obtained from Origination's /api/auth/token endpoint work
// across Funding and Servicing too — a lightweight stand-in for a real identity
// provider (Azure AD / Entra ID, Auth0, etc.).
//
// DEMO SIMPLIFICATION, called out explicitly:
//   - Tokens are signed with a single shared symmetric key (HMAC-SHA256), read from
//     config ("Jwt:SigningKey"). In production this key must be a long, random secret
//     stored in Key Vault — never checked into source control or left as the sample
//     value below.
//   - There's no real user store or password check (see AuthEndpoints.cs) — any
//     username/password combination is accepted, matching this being a demo/reference
//     project rather than a shipped identity system. A real deployment would swap this
//     out entirely for Azure AD / Entra ID (or another real IdP), and these services
//     would only ever validate tokens issued by that IdP, never issue their own.
// =====================================================================================

using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace TienFS.Shared.Auth;

public static class JwtAuthExtensions
{
    /// <summary>
    /// Registers JWT Bearer authentication using shared signing key/issuer/audience
    /// config. Call this from each service's Program.cs, then app.UseAuthentication()
    /// and app.UseAuthorization() before mapping endpoints.
    /// </summary>
    public static IServiceCollection AddSharedJwtAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var signingKey = configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException(
                "Jwt:SigningKey must be configured. In Development this comes from " +
                "appsettings.Development.json; in production it should be pulled from Key Vault.");
        var issuer = configuration["Jwt:Issuer"] ?? "tienfs-loan-platform";
        var audience = configuration["Jwt:Audience"] ?? "tienfs-loan-platform";

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });

        services.AddAuthorization();
        return services;
    }
}
