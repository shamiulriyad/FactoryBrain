namespace FactoryBrain.Application.Abstractions.Interfaces;

/// <summary>
/// Thrown by <see cref="ISensorService"/> when a caller asks the simulation
/// to advance further than one request is allowed to. Controllers map it to
/// a 400 ValidationProblemDetails on the <c>tick</c> field. Raised before any
/// state is mutated, so a rejected request has no side effects.
/// </summary>
public sealed class SimulationLimitException : Exception
{
    public SimulationLimitException(string message) : base(message) { }
}
