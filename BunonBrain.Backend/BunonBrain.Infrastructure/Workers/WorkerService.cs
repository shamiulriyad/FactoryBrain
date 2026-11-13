using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Application.Qc;
using BunonBrain.Application.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Assignments;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Workers;

/// <summary>
/// Implements <see cref="IWorkerService"/>. The rules, all enforced here:
/// <list type="bullet">
///   <item><b>Reach.</b> Super Admin: every worker. Factory roles: workers of
///   their factories. Production Supervisor: workers on assigned lines.
///   Worker: only the record linked to their own account. Anything else
///   answers "not found".</item>
///   <item><b>Detail.</b> What is returned depends on the caller (see
///   <see cref="WorkerDto"/>): basic, employment or full.</item>
///   <item><b>Worker ID.</b> Drawn from the database sequence
///   <c>worker_number_seq</c>, so concurrent registrations can't get the same
///   one and a number is never handed out twice; unique indexes back it up.</item>
///   <item><b>Status.</b> Changed only through
///   <see cref="ChangeStatusAsync"/>, which checks
///   <see cref="WorkerStatusTransitions"/> under a row lock and writes the
///   status history. Workers are never deleted.</item>
///   <item><b>Audit.</b> Every change is written to the audit log in the same
///   transaction. Personal values are not copied into the audit text.</item>
/// </list>
/// </summary>
public sealed partial class WorkerService : IWorkerService
{
    public const int MaxPageSize = 100;

    private const string TierBasic = "basic", TierEmployment = "employment", TierFull = "full";

    private readonly BunonBrainDbContext _db;
    private readonly ISessionValidator _sessions;
    private readonly IUserAdminService _users;
    private readonly IWorkerPhotoStore _photos;
    private readonly ILogger<WorkerService> _log;

    public WorkerService(BunonBrainDbContext db, ISessionValidator sessions, IUserAdminService users,
        IWorkerPhotoStore photos, ILogger<WorkerService> log)
    {
        _db = db; _sessions = sessions; _users = users; _photos = photos; _log = log;
    }

    // ─── Reach and detail ──────────────────────────────────────────────────

    /// <summary>Workers inside the caller's scope (not counting their own record).</summary>
    private IQueryable<WorkerProfile> InScope(AccessContext caller)
    {
        var workers = _db.Workers.AsNoTracking();
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => workers,
            RoleScope.Factory => workers.Where(w => factories.Contains(w.FactoryId)),
            RoleScope.Line => workers.Where(w => factories.Contains(w.FactoryId) && w.LineId != null && lines.Contains(w.LineId)),
            _ => workers.Where(_ => false),
        };
    }

    private static bool IsOwn(AccessContext caller, Guid? workerUserId) =>
        workerUserId == caller.UserId && caller.Has(Permissions.SelfView);

    private static string TierOf(AccessContext caller, Guid? workerUserId) =>
        IsOwn(caller, workerUserId) || caller.Has(Permissions.WorkersManage) ? TierFull
        : caller.Has(Permissions.WorkersDetailsView) ? TierEmployment
        : TierBasic;

    private enum Reach
    {
        /// <summary>The directory: in scope with <c>workers.view</c>, or the caller's own record.</summary>
        View,
        /// <summary>In scope with factory-wide reach (register, edit, status, cards, account).</summary>
        Manage,
        /// <summary>In scope at any level (a supervisor moving a worker on their line).</summary>
        Scope,
    }

    /// <summary>The id of the worker <paramref name="idOrCode"/> names, when the caller reaches it; else null.</summary>
    private async Task<Guid?> ResolveAsync(AccessContext caller, string idOrCode, Reach reach, CancellationToken ct)
    {
        var workers = _db.Workers.AsNoTracking();
        if (Guid.TryParse(idOrCode, out var id)) workers = workers.Where(w => w.Id == id);
        else if (WorkerRules.LooksLikeWorkerCode(idOrCode))
        {
            var code = WorkerRules.NormaliseWorkerCode(idOrCode);
            workers = workers.Where(w => w.WorkerCode == code);
        }
        else return null;

        var row = await workers.Select(w => new { w.Id, w.FactoryId, w.LineId, w.UserId }).FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var inScope = caller.Scope switch
        {
            RoleScope.Platform => true,
            RoleScope.Factory => caller.FactoryIds.Contains(row.FactoryId),
            RoleScope.Line => caller.FactoryIds.Contains(row.FactoryId) && row.LineId is not null && caller.LineIds.Contains(row.LineId),
            _ => false,
        };
        var ok = reach switch
        {
            Reach.View => (inScope && caller.Has(Permissions.WorkersView)) || IsOwn(caller, row.UserId),
            Reach.Manage => inScope && caller.Scope >= RoleScope.Factory,
            _ => inScope,
        };
        return ok ? row.Id : null;
    }

    // ─── Queries ───────────────────────────────────────────────────────────

    public async Task<PagedResult<WorkerDto>> ListAsync(AccessContext caller, WorkerListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var workers = Sort(caller, Filter(caller, q), q.Sort);
        var total = await workers.CountAsync(ct);
        var rows = await workers.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<WorkerDto>(await ToDtosAsync(caller, rows, ct), page, size, total);
    }

    /// <summary>The directory query: the caller's scope narrowed by the filters. Filters on fields the caller can't see are ignored.</summary>
    private IQueryable<WorkerProfile> Filter(AccessContext caller, WorkerListQuery q)
    {
        var workers = caller.Has(Permissions.WorkersView) ? InScope(caller) : _db.Workers.AsNoTracking().Where(_ => false);
        var employment = caller.HasAny(Permissions.WorkersDetailsView, Permissions.WorkersManage);

        if (q.FactoryId is { } f) workers = workers.Where(w => w.FactoryId == f);
        if (q.DepartmentId is { } d) workers = workers.Where(w => w.DepartmentId == d);
        if (q.FloorId is { } fl) workers = workers.Where(w => w.FloorId == fl);
        if (!string.IsNullOrWhiteSpace(q.LineId))
        {
            var line = q.LineId.Trim();
            workers = line == "none" ? workers.Where(w => w.LineId == null) : workers.Where(w => w.LineId == line);
        }
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            var status = WorkerStatuses.Parse(q.Status);
            workers = status is null ? workers.Where(_ => false) : workers.Where(w => w.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(q.Designation))
        {
            var exact = OrganizationService.Like(q.Designation.Trim());
            workers = workers.Where(w => w.Designation != null && EF.Functions.ILike(w.Designation, exact, "\\"));
        }
        if (q.Assigned is { } assigned)
            workers = assigned
                ? workers.Where(w => _db.Assignments.Any(a => a.WorkerId == w.Id && a.EndAt == null))
                : workers.Where(w => !_db.Assignments.Any(a => a.WorkerId == w.Id && a.EndAt == null));
        if (employment && !string.IsNullOrWhiteSpace(q.EmploymentType))
        {
            var type = EmploymentTypes.Parse(q.EmploymentType);
            workers = type is null ? workers.Where(_ => false) : workers.Where(w => w.EmploymentType == type);
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            workers = employment
                ? workers.Where(w => EF.Functions.ILike(w.FullName, pattern, "\\") || EF.Functions.ILike(w.WorkerCode, pattern, "\\")
                                     || (w.EmployeeCode != null && EF.Functions.ILike(w.EmployeeCode, pattern, "\\")))
                : workers.Where(w => EF.Functions.ILike(w.FullName, pattern, "\\") || EF.Functions.ILike(w.WorkerCode, pattern, "\\"));
        }
        return workers;
    }

    private IQueryable<WorkerProfile> Sort(AccessContext caller, IQueryable<WorkerProfile> workers, string? sort)
    {
        var key = (sort?.Trim() ?? "workerId");
        // Ordering by a field the caller can't see would reveal it.
        if (key.TrimStart('-') == "joined" && !caller.HasAny(Permissions.WorkersDetailsView, Permissions.WorkersManage)) key = "workerId";
        return key switch
        {
            "-workerId" => workers.OrderByDescending(w => w.WorkerNumber),
            "name" => workers.OrderBy(w => w.FullName).ThenBy(w => w.WorkerNumber),
            "-name" => workers.OrderByDescending(w => w.FullName).ThenBy(w => w.WorkerNumber),
            "joined" => workers.OrderBy(w => w.JoinedOn == null).ThenBy(w => w.JoinedOn).ThenBy(w => w.WorkerNumber),
            "-joined" => workers.OrderBy(w => w.JoinedOn == null).ThenByDescending(w => w.JoinedOn).ThenBy(w => w.WorkerNumber),
            "status" => workers.OrderBy(w => w.Status).ThenBy(w => w.WorkerNumber),
            "-status" => workers.OrderByDescending(w => w.Status).ThenBy(w => w.WorkerNumber),
            "designation" => workers.OrderBy(w => w.Designation == null).ThenBy(w => w.Designation).ThenBy(w => w.WorkerNumber),
            "-designation" => workers.OrderBy(w => w.Designation == null).ThenByDescending(w => w.Designation).ThenBy(w => w.WorkerNumber),
            "department" => workers.OrderBy(w => _db.Departments.Where(d => d.Id == w.DepartmentId).Select(d => d.Name).FirstOrDefault()).ThenBy(w => w.WorkerNumber),
            "-department" => workers.OrderByDescending(w => _db.Departments.Where(d => d.Id == w.DepartmentId).Select(d => d.Name).FirstOrDefault()).ThenBy(w => w.WorkerNumber),
            "created" => workers.OrderBy(w => w.CreatedAt).ThenBy(w => w.WorkerNumber),
            "-created" => workers.OrderByDescending(w => w.CreatedAt).ThenBy(w => w.WorkerNumber),
            _ => workers.OrderBy(w => w.WorkerNumber),
        };
    }

    public async Task<AccessResult<WorkerDto>> GetAsync(AccessContext caller, string idOrCode, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.View, ct) is not { } id) return NotFound<WorkerDto>();
        return AccessResult<WorkerDto>.Ok(await DtoAsync(caller, id, ct));
    }

    public async Task<WorkerDto?> MineAsync(AccessContext caller, CancellationToken ct)
    {
        if (!caller.Has(Permissions.SelfView)) return null;
        var w = await _db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == caller.UserId, ct);
        return w is null ? null : (await ToDtosAsync(caller, new[] { w }, ct))[0];
    }

    public async Task<AccessResult<WorkerProfileDto>> ProfileAsync(AccessContext caller, string idOrCode, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.View, ct) is not { } id) return NotFound<WorkerProfileDto>();
        var w = await _db.Workers.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        var dto = (await ToDtosAsync(caller, new[] { w }, ct))[0];
        var tier = dto.Detail;
        var own = IsOwn(caller, w.UserId);
        var factory = await _db.Factories.AsNoTracking().Where(f => f.Id == w.FactoryId)
            .Select(f => new FactoryRef(f.Id, f.Code, f.Name)).FirstAsync(ct);

        WorkerAccountDto? account = null;
        if (w.UserId is { } uid && (tier == TierFull || caller.Has(Permissions.UsersView)))
        {
            var user = await _db.Users.AsNoTracking().Where(u => u.Id == uid)
                .Select(u => new { u.Email, u.IsActive, Roles = u.Roles.Select(r => r.RoleId).ToList() }).FirstOrDefaultAsync(ct);
            if (user is not null)
                account = new WorkerAccountDto(uid, user.Email, user.IsActive,
                    caller.Has(Permissions.UsersView) || own ? user.Roles.Where(AccessCatalog.IsRole).ToList() : null);
        }

        IReadOnlyList<WorkerCardDto>? cards = null;
        if (caller.Has(Permissions.WorkersManage) || own)
            cards = await CardRowsAsync(id, ct);

        IReadOnlyList<WorkerStatusChangeDto>? history = null;
        if (tier != TierBasic)
            history = await _db.WorkerStatusHistory.AsNoTracking().Where(h => h.WorkerId == id).OrderByDescending(h => h.Id).Take(100)
                .Select(h => new WorkerStatusChangeDto(h.Id, h.FromStatus, h.ToStatus, h.Reason, h.EffectiveOn, h.ChangedAt, h.ChangedByEmail))
                .ToListAsync(ct);

        var manage = caller.Has(Permissions.WorkersManage) && caller.Scope >= RoleScope.Factory && caller.CanAccessFactory(w.FactoryId);
        var separated = WorkerStatuses.IsSeparated(w.Status);
        var can = new WorkerCapabilities(
            Edit: manage,
            ChangeStatus: manage,
            AssignWorkstation: !separated && caller.HasAny(Permissions.WorkersAssign, Permissions.WorkersManage) && !own,
            ManageAccount: caller.Has(Permissions.UsersUpdate) && caller.Scope >= RoleScope.Factory && caller.CanAccessFactory(w.FactoryId),
            ManageCards: manage,
            AllowedStatuses: manage ? WorkerStatusTransitions.AllowedFrom(w.Status) : Array.Empty<string>());

        var machine = await (from a in _db.Assignments.AsNoTracking()
                             join m in _db.Machines.AsNoTracking() on a.MachineId equals m.Id
                             where a.WorkerId == id && a.EndAt == null
                             select m.MachineType == null ? m.Code : m.Code + " · " + m.MachineType).FirstOrDefaultAsync(ct);
        return AccessResult<WorkerProfileDto>.Ok(new WorkerProfileDto(dto, factory, machine, account, cards, history, ActivityPlaceholders, can));
    }

    /// <summary>
    /// Sections the profile reserves for modules that don't exist yet. They
    /// carry no rows: nothing about attendance or activity is invented.
    /// </summary>
    private static readonly IReadOnlyList<WorkerActivitySection> ActivityPlaceholders = new[]
    {
        Section("attendance", "Attendance history", "Appears when the attendance module is connected."),
        Section("shifts", "Shift history", "Appears when shift management is connected."),
        Section("activityTimeline", "Activity timeline", "Appears when floor monitoring is connected."),
        Section("breaks", "Break history", "Appears when the attendance module is connected."),
        Section("overtime", "Overtime history", "Appears when overtime management is connected."),
        Section("monitoringAlerts", "Monitoring alerts", "Appears when worker monitoring is connected."),
    };

    private static WorkerActivitySection Section(string key, string title, string note) =>
        new(key, title, Available: false, note, Array.Empty<object>());

    public async Task<WorkerStatisticsDto> StatisticsAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var workers = caller.Has(Permissions.WorkersView) ? InScope(caller) : _db.Workers.AsNoTracking().Where(_ => false);
        if (factoryId is { } f) workers = workers.Where(w => w.FactoryId == f);
        var groups = await workers
            .GroupBy(w => new { w.FactoryId, w.DepartmentId, w.LineId, w.Status, w.EmploymentType, w.Designation })
            .Select(g => new { g.Key.FactoryId, g.Key.DepartmentId, g.Key.LineId, g.Key.Status, g.Key.EmploymentType, g.Key.Designation, Count = g.Count() })
            .ToListAsync(ct);

        int Of(string status) => groups.Where(g => g.Status == status).Sum(g => g.Count);
        var factoryIds = groups.Select(g => g.FactoryId).Distinct().ToList();
        var deptIds = groups.Where(g => g.DepartmentId != null).Select(g => g.DepartmentId!.Value).Distinct().ToList();
        var lineIds = groups.Where(g => g.LineId != null).Select(g => g.LineId!).Distinct().ToList();
        var factories = await _db.Factories.AsNoTracking().Where(x => factoryIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var depts = await _db.Departments.AsNoTracking().Where(x => deptIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var lines = await _db.ProductionLines.AsNoTracking().Where(x => lineIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var rows = groups.Select(g => (g.FactoryId, g.DepartmentId, g.LineId, g.Status, g.EmploymentType, g.Designation, g.Count)).ToList();
        List<WorkerCount> Count<TKey>(Func<(Guid FactoryId, Guid? DepartmentId, string? LineId, string Status, string EmploymentType, string? Designation, int Count), TKey?> key,
            Func<TKey, (string Key, string Label)> label, bool keepNull = false) =>
            rows.GroupBy(key)
                .Where(g => keepNull || g.Key is not null)
                .Select(g =>
                {
                    var (k, l) = g.Key is null ? ("none", "Not assigned") : label(g.Key);
                    return new WorkerCount(k, l, g.Sum(x => x.Count), g.Where(x => x.Status == WorkerStatuses.Active).Sum(x => x.Count));
                })
                .OrderByDescending(c => c.Total).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();

        var employment = caller.HasAny(Permissions.WorkersDetailsView, Permissions.WorkersManage);
        return new WorkerStatisticsDto(
            Total: rows.Sum(r => r.Count),
            Active: Of(WorkerStatuses.Active), Inactive: Of(WorkerStatuses.Inactive), Suspended: Of(WorkerStatuses.Suspended),
            Resigned: Of(WorkerStatuses.Resigned), Terminated: Of(WorkerStatuses.Terminated),
            WithoutDepartment: rows.Where(r => r.DepartmentId is null).Sum(r => r.Count),
            WithoutLine: rows.Where(r => r.LineId is null).Sum(r => r.Count),
            ByStatus: WorkerStatuses.All.Select(s => new WorkerCount(s, s, Of(s), s == WorkerStatuses.Active ? Of(s) : 0)).ToList(),
            ByFactory: Count<Guid?>(r => r.FactoryId, k => (k!.Value.ToString(), factories.GetValueOrDefault(k.Value, "Unknown factory"))),
            ByDepartment: Count<Guid?>(r => r.DepartmentId, k => (k!.Value.ToString(), depts.GetValueOrDefault(k.Value, "Unknown department")), keepNull: true),
            ByLine: Count<string>(r => r.LineId, k => (k, lines.GetValueOrDefault(k, k)), keepNull: true),
            ByEmploymentType: employment ? Count<string>(r => r.EmploymentType, k => (k, k)) : Array.Empty<WorkerCount>(),
            ByDesignation: Count<string>(r => r.Designation, k => (k, k)).Take(50).ToList());
    }

    // ─── Registration and editing ──────────────────────────────────────────

    public async Task<AccessResult<WorkerDto>> CreateAsync(AccessContext caller, CreateWorkerRequest r, string? traceId, CancellationToken ct)
    {
        if (caller.Scope < RoleScope.Factory || !caller.CanAccessFactory(r.FactoryId))
            return AccessResult<WorkerDto>.Forbidden("factory_out_of_scope", "You can only add workers to your own factory.");
        if (!await _db.Factories.AnyAsync(f => f.Id == r.FactoryId && f.IsActive, ct))
            return AccessResult<WorkerDto>.Invalid("factory_not_found", "Factory not found or deactivated.");
        if (await CheckPlacementAsync(caller, r.FactoryId, r.DepartmentId, r.FloorId, Clean(r.LineId), requireActive: true, ct) is { } placement)
            return placement;

        var employeeCode = WorkerRules.NormaliseEmployeeCode(r.EmployeeCode);
        if (employeeCode is not null && await _db.Workers.AnyAsync(w => w.FactoryId == r.FactoryId && w.EmployeeCode == employeeCode, ct))
            return AccessResult<WorkerDto>.Conflict("employee_code_taken", $"Employee number {employeeCode} is already used in this factory.");

        var now = DateTimeOffset.UtcNow;
        var number = (await NextWorkerNumbersAsync(1, ct))[0];
        var worker = new WorkerProfile
        {
            WorkerNumber = number,
            WorkerCode = WorkerRules.FormatWorkerCode(number),
            FactoryId = r.FactoryId,
            EmployeeCode = employeeCode,
            FullName = r.FullName.Trim(),
            Gender = Genders.Parse(r.Gender),
            DateOfBirth = r.DateOfBirth,
            Phone = Clean(r.Phone),
            Email = Clean(r.Email)?.ToLowerInvariant(),
            PresentAddress = Clean(r.PresentAddress),
            EmergencyContactName = Clean(r.EmergencyContactName),
            EmergencyContactPhone = Clean(r.EmergencyContactPhone),
            EmergencyContactRelation = Clean(r.EmergencyContactRelation),
            JoinedOn = r.JoinedOn,
            Status = WorkerStatuses.Active,
            StatusChangedAt = now,
            EmploymentType = EmploymentTypes.Parse(r.EmploymentType) ?? EmploymentTypes.Permanent,
            Designation = Clean(r.Designation),
            Grade = Clean(r.Grade),
            DepartmentId = r.DepartmentId,
            FloorId = r.FloorId,
            LineId = Clean(r.LineId),
            CreatedByUserId = caller.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        AddRegistration(caller, worker, "registered", traceId, now);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<WorkerDto>.Conflict("employee_code_taken", "This employee number was just taken by another registration.");
        }
        _log.LogInformation("User {ActorId} registered worker {WorkerCode} in factory {FactoryId}.", caller.UserId, worker.WorkerCode, worker.FactoryId);
        return AccessResult<WorkerDto>.Created(await DtoAsync(caller, worker.Id, ct));
    }

    /// <summary>Track a new worker with its first status-history row and its audit entry.</summary>
    private void AddRegistration(AccessContext caller, WorkerProfile worker, string how, string? traceId, DateTimeOffset now)
    {
        _db.Workers.Add(worker);
        _db.WorkerStatusHistory.Add(new WorkerStatusChange
        {
            WorkerId = worker.Id, FactoryId = worker.FactoryId, FromStatus = null, ToStatus = worker.Status,
            Reason = how == "registered" ? "Registered" : "Registered by CSV import", EffectiveOn = worker.JoinedOn,
            ChangedAt = now, ChangedByUserId = caller.UserId, ChangedByEmail = AccessAudit.Clip(caller.Email, 256)!,
        });
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.created", AccessAudit.EntityWorker, worker.Id.ToString(),
            null, worker.Status, $"worker {worker.WorkerCode} {how}", traceId, worker.FactoryId));
    }

    /// <summary>
    /// The next <paramref name="count"/> worker numbers. <c>nextval</c> is
    /// atomic and independent of any transaction: two callers never receive
    /// the same value, and a value consumed by a failed registration is
    /// simply skipped (never reused).
    /// </summary>
    private async Task<List<long>> NextWorkerNumbersAsync(int count, CancellationToken ct)
    {
        const string seq = WorkerProfileConfiguration.NumberSequence;
        return await _db.Database
            .SqlQueryRaw<long>($"SELECT nextval('{seq}') AS \"Value\" FROM generate_series(1, {{0}})", count)
            .ToListAsync(ct);
    }

    public async Task<AccessResult<WorkerDto>> UpdateAsync(AccessContext caller, string idOrCode, UpdateWorkerRequest r, string? traceId, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Manage, ct) is not { } id) return NotFound<WorkerDto>();
        var w = await _db.Workers.FirstAsync(x => x.Id == id, ct);
        var lineId = Clean(r.LineId);
        if (await CheckPlacementAsync(caller, w.FactoryId, r.DepartmentId, r.FloorId, lineId,
                requireActive: r.DepartmentId != w.DepartmentId || r.FloorId != w.FloorId, ct) is { } placement)
            return placement;

        var employeeCode = WorkerRules.NormaliseEmployeeCode(r.EmployeeCode);
        if (employeeCode is not null && employeeCode != w.EmployeeCode
            && await _db.Workers.AnyAsync(x => x.FactoryId == w.FactoryId && x.EmployeeCode == employeeCode && x.Id != id, ct))
            return AccessResult<WorkerDto>.Conflict("employee_code_taken", $"Employee number {employeeCode} is already used in this factory.");

        // Field names only: the audit log must not become a second copy of personal data.
        var changed = new List<string>();
        void Set<T>(string name, T current, T next, Action<T> assign)
        {
            if (EqualityComparer<T>.Default.Equals(current, next)) return;
            changed.Add(name);
            assign(next);
        }
        Set("name", w.FullName, r.FullName.Trim(), v => w.FullName = v);
        Set("employee number", w.EmployeeCode, employeeCode, v => w.EmployeeCode = v);
        Set("phone", w.Phone, Clean(r.Phone), v => w.Phone = v);
        Set("email", w.Email, Clean(r.Email)?.ToLowerInvariant(), v => w.Email = v);
        Set("gender", w.Gender, Genders.Parse(r.Gender), v => w.Gender = v);
        Set("date of birth", w.DateOfBirth, r.DateOfBirth, v => w.DateOfBirth = v);
        Set("address", w.PresentAddress, Clean(r.PresentAddress), v => w.PresentAddress = v);
        Set("emergency contact", w.EmergencyContactName, Clean(r.EmergencyContactName), v => w.EmergencyContactName = v);
        Set("emergency contact", w.EmergencyContactPhone, Clean(r.EmergencyContactPhone), v => w.EmergencyContactPhone = v);
        Set("emergency contact", w.EmergencyContactRelation, Clean(r.EmergencyContactRelation), v => w.EmergencyContactRelation = v);
        Set("joining date", w.JoinedOn, r.JoinedOn, v => w.JoinedOn = v);
        Set("employment type", w.EmploymentType, EmploymentTypes.Parse(r.EmploymentType) ?? w.EmploymentType, v => w.EmploymentType = v);
        Set("designation", w.Designation, Clean(r.Designation), v => w.Designation = v);
        Set("grade", w.Grade, Clean(r.Grade), v => w.Grade = v);
        Set("department", w.DepartmentId, r.DepartmentId, v => w.DepartmentId = v);
        Set("floor", w.FloorId, r.FloorId, v => w.FloorId = v);
        if (lineId != w.LineId)
        {
            if (await _db.Assignments.AnyAsync(a => a.WorkerId == id && a.EndAt == null, ct))
                return AccessResult<WorkerDto>.Conflict("worker_has_assignment",
                    "This worker has an active workstation assignment. Transfer the worker to change the line.");
            changed.Add("line");
            w.LineId = lineId;
            w.Workstation = null; // a workstation belongs to a line
        }
        if (changed.Count == 0) return AccessResult<WorkerDto>.Ok(await DtoAsync(caller, id, ct));

        w.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.updated", AccessAudit.EntityWorker, w.Id.ToString(),
            null, null, $"worker {w.WorkerCode} updated: {string.Join(", ", changed.Distinct())}", traceId, w.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<WorkerDto>.Conflict("employee_code_taken", "This employee number was just taken by another worker.");
        }
        return AccessResult<WorkerDto>.Ok(await DtoAsync(caller, id, ct));
    }

    /// <summary>
    /// Department (required), floor and line must belong to the worker's
    /// factory; with <paramref name="requireActive"/> the department and floor
    /// must also be active (an edit that keeps a deactivated one is fine).
    /// </summary>
    private async Task<AccessResult<WorkerDto>?> CheckPlacementAsync(AccessContext caller, Guid factoryId, Guid? departmentId,
        Guid? floorId, string? lineId, bool requireActive, CancellationToken ct)
    {
        if (departmentId is not { } dept)
            return AccessResult<WorkerDto>.Invalid("department_required", "Choose the worker's department.");
        var d = await _db.Departments.AsNoTracking().Where(x => x.Id == dept).Select(x => new { x.FactoryId, x.IsActive }).FirstOrDefaultAsync(ct);
        if (d is null || d.FactoryId != factoryId)
            return AccessResult<WorkerDto>.Invalid("department_not_in_factory", "That department doesn't belong to the worker's factory.");
        if (requireActive && !d.IsActive)
            return AccessResult<WorkerDto>.Invalid("department_inactive", "That department is deactivated.");
        if (floorId is { } floor)
        {
            var fl = await _db.ProductionFloors.AsNoTracking().Where(x => x.Id == floor).Select(x => new { x.FactoryId, x.IsActive }).FirstOrDefaultAsync(ct);
            if (fl is null || fl.FactoryId != factoryId)
                return AccessResult<WorkerDto>.Invalid("floor_not_in_factory", "That production floor doesn't belong to the worker's factory.");
            if (requireActive && !fl.IsActive)
                return AccessResult<WorkerDto>.Invalid("floor_inactive", "That production floor is deactivated.");
        }
        return await CheckLineAsync(caller, factoryId, lineId, ct);
    }

    private async Task<AccessResult<WorkerDto>?> CheckLineAsync(AccessContext caller, Guid factoryId, string? lineId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lineId)) return null;
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line is null || line.FactoryId != factoryId)
            return AccessResult<WorkerDto>.Invalid("line_not_in_factory", "That line doesn't belong to the worker's factory.");
        if (!caller.CanAccessLine(line.Id, line.FactoryId))
            return AccessResult<WorkerDto>.Forbidden("line_out_of_scope", "That line is outside your assignment.");
        return null;
    }

    // ─── Status ────────────────────────────────────────────────────────────

    public async Task<AccessResult<WorkerDto>> ChangeStatusAsync(AccessContext caller, string idOrCode, ChangeWorkerStatusRequest r, string? traceId, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Manage, ct) is not { } id) return NotFound<WorkerDto>();
        var to = WorkerStatuses.Parse(r.Status);
        if (to is null)
            return AccessResult<WorkerDto>.Invalid("unknown_status", $"Status must be one of: {string.Join(", ", WorkerStatuses.All)}.");
        var reason = Clean(r.Reason);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (r.EffectiveOn is { } on && (on.Year < 1950 || on > today.AddDays(366)))
            return AccessResult<WorkerDto>.Invalid("effective_date_invalid", "The effective date is not a realistic date.");

        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            // The row lock makes concurrent status changes queue up, so each one is checked against the status it really follows.
            var w = (await _db.Workers.FromSqlInterpolated($"SELECT * FROM workers WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct)).Single();
            var check = WorkerStatusTransitions.Check(w.Status, to, reason);
            if (!check.Allowed)
            {
                await tx.RollbackAsync(ct);
                return check.Code == "status_reason_required"
                    ? AccessResult<WorkerDto>.Invalid(check.Code!, check.Message!)
                    : AccessResult<WorkerDto>.Conflict(check.Code!, check.Message!);
            }

            var now = DateTimeOffset.UtcNow;
            var from = w.Status;
            w.Status = to;
            w.StatusReason = reason;
            w.StatusChangedAt = now;
            w.UpdatedAt = now;
            _db.WorkerStatusHistory.Add(new WorkerStatusChange
            {
                WorkerId = w.Id, FactoryId = w.FactoryId, FromStatus = from, ToStatus = to, Reason = reason, EffectiveOn = r.EffectiveOn,
                ChangedAt = now, ChangedByUserId = caller.UserId, ChangedByEmail = AccessAudit.Clip(caller.Email, 256)!,
            });

            // Someone who has left keeps no working credential.
            var cardsEnded = 0;
            if (WorkerStatuses.IsSeparated(to))
            {
                var cards = await _db.WorkerCards.Where(c => c.WorkerId == w.Id && c.IsActive).ToListAsync(ct);
                foreach (var card in cards)
                {
                    card.IsActive = false;
                    card.DeactivatedAt = now;
                    card.DeactivatedByUserId = caller.UserId;
                    card.DeactivationReason = CardDeactivationReasons.WorkerLeft;
                }
                cardsEnded = cards.Count;
            }

            // Only active workers hold workstations: the assignment ends with the status change.
            var assignmentsEnded = 0;
            if (to != WorkerStatuses.Active)
            {
                assignmentsEnded = await AssignmentService.EndForWorkerStatusAsync(_db, caller, w.Id, to, now, traceId, ct);
                if (assignmentsEnded > 0) w.Workstation = null;
            }

            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.status_changed", AccessAudit.EntityWorker, w.Id.ToString(), from, to,
                $"worker {w.WorkerCode}: {from} → {to}{(reason is null ? "" : $": {reason}")}"
                + (cardsEnded > 0 ? $"; {cardsEnded} card(s) deactivated" : "")
                + (assignmentsEnded > 0 ? $"; {assignmentsEnded} workstation assignment(s) ended" : "")
                + (WorkerStatuses.IsSeparated(to) && w.UserId is not null ? "; a login account is still linked" : ""),
                traceId, w.FactoryId));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AccessResult<WorkerDto>.Ok(default!);
        });
        _db.ChangeTracker.Clear();
        return result.Outcome == AccessOutcome.Ok ? AccessResult<WorkerDto>.Ok(await DtoAsync(caller, id, ct)) : result;
    }

    public async Task<AccessResult<WorkerDto>> AssignWorkstationAsync(AccessContext caller, string idOrCode, AssignWorkstationRequest r, string? traceId, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Scope, ct) is not { } id) return NotFound<WorkerDto>();
        var w = await _db.Workers.FirstAsync(x => x.Id == id, ct);
        if (WorkerStatuses.IsSeparated(w.Status))
            return AccessResult<WorkerDto>.Conflict("worker_separated", $"This worker is {w.Status} and can't be assigned to a workstation.");
        // Workstations are assigned through /api/assignments (capacity, history, transfers). This endpoint only sets the line.
        if (!string.IsNullOrWhiteSpace(r.Workstation))
            return AccessResult<WorkerDto>.Invalid("use_assignments", "Assign a workstation with POST /api/assignments (or transfer the worker); this endpoint only sets the line.");
        if (await _db.Assignments.AnyAsync(a => a.WorkerId == id && a.EndAt == null, ct))
            return AccessResult<WorkerDto>.Conflict("worker_has_assignment", "This worker has an active workstation assignment. Transfer the worker instead.");
        var lineId = Clean(r.LineId);
        var lineError = await CheckLineAsync(caller, w.FactoryId, lineId, ct);
        if (lineError is not null) return lineError;
        // A supervisor can't move a worker off their lines to one they don't run.
        if (caller.IsLineScoped && lineId is null)
            return AccessResult<WorkerDto>.Forbidden("line_out_of_scope", "Choose one of your lines.");
        var from = w.LineId ?? "-";
        w.LineId = lineId;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.line_changed", AccessAudit.EntityWorker, w.Id.ToString(),
            from, w.LineId ?? "-", $"worker {w.WorkerCode} moved to line {w.LineId ?? "(none)"}", traceId, w.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<WorkerDto>.Ok(await DtoAsync(caller, id, ct));
    }

    // ─── Login account ─────────────────────────────────────────────────────

    public async Task<AccessResult<WorkerDto>> LinkAccountAsync(AccessContext caller, string idOrCode, LinkWorkerAccountRequest r, string? traceId, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Manage, ct) is not { } id) return NotFound<WorkerDto>();
        var w = await _db.Workers.FirstAsync(x => x.Id == id, ct);
        var before = w.UserId;
        if (before == r.UserId) return AccessResult<WorkerDto>.Ok(await DtoAsync(caller, id, ct));

        var roleGranted = false;
        if (r.UserId is { } uid)
        {
            var user = await _db.Users.Include(u => u.Factories).Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == uid, ct);
            var inFactory = user is not null && user.Factories.Any(f => f.FactoryId == w.FactoryId)
                            && (caller.IsPlatform || user.Factories.All(f => caller.FactoryIds.Contains(f.FactoryId)))
                            && user.Roles.All(x => x.RoleId != SystemRoles.SuperAdmin);
            if (!inFactory) return AccessResult<WorkerDto>.Invalid("user_not_in_factory", "Choose an account that belongs to this worker's factory.");
            if (await _db.Workers.AnyAsync(x => x.UserId == uid && x.Id != id, ct))
                return AccessResult<WorkerDto>.Conflict("account_already_linked", "That account is already linked to another worker.");

            // The account must be able to open "my profile". If none of its roles allows that, it gets the Worker role.
            var roles = user!.Roles.Select(x => x.RoleId).ToList();
            if (!AccessCatalog.PermissionsFor(roles).Contains(Permissions.SelfView))
            {
                var perms = caller.Permissions.ToArray();
                var mayGrant = caller.Has(Permissions.RolesAssign) && caller.UserId != uid
                               && AccessCatalog.CanGrant(caller.Roles, perms, SystemRoles.Worker)
                               && roles.All(role => AccessCatalog.CanGrant(caller.Roles, perms, role));
                if (!mayGrant)
                    return AccessResult<WorkerDto>.Forbidden("role_not_grantable",
                        "This account has no Worker role and you are not allowed to give it one. Ask a Factory Admin to assign the Worker role first.");
                var now = DateTimeOffset.UtcNow;
                user.Roles.Add(new UserRole { UserId = uid, RoleId = SystemRoles.Worker, AssignedAt = now, AssignedByUserId = caller.UserId });
                user.SecurityStamp = User.NewStamp();
                user.UpdatedAt = now;
                roleGranted = true;
                _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "user.roles_changed", AccessAudit.EntityUser, uid.ToString(),
                    AccessAudit.Roles(roles), AccessAudit.Roles(roles.Append(SystemRoles.Worker)),
                    $"Worker role added to {user.Email} when linking it to worker {w.WorkerCode}", traceId, w.FactoryId));
            }
        }
        w.UserId = r.UserId;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, r.UserId is null ? "worker.account_unlinked" : "worker.account_linked",
            AccessAudit.EntityWorker, w.Id.ToString(), before?.ToString() ?? "(none)", r.UserId?.ToString() ?? "(none)",
            $"login account of worker {w.WorkerCode} {(r.UserId is null ? "unlinked" : "linked")}", traceId, w.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<WorkerDto>.Conflict("account_already_linked", "That account is already linked to another worker.");
        }
        if (before is { } b) _sessions.Invalidate(b);
        if (roleGranted && r.UserId is { } granted) _sessions.Invalidate(granted);
        return AccessResult<WorkerDto>.Ok(await DtoAsync(caller, id, ct));
    }

    public async Task<AccessResult<WorkerDto>> CreateAccountAsync(AccessContext caller, string idOrCode, CreateWorkerAccountRequest r, string? traceId, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Manage, ct) is not { } id) return NotFound<WorkerDto>();
        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var w = (await _db.Workers.FromSqlInterpolated($"SELECT * FROM workers WHERE \"Id\" = {id} FOR UPDATE").ToListAsync(ct)).Single();
            AccessResult<WorkerDto>? refuse =
                w.UserId is not null ? AccessResult<WorkerDto>.Conflict("worker_has_account", "This worker already has a login account.")
                : WorkerStatuses.IsSeparated(w.Status) ? AccessResult<WorkerDto>.Conflict("worker_separated", $"This worker is {w.Status}; a login can't be created.")
                : null;
            if (refuse is not null)
            {
                await tx.RollbackAsync(ct);
                return refuse;
            }

            // The account carries only what sign-in needs: name and phone stay on the worker record.
            var created = await _users.CreateAsync(caller,
                new CreateUserRequest(r.Email, FullName: null, Phone: null, r.TemporaryPassword,
                    new[] { SystemRoles.Worker }, new[] { w.FactoryId }, LineIds: null), traceId, ct);
            if (created.Outcome != AccessOutcome.Created)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                return new AccessResult<WorkerDto>(created.Outcome, default, created.Code, created.Message);
            }

            w.UserId = created.Value!.Id;
            w.UpdatedAt = DateTimeOffset.UtcNow;
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.account_linked", AccessAudit.EntityWorker, w.Id.ToString(),
                "(none)", w.UserId.ToString(), $"login account created for worker {w.WorkerCode} with the Worker role", traceId, w.FactoryId));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AccessResult<WorkerDto>.Ok(default!);
        });
        _db.ChangeTracker.Clear();
        return result.Outcome == AccessOutcome.Ok ? AccessResult<WorkerDto>.Created(await DtoAsync(caller, id, ct)) : result;
    }

    // ─── Photo ─────────────────────────────────────────────────────────────

    public async Task<AccessResult<WorkerPhoto>> PhotoAsync(AccessContext caller, string idOrCode, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.View, ct) is not { } id) return NotFound<WorkerPhoto>();
        var photo = await _db.Workers.AsNoTracking().Where(w => w.Id == id).Select(w => new { w.PhotoKey, w.PhotoContentType }).FirstAsync(ct);
        if (photo.PhotoKey is null || photo.PhotoContentType is null)
            return AccessResult<WorkerPhoto>.NotFound("photo_not_found", "This worker has no photo.");
        var stream = await _photos.OpenReadAsync(photo.PhotoKey, ct);
        return stream is null
            ? AccessResult<WorkerPhoto>.NotFound("photo_not_found", "The photo file is missing.")
            : AccessResult<WorkerPhoto>.Ok(new WorkerPhoto(stream, photo.PhotoContentType));
    }

    public async Task<AccessResult<WorkerDto>> SetPhotoAsync(AccessContext caller, string idOrCode, byte[] content, string? traceId, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Manage, ct) is not { } id) return NotFound<WorkerDto>();
        // The file is identified by its bytes, never by the client's file name or Content-Type.
        if (!QcImageInspector.TryInspect(content, _photos.MaxBytes, out var info, out var error))
            return AccessResult<WorkerDto>.Invalid(error!, error == QcImageInspector.ErrTooLarge
                ? $"The photo is larger than {_photos.MaxBytes / (1024 * 1024.0):0.#} MB."
                : QcImageInspector.Describe(error!));

        var w = await _db.Workers.FirstAsync(x => x.Id == id, ct);
        var old = w.PhotoKey;
        var key = await _photos.SaveAsync(content, info!.Extension, ct);
        w.PhotoKey = key;
        w.PhotoContentType = info.ContentType;
        w.UpdatedAt = DateTimeOffset.UtcNow;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.photo_updated", AccessAudit.EntityWorker, w.Id.ToString(),
            null, null, $"photo of worker {w.WorkerCode} {(old is null ? "added" : "replaced")}", traceId, w.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch
        {
            await _photos.DeleteAsync(key, CancellationToken.None);
            throw;
        }
        if (old is not null) await _photos.DeleteAsync(old, CancellationToken.None);
        return AccessResult<WorkerDto>.Ok(await DtoAsync(caller, id, ct));
    }

    public async Task<AccessResult<WorkerDto>> RemovePhotoAsync(AccessContext caller, string idOrCode, string? traceId, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Manage, ct) is not { } id) return NotFound<WorkerDto>();
        var w = await _db.Workers.FirstAsync(x => x.Id == id, ct);
        if (w.PhotoKey is { } old)
        {
            w.PhotoKey = null;
            w.PhotoContentType = null;
            w.UpdatedAt = DateTimeOffset.UtcNow;
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.photo_removed", AccessAudit.EntityWorker, w.Id.ToString(),
                null, null, $"photo of worker {w.WorkerCode} removed", traceId, w.FactoryId));
            await _db.SaveChangesAsync(ct);
            await _photos.DeleteAsync(old, CancellationToken.None);
        }
        return AccessResult<WorkerDto>.Ok(await DtoAsync(caller, id, ct));
    }

    // ─── Cards ─────────────────────────────────────────────────────────────

    public async Task<AccessResult<IReadOnlyList<WorkerCardDto>>> CardsAsync(AccessContext caller, string idOrCode, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Manage, ct) is not { } id) return NotFound<IReadOnlyList<WorkerCardDto>>();
        return AccessResult<IReadOnlyList<WorkerCardDto>>.Ok(await CardRowsAsync(id, ct));
    }

    private async Task<IReadOnlyList<WorkerCardDto>> CardRowsAsync(Guid workerId, CancellationToken ct) =>
        await _db.WorkerCards.AsNoTracking().Where(c => c.WorkerId == workerId)
            .OrderByDescending(c => c.IsActive).ThenByDescending(c => c.IssuedAt)
            .Select(c => new WorkerCardDto(c.Id, c.WorkerId, c.CardUid, c.CardType, c.Label, c.IsActive, c.IssuedAt, c.DeactivatedAt, c.DeactivationReason))
            .ToListAsync(ct);

    public async Task<AccessResult<WorkerCardDto>> IssueCardAsync(AccessContext caller, string idOrCode, IssueWorkerCardRequest r, string? traceId, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Manage, ct) is not { } id) return NotFound<WorkerCardDto>();
        var w = await _db.Workers.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        if (WorkerStatuses.IsSeparated(w.Status))
            return AccessResult<WorkerCardDto>.Conflict("worker_separated", $"This worker is {w.Status}; a card can't be issued.");
        var uid = WorkerRules.NormaliseCardUid(r.CardUid);
        var holder = await _db.WorkerCards.AsNoTracking().Where(c => c.CardUid == uid && c.IsActive)
            .Select(c => new { c.WorkerId, c.FactoryId }).FirstOrDefaultAsync(ct);
        if (holder is not null)
            return AccessResult<WorkerCardDto>.Conflict("card_in_use", holder.WorkerId == id
                ? "This worker already holds this card."
                : caller.CanAccessFactory(holder.FactoryId)
                    ? "This card is active for another worker. Deactivate it there first."
                    : "This card is active elsewhere and can't be issued here.");

        var now = DateTimeOffset.UtcNow;
        var card = new WorkerCard
        {
            WorkerId = id, FactoryId = w.FactoryId, CardUid = uid, CardType = CardTypes.Parse(r.CardType) ?? CardTypes.Rfid,
            Label = Clean(r.Label), IssuedAt = now, IssuedByUserId = caller.UserId,
        };
        _db.WorkerCards.Add(card);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.card_issued", AccessAudit.EntityWorkerCard, card.Id.ToString(),
            null, "active", $"{card.CardType} card issued to worker {w.WorkerCode}", traceId, w.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<WorkerCardDto>.Conflict("card_in_use", "This card was just issued to someone else.");
        }
        return AccessResult<WorkerCardDto>.Created(new WorkerCardDto(card.Id, id, uid, card.CardType, card.Label, true, now, null, null));
    }

    public async Task<AccessResult<WorkerCardDto>> DeactivateCardAsync(AccessContext caller, string idOrCode, Guid cardId, DeactivateWorkerCardRequest r, string? traceId, CancellationToken ct)
    {
        if (await ResolveAsync(caller, idOrCode, Reach.Manage, ct) is not { } id) return NotFound<WorkerCardDto>();
        var card = await _db.WorkerCards.FirstOrDefaultAsync(c => c.Id == cardId && c.WorkerId == id, ct);
        if (card is null) return AccessResult<WorkerCardDto>.NotFound("card_not_found", "Card not found.");
        var reason = CardDeactivationReasons.Parse(r.Reason);
        if (reason is null)
            return AccessResult<WorkerCardDto>.Invalid("unknown_reason", $"Reason must be one of: {string.Join(", ", CardDeactivationReasons.Selectable)}.");
        if (card.IsActive)
        {
            card.IsActive = false;
            card.DeactivatedAt = DateTimeOffset.UtcNow;
            card.DeactivatedByUserId = caller.UserId;
            card.DeactivationReason = reason;
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.card_deactivated", AccessAudit.EntityWorkerCard, card.Id.ToString(),
                "active", "inactive", $"{card.CardType} card deactivated: {reason}", traceId, card.FactoryId));
            await _db.SaveChangesAsync(ct);
        }
        return AccessResult<WorkerCardDto>.Ok(new WorkerCardDto(card.Id, card.WorkerId, card.CardUid, card.CardType, card.Label, card.IsActive,
            card.IssuedAt, card.DeactivatedAt, card.DeactivationReason));
    }

    // ─── Read model ────────────────────────────────────────────────────────

    private async Task<WorkerDto> DtoAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var w = await _db.Workers.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        return (await ToDtosAsync(caller, new[] { w }, ct))[0];
    }

    private async Task<IReadOnlyList<WorkerDto>> ToDtosAsync(AccessContext caller, IReadOnlyList<WorkerProfile> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<WorkerDto>();
        var deptIds = rows.Where(r => r.DepartmentId != null).Select(r => r.DepartmentId!.Value).Distinct().ToList();
        var floorIds = rows.Where(r => r.FloorId != null).Select(r => r.FloorId!.Value).Distinct().ToList();
        var lineIds = rows.Where(r => r.LineId != null).Select(r => r.LineId!).Distinct().ToList();
        var depts = deptIds.Count == 0 ? new Dictionary<Guid, OrgRef>()
            : await _db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => new OrgRef(d.Id, d.Code, d.Name), ct);
        var floors = floorIds.Count == 0 ? new Dictionary<Guid, OrgRef>()
            : await _db.ProductionFloors.AsNoTracking().Where(d => floorIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => new OrgRef(d.Id, d.Code, d.Name), ct);
        var lines = lineIds.Count == 0 ? new Dictionary<string, WorkerLineRef>()
            : await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new WorkerLineRef(l.Id, l.Name), ct);

        return rows.Select(w =>
        {
            var tier = TierOf(caller, w.UserId);
            var employment = tier != TierBasic;
            var full = tier == TierFull;
            return new WorkerDto(
                w.Id, w.WorkerCode, w.FactoryId, w.FullName, w.PhotoKey is not null,
                w.Status, w.Status == WorkerStatuses.Active,
                w.Designation,
                w.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null,
                w.FloorId is { } f ? floors.GetValueOrDefault(f) : null,
                w.LineId is { } l ? lines.GetValueOrDefault(l) : null,
                w.LineId, w.Workstation,
                employment ? w.EmploymentType : null,
                employment ? w.EmployeeCode : null,
                employment ? w.JoinedOn : null,
                employment ? w.Grade : null,
                employment ? w.StatusReason : null,
                employment ? w.StatusChangedAt : null,
                employment ? w.UserId is not null : null,
                employment ? w.CreatedAt : null,
                employment ? w.UpdatedAt : null,
                full ? w.Phone : null,
                full ? w.Email : null,
                full ? w.Gender : null,
                full ? w.DateOfBirth : null,
                full ? w.PresentAddress : null,
                full ? w.EmergencyContactName : null,
                full ? w.EmergencyContactPhone : null,
                full ? w.EmergencyContactRelation : null,
                full || caller.Has(Permissions.UsersView) ? w.UserId : null,
                tier);
        }).ToList();
    }

    private static AccessResult<T> NotFound<T>() => AccessResult<T>.NotFound("worker_not_found", "Worker not found.");
    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
