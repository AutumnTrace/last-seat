using LastSeat.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LastSeat.Api.Queries;

public sealed record ClockInfo(DateTime Now, long OffsetMs, DateTime? FrozenAt, bool CleanupPaused);

public sealed record SeatCounts(int Available, int Held, int Booked);

public sealed record BoxOfficeHold(Guid HoldId, string Session, IReadOnlyList<string> Seats, DateTime CreatedAt,
    DateTime ExpiresAt, bool Expired, bool SeatsStillMarked);

public sealed record BoxOfficeBooking(string Reference, string Session, IReadOnlyList<string> Seats, DateTime CreatedAt);

public sealed record BoxOfficeEvent(long Id, DateTime At, string? Session, string Kind, string Outcome,
    IReadOnlyList<string> Seats, double? LockWaitMs, string? Detail);

public sealed record BoxOfficeBody(PerformanceInfo Performance, ClockInfo Clock, SeatCounts Counts,
    IReadOnlyList<BoxOfficeHold> Holds, IReadOnlyList<BoxOfficeBooking> Bookings, IReadOnlyList<BoxOfficeEvent> Events);

// Read model for the box-office screen. Plain EF Core queries: nothing here takes a lock.
public sealed class BoxOffice(LastSeatDb db)
{
    public async Task<BoxOfficeBody?> ReadAsync(int performanceId, CancellationToken ct)
    {
        var performance = await db.Performances.AsNoTracking()
            .Where(p => p.Id == performanceId)
            .Select(p => new PerformanceInfo(p.Id, p.Title, p.Venue, p.StartsAt, p.HoldSeconds))
            .SingleOrDefaultAsync(ct);
        if (performance is null)
        {
            return null;
        }

        var now = await db.Database.SqlQuery<DateTime>($"select lastseat_now() as \"Value\"").SingleAsync(ct);
        var clock = await db.DemoClock.AsNoTracking()
            .Select(c => new ClockInfo(now, c.OffsetMs, c.FrozenAt, c.CleanupPaused))
            .SingleAsync(ct);

        var seats = await db.Seats.AsNoTracking().Where(s => s.PerformanceId == performanceId).ToListAsync(ct);
        var labels = seats.ToDictionary(s => s.Id, s => $"{s.Row}{s.Number}");
        var counts = new SeatCounts(
            seats.Count(s => s.BookingId is null && (s.HoldId is null || s.HoldExpiresAt <= now)),
            seats.Count(s => s.BookingId is null && s.HoldId is not null && s.HoldExpiresAt > now),
            seats.Count(s => s.BookingId is not null));

        var sessions = await db.Sessions.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var openHolds = await db.Holds.AsNoTracking()
            .Where(h => h.PerformanceId == performanceId && h.Status == "active")
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);
        var openHoldIds = openHolds.Select(h => h.Id).ToList();
        var holdSeats = await db.HoldSeats.AsNoTracking()
            .Where(hs => openHoldIds.Contains(hs.HoldId))
            .ToListAsync(ct);
        // Expired holds stay listed while their seat columns are still set (the sweep has not run
        // or is paused), so the screen shows that those seats are free to take anyway.
        var holds = openHolds.Select(h =>
        {
            var ids = holdSeats.Where(hs => hs.HoldId == h.Id).Select(hs => hs.SeatId).Order().ToList();
            return new BoxOfficeHold(h.Id, sessions.GetValueOrDefault(h.SessionId, "?"),
                ids.Select(id => labels[id]).ToList(), h.CreatedAt, h.ExpiresAt, h.ExpiresAt <= now,
                seats.Any(s => s.HoldId == h.Id));
        }).Where(h => !h.Expired || h.SeatsStillMarked).ToList();

        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => b.PerformanceId == performanceId && b.Source == "online")
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        var bookingRows = bookings.Select(b => new BoxOfficeBooking(
            b.Reference, b.SessionId is Guid s ? sessions.GetValueOrDefault(s, "?") : "box office",
            seats.Where(x => x.BookingId == b.Id).OrderBy(x => x.Id).Select(x => labels[x.Id]).ToList(),
            b.CreatedAt)).ToList();

        var events = await db.AllocationEvents.AsNoTracking()
            .Where(e => e.PerformanceId == performanceId)
            .OrderByDescending(e => e.Id)
            .Take(40)
            .ToListAsync(ct);
        var eventRows = events.Select(e => new BoxOfficeEvent(
            e.Id, e.At, e.SessionId is Guid s ? sessions.GetValueOrDefault(s) : null, e.Kind, e.Outcome,
            e.SeatIds.Select(id => labels.GetValueOrDefault(id, id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList(),
            e.LockWaitMs, e.Detail)).ToList();

        return new BoxOfficeBody(performance, clock, counts, holds, bookingRows, eventRows);
    }
}
