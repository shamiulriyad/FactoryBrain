namespace FactoryBrain.Application.Abstractions;

/// <summary>
/// Result of a reindex run. Lives in the Application layer because it is
/// part of the <see cref="IRagService"/> contract; Infrastructure
/// implementations produce one but the Application interface consumes it.
/// Safe to serialize, never includes the API key.
/// </summary>
public sealed record ReindexReport(int Documents, int Chunks, string Provider, string Model, int Dims);
