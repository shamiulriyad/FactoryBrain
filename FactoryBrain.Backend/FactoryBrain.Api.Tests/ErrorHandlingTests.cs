using System.Net;
using System.Text;
using FactoryBrain.Api.Middleware;

namespace FactoryBrain.Api.Tests;

/// <summary>Safe failure responses that don't need a database.</summary>
[Collection(ApiCollection.Name)]
public sealed class ErrorHandlingTests
{
    private readonly ApiFactory _factory;
    public ErrorHandlingTests(ApiFactory factory) { _factory = factory; factory.Activate(); }

    [Fact]
    public async Task Reindex_with_the_database_down_fails_closed_with_a_generic_error_and_trace_id()
    {
        // The test host's database is a closed loopback port. The reindex now
        // reads the settings first, which fails with a real Npgsql error; the
        // response is a generic 503, never the exception text. (A database
        // failure inside the reindex transaction itself → 500 DatabaseError
        // is covered against a real database in DbEmbeddingSettingsTests.)
        using var admin = _factory.ClientAs(Roles.Admin);
        using var res = await admin.PostAsync("/api/rag/reindex", new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("\"error\":\"settings_unavailable\"", body);
        Assert.Contains("\"traceId\":", body);
        foreach (var leak in new[] { "127.0.0.1", "Port", "Npgsql", "Exception", "refused", "factorybrain_tests", "Host=" })
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("line--1")]
    [InlineData("-")]
    [InlineData("line-")]
    [InlineData("Line 1; drop")]
    public async Task Malformed_qc_line_ids_are_a_400_not_a_500(string lineId)
    {
        using var op = _factory.ClientAs(Roles.Operator);
        var json = $$"""{"operation":"sewing","lineId":"{{lineId}}","defectRatePct":4}""";
        using var res = await op.PostAsync("/api/qc/flag", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("lineId", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
