// =====================================================================================
// DEMO TOKEN ISSUER
// -------------------------------------------------------------------------------------
// Issues JWTs signed with the same shared key every service validates against.
// This is deliberately NOT a real identity provider — there's no user database, no
// password hashing, no MFA, no lockout policy. It exists purely so this reference
// project has a runnable, self-contained way to demonstrate authenticated endpoints
// without standing up a full external IdP.
//
// A real deployment would delete this file entirely and point Jwt:Issuer/Audience/
// signing validation at Azure AD / Entra ID (or another real IdP) instead.
// =====================================================================================

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace TienFS.Shared.Auth;

public static class TokenIssuer
{
    public static string IssueDemoToken(string signingKey, string issuer, string audience, string username)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(JwtRegisteredClaimNames.Sub, username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
