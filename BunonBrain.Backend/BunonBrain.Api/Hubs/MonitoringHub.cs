using System.Collections.Concurrent;
using BunonBrain.Application.Access;
using BunonBrain.Application.Monitoring;
using Microsoft.AspNetCore.SignalR;

namespace BunonBrain.Api.Hubs;

/// <summary>
/// Real-time monitoring updates at <c>/hubs/monitoring</c>. Connecting needs
/// a valid token with <c>monitoring.view</c> or <c>devices.view</c> (401 / 403
/// on negotiate otherwise). A connection receives nothing until it calls
/// <see cref="Subscribe"/>, which joins only the groups the caller's token
/// allows (<see cref="MonitoringSubscriptions.Plan"/>): its factories (or its
/// lines, for supervisors), VISION only with <c>monitoring.view</c>. The
/// connection closes when the token expires; the client reconnects with a
/// fresh token and subscribes again.
/// <para>Server → client messages: <c>monitoringEvent</c> (an event),
/// <c>monitoringAlert</c> ({ change, alert }), <c>cameraStatus</c> and
/// <c>deviceStatus</c> ({ id, code, status, sourceType, occurredAt }).</para>
/// </summary>
[BunonBrain.Api.Authorization.RequirePermission(Permissions.MonitoringView, Permissions.DevicesView)]
public sealed class MonitoringHub : Hub
{
    private readonly MonitoringConnections _connections;
    private readonly ILogger<MonitoringHub> _log;

    public MonitoringHub(MonitoringConnections connections, ILogger<MonitoringHub> log) { _connections = connections; _log = log; }

    /// <summary>Follow one factory (or, with no factory, all of the caller's). Replaces the connection's previous subscription.</summary>
    public async Task<MonitoringSubscription> Subscribe(Guid? factoryId)
    {
        var caller = AccessContext.FromPrincipal(Context.User) ?? throw new HubException("unauthorized");
        var plan = MonitoringSubscriptions.Plan(caller, factoryId);
        if (plan.Error is not null) throw new HubException($"{plan.Error}: {plan.Message}");
        var previous = _connections.Replace(Context.ConnectionId, caller.UserId, plan.Groups);
        foreach (var g in previous.Except(plan.Groups)) await Groups.RemoveFromGroupAsync(Context.ConnectionId, g);
        foreach (var g in plan.Groups.Except(previous)) await Groups.AddToGroupAsync(Context.ConnectionId, g);
        return new MonitoringSubscription(plan.AllFactories, plan.Factories, plan.Lines, plan.Categories, DateTimeOffset.UtcNow);
    }

    /// <summary>Stop receiving updates (the connection stays open).</summary>
    public async Task Unsubscribe()
    {
        foreach (var g in _connections.Replace(Context.ConnectionId, Guid.Empty, Array.Empty<string>()))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, g);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR drops the connection from its groups; forget our record of it too.
        _connections.Remove(Context.ConnectionId);
        if (exception is not null) _log.LogDebug(exception, "Monitoring connection {Id} closed with an error.", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>What a connection now receives.</summary>
public sealed record MonitoringSubscription(bool AllFactories, IReadOnlyList<Guid> Factories, IReadOnlyList<string> Lines,
    IReadOnlyList<string> Categories, DateTimeOffset SubscribedAt);

/// <summary>The groups each open monitoring connection joined (for replacing subscriptions and cleanup on disconnect).</summary>
public sealed class MonitoringConnections
{
    private readonly ConcurrentDictionary<string, (Guid UserId, IReadOnlyList<string> Groups)> _byConnection = new();

    /// <returns>The groups the connection had before.</returns>
    public IReadOnlyList<string> Replace(string connectionId, Guid userId, IReadOnlyList<string> groups)
    {
        IReadOnlyList<string> previous = Array.Empty<string>();
        _byConnection.AddOrUpdate(connectionId, _ => (userId, groups), (_, old) => { previous = old.Groups; return (userId, groups); });
        return previous;
    }

    public void Remove(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    public int Count => _byConnection.Count;

    public bool IsSubscribed(string connectionId) => _byConnection.TryGetValue(connectionId, out var v) && v.Groups.Count > 0;
}
