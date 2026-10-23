using System.Collections.Concurrent;
using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Dtos.Sensors;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Services;

/// <summary>
/// In-memory simulated sensor buffer — same role as
/// <c>src/services/sensors.server</c>. Persists latest 200 readings, advances
/// a tick counter, derives line / machine summaries deterministically.
/// </summary>
public sealed class SensorService : ISensorService
{
    // Static arrays must be declared BEFORE _state is initialized because the
    // static field initializer calls Seed() which reads LINES / MACHINES.
    private static readonly string[] LINES =
        { "line-1", "line-2", "line-3", "line-4", "line-5", "line-6" };
    private static readonly string[] MACHINES =
        { "M-101", "M-102", "M-103", "M-104", "M-105", "M-201", "M-202", "M-203",
          "M-204", "M-205", "M-206", "M-207", "M-208", "M-209", "M-210", "M-267" };

    private static readonly object _gate = new();
    private static SimState _state = Seed();
    /// <summary>
    /// Optional XOR-mixed seed supplied by <c>SIMULATOR_SEED</c>. When
    /// non-zero, every <see cref="StepTick"/> RNG combines it with the
    /// tick number so two runs with the same seed produce the same
    /// first N readings (after ignoring timestamps and ids). Defaults
    /// to 0 — i.e. the existing per-tick seed <c>0xA11CE ^ Tick</c>.
    /// </summary>
    public static int SimulatorSeedOverride;
    private readonly FactoryBrainDbContext _db;
    private readonly IRealtimeNotifier _notifier;

    public SensorService(FactoryBrainDbContext db, IRealtimeNotifier notifier)
    {
        _db = db;
        _notifier = notifier;
        SyncNextIdFromDatabaseAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// On first construction (per process), look at the highest existing
    /// <c>srv-sensor-N</c> primary key in Postgres and bump <c>_state.NextId</c>
    /// past it so freshly-ingested readings don't collide with persisted ones.
    /// </summary>
    private async Task SyncNextIdFromDatabaseAsync()
    {
        try
        {
            var maxId = await _db.SensorReadings
                .Where(r => r.Id.StartsWith("srv-sensor-"))
                .Select(r => r.Id)
                .ToListAsync();
            int max = 0;
            foreach (var id in maxId)
            {
                if (id.StartsWith("srv-sensor-") &&
                    int.TryParse(id.AsSpan("srv-sensor-".Length), out var n) &&
                    n > max) max = n;
            }
            if (max >= _state.NextId) _state.NextId = max + 1;
        }
        catch
        {
            // best-effort; if DB is unreachable the first ingest will surface the
            // underlying error via GlobalExceptionMiddleware.
        }
    }

    public bool LiveConnected => _state.Live;

    public SensorSimState CurrentState => new(
        _state.Tick,
        _state.Lines.Select(l => new LineBoardLine(l.Id, l.Efficiency, l.Uptime, l.EnergyKwh)).ToList(),
        _state.Machines.Select(m => new LineBoardMachine(m.Id, m.Vibration, m.Temperature, m.DutyCycle, m.Status)).ToList(),
        _state.Readings.ToList()
    );

    public async Task<IngestResponse> AcceptLiveAsync(LiveIngestRequest req, CancellationToken ct)
    {
        List<SensorReading> batch;
        lock (_gate)
        {
            _state.Tick++;
            _state.Live = true;
            batch = new List<SensorReading>();
            foreach (var input in req.Readings)
            {
                if (!Enum.TryParse<SensorSource>(input.Source, ignoreCase: true, out var source))
                    continue;
                var reading = new SensorReading
                {
                    Id = $"srv-sensor-{++_state.NextId}",
                    Source = source,
                    EntityId = input.EntityId.Trim(),
                    Metric = input.Metric.Trim(),
                    Value = input.Value,
                    Unit = string.IsNullOrWhiteSpace(input.Unit) ? UnitFor(input.Metric) : input.Unit!,
                    Ts = input.Ts?.ToUniversalTime() ?? DateTime.UtcNow,
                    SimTick = _state.Tick
                };
                ApplyToFloor(reading);
                batch.Add(reading);
                _state.Readings.Insert(0, reading);
            }
            while (_state.Readings.Count > 200) _state.Readings.RemoveAt(_state.Readings.Count - 1);
            _state.LastBatch = batch;
        }

        if (batch.Count > 0)
        {
            _db.SensorReadings.AddRange(batch);
            await UpdateLineBoardAsync(batch, ct);
            await _db.SaveChangesAsync(ct);
        }

        var response = Snapshot(simulated: false, source: "Live — POST /api/sensors/live");
        await _notifier.SensorReadingAsync(response, ct);
        return response;
    }

    public async Task<IngestResponse> IngestAsync(IngestRequest req, CancellationToken ct)
    {
        if (_state.Live && req.Simulate != true)
            return Snapshot(simulated: false, source: "Live — holding last POST /api/sensors/live readings");

        AdvanceSim(req.Tick);
        // Persist last batch into Postgres so the live page reflects history.
        var batch = _state.LastBatch ?? new List<SensorReading>();
        if (batch.Count > 0)
        {
            _db.SensorReadings.AddRange(batch);
            await _db.SaveChangesAsync(ct);
        }

        var response = Snapshot(simulated: true, source: "Simulated — no live PLC / Modbus / MQTT traffic");

        // Realtime broadcast (best-effort — the notifier swallows its own errors).
        await _notifier.SensorReadingAsync(response, ct);

        return response;
    }

    public Task<LatestReadingsResponse> LatestAsync(CancellationToken ct)
    {
        var sorted = _state.Readings
            .OrderByDescending(r => r.SimTick).ThenByDescending(r => r.Ts)
            .GroupBy(r => $"{r.Source}::{r.EntityId}")
            .Select(g => g.First())
            .OrderBy(r => r.Source).ThenBy(r => r.EntityId)
            .Select(ToDto).ToList();

        return Task.FromResult(new LatestReadingsResponse(
            Simulated: !_state.Live,
            Source: _state.Live
                ? "Live — latest accepted floor readings"
                : "Simulated — derived from server sensor ingest buffer",
            Count: sorted.Count,
            Readings: sorted
        ));
    }

    public Task<SimStatusResponse> StatusAsync(CancellationToken ct)
        => Task.FromResult(new SimStatusResponse(!_state.Live,
            _state.Live
                ? "Live — POST /api/sensors/live"
                : "Simulated — no live PLC / Modbus / MQTT traffic",
            _state.Tick, _state.Readings.Count, _state.Lines.Count, _state.Machines.Count));

    // ----------------------------------------------------------------------

    private IngestResponse Snapshot(bool simulated, string source)
    {
        var readings = _state.Readings.Take(50).Select(ToDto).ToList();
        return new IngestResponse(
            Simulated: simulated,
            Source: source,
            Tick: _state.Tick,
            Readings: readings,
            Lines: _state.Lines.Select(l => new LineSummaryDto(l.Id, l.Efficiency, l.Uptime, l.EnergyKwh)).ToList(),
            Machines: _state.Machines.Select(m => new MachineSummaryDto(m.Id, m.Vibration, m.Temperature, m.DutyCycle, m.Status)).ToList()
        );
    }

    private static string UnitFor(string metric) => metric switch
    {
        "vibration_rms" => "mm/s",
        "temperature_c" => "°C",
        "duty_cycle" or "efficiency" or "uptime" or "miss_rate" => "%",
        "kwh" or "energy_kwh" => "kWh",
        "scans_per_min" => "scans/min",
        "wip_bundles" => "bundles",
        _ => ""
    };

    private static void ApplyToFloor(SensorReading reading)
    {
        var metric = reading.Metric.ToLowerInvariant();
        var entity = reading.EntityId;

        if (entity.StartsWith("line-", StringComparison.OrdinalIgnoreCase))
        {
            var line = _state.Lines.FirstOrDefault(l => l.Id.Equals(entity, StringComparison.OrdinalIgnoreCase));
            if (line is null)
            {
                line = new LineSim { Id = entity, Efficiency = 0.7, Uptime = 1, EnergyKwh = 0 };
                _state.Lines.Add(line);
            }
            if (metric is "efficiency" or "efficiency_pct")
                line.Efficiency = reading.Value > 1 ? reading.Value / 100.0 : reading.Value;
            else if (metric == "uptime")
                line.Uptime = reading.Value > 1 ? reading.Value / 100.0 : reading.Value;
            else if (metric is "kwh" or "energy_kwh")
                line.EnergyKwh = reading.Value;
            else if (metric == "scans_per_min")
                line.Efficiency = Math.Clamp(reading.Value / 22.0, 0.4, 0.98);
        }
        else if (reading.Source == SensorSource.Telemetry)
        {
            var machine = _state.Machines.FirstOrDefault(m => m.Id.Equals(entity, StringComparison.OrdinalIgnoreCase));
            if (machine is null)
            {
                machine = new MachineSim { Id = entity, Status = "healthy" };
                _state.Machines.Add(machine);
            }
            if (metric == "vibration_rms") machine.Vibration = Math.Clamp(reading.Value, 0, 20);
            else if (metric == "temperature_c") machine.Temperature = Math.Clamp(reading.Value, 0, 120);
            else if (metric == "duty_cycle") machine.DutyCycle = Math.Clamp(reading.Value > 1 ? reading.Value / 100.0 : reading.Value, 0, 1);
            machine.Status = (machine.Vibration > 6 || machine.Temperature > 78) ? "down"
                : (machine.Vibration > 4.5 || machine.Temperature > 70) ? "at_risk"
                : "healthy";
        }
        else if (reading.Source == SensorSource.Energy && metric is "kwh" or "energy_kwh")
        {
            if (_state.Lines.Count > 0)
            {
                var share = reading.Value / _state.Lines.Count;
                foreach (var line in _state.Lines) line.EnergyKwh = Math.Round(share, 2);
            }
        }
    }

    private async Task UpdateLineBoardAsync(IReadOnlyList<SensorReading> batch, CancellationToken ct)
    {
        foreach (var reading in batch)
        {
            if (!reading.EntityId.StartsWith("line-", StringComparison.OrdinalIgnoreCase)) continue;
            var metric = reading.Metric.ToLowerInvariant();
            if (metric is not ("efficiency" or "efficiency_pct" or "scans_per_min")) continue;

            var row = await _db.LineBoardMetrics.FirstOrDefaultAsync(
                r => r.Id == reading.EntityId, ct);
            if (row is null) continue;

            double eff = metric == "scans_per_min"
                ? Math.Clamp(reading.Value / 22.0, 0.4, 0.98)
                : (reading.Value > 1 ? reading.Value / 100.0 : reading.Value);
            row.EfficiencyPct = (int)Math.Round(Math.Clamp(eff, 0, 1) * 100);
            row.Bottleneck = row.EfficiencyPct >= 75 ? Bottleneck.Green
                : row.EfficiencyPct >= 60 ? Bottleneck.Amber
                : Bottleneck.Red;
            row.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static void AdvanceSim(int? tickOverride)
    {
        lock (_gate)
        {
            int target = tickOverride.HasValue ? Math.Max(_state.Tick + 1, tickOverride.Value) : _state.Tick + 1;
            var batch = new List<SensorReading>();
            for (; _state.Tick < target; _state.Tick++)
            {
                batch.AddRange(StepTick());
            }
            _state.LastBatch = batch;
            // append to head, cap @ 200
            foreach (var r in batch.AsEnumerable().Reverse())
            {
                _state.Readings.Insert(0, r);
            }
            while (_state.Readings.Count > 200) _state.Readings.RemoveAt(_state.Readings.Count - 1);
        }
    }

    private static IEnumerable<SensorReading> StepTick()
    {
        // Tick-scoped RNG; readings use the global _state.NextId counter so IDs
        // are unique across the whole process (multiple StepTicks per Ingest).
        // When the simulator sets SimulatorSeedOverride != 0, the seed is
        // XOR'd in so two runs with the same seed produce identical
        // readings (modulo timestamps and ids).
        var ts = DateTime.UtcNow;
        var rng = new Random((0xA11CE ^ (_state.Tick + 1)) ^ SimulatorSeedOverride);

        var profiles = new (SensorSource Source, string Metric, string Unit, double Lo, double Hi, string Entity)[]
        {
            (SensorSource.Rfid,      "scans_per_min", "scans/min", 8,   22,  Pick(rng, "gate-A1","gate-A2","gate-B1","gate-C1")),
            (SensorSource.Telemetry, "vibration_rms", "mm/s",     0.8, 4.5, Pick(rng, "M-101","M-102","M-103","M-201","M-202","M-301","M-302")),
            (SensorSource.Energy,    "kwh",           "kWh",      42,  78,  Pick(rng, "meter:floor-1","meter:floor-2","meter:floor-3")),
        };

        var batch = new List<SensorReading>();
        foreach (var p in profiles)
        {
            double v = Math.Round(p.Lo + rng.NextDouble() * (p.Hi - p.Lo), 2);
            var reading = new SensorReading
            {
                Id = $"srv-sensor-{++_state.NextId}",
                Source = p.Source,
                EntityId = p.Entity,
                Metric = p.Metric,
                Value = v,
                Unit = p.Unit,
                Ts = ts,
                SimTick = _state.Tick
            };
            batch.Add(reading);

            if (p.Source == SensorSource.Rfid)
            {
                var l = _state.Lines[rng.Next(_state.Lines.Count)];
                l.Efficiency = Math.Clamp(l.Efficiency + (rng.NextDouble() - 0.5) * 0.04, 0.4, 0.95);
                l.Uptime     = Math.Clamp(l.Uptime + (rng.NextDouble() - 0.5) * 0.01, 0.7, 1.0);
            }
            else if (p.Source == SensorSource.Telemetry)
            {
                var m = _state.Machines.FirstOrDefault(x => x.Id == p.Entity);
                if (m is not null)
                {
                    if (p.Metric == "vibration_rms") m.Vibration = Math.Clamp(v, 0.5, 8);
                    else if (p.Metric == "temperature_c") m.Temperature = Math.Clamp(v, 40, 90);
                    else if (p.Metric == "duty_cycle") m.DutyCycle = Math.Clamp(v / 100, 0.2, 1);
                    m.Status = (m.Vibration > 6 || m.Temperature > 78) ? "down"
                              : (m.Vibration > 4.5 || m.Temperature > 70) ? "at_risk"
                              : "healthy";
                }
            }
            else if (p.Source == SensorSource.Energy)
            {
                foreach (var l in _state.Lines) l.EnergyKwh = Math.Round(l.EnergyKwh + v / _state.Lines.Count, 2);
            }
        }
        return batch;
    }

    private static string Pick(Random rng, params string[] xs) => xs[rng.Next(xs.Length)];

    private static SensorReadingDto ToDto(SensorReading r) => new(
        r.Id, r.Source.ToString().ToLowerInvariant(),
        r.EntityId, r.Metric, r.Value, r.Unit, r.Ts, r.SimTick);

    private static SimState Seed()
    {
        var rng = new Random(unchecked((int)0xFA47_0001));
        var lines = LINES.Select(id => new LineSim { Id = id,
            Efficiency = Math.Round(0.62 + rng.NextDouble() * 0.28, 3),
            Uptime     = Math.Round(0.86 + rng.NextDouble() * 0.13, 3),
            EnergyKwh  = 0 }).ToList();

        var machines = MACHINES.Select(id => {
            int wear = rng.Next(100);
            double vibration = Math.Round(1.5 + wear / 100.0 * 5.5, 2);
            double temperature = Math.Round(55 + wear / 100.0 * 25, 1);
            double duty = Math.Round(0.55 + rng.NextDouble() * 0.4, 3);
            string status = wear > 80 ? "down" : wear > 60 ? "at_risk" : "healthy";
            return new MachineSim { Id = id, Vibration = vibration, Temperature = temperature, DutyCycle = duty, Status = status };
        }).ToList();

        return new SimState(0, 1, lines, machines, new List<SensorReading>(), null);
    }

    private sealed class SimState
    {
        public int Tick;
        public int NextId;
        public List<LineSim> Lines;
        public List<MachineSim> Machines;
        public List<SensorReading> Readings;
        public bool Live;
        public List<SensorReading>? LastBatch;
        public SimState(int tick, int nextId, List<LineSim> lines, List<MachineSim> machines,
            List<SensorReading> readings, List<SensorReading>? lastBatch)
        { Tick = tick; NextId = nextId; Lines = lines; Machines = machines; Readings = readings; LastBatch = lastBatch; Live = false; }
    }
    private sealed class LineSim    { public string Id = default!; public double Efficiency { get; set; } public double Uptime { get; set; } public double EnergyKwh { get; set; } }
    private sealed class MachineSim { public string Id = default!; public double Vibration { get; set; } public double Temperature { get; set; } public double DutyCycle { get; set; } public string Status { get; set; } = "healthy"; }
}
