using Microsoft.AspNetCore.Http;

namespace FactoryBrain.Api.Middleware;

/// <summary>
/// Cookie attributes for the <c>fb_refresh</c> refresh token. Resolved
/// once from <c>AUTH_COOKIE_SAMESITE</c> (default <c>Lax</c>; accepts
/// <c>Lax | Strict | None</c>) and a small helper decides whether to
/// stamp <c>Secure</c>:
/// <list type="bullet">
///   <item>Development: <c>Secure</c> is OFF so the cookie can ride over http://localhost.</item>
///   <item>Anywhere else with <c>SameSite=None</c>: <c>Secure</c> is FORCED (the spec requires it).</item>
///   <item>Anywhere else with <c>Lax</c> / <c>Strict</c>: <c>Secure</c> is ON (set to <c>secureOnlyOverride=true</c> when stamping).</item>
/// </list>
/// </summary>
public static class AuthCookieSettings
{
    public const string CookieName = "fb_refresh";
    public const string CookiePath = "/api/auth";

    /// <summary>
    /// Parse and validate <c>AUTH_COOKIE_SAMESITE</c>. Throws with a
    /// clear message if the value is set to anything other than
    /// <c>Lax | Strict | None</c> (case-insensitive).
    /// </summary>
    public static SameSiteMode ParseSameSite(string? raw)
    {
        var s = (raw ?? "Lax").Trim();
        if (s.Length == 0) return SameSiteMode.Lax;
        if (string.Equals(s, "Lax",    StringComparison.OrdinalIgnoreCase)) return SameSiteMode.Lax;
        if (string.Equals(s, "Strict", StringComparison.OrdinalIgnoreCase)) return SameSiteMode.Strict;
        if (string.Equals(s, "None",   StringComparison.OrdinalIgnoreCase)) return SameSiteMode.None;
        throw new InvalidOperationException(
            $"AUTH_COOKIE_SAMESITE must be one of Lax | Strict | None (got '{raw}'). " +
            "SameSite=None requires the cookie to be marked Secure.");
    }

    /// <summary>
    /// True when the <c>Secure</c> flag must be set on the refresh
    /// cookie. Forced ON for <c>SameSite=None</c> (spec requirement) and
    /// for non-Development environments. Forced OFF in Development so
    /// the smoke tests + local checks can ride over plain http.
    /// </summary>
    public static bool ShouldBeSecure(SameSiteMode sameSite, IHostEnvironment env)
        => sameSite == SameSiteMode.None || !env.IsDevelopment();
}
