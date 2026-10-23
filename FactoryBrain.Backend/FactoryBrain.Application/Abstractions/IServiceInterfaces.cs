using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Dtos.Agents;
using FactoryBrain.Application.Dtos.Ask;
using FactoryBrain.Application.Dtos.Auth;
using FactoryBrain.Application.Dtos.Brief;
using FactoryBrain.Application.Dtos.FloorAlerts;
using FactoryBrain.Application.Dtos.LineBoard;
using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Application.Dtos.Rag;
using FactoryBrain.Application.Dtos.Sensors;
using FactoryBrain.Application.Dtos.Settings;
using FactoryBrain.Application.Dtos.Vision;

namespace FactoryBrain.Application.Abstractions.Interfaces;

public interface IAskService
{
    Task<AskAnswerResponse> AskAsync(AskRequest req, CancellationToken ct);
}

public interface IBriefService
{
    Task<MorningBriefResponse> BuildMorningBriefAsync(CancellationToken ct);
}

public interface IFloorAlertService
{
    Task<FloorAlertListResponse> ListAsync(CancellationToken ct);
    Task<FloorAlertPatchResponse?> MarkReadAsync(string id, bool read, CancellationToken ct);
    Task<FloorAlert> PushAsync(FloorAlert alert, CancellationToken ct);
}

public interface ILineBoardService
{
    Task<LineBoardResponse> BuildAsync(CancellationToken ct);
    Task<LineBoardResponse> RefreshAsync(int? tickOverride, CancellationToken ct);
}

public interface IQcService
{
    Task<QcDefectsResponse> BuildAsync(CancellationToken ct);
    Task<QcFlagResponse> FlagAsync(QcFlagRequest req, CancellationToken ct);
}

public interface ISensorService
{
    bool LiveConnected { get; }
    Task<IngestResponse>  IngestAsync(IngestRequest req, CancellationToken ct);
    Task<IngestResponse>  AcceptLiveAsync(LiveIngestRequest req, CancellationToken ct);
    Task<LatestReadingsResponse> LatestAsync(CancellationToken ct);
    Task<SimStatusResponse> StatusAsync(CancellationToken ct);
    /// <summary>Immiration from src/services/sensors.server for the sim buffer.</summary>
    SensorSimState CurrentState { get; }
}

public interface IVisionService
{
    VisionAnalyzeResponse Analyze(VisionAnalyzeRequest req, CancellationToken ct);
    VisionAllowedResponse Allowed();
}

public interface ILlmSettingsService
{
    LlmSettingsResponse ReadStatus();
    LlmSettingsResponse Update(LlmSettingsRequest req);
}

public interface IAgentRunService
{
    Task<AgentRunResponse> RunAsync(string agentId, CancellationToken ct);
    Task<IReadOnlyList<AgentDefinition>> RosterAsync(CancellationToken ct = default);
}

public interface IRagService
{
    /// <summary>Chunk a doc body and emit embeddings (pgvector or in-memory).</summary>
    Task<List<DocumentChunk>> ChunkAsync(
        string docId, string title, string body, IEnumerable<string> tags,
        string department, string category, CancellationToken ct = default);

    /// <summary>Hybrid retrieval: cosine + BM25, returns top-k RAG hits.</summary>
    Task<IReadOnlyList<AskRagHit>> RetrieveAsync(
        string query, RagFilter? filter, int topK = 4, double similarityThreshold = 0,
        double bm25Weight = 0.35, CancellationToken ct = default);

    /// <summary>Re-embed every chunk with the currently active embedder.</summary>
    Task<ReindexReport> ReindexAsync(CancellationToken ct = default);

    /// <summary>
    /// True when the embedder metadata stored on existing manual rows
    /// (provider / model / dims) does not match the active configuration —
    /// i.e. a reindex is required to keep vectors consistent with the
    /// configured <see cref="FactoryBrain.Application.Abstractions.Interfaces.IRagService"/>'s
    /// embedder.
    /// </summary>
    Task<bool> NeedsReindexAsync(CancellationToken ct = default);

    /// <summary>
    /// Ingest a new document into the RAG index. Chunks the body with
    /// <see cref="FactoryBrain.Application.Rag.TextChunker"/>, embeds
    /// each chunk with the configured embedder, and writes the row + its
    /// chunks to <c>manual_documents</c>. In degraded mode the chunks are
    /// stored with <c>Embedding = null</c> and
    /// <c>EmbeddingProvider = "pending"</c> — they will be embedded by
    /// the next non-degraded startup reindex or POST /api/rag/reindex.
    /// </summary>
    Task<IngestOutcome> IngestAsync(IngestInput input, CancellationToken ct = default);

    /// <summary>
    /// List the manual documents currently stored in the index. Pass a
    /// non-null <paramref name="source"/> to filter by source
    /// (<c>manual | sop | compliance | faq</c>). Embedding vectors are
    /// never returned.
    /// </summary>
    Task<IReadOnlyList<DocumentListItem>> ListDocumentsAsync(string? source, CancellationToken ct = default);
}

/// <summary>
/// Inputs to <see cref="IRagService.IngestAsync"/>. Kept distinct from
/// the wire DTO so the service layer doesn't drag an HTTP shape around.
/// </summary>
public sealed record IngestInput(
    string Title,
    string? TitleBn,
    string Source,
    IReadOnlyList<string> Tags,
    string Content,
    string? Url,
    bool IsDemo
);

/// <summary>
/// Result of a single ingest call. Mirrors
/// <see cref="FactoryBrain.Application.Dtos.Rag.IngestResponse"/> but is safe to
/// return from the service layer (no HTTP coupling).
/// </summary>
public sealed record IngestOutcome(
    string DocumentId,
    int Chunks,
    string Provider,
    string Model,
    int Dims,
    bool Embedded,
    string? Warning
);

public sealed record SensorSimState(
    int Tick,
    IReadOnlyList<LineBoardLine> Lines,
    IReadOnlyList<LineBoardMachine> Machines,
    IReadOnlyList<SensorReading> Readings);

public sealed record LineBoardLine(string Id, double Efficiency, double Uptime, double EnergyKwh);
public sealed record LineBoardMachine(string Id, double Vibration, double Temperature, double DutyCycle, string Status);

/// <summary>
/// Login + refresh outcome flags. The controller branches on
/// <c>Kind</c>; the remaining fields are only populated when
/// <c>Kind == Success</c>.
/// </summary>
public enum AuthOutcome { Success, Invalid, Expired }

/// <summary>
/// Login outcome. <see cref="AuthOutcome.Success"/> carries the issued
/// access token, the lifetime in seconds, and the raw refresh-cookie
/// value the caller should stamp as <c>fb_refresh</c>. <see cref="AuthOutcome.Invalid"/>
/// is returned for BOTH unknown-email and wrong-password — the
/// controller emits one canonical 401 envelope so an attacker can't
/// enumerate accounts.
/// </summary>
public sealed record LoginResult(
    AuthOutcome Kind,
    string?     AccessToken,
    int         ExpiresInSeconds,
    UserInfo?   User,
    string?     RefreshTokenRaw);

/// <summary>
/// Refresh outcome. <see cref="AuthOutcome.Success"/> carries the
/// freshly-rotated access token + lifetime + the new raw refresh
/// cookie value (the old cookie must be cleared). <see cref="AuthOutcome.Invalid"/>
/// covers both missing and tampered cookies; <see cref="AuthOutcome.Expired"/>
/// covers a cookie whose stored expiry has passed. The controller
/// maps both failure kinds to the same 401 + clear-cookie envelope.
/// </summary>
public sealed record RefreshResult(
    AuthOutcome Kind,
    string?     AccessToken,
    int         ExpiresInSeconds,
    UserInfo?   User,
    string?     RefreshTokenRaw);

/// <summary>
/// Authentication service. Implementations live in
/// <c>FactoryBrain.Infrastructure.Services.AuthService</c> and depend on
/// the <see cref="User"/> aggregate, an <see cref="IPasswordHasher{T}"/>,
/// and a JWT issuer.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Verify the supplied credentials and (on success) issue a fresh
    /// access token + 7-day refresh token. Refresh token is stored as
    /// SHA-256(raw) on the user; the raw value is returned so the
    /// controller can stamp it in the <c>fb_refresh</c> cookie.
    /// </summary>
    Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct);

    /// <summary>
    /// Look up the user by <paramref name="presentedRefreshToken"/>
    /// (hashed then matched), validate expiry, then rotate the stored
    /// hash + extend the expiry by another 7 days. The old token is
    /// immediately invalid.
    /// </summary>
    Task<RefreshResult> RefreshAsync(string presentedRefreshToken, CancellationToken ct);

    /// <summary>
    /// If the presented refresh token matches a user, clear that user's
    /// <c>RefreshTokenHash</c> + expiry. Always succeeds — calling with
    /// an unknown or expired cookie is a no-op.
    /// </summary>
    Task LogoutAsync(string presentedRefreshToken, CancellationToken ct);

    /// <summary>
    /// Resolve the currently-authenticated user by id (the <c>sub</c>
    /// claim on the access token). Returns null when the user no
    /// longer exists.
    /// </summary>
    Task<UserInfo?> MeAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Issues HS256-signed JWT access tokens and parses them back into a
/// <see cref="TokenPrincipal"/>. Lifetime is 15 minutes by spec.
/// </summary>
public interface ITokenService
{
    /// <summary>Issue a fresh access token for <paramref name="user"/>.</summary>
    string IssueAccessToken(User user, out int expiresInSeconds);

    /// <summary>
    /// Parse and validate <paramref name="jwt"/>. Returns null on any
    /// failure (bad signature, wrong issuer/audience, expired past the
    /// 30 s clock skew tolerance, or malformed). Never throws.
    /// </summary>
    TokenPrincipal? ValidateAccessToken(string jwt);
}

/// <summary>
/// Decoded JWT principal — the user id, email, and role taken straight
/// from the validated token. Returned by
/// <see cref="ITokenService.ValidateAccessToken"/> on success.
/// </summary>
public sealed record TokenPrincipal(Guid UserId, string Email, string Role);

/// <summary>
/// Server-to-client realtime notifications. Implemented in
/// <c>FactoryBrain.Api.Realtime</c> over SignalR's
/// <c>IHubContext&lt;FactoryHub&gt;</c>; services consume only this
/// abstraction so <c>FactoryBrain.Infrastructure</c> never references
/// SignalR. All methods MUST swallow + log any failure — a notifier
/// outage must never fail the REST write that triggered it.
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>Broadcast after a successful <c>POST /api/sensors/ingest</c>.</summary>
    Task SensorReadingAsync(IngestResponse payload, CancellationToken ct);

    /// <summary>Broadcast after a successful <c>POST /api/line-board/refresh</c>.</summary>
    Task LineBoardUpdatedAsync(LineBoardResponse payload, CancellationToken ct);

    /// <summary>Broadcast after <see cref="IFloorAlertService.PushAsync"/> persists a new alert.</summary>
    Task FloorAlertAsync(FloorAlert payload, CancellationToken ct);
}

/// <summary>
/// Small wrapper over
/// <c>Microsoft.AspNetCore.Identity.PasswordHasher&lt;User&gt;</c> so the
/// Application layer doesn't depend on Microsoft.Extensions.Identity
/// directly — Infrastructure provides the implementation.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Produce a self-contained hash string (PBKDF2 + salt + format marker).</summary>
    string Hash(User user, string password);

    /// <summary>True when <paramref name="provided"/> matches <paramref name="hashed"/>.</summary>
    bool Verify(User user, string hashed, string provided);
}
