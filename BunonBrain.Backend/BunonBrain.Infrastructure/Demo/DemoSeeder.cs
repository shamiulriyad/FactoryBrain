using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Demo;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BunonBrain.Infrastructure.Demo;

/// <summary>
/// The demo's baseline dataset: one garment factory with its organisation,
/// people, attendance, production, planning, merchandising, store, cameras,
/// devices and documents, all connected, plus one demo account per role.
/// <para>
/// Everything except the accounts and the factory row goes through the
/// application's own services, with the demo accounts as the acting users, so
/// the data obeys the same validation, workflow (submit → approve by a second
/// person) and audit rules as anything a reviewer creates afterwards.
/// </para>
/// <para>
/// Repeatable: the reset empties the database and runs this again, and every
/// step looks up what already exists before creating it, so a re-run on the
/// same day adds nothing twice. Dates are relative to "today" in the demo's
/// time zone; <see cref="CatchUpTodayAsync"/> fills today's attendance and
/// hourly output as the day goes on.
/// </para>
/// </summary>
public sealed class DemoSeeder
{
    /// <summary>Bump when the dataset changes: a demo whose baseline is older is reset at the next start.</summary>
    public const int BaselineVersion = 1;

    public const string FactoryCode = "BBD-1";
    public const string FactoryName = "Bunon Demo Apparels Ltd.";
    public static readonly string[] Lines = { "demo-l1", "demo-l2", "demo-l3" };
    private const int StationsPerLine = 10;
    private const int HistoryDays = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IServiceProvider _root;
    private readonly DemoOptions _opts;
    private readonly ILogger _log;
    private readonly DateTimeOffset _now;
    private readonly DateOnly _today;

    private DemoSeeder(IServiceProvider root, DemoOptions opts, ILogger log, DateTimeOffset now)
    {
        _root = root; _opts = opts; _log = log; _now = now; _today = opts.Today(now);
    }

    // ── Entry points ────────────────────────────────────────────────────────

    /// <summary>Build the baseline (the database has been emptied and the catalog re-synced).</summary>
    public static async Task SeedAsync(IServiceProvider root, DemoOptions opts, ILogger log, DateTimeOffset now, CancellationToken ct)
    {
        var s = new DemoSeeder(root, opts, log, now);
        await s.SeedIdentitiesAndFactoryAsync(ct);
        var org = await s.SeedOrganisationAsync(ct);
        var people = await s.SeedPeopleAsync(org, ct);
        await s.LinkIdentitiesAsync(org, people, ct);
        await s.SeedAttendanceHistoryAsync(org, people, ct);
        var prod = await s.SeedProductionAsync(org, ct);
        await s.SeedOutputHistoryAsync(prod, ct);
        await s.SeedPlanningAsync(org, prod, ct);
        await s.Optional("merchandising", () => s.SeedMerchandisingAsync(prod, ct));
        await s.Optional("inventory", () => s.SeedInventoryAsync(org, ct));
        await s.Optional("cameras and devices", () => s.SeedCamerasAndDevicesAsync(org, ct));
        await s.Optional("documents", () => s.SeedDocumentsAsync(org, people, ct));
        await s.CatchUpAsync(org, people, prod, ct);
    }

    /// <summary>
    /// Today's gate punches and hourly output up to <paramref name="now"/>, and
    /// a running monitoring simulation during working hours. Safe to call often:
    /// punches carry idempotency keys and output rows are looked up first.
    /// </summary>
    public static async Task CatchUpTodayAsync(IServiceProvider root, DemoOptions opts, ILogger log, DateTimeOffset now, CancellationToken ct)
    {
        var s = new DemoSeeder(root, opts, log, now);
        var org = await s.LoadOrgAsync(ct);
        if (org is null) return;
        var people = await s.LoadPeopleAsync(org, ct);
        var prod = await s.LoadProductionAsync(ct);
        await s.CatchUpAsync(org, people, prod, ct);
    }

    private async Task CatchUpAsync(Org org, People people, Prod prod, CancellationToken ct)
    {
        if (!IsWorkingDay(_today)) return;
        await Optional("today's attendance", () => SeedAttendanceDayAsync(org, people, _today, ct));
        await Optional("today's output", () => SeedOutputDayAsync(prod, _today, ct));
        await Optional("monitoring simulation", () => KeepMonitoringSimulationAsync(org, ct));
    }

    // ── Accounts and factory ────────────────────────────────────────────────

    private async Task SeedIdentitiesAndFactoryAsync(CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var factory = await db.Factories.FirstOrDefaultAsync(f => f.Code == FactoryCode, ct);
        if (factory is null)
        {
            factory = new Factory { Code = FactoryCode, Name = FactoryName, Location = "Konabari, Gazipur, Bangladesh" };
            db.Factories.Add(factory);
        }

        foreach (var identity in DemoIdentities.All)
        {
            if (await db.Users.AnyAsync(u => u.Id == identity.UserId, ct)) continue;
            var role = AccessCatalog.Role(identity.RoleId)!;
            var user = new User { Id = identity.UserId, Email = identity.Email, FullName = identity.FullName, CreatedAt = _now };
            // Nobody signs in with this password (demo mode accepts only the shared credential);
            // a random one keeps the column valid and the account unusable elsewhere.
            user.PasswordHash = hasher.Hash(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            if (role.Scope != RoleScope.Platform)
                user.Factories.Add(new UserFactoryAssignment { UserId = user.Id, FactoryId = factory.Id });
            db.Users.Add(user);
        }
        db.AuditLog.Add(new AuditLogEntry
        {
            OccurredAt = DateTime.UtcNow, ActorEmail = "system:demo-seed", ActorRole = "System", Action = "demo.baseline_seeded",
            EntityType = "factory", EntityId = factory.Id.ToString(), ToState = "active",
            Reason = $"demo accounts ({DemoIdentities.All.Count}) and factory {FactoryCode}", FactoryId = factory.Id,
        });
        await db.SaveChangesAsync(ct);
    }

    private sealed record Ctx(AccessContext Admin, AccessContext Manager, AccessContext Hr, AccessContext Supervisor, AccessContext Device);

    private async Task<Ctx> ContextsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var loader = sp.GetRequiredService<IAccessSnapshotLoader>();
        async Task<AccessContext> For(string role)
        {
            var snap = await loader.LoadAsync(DemoIdentities.ForRole(role)!.UserId, ct)
                ?? throw new InvalidOperationException($"Demo account for {role} is missing.");
            return AccessContext.FromSnapshot(snap);
        }
        return new Ctx(await For(SystemRoles.FactoryAdmin), await For(SystemRoles.FactoryManager), await For(SystemRoles.HrOfficer),
            await For(SystemRoles.ProductionSupervisor), await For(SystemRoles.DeviceAdmin));
    }

    // ── Organisation ────────────────────────────────────────────────────────

    private sealed record Org(Guid FactoryId, Dictionary<string, Guid> Departments, Dictionary<string, Guid> Floors,
        Guid ShiftId, Dictionary<string, Guid> Workstations, Dictionary<string, Guid> Machines, Dictionary<string, Guid> Readers);

    private static readonly (string Code, string Name)[] DepartmentRows =
    {
        ("CUT", "Cutting"), ("SEW", "Sewing"), ("FIN", "Finishing"), ("QC", "Quality Control"),
        ("STR", "Store & Inventory"), ("MNT", "Maintenance"), ("HRA", "HR & Admin"), ("MER", "Merchandising"),
    };

    private static readonly (string Code, string Name)[] FloorRows =
    {
        ("F1", "Cutting & Store floor"), ("F2", "Sewing floor"), ("F3", "Finishing & Packing floor"),
    };

    private static readonly (string Type, string Brand)[] MachineTypes =
    {
        ("Single needle lockstitch", "JUKI DDL-9000C"), ("Overlock 4-thread", "PEGASUS M952"), ("Flatlock", "SIRUBA F007K"),
        ("Single needle lockstitch", "BROTHER S-7300A"), ("Bartack", "JUKI LK-1900B"),
    };

    private async Task<Org> SeedOrganisationAsync(CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var c = await ContextsAsync(sp, ct);
        var admin = c.Admin;
        var factoryId = admin.FactoryIds.Single();
        var orgSvc = sp.GetRequiredService<IOrganizationService>();

        var depts = Ok(await orgSvc.DepartmentsAsync(admin, factoryId, ct), "departments").ToDictionary(d => d.Code, d => d.Id);
        foreach (var (code, name) in DepartmentRows.Where(r => !depts.ContainsKey(r.Code)))
            depts[code] = Ok(await orgSvc.CreateDepartmentAsync(admin, factoryId, new(code, name), null, ct), $"department {code}").Id;

        var floors = Ok(await orgSvc.FloorsAsync(admin, factoryId, ct), "floors").ToDictionary(d => d.Code, d => d.Id);
        foreach (var (code, name) in FloorRows.Where(r => !floors.ContainsKey(r.Code)))
            floors[code] = Ok(await orgSvc.CreateFloorAsync(admin, factoryId, new(code, name), null, ct), $"floor {code}").Id;

        // Production lines (production module) attached to the factory.
        var setup = sp.GetRequiredService<IProductionSetupService>();
        var factories = sp.GetRequiredService<IFactoryService>();
        for (var i = 0; i < Lines.Length; i++)
        {
            if (!await db.ProductionLines.AnyAsync(l => l.Id == Lines[i], ct))
                Ok(await setup.CreateLineAsync(new(Lines[i], $"Sewing Line {i + 1}", StationsPerLine, true, null), admin.Actor, null, ct), $"line {Lines[i]}");
            if (!await db.ProductionLines.AnyAsync(l => l.Id == Lines[i] && l.FactoryId == factoryId, ct))
                Ok(await factories.AttachLineAsync(admin, factoryId, Lines[i], null, ct), $"attach {Lines[i]}");
        }

        // Production shift A + the attendance day shift linked to it.
        if (!await db.ProductionShifts.AnyAsync(s => s.Code == "A", ct))
            Ok(await setup.CreateShiftAsync(new("A", "Day shift", "08:00", "17:00", "13:00", 60,
                new[] { "Sat", "Sun", "Mon", "Tue", "Wed", "Thu" }, true, null), admin.Actor, null, ct), "production shift A");
        var shifts = sp.GetRequiredService<IShiftService>();
        var shiftId = await db.Shifts.Where(s => s.FactoryId == factoryId && s.Code == "DAY").Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (shiftId is null)
        {
            var created = Ok(await shifts.CreateAsync(admin, J<Application.Dtos.Attendance.CreateShiftRequest>(new
            {
                factoryId, code = "DAY", name = "Day shift 08:00–17:00", startLocal = "08:00", endLocal = "17:00",
                workingDays = new[] { "Saturday", "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday" },
                lateGraceMinutes = 10, earlyLeaveGraceMinutes = 5, productionShiftCode = "A",
                breaks = new object[]
                {
                    new { breakType = "Lunch", name = "Lunch & prayer", startLocal = "13:00", durationMinutes = 60, isPaid = false },
                    new { breakType = "Tea", name = "Tea break", startLocal = "16:00", durationMinutes = 15, isPaid = true },
                },
            }), null, ct), "day shift");
            shiftId = created.Id;
        }

        // Workstations and one machine per station.
        var stations = sp.GetRequiredService<IWorkstationService>();
        for (var i = 0; i < Lines.Length; i++)
        {
            var prefix = $"L{i + 1}-";
            if (await db.Workstations.AnyAsync(w => w.FactoryId == factoryId && w.Code.StartsWith(prefix), ct)) continue;
            Ok(await stations.BulkCreateAsync(admin, new(factoryId, Lines[i], depts["SEW"], prefix, 1, StationsPerLine, floors["F2"], 1), null, ct), $"workstations {prefix}");
        }
        var ws = await db.Workstations.AsNoTracking().Where(w => w.FactoryId == factoryId).ToDictionaryAsync(w => w.Code, w => w.Id, ct);
        var machines = await db.Machines.AsNoTracking().Where(m => m.FactoryId == factoryId).ToDictionaryAsync(m => m.Code, m => m.Id, ct);
        var n = 0;
        foreach (var (code, id) in ws.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            n++;
            var mcode = $"MC-{code}";
            if (machines.ContainsKey(mcode)) continue;
            var t = MachineTypes[n % MachineTypes.Length];
            machines[mcode] = Ok(await stations.CreateMachineAsync(admin, new(factoryId, mcode, t.Type, t.Brand, $"SN{2024000 + n}", id), null, ct), mcode).Id;
        }

        // Gate readers, attendance policy (backdating window for the history) and holidays.
        var attendance = sp.GetRequiredService<IAttendanceService>();
        var readers = await db.AttendanceReaders.AsNoTracking().Where(r => r.FactoryId == factoryId).ToDictionaryAsync(r => r.Code, r => r.Id, ct);
        foreach (var (code, name, dir) in new[] { ("GATE-IN", "Main gate — entry", "Entry"), ("GATE-OUT", "Main gate — exit", "Exit") })
            if (!readers.ContainsKey(code))
                readers[code] = Ok(await attendance.CreateReaderAsync(admin, new(factoryId, code, name, dir, "Rfid", "Factory main gate"), null, ct), code).Id;

        var policy = JsonSerializer.SerializeToNode(Ok(await attendance.PolicyAsync(admin, factoryId, ct), "policy"), Json)!.AsObject();
        policy["timeZoneId"] = "Asia/Dhaka";
        policy["maxBackdateDays"] = 30;
        Ok(await attendance.UpdatePolicyAsync(admin, factoryId, policy.Deserialize<Application.Dtos.Attendance.UpdatePolicyRequest>(Json)!, null, ct), "policy");

        var holidays = sp.GetRequiredService<IHolidayService>();
        if (!await db.FactoryHolidays.AnyAsync(h => h.FactoryId == factoryId, ct))
            foreach (var (date, name) in new[] { (NextDate(12, 16), "Victory Day"), (NextDate(2, 21), "Language Martyrs' Day"), (NextDate(3, 26), "Independence Day") })
                await Optional($"holiday {name}", async () => Ok(await holidays.CreateAsync(admin, new(factoryId, date, name, "PUBLIC"), null, ct), name));

        return new Org(factoryId, depts, floors, shiftId.Value, ws, machines, readers);
    }

    private async Task<Org?> LoadOrgAsync(CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var factory = await db.Factories.AsNoTracking().FirstOrDefaultAsync(f => f.Code == FactoryCode, ct);
        if (factory is null) return null;
        var fid = factory.Id;
        var shift = await db.Shifts.AsNoTracking().Where(s => s.FactoryId == fid && s.Code == "DAY").Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (shift is null) return null;
        return new Org(fid,
            await db.Departments.AsNoTracking().Where(d => d.FactoryId == fid).ToDictionaryAsync(d => d.Code, d => d.Id, ct),
            await db.ProductionFloors.AsNoTracking().Where(d => d.FactoryId == fid).ToDictionaryAsync(d => d.Code, d => d.Id, ct),
            shift.Value,
            await db.Workstations.AsNoTracking().Where(w => w.FactoryId == fid).ToDictionaryAsync(w => w.Code, w => w.Id, ct),
            await db.Machines.AsNoTracking().Where(m => m.FactoryId == fid).ToDictionaryAsync(m => m.Code, m => m.Id, ct),
            await db.AttendanceReaders.AsNoTracking().Where(r => r.FactoryId == fid).ToDictionaryAsync(r => r.Code, r => r.Id, ct));
    }

    // ── People ──────────────────────────────────────────────────────────────

    /// <summary>A worker as the seeder needs it: id, Worker ID, employee code, line, department and card.</summary>
    private sealed record W(Guid Id, string WorkerCode, string EmployeeCode, string? LineId, string Dept, string Designation, string CardUid);
    private sealed record People(List<W> Workers);

    private static readonly string[] Female = { "Rokeya", "Shirin", "Nasima", "Hasina", "Moriom", "Shahnaz", "Rehana", "Fatema", "Salma", "Nurjahan", "Taslima",
        "Josna", "Parvin", "Rina", "Mousumi", "Ayesha", "Sumi", "Lipi", "Shapla", "Kulsum", "Rupali", "Asma", "Morjina", "Halima", "Jesmin", "Sathi", "Mitu" };
    private static readonly string[] Male = { "Rahim", "Karim", "Jamal", "Kamal", "Sohel", "Rubel", "Mizan", "Shafiq", "Alamgir", "Habib", "Nazmul",
        "Saiful", "Imran", "Rashed", "Faruk", "Masud", "Shahin", "Liton", "Mamun" };
    private static readonly string[] Last = { "Akter", "Begum", "Khatun", "Islam", "Hossain", "Rahman", "Uddin", "Mia", "Sarker", "Ahmed", "Haque", "Mondal" };

    // (department, floor, designation, count, on a line?)
    private static readonly (string Dept, string Floor, string Designation, int Count, bool Line)[] Staffing =
    {
        ("SEW", "F2", "Sewing Operator", StationsPerLine * 3, true), ("SEW", "F2", "Line Supervisor", 3, true),
        ("CUT", "F1", "Cutter", 3, false), ("FIN", "F3", "Ironman", 2, false), ("FIN", "F3", "Packer", 2, false),
        ("QC", "F2", "QC Inspector", 3, false), ("STR", "F1", "Store Keeper", 1, false), ("MNT", "F2", "Mechanic", 1, false),
        ("HRA", "F1", "HR Assistant", 1, false),
    };

    private async Task<People> SeedPeopleAsync(Org org, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var c = await ContextsAsync(sp, ct);
        var workersSvc = sp.GetRequiredService<IWorkerService>();
        var rnd = new Random(70);

        var existing = await db.Workers.AsNoTracking().Where(w => w.FactoryId == org.FactoryId && w.EmployeeCode != null)
            .Select(w => w.EmployeeCode!).ToListAsync(ct);
        var have = existing.ToHashSet(StringComparer.Ordinal);
        var n = 0;
        foreach (var (dept, floor, designation, count, onLine) in Staffing)
        {
            for (var k = 0; k < count; k++)
            {
                n++;
                var code = $"BBD-{n:0000}";
                var female = rnd.NextDouble() < (dept is "SEW" or "FIN" or "QC" ? 0.7 : 0.3);
                var first = (female ? Female : Male)[rnd.Next(female ? Female.Length : Male.Length)];
                var last = female && rnd.NextDouble() < 0.6 ? new[] { "Akter", "Begum", "Khatun" }[rnd.Next(3)] : Last[rnd.Next(Last.Length)];
                var joined = new DateOnly(2021, 1, 1).AddDays(rnd.Next(0, 1700));
                var line = onLine ? Lines[k % Lines.Length] : null;
                var phone = $"01{"3456789"[rnd.Next(7)]}{rnd.Next(10_000_000, 99_999_999)}";
                var dob = new DateOnly(rnd.Next(1985, 2004), rnd.Next(1, 13), rnd.Next(1, 29));
                if (have.Contains(code)) continue;
                Ok(await workersSvc.CreateAsync(c.Hr, J<Application.Dtos.Workers.CreateWorkerRequest>(new
                {
                    factoryId = org.FactoryId, fullName = $"{first} {last}", phone, joinedOn = joined, departmentId = org.Departments[dept],
                    designation, employmentType = joined.Year < 2025 ? "Permanent" : "Probation", employeeCode = code,
                    gender = female ? "Female" : "Male", dateOfBirth = dob,
                    presentAddress = new[] { "Tongi, Gazipur", "Konabari, Gazipur", "Chandra, Kaliakair", "Board Bazar, Gazipur" }[rnd.Next(4)],
                    floorId = org.Floors[floor], lineId = line,
                    grade = designation switch { "Line Supervisor" => "Grade-2", "Mechanic" => "Grade-3", "HR Assistant" => "Staff", _ => $"Grade-{rnd.Next(4, 8)}" },
                }), null, ct), $"worker {code}");
            }
        }

        var people = await LoadPeopleAsync(org, ct, db);

        // RFID cards, the day shift for everyone, operators at their stations.
        foreach (var w in people.Workers)
            if (!await db.WorkerCards.AnyAsync(x => x.WorkerId == w.Id, ct))
                Ok(await workersSvc.IssueCardAsync(c.Hr, w.Id.ToString(), new(w.CardUid, "RFID", $"ID card {w.EmployeeCode}"), null, ct), $"card {w.EmployeeCode}");

        var shifts = sp.GetRequiredService<IShiftService>();
        if (!await db.ShiftAssignments.AnyAsync(a => a.ShiftId == org.ShiftId, ct))
            Ok(await shifts.AssignAsync(c.Hr, new(org.ShiftId, _today.AddDays(-60), null, people.Workers.Select(w => w.Id).ToList(), Reason: "Initial roster"), null, ct), "shift roster");

        var assignments = sp.GetRequiredService<IAssignmentService>();
        var start = Utc(_today.AddDays(-28), 8, 0);
        for (var i = 0; i < Lines.Length; i++)
        {
            var ops = people.Workers.Where(w => w.LineId == Lines[i] && w.Designation == "Sewing Operator").OrderBy(w => w.EmployeeCode, StringComparer.Ordinal).ToList();
            for (var slot = 0; slot < ops.Count && slot < StationsPerLine; slot++)
            {
                if (await db.Assignments.AnyAsync(a => a.WorkerId == ops[slot].Id, ct)) continue;
                var station = $"L{i + 1}-{slot + 1:000}";
                if (!org.Workstations.TryGetValue(station, out var wsId)) continue;
                org.Machines.TryGetValue($"MC-{station}", out var machineId);
                Ok(await assignments.CreateAsync(c.Admin, new(ops[slot].Id, wsId, machineId == Guid.Empty ? null : machineId, start, "Line set-up"), null, ct), $"assignment {station}");
            }
        }
        return people;
    }

    private async Task<People> LoadPeopleAsync(Org org, CancellationToken ct, BunonBrainDbContext? db = null)
    {
        await using var scope = db is null ? _root.CreateAsyncScope() : (AsyncServiceScope?)null;
        db ??= scope!.Value.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var rows = await (from w in db.Workers.AsNoTracking()
                          join d in db.Departments.AsNoTracking() on w.DepartmentId equals d.Id into dj
                          from d in dj.DefaultIfEmpty()
                          where w.FactoryId == org.FactoryId && w.EmployeeCode != null
                          orderby w.EmployeeCode
                          select new { w.Id, w.WorkerCode, w.EmployeeCode, w.LineId, Dept = d == null ? "" : d.Code, w.Designation }).ToListAsync(ct);
        return new People(rows.Select(r => new W(r.Id, r.WorkerCode, r.EmployeeCode!, r.LineId, r.Dept, r.Designation ?? "", CardFor(r.EmployeeCode!))).ToList());
    }

    /// <summary>The worker's RFID card number, the same after every reset.</summary>
    private static string CardFor(string employeeCode)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("card:" + employeeCode));
        return $"E2{int.Parse(employeeCode[4..]):000000}{BitConverter.ToUInt16(h, 0) % 9000 + 1000}";
    }

    /// <summary>Line scope for the supervisor, and the Worker account linked to a real worker record.</summary>
    private async Task LinkIdentitiesAsync(Org org, People people, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var supervisor = DemoIdentities.ForRole(SystemRoles.ProductionSupervisor)!.UserId;
        foreach (var line in Lines.Take(2))
            if (!await db.UserLines.AnyAsync(l => l.UserId == supervisor && l.LineId == line, ct))
                db.UserLines.Add(new UserProductionLineAssignment { UserId = supervisor, LineId = line });

        var workerAccount = DemoIdentities.ForRole(SystemRoles.Worker)!.UserId;
        if (!await db.Workers.AnyAsync(w => w.UserId == workerAccount, ct))
        {
            var first = people.Workers.First(w => w.LineId == Lines[0] && w.Designation == "Sewing Operator");
            var profile = await db.Workers.FirstAsync(w => w.Id == first.Id, ct);
            profile.UserId = workerAccount;
        }
        await db.SaveChangesAsync(ct);
        // The account names follow the people they stand for.
        var user = await db.Users.FirstAsync(u => u.Id == workerAccount, ct);
        var linked = await db.Workers.AsNoTracking().FirstAsync(w => w.UserId == workerAccount, ct);
        user.FullName = linked.FullName;
        user.SecurityStamp = User.NewStamp();
        var sup = await db.Users.FirstAsync(u => u.Id == supervisor, ct);
        sup.SecurityStamp = User.NewStamp();
        await db.SaveChangesAsync(ct);
    }

    // ── Attendance ──────────────────────────────────────────────────────────

    private async Task SeedAttendanceHistoryAsync(Org org, People people, CancellationToken ct)
    {
        foreach (var day in WorkingDaysBefore(_today, HistoryDays).OrderBy(d => d))
            await SeedAttendanceDayAsync(org, people, day, ct);

        // Overtime: approved for past evenings, and a few requests waiting for the manager.
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        if (await db.OvertimeRequests.AnyAsync(o => o.FactoryId == org.FactoryId, ct)) return;
        var c = await ContextsAsync(sp, ct);
        var overtime = sp.GetRequiredService<IOvertimeService>();
        var rnd = new Random(1970);
        var sewing = people.Workers.Where(w => w.Designation == "Sewing Operator").ToList();
        var past = WorkingDaysBefore(_today, 3).ToList();
        foreach (var w in sewing.Where((_, i) => i % 4 == 0))
        {
            var d = past[rnd.Next(past.Count)];
            var o = Ok(await overtime.CreateAsync(c.Supervisor.CanAccessLine(w.LineId ?? "", org.FactoryId) ? c.Supervisor : c.Manager,
                new(w.Id, Utc(d, 17, 0), Utc(d, 19, 0), "Shipment deadline — order backlog on the line", true), null, ct), "overtime");
            await Optional("overtime approval", async () =>
                Ok(await overtime.ApproveAsync(c.Hr, IdOf(o), new(null, "Approved for the shipment"), null, ct), "approve overtime"));
        }
        var next = NextWorkingDay(_today);
        foreach (var w in sewing.Where((_, i) => i % 7 == 3))
            Ok(await overtime.CreateAsync(c.Supervisor.CanAccessLine(w.LineId ?? "", org.FactoryId) ? c.Supervisor : c.Hr,
                new(w.Id, Utc(next, 17, 0), Utc(next, 19, 0), "Target catch-up for the H&M order", true), null, ct), "pending overtime");
    }

    /// <summary>One day of gate punches. Times are derived from the worker and the date, so a repeat produces the same events (and their idempotency keys).</summary>
    private async Task SeedAttendanceDayAsync(Org org, People people, DateOnly day, CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTime(_now, _opts.TimeZone);
        var isToday = day == _today;
        foreach (var batch in people.Workers.Chunk(12))
        {
            await using var scope = _root.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var c = await ContextsAsync(sp, ct);
            var attendance = sp.GetRequiredService<IAttendanceService>();
            foreach (var w in batch)
            {
                var rnd = new Random(Seed(w.EmployeeCode, day));
                if (rnd.NextDouble() < 0.05) continue; // absent
                var late = rnd.NextDouble() < 0.1;
                var (ih, im) = late ? (8, rnd.Next(11, 40)) : rnd.NextDouble() < 0.8 ? (7, rnd.Next(40, 60)) : (8, rnd.Next(0, 6));
                var checkIn = Utc(day, ih, im, rnd.Next(60));
                var overtime = w.Designation == "Sewing Operator" && rnd.NextDouble() < 0.2;
                var (oh, om) = overtime ? (19, rnd.Next(0, 15)) : rnd.NextDouble() < 0.03 ? (16, rnd.Next(0, 40)) : (17, rnd.Next(0, 20));
                var checkOut = Utc(day, oh, om, rnd.Next(60));
                var forgot = rnd.NextDouble() < 0.01;

                if (checkIn <= _now)
                    await Punch(attendance, c.Hr, org.Readers["GATE-IN"], w, "CheckIn", checkIn, $"demo-{w.EmployeeCode}-{day:yyyyMMdd}-in", ct);
                if (!forgot && checkOut <= _now && (!isToday || local.TimeOfDay >= TimeSpan.FromHours(17)))
                    await Punch(attendance, c.Hr, org.Readers["GATE-OUT"], w, "CheckOut", checkOut, $"demo-{w.EmployeeCode}-{day:yyyyMMdd}-out", ct);
            }
        }
    }

    private async Task Punch(IAttendanceService attendance, AccessContext caller, Guid readerId, W w, string type, DateTimeOffset at, string key, CancellationToken ct)
    {
        var r = await attendance.DeviceEventAsync(caller, new(readerId, w.CardUid, type, at, key), ct);
        if (r.Outcome is not (AccessOutcome.Ok or AccessOutcome.Created) && r.Code is not ("duplicate" or "duplicate_event"))
            _log.LogDebug("Demo punch {Key} not recorded: {Code} {Message}", key, r.Code, r.Message);
    }

    // ── Production ──────────────────────────────────────────────────────────

    private sealed record Prod(Dictionary<string, Guid> Styles, Dictionary<string, Guid> Orders, Dictionary<string, (string Order, double Smv)> LineOrders);

    private static readonly (string Line, string Order, double Smv)[] LineLoading =
    {
        ("demo-l1", "BBD-PO-24117", 8.5), ("demo-l2", "BBD-PO-24122", 14.2), ("demo-l3", "BBD-PO-24131", 12.4),
    };

    private async Task<Prod> SeedProductionAsync(Org org, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var c = await ContextsAsync(sp, ct);
        var setup = sp.GetRequiredService<IProductionSetupService>();

        object Op(int seq, string name, double smv, string machine) => new { sequence = seq, name, smv, machineType = machine, predecessors = seq > 1 ? new[] { seq - 1 } : Array.Empty<int>() };
        var styleRows = new (string Code, string Name, string Type, string Cat, string Cx, double Smv, string[] Sizes, string[] Colors, object[] Ops)[]
        {
            ("BBD-TS-101", "Men's basic crew-neck T-shirt", "T-shirt", "KNIT_TOP", "LOW", 8.5, new[] { "S", "M", "L", "XL", "XXL" }, new[] { "Black", "White", "Navy" },
                new[] { Op(1, "Shoulder join", 0.9, "Overlock 4-thread"), Op(2, "Neck rib attach", 1.2, "Overlock 4-thread"), Op(3, "Neck top stitch", 1.0, "Flatlock"),
                        Op(4, "Sleeve attach", 1.6, "Overlock 4-thread"), Op(5, "Side seam", 1.7, "Overlock 4-thread"), Op(6, "Sleeve hem", 1.0, "Flatlock"), Op(7, "Bottom hem", 1.1, "Flatlock") }),
            ("BBD-PL-205", "Men's pique polo shirt", "Polo shirt", "KNIT_TOP", "MEDIUM", 14.2, new[] { "S", "M", "L", "XL" }, new[] { "Red", "Royal blue", "White" },
                new[] { Op(1, "Placket make", 2.4, "Single needle lockstitch"), Op(2, "Placket attach", 2.1, "Single needle lockstitch"), Op(3, "Collar attach", 2.2, "Single needle lockstitch"),
                        Op(4, "Shoulder join", 1.0, "Overlock 4-thread"), Op(5, "Sleeve attach", 1.9, "Overlock 4-thread"), Op(6, "Side seam + vent", 2.4, "Overlock 4-thread"),
                        Op(7, "Hems", 1.2, "Flatlock"), Op(8, "Buttonhole + button", 1.0, "Bartack") }),
            ("BBD-JG-310", "Kids' fleece jogger pant", "Jogger", "KNIT_BOTTOM", "MEDIUM", 12.4, new[] { "2Y", "4Y", "6Y", "8Y", "10Y" }, new[] { "Grey melange", "Navy" },
                new[] { Op(1, "Pocket attach", 2.1, "Single needle lockstitch"), Op(2, "Inseam", 1.8, "Overlock 4-thread"), Op(3, "Outseam", 1.9, "Overlock 4-thread"),
                        Op(4, "Rise join", 1.6, "Overlock 4-thread"), Op(5, "Waistband attach", 2.4, "Overlock 4-thread"), Op(6, "Cuff attach", 1.6, "Overlock 4-thread"), Op(7, "Bartack", 1.0, "Bartack") }),
            ("BBD-HD-520", "Unisex pullover hoodie", "Hoodie", "KNIT_TOP", "HIGH", 22.0, new[] { "S", "M", "L", "XL" }, new[] { "Black", "Olive" },
                new[] { Op(1, "Hood make", 3.4, "Overlock 4-thread"), Op(2, "Kangaroo pocket", 3.2, "Flatlock"), Op(3, "Shoulder join", 1.2, "Overlock 4-thread"), Op(4, "Hood attach", 3.0, "Overlock 4-thread"),
                        Op(5, "Sleeve attach", 2.8, "Overlock 4-thread"), Op(6, "Side seam", 2.6, "Overlock 4-thread"), Op(7, "Cuff + waistband", 3.6, "Overlock 4-thread"), Op(8, "Eyelet + drawcord", 2.2, "Bartack") }),
        };
        var styles = await db.ProductionStyles.AsNoTracking().ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        foreach (var s in styleRows.Where(s => !styles.ContainsKey(s.Code)))
            styles[s.Code] = IdOf(Ok(await setup.CreateStyleAsync(J<Application.Dtos.Production.StyleWrite>(new
            {
                code = s.Code, name = s.Name, productType = s.Type, approvedSmv = s.Smv, isActive = true, operations = s.Ops,
                garmentCategory = s.Cat, description = $"{s.Name} — bulk production style.", sizeRange = s.Sizes, colorVariants = s.Colors,
                complexity = s.Cx, qualityCheckpoints = new[] { "Measurement check", "Stitch density", "Shade matching", "Final visual" },
            }), c.Admin.Actor, null, ct), $"style {s.Code}"));

        var orderRows = new (string Num, string Style, string Buyer, int Qty, int Due, bool Running, string Prio, string Desc)[]
        {
            ("BBD-PO-24117", "BBD-TS-101", "H&M", 24000, 38, true, "HIGH", "Basic tee — 3 colours, S–XXL"),
            ("BBD-PO-24122", "BBD-PL-205", "Primark", 15000, 45, true, "NORMAL", "Pique polo — FW26 programme"),
            ("BBD-PO-24131", "BBD-JG-310", "C&A", 18000, 52, true, "URGENT", "Kids jogger — school range"),
            ("BBD-PO-24155", "BBD-HD-520", "Walmart", 12000, 80, false, "NORMAL", "Hoodie — winter replenishment"),
        };
        var ordersSvc = sp.GetRequiredService<IProductionOrderService>();
        var orders = await db.ProductionOrders.AsNoTracking().ToDictionaryAsync(o => o.OrderNumber, o => o.Id, ct);
        foreach (var o in orderRows.Where(o => !orders.ContainsKey(o.Num)))
        {
            var created = Ok(await ordersSvc.CreateAsync(J<Application.Dtos.Production.OrderWrite>(new
            {
                orderNumber = o.Num, styleId = styles[o.Style], buyer = o.Buyer, quantity = o.Qty, dueDate = D(o.Due), status = "planned",
                notes = o.Desc, factoryId = org.FactoryId, orderReference = $"{o.Buyer[..Math.Min(3, o.Buyer.Length)].ToUpperInvariant()}-{o.Num[^5..]}",
                productDescription = o.Desc, productCategory = "Apparel", startDate = D(-16), plannedCompletionDate = D(o.Due - 7), priority = o.Prio,
            }), c.Admin, null, ct), $"order {o.Num}");
            orders[o.Num] = IdOf(created);
            if (o.Running)
                await Optional($"start {o.Num}", async () =>
                    Ok(await ordersSvc.ChangeStatusAsync(orders[o.Num], new("in_progress", "Line started", Version(created)), c.Admin, null, ct), "order status"));
        }

        // Line plans: prepared by the factory admin, activated by the manager (two people).
        var plans = sp.GetRequiredService<IProductionPlanService>();
        foreach (var (line, num, _) in LineLoading)
        {
            if (await db.ProductionPlans.AnyAsync(p => p.LineId == line, ct)) continue;
            await Optional($"plan {line}", async () =>
            {
                var plan = Ok(await plans.CreateAsync(new(orders[num], line, D(-16), D(30), num == "BBD-PO-24117" ? 12000 : 9000, StationsPerLine, 62.0,
                    new[] { "A" }, 60, $"Line loading for {num}", "Initial line plan", null), c.Admin.Actor, null, ct), "plan");
                var id = IdOf(plan);
                var submitted = Ok(await plans.SubmitAsync(id, new("Ready for approval", Version(plan)), c.Admin.Actor, null, ct), "submit plan");
                // Activation goes through the approval queue: a second person decides it first.
                var reloaded = await plans.GetAsync(id, ct);
                var current = Version(submitted) ?? (reloaded is null ? null : Version(reloaded));
                await Optional("approve and activate plan", async () =>
                {
                    var json = JsonSerializer.SerializeToElement(reloaded, Json);
                    if (json.TryGetProperty("activationApproval", out var a) && a.ValueKind == JsonValueKind.Object
                        && a.GetProperty("id").GetString() is { } approvalId)
                    {
                        var approvals = sp.GetRequiredService<IApprovalService>();
                        var decided = await approvals.DecideAsync(approvalId, true, "Line loading agreed", ApproverFor(c.Manager), null, ct);
                        if (decided.Outcome == Application.Dtos.Approvals.ApprovalDecisionOutcome.RequiresAdmin)
                            decided = await approvals.DecideAsync(approvalId, true, "Line loading agreed", ApproverFor(await PlatformAdminAsync(sp, ct)), null, ct);
                        if (decided.Outcome is not (Application.Dtos.Approvals.ApprovalDecisionOutcome.Ok or Application.Dtos.Approvals.ApprovalDecisionOutcome.AlreadyDecided))
                            throw new InvalidOperationException($"plan approval: {decided.Outcome}");
                    }
                    Ok(await plans.ActivateAsync(id, new("Approved", current), c.Manager.Actor, null, ct), "activate plan");
                });
            });
        }
        return new Prod(styles, orders, LineLoading.ToDictionary(l => l.Line, l => (l.Order, l.Smv)));
    }

    private async Task<Prod> LoadProductionAsync(CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        return new Prod(await db.ProductionStyles.AsNoTracking().ToDictionaryAsync(s => s.Code, s => s.Id, ct),
            await db.ProductionOrders.AsNoTracking().ToDictionaryAsync(o => o.OrderNumber, o => o.Id, ct),
            LineLoading.ToDictionary(l => l.Line, l => (l.Order, l.Smv)));
    }

    private static readonly string[] Downtime = { "Machine breakdown (needle bar)", "Waiting for cut panels", "Style changeover", "Power interruption", "Thread shade mismatch" };
    private static readonly int[] WorkHours = { 8, 9, 10, 11, 12, 14, 15, 16 };

    private async Task SeedOutputHistoryAsync(Prod prod, CancellationToken ct)
    {
        foreach (var day in WorkingDaysBefore(_today, HistoryDays).OrderBy(d => d))
            await SeedOutputDayAsync(prod, day, ct);
    }

    /// <summary>Hourly sewing output of each line for <paramref name="day"/>, for hours that have ended.</summary>
    private async Task SeedOutputDayAsync(Prod prod, DateOnly day, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var c = await ContextsAsync(sp, ct);
        var output = sp.GetRequiredService<IProductionOutputService>();
        foreach (var (line, (orderNum, smv)) in prod.LineOrders)
        {
            if (!prod.Orders.TryGetValue(orderNum, out var orderId)) continue;
            var dayStart = Utc(day, 0, 0).UtcDateTime;
            var dayEnd = dayStart.AddDays(1);
            var have = (await db.ProductionOutputEvents.AsNoTracking()
                .Where(e => e.LineId == line && e.IntervalStartUtc >= dayStart && e.IntervalStartUtc < dayEnd)
                .Select(e => e.IntervalStartUtc).ToListAsync(ct)).ToHashSet();
            foreach (var h in WorkHours)
            {
                var start = Utc(day, h, 0).UtcDateTime;
                var end = start.AddHours(1);
                if (end > _now.UtcDateTime || have.Contains(start)) continue;
                var rnd = new Random(Seed(line + h, day));
                var operators = new[] { 9, 10, 10, 10 }[rnd.Next(4)];
                var qty = operators * 60 * (0.52 + rnd.NextDouble() * 0.18) / smv;
                double down = 0; string? reason = null;
                if (rnd.NextDouble() < 0.07) { down = new[] { 10, 15, 20, 30 }[rnd.Next(4)]; reason = Downtime[rnd.Next(Downtime.Length)]; qty *= (60 - down) / 60; }
                if (h == 8) qty *= 0.8;
                var q = (int)Math.Round(qty);
                await Optional($"output {line} {day} {h}", async () => Ok(await output.RecordAsync(new(line, orderId, "sewing_output", start, end, q,
                    rnd.Next(0, Math.Max(1, q / 15)), rnd.NextDouble() < 0.3 ? rnd.Next(0, 2) : 0, operators, down, reason, "manual",
                    $"demo-{line}-{day:yyyyMMdd}-{h}", null), c.Supervisor.Actor, null, ct), "output"));
            }
        }
    }

    // ── Planning ────────────────────────────────────────────────────────────

    private async Task SeedPlanningAsync(Org org, Prod prod, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var c = await ContextsAsync(sp, ct);

        var setup = sp.GetRequiredService<IPlanningSetupService>();
        foreach (var (line, eff) in Lines.Zip(new[] { 62.0, 58.0, 60.0 }))
            await Optional($"capacity {line}", async () =>
                Ok(await setup.SaveCapacitySettingsAsync(c.Manager, line, new(eff, StationsPerLine, "Standard loading, 1 shift", null, null, null), null, ct), "capacity"));

        var targets = sp.GetRequiredService<ITargetService>();
        if (!await db.ProductionTargets.AnyAsync(t => t.FactoryId == org.FactoryId, ct))
            foreach (var (line, qty) in Lines.Zip(new[] { 420, 240, 290 }))
                await Optional($"target {line}", async () =>
                {
                    var t = Ok(await targets.CreateAsync(c.Manager, new(org.FactoryId, "LINE", "DAILY", null, null, line, null, null, null, null, null, qty,
                        D(-14), null, "Daily line target", "Initial targets from line capacity", null), null, ct), "target");
                    var json = JsonSerializer.SerializeToElement(t, Json);
                    if (json.TryGetProperty("status", out var st) && st.GetString() is { } status && status.Contains("PENDING", StringComparison.OrdinalIgnoreCase))
                        Ok(await targets.DecideAsync(c.Admin, IdOf(t), new(true, "Agreed", null), null, ct), "approve target");
                });

        // Today's daily plan: prepared by the manager, approved and published by the factory admin.
        var daily = sp.GetRequiredService<IDailyPlanService>();
        if (IsWorkingDay(_today) && !await db.DailyProductionPlans.AnyAsync(p => p.FactoryId == org.FactoryId, ct))
            await Optional("daily plan", async () =>
            {
                var plan = Ok(await daily.CreateAsync(c.Manager, new(org.FactoryId, _today.ToString("yyyy-MM-dd"), "Plan for the three sewing lines", null), null, ct), "daily plan");
                var id = IdOf(plan);
                foreach (var (line, (num, _)) in prod.LineOrders)
                {
                    var qty = line == "demo-l1" ? 430 : line == "demo-l2" ? 245 : 300;
                    plan = Ok(await daily.AddItemAsync(c.Manager, id, new(prod.Orders[num], null, line, org.ShiftId, qty, null, null, "Carry-over plus today's target"),
                        Version(plan), null, ct), "plan item");
                }
                plan = Ok(await daily.SubmitAsync(c.Manager, id, new("Ready for approval", Version(plan)), null, ct), "submit daily plan");
                plan = Ok(await daily.ApproveAsync(c.Admin, id, new("Approved", Version(plan)), null, ct), "approve daily plan");
                Ok(await daily.PublishAsync(c.Admin, id, new("Published to the floor", Version(plan)), null, ct), "publish daily plan");
            });

        await Optional("exceptions", async () =>
            Ok(await sp.GetRequiredService<IProductionExceptionService>().EvaluateAsync(c.Manager, new(org.FactoryId, null), null, ct), "exceptions"));
    }

    // ── Merchandising, store, cameras, documents ────────────────────────────

    private async Task SeedMerchandisingAsync(Prod prod, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var merch = sp.GetRequiredService<IMerchOrderService>();
        var actor = AccessContext.FromSnapshot((await sp.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(DemoIdentities.ForRole(SystemRoles.Merchandiser)!.UserId, ct))!).Actor;
        var rows = new (string Buyer, string Po, string Style, string Desc, int Qty, int Due, double Price, string Dest, string Prod)[]
        {
            ("H&M", "HM-778124", "BBD-TS-101", "Men's basic crew-neck T-shirt", 24000, 38, 2.85, "Hamburg, DE", "BBD-PO-24117"),
            ("Primark", "PK-55210", "BBD-PL-205", "Men's pique polo shirt", 15000, 45, 4.10, "Felixstowe, UK", "BBD-PO-24122"),
            ("C&A", "CA-90311", "BBD-JG-310", "Kids' fleece jogger pant", 18000, 52, 3.40, "Rotterdam, NL", "BBD-PO-24131"),
            ("Walmart", "WM-210677", "BBD-HD-520", "Unisex pullover hoodie", 12000, 80, 7.80, "Los Angeles, US", "BBD-PO-24155"),
        };
        var have = (await db.MerchOrders.AsNoTracking().Select(o => o.BuyerOrderNumber).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var r in rows.Where(r => !have.Contains(r.Po)))
        {
            var milestones = new (string Name, int Offset)[] { ("Fabric booking", -55), ("Lab dip approval", -45), ("PP sample approval", -30), ("Fabric in-house", -20),
                ("Cutting start", -15), ("Sewing start", -12), ("Inline inspection", -5), ("Final inspection (AQL)", -4), ("Ex-factory", 0) }
                .Select(m =>
                {
                    var due = _today.AddDays(m.Offset + (r.Due - 38));
                    var done = due < _today;
                    return new { name = m.Name, dueDate = due.ToString("yyyy-MM-dd"), status = done ? "done" : "pending", completedOn = done ? due.ToString("yyyy-MM-dd") : null };
                }).ToArray();
            Ok(await merch.CreateAsync(J<Application.Dtos.Merchandising.MerchOrderWrite>(new
            {
                buyer = r.Buyer, buyerOrderNumber = r.Po, styleRef = r.Style, productDescription = r.Desc, quantity = r.Qty, unit = "pcs",
                deliveryDate = D(r.Due), currency = "USD", unitPrice = r.Price, destination = r.Dest,
                requiredDocumentTypes = new[] { "purchase_order", "tech_pack", "measurement_sheet" },
                productionOrderId = prod.Orders.TryGetValue(r.Prod, out var po) ? po : (Guid?)null,
                notes = $"{r.Buyer} order {r.Po}", milestones,
            }), actor, null, ct), $"merch {r.Po}");
        }
    }

    private async Task SeedInventoryAsync(Org org, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var store = AccessContext.FromSnapshot((await sp.GetRequiredService<IAccessSnapshotLoader>().LoadAsync(DemoIdentities.ForRole(SystemRoles.StoreOfficer)!.UserId, ct))!);
        var inventory = sp.GetRequiredService<IInventoryService>();
        var items = new (string Code, string Name, string Cat, string Unit, decimal Reorder)[]
        {
            ("FAB-SJ-160", "Single jersey 160 GSM, 100% cotton", "FABRIC", "kg", 800), ("FAB-PQ-220", "Pique 220 GSM, CVC 60/40", "FABRIC", "kg", 600),
            ("FAB-FL-280", "Fleece 280 GSM, brushed back", "FABRIC", "kg", 700), ("FAB-RIB-1X1", "1x1 rib for collars and cuffs", "FABRIC", "kg", 150),
            ("THR-40-2", "Sewing thread 40/2 polyester, 5000 m cone", "TRIMS", "cone", 300), ("THR-OL-120", "Overlock thread 120 tex, 8000 m cone", "TRIMS", "cone", 250),
            ("BTN-PL-14L", "Polo button 14L, 2-hole", "ACCESSORIES", "gross", 40), ("LBL-MAIN", "Woven main label", "TRIMS", "pcs", 10000),
            ("PKG-POLY", "Polybag 30x40 cm with warning", "PACKING", "pcs", 8000), ("PKG-CTN-5P", "Export carton 5-ply 60x40x40", "PACKING", "pcs", 600),
        };
        var have = (await db.InventoryItems.AsNoTracking().Where(i => i.FactoryId == org.FactoryId).Select(i => i.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var i in items.Where(i => !have.Contains(i.Code)))
            Ok(await inventory.CreateAsync(store, new(org.FactoryId, i.Code, i.Name, i.Cat, i.Unit, i.Reorder, true), null, ct), $"item {i.Code}");
    }

    private async Task SeedCamerasAndDevicesAsync(Org org, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var c = await ContextsAsync(sp, ct);
        var cameras = sp.GetRequiredService<ICameraService>();
        var have = (await db.Cameras.AsNoTracking().Where(x => x.FactoryId == org.FactoryId).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var rows = Lines.Select((l, i) => (Code: $"CAM-L{i + 1}", Name: $"Sewing Line {i + 1} overview", Type: "IpCamera", Line: (string?)l,
                Url: (string?)$"rtsp://10.70.2.{11 + i}:554/Streaming/Channels/101"))
            .Append(("CAM-CUT", "Cutting table overview", "IpCamera", null, "rtsp://10.70.1.21:554/Streaming/Channels/101"))
            .Append(("CAM-AI-1", "Browser webcam (camera AI)", "Webcam", null, null));
        foreach (var r in rows.Where(r => !have.Contains(r.Code)))
        {
            var cut = r.Code == "CAM-CUT";
            var cam = Ok(await cameras.CreateAsync(c.Device, J<Application.Dtos.Cameras.CreateCameraRequest>(new
            {
                factoryId = org.FactoryId, code = r.Code, name = r.Name, cameraType = r.Type, floorId = org.Floors[cut ? "F1" : "F2"],
                departmentId = org.Departments[cut ? "CUT" : "SEW"], lineId = r.Line,
                location = r.Line is not null ? "Ceiling, facing the line" : cut ? "Cutting tables 1–3" : "Demo desk",
                installedOn = _today.AddDays(-120), coverageLineIds = r.Line is null ? Array.Empty<string>() : new[] { r.Line },
            }), null, ct), $"camera {r.Code}");
            var source = r.Url is null
                ? new Application.Dtos.Cameras.SetCameraSourceRequest("LocalWebcam")
                : J<Application.Dtos.Cameras.SetCameraSourceRequest>(new { sourceType = "Rtsp", streamUrl = r.Url, notes = "NVR channel (demo address)" });
            await Optional($"camera source {r.Code}", async () => Ok(await cameras.SetSourceAsync(c.Device, IdOf(cam), source, null, ct), "camera source"));
        }

        var devices = sp.GetRequiredService<IDeviceService>();
        var devs = (await db.Devices.AsNoTracking().Where(x => x.FactoryId == org.FactoryId).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var (code, name, reader) in new[] { ("RFID-GATE-IN", "Gate entry RFID reader", "GATE-IN"), ("RFID-GATE-OUT", "Gate exit RFID reader", "GATE-OUT") })
            if (!devs.Contains(code))
                Ok(await devices.CreateAsync(c.Device, J<Application.Dtos.Devices.CreateDeviceRequest>(new
                {
                    factoryId = org.FactoryId, code, name, category = "RfidReader", isSimulated = false, attendanceReaderId = org.Readers[reader],
                    manufacturer = "ZKTeco", model = "ProCapture-T", serialNumber = $"ZK{code[^3..]}7741", firmwareVersion = "6.60",
                    installedOn = _today.AddDays(-200), heartbeatIntervalSeconds = 60, offlineAfterSeconds = 300,
                }), null, ct), code);
    }

    private async Task SeedDocumentsAsync(Org org, People people, CancellationToken ct)
    {
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        if (await db.FactoryDocuments.AnyAsync(d => d.FactoryId == org.FactoryId, ct)) return;
        var c = await ContextsAsync(sp, ct);
        var docs = sp.GetRequiredService<IDocumentService>();
        var yesterday = WorkingDaysBefore(_today, 1).First();
        var week = WorkingDaysBefore(_today, HistoryDays).ToList();

        async Task Create(AccessContext who, string type, DateOnly? from, DateOnly? to, string? line, string? title, JsonObject? data, bool submit)
        {
            await Optional($"document {type}", async () =>
            {
                var d = Ok(await docs.CreateAsync(who, new(org.FactoryId, type, null, from, to, line, title, data, null, null), null, ct), type);
                if (!submit) return;
                var doc = JsonSerializer.SerializeToElement(d, Json);
                var version = doc.TryGetProperty("document", out var inner) && inner.TryGetProperty("currentVersion", out var v) ? v.GetInt32() : (int?)null;
                Ok(await docs.ActAsync(who, IdOf(d), "submit", new("Submitted for review", version), null, ct), $"submit {type}");
            });
        }

        // Generated from the operational records above.
        foreach (var d in week.Take(3))
            await Create(c.Admin, "production.daily", d, d, null, null, null, submit: true);
        await Create(c.Supervisor, "production.line", yesterday, yesterday, Lines[0], null, null, submit: true);
        await Create(c.Hr, "hr.attendance", yesterday, yesterday, null, null, null, submit: true);
        await Create(c.Hr, "hr.overtime", week.Min(), yesterday, null, null, null, submit: false);
        await Create(c.Device, "devices.log", week.Min(), yesterday, null, null, null, submit: false);

        // Leave applications on HR's desk.
        var sewing = people.Workers.Where(w => w.Designation == "Sewing Operator").ToList();
        foreach (var (w, type, from, to, reason) in new[]
        {
            (sewing[3], "SICK", -3, -2, "Fever — doctor's note at the HR desk."),
            (sewing[11], "CASUAL", 2, 2, "Family programme."),
            (sewing[20], "ANNUAL", 5, 9, "Visiting home district."),
        })
            await Create(c.Hr, "hr.leave", null, null, null, $"{type[0]}{type[1..].ToLowerInvariant()} leave", new JsonObject
            {
                ["workerCode"] = w.WorkerCode, ["leaveType"] = type, ["fromDate"] = D(from), ["toDate"] = D(to), ["reason"] = reason,
            }, submit: true);
    }

    // ── Monitoring ──────────────────────────────────────────────────────────

    /// <summary>During working hours keep one monitoring simulation (labelled SIMULATED) running on the line cameras.</summary>
    private async Task KeepMonitoringSimulationAsync(Org org, CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTime(_now, _opts.TimeZone).TimeOfDay;
        if (local < TimeSpan.FromHours(8) || local > TimeSpan.FromHours(17)) return;
        await using var scope = _root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<BunonBrainDbContext>();
        var operatorCtx = AccessContext.FromSnapshot((await sp.GetRequiredService<IAccessSnapshotLoader>()
            .LoadAsync(DemoIdentities.ForRole(SystemRoles.MonitoringOperator)!.UserId, ct))!);
        var sim = sp.GetRequiredService<IMonitoringSimulatorService>();
        var status = Ok(await sim.StatusAsync(operatorCtx, org.FactoryId, ct), "simulator status");
        if (status.Running is not null) return;
        var cams = await db.Cameras.AsNoTracking().Where(x => x.FactoryId == org.FactoryId && x.Code.StartsWith("CAM-L")).Select(x => x.Id).ToListAsync(ct);
        if (cams.Count == 0) return;
        Ok(await sim.StartAsync(operatorCtx, new(org.FactoryId, cams, 3, 120), null, ct), "simulation");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Application.Dtos.Approvals.ApprovalActor ApproverFor(AccessContext who) =>
        new(who.UserId, who.Email, who.PrimaryRole, who.Has(Permissions.ApprovalsDecideHigh));

    private static async Task<AccessContext> PlatformAdminAsync(IServiceProvider sp, CancellationToken ct) =>
        AccessContext.FromSnapshot((await sp.GetRequiredService<IAccessSnapshotLoader>()
            .LoadAsync(DemoIdentities.ForRole(SystemRoles.SuperAdmin)!.UserId, ct))!);

    private async Task Optional(string what, Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Demo seed step '{Step}' skipped: {Message}", what, ex.Message);
        }
    }

    private static T Ok<T>(AccessResult<T> r, string what) =>
        r.Outcome is AccessOutcome.Ok or AccessOutcome.Created && r.Value is not null ? r.Value
            : throw new InvalidOperationException($"{what}: {r.Outcome} {r.Code} {r.Message}");

    private static T Ok<T>(Application.Abstractions.Interfaces.ProductionResult<T> r, string what) =>
        r.Value is not null && r.Code is null && r.Errors is null ? r.Value
            : throw new InvalidOperationException($"{what}: {r.Outcome} {r.Code} {r.Message} {Flatten(r.Errors)}");

    private static T Ok<T>(Application.Abstractions.Interfaces.MerchResult<T> r, string what) =>
        r.Value is not null && r.Errors is null ? r.Value
            : throw new InvalidOperationException($"{what}: {r.Outcome} {Flatten(r.Errors)}");

    private static string Flatten(IReadOnlyDictionary<string, string[]>? errors) =>
        errors is null ? "" : string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}"));

    /// <summary>Build a request record from an anonymous object, the way the API binds JSON bodies.</summary>
    private static T J<T>(object body) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(body, Json), Json)!;

    /// <summary>The <c>id</c> of a DTO (top level, or inside a <c>document</c> / <c>plan</c> / <c>order</c> wrapper).</summary>
    private static Guid IdOf(object dto)
    {
        var e = JsonSerializer.SerializeToElement(dto, Json);
        if (e.TryGetProperty("id", out var id)) return id.GetGuid();
        foreach (var wrapper in Wrappers)
            if (e.TryGetProperty(wrapper, out var w) && w.ValueKind == JsonValueKind.Object && w.TryGetProperty("id", out var wid)) return wid.GetGuid();
        throw new InvalidOperationException($"No id in {dto.GetType().Name} result.");
    }

    private static readonly string[] Wrappers = { "summary", "plan", "document", "order", "request", "target" };

    private static uint? Version(object dto)
    {
        var e = JsonSerializer.SerializeToElement(dto, Json);
        if (e.TryGetProperty("version", out var v) && v.TryGetUInt32(out var n)) return n;
        foreach (var wrapper in Wrappers)
            if (e.TryGetProperty(wrapper, out var w) && w.ValueKind == JsonValueKind.Object
                && w.TryGetProperty("version", out var wv) && wv.TryGetUInt32(out var wn)) return wn;
        return null;
    }

    private DateTimeOffset Utc(DateOnly day, int hour, int minute, int second = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute, second));
        return new DateTimeOffset(local, _opts.TimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private string D(int days) => _today.AddDays(days).ToString("yyyy-MM-dd");

    private DateOnly NextDate(int month, int day)
    {
        var d = new DateOnly(_today.Year, month, day);
        return d <= _today ? d.AddYears(1) : d;
    }

    private static bool IsWorkingDay(DateOnly d) => d.DayOfWeek != DayOfWeek.Friday;

    private static IEnumerable<DateOnly> WorkingDaysBefore(DateOnly today, int count)
    {
        var d = today.AddDays(-1);
        for (var found = 0; found < count; d = d.AddDays(-1))
            if (IsWorkingDay(d)) { found++; yield return d; }
    }

    private static DateOnly NextWorkingDay(DateOnly today)
    {
        var d = today.AddDays(1);
        while (!IsWorkingDay(d)) d = d.AddDays(1);
        return d;
    }

    private static int Seed(string key, DateOnly day) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}|{day:yyyyMMdd}")), 0);
}
