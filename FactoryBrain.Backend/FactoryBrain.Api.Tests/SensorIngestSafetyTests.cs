using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Validation;
using FactoryBrain.Infrastructure.Services;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Input bounds, read-only polling and rate limits on the sensor endpoints.
/// Every request here is decided before the service writes to the database
/// (validation, the tick limit, or the rate limiter), and none of them
/// advances the shared simulation state.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SensorIngestSafetyTests
{
    private readonly ApiFactory _factory;
    public SensorIngestSafetyTests(ApiFactory factory) { _factory = factory; factory.Activate(); }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<int> CurrentTick()
    {
        using var viewer = _factory.ClientAs(Roles.Viewer);
        using var doc = JsonDocument.Parse(await viewer.GetStringAsync("/api/sensors/snapshot"));
        return doc.RootElement.GetProperty("tick").GetInt32();
    }

    private static async Task<JsonElement> Errors(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errors").Clone();
    }

    [Theory]
    [InlineData("/api/sensors/ingest")]
    [InlineData("/api/line-board/refresh")]
    public async Task Tick_above_the_absolute_ceiling_is_rejected(string route)
    {
        using var client = _factory.ClientAs(Roles.Operator);
        using var response = await client.PostAsync(route, Json($$"""{"tick":{{SensorInputLimits.MaxTick + 1}}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Errors(response)).TryGetProperty("tick", out _));
    }

    [Theory]
    [InlineData("/api/sensors/ingest", 2_000_000_000)]
    [InlineData("/api/line-board/refresh", -1)]
    public async Task Out_of_range_tick_is_rejected(string route, int tick)
    {
        using var client = _factory.ClientAs(Roles.Operator);
        using var response = await client.PostAsync(route, Json($$"""{"tick":{{tick}}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/sensors/ingest")]
    [InlineData("/api/line-board/refresh")]
    public async Task Tick_too_far_ahead_is_rejected_quickly_without_advancing(string route)
    {
        var before = await CurrentTick();
        using var client = _factory.ClientAs(Roles.Operator);

        // Inside the absolute ceiling, but far beyond one request's budget.
        // Before the fix this looped ~5 million times under the global lock.
        var target = before + SensorService.MaxTicksPerRequest + 5_000_000;
        var sw = Stopwatch.StartNew();
        using var response = await client.PostAsync(route, Json($$"""{"tick":{{target}}}"""));
        sw.Stop();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("tick", (await Errors(response)).GetProperty("tick")[0].GetString());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"rejection took {sw.Elapsed}");
        Assert.Equal(before, await CurrentTick());
    }

    [Fact]
    public async Task Polling_the_snapshot_never_advances_the_simulation()
    {
        using var client = _factory.ClientAs(Roles.Viewer);
        var ticks = new List<int>();
        var counts = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            using (var snapshot = JsonDocument.Parse(await client.GetStringAsync("/api/sensors/snapshot")))
                ticks.Add(snapshot.RootElement.GetProperty("tick").GetInt32());
            using (var status = JsonDocument.Parse(await client.GetStringAsync("/api/sensors/ingest")))
                counts.Add(status.RootElement.GetProperty("readingsCount").GetInt32());
        }
        Assert.Single(ticks.Distinct());
        Assert.Single(counts.Distinct());
    }

    [Fact]
    public async Task Viewer_cannot_advance_the_simulation()
    {
        using var client = _factory.ClientAs(Roles.Viewer);
        using var response = await client.PostAsync("/api/sensors/ingest", Json("{}"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public static IEnumerable<object[]> InvalidLiveBatches()
    {
        string Reading(string extra = "", string entity = "line-1", string value = "70")
            => $$"""{"source":"rfid","entityId":"{{entity}}","metric":"efficiency","value":{{value}}{{extra}}}""";

        var oversized = "[" + string.Join(",", Enumerable.Repeat(Reading(), SensorInputLimits.MaxReadingsPerBatch + 1)) + "]";
        yield return new object[] { "too many readings", $$"""{"readings":{{oversized}}}""", "readings" };
        yield return new object[] { "value too large", $$"""{"readings":[{{Reading(value: "1e12")}}]}""", "readings[0].value" };
        yield return new object[] { "value too small", $$"""{"readings":[{{Reading(value: "-1e12")}}]}""", "readings[0].value" };
        yield return new object[] { "timestamp in the future", $$"""{"readings":[{{Reading(",\"ts\":\"2100-01-01T00:00:00Z\"")}}]}""", "readings[0].ts" };
        yield return new object[] { "timestamp too old", $$"""{"readings":[{{Reading(",\"ts\":\"2000-01-01T00:00:00Z\"")}}]}""", "readings[0].ts" };
        yield return new object[] { "entity id with spaces", $$"""{"readings":[{{Reading(entity: "line 1; drop")}}]}""", "readings[0].entityId" };
        yield return new object[] { "empty batch", """{"readings":[]}""", "readings" };
    }

    [Theory]
    [MemberData(nameof(InvalidLiveBatches))]
    public async Task Invalid_live_batches_are_rejected(string because, string body, string errorKey)
    {
        using var client = _factory.ClientAs(Roles.Operator);
        using var response = await client.PostAsync("/api/sensors/live", Json(body));
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{because}: got {(int)response.StatusCode}");
        var errors = await Errors(response);
        var keys = errors.EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(keys.Any(k => string.Equals(k, errorKey, StringComparison.OrdinalIgnoreCase)),
            $"{because}: expected error key '{errorKey}', got [{string.Join(", ", keys)}]");
    }

    [Fact]
    public async Task Repeated_simulation_requests_are_rate_limited_per_user()
    {
        // A fresh user id gets a fresh budget. Each request is rejected by
        // validation, so nothing is written, but every one still counts.
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.TokenFor(Roles.Operator, Guid.NewGuid()));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < RateLimitPolicies.SensorSimulationPerMinute + 1; i++)
        {
            using var response = await client.PostAsync("/api/sensors/ingest", Json("""{"tick":-1}"""));
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.True(response.Headers.RetryAfter is not null, "429 must carry Retry-After");
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(429, problem.GetProperty("status").GetInt32());
            }
        }

        Assert.All(statuses.Take(RateLimitPolicies.SensorSimulationPerMinute), s => Assert.Equal(HttpStatusCode.BadRequest, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses.Last());

        // The refresh endpoint shares the same per-user budget.
        using var refresh = await client.PostAsync("/api/line-board/refresh", Json("""{"tick":-1}"""));
        Assert.Equal(HttpStatusCode.TooManyRequests, refresh.StatusCode);

        // Another user is unaffected.
        using var other = _factory.ClientAs(Roles.Operator);
        using var otherResponse = await other.PostAsync("/api/sensors/ingest", Json("""{"tick":-1}"""));
        Assert.Equal(HttpStatusCode.BadRequest, otherResponse.StatusCode);
    }

    [Fact]
    public async Task Repeated_live_posts_are_rate_limited_per_user()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.TokenFor(Roles.Operator, Guid.NewGuid()));

        HttpStatusCode last = default;
        for (var i = 0; i < RateLimitPolicies.SensorLivePerMinute + 1; i++)
        {
            using var response = await client.PostAsync("/api/sensors/live", Json("""{"readings":[]}"""));
            last = response.StatusCode;
            if (i < RateLimitPolicies.SensorLivePerMinute)
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
