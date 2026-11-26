using Microsoft.AspNetCore.SignalR;

namespace LastSeat.Api.Realtime;

// SignalR carries no seat state. It only says "something changed in this performance";
// clients answer by fetching the snapshot, which is read from PostgreSQL.
public sealed class SeatHub : Hub
{
    public Task Watch(int performanceId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupName(performanceId));

    public static string GroupName(int performanceId) => $"performance:{performanceId}";
}

public interface ISeatChangeNotifier
{
    Task SeatsChangedAsync(int performanceId, CancellationToken ct);
}

public sealed class SignalRSeatChangeNotifier(IHubContext<SeatHub> hub, ILogger<SignalRSeatChangeNotifier> log)
    : ISeatChangeNotifier
{
    public async Task SeatsChangedAsync(int performanceId, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(SeatHub.GroupName(performanceId)).SendAsync("seatsChanged", performanceId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The change is committed either way. A missed hint costs a client one stale view
            // until its next refresh (reconnect, hold expiry or the next change).
            log.LogWarning(e, "Could not notify clients of performance {PerformanceId}", performanceId);
        }
    }
}
