using System.Net;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// The retired simulated vision demo and floor-alert labelling on the real
/// database. Real image inspection is covered by DbQcInspectionTests.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbVisionTests
{
    private readonly PostgresStack _stack;
    public DbVisionTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    [DockerFact]
    public async Task Retired_vision_demo_answers_410_and_creates_no_results_approvals_or_alerts()
    {
        var resultsBefore = await Rag.WithDb(Api, db => db.VisionResults.CountAsync());
        var approvalsBefore = await Rag.WithDb(Api, db => db.Approvals.CountAsync());
        var alertsBefore = await Rag.WithDb(Api, db => db.FloorAlerts.CountAsync());

        using var op = Rag.Client(Api, Roles.Operator);
        using var post = await op.PostAsync("/api/vision/analyze", Rag.Json(new { sampleFile = "defect-1-stitch-skip.jpg" }));
        Assert.Equal(HttpStatusCode.Gone, post.StatusCode);
        using var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Assert.Equal("vision_demo_retired", body.RootElement.GetProperty("error").GetString());
        Assert.Equal("/api/qc/inspections", body.RootElement.GetProperty("replacement").GetString());

        using var get = await op.GetAsync("/api/vision/analyze");
        Assert.Equal(HttpStatusCode.Gone, get.StatusCode);

        Assert.Equal(resultsBefore, await Rag.WithDb(Api, db => db.VisionResults.CountAsync()));
        Assert.Equal(approvalsBefore, await Rag.WithDb(Api, db => db.Approvals.CountAsync()));
        Assert.Equal(alertsBefore, await Rag.WithDb(Api, db => db.FloorAlerts.CountAsync()));
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
