using LastSeat.Api.Realtime;
using Npgsql;

namespace LastSeat.Api.Allocation;

// Clears the seat columns of expired holds and tells clients. Correctness does not depend on
// it: the allocator already treats an expired hold as free. The sweep exists so that other
// browsers see the seat come back without waiting for a change of their own.
public sealed class HoldCleanup(NpgsqlDataSource dataSource, ISeatChangeNotifier notifier)
{
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var sql = new Sql(conn, tx, ct);

        if (await sql.ScalarAsync<bool?>("select cleanup_paused from demo_clock where id = 1") == true)
        {
            return 0;
        }

        // SKIP LOCKED: a seat that a hold or confirm transaction has locked is that transaction's
        // to decide. The sweep never waits, so it can never be part of a deadlock.
        var cleared = await sql.ListAsync(
            """
            with expired as (
                select id, hold_id from seats
                where booking_id is null and hold_id is not null and hold_expires_at <= lastseat_now()
                order by id
                for update skip locked
            )
            update seats s set hold_id = null, hold_expires_at = null
            from expired e
            where s.id = e.id
            returning s.performance_id, e.hold_id, s.id
            """,
            r => (PerformanceId: r.GetInt32(0), HoldId: r.GetGuid(1), SeatId: r.GetInt32(2)));

        foreach (var hold in cleared.GroupBy(c => (c.PerformanceId, c.HoldId)))
        {
            await sql.ExecuteAsync(
                """
                insert into allocation_events (at, performance_id, session_id, kind, outcome, seat_ids, detail)
                select lastseat_now(), @performance, h.session_id, 'cleanup', 'expired', @seats, 'seats released by the sweep'
                from holds h where h.id = @hold
                """,
                ("performance", hold.Key.PerformanceId), ("hold", hold.Key.HoldId),
                ("seats", hold.Select(c => c.SeatId).Order().ToArray()));
        }

        await tx.CommitAsync(ct);

        foreach (var performanceId in cleared.Select(c => c.PerformanceId).Distinct())
        {
            await notifier.SeatsChangedAsync(performanceId, ct);
        }
        return cleared.Count;
    }
}

public sealed class HoldCleanupService(HoldCleanup cleanup, IConfiguration config, ILogger<HoldCleanupService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(config.GetValue("Cleanup:IntervalMs", 1000));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await cleanup.SweepAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Hold cleanup sweep failed");
            }
        }
    }
}
