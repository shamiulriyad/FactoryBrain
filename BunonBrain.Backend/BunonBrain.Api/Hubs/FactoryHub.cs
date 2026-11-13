using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BunonBrain.Api.Hubs;

/// <summary>
/// SignalR hub for server-to-client live updates. Mapped at
/// <c>/hubs/factory</c>. Auth is enforced by the permission attribute:
/// an anonymous negotiate returns 401 and a user without monitoring.view,
/// production.view or devices.view gets 403, so a WebSocket can never be
/// established without a valid, permitted access token. Clients never
/// call hub methods; this class is intentionally empty.
/// </summary>
[BunonBrain.Api.Authorization.RequirePermission(
    BunonBrain.Application.Access.Permissions.MonitoringView,
    BunonBrain.Application.Access.Permissions.ProductionView,
    BunonBrain.Application.Access.Permissions.DevicesView)]
public sealed class FactoryHub : Hub
{
}
