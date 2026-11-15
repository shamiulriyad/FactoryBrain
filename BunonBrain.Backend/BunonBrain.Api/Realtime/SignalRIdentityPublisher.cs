using BunonBrain.Api.Hubs;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Dtos.Identity;
using BunonBrain.Application.Monitoring;
using Microsoft.AspNetCore.SignalR;

namespace BunonBrain.Api.Realtime;

/// <summary>
/// Pushes identity association changes over <see cref="MonitoringHub"/> as
/// <c>identityAssociation</c> messages, only to the VISION groups of the
/// association's factory and line (people with <c>monitoring.view</c> in that
/// scope). A worker is named only in a currently valid VERIFIED association.
/// </summary>
public sealed class SignalRIdentityPublisher : IIdentityPublisher
{
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<SignalRIdentityPublisher> _log;

    public SignalRIdentityPublisher(IHubContext<MonitoringHub> hub, ILogger<SignalRIdentityPublisher> log) { _hub = hub; _log = log; }

    public async Task AssociationChangedAsync(AssociationDto a, string change, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.Groups(MonitoringSubscriptions.Targets(a.FactoryId, a.Line?.Id, MonitoringCategories.Vision))
                .SendAsync("identityAssociation", new { change, association = a }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "realtime identityAssociation push failed");
        }
    }
}
