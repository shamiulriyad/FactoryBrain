using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Identity;
using BunonBrain.Application.Identity;
using BunonBrain.Application.Monitoring;
using BunonBrain.Application.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Devices;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BunonBrain.Infrastructure.Identity;

/// <summary>
/// The RFID read pipeline, the same for physical readers (via their gateway
/// or an integration, source Device / Api) and the development simulator
/// (source Simulator, simulated readers only).
/// <para>
/// Validation: the reader is a registered, active RfidReader device of the
/// factory, real and simulated never mix, the time is plausible (at most
/// 2 minutes ahead, 7 days back), the idempotency key is new (a retry returns
/// the first result). The card: a registered, active card of an active worker
/// in this factory, else the read is stored REJECTED (card_unknown,
/// card_inactive, worker_not_active) and never evaluated. The raw UID is
/// never stored or returned: only the card id, a hash and its last four
/// characters.
/// </para>
/// An accepted read is evaluated at once (PENDING when it must wait for the
/// time window to see the camera tracks).
/// </summary>
public sealed class RfidEventProcessor : IRfidEventProcessor
{
    private readonly BunonBrainDbContext _db;
    private readonly IIdentityAssociationService _identity;
    private readonly IdentityOptions _o;

    public RfidEventProcessor(BunonBrainDbContext db, IIdentityAssociationService identity, IOptions<IdentityOptions> options)
    {
        _db = db; _identity = identity; _o = options.Value;
    }

    private static AccessResult<RfidReadResult> Invalid(string code, string message) => AccessResult<RfidReadResult>.Invalid(code, message);

    public async Task<AccessResult<RfidReadResult>> IngestAsync(AccessContext caller, RfidReadRequest r, string source, CancellationToken ct)
    {
        // ── Reader ──
        var devices = DeviceQueries.InScope(_db, caller).Where(d => d.FactoryId == r.FactoryId);
        Device? reader = r.DeviceId is { } id ? await devices.FirstOrDefaultAsync(d => d.Id == id, ct)
            : string.IsNullOrWhiteSpace(r.DeviceCode) ? null : await devices.FirstOrDefaultAsync(d => d.Code == DeviceRules.NormaliseCode(r.DeviceCode), ct);
        if (reader is null) return AccessResult<RfidReadResult>.NotFound("reader_not_found", "No reader with this id or code in the factory.");
        if (!DeviceQueries.CanConfigure(caller, reader.FactoryId))
            return AccessResult<RfidReadResult>.Forbidden("factory_scope_required", "Only factory-level device administrators send reader events.");
        if (reader.Category != DeviceCategories.RfidReader)
            return Invalid("device_not_reader", $"{reader.Code} is a {reader.Category}, not an RFID reader.");
        if (source == RfidReadSources.Simulator && !reader.IsSimulated)
            return AccessResult<RfidReadResult>.Conflict("device_not_simulated", $"{reader.Code} is a real reader: the simulator only uses simulated readers.");
        if (source != RfidReadSources.Simulator && reader.IsSimulated)
            return AccessResult<RfidReadResult>.Conflict("device_is_simulated", $"{reader.Code} is a simulated reader: it only accepts simulator reads.");
        if (reader.Status != DeviceStatuses.Active)
            return AccessResult<RfidReadResult>.Conflict("reader_not_active", $"{reader.Code} is {reader.Status.ToLowerInvariant()}; its reads are not accepted.");

        // ── Time and idempotency ──
        var now = DateTimeOffset.UtcNow;
        var at = r.OccurredAt?.ToUniversalTime() ?? now;
        if (at > now + MonitoringRules.MaxClockSkew) return Invalid("occurred_in_future", "The read is dated in the future. Check the reader's clock (UTC).");
        if (at < now - MonitoringRules.MaxBackdate) return Invalid("occurred_too_old", "The read is more than 7 days old and was not recorded.");
        var key = r.IdempotencyKey?.Trim();
        if (!MonitoringRules.IsIdempotencyKey(key))
            return Invalid("idempotency_key_required", "idempotencyKey: 1–128 letters, digits, '.', '_', ':', '/', '-'. Send the same key when retrying.");
        var storedKey = $"{source.ToLowerInvariant()}/{key}";
        if (await _db.RfidReadEvents.AsNoTracking().FirstOrDefaultAsync(x => x.FactoryId == reader.FactoryId && x.IdempotencyKey == storedKey, ct) is { } dup)
            return await DuplicateAsync(dup, ct);

        // ── Card ──
        var uid = WorkerRules.NormaliseCardUid(r.CardUid ?? "");
        if (!WorkerRules.IsCardUid(uid)) return Invalid("card_uid_invalid", "cardUid: 4–64 letters and digits (separators ':' '-' and spaces are ignored).");
        var cards = await _db.WorkerCards.AsNoTracking().Where(c => c.FactoryId == reader.FactoryId && c.CardUid == uid)
            .OrderByDescending(c => c.IsActive).ThenByDescending(c => c.IssuedAt).ToListAsync(ct);
        var active = cards.FirstOrDefault(c => c.IsActive);
        string outcome = RfidReadOutcomes.Accepted;
        string? code = null;
        Guid? workerId = null;
        if (active is null)
        {
            outcome = RfidReadOutcomes.Rejected;
            code = cards.Count > 0 ? "card_inactive" : "card_unknown";
        }
        else
        {
            var status = await _db.Workers.AsNoTracking().Where(w => w.Id == active.WorkerId && w.FactoryId == reader.FactoryId).Select(w => w.Status).FirstOrDefaultAsync(ct);
            if (status is null) { outcome = RfidReadOutcomes.Rejected; code = "card_unassigned"; }
            else if (status != WorkerStatuses.Active) { outcome = RfidReadOutcomes.Rejected; code = "worker_not_active"; }
            else workerId = active.WorkerId;
        }

        // ── The mapping in force at the time of the read ──
        var mappingId = await _db.RfidReaderMappings.AsNoTracking()
            .Where(m => m.DeviceId == reader.Id && m.EffectiveFrom <= at && (m.EffectiveTo == null || m.EffectiveTo > at))
            .OrderByDescending(m => m.EffectiveFrom).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);

        var read = new RfidReadEvent
        {
            FactoryId = reader.FactoryId, DeviceId = reader.Id, MappingId = mappingId, CardId = (active ?? cards.FirstOrDefault())?.Id, WorkerId = workerId,
            CardHash = CardIdentifiers.Hash(uid), CardSuffix = CardIdentifiers.Suffix(uid), Source = source, IsSimulated = source == RfidReadSources.Simulator,
            OccurredAt = at, ReceivedAt = now, IdempotencyKey = storedKey, Outcome = outcome, OutcomeCode = code,
            Evaluation = outcome == RfidReadOutcomes.Accepted ? AssociationStatuses.Pending : null,
            RecordedByUserId = caller.IsService ? null : caller.UserId, RecordedByEmail = caller.Email,
        };
        _db.RfidReadEvents.Add(read);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505", ConstraintName: RfidReadEventConfiguration.IdempotencyIndex })
        {
            _db.ChangeTracker.Clear();
            return await DuplicateAsync(await _db.RfidReadEvents.AsNoTracking().FirstAsync(x => x.FactoryId == reader.FactoryId && x.IdempotencyKey == storedKey, ct), ct);
        }
        _db.ChangeTracker.Clear();

        var associations = outcome == RfidReadOutcomes.Accepted
            ? await _identity.EvaluateReadAsync(read.Id, final: now > at + _o.After, ct)
            : Array.Empty<AssociationDto>();
        var fresh = await _db.RfidReadEvents.AsNoTracking().FirstAsync(x => x.Id == read.Id, ct);
        var dto = (await IdentityQueries.ReadDtosAsync(_db, new[] { fresh }, ct))[0];
        return AccessResult<RfidReadResult>.Created(new RfidReadResult(outcome == RfidReadOutcomes.Accepted ? "Accepted" : "Rejected", dto, associations));
    }

    private async Task<AccessResult<RfidReadResult>> DuplicateAsync(RfidReadEvent existing, CancellationToken ct)
    {
        var dto = (await IdentityQueries.ReadDtosAsync(_db, new[] { existing }, ct))[0];
        return AccessResult<RfidReadResult>.Ok(new RfidReadResult("Duplicate", dto, Array.Empty<AssociationDto>()));
    }
}
