using System.Globalization;
using System.Text;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Application.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Workers;

// CSV import (validate → preview → register) and CSV export of the directory.
public sealed partial class WorkerService
{
    /// <summary>More rows than this must be exported in parts (by factory, department or status).</summary>
    public const int MaxExportRows = 20_000;

    public async Task<AccessResult<WorkerImportResultDto>> ImportAsync(AccessContext caller, Guid factoryId, string csv, bool dryRun, string? traceId, CancellationToken ct)
    {
        if (caller.Scope < RoleScope.Factory || !caller.CanAccessFactory(factoryId))
            return AccessResult<WorkerImportResultDto>.Forbidden("factory_out_of_scope", "You can only import workers into your own factory.");
        if (!await _db.Factories.AnyAsync(f => f.Id == factoryId && f.IsActive, ct))
            return AccessResult<WorkerImportResultDto>.Invalid("factory_not_found", "Factory not found or deactivated.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(14));
        var parse = WorkerImportParser.Parse(csv, today);
        if (parse.FileError is not null)
            return AccessResult<WorkerImportResultDto>.Invalid("import_file_invalid", parse.FileError);

        // Names in the file are matched to this factory's departments, floors and lines by code or name.
        var departments = await _db.Departments.AsNoTracking().Where(d => d.FactoryId == factoryId)
            .Select(d => new { d.Id, d.Code, d.Name, d.IsActive }).ToListAsync(ct);
        var floors = await _db.ProductionFloors.AsNoTracking().Where(d => d.FactoryId == factoryId)
            .Select(d => new { d.Id, d.Code, d.Name, d.IsActive }).ToListAsync(ct);
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factoryId)
            .Select(l => new { l.Id, l.Name }).ToListAsync(ct);
        var takenCodes = (await _db.Workers.AsNoTracking().Where(w => w.FactoryId == factoryId && w.EmployeeCode != null)
            .Select(w => w.EmployeeCode!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var known = (await _db.Workers.AsNoTracking().Where(w => w.FactoryId == factoryId && w.Phone != null)
                .Select(w => new { w.Phone, w.FullName, w.WorkerCode }).ToListAsync(ct))
            .GroupBy(w => Digits(w.Phone!)).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        var placements = new Dictionary<int, (Guid Department, Guid? Floor, string? Line)>();
        foreach (var row in parse.Rows)
        {
            Guid? departmentId = null, floorId = null;
            string? lineId = null;
            if (row.Department is { } dn)
            {
                var d = departments.FirstOrDefault(x => Same(x.Code, dn)) ?? departments.FirstOrDefault(x => Same(x.Name, dn));
                if (d is null) row.Errors.Add(new ImportIssue("department", $"Department \"{dn}\" doesn't exist in this factory."));
                else if (!d.IsActive) row.Errors.Add(new ImportIssue("department", $"Department \"{d.Name}\" is deactivated."));
                else { departmentId = d.Id; row.Department = d.Name; }
            }
            if (row.Floor is { } fn)
            {
                var f = floors.FirstOrDefault(x => Same(x.Code, fn)) ?? floors.FirstOrDefault(x => Same(x.Name, fn));
                if (f is null) row.Errors.Add(new ImportIssue("floor", $"Production floor \"{fn}\" doesn't exist in this factory."));
                else if (!f.IsActive) row.Errors.Add(new ImportIssue("floor", $"Production floor \"{f.Name}\" is deactivated."));
                else { floorId = f.Id; row.Floor = f.Name; }
            }
            if (row.Line is { } ln)
            {
                var l = lines.FirstOrDefault(x => Same(x.Id, ln)) ?? lines.FirstOrDefault(x => Same(x.Name, ln));
                if (l is null) row.Errors.Add(new ImportIssue("line", $"Production line \"{ln}\" doesn't exist in this factory."));
                else { lineId = l.Id; row.Line = l.Name; }
            }
            if (row.EmployeeCode is { } code && takenCodes.Contains(code))
                row.Errors.Add(new ImportIssue("employeeCode", $"Employee number {code} is already used in this factory."));
            if (row.Phone is { } phone && row.FullName is { } name && known.TryGetValue(Digits(phone), out var samePhone)
                && samePhone.FirstOrDefault(w => Same(w.FullName, name)) is { } twin)
                row.Warnings.Add(new ImportIssue("row", $"{twin.WorkerCode} already has this name and phone number. Importing creates a second worker."));
            if (row.IsValid) placements[row.RowNumber] = (departmentId!.Value, floorId, lineId);
        }

        var valid = parse.Rows.Where(r => r.IsValid).ToList();
        var codes = new Dictionary<int, string>();
        if (!dryRun && valid.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var numbers = await NextWorkerNumbersAsync(valid.Count, ct);
            for (var i = 0; i < valid.Count; i++)
            {
                var row = valid[i];
                var place = placements[row.RowNumber];
                var worker = new WorkerProfile
                {
                    WorkerNumber = numbers[i],
                    WorkerCode = WorkerRules.FormatWorkerCode(numbers[i]),
                    FactoryId = factoryId,
                    EmployeeCode = row.EmployeeCode,
                    FullName = row.FullName!,
                    Gender = row.Gender,
                    DateOfBirth = row.DateOfBirth,
                    Phone = row.Phone,
                    Email = row.Email,
                    PresentAddress = row.PresentAddress,
                    EmergencyContactName = row.EmergencyContactName,
                    EmergencyContactPhone = row.EmergencyContactPhone,
                    EmergencyContactRelation = row.EmergencyContactRelation,
                    JoinedOn = row.JoinedOn,
                    Status = WorkerStatuses.Active,
                    StatusChangedAt = now,
                    EmploymentType = row.EmploymentType,
                    Designation = row.Designation,
                    Grade = row.Grade,
                    DepartmentId = place.Department,
                    FloorId = place.Floor,
                    LineId = place.Line,
                    CreatedByUserId = caller.UserId,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                AddRegistration(caller, worker, "imported from CSV", traceId, now);
                codes[row.RowNumber] = worker.WorkerCode;
            }
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.imported", AccessAudit.EntityFactory, factoryId.ToString(), null, null,
                $"{valid.Count} worker(s) imported from CSV ({codes.Values.First()} … {codes.Values.Last()}); {parse.Rows.Count - valid.Count} row(s) skipped",
                traceId, factoryId));
            try
            {
                // One SaveChanges = one transaction: either every valid row is registered or none is.
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                _db.ChangeTracker.Clear();
                return AccessResult<WorkerImportResultDto>.Conflict("import_conflict",
                    "An employee number in the file was taken while importing. Nothing was imported; run the preview again.");
            }
            _log.LogInformation("User {ActorId} imported {Count} workers into factory {FactoryId}.", caller.UserId, valid.Count, factoryId);
        }

        var rows = parse.Rows.Select(r => new WorkerImportRowDto(
            r.RowNumber, r.IsValid, r.FullName, r.EmployeeCode, r.Phone, r.JoinedOn, r.Department, r.Designation, r.EmploymentType, r.Floor, r.Line,
            r.Errors.Select(e => new WorkerImportIssueDto(e.Field, e.Message)).ToList(),
            r.Warnings.Select(e => new WorkerImportIssueDto(e.Field, e.Message)).ToList(),
            codes.GetValueOrDefault(r.RowNumber))).ToList();
        var result = new WorkerImportResultDto(dryRun, factoryId, rows.Count, valid.Count, rows.Count - valid.Count,
            dryRun ? 0 : valid.Count, parse.RecognisedColumns, parse.IgnoredColumns, rows);
        return dryRun || valid.Count == 0 ? AccessResult<WorkerImportResultDto>.Ok(result) : AccessResult<WorkerImportResultDto>.Created(result);
    }

    private static string Digits(string phone)
    {
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        // Compare the national part, so +880 17… and 017… are the same number.
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    public async Task<AccessResult<WorkerExport>> ExportAsync(AccessContext caller, WorkerListQuery q, string? traceId, CancellationToken ct)
    {
        var full = caller.Has(Permissions.WorkersManage);
        if (!full && !caller.Has(Permissions.WorkersDetailsView))
            return AccessResult<WorkerExport>.Forbidden("permission_denied", "Exporting the worker list needs the View employment details permission.");

        var workers = Sort(caller, Filter(caller, q), q.Sort);
        var total = await workers.CountAsync(ct);
        if (total > MaxExportRows)
            return AccessResult<WorkerExport>.Invalid("export_too_large",
                $"{total} workers match. Narrow the filters (factory, department or status) to export at most {MaxExportRows} at a time.");
        var rows = await workers.ToListAsync(ct);
        var dtos = await ToDtosAsync(caller, rows, ct);
        var factoryIds = rows.Select(r => r.FactoryId).Distinct().ToList();
        var factories = await _db.Factories.AsNoTracking().Where(f => factoryIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Code, ct);

        var header = new List<string>
        {
            "worker_id", "full_name", "status", "designation", "factory", "department", "floor", "line", "workstation",
            "employment_type", "employee_number", "joining_date", "grade", "status_reason", "has_login",
        };
        if (full)
            header.AddRange(new[]
            {
                "phone", "email", "gender", "date_of_birth", "present_address",
                "emergency_contact_name", "emergency_contact_phone", "emergency_contact_relation",
            });

        static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append(Csv.Line(header)).Append("\r\n");
        foreach (var w in dtos)
        {
            var cells = new List<string?>
            {
                w.WorkerCode, w.FullName, w.Status, w.Designation, factories.GetValueOrDefault(w.FactoryId), w.Department?.Name, w.Floor?.Name,
                w.Line?.Name ?? w.LineId, w.Workstation, w.EmploymentType, w.EmployeeCode, Date(w.JoinedOn), w.Grade, w.StatusReason,
                w.HasAccount == true ? "yes" : "no",
            };
            if (full)
                cells.AddRange(new[]
                {
                    w.Phone, w.Email, w.Gender, Date(w.DateOfBirth), w.PresentAddress,
                    w.EmergencyContactName, w.EmergencyContactPhone, w.EmergencyContactRelation,
                });
            sb.Append(Csv.Line(cells)).Append("\r\n");
        }

        // Taking worker data out of the system is recorded, with who and how much.
        Guid? auditFactory = q.FactoryId ?? (factoryIds.Count == 1 ? factoryIds[0] : caller.FactoryIds.Count == 1 ? caller.FactoryIds.First() : null);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "worker.exported", AccessAudit.EntityWorker, "list", null, null,
            $"{dtos.Count} worker(s) exported to CSV ({(full ? "with personal details" : "employment details only")})", traceId, auditFactory));
        await _db.SaveChangesAsync(ct);

        // The byte-order mark makes spreadsheet programs read the file as UTF-8 (Bangla names).
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return AccessResult<WorkerExport>.Ok(new WorkerExport($"workers-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv", bytes, dtos.Count));
    }
}
