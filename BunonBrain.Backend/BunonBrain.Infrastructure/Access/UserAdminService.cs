using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Approvals;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Access;

/// <summary>
/// Implements <see cref="IUserAdminService"/>. The rules, all enforced here:
/// <list type="bullet">
///   <item><b>Visibility.</b> A Super Admin sees everyone. Anyone else sees a
///   user only when that user has at least one factory, every one of the
///   user's factories is one of the caller's, and the user is not a Super
///   Admin. Users outside that set answer 404, so their existence isn't revealed.</item>
///   <item><b>Authority.</b> A caller manages a visible user only when the
///   caller could grant every role the user holds (a Factory Admin can't
///   touch another Factory Admin or a Super Admin), and grants only roles
///   they manage whose permissions they hold.</item>
///   <item><b>Self.</b> Nobody changes their own roles, status or scope, or
///   resets their own password here.</item>
///   <item><b>Last Super Admin.</b> The last active Super Admin can't be
///   deactivated or lose the role (checked under a row lock).</item>
///   <item><b>Sessions.</b> Every change to roles, scope, status, email or
///   password rotates the user's security stamp, so older access tokens stop
///   working; deactivation and password resets also revoke the refresh token.</item>
/// </list>
/// Every change is audited in the same transaction.
/// </summary>
public sealed class UserAdminService : IUserAdminService
{
    public const int MaxPageSize = 100;

    private readonly BunonBrainDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ISessionValidator _sessions;
    private readonly ILogger<UserAdminService> _log;

    public UserAdminService(BunonBrainDbContext db, IPasswordHasher hasher, ISessionValidator sessions, ILogger<UserAdminService> log)
    {
        _db = db; _hasher = hasher; _sessions = sessions; _log = log;
    }

    // ─── Queries ───────────────────────────────────────────────────────────

    public async Task<PagedResult<UserSummary>> ListAsync(AccessContext caller, UserListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var users = VisibleUsers(caller);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + EscapeLike(q.Search.Trim()) + "%";
            users = users.Where(u => EF.Functions.ILike(u.Email, pattern, "\\") ||
                                     (u.FullName != null && EF.Functions.ILike(u.FullName, pattern, "\\")));
        }
        if (!string.IsNullOrWhiteSpace(q.Role))
        {
            var role = q.Role.Trim();
            users = role == "none" ? users.Where(u => !u.Roles.Any()) : users.Where(u => u.Roles.Any(r => r.RoleId == role));
        }
        if (q.FactoryId is { } fid)
        {
            if (!caller.CanAccessFactory(fid)) return new PagedResult<UserSummary>(Array.Empty<UserSummary>(), page, size, 0);
            users = users.Where(u => u.Factories.Any(f => f.FactoryId == fid));
        }
        users = q.Status?.Trim().ToLowerInvariant() switch
        {
            "active" => users.Where(u => u.IsActive),
            "inactive" => users.Where(u => !u.IsActive),
            "must_change_password" => users.Where(u => u.MustChangePassword),
            _ => users,
        };
        users = (q.Sort?.Trim() ?? "email") switch
        {
            "-email" => users.OrderByDescending(u => u.Email),
            "name" => users.OrderBy(u => u.FullName ?? u.Email).ThenBy(u => u.Email),
            "-name" => users.OrderByDescending(u => u.FullName ?? u.Email).ThenBy(u => u.Email),
            "created" => users.OrderBy(u => u.CreatedAt).ThenBy(u => u.Email),
            "-created" => users.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Email),
            "lastLogin" => users.OrderBy(u => u.LastLoginAt == null).ThenBy(u => u.LastLoginAt).ThenBy(u => u.Email),
            "-lastLogin" => users.OrderBy(u => u.LastLoginAt == null).ThenByDescending(u => u.LastLoginAt).ThenBy(u => u.Email),
            _ => users.OrderBy(u => u.Email),
        };

        var total = await users.CountAsync(ct);
        var rows = await users.Skip((page - 1) * size).Take(size).Select(u => new
        {
            u.Id, u.Email, u.FullName, u.IsActive, u.MustChangePassword, u.CreatedAt, u.LastLoginAt,
            Roles = u.Roles.Select(r => r.RoleId).ToList(),
            FactoryIds = u.Factories.Select(f => f.FactoryId).ToList(),
            LineCount = u.Lines.Count,
        }).ToListAsync(ct);

        var names = await FactoryRefsAsync(rows.SelectMany(r => r.FactoryIds), ct);
        var items = rows.Select(r => new UserSummary(
            r.Id, r.Email, r.FullName, r.IsActive, r.MustChangePassword, SortRoles(r.Roles),
            r.FactoryIds.Where(names.ContainsKey).Select(id => names[id]).OrderBy(f => f.Name).ToList(),
            r.LineCount, r.CreatedAt, r.LastLoginAt,
            CanManage(caller, r.Id, r.Roles))).ToList();
        return new PagedResult<UserSummary>(items, page, size, total);
    }

    public async Task<AccessResult<UserDetail>> GetAsync(AccessContext caller, Guid userId, CancellationToken ct)
    {
        var detail = await DetailAsync(caller, userId, ct);
        return detail is null ? NotFound<UserDetail>() : AccessResult<UserDetail>.Ok(detail);
    }

    public async Task<AccessResult<IReadOnlyList<AuditEntryDto>>> ActivityAsync(AccessContext caller, Guid userId, int limit, CancellationToken ct)
    {
        if (!await VisibleUsers(caller).AnyAsync(u => u.Id == userId, ct)) return NotFound<IReadOnlyList<AuditEntryDto>>();
        var id = userId.ToString();
        var rows = await _db.AuditLog.AsNoTracking()
            .Where(e => (e.EntityType == AccessAudit.EntityUser && e.EntityId == id) || e.ActorUserId == userId)
            .OrderByDescending(e => e.Id).Take(Math.Clamp(limit, 1, 200))
            .Select(e => new AuditEntryDto(e.Id, e.OccurredAt, e.ActorUserId, e.ActorEmail, e.ActorRole, e.Action,
                e.EntityType, e.EntityId, e.FromState, e.ToState, e.Reason, e.TraceId, e.FactoryId))
            .ToListAsync(ct);
        return AccessResult<IReadOnlyList<AuditEntryDto>>.Ok(rows);
    }

    // ─── Commands ──────────────────────────────────────────────────────────

    public async Task<AccessResult<UserDetail>> CreateAsync(AccessContext caller, CreateUserRequest request, string? traceId, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var roles = (request.Roles ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
        var factoryIds = (request.FactoryIds ?? Array.Empty<Guid>()).Distinct().ToList();
        var lineIds = (request.LineIds ?? Array.Empty<string>()).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        if (roles.Count > 0 && !caller.Has(Permissions.RolesAssign))
            return Forbidden<UserDetail>("roles_assign_required", "You need the Assign roles permission to create a user with roles.");
        var grantError = CheckGrantable(caller, roles);
        if (grantError is not null) return grantError.As<UserDetail>();

        // A non-platform caller always creates users inside their own factory.
        if (!caller.IsPlatform && factoryIds.Count == 0 && caller.FactoryIds.Count == 1) factoryIds.Add(caller.FactoryIds.First());
        var scopeError = await CheckScopeAsync(caller, roles, factoryIds, lineIds, ct);
        if (scopeError is not null) return scopeError.As<UserDetail>();

        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return AccessResult<UserDetail>.Conflict("email_taken", "An account with this email already exists.");

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Email = email,
            FullName = Clean(request.FullName),
            Phone = Clean(request.Phone),
            MustChangePassword = true,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = caller.UserId,
        };
        user.PasswordHash = _hasher.Hash(user, request.TemporaryPassword);
        foreach (var r in roles) user.Roles.Add(new UserRole { UserId = user.Id, RoleId = r, AssignedAt = now, AssignedByUserId = caller.UserId });
        foreach (var f in factoryIds) user.Factories.Add(new UserFactoryAssignment { UserId = user.Id, FactoryId = f, AssignedAt = now, AssignedByUserId = caller.UserId });
        foreach (var l in lineIds) user.Lines.Add(new UserProductionLineAssignment { UserId = user.Id, LineId = l, AssignedAt = now, AssignedByUserId = caller.UserId });
        _db.Users.Add(user);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "user.created", AccessAudit.EntityUser, user.Id.ToString(),
            null, AccessAudit.Roles(roles),
            $"{email} created with roles {AccessAudit.Roles(roles)}, {factoryIds.Count} factory(ies), {lineIds.Count} line(s); temporary password must be changed at first sign-in",
            traceId, factoryIds.Count > 0 ? factoryIds[0] : null));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return AccessResult<UserDetail>.Conflict("email_taken", "An account with this email already exists.");
        }
        _log.LogInformation("User {ActorId} created user {UserId} with roles {Roles}.", caller.UserId, user.Id, AccessAudit.Roles(roles));
        return AccessResult<UserDetail>.Created((await DetailAsync(caller, user.Id, ct))!);
    }

    public async Task<AccessResult<UserDetail>> UpdateAsync(AccessContext caller, Guid userId, UpdateUserRequest request, string? traceId, CancellationToken ct)
    {
        var (user, error) = await LoadManageableAsync(caller, userId, allowSelf: false, ct);
        if (error is not null) return error.As<UserDetail>();

        var email = request.Email.Trim().ToLowerInvariant();
        if (email != user!.Email && await _db.Users.AnyAsync(u => u.Email == email && u.Id != userId, ct))
            return AccessResult<UserDetail>.Conflict("email_taken", "An account with this email already exists.");

        var changes = new List<string>();
        if (email != user.Email) { changes.Add($"email {user.Email} → {email}"); user.Email = email; user.SecurityStamp = User.NewStamp(); }
        var name = Clean(request.FullName);
        if (name != user.FullName) { changes.Add($"name {user.FullName ?? "-"} → {name ?? "-"}"); user.FullName = name; }
        var phone = Clean(request.Phone);
        if (phone != user.Phone) { changes.Add("phone changed"); user.Phone = phone; }
        if (changes.Count == 0) return AccessResult<UserDetail>.Ok((await DetailAsync(caller, userId, ct))!);

        user.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "user.updated", AccessAudit.EntityUser, user.Id.ToString(),
            null, null, string.Join("; ", changes), traceId, user.Factories.FirstOrDefault()?.FactoryId));
        await _db.SaveChangesAsync(ct);
        _sessions.Invalidate(userId);
        return AccessResult<UserDetail>.Ok((await DetailAsync(caller, userId, ct))!);
    }

    public Task<AccessResult<UserDetail>> SetStatusAsync(AccessContext caller, Guid userId, SetUserStatusRequest request, string? traceId, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            if (caller.UserId == userId)
                return Forbidden<UserDetail>("cannot_change_own_status", "You can't deactivate or reactivate your own account.");
            var (user, error) = await LoadManageableAsync(caller, userId, allowSelf: false, ct);
            if (error is not null) return error.As<UserDetail>();
            if (user!.IsActive == request.IsActive) return AccessResult<UserDetail>.Ok((await DetailAsync(caller, userId, ct))!);

            if (!request.IsActive && user.Roles.Any(r => r.RoleId == SystemRoles.SuperAdmin) &&
                await OtherActiveSuperAdminsAsync(userId, ct) == 0)
                return AccessResult<UserDetail>.Conflict("last_super_admin", "This is the last active Super Admin. Make someone else a Super Admin first.");

            var now = DateTimeOffset.UtcNow;
            user.IsActive = request.IsActive;
            user.DeactivatedAt = request.IsActive ? null : now;
            user.UpdatedAt = now;
            user.SecurityStamp = User.NewStamp();
            if (!request.IsActive)
            {
                user.RefreshTokenHash = null;
                user.RefreshTokenExpiresAt = null;
            }
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, request.IsActive ? "user.activated" : "user.deactivated",
                AccessAudit.EntityUser, user.Id.ToString(), request.IsActive ? "inactive" : "active", request.IsActive ? "active" : "inactive",
                $"{user.Email} {(request.IsActive ? "reactivated" : "deactivated; all sessions ended")}{(reason is null ? "" : $": {reason}")}",
                traceId, user.Factories.FirstOrDefault()?.FactoryId));
            await _db.SaveChangesAsync(ct);
            return AccessResult<UserDetail>.Ok(default!);
        }, userId, caller, ct);

    public Task<AccessResult<UserDetail>> AssignRolesAsync(AccessContext caller, Guid userId, AssignRolesRequest request, string? traceId, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            if (caller.UserId == userId)
                return Forbidden<UserDetail>("cannot_change_own_roles", "You can't change your own roles. Ask another administrator.");
            var (user, error) = await LoadManageableAsync(caller, userId, allowSelf: false, ct);
            if (error is not null) return error.As<UserDetail>();

            var wanted = request.Roles.Distinct(StringComparer.Ordinal).ToList();
            var current = user!.Roles.Select(r => r.RoleId).ToList();
            var added = wanted.Except(current).ToList();
            var removed = current.Except(wanted).ToList();
            if (added.Count == 0 && removed.Count == 0) return AccessResult<UserDetail>.Ok(default!);

            var grantError = CheckGrantable(caller, added);
            if (grantError is not null) return grantError.As<UserDetail>();

            if (removed.Contains(SystemRoles.SuperAdmin) && await OtherActiveSuperAdminsAsync(userId, ct) == 0)
                return AccessResult<UserDetail>.Conflict("last_super_admin", "This is the last Super Admin. Make someone else a Super Admin first.");

            var newScope = AccessCatalog.ScopeFor(wanted);
            if (wanted.Count > 0 && newScope != RoleScope.Platform && user.Factories.Count == 0)
                return AccessResult<UserDetail>.Invalid("factory_required", "Assign the user to a factory before giving them a factory role.");

            var now = DateTimeOffset.UtcNow;
            user.Roles.RemoveAll(r => removed.Contains(r.RoleId));
            foreach (var r in added) user.Roles.Add(new UserRole { UserId = user.Id, RoleId = r, AssignedAt = now, AssignedByUserId = caller.UserId });
            user.SecurityStamp = User.NewStamp();
            user.UpdatedAt = now;
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "user.roles_changed", AccessAudit.EntityUser, user.Id.ToString(),
                AccessAudit.Roles(current), AccessAudit.Roles(wanted),
                $"roles of {user.Email}: +[{string.Join(",", added)}] -[{string.Join(",", removed)}]",
                traceId, user.Factories.FirstOrDefault()?.FactoryId));
            await _db.SaveChangesAsync(ct);
            return AccessResult<UserDetail>.Ok(default!);
        }, userId, caller, ct);

    public Task<AccessResult<UserDetail>> AssignScopeAsync(AccessContext caller, Guid userId, AssignScopeRequest request, string? traceId, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            if (caller.UserId == userId)
                return Forbidden<UserDetail>("cannot_change_own_scope", "You can't change your own factory or line assignment.");
            var (user, error) = await LoadManageableAsync(caller, userId, allowSelf: false, ct);
            if (error is not null) return error.As<UserDetail>();

            var factoryIds = request.FactoryIds.Distinct().ToList();
            var lineIds = (request.LineIds ?? Array.Empty<string>()).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            var roles = user!.Roles.Select(r => r.RoleId).ToList();
            var scopeError = await CheckScopeAsync(caller, roles, factoryIds, lineIds, ct);
            if (scopeError is not null) return scopeError.As<UserDetail>();

            var beforeF = user.Factories.Select(f => f.FactoryId).ToHashSet();
            var beforeL = user.Lines.Select(l => l.LineId).ToHashSet(StringComparer.Ordinal);
            if (beforeF.SetEquals(factoryIds) && beforeL.SetEquals(lineIds)) return AccessResult<UserDetail>.Ok(default!);

            var now = DateTimeOffset.UtcNow;
            user.Factories.RemoveAll(f => !factoryIds.Contains(f.FactoryId));
            foreach (var f in factoryIds.Where(f => !beforeF.Contains(f)))
                user.Factories.Add(new UserFactoryAssignment { UserId = user.Id, FactoryId = f, AssignedAt = now, AssignedByUserId = caller.UserId });
            user.Lines.RemoveAll(l => !lineIds.Contains(l.LineId));
            foreach (var l in lineIds.Where(l => !beforeL.Contains(l)))
                user.Lines.Add(new UserProductionLineAssignment { UserId = user.Id, LineId = l, AssignedAt = now, AssignedByUserId = caller.UserId });
            user.SecurityStamp = User.NewStamp();
            user.UpdatedAt = now;
            var codes = await _db.Factories.Where(f => factoryIds.Contains(f.Id) || beforeF.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Code, ct);
            string Fmt(IEnumerable<Guid> ids) => string.Join(",", ids.Select(i => codes.GetValueOrDefault(i, i.ToString())).OrderBy(c => c));
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "user.scope_changed", AccessAudit.EntityUser, user.Id.ToString(),
                $"factories={Fmt(beforeF)}; lines={string.Join(",", beforeL.OrderBy(l => l))}",
                $"factories={Fmt(factoryIds)}; lines={string.Join(",", lineIds.OrderBy(l => l))}",
                $"factory / line scope of {user.Email} changed", traceId,
                factoryIds.Count > 0 ? factoryIds[0] : beforeF.Cast<Guid?>().FirstOrDefault()));
            await _db.SaveChangesAsync(ct);
            return AccessResult<UserDetail>.Ok(default!);
        }, userId, caller, ct);

    public async Task<AccessResult<UserDetail>> ResetPasswordAsync(AccessContext caller, Guid userId, AdminResetPasswordRequest request, string? traceId, CancellationToken ct)
    {
        if (caller.UserId == userId)
            return Forbidden<UserDetail>("use_account_password", "Change your own password in Account settings.");
        var (user, error) = await LoadManageableAsync(caller, userId, allowSelf: false, ct);
        if (error is not null) return error.As<UserDetail>();
        if (string.Equals(request.TemporaryPassword.Trim(), user!.Email, StringComparison.OrdinalIgnoreCase))
            return AccessResult<UserDetail>.Invalid("password_is_email", "Password must not be the email address.");

        var now = DateTimeOffset.UtcNow;
        user.PasswordHash = _hasher.Hash(user, request.TemporaryPassword);
        user.MustChangePassword = true;
        user.SecurityStamp = User.NewStamp();
        user.RefreshTokenHash = null;
        user.RefreshTokenExpiresAt = null;
        user.UpdatedAt = now;
        await _db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "user.password_reset_by_admin", AccessAudit.EntityUser, user.Id.ToString(),
            null, null, $"temporary password set for {user.Email}; must be changed at next sign-in; all sessions ended",
            traceId, user.Factories.FirstOrDefault()?.FactoryId));
        await _db.SaveChangesAsync(ct);
        _sessions.Invalidate(userId);
        return AccessResult<UserDetail>.Ok((await DetailAsync(caller, userId, ct))!);
    }

    // ─── Rules ─────────────────────────────────────────────────────────────

    /// <summary>Users the caller may see (see the class summary).</summary>
    private IQueryable<User> VisibleUsers(AccessContext caller)
    {
        var users = _db.Users.AsNoTracking();
        if (caller.IsPlatform) return users;
        var mine = caller.FactoryIds.ToList();
        return users.Where(u => u.Factories.Any()
                                && u.Factories.All(f => mine.Contains(f.FactoryId))
                                && !u.Roles.Any(r => r.RoleId == SystemRoles.SuperAdmin));
    }

    /// <summary>The caller may manage a (visible) user: not themselves, and every role of theirs is one the caller could grant.</summary>
    private static bool CanManage(AccessContext caller, Guid userId, IEnumerable<string> userRoles) =>
        caller.UserId != userId && userRoles.All(r => AccessCatalog.CanGrant(caller.Roles, caller.Permissions.ToArray(), r));

    private async Task<(User? User, AccessResult<object>? Error)> LoadManageableAsync(AccessContext caller, Guid userId, bool allowSelf, CancellationToken ct)
    {
        if (!await VisibleUsers(caller).AnyAsync(u => u.Id == userId, ct))
            return (null, AccessResult<object>.NotFound("user_not_found", "User not found."));
        var user = await _db.Users.Include(u => u.Roles).Include(u => u.Factories).Include(u => u.Lines)
            .FirstAsync(u => u.Id == userId, ct);
        var roles = user.Roles.Select(r => r.RoleId).ToList();
        if (!allowSelf && caller.UserId == userId)
            return (null, AccessResult<object>.Forbidden("cannot_modify_self", "Use Account settings to change your own account."));
        if (roles.Contains(SystemRoles.SuperAdmin) && !caller.IsPlatform)
            return (null, AccessResult<object>.Forbidden("cannot_modify_super_admin", "Only a Super Admin can change a Super Admin account."));
        if (!CanManage(caller, userId, roles))
            return (null, AccessResult<object>.Forbidden("insufficient_authority",
                "This user holds a role you are not allowed to manage."));
        return (user, null);
    }

    private static AccessResult<object>? CheckGrantable(AccessContext caller, IEnumerable<string> roles)
    {
        var perms = caller.Permissions.ToArray();
        foreach (var role in roles)
        {
            if (!AccessCatalog.IsRole(role)) return AccessResult<object>.Invalid("unknown_role", $"Unknown role '{role}'.");
            if (!AccessCatalog.CanGrant(caller.Roles, perms, role))
                return AccessResult<object>.Forbidden("role_not_grantable",
                    $"You are not allowed to grant the {AccessCatalog.Role(role)!.Name} role.");
        }
        return null;
    }

    /// <summary>Factories must exist, be active and (for non-platform callers) be the caller's; lines must belong to those factories.</summary>
    private async Task<AccessResult<object>?> CheckScopeAsync(AccessContext caller, IReadOnlyCollection<string> roles,
        IReadOnlyCollection<Guid> factoryIds, IReadOnlyCollection<string> lineIds, CancellationToken ct)
    {
        var scope = AccessCatalog.ScopeFor(roles);
        if (!caller.IsPlatform)
        {
            if (factoryIds.Count == 0)
                return AccessResult<object>.Invalid("factory_required", "Choose the factory this user belongs to.");
            if (factoryIds.Any(f => !caller.FactoryIds.Contains(f)))
                return AccessResult<object>.Forbidden("factory_out_of_scope", "You can only assign factories you belong to.");
        }
        if (roles.Count > 0 && scope != RoleScope.Platform && factoryIds.Count == 0)
            return AccessResult<object>.Invalid("factory_required", "A factory role needs a factory. Choose one.");

        if (factoryIds.Count > 0)
        {
            var ids = factoryIds.ToList();
            var found = await _db.Factories.AsNoTracking().Where(f => ids.Contains(f.Id)).Select(f => new { f.Id, f.IsActive }).ToListAsync(ct);
            if (found.Count != ids.Count) return AccessResult<object>.Invalid("factory_not_found", "One of the factories doesn't exist.");
            if (found.Any(f => !f.IsActive)) return AccessResult<object>.Invalid("factory_inactive", "One of the factories is deactivated.");
        }
        if (lineIds.Count > 0)
        {
            var ids = lineIds.ToList();
            var lines = await _db.ProductionLines.AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => new { l.Id, l.FactoryId }).ToListAsync(ct);
            if (lines.Count != ids.Count) return AccessResult<object>.Invalid("line_not_found", "One of the production lines doesn't exist.");
            if (lines.Any(l => l.FactoryId is null || !factoryIds.Contains(l.FactoryId.Value)))
                return AccessResult<object>.Invalid("line_not_in_factory", "Every line must belong to one of the user's factories.");
        }
        return null;
    }

    /// <summary>Active Super Admins other than <paramref name="userId"/>, counted under a row lock.</summary>
    private async Task<int> OtherActiveSuperAdminsAsync(Guid userId, CancellationToken ct)
    {
        var ids = await _db.Database.SqlQuery<Guid>(
            $"""
             SELECT u."Id" AS "Value" FROM users u
             JOIN user_roles r ON r."UserId" = u."Id"
             WHERE r."RoleId" = {SystemRoles.SuperAdmin} AND u."IsActive"
             FOR UPDATE OF u
             """).ToListAsync(ct);
        return ids.Count(id => id != userId);
    }

    private async Task<AccessResult<UserDetail>> InTransactionAsync(Func<Task<AccessResult<UserDetail>>> work, Guid userId,
        AccessContext caller, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var r = await work();
            if (r.Outcome is AccessOutcome.Ok or AccessOutcome.Created) await tx.CommitAsync(ct);
            else await tx.RollbackAsync(ct);
            return r;
        });
        if (result.Outcome is not (AccessOutcome.Ok or AccessOutcome.Created)) return result;
        _sessions.Invalidate(userId);
        _db.ChangeTracker.Clear();
        return AccessResult<UserDetail>.Ok((await DetailAsync(caller, userId, ct))!);
    }

    // ─── Read model ────────────────────────────────────────────────────────

    private async Task<UserDetail?> DetailAsync(AccessContext caller, Guid userId, CancellationToken ct)
    {
        if (!await VisibleUsers(caller).AnyAsync(u => u.Id == userId, ct)) return null;
        var u = await _db.Users.AsNoTracking().Include(x => x.Roles).Include(x => x.Factories).Include(x => x.Lines)
            .FirstAsync(x => x.Id == userId, ct);
        var roles = SortRoles(u.Roles.Select(r => r.RoleId));
        var factories = await FactoryRefsAsync(u.Factories.Select(f => f.FactoryId), ct);
        var lineIds = u.Lines.Select(l => l.LineId).ToList();
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).OrderBy(l => l.Id)
            .Select(l => new LineRef(l.Id, l.Name, l.FactoryId)).ToListAsync(ct);
        var worker = await _db.Workers.AsNoTracking().Where(w => w.UserId == userId)
            .Select(w => new WorkerRef(w.Id, w.WorkerCode, w.FullName)).FirstOrDefaultAsync(ct);

        var manage = CanManage(caller, userId, roles) && (caller.IsPlatform || !roles.Contains(SystemRoles.SuperAdmin));
        var can = new UserCapabilities(
            Edit: manage && caller.Has(Permissions.UsersUpdate),
            ChangeStatus: manage && caller.Has(Permissions.UsersDeactivate),
            AssignRoles: manage && caller.Has(Permissions.RolesAssign),
            ChangeScope: manage && caller.Has(Permissions.UsersUpdate),
            ResetPassword: manage && caller.Has(Permissions.UsersResetPassword),
            GrantableRoles: manage && caller.Has(Permissions.RolesAssign)
                ? AccessCatalog.GrantableBy(caller.Roles, caller.Permissions.ToArray())
                : Array.Empty<string>());

        return new UserDetail(u.Id, u.Email, u.FullName, u.Phone, u.IsActive, u.MustChangePassword, roles,
            AccessCatalog.PermissionsFor(roles), AccessCatalog.ScopeFor(roles).ToString(),
            factories.Values.OrderBy(f => f.Name).ToList(), lines, worker,
            u.CreatedAt, u.UpdatedAt, u.LastLoginAt, u.DeactivatedAt, can);
    }

    private async Task<Dictionary<Guid, FactoryRef>> FactoryRefsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return await _db.Factories.AsNoTracking().Where(f => list.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, f => new FactoryRef(f.Id, f.Code, f.Name), ct);
    }

    private static IReadOnlyList<string> SortRoles(IEnumerable<string> roles) =>
        roles.Where(AccessCatalog.IsRole).OrderByDescending(r => AccessCatalog.Role(r)!.Rank).ThenBy(r => r, StringComparer.Ordinal).ToList();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static AccessResult<T> NotFound<T>() => AccessResult<T>.NotFound("user_not_found", "User not found.");
    private static AccessResult<T> Forbidden<T>(string code, string message) => AccessResult<T>.Forbidden(code, message);
}

internal static class AccessResultExtensions
{
    /// <summary>Re-type a failed result (no value).</summary>
    public static AccessResult<T> As<T>(this AccessResult<object> r) => new(r.Outcome, default, r.Code, r.Message);
}
