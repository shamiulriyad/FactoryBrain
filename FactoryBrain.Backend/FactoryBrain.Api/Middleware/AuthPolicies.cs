namespace FactoryBrain.Api.Middleware;

/// <summary>
/// Centralised policy names so controllers and <c>Program.cs</c>
/// reference the same string.
/// </summary>
public static class AuthPolicies
{
    /// <summary>
    /// Pass if EITHER the request has a valid JWT with role Admin OR
    /// the legacy <c>X-Admin-Token</c> check passes. Used on the three
    /// admin write actions (<c>POST /api/settings/llm</c>,
    /// <c>POST /api/rag/reindex</c>, <c>POST /api/rag/ingest</c>) so a
    /// valid JWT can replace the legacy token without removing the
    /// legacy path.
    /// </summary>
    public const string AdminOrLegacyToken = "AdminOrLegacyToken";
}
