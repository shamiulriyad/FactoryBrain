using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Server-side approvals and the append-only audit log, on the disposable
/// PostgreSQL test database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbApprovalTests
{
    private readonly PostgresStack _stack;
    public DbApprovalTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    private sealed record Person(Guid Id, string Email, string Role, HttpClient Client);

    private Person As(string role)
    {
        var id = Guid.NewGuid();
        var email = $"{role.ToLowerInvariant()}-{id:N}@approvals.test";
        var token = Api.Services.GetRequiredService<ITokenService>()
            .IssueAccessToken(new User { Id = id, Email = email, Role = role }, out _);
        var client = Api.RawClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new Person(id, email, role, client);
    }

    private async Task<string> SeedPendingApprovalAsync(RiskTier risk)
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>();
        db.Insights.Add(new Insight
        {
            Id = $"ins-test-{suffix}", AgentId = "maintenance-agent", AgentLabel = "Maintenance & Uptime Agent",
            Title = $"Replace bearing {suffix[..6]}", TitleBn = "বিয়ারিং প্রতিস্থাপন",
            Finding = "Vibration trending up.", FindingBn = "কম্পন বাড়ছে।",
            Recommendation = new InsightRecommendation
            {
                Title = "Replace bearing", TitleBn = "বিয়ারিং প্রতিস্থাপন",
                Action = "Schedule bearing replacement", ActionBn = "প্রতিস্থাপন নির্ধারণ",
                RiskTier = risk, TargetStage = Stage.PendingApproval,
            },
            Stage = Stage.PendingApproval, Confidence = 0.8,
        });
        db.Approvals.Add(new ApprovalRequest
        {
            Id = $"apr-test-{suffix}", InsightId = $"ins-test-{suffix}",
            Title = $"Replace bearing {suffix[..6]}", TitleBn = "বিয়ারিং প্রতিস্থাপন",
            Reason = "Risk tier != low", RiskTier = risk, Stage = Stage.PendingApproval,
        });
        await db.SaveChangesAsync();
        return $"apr-test-{suffix}";
    }

    private Task<T> Db<T>(Func<FactoryBrainDbContext, Task<T>> q) => Rag.WithDb(Api, q);

    private static Task<HttpResponseMessage> Decide(Person who, string id, string verb, string? reason = null) =>
        who.Client.PostAsync($"/api/approvals/{id}/{verb}", Rag.Json(new { reason }));

    [DockerFact]
    public async Task Pending_approvals_are_served_from_the_database_with_their_insight()
    {
        var id = await SeedPendingApprovalAsync(RiskTier.Medium);
        var viewer = As(Roles.Viewer);

        using var doc = JsonDocument.Parse(await viewer.Client.GetStringAsync("/api/approvals?status=pending"));
        var item = doc.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);
        Assert.Equal("pending_approval", item.GetProperty("status").GetString());
        Assert.Equal("medium", item.GetProperty("riskTier").GetString());
        Assert.Equal("pending_approval", item.GetProperty("insight").GetProperty("stage").GetString());
        Assert.Equal("medium", item.GetProperty("insight").GetProperty("recommendation").GetProperty("riskTier").GetString());
        Assert.True(doc.RootElement.GetProperty("pendingCount").GetInt32() >= 1);
    }

    [DockerFact]
    public async Task Operator_approval_updates_both_rows_and_writes_an_audit_entry()
    {
        var id = await SeedPendingApprovalAsync(RiskTier.Medium);
        var op = As(Roles.Operator);

        using var res = await Decide(op, id, "approve", "Parts are in stock.");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("approved", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(op.Email, body.RootElement.GetProperty("decidedByEmail").GetString());
        Assert.Equal("approved", body.RootElement.GetProperty("insight").GetProperty("stage").GetString());

        var approval = await Db(db => db.Approvals.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Equal(Stage.Approved, approval.Stage);
        Assert.Equal(op.Id, approval.DecidedByUserId);
        Assert.NotNull(approval.DecidedAt);
        Assert.Equal(Stage.Approved, (await Db(db => db.Insights.AsNoTracking().SingleAsync(i => i.Id == approval.InsightId))).Stage);

        var audit = await Db(db => db.AuditLog.AsNoTracking().Where(e => e.EntityId == id).ToListAsync());
        var entry = Assert.Single(audit);
        Assert.Equal(("approval.approved", "approval", "pending_approval", "approved"),
                     (entry.Action, entry.EntityType, entry.FromState, entry.ToState));
        Assert.Equal((op.Id, op.Email, Roles.Operator), (entry.ActorUserId!.Value, entry.ActorEmail, entry.ActorRole));
        Assert.Equal("Parts are in stock.", entry.Reason);
        Assert.False(string.IsNullOrEmpty(entry.TraceId));
    }

    [DockerFact]
    public async Task A_decided_approval_cannot_be_decided_again()
    {
        var id = await SeedPendingApprovalAsync(RiskTier.Low);
        var op = As(Roles.Operator);
        Assert.Equal(HttpStatusCode.OK, (await Decide(op, id, "approve")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await Decide(op, id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(As(Roles.Admin), id, "reject", "changed my mind")).StatusCode);
        Assert.Single(await Db(db => db.AuditLog.AsNoTracking().Where(e => e.EntityId == id).ToListAsync()));
    }

    [DockerFact]
    public async Task Rejection_requires_a_reason_which_is_recorded()
    {
        var id = await SeedPendingApprovalAsync(RiskTier.Medium);
        var op = As(Roles.Operator);

        using (var missing = await Decide(op, id, "reject", "   "))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Contains("reason", await missing.Content.ReadAsStringAsync());
        }
        Assert.Equal(Stage.PendingApproval, (await Db(db => db.Approvals.AsNoTracking().SingleAsync(a => a.Id == id))).Stage);

        Assert.Equal(HttpStatusCode.OK, (await Decide(op, id, "reject", "Duplicate of an open work order.")).StatusCode);
        var approval = await Db(db => db.Approvals.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Equal((Stage.Rejected, "Duplicate of an open work order."), (approval.Stage, approval.DecisionReason));
        var entry = Assert.Single(await Db(db => db.AuditLog.AsNoTracking().Where(e => e.EntityId == id).ToListAsync()));
        Assert.Equal(("approval.rejected", "rejected"), (entry.Action, entry.ToState));
    }

    [DockerFact]
    public async Task High_risk_approvals_need_an_admin()
    {
        var id = await SeedPendingApprovalAsync(RiskTier.High);

        using (var res = await Decide(As(Roles.Operator), id, "approve"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Contains("Admin", await res.Content.ReadAsStringAsync());
        }
        Assert.Equal(Stage.PendingApproval, (await Db(db => db.Approvals.AsNoTracking().SingleAsync(a => a.Id == id))).Stage);
        Assert.Empty(await Db(db => db.AuditLog.AsNoTracking().Where(e => e.EntityId == id).ToListAsync()));

        var admin = As(Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await Decide(admin, id, "approve")).StatusCode);
        Assert.Equal(Roles.Admin, (await Db(db => db.AuditLog.AsNoTracking().SingleAsync(e => e.EntityId == id))).ActorRole);
    }

    [DockerFact]
    public async Task Concurrent_decisions_produce_exactly_one_winner()
    {
        var id = await SeedPendingApprovalAsync(RiskTier.Medium);
        var reviewers = Enumerable.Range(0, 6).Select(_ => As(Roles.Operator)).ToList();

        var responses = await Task.WhenAll(reviewers.Select((r, i) => Decide(r, id, i % 2 == 0 ? "approve" : "reject", "race")));
        var statuses = responses.Select(r => r.StatusCode).ToList();
        foreach (var r in responses) r.Dispose();

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.All(statuses.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Conflict, s));
        Assert.Single(await Db(db => db.AuditLog.AsNoTracking().Where(e => e.EntityId == id).ToListAsync()));
    }

    [DockerFact]
    public async Task Audit_log_rejects_update_delete_and_truncate_in_the_database()
    {
        var id = await SeedPendingApprovalAsync(RiskTier.Low);
        Assert.Equal(HttpStatusCode.OK, (await Decide(As(Roles.Operator), id, "approve")).StatusCode);

        foreach (var sql in new[]
        {
            $"UPDATE audit_log SET \"ActorEmail\" = 'tampered@x' WHERE \"EntityId\" = '{id}'",
            $"DELETE FROM audit_log WHERE \"EntityId\" = '{id}'",
            "TRUNCATE audit_log",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Db(db => db.Database.ExecuteSqlRawAsync(sql)));
            Assert.Contains("append-only", ex.MessageText);
        }
        var entry = await Db(db => db.AuditLog.AsNoTracking().SingleAsync(e => e.EntityId == id));
        Assert.NotEqual("tampered@x", entry.ActorEmail);
    }

    [DockerFact]
    public async Task Admin_can_read_the_audit_trail_for_an_approval()
    {
        var id = await SeedPendingApprovalAsync(RiskTier.Medium);
        var op = As(Roles.Operator);
        Assert.Equal(HttpStatusCode.OK, (await Decide(op, id, "approve")).StatusCode);

        using var doc = JsonDocument.Parse(await As(Roles.Admin).Client.GetStringAsync($"/api/audit?entityType=approval&entityId={id}"));
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(op.Email, entry.GetProperty("actorEmail").GetString());
        Assert.Equal("approval.approved", entry.GetProperty("action").GetString());
    }

    [DockerFact]
    public async Task Unknown_ids_and_bad_status_filters_are_rejected()
    {
        var op = As(Roles.Operator);
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(op, "apr-does-not-exist", "approve")).StatusCode);
        using var bad = await op.Client.GetAsync("/api/approvals?status=everything");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [DockerFact]
    public async Task Agent_run_approval_flows_through_the_api_and_the_brief_count_drops_after_a_decision()
    {
        var op = As(Roles.Operator);
        // maintenance-agent is configured as medium risk, so its run queues an approval.
        using var run = await op.Client.PostAsync("/api/agents/maintenance-agent/run", Rag.Json(new { }));
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        using var runBody = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        Assert.True(runBody.RootElement.GetProperty("approvalPending").GetBoolean());
        var insightId = runBody.RootElement.GetProperty("insight").GetProperty("id").GetString();

        using var list = JsonDocument.Parse(await op.Client.GetStringAsync("/api/approvals?status=pending&limit=200"));
        var approvalId = list.RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("insightId").GetString() == insightId).GetProperty("id").GetString()!;

        async Task<int> BriefPending()
        {
            using var brief = JsonDocument.Parse(await op.Client.GetStringAsync("/api/brief/morning"));
            return brief.RootElement.GetProperty("pendingApprovals").GetInt32();
        }
        var before = await BriefPending();
        Assert.Equal(HttpStatusCode.OK, (await Decide(op, approvalId, "approve")).StatusCode);
        Assert.Equal(before - 1, await BriefPending());
    }
}
