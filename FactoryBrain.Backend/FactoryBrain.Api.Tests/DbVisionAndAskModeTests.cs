using System.Net;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Api.Tests;

/// <summary>Vision analysis persistence and floor-alert labelling on the real database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DbVisionTests
{
    private readonly PostgresStack _stack;
    public DbVisionTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    [DockerFact]
    public async Task Vision_analysis_persists_the_alert_and_approval_it_returns()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        using var res = await op.PostAsync("/api/vision/analyze", Rag.Json(new { sampleFile = "defect-1-stitch-skip.jpg" }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var alertId = body.RootElement.GetProperty("floorAlertId").GetString()!;
        var insightId = body.RootElement.GetProperty("insight").GetProperty("id").GetString()!;
        Assert.True(body.RootElement.GetProperty("simulated").GetBoolean()); // honest: no real model

        // The returned alert id exists and is listed.
        using var alerts = JsonDocument.Parse(await op.GetStringAsync("/api/floor-alerts"));
        Assert.Contains(alerts.RootElement.GetProperty("alerts").EnumerateArray(),
            a => a.GetProperty("id").GetString() == alertId && a.GetProperty("insightId").GetString() == insightId);

        // …and its approval is in the server-side queue.
        using var approvals = JsonDocument.Parse(await op.GetStringAsync("/api/approvals?status=pending&limit=200"));
        Assert.Contains(approvals.RootElement.GetProperty("items").EnumerateArray(),
            a => a.GetProperty("insightId").GetString() == insightId);

        Assert.True(await Rag.WithDb(Api, db => db.VisionResults.AnyAsync(v => v.Id == body.RootElement.GetProperty("result").GetProperty("id").GetString())));
    }

    [DockerFact]
    public async Task Concurrent_vision_analyses_get_distinct_ids()
    {
        using var op = Rag.Client(Api, Roles.Operator);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            op.PostAsync("/api/vision/analyze", Rag.Json(new { sampleFile = "defect-3-seam-pucker.jpg" }))));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = new List<string>();
        foreach (var r in responses)
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            ids.Add(doc.RootElement.GetProperty("result").GetProperty("id").GetString()!);
            r.Dispose();
        }
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [DockerFact]
    public async Task Floor_alert_source_label_describes_what_the_data_really_is()
    {
        using var viewer = Rag.Client(Api, Roles.Viewer);
        using var doc = JsonDocument.Parse(await viewer.GetStringAsync("/api/floor-alerts"));
        var source = doc.RootElement.GetProperty("source").GetString()!;
        Assert.DoesNotContain("src/data", source);
        Assert.Contains("no message is sent", source);
    }
}

/// <summary>Ask's <c>mode</c> must only be <c>live</c> when a language model actually answered.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DbAskModeTests
{
    private readonly PostgresStack _stack;
    public DbAskModeTests(PostgresStack stack) { _stack = stack; }

    [DockerFact]
    public async Task Without_a_model_the_answer_is_demo_with_no_invented_content()
    {
        using var viewer = Rag.Client(_stack.Local, Roles.Viewer);
        var a = await Rag.Ask(viewer, "needle breakage on lockstitch machines");

        Assert.Equal("demo", a.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, a.TryGetProperty("warning", out var w) ? w.ValueKind : JsonValueKind.Null);
        Assert.Empty(a.GetProperty("factors").EnumerateArray());
        Assert.Equal(0, a.GetProperty("confidence").GetDouble());
        var finding = a.GetProperty("finding").GetString()!;
        Assert.StartsWith("Demo mode", finding);
        Assert.DoesNotContain("−12%", finding);
        Assert.DoesNotContain("helpers", a.GetProperty("recommendation").GetProperty("action").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(a.GetProperty("citations").EnumerateArray());
        Assert.Empty(_stack.Local.Http.Blocked);
    }

    [DockerFact]
    public async Task Nothing_retrieved_is_no_source_not_live()
    {
        using var viewer = Rag.Client(_stack.Local, Roles.Viewer);
        var a = await Rag.Ask(viewer, "zzqx purple volcano tax");
        Assert.Equal("no_source", a.GetProperty("mode").GetString());
    }

    [DockerFact]
    public async Task A_model_answer_is_live()
    {
        using var viewer = Rag.Client(_stack.Mocked, Roles.Viewer);
        var a = await Rag.Ask(viewer, "needle breakage on lockstitch machines");
        Assert.Equal("live", a.GetProperty("mode").GetString());
        Assert.Equal(DbApiFactory.FakeLlmFinding, a.GetProperty("finding").GetString());
    }

    [DockerFact]
    public async Task A_failed_model_call_is_demo_with_a_warning_never_live()
    {
        using var viewer = Rag.Client(_stack.Mocked, Roles.Viewer);
        var a = await Rag.Ask(viewer, $"needle breakage {DbApiFactory.ForceLlmFailure}");

        Assert.Equal("demo", a.GetProperty("mode").GetString());
        Assert.Contains("language model call failed", a.GetProperty("warning").GetString());
        Assert.Equal(0, a.GetProperty("confidence").GetDouble());
        Assert.NotEmpty(a.GetProperty("citations").EnumerateArray());
        // The failing call went to the fake, nowhere else.
        Assert.Empty(_stack.Mocked.Http.Blocked);
    }
}
