using System.Net;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Ask and agent runs answer only from the Python RAG service (faked by the
/// test host at <see cref="DbApiFactory.FakeRagHost"/>): its answer and cited
/// sources are passed through, nothing is invented, and a failure is an error,
/// never a substitute answer.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbRagServiceAnswerTests
{
    private readonly PostgresStack _stack;
    public DbRagServiceAnswerTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    [DockerFact]
    public async Task A_cited_rag_answer_is_live_with_its_sources()
    {
        using var viewer = Rag.Client(Api, Roles.Viewer);
        var a = await Rag.Ask(viewer, "when do I replace a bearing?");

        Assert.Equal("live", a.GetProperty("mode").GetString());
        Assert.Equal(DbApiFactory.FakeRagAnswer, a.GetProperty("finding").GetString());
        Assert.Equal(DbApiFactory.FakeRagScore, a.GetProperty("confidence").GetDouble(), 3);
        var c = Assert.Single(a.GetProperty("citations").EnumerateArray());
        Assert.Equal($"{DbApiFactory.FakeRagSourceFile} · page 2", c.GetProperty("title").GetString());
        Assert.Equal("doc-test:0", c.GetProperty("id").GetString());
        Assert.Equal(DbApiFactory.FakeRagCollection, c.GetProperty("source").GetString());
        // Nothing the service didn't return.
        Assert.False(a.TryGetProperty("recommendation", out var rec) && rec.ValueKind != JsonValueKind.Null);
        Assert.Empty(a.GetProperty("factors").EnumerateArray());

        var (question, key) = Api.RagQuestions.Last();
        Assert.Equal("when do I replace a bearing?", question);
        Assert.Equal(DbApiFactory.FakeRagKey, key);
        Assert.Empty(Api.Http.Blocked);
    }

    [DockerFact]
    public async Task An_answer_citing_nothing_is_no_source_with_no_score()
    {
        using var viewer = Rag.Client(Api, Roles.Viewer);
        var a = await Rag.Ask(viewer, $"{DbApiFactory.RagNoSourceMarker} purple volcano tax");

        Assert.Equal("no_source", a.GetProperty("mode").GetString());
        Assert.Equal(DbApiFactory.FakeRagNoSourceAnswer, a.GetProperty("finding").GetString());
        Assert.Equal(0, a.GetProperty("confidence").GetDouble());
        Assert.Empty(a.GetProperty("citations").EnumerateArray());
    }

    [DockerFact]
    public async Task A_failed_rag_call_is_503_never_an_answer()
    {
        using var viewer = Rag.Client(Api, Roles.Viewer);
        using var res = await viewer.PostAsync("/api/ask", Rag.Json(new { query = $"bearing {DbApiFactory.ForceRagFailure}" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("Gemini quota exceeded", await res.Content.ReadAsStringAsync());
    }

    [DockerFact]
    public async Task Status_names_the_rag_service_collection_and_models()
    {
        using var viewer = Rag.Client(Api, Roles.Viewer);
        using var doc = JsonDocument.Parse(await viewer.GetStringAsync("/api/ask/status"));
        var s = doc.RootElement;
        Assert.True(s.GetProperty("reachable").GetBoolean());
        Assert.True(s.GetProperty("ready").GetBoolean());
        Assert.Equal(DbApiFactory.FakeRagCollection, s.GetProperty("collection").GetString());
    }

    [DockerFact]
    public async Task An_agent_run_stores_the_rag_answer_its_sources_and_the_readings_it_asked_about()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        using var res = await op.PostAsync("/api/agents/maintenance-agent/run", Rag.Json(new { }));
        var raw = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK,
            $"{(int)res.StatusCode}: {raw}\n{string.Join("\n", Api.Logs.Lines.Where(l => l.Contains("Exception")).TakeLast(3))}");
        using var body = JsonDocument.Parse(raw);
        var run = body.RootElement;

        Assert.Equal("rag", run.GetProperty("source").GetString());
        var insightId = run.GetProperty("insight").GetProperty("id").GetString()!;
        Assert.Equal(DbApiFactory.FakeRagAnswer, run.GetProperty("insight").GetProperty("finding").GetString());
        Assert.Equal(DbApiFactory.FakeRagScore, run.GetProperty("insight").GetProperty("confidence").GetDouble(), 3);
        Assert.True(run.TryGetProperty("energyInsight", out var e) is false || e.ValueKind == JsonValueKind.Null);

        // The question carried the agent's configured purpose and its readings.
        var (question, _) = Api.RagQuestions.Last();
        var purpose = await Rag.WithDb(Api, db => db.Agents.AsNoTracking().Where(a => a.Id == "maintenance-agent").Select(a => a.Purpose).SingleAsync());
        Assert.StartsWith($"Role: {purpose}", question);

        var stored = await Rag.WithDb(Api, db => db.Insights.AsNoTracking().SingleAsync(i => i.Id == insightId));
        Assert.Equal(DbApiFactory.FakeRagAnswer, stored.Finding);
        var evidence = Assert.Single(stored.Evidence);
        Assert.Equal(1, evidence.Count);
        Assert.Contains($"{DbApiFactory.FakeRagSourceFile} p.2", evidence.PreviewIds!);
        // Factors are exactly the readings that were put in the question.
        Assert.All(stored.Factors, f => Assert.Contains($"- {f.Label}: {f.Magnitude}", question));
    }

    [DockerFact]
    public async Task The_agent_roster_reports_real_run_counts_and_the_rag_model()
    {
        using var viewer = Rag.Client(Api, Roles.Viewer);
        using var doc = JsonDocument.Parse(await viewer.GetStringAsync("/api/agents"));
        foreach (var a in doc.RootElement.EnumerateArray())
        {
            Assert.StartsWith("RAG · ", a.GetProperty("model").GetString());
            Assert.Contains($"({DbApiFactory.FakeRagCollection})", a.GetProperty("model").GetString());
            Assert.Equal("ready", a.GetProperty("status").GetString());
            var id = a.GetProperty("id").GetString()!;
            var total = await Rag.WithDb(Api, db => db.Insights.CountAsync(i => i.AgentId == id));
            Assert.Equal(total, a.GetProperty("recentCount").GetInt32());
        }
    }
}
