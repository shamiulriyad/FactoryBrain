namespace FactoryBrain.Api.Middleware;

/// <summary>
/// Centralised policy and role names so controllers, <c>Program.cs</c>
/// and the integration tests reference the same strings.
/// <para>
/// Access model (enforced server-side; see <c>Program.cs</c>):
/// <list type="bullet">
///   <item><b>Anonymous</b> — only endpoints marked <c>[AllowAnonymous]</c>
///   (<c>/health</c>, <c>/api/auth/login</c>, <c>/refresh</c>, <c>/logout</c>,
///   <c>/register</c>, <c>/forgot-password</c>, <c>/reset-password</c>, <c>/options</c>).</item>
///   <item><b>Authenticated</b> — the fallback policy: every other endpoint
///   needs a valid JWT, any role.</item>
///   <item><b><see cref="Operator"/></b> — operational writes (agent runs,
///   QC flags, sensor ingest, alert acknowledgement).</item>
///   <item><b><see cref="Admin"/></b> — configuration and index management
///   (LLM and embedding settings, RAG ingest and reindex, user roles).</item>
/// </list>
/// </para>
/// </summary>
public static class AuthPolicies
{
    /// <summary>Roles <c>Admin</c> or <c>Operator</c>.</summary>
    public const string Operator = "Operator";

    /// <summary>Role <c>Admin</c> only.</summary>
    public const string Admin = "Admin";
}

/// <summary>Role names as stored in <c>users.Role</c> and issued in the JWT role claim.</summary>
public static class Roles
{
    public const string Admin    = "Admin";
    public const string Operator = "Operator";
    public const string Viewer   = "Viewer";
}
