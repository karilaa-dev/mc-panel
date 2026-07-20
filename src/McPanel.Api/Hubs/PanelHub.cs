using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using McPanel.Api.Services;

namespace McPanel.Api.Hubs;

[Authorize]
public sealed class PanelHub(SessionAudience audience) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var stamp = Context.User?.FindFirstValue(AdminAuthService.SessionStampClaim);
        if (!audience.TryGetCurrentGroup(stamp, out var group))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public Task SubscribeServer(Guid serverId) => Groups.AddToGroupAsync(Context.ConnectionId, serverId.ToString("N"));
    public Task UnsubscribeServer(Guid serverId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, serverId.ToString("N"));
}
