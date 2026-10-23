using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FactoryBrain.Api.Hubs;

/// <summary>
/// SignalR hub for server-to-client live updates. Mapped at
/// <c>/hubs/factory</c>. Auth is enforced by the <c>[Authorize]</c>
/// attribute: an anonymous negotiate returns 401, so a WebSocket can
/// never be established without a valid access token. Clients never
/// call hub methods; this class is intentionally empty.
/// </summary>
[Authorize]
public sealed class FactoryHub : Hub
{
}
