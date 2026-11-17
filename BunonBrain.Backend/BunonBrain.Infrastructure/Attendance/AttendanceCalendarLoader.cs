using BunonBrain.Application.Attendance;
using BunonBrain.Application.Documents;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Attendance;

/// <summary>
/// Loads the attendance calendar of some factories for a date range: factory holidays and the
/// approved leave of their workers. Leave is recorded as Leave Record documents (<c>hr.leave</c>);
/// one counts when it is approved (or archived after approval) — drafts, submitted, rejected or
/// reopened records don't. The worker is the record's Worker ID in the document's factory.
/// </summary>
public static class AttendanceCalendarLoader
{
    public static async Task<AttendanceCalendar> LoadAsync(BunonBrainDbContext db, IReadOnlyCollection<Guid> factoryIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (factoryIds.Count == 0 || to < from) return AttendanceCalendar.Empty;
        var ids = factoryIds.ToList();
        var holidays = await db.FactoryHolidays.AsNoTracking().Where(h => ids.Contains(h.FactoryId) && h.Date >= from && h.Date <= to)
            .Select(h => new HolidayDay(h.FactoryId, h.Date, h.Name, h.Kind)).ToListAsync(ct);

        var docs = await (from x in db.FactoryDocuments.AsNoTracking()
                          join v in db.FactoryDocumentVersions.AsNoTracking() on new { Id = x.Id, N = x.CurrentVersion } equals new { Id = v.DocumentId, N = v.Number }
                          where ids.Contains(x.FactoryId) && x.TypeCode == "hr.leave"
                                && (x.Status == DocumentStatuses.Approved || x.Status == DocumentStatuses.Archived)
                                && x.PeriodFrom <= to && x.PeriodTo >= @from
                          select new { x.Id, x.FactoryId, x.Status, x.Reference, v.DataJson }).ToListAsync(ct);
        if (docs.Count == 0) return new AttendanceCalendar(Array.Empty<ApprovedLeave>(), holidays);

        // An archived record counts only if its last decision was an approval.
        var archived = docs.Where(d => d.Status == DocumentStatuses.Archived).Select(d => d.Id).ToList();
        var decisions = archived.Count == 0 ? new Dictionary<Guid, string>()
            : (await db.FactoryDocumentEvents.AsNoTracking()
                .Where(e => archived.Contains(e.DocumentId) && (e.Action == "approved" || e.Action == "filed" || e.Action == "rejected" || e.Action == "reopened_for_revision"))
                .Select(e => new { e.DocumentId, e.Id, e.Action }).ToListAsync(ct))
              .GroupBy(e => e.DocumentId).ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Id).First().Action);

        var parsed = docs
            .Where(d => d.Status == DocumentStatuses.Approved || decisions.GetValueOrDefault(d.Id) is "approved" or "filed")
            .Select(d => (Doc: d, Data: DocJson.Parse(d.DataJson)))
            .Select(x => (x.Doc, Code: DocJson.Str(x.Data["workerCode"])?.ToUpperInvariant(), From: DocJson.Date(x.Data["fromDate"]), To: DocJson.Date(x.Data["toDate"]),
                Type: DocJson.Str(x.Data["leaveType"]) ?? "LEAVE"))
            .Where(x => x.Code is not null && x.From is not null && x.To is not null && x.To >= x.From)
            .ToList();
        var codes = parsed.Select(x => x.Code!).Distinct().ToList();
        var workers = await db.Workers.AsNoTracking().Where(w => ids.Contains(w.FactoryId) && codes.Contains(w.WorkerCode.ToUpper()))
            .Select(w => new { w.Id, w.FactoryId, Code = w.WorkerCode.ToUpper() }).ToListAsync(ct);
        var leave = new List<ApprovedLeave>();
        foreach (var x in parsed)
            if (workers.FirstOrDefault(w => w.FactoryId == x.Doc.FactoryId && w.Code == x.Code) is { } w)
                leave.Add(new ApprovedLeave(w.Id, x.From!.Value, x.To!.Value, x.Type, x.Doc.Reference));
        return new AttendanceCalendar(leave, holidays);
    }
}
