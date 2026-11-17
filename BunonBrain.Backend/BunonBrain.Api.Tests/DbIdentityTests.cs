using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Verified worker identity association against real PostgreSQL (a disposable
/// local container, never a shared database): RFID ingestion, the evidence
/// rules end to end, manual review and its audit, concurrency, expiry,
/// factory isolation, append-only history and real-time filtering.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbIdentityTests
{
    private readonly PostgresStack _stack;
    public DbIdentityTests(PostgresStack stack) { _stack = stack; }
    private DbApiFactory Api => _stack.Local;

    public sealed record Reply(HttpStatusCode Status, JsonElement Body, string Raw)
    {
        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
        public string Id => Body.GetProperty("id").GetString()!;
    }

    private static string? S(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out var next)) return null;
            e = next;
        }
        return e.ValueKind switch { JsonValueKind.Null or JsonValueKind.Undefined => null, JsonValueKind.String => e.GetString(), _ => e.ToString() };
    }

    private async Task<Reply> Send(HttpMethod method, string path, string bearer, object? body = null)
    {
        using var client = Api.RawClient();
        using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : Rag.Json(body) };
        req.Headers.Add(TestClientIpStartupFilter.Header, TestClientIpStartupFilter.NewAddress());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var res = await client.SendAsync(req);
        var raw = Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync());
        var json = raw.Length > 0 && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('[')) ? JsonDocument.Parse(raw).RootElement.Clone() : default;
        return new Reply(res.StatusCode, json, raw);
    }

    private Task<Reply> Get(string path, string bearer) => Send(HttpMethod.Get, path, bearer);
    private Task<Reply> Post(string path, string bearer, object body) => Send(HttpMethod.Post, path, bearer, body);
    private Task<Reply> Put(string path, string bearer, object body) => Send(HttpMethod.Put, path, bearer, body);

    private static void Is(HttpStatusCode expected, Reply r, string? code = null)
    {
        Assert.True(r.Status == expected, $"expected {(int)expected}, got {(int)r.Status}: {r.Raw}");
        if (code is not null) Assert.Equal(code, r.Code);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static string K() => $"id-{Guid.NewGuid():N}";

    public sealed record Person(Guid Id, string Email, string Token);

    private async Task<Person> NewPerson(string role, Guid? factory)
    {
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@identity.test";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        user.Roles.Add(new UserRole { UserId = id, RoleId = role });
        if (factory is not null) user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = factory.Value });
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(id, default);
        return new Person(id, email, scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(snapshot!, out _));
    }

    /// <summary>One factory with a line, a workstation, a camera with a zone on it, workers with cards and readers.</summary>
    public sealed record Rig(Guid Factory, string Line, Guid Workstation, Guid Camera, Guid Zone, Guid RealReader, Guid SimReader, Guid BroadReader,
        Guid WorkerA, string CardA, Guid WorkerB, string CardB, Guid AssignmentA, Person Admin, Person Devices, Person Monitor);

    private async Task<Rig> NewRig()
    {
        var t = Tag().ToUpperInvariant();
        var factory = Guid.NewGuid();
        var dept = Guid.NewGuid();
        var line = $"id-{t.ToLowerInvariant()}";
        var ws = Guid.NewGuid();
        var cam = Guid.NewGuid();
        var zone = Guid.NewGuid();
        var (real, sim, broad) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (wa, wb) = (Guid.NewGuid(), Guid.NewGuid());
        var (ca, cb) = ($"04A{t}", $"04B{t}");
        var asg = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await Rag.WithDb(Api, async db =>
        {
            db.Factories.Add(new Factory { Id = factory, Code = $"I{t[..6]}", Name = $"Identity {t}" });
            await db.SaveChangesAsync();
            db.Departments.Add(new Department { Id = dept, FactoryId = factory, Code = $"D{t[..4]}", Name = $"Sewing {t}" });
            db.ProductionLines.Add(new ProductionLine { Id = line, Name = $"Line {t}", Workstations = 10, FactoryId = factory });
            await db.SaveChangesAsync();
            db.Workstations.Add(new Workstation { Id = ws, FactoryId = factory, DepartmentId = dept, LineId = line, Code = $"T-{t[..3]}", Capacity = 1, Position = 1 });
            db.Cameras.Add(new Camera { Id = cam, FactoryId = factory, Code = $"CAM-{t[..4]}", Name = "Line camera", CameraType = "IpCamera",
                FloorId = await NewFloor(db, factory, t), DepartmentId = dept, LineId = line, Status = "Active", CreatedByEmail = "test" });
            await db.SaveChangesAsync();
            db.CameraZones.Add(new CameraZone { Id = zone, CameraId = cam, FactoryId = factory, Name = $"WS {t[..3]}", ZoneType = "Workstation", WorkstationId = ws,
                LineId = line, Polygon = "[{\"x\":0.1,\"y\":0.1},{\"x\":0.5,\"y\":0.1},{\"x\":0.5,\"y\":0.9}]", CreatedByEmail = "test", UpdatedByEmail = "test" });
            foreach (var (id, code, simulated) in new[] { (real, $"RF-{t[..4]}-R", false), (sim, $"RF-{t[..4]}-S", true), (broad, $"RF-{t[..4]}-B", false) })
                db.Devices.Add(new Device { Id = id, FactoryId = factory, Code = code, Name = code, Category = "RfidReader", IsSimulated = simulated, CreatedByEmail = "test" });
            db.Workers.Add(new WorkerProfile { Id = wa, WorkerNumber = await TestWorkers.NextNumberAsync(db), FactoryId = factory, WorkerCode = $"WA-{t}", FullName = "Worker A", LineId = line });
            db.Workers.Add(new WorkerProfile { Id = wb, WorkerNumber = await TestWorkers.NextNumberAsync(db), FactoryId = factory, WorkerCode = $"WB-{t}", FullName = "Worker B", LineId = line });
            await db.SaveChangesAsync();
            db.WorkerCards.Add(new WorkerCard { WorkerId = wa, FactoryId = factory, CardUid = ca, Label = "A-badge" });
            db.WorkerCards.Add(new WorkerCard { WorkerId = wb, FactoryId = factory, CardUid = cb });
            db.Assignments.Add(new WorkstationAssignment { Id = asg, WorkerId = wa, FactoryId = factory, DepartmentId = dept, LineId = line, WorkstationId = ws,
                StartAt = now.AddHours(-1), AssignedByEmail = "test" });
            await db.SaveChangesAsync();
            return 0;
        });
        var admin = await NewPerson(R.FactoryAdmin, factory);
        var rig = new Rig(factory, line, ws, cam, zone, real, sim, broad, wa, ca, wb, cb, asg, admin, await NewPerson(R.DeviceAdmin, factory),
            await NewPerson(R.MonitoringOperator, factory));
        Is(HttpStatusCode.OK, await Put($"/api/identity/readers/{real}/mapping", rig.Devices.Token, new { precision = "WORKSTATION", workstationId = ws }));
        Is(HttpStatusCode.OK, await Put($"/api/identity/readers/{sim}/mapping", rig.Devices.Token, new { precision = "ZONE", cameraZoneId = zone }));
        Is(HttpStatusCode.OK, await Put($"/api/identity/readers/{broad}/mapping", rig.Devices.Token, new { precision = "LINE", lineId = line }));
        return rig;
    }

    private static async Task<Guid> NewFloor(BunonBrainDbContext db, Guid factory, string t)
    {
        var f = new ProductionFloor { Id = Guid.NewGuid(), FactoryId = factory, Code = $"F{t[..4]}", Name = $"Floor {t}" };
        db.ProductionFloors.Add(f);
        await db.SaveChangesAsync();
        return f.Id;
    }

    /// <summary>A REAL track observation, as the computer-vision service sends it (here through the ingestion API).</summary>
    private Task<Reply> Track(Rig r, string type, string track, bool zone = true, int secondsAgo = 5) => Post("/api/monitoring/events", r.Admin.Token, new
    {
        eventType = type, factoryId = r.Factory, idempotencyKey = K(), cameraId = r.Camera, zoneId = zone ? r.Zone : (Guid?)null, trackId = track,
        occurredAt = DateTimeOffset.UtcNow.AddSeconds(-secondsAgo), confidence = 0.9, modelName = "yolo11n",
    });

    private Task<Reply> Read(Rig r, string card, Guid? reader = null, string? key = null) => Post("/api/identity/rfid-reads", r.Devices.Token, new
    {
        factoryId = r.Factory, deviceId = reader ?? r.RealReader, cardUid = card, idempotencyKey = key ?? K(), occurredAt = DateTimeOffset.UtcNow,
    });

    // ─── End to end ───────────────────────────────────────────────────────

    [DockerFact]
    public async Task A_card_read_at_the_assigned_workstation_with_one_track_verifies_the_track_and_never_exposes_the_card()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Track(r, "ZONE_ENTERED", "s1-t12"));
        var read = await Read(r, r.CardA);
        Is(HttpStatusCode.Created, read);
        Assert.Equal("Accepted", S(read.Body, "outcome"));
        Assert.Equal("VERIFIED", S(read.Body, "read", "evaluation"));
        Assert.DoesNotContain(r.CardA, read.Raw);
        Assert.Equal("…" + r.CardA[^4..], S(read.Body, "read", "card"));
        var a = Assert.Single(read.Body.GetProperty("associations").EnumerateArray());
        Assert.Equal(("VERIFIED", "s1-t12", $"WA-{r.CardA[3..]}"), (S(a, "status"), S(a, "trackId"), S(a, "worker", "code")));

        var tracks = await Get($"/api/identity/tracks?cameraId={r.Camera}", r.Monitor.Token);
        var t = Assert.Single(tracks.Body.EnumerateArray());
        Assert.Equal(("VERIFIED", $"WA-{r.CardA[3..]}"), (S(t, "identity"), S(t, "worker", "code")));
        var detail = await Get($"/api/identity/associations/{S(a, "id")}", r.Monitor.Token);
        Is(HttpStatusCode.OK, detail);
        var kinds = detail.Body.GetProperty("evidence").EnumerateArray().Select(e => S(e, "kind")).ToHashSet();
        Assert.Superset(new HashSet<string?> { "RFID_READ", "CARD_OWNERSHIP", "READER_MAPPING", "ASSIGNMENT", "CAMERA_ZONE", "TRACK_OBSERVATION" }, kinds);
        var overview = await Get($"/api/identity/overview?factoryId={r.Factory}", r.Monitor.Token);
        Assert.Equal("1", S(overview.Body, "counts", "verifiedNow"));
        foreach (var body in new[] { tracks.Raw, detail.Raw, overview.Raw, (await Get($"/api/identity/reads?factoryId={r.Factory}", r.Monitor.Token)).Raw })
            Assert.DoesNotContain(r.CardA, body);
    }

    [DockerFact]
    public async Task Invalid_inactive_unknown_and_duplicate_reads_are_refused_or_recorded_without_any_association()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Track(r, "ZONE_ENTERED", "s1-t1"));
        var unknown = await Read(r, "DEADBEEF01");
        Assert.Equal(("Rejected", "card_unknown"), (S(unknown.Body, "outcome"), S(unknown.Body, "read", "outcomeCode")));
        Assert.Empty(unknown.Body.GetProperty("associations").EnumerateArray());
        await Rag.WithDb(Api, async db =>
        {
            var c = await db.WorkerCards.FirstAsync(x => x.CardUid == r.CardB);
            c.IsActive = false;
            c.DeactivatedAt = DateTimeOffset.UtcNow;
            c.DeactivationReason = "Lost";
            return await db.SaveChangesAsync();
        });
        Assert.Equal("card_inactive", S((await Read(r, r.CardB)).Body, "read", "outcomeCode"));
        Is(HttpStatusCode.BadRequest, await Read(r, "x"), "card_uid_invalid");
        Is(HttpStatusCode.Conflict, await Read(r, r.CardA, r.SimReader), "device_is_simulated");
        var key = K();
        Is(HttpStatusCode.Created, await Read(r, r.CardA, key: key));
        var dup = await Read(r, r.CardA, key: key);
        Is(HttpStatusCode.OK, dup);
        Assert.Equal("Duplicate", S(dup.Body, "outcome"));
        // A reader in maintenance is not trusted.
        await Rag.WithDb(Api, async db => { (await db.Devices.FirstAsync(d => d.Id == r.RealReader)).Status = "Maintenance"; return await db.SaveChangesAsync(); });
        Is(HttpStatusCode.Conflict, await Read(r, r.CardA), "reader_not_active");
        // Other factories' administrators can't send reads for this factory.
        var otherAdmin = await NewPerson(R.DeviceAdmin, (await NewRig()).Factory);
        Is(HttpStatusCode.NotFound, await Post("/api/identity/rfid-reads", otherAdmin.Token, new { factoryId = r.Factory, deviceId = r.RealReader, cardUid = r.CardA, idempotencyKey = K() }));
    }

    [DockerFact]
    public async Task Broad_readers_assignment_mismatches_and_missing_tracks_stay_unverified()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Track(r, "ZONE_ENTERED", "s1-t3"));
        Assert.Equal(("UNVERIFIED", "MAPPING_NOT_WORKSTATION"),
            (S((await Read(r, r.CardA, r.BroadReader)).Body, "read", "evaluation"), S((await Read(r, r.CardA, r.BroadReader)).Body, "read", "evaluationCode")));
        var b = await Read(r, r.CardB);                     // worker B has no workstation assignment at all
        Assert.Equal(("UNVERIFIED", "NO_ACTIVE_ASSIGNMENT"), (S(b.Body, "read", "evaluation"), S(b.Body, "read", "evaluationCode")));
        // Worker B works at another workstation of the line: a read here contradicts that assignment.
        await Rag.WithDb(Api, async db =>
        {
            var here = await db.Workstations.AsNoTracking().FirstAsync(w => w.Id == r.Workstation);
            var other = new Workstation { Id = Guid.NewGuid(), FactoryId = r.Factory, DepartmentId = here.DepartmentId, LineId = here.LineId, Code = here.Code + "B", Capacity = 1, Position = here.Position + 1 };
            db.Workstations.Add(other);
            await db.SaveChangesAsync();
            db.Assignments.Add(new WorkstationAssignment { WorkerId = r.WorkerB, FactoryId = r.Factory, DepartmentId = here.DepartmentId, LineId = here.LineId, WorkstationId = other.Id,
                StartAt = DateTimeOffset.UtcNow.AddHours(-1), AssignedByEmail = "test" });
            return await db.SaveChangesAsync();
        });
        b = await Read(r, r.CardB);
        Assert.Equal(("UNVERIFIED", "ASSIGNMENT_MISMATCH"), (S(b.Body, "read", "evaluation"), S(b.Body, "read", "evaluationCode")));
        var r2 = await NewRig();                            // no track in the zone yet
        Assert.Equal("PENDING", S((await Read(r2, r2.CardA)).Body, "read", "evaluation"));
    }

    [DockerFact]
    public async Task Several_tracks_are_ambiguous_and_a_reviewer_confirms_one_with_an_audited_reason()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Track(r, "ZONE_ENTERED", "s1-t5"));
        Is(HttpStatusCode.Created, await Track(r, "ZONE_ENTERED", "s1-t6"));
        var read = await Read(r, r.CardA);
        Assert.Equal("AMBIGUOUS", S(read.Body, "read", "evaluation"));
        var cases = read.Body.GetProperty("associations").EnumerateArray().ToList();
        Assert.Equal(2, cases.Count);
        Assert.All(cases, c => { Assert.Equal("AMBIGUOUS", S(c, "status")); Assert.Null(S(c, "worker")); });
        var tracks = (await Get($"/api/identity/tracks?cameraId={r.Camera}", r.Monitor.Token)).Body.EnumerateArray().ToList();
        Assert.All(tracks, t => Assert.Equal("AMBIGUOUS", S(t, "identity")));
        var first = S(cases[0], "id")!;
        var second = S(cases[1], "id")!;

        Is(HttpStatusCode.BadRequest, await Post($"/api/identity/associations/{first}/review", r.Monitor.Token,
            new { decision = "CONFIRM", workerId = r.WorkerA, reason = "" }), "reason_required");
        Is(HttpStatusCode.BadRequest, await Post($"/api/identity/associations/{first}/review", r.Monitor.Token,
            new { decision = "CONFIRM", workerId = r.WorkerB, reason = "Checked on the floor" }), "worker_not_candidate");
        var ok = await Post($"/api/identity/associations/{first}/review", r.Monitor.Token,
            new { decision = "CONFIRM", workerId = r.WorkerA, reason = "Supervisor confirmed on the floor: A sits at the table" });
        Is(HttpStatusCode.OK, ok);
        Assert.Equal(("VERIFIED", "MANUAL_REVIEW"), (S(ok.Body, "association", "status"), S(ok.Body, "association", "verificationMethod")));
        var manual = ok.Body.GetProperty("history").EnumerateArray().Last();
        Assert.Equal(("CONFIRMED", "AMBIGUOUS", "VERIFIED", "True"), (S(manual, "action"), S(manual, "fromStatus"), S(manual, "toStatus"), S(manual, "isManual")));
        Assert.Equal(r.Monitor.Email, S(manual, "actorEmail"));
        // The same worker can't also be confirmed on the other track of this camera.
        Is(HttpStatusCode.Conflict, await Post($"/api/identity/associations/{second}/review", r.Monitor.Token,
            new { decision = "CONFIRM", workerId = r.WorkerA, reason = "Second guess on the other track" }), "identity_conflict");
        Is(HttpStatusCode.OK, await Post($"/api/identity/associations/{second}/review", r.Monitor.Token,
            new { decision = "REJECT", reason = "That is a visitor, not A" }));
        var audit = await AiSettingsTestKit.AuditText(Api, "identity_association.");
        Assert.Contains(audit, a => a.Contains("Supervisor confirmed on the floor"));
        // Reviewing needs the permission: a device administrator can't.
        Is(HttpStatusCode.Forbidden, await Post($"/api/identity/associations/{second}/review", r.Devices.Token, new { decision = "REVOKE", reason = "test reason" }));
    }

    [DockerFact]
    public async Task Two_simultaneous_confirmations_can_never_verify_one_track_as_two_workers()
    {
        var r = await NewRig();
        await Rag.WithDb(Api, async db =>
        {
            var ws = await db.Workstations.FirstAsync(w => w.Id == r.Workstation);
            ws.Capacity = 2;
            db.Assignments.Add(new WorkstationAssignment { WorkerId = r.WorkerB, FactoryId = r.Factory, DepartmentId = ws.DepartmentId, LineId = r.Line,
                WorkstationId = r.Workstation, StartAt = DateTimeOffset.UtcNow.AddHours(-1), AssignedByEmail = "test" });
            return await db.SaveChangesAsync();
        });
        Is(HttpStatusCode.Created, await Track(r, "ZONE_ENTERED", "s1-t9"));
        var a = await Read(r, r.CardA);                      // shared workstation → ambiguous for A
        var b = await Read(r, r.CardB);                      // and for B (two workers at one reader too)
        var ca = S(a.Body.GetProperty("associations").EnumerateArray().First(), "id")!;
        var cb = S(b.Body.GetProperty("associations").EnumerateArray().First(), "id")!;
        var results = await Task.WhenAll(
            Post($"/api/identity/associations/{ca}/review", r.Monitor.Token, new { decision = "CONFIRM", workerId = r.WorkerA, reason = "Race test: A" }),
            Post($"/api/identity/associations/{cb}/review", r.Monitor.Token, new { decision = "CONFIRM", workerId = r.WorkerB, reason = "Race test: B" }));
        Assert.Equal(1, results.Count(x => x.Status == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(x => x.Status == HttpStatusCode.Conflict));
        var verified = await Rag.WithDb(Api, db => db.WorkerTrackAssociations.CountAsync(x => x.CameraId == r.Camera && x.TrackId == "s1-t9" && x.Status == "VERIFIED"));
        Assert.Equal(1, verified);
    }

    [DockerFact]
    public async Task A_lost_track_or_a_transfer_ends_the_verified_identity()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Track(r, "ZONE_ENTERED", "s1-t20"));
        Assert.Equal("VERIFIED", S((await Read(r, r.CardA)).Body, "read", "evaluation"));
        Is(HttpStatusCode.Created, await Track(r, "PERSON_LOST", "s1-t20", zone: false, secondsAgo: 0));
        var list = await Get($"/api/identity/associations?cameraId={r.Camera}", r.Monitor.Token);
        var a = Assert.Single(list.Body.GetProperty("items").EnumerateArray());
        Assert.Equal(("EXPIRED", "TRACK_LOST", "False"), (S(a, "status"), S(a, "reasonCode"), S(a, "isCurrent")));
        Assert.Null(S(a, "worker"));                         // never shown once it isn't current

        var r2 = await NewRig();
        Is(HttpStatusCode.Created, await Track(r2, "ZONE_ENTERED", "s2-t1"));
        Assert.Equal("VERIFIED", S((await Read(r2, r2.CardA)).Body, "read", "evaluation"));
        await Rag.WithDb(Api, async db =>
        {
            var asg = await db.Assignments.FirstAsync(x => x.Id == r2.AssignmentA);
            asg.EndAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            asg.Status = "Transferred";
            return await db.SaveChangesAsync();
        });
        var t = Assert.Single((await Get($"/api/identity/tracks?cameraId={r2.Camera}", r2.Monitor.Token)).Body.EnumerateArray());
        Assert.Equal("UNVERIFIED", S(t, "identity"));         // the outdated assignment is not used
    }

    [DockerFact]
    public async Task Identity_is_isolated_per_factory_history_is_append_only_and_pushes_reach_only_the_factory()
    {
        var r = await NewRig();
        var other = await NewRig();
        var mine = new ConcurrentQueue<JsonElement>();
        var theirs = new ConcurrentQueue<JsonElement>();
        await using var hubMine = Hub(r.Monitor.Token, mine);
        await using var hubTheirs = Hub(other.Monitor.Token, theirs);
        await hubMine.StartAsync();
        await hubTheirs.StartAsync();
        await hubMine.InvokeAsync<JsonElement>("Subscribe", (Guid?)null);
        await hubTheirs.InvokeAsync<JsonElement>("Subscribe", (Guid?)null);

        Is(HttpStatusCode.Created, await Track(r, "ZONE_ENTERED", "s1-t30"));
        var read = await Read(r, r.CardA);
        var id = S(read.Body.GetProperty("associations").EnumerateArray().First(), "id")!;
        for (var i = 0; i < 50 && mine.IsEmpty; i++) await Task.Delay(100);
        Assert.Contains(mine, m => S(m, "association", "id") == id);
        await Task.Delay(300);
        Assert.DoesNotContain(theirs, m => S(m, "association", "id") == id);

        Is(HttpStatusCode.NotFound, await Get($"/api/identity/associations/{id}", other.Monitor.Token));
        Assert.Empty((await Get($"/api/identity/tracks?cameraId={r.Camera}", other.Monitor.Token)).Body.EnumerateArray());
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Rag.WithDb(Api, db =>
            db.Database.ExecuteSqlRawAsync("UPDATE identity_association_history SET \"Reason\" = 'rewritten'")));
        Assert.Contains("append-only", ex.ToString());
    }

    [DockerFact]
    public async Task The_simulator_uses_the_same_pipeline_marks_everything_simulated_and_never_mixes_with_real_tracks()
    {
        var r = await NewRig();
        Is(HttpStatusCode.Created, await Track(r, "ZONE_ENTERED", "s1-real"));           // a REAL track in the zone
        var simOnly = await Post("/api/identity/simulator", r.Admin.Token, new { factoryId = r.Factory, deviceId = r.SimReader, scenario = "READ", workerId = r.WorkerA });
        Is(HttpStatusCode.OK, simOnly);
        var read = simOnly.Body.GetProperty("reads").EnumerateArray().Single().GetProperty("read");
        Assert.Equal(("True", "Simulator"), (S(read, "isSimulated"), S(read, "source")));
        Assert.NotEqual("VERIFIED", S(read, "evaluation"));                              // a simulated read never verifies a real track
        // Two people tap the same reader: worker B shares A's workstation (capacity 2), so B's read
        // reaches the "other workers at this reader" check instead of stopping at NO_ACTIVE_ASSIGNMENT.
        await Rag.WithDb(Api, async db =>
        {
            var ws = await db.Workstations.FirstAsync(w => w.Id == r.Workstation);
            ws.Capacity = 2;
            await db.SaveChangesAsync();
            db.Assignments.Add(new WorkstationAssignment { WorkerId = r.WorkerB, FactoryId = r.Factory, DepartmentId = ws.DepartmentId, LineId = ws.LineId, WorkstationId = ws.Id,
                StartAt = DateTimeOffset.UtcNow.AddHours(-1), AssignedByEmail = "test" });
            return await db.SaveChangesAsync();
        });
        var withTrack = await Post("/api/identity/simulator", r.Admin.Token,
            new { factoryId = r.Factory, deviceId = r.SimReader, scenario = "MULTIPLE", workerId = r.WorkerA, secondWorkerId = r.WorkerB, withSimulatedTrack = true });
        Is(HttpStatusCode.OK, withTrack);
        Assert.All(withTrack.Body.GetProperty("reads").EnumerateArray(), x => Assert.Equal("True", S(x, "read", "isSimulated")));
        Assert.Contains(withTrack.Body.GetProperty("reads").EnumerateArray(), x => S(x, "read", "evaluation") == "AMBIGUOUS");
        Is(HttpStatusCode.Conflict, await Post("/api/identity/simulator", r.Admin.Token, new { factoryId = r.Factory, deviceId = r.RealReader, scenario = "READ", workerId = r.WorkerA }),
            "device_not_simulated");
    }

    private HubConnection Hub(string token, ConcurrentQueue<JsonElement> inbox)
    {
        var server = Api.Server;
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/monitoring"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Headers[TestClientIpStartupFilter.Header] = TestClientIpStartupFilter.NewAddress();
            })
            .Build();
        conn.On<JsonElement>("identityAssociation", m => inbox.Enqueue(m));
        return conn;
    }
}
