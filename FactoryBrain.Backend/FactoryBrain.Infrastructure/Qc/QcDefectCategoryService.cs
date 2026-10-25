using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Application.Qc;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FactoryBrain.Infrastructure.Qc;

/// <summary>
/// The configurable defect taxonomy. Categories are referenced by findings,
/// so they are deactivated rather than deleted; each change is audited and
/// updates are guarded by the row's <c>xmin</c>.
/// </summary>
public sealed class QcDefectCategoryService : IQcDefectCategoryService
{
    private const string EntityType = "qc_defect_category";

    private readonly FactoryBrainDbContext _db;
    public QcDefectCategoryService(FactoryBrainDbContext db) { _db = db; }

    public async Task<IReadOnlyList<QcCategoryDto>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var q = _db.QcDefectCategories.AsNoTracking();
        if (!includeInactive) q = q.Where(c => c.IsActive);
        var rows = await q.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<QcCategoryWriteResult> CreateAsync(QcCategoryCreateRequest req, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var category = new QcDefectCategory
        {
            Code = req.Code!.Trim(),
            Name = req.Name!.Trim(),
            NameBn = Clean(req.NameBn),
            Description = Clean(req.Description),
            DefaultSeverity = QcWire.TryParseSeverity(req.DefaultSeverity, out var s) ? s : QcDefectSeverity.Major,
            SortOrder = req.SortOrder ?? await NextSortOrderAsync(ct),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.QcDefectCategories.Add(category);
        _db.AuditLog.Add(Audit(actor, traceId, "qc_category.created", category.Code, null, "active", category.Name));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new QcCategoryWriteResult(QcCategoryWriteOutcome.Duplicate, null, new Dictionary<string, string[]>
            {
                ["code"] = new[] { "A category with this code or name already exists." },
            });
        }
        return new QcCategoryWriteResult(QcCategoryWriteOutcome.Ok, ToDto(category));
    }

    public async Task<QcCategoryWriteResult> UpdateAsync(string code, QcCategoryUpdateRequest req, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var category = await _db.QcDefectCategories.FirstOrDefaultAsync(c => c.Code == code, ct);
        if (category is null) return new QcCategoryWriteResult(QcCategoryWriteOutcome.NotFound);
        if (req.ExpectedVersion != category.Version) return new QcCategoryWriteResult(QcCategoryWriteOutcome.VersionConflict);

        if (req.IsActive == false && category.IsActive
            && !await _db.QcDefectCategories.AnyAsync(c => c.IsActive && c.Code != code, ct))
        {
            return new QcCategoryWriteResult(QcCategoryWriteOutcome.Invalid, null, new Dictionary<string, string[]>
            {
                ["isActive"] = new[] { "At least one category must stay active." },
            });
        }

        var from = category.IsActive ? "active" : "inactive";
        if (req.Name is not null) category.Name = req.Name.Trim();
        if (req.NameBn is not null) category.NameBn = Clean(req.NameBn);
        if (req.Description is not null) category.Description = Clean(req.Description);
        if (QcWire.TryParseSeverity(req.DefaultSeverity, out var s)) category.DefaultSeverity = s;
        if (req.IsActive is { } active) category.IsActive = active;
        if (req.SortOrder is { } order) category.SortOrder = order;
        category.UpdatedAt = DateTime.UtcNow;
        _db.AuditLog.Add(Audit(actor, traceId, "qc_category.updated", code, from, category.IsActive ? "active" : "inactive",
            $"{category.Name} · default {QcWire.Severity(category.DefaultSeverity)} · order {category.SortOrder}"));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new QcCategoryWriteResult(QcCategoryWriteOutcome.VersionConflict);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new QcCategoryWriteResult(QcCategoryWriteOutcome.Duplicate, null, new Dictionary<string, string[]>
            {
                ["name"] = new[] { "Another category already has this name." },
            });
        }
        return new QcCategoryWriteResult(QcCategoryWriteOutcome.Ok, ToDto(category));
    }

    private async Task<int> NextSortOrderAsync(CancellationToken ct) =>
        (await _db.QcDefectCategories.MaxAsync(c => (int?)c.SortOrder, ct) ?? 0) + 10;

    private static QcCategoryDto ToDto(QcDefectCategory c) => new(
        c.Code, c.Name, c.NameBn, c.Description, QcWire.Severity(c.DefaultSeverity), c.IsActive, c.SortOrder, c.Version, c.UpdatedAt);

    private static AuditLogEntry Audit(AuditActor actor, string? traceId, string action, string code, string? from, string? to, string? reason) => new()
    {
        OccurredAt = DateTime.UtcNow,
        ActorUserId = actor.UserId,
        ActorEmail = actor.Email,
        ActorRole = actor.Role,
        Action = action,
        EntityType = EntityType,
        EntityId = code,
        FromState = from,
        ToState = to,
        Reason = reason is { Length: > 1000 } ? reason[..1000] : reason,
        TraceId = traceId,
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
