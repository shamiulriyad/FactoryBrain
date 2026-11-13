using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace BunonBrain.Infrastructure.Access;

/// <summary>
/// Loads a user's effective access from the database. Factories that are
/// deactivated don't count, and line assignments count only for lines that
/// belong to one of the user's active factories. A non-platform user with no
/// active factory keeps only self-service permissions: factory roles mean
/// nothing without a factory.
/// </summary>
public sealed class AccessSnapshotLoader : IAccessSnapshotLoader
{
    private static readonly HashSet<string> SelfServicePermissions = new(StringComparer.Ordinal)
    {
        Permissions.SelfView, Permissions.NotificationsView,
    };

    private readonly BunonBrainDbContext _db;
    public AccessSnapshotLoader(BunonBrainDbContext db) { _db = db; }

    public async Task<AccessSnapshot?> LoadAsync(Guid userId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Email, u.FullName, u.SecurityStamp, u.MustChangePassword })
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        var roles = (await _db.UserRoles.AsNoTracking().Where(r => r.UserId == userId).Select(r => r.RoleId).ToListAsync(ct))
            .Where(AccessCatalog.IsRole).OrderByDescending(r => AccessCatalog.Role(r)!.Rank).ToList();

        var factories = await (from a in _db.UserFactories.AsNoTracking()
                               join f in _db.Factories.AsNoTracking() on a.FactoryId equals f.Id
                               where a.UserId == userId && f.IsActive
                               orderby f.Name
                               select new FactoryRef(f.Id, f.Code, f.Name)).ToListAsync(ct);
        var factoryIds = factories.Select(f => f.Id).ToList();

        var lines = await (from a in _db.UserLines.AsNoTracking()
                           join l in _db.ProductionLines.AsNoTracking() on a.LineId equals l.Id
                           where a.UserId == userId && l.FactoryId != null && factoryIds.Contains(l.FactoryId.Value)
                           orderby l.Id
                           select l.Id).ToListAsync(ct);

        IReadOnlyList<string> permissions = AccessCatalog.PermissionsFor(roles);
        if (AccessCatalog.ScopeFor(roles) != RoleScope.Platform && factories.Count == 0)
            permissions = permissions.Where(SelfServicePermissions.Contains).ToArray();

        return new AccessSnapshot(user.Id, user.Email, user.FullName, roles, permissions, factories, lines,
            user.SecurityStamp, user.MustChangePassword);
    }
}

/// <summary>
/// Per-request token freshness check (see <see cref="ISessionValidator"/>).
/// Results are cached for <see cref="CacheFor"/>; this process evicts the
/// entry the moment it changes the account, so a deactivation or role change
/// takes effect immediately here and within <see cref="CacheFor"/> on other
/// instances. Fails closed: a database error means "not current".
/// </summary>
public sealed class SessionValidator : ISessionValidator
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SessionValidator> _log;

    public SessionValidator(IServiceScopeFactory scopes, IMemoryCache cache, ILogger<SessionValidator> log)
    {
        _scopes = scopes; _cache = cache; _log = log;
    }

    private sealed record Entry(bool Exists, bool Active, string? Stamp);

    private static string Key(Guid userId) => "session:" + userId.ToString("N");

    public async Task<bool> IsCurrentAsync(Guid userId, string? securityStamp, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(securityStamp)) return false;
        if (!_cache.TryGetValue(Key(userId), out Entry? entry) || entry is null)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
                var row = await db.Users.AsNoTracking().Where(u => u.Id == userId)
                    .Select(u => new { u.IsActive, u.SecurityStamp }).FirstOrDefaultAsync(ct);
                entry = row is null ? new Entry(false, false, null) : new Entry(true, row.IsActive, row.SecurityStamp);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Session check for user {UserId} failed; rejecting the token.", userId);
                return false;
            }
            _cache.Set(Key(userId), entry, CacheFor);
        }
        return entry.Exists && entry.Active && string.Equals(entry.Stamp, securityStamp, StringComparison.Ordinal);
    }

    public void Invalidate(Guid userId) => _cache.Remove(Key(userId));
}

/// <summary>Builds audit records for access-management changes.</summary>
internal static class AccessAudit
{
    public const string EntityUser = "user";
    public const string EntityFactory = "factory";
    public const string EntityWorker = "worker";
    public const string EntityLine = "production_line";
    public const string EntityDepartment = "department";
    public const string EntityFloor = "production_floor";
    public const string EntityWorkerCard = "worker_card";
    public const string EntityWorkstation = "workstation";
    public const string EntityMachine = "machine";
    public const string EntityAssignment = "workstation_assignment";
    public const string EntityShift = "shift";
    public const string EntityShiftAssignment = "shift_assignment";
    public const string EntityAttendanceSession = "attendance_session";
    public const string EntityAttendanceCorrection = "attendance_correction";
    public const string EntityOvertime = "overtime_request";
    public const string EntityAttendancePolicy = "attendance_policy";
    public const string EntityAttendanceReader = "attendance_reader";

    public static AuditLogEntry Entry(AuditActor actor, string action, string entityType, string entityId,
        string? from, string? to, string reason, string? traceId, Guid? factoryId) => new()
    {
        OccurredAt = DateTime.UtcNow,
        ActorUserId = actor.UserId,
        ActorEmail = Clip(actor.Email, 256)!,
        ActorRole = Clip(actor.Role, 32)!,
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        FromState = Clip(from, 256),
        ToState = Clip(to, 256),
        Reason = Clip(reason, 1000),
        TraceId = Clip(traceId, 128),
        FactoryId = factoryId,
    };

    public static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    public static string Roles(IEnumerable<string> roles)
    {
        var list = roles.OrderBy(r => r, StringComparer.Ordinal).ToList();
        return list.Count == 0 ? "(none)" : string.Join(",", list);
    }
}

/// <summary>
/// Projects <see cref="AccessCatalog"/> into the roles / permissions /
/// role_permissions tables, and maps accounts from the pre-RBAC model
/// (users.LegacyRole) onto system roles exactly once.
/// </summary>
public static class AccessSeeder
{
    public const string DefaultFactoryCode = "MAIN";
    public const string DefaultFactoryName = "Main Factory";
    public const string SystemActorEmail = "system:access-migration";

    /// <summary>
    /// Legacy role → system roles.
    /// Admin → Super Admin (they controlled the whole installation).
    /// Operator → Factory Manager + Production Supervisor (operational writes).
    /// Viewer → no role: none of the eight roles is a read-only view of every
    /// module, so the account keeps its sign-in but an administrator chooses its role.
    /// </summary>
    public static IReadOnlyList<string> MapLegacyRole(string legacy) => legacy switch
    {
        "Admin" => new[] { SystemRoles.SuperAdmin },
        "Operator" => new[] { SystemRoles.FactoryManager, SystemRoles.ProductionSupervisor },
        _ => Array.Empty<string>(),
    };

    public static async Task SyncCatalogAsync(BunonBrainDbContext db, CancellationToken ct)
    {
        var permissions = await db.Permissions.ToDictionaryAsync(p => p.Code, ct);
        foreach (var def in AccessCatalog.AllPermissions)
        {
            if (!permissions.TryGetValue(def.Code, out var row))
                db.Permissions.Add(row = new Permission { Code = def.Code });
            row.Module = def.Module;
            row.Name = def.Name;
            row.Description = def.Description;
            row.PlatformOnly = def.PlatformOnly;
            row.Reserved = def.Reserved;
        }
        var stalePermissions = permissions.Keys.Where(k => AccessCatalog.Permission(k) is null).ToList();
        db.Permissions.RemoveRange(stalePermissions.Select(k => permissions[k]));

        var roles = await db.Roles.Include(r => r.Permissions).ToDictionaryAsync(r => r.Id, ct);
        foreach (var def in AccessCatalog.AllRoles)
        {
            if (!roles.TryGetValue(def.Id, out var row))
                db.Roles.Add(row = new Role { Id = def.Id });
            row.Name = def.Name;
            row.Description = def.Description;
            row.Scope = def.Scope.ToString();
            row.Rank = def.Rank;
            row.IsSystem = true;
            var want = def.Permissions.ToHashSet(StringComparer.Ordinal);
            row.Permissions.RemoveAll(p => !want.Contains(p.PermissionCode));
            foreach (var code in want.Where(c => row.Permissions.All(p => p.PermissionCode != c)))
                row.Permissions.Add(new RolePermission { RoleId = def.Id, PermissionCode = code });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The default factory, created on first need. Lines with no factory yet are attached to it.</summary>
    public static async Task<Factory> EnsureDefaultFactoryAsync(BunonBrainDbContext db, CancellationToken ct)
    {
        var factory = await db.Factories.FirstOrDefaultAsync(f => f.Code == DefaultFactoryCode, ct);
        if (factory is not null) return factory;
        factory = new Factory { Code = DefaultFactoryCode, Name = DefaultFactoryName, Location = null };
        db.Factories.Add(factory);
        await db.SaveChangesAsync(ct);
        await db.ProductionLines.Where(l => l.FactoryId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.FactoryId, factory.Id), ct);
        db.AuditLog.Add(AccessAudit.Entry(new AuditActor(null, SystemActorEmail, "System"), "factory.created",
            AccessAudit.EntityFactory, factory.Id.ToString(), null, "active",
            "default factory for accounts and lines that existed before factories", null, factory.Id));
        await db.SaveChangesAsync(ct);
        return factory;
    }

    public static async Task<int> MigrateLegacyRolesAsync(BunonBrainDbContext db, ILogger log, CancellationToken ct)
    {
        var pending = await db.Users.Include(u => u.Roles).Include(u => u.Factories)
            .Where(u => u.LegacyRole != null).ToListAsync(ct);
        if (pending.Count == 0) return 0;

        Factory? main = null;
        foreach (var user in pending)
        {
            var legacy = user.LegacyRole!;
            var roles = MapLegacyRole(legacy);
            foreach (var role in roles.Where(r => user.Roles.All(x => x.RoleId != r)))
                user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role });
            Guid? factoryId = null;
            if (legacy != "Admin")
            {
                main ??= await EnsureDefaultFactoryAsync(db, ct);
                factoryId = main.Id;
                if (user.Factories.All(f => f.FactoryId != main.Id))
                    user.Factories.Add(new UserFactoryAssignment { UserId = user.Id, FactoryId = main.Id });
            }
            user.LegacyRole = null;
            user.SecurityStamp = User.NewStamp();
            db.AuditLog.Add(AccessAudit.Entry(new AuditActor(null, SystemActorEmail, "System"), "user.role_migrated",
                AccessAudit.EntityUser, user.Id.ToString(), legacy, AccessAudit.Roles(roles),
                $"{user.Email}: legacy role {legacy} mapped to {AccessAudit.Roles(roles)}", null, factoryId));
            log.LogInformation("Mapped legacy role {Legacy} of {UserId} to {Roles}.", legacy, user.Id, AccessAudit.Roles(roles));
        }
        await db.SaveChangesAsync(ct);
        return pending.Count;
    }

    /// <summary>
    /// Create an account with the given system roles in the default factory
    /// (platform roles get no factory). Used by the env-driven dev/test seeds.
    /// No-op when the email already has an account.
    /// </summary>
    public static async Task<bool> SeedAccountAsync(BunonBrainDbContext db, IPasswordHasher hasher, string email, string password,
        IReadOnlyList<string> roles, string reason, CancellationToken ct)
    {
        var normalised = email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == normalised, ct)) return false;
        var user = new User { Email = normalised, CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = hasher.Hash(user, password);
        foreach (var role in roles) user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role });
        Guid? factoryId = null;
        if (AccessCatalog.ScopeFor(roles) != RoleScope.Platform)
        {
            var main = await EnsureDefaultFactoryAsync(db, ct);
            factoryId = main.Id;
            user.Factories.Add(new UserFactoryAssignment { UserId = user.Id, FactoryId = main.Id });
        }
        db.Users.Add(user);
        db.AuditLog.Add(AccessAudit.Entry(new AuditActor(null, "system:seed", "System"), "user.created",
            AccessAudit.EntityUser, user.Id.ToString(), null, AccessAudit.Roles(roles), reason, null, factoryId));
        await db.SaveChangesAsync(ct);
        return true;
    }
}
