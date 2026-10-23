using FactoryBrain.Domain.Enums;

namespace FactoryBrain.Domain.Entities;

/// <summary>
/// A single simulated or live IoT reading. Mirrors
/// <c>src/data/sensors.ts::SensorReading</c>.
/// </summary>
public class SensorReading
{
    public string Id { get; set; } = default!;
    public SensorSource Source { get; set; }
    public string EntityId { get; set; } = default!; // machineId / lineId / "meter:floor-N"
    public string Metric { get; set; } = default!;   // vibration_rms / temperature_c / kwh / ...
    public double Value { get; set; }
    public string Unit { get; set; } = default!;
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public int SimTick { get; set; }
}
