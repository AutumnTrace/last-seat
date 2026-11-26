using System.Data;
using System.Security.Cryptography;
using System.Text;
using LastSeat.Api.Realtime;
using Npgsql;

namespace LastSeat.Api.Allocation;

// Every change to who holds or owns a seat happens in this file, in one transaction each.
//
// The rules, in the order the code applies them:
//  1. Lock the session row. One session's requests run one at a time, so a request key is
//     claimed or replayed without a race, and "one open hold per session" holds trivially.
//  2. Claim the request key. Same key, same payload: return the stored response. Same key,
//     different payload: refuse.
//  3. Lock every seat row the decision can touch, ordered by seat id. Overlapping requests then
//     queue on the lowest shared seat instead of deadlocking.
//  4. Only after the locks are granted, read lastseat_now() (clock_timestamp(), not now(),
//     which is frozen at transaction start) and re-read the seats. Time spent waiting for a
//     lock therefore counts: a hold that expired while we queued is treated as expired.
//  5. Decide, write, store the response against the key, commit, then tell clients to refresh.
public sealed class SeatAllocator(NpgsqlDataSource dataSource, ISeatChangeNotifier notifier)
{
    public const int MaxGroupSize = 8;

    public async Task<Outcome> HoldAsync(Guid sessionId, string requestKey, int performanceId,
        IReadOnlyCollection<int> requestedSeatIds, CancellationToken ct = default)
    {
        var seatIds = requestedSeatIds.Distinct().Order().ToArray();
        if (seatIds.Length is 0 or > MaxGroupSize)
        {
            return Outcome.Problem(400, "bad_group_size", $"Choose between 1 and {MaxGroupSize} seats.");
        }

        var payloadHash = Hash($"hold|{performanceId}|{string.Join(',', seatIds)}");
        return await InSessionTransactionAsync(sessionId, requestKey, "hold", payloadHash,
            sql => DecideHoldAsync(sql, sessionId, performanceId, seatIds), performanceId, ct);
    }

    public async Task<Outcome> ConfirmAsync(Guid sessionId, string requestKey, Guid holdId, CancellationToken ct = default)
    {
        var payloadHash = Hash($"confirm|{holdId}");
        return await InSessionTransactionAsync(sessionId, requestKey, "confirm", payloadHash,
            sql => DecideConfirmAsync(sql, sessionId, holdId), performanceId: null, ct);
    }

    public async Task<Outcome> ReleaseAsync(Guid sessionId, Guid holdId, CancellationToken ct = default) =>
        await InSessionTransactionAsync(sessionId, requestKey: null, "release", payloadHash: "",
            sql => DecideReleaseAsync(sql, sessionId, holdId), performanceId: null, ct);

    async Task<Outcome> InSessionTransactionAsync(Guid sessionId, string? requestKey, string operation,
        string payloadHash, Func<Sql, Task<Decision>> decide, int? performanceId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var sql = new Sql(conn, tx, ct);

        var sessionExists = await sql.ScalarAsync<int?>(
            "select 1 from sessions where id = @session for no key update",
            ("session", sessionId)) is not null;
        if (!sessionExists)
        {
            return Outcome.Problem(401, "unknown_session", "Start a session before booking.");
        }

        if (requestKey is not null)
        {
            var claimed = await sql.ExecuteAsync(
                """
                insert into request_keys (session_id, key, operation, payload_hash, status_code, response_json, created_at)
                values (@session, @key, @operation, @hash, 0, 'null', lastseat_now())
                on conflict do nothing
                """,
                ("session", sessionId), ("key", requestKey), ("operation", operation), ("hash", payloadHash)) == 1;

            if (!claimed)
            {
                var earlier = await ReadRequestKeyAsync(sql, sessionId, requestKey);
                if (earlier.Operation != operation || earlier.PayloadHash != payloadHash)
                {
                    return Outcome.Problem(422, "request_key_reused",
                        "This request key was already used for a different request. Send a new key.");
                }
                await tx.CommitAsync(ct);
                return new Outcome(earlier.StatusCode, earlier.ResponseJson, Replayed: true);
            }
        }

        var decision = await decide(sql);

        await sql.ExecuteAsync(
            """
            insert into allocation_events (at, performance_id, session_id, kind, outcome, seat_ids, lock_wait_ms, detail)
            values (@at, @performance, @session, @kind, @outcome, @seats, @wait, @detail)
            """,
            ("at", decision.DecidedAt), ("performance", decision.PerformanceId), ("session", sessionId),
            ("kind", operation), ("outcome", decision.EventOutcome), ("seats", decision.EventSeatIds),
            ("wait", decision.LockWaitMs), ("detail", decision.EventDetail));

        if (requestKey is not null)
        {
            await sql.ExecuteAsync(
                """
                update request_keys set status_code = @status, response_json = @json::json
                where session_id = @session and key = @key
                """,
                ("status", decision.Outcome.Status), ("json", decision.Outcome.Json),
                ("session", sessionId), ("key", requestKey));
        }

        await tx.CommitAsync(ct);

        // Only after commit: a client that refreshes on this message must see the new state.
        if (decision.Outcome.ChangedSeats)
        {
            await notifier.SeatsChangedAsync(decision.PerformanceId ?? performanceId ?? 0, CancellationToken.None);
        }
        return decision.Outcome;
    }

    async Task<Decision> DecideHoldAsync(Sql sql, Guid sessionId, int performanceId, int[] seatIds)
    {
        var requestedAt = await sql.NowAsync();
        var holdSeconds = await sql.ScalarAsync<int?>(
            "select hold_seconds from performances where id = @performance", ("performance", performanceId));
        if (holdSeconds is null)
        {
            return Decision.Rejected(Outcome.Problem(404, "performance_not_found", "No such performance."), requestedAt, performanceId: null);
        }

        // The session's open hold, if any, is replaced by this one. Its seats are locked too.
        var previousHoldId = await sql.ScalarAsync<Guid?>(
            "select id from holds where session_id = @session and performance_id = @performance and status = 'active'",
            ("session", sessionId), ("performance", performanceId));

        var locked = await sql.ListAsync(
            """
            select id from seats
            where performance_id = @performance and (id = any(@seats) or hold_id = @previous)
            order by id
            for update
            """,
            r => r.GetInt32(0),
            ("performance", performanceId), ("seats", seatIds), ("previous", previousHoldId));

        var decidedAt = await sql.NowAsync();
        var lockWaitMs = (decidedAt - requestedAt).TotalMilliseconds;
        var state = (await SeatStateAsync(sql, locked)).ToDictionary(s => s.Id);

        if (seatIds.Any(id => !state.ContainsKey(id)))
        {
            return Decision.Rejected(
                Outcome.Problem(400, "unknown_seats", "Some of those seats are not in this performance."),
                decidedAt, performanceId);
        }

        var conflicts = new List<SeatConflict>();
        foreach (var id in seatIds)
        {
            var s = state[id];
            if (s.BookingId is not null)
            {
                conflicts.Add(new SeatConflict(id, s.Label, "booked", s.BookedAt, s.BookerSessionId == sessionId));
            }
            else if (s.HoldId is not null && s.HoldExpiresAt > decidedAt && s.HolderSessionId != sessionId)
            {
                conflicts.Add(new SeatConflict(id, s.Label, "held", s.HeldSince, Yours: false));
            }
        }

        if (conflicts.Count > 0)
        {
            // Nothing is written to the seats: a group either gets every seat or none.
            var house = await HouseAsync(sql, performanceId, sessionId, decidedAt);
            var stillAvailable = seatIds.Where(id => conflicts.All(c => c.SeatId != id))
                .Select(id => new SeatRef(id, state[id].Label)).ToList();
            var alternatives = Alternatives.Suggest(house.Seats, house.Available, seatIds);
            var taken = string.Join(", ", conflicts.Select(c => c.Label));
            var body = new SeatsUnavailable(
                "seats_unavailable",
                conflicts.Count == 1 ? $"{taken} is no longer available." : $"{taken} are no longer available.",
                requestedAt, decidedAt, lockWaitMs, conflicts, stillAvailable, alternatives);
            return new Decision(Outcome.Of(409, body), performanceId, decidedAt, lockWaitMs, "conflict", seatIds,
                $"lost {taken}");
        }

        var holdId = Guid.CreateVersion7();
        var expiresAt = decidedAt.AddSeconds(holdSeconds.Value);

        if (previousHoldId is not null)
        {
            await sql.ExecuteAsync(
                "update holds set status = 'replaced', closed_at = @at where id = @previous",
                ("at", decidedAt), ("previous", previousHoldId));
            await sql.ExecuteAsync(
                """
                update seats set hold_id = null, hold_expires_at = null
                where hold_id = @previous and not (id = any(@seats))
                """,
                ("previous", previousHoldId), ("seats", seatIds));
        }

        await sql.ExecuteAsync(
            """
            insert into holds (id, performance_id, session_id, created_at, expires_at, status)
            values (@hold, @performance, @session, @at, @expires, 'active')
            """,
            ("hold", holdId), ("performance", performanceId), ("session", sessionId),
            ("at", decidedAt), ("expires", expiresAt));
        await sql.ExecuteAsync(
            "insert into hold_seats (hold_id, seat_id) select @hold, unnest(@seats)",
            ("hold", holdId), ("seats", seatIds));
        await sql.ExecuteAsync(
            "update seats set hold_id = @hold, hold_expires_at = @expires where id = any(@seats)",
            ("hold", holdId), ("expires", expiresAt), ("seats", seatIds));

        var seats = seatIds.Select(id => new SeatRef(id, state[id].Label)).ToList();
        var created = new HoldCreated(holdId, performanceId, seats, requestedAt, decidedAt, lockWaitMs, expiresAt, previousHoldId);
        return new Decision(Outcome.Of(201, created, changedSeats: true), performanceId, decidedAt, lockWaitMs,
            "held", seatIds, previousHoldId is null ? null : "replaced previous hold");
    }

    async Task<Decision> DecideConfirmAsync(Sql sql, Guid sessionId, Guid holdId)
    {
        var requestedAt = await sql.NowAsync();
        var hold = await HoldAsync(sql, holdId);

        // A hold id that belongs to another session is indistinguishable from one that does not
        // exist. A stale tab cannot confirm, or even probe, somebody else's hold.
        if (hold is null || hold.SessionId != sessionId)
        {
            return Decision.Rejected(
                Outcome.Problem(404, "hold_not_found", "This session has no hold with that id."),
                requestedAt, performanceId: null);
        }

        var seatIds = (await sql.ListAsync(
            "select seat_id from hold_seats where hold_id = @hold order by seat_id",
            r => r.GetInt32(0), ("hold", holdId))).ToArray();

        if (hold.Status == "confirmed")
        {
            var existing = await BookingForHoldAsync(sql, holdId, seatIds);
            return new Decision(Outcome.Of(200, existing with { AlreadyConfirmed = true }), hold.PerformanceId,
                requestedAt, 0, "already-confirmed", seatIds, existing.Reference);
        }

        if (hold.Status != "active")
        {
            return Decision.Rejected(
                Outcome.Problem(409, "hold_closed", $"This hold was {hold.Status} at {hold.ClosedAt:HH:mm:ss.fff} UTC."),
                requestedAt, hold.PerformanceId);
        }

        await sql.ListAsync("select id from seats where id = any(@seats) order by id for update",
            r => r.GetInt32(0), ("seats", seatIds));

        var decidedAt = await sql.NowAsync();
        var lockWaitMs = (decidedAt - requestedAt).TotalMilliseconds;
        var state = await SeatStateAsync(sql, seatIds);

        // Strictly before expires_at. A confirmation decided at the expiry instant has lost.
        var stillOurs = state.All(s => s.HoldId == holdId && s.BookingId is null && s.HoldExpiresAt > decidedAt);
        if (!stillOurs)
        {
            var seats = state.Select(s => s switch
            {
                { BookingId: not null } => new ExpiredSeat(s.Id, s.Label, "booked", s.BookedAt, s.BookerSessionId == sessionId),
                { HoldId: not null } when s.HoldId != holdId && s.HoldExpiresAt > decidedAt =>
                    new ExpiredSeat(s.Id, s.Label, "held", s.HeldSince, s.HolderSessionId == sessionId),
                _ => new ExpiredSeat(s.Id, s.Label, "free", null, false),
            }).ToList();
            var body = new HoldExpired(
                "hold_expired",
                $"The hold ran out at {hold.ExpiresAt:HH:mm:ss.fff} UTC, before this confirmation was decided.",
                holdId, hold.ExpiresAt, requestedAt, decidedAt, lockWaitMs, seats,
                CanHoldAgain: seats.All(s => s.State == "free"));
            return new Decision(Outcome.Of(409, body), hold.PerformanceId, decidedAt, lockWaitMs, "expired", seatIds,
                string.Join(", ", seats.Select(s => $"{s.Label} {s.State}")));
        }

        var bookingId = Guid.CreateVersion7();
        var reference = NewReference();
        await sql.ExecuteAsync(
            """
            insert into bookings (id, performance_id, session_id, hold_id, reference, source, created_at)
            values (@booking, @performance, @session, @hold, @reference, 'online', @at)
            """,
            ("booking", bookingId), ("performance", hold.PerformanceId), ("session", sessionId),
            ("hold", holdId), ("reference", reference), ("at", decidedAt));
        await sql.ExecuteAsync(
            "update seats set booking_id = @booking, hold_id = null, hold_expires_at = null where id = any(@seats)",
            ("booking", bookingId), ("seats", seatIds));
        await sql.ExecuteAsync(
            "update holds set status = 'confirmed', closed_at = @at where id = @hold",
            ("at", decidedAt), ("hold", holdId));

        var confirmed = new BookingConfirmed(bookingId, reference, holdId,
            state.Select(s => new SeatRef(s.Id, s.Label)).ToList(), decidedAt, AlreadyConfirmed: false);
        return new Decision(Outcome.Of(200, confirmed, changedSeats: true), hold.PerformanceId, decidedAt, lockWaitMs,
            "booked", seatIds, reference);
    }

    async Task<Decision> DecideReleaseAsync(Sql sql, Guid sessionId, Guid holdId)
    {
        var requestedAt = await sql.NowAsync();
        var hold = await HoldAsync(sql, holdId);
        if (hold is null || hold.SessionId != sessionId)
        {
            return Decision.Rejected(
                Outcome.Problem(404, "hold_not_found", "This session has no hold with that id."), requestedAt, null);
        }

        var seatIds = (await sql.ListAsync(
            "select seat_id from hold_seats where hold_id = @hold order by seat_id",
            r => r.GetInt32(0), ("hold", holdId))).ToArray();
        if (hold.Status != "active")
        {
            return new Decision(Outcome.Of(200, new HoldReleased(holdId, hold.Status, [])), hold.PerformanceId,
                requestedAt, 0, "noop", seatIds, $"already {hold.Status}");
        }

        await sql.ListAsync("select id from seats where id = any(@seats) order by id for update",
            r => r.GetInt32(0), ("seats", seatIds));
        var decidedAt = await sql.NowAsync();
        var cleared = await sql.ListAsync(
            """
            update seats set hold_id = null, hold_expires_at = null
            where id = any(@seats) and hold_id = @hold
            returning id, row_label || number
            """,
            r => new SeatRef(r.GetInt32(0), r.GetString(1)), ("seats", seatIds), ("hold", holdId));
        await sql.ExecuteAsync(
            "update holds set status = 'released', closed_at = @at where id = @hold",
            ("at", decidedAt), ("hold", holdId));

        return new Decision(Outcome.Of(200, new HoldReleased(holdId, "released", cleared), changedSeats: cleared.Count > 0),
            hold.PerformanceId, decidedAt, (decidedAt - requestedAt).TotalMilliseconds, "released", seatIds, null);
    }

    // Read after the locks are held: this is the state the decision is made against.
    static async Task<List<SeatState>> SeatStateAsync(Sql sql, IReadOnlyCollection<int> seatIds) =>
        await sql.ListAsync(
            """
            select s.id, s.row_label || s.number, s.hold_id, s.hold_expires_at, s.booking_id,
                   h.session_id, h.created_at, b.session_id, b.created_at
            from seats s
            left join holds h on h.id = s.hold_id
            left join bookings b on b.id = s.booking_id
            where s.id = any(@seats)
            order by s.id
            """,
            r => new SeatState(
                r.GetInt32(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetGuid(2),
                r.IsDBNull(3) ? null : r.GetDateTime(3),
                r.IsDBNull(4) ? null : r.GetGuid(4),
                r.IsDBNull(5) ? null : r.GetGuid(5),
                r.IsDBNull(6) ? null : r.GetDateTime(6),
                r.IsDBNull(7) ? null : r.GetGuid(7),
                r.IsDBNull(8) ? null : r.GetDateTime(8)),
            ("seats", seatIds.ToArray()));

    // Every seat in the house with what this session could hold right now. Not locked: it only
    // feeds suggestions, which the client has to request (and win) like any other hold.
    static async Task<(List<Alternatives.SeatPosition> Seats, HashSet<int> Available)> HouseAsync(
        Sql sql, int performanceId, Guid sessionId, DateTime at)
    {
        var available = new HashSet<int>();
        var seats = await sql.ListAsync(
            """
            select s.id, s.row_label, s.number, s.block, s.x, s.y,
                   s.booking_id is null and (s.hold_id is null or s.hold_expires_at <= @at or h.session_id = @session)
            from seats s left join holds h on h.id = s.hold_id
            where s.performance_id = @performance
            order by s.id
            """,
            r =>
            {
                if (r.GetBoolean(6))
                {
                    available.Add(r.GetInt32(0));
                }
                return new Alternatives.SeatPosition(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                    r.GetFloat(4), r.GetFloat(5));
            },
            ("performance", performanceId), ("session", sessionId), ("at", at));
        return (seats, available);
    }

    static async Task<HoldRow?> HoldAsync(Sql sql, Guid holdId)
    {
        var rows = await sql.ListAsync(
            "select performance_id, session_id, status, expires_at, closed_at from holds where id = @hold",
            r => new HoldRow(r.GetInt32(0), r.GetGuid(1), r.GetString(2), r.GetDateTime(3),
                r.IsDBNull(4) ? null : r.GetDateTime(4)),
            ("hold", holdId));
        return rows.SingleOrDefault();
    }

    static async Task<BookingConfirmed> BookingForHoldAsync(Sql sql, Guid holdId, int[] seatIds)
    {
        var booking = (await sql.ListAsync(
            "select id, reference, created_at from bookings where hold_id = @hold",
            r => (Id: r.GetGuid(0), Reference: r.GetString(1), At: r.GetDateTime(2)), ("hold", holdId))).Single();
        var seats = await sql.ListAsync(
            "select id, row_label || number from seats where id = any(@seats) order by id",
            r => new SeatRef(r.GetInt32(0), r.GetString(1)), ("seats", seatIds));
        return new BookingConfirmed(booking.Id, booking.Reference, holdId, seats, booking.At, AlreadyConfirmed: false);
    }

    static async Task<(string Operation, string PayloadHash, int StatusCode, string ResponseJson)> ReadRequestKeyAsync(
        Sql sql, Guid sessionId, string key) =>
        (await sql.ListAsync(
            "select operation, payload_hash, status_code, response_json::text from request_keys where session_id = @session and key = @key",
            r => (r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)),
            ("session", sessionId), ("key", key))).Single();

    static string Hash(string payload) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    static string NewReference()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return $"LS-{chars}";
    }

    sealed record SeatState(int Id, string Label, Guid? HoldId, DateTime? HoldExpiresAt, Guid? BookingId,
        Guid? HolderSessionId, DateTime? HeldSince, Guid? BookerSessionId, DateTime? BookedAt);

    sealed record HoldRow(int PerformanceId, Guid SessionId, string Status, DateTime ExpiresAt, DateTime? ClosedAt);

    sealed record Decision(Outcome Outcome, int? PerformanceId, DateTime DecidedAt, double? LockWaitMs,
        string EventOutcome, int[] EventSeatIds, string? EventDetail)
    {
        public static Decision Rejected(Outcome outcome, DateTime at, int? performanceId) =>
            new(outcome, performanceId, at, null, "rejected", [], outcome.ReadCode());
    }
}
