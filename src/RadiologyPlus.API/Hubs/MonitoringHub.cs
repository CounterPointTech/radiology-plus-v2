using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.API.Hubs;

/// <summary>
/// SignalR hub for progress/notification streams. Used by Tech Validation's "Do the Do"
/// cooking-progress UI; can be extended in later phases for Rad worklist updates.
/// Clients join per-resource groups via <see cref="JoinValidationAsync"/> after auth.
/// </summary>
[Authorize]
public sealed class MonitoringHub : Hub
{
    public static string ValidationGroup(Guid validationId) => $"validation:{validationId}";

    public async Task JoinValidationAsync(Guid validationId, ICurrentUser currentUser)
    {
        var user = currentUser.Current;
        if (user is null || !user.Role.CanAccessTechValidation())
        {
            throw new HubException("Not authorized for tech validation.");
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, ValidationGroup(validationId));
    }

    public async Task LeaveValidationAsync(Guid validationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ValidationGroup(validationId));
    }
}
