using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BunonBrain.Infrastructure.Services;

/// <summary>
/// Configuration injected into <see cref="TokenService"/>. Bound from
/// the <c>Jwt</c> section of <c>appsettings.json</c> + env overrides
/// (<c>Jwt__Issuer</c>, <c>Jwt__Audience</c>, <c>Jwt__SigningKey</c>)
/// and the lifetime comes from <c>Jwt:AccessTokenLifetimeSeconds</c>
/// (default 900 = 15 min). The signing key bytes are resolved once at
/// construction time so a missing or short key fails startup rather
/// than the first request.
/// </summary>
public sealed class JwtOptions
{
    public string Issuer   { get; set; } = "bunonbrain";
    public string Audience { get; set; } = "bunonbrain";
    /// <summary>UTF-8 bytes. Must be ≥ 32 bytes (HS256).</summary>
    public byte[] SigningKeyBytes { get; set; } = Array.Empty<byte>();
    public int    AccessTokenLifetimeSeconds { get; set; } = 900;
    public int    ClockSkewSeconds           { get; set; } = 30;
}

/// <summary>
/// HS256 JWT issuer + validator. Registered as a singleton (stateless,
/// only holds the key bytes). The <c>iss</c>/<c>aud</c>/<c>exp</c> claims
/// are set on issue; on validation we use the same triple plus a
/// matching <see cref="TokenValidationParameters"/> with the configured
/// 30-second clock skew.
/// </summary>
public sealed class TokenService : ITokenService
{
    private readonly JwtOptions _opts;
    private readonly JwtSecurityTokenHandler _handler = new();

    public TokenService(IOptions<JwtOptions> opts)
    {
        _opts = opts.Value;
    }

    public string IssueAccessToken(AccessSnapshot access, out int expiresInSeconds)
    {
        expiresInSeconds = _opts.AccessTokenLifetimeSeconds;
        var now    = DateTime.UtcNow;
        var expiry = now.AddSeconds(expiresInSeconds);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,   access.UserId.ToString()),
            new(JwtRegisteredClaimNames.Email, access.Email),
            new(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString("N")),
            new(AccessClaims.SecurityStamp,    access.SecurityStamp),
        };
        if (!string.IsNullOrWhiteSpace(access.FullName)) claims.Add(new(AccessClaims.Name, access.FullName));
        if (access.MustChangePassword) claims.Add(new(AccessClaims.MustChangePassword, "1"));
        claims.AddRange(access.Roles.Select(r => new Claim(AccessClaims.Role, r)));
        claims.AddRange(access.Permissions.Select(p => new Claim(AccessClaims.Permission, p)));
        claims.AddRange(access.Factories.Select(f => new Claim(AccessClaims.Factory, f.Id.ToString())));
        claims.AddRange(access.LineIds.Select(l => new Claim(AccessClaims.Line, l)));
        var key   = new SymmetricSecurityKey(_opts.SigningKeyBytes);
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer:             _opts.Issuer,
            audience:           _opts.Audience,
            claims:             claims,
            notBefore:          now,
            expires:            expiry,
            signingCredentials: creds);
        return _handler.WriteToken(token);
    }

    public TokenPrincipal? ValidateAccessToken(string jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        try
        {
            var validation = new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidIssuer              = _opts.Issuer,
                ValidateAudience         = true,
                ValidAudience            = _opts.Audience,
                ValidateLifetime         = true,
                ClockSkew                = TimeSpan.FromSeconds(_opts.ClockSkewSeconds),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = new SymmetricSecurityKey(_opts.SigningKeyBytes),
                // We don't care about name mapping — we read the standard
                // claim names ourselves below.
                NameClaimType            = JwtRegisteredClaimNames.Email,
                RoleClaimType            = ClaimTypes.Role,
            };
            var principal = _handler.ValidateToken(jwt, validation, out _);
            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
                        ?? principal.Identity?.Name;
            var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
            if (sub is null || email is null) return null;
            if (!Guid.TryParse(sub, out var userId)) return null;
            return new TokenPrincipal(userId, email, roles);
        }
        catch
        {
            return null;
        }
    }
}
