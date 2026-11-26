using LastSeat.Api.Allocation;
using Npgsql;

namespace LastSeat.Api.Queries;

public sealed record SnapshotSeat(int Id, string Label, string Row, int Number, int Block, float X, float Y,
    string State, bool Yours, DateTime? HeldUntil);

public sealed record SnapshotHold(Guid HoldId, IReadOnlyList<int> SeatIds, DateTime CreatedAt, DateTime ExpiresAt);

public sealed record SnapshotBooking(string Reference, IReadOnlyList<int> SeatIds, DateTime CreatedAt);

public sealed record PerformanceInfo(int Id, string Title, string Venue, DateTime StartsAt, int HoldSeconds);

// The authoritative view a browser renders. "held" means held by an unexpired hold, so a seat
// whose hold ran out shows as available even if the cleanup sweep has not cleared it yet.
public sealed record SeatSnapshotBody(PerformanceInfo Performance, DateTime ServerNow, IReadOnlyList<SnapshotSeat> Seats,
    SnapshotHold? Hold, IReadOnlyList<SnapshotBooking> Bookings);

public sealed class SeatSnapshot(NpgsqlDataSource dataSource)
{
    public async Task<SeatSnapshotBody?> ReadAsync(int performanceId, Guid? sessionId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // One statement snapshot for everything below: REPEATABLE READ, read only.
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var sql = new Sql(conn, tx, ct);

        var performance = (await sql.ListAsync(
            "select id, title, venue, starts_at, hold_seconds from performances where id = @performance",
            r => new PerformanceInfo(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDateTime(3), r.GetInt32(4)),
            ("performance", performanceId))).SingleOrDefault();
        if (performance is null)
        {
            return null;
        }

        var now = await sql.NowAsync();
        var seats = await sql.ListAsync(
            """
            select s.id, s.row_label, s.number, s.block, s.x, s.y,
                   case when s.booking_id is not null then 'booked'
                        when s.hold_id is not null and s.hold_expires_at > @now then 'held'
                        else 'available' end,
                   coalesce(b.session_id = @session, false)
                     or coalesce(s.hold_expires_at > @now and h.session_id = @session, false),
                   case when s.hold_expires_at > @now then s.hold_expires_at end
            from seats s
            left join holds h on h.id = s.hold_id
            left join bookings b on b.id = s.booking_id
            where s.performance_id = @performance
            order by s.id
            """,
            r => new SnapshotSeat(r.GetInt32(0), $"{r.GetString(1)}{r.GetInt32(2)}", r.GetString(1), r.GetInt32(2),
                r.GetInt32(3), r.GetFloat(4), r.GetFloat(5), r.GetString(6), r.GetBoolean(7),
                r.IsDBNull(8) ? null : r.GetDateTime(8)),
            ("performance", performanceId), ("session", sessionId), ("now", now));

        SnapshotHold? hold = null;
        var bookings = new List<SnapshotBooking>();
        if (sessionId is not null)
        {
            hold = (await sql.ListAsync(
                """
                select h.id, array(select seat_id from hold_seats where hold_id = h.id order by seat_id), h.created_at, h.expires_at
                from holds h
                where h.session_id = @session and h.performance_id = @performance and h.status = 'active' and h.expires_at > @now
                """,
                r => new SnapshotHold(r.GetGuid(0), r.GetFieldValue<int[]>(1), r.GetDateTime(2), r.GetDateTime(3)),
                ("session", sessionId), ("performance", performanceId), ("now", now))).SingleOrDefault();

            bookings = await sql.ListAsync(
                """
                select b.reference, array(select id from seats where booking_id = b.id order by id), b.created_at
                from bookings b
                where b.session_id = @session and b.performance_id = @performance
                order by b.created_at
                """,
                r => new SnapshotBooking(r.GetString(0), r.GetFieldValue<int[]>(1), r.GetDateTime(2)),
                ("session", sessionId), ("performance", performanceId));
        }

        await tx.CommitAsync(ct);
        return new SeatSnapshotBody(performance, now, seats, hold, bookings);
    }
}
