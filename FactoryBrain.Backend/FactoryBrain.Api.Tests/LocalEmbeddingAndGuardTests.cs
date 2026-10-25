using FactoryBrain.Infrastructure.Rag;

namespace FactoryBrain.Api.Tests;

/// <summary>Local hash embedder (the no-key, no-network RAG path) in isolation.</summary>
public sealed class LocalEmbeddingTests
{
    [Fact]
    public void Local_embedder_produces_deterministic_unit_vectors_of_384_dims()
    {
        var embedder = new HashEmbeddingService();
        Assert.Equal(("local", "hash-md5", 384), (embedder.ProviderId, embedder.ModelId, embedder.Dimensions));

        var a1 = embedder.Embed("needle breakage on line 3").ToArray();
        var a2 = embedder.Embed("needle breakage on line 3").ToArray();
        var b  = embedder.Embed("compressor duty cycle").ToArray();

        Assert.Equal(384, a1.Length);
        Assert.Equal(a1, a2);                               // deterministic
        Assert.NotEqual(a1, b);                             // input-dependent
        Assert.InRange(Math.Sqrt(a1.Sum(x => (double)x * x)), 0.999, 1.001); // L2-normalised
    }

    [Fact]
    public void Local_embedder_honours_a_configured_dimension()
    {
        Assert.Equal(768, new HashEmbeddingService(768).Embed("x").ToArray().Length);
    }
}

/// <summary>The IntegrationTesting environment must never run against a non-local database.</summary>
public sealed class IntegrationTestingGuardTests
{
    [Theory]
    [InlineData("Host=aws-0-ap-south-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.example;Password=not-a-real-password")]
    [InlineData("Host=10.0.0.5;Database=factorybrain;Username=u;Password=p")]
    public void Host_refuses_to_start_against_a_remote_database(string remote)
    {
        // The guard runs before any DbContext or connection is created, so
        // nothing is sent to the (fictitious) remote host.
        using var factory = DbApiFactory.UncheckedForGuardTest(remote);
        var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
        var guard = ex as InvalidOperationException ?? ex.InnerException as InvalidOperationException;
        Assert.NotNull(guard);
        Assert.Contains("IntegrationTesting only runs against a loopback database", guard!.Message);
        Assert.Empty(factory.Http.Handled);
        Assert.Empty(factory.Http.Blocked);
    }
}
