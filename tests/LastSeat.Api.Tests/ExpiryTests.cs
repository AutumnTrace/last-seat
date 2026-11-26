using LastSeat.Api.Allocation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LastSeat.Api.Tests;

// Expiry is tested on a frozen database clock: lastseat_now() returns demo_clock.frozen_at,
// so "one millisecond before the hold runs out" is an exact instant rather than a sleep.
public sealed class ExpiryTests(PostgresServer server)
{
    [Fact]
    public async Task A_confirmation_one_millisecond_before_expiry_wins_and_one_at_expiry_loses()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        await house.Clock.FreezeAsync(TestHouse.T0);
        var early = (await house.HoldAsync(ada, "A1")).ShouldBeHeld();
        Assert.Equal(TestHouse.T0.AddSeconds(90), early.ExpiresAt);

        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(90).AddMilliseconds(-1));
        (await house.ConfirmAsync(ada, early.HoldId)).ShouldBeBooked();

        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(100));
        var late = (await house.HoldAsync(ada, "A2")).ShouldBeHeld();
        await house.Clock.FreezeAsync(late.ExpiresAt);
        var expired = (await house.ConfirmAsync(ada, late.HoldId)).ShouldFail<HoldExpired>(409, "hold_expired");
        Assert.Equal(late.ExpiresAt, expired.ExpiredAt);
        Assert.Equal(late.ExpiresAt, expired.DecidedAt);
        Assert.True(expired.CanHoldAgain);
    }

    [Fact]
    public async Task An_expired_hold_can_be_taken_while_cleanup_is_paused()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var grace = await house.SessionAsync("Grace");
        await house.Clock.PauseCleanupAsync(true);
        await house.Clock.FreezeAsync(TestHouse.T0);
        var first = (await house.HoldAsync(ada, "A1", "A2")).ShouldBeHeld();

        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(91));
        Assert.Equal(0, await house.Cleanup.SweepAsync());
        // The stale columns are still on the seat: nothing has cleaned them up.
        Assert.Equal(first.HoldId, (await house.SeatRowAsync("A1")).HoldId);

        var second = (await house.HoldAsync(grace, "A1", "A2")).ShouldBeHeld();
        Assert.Equal(second.HoldId, (await house.SeatRowAsync("A1")).HoldId);
        Assert.Equal(second.HoldId, (await house.SeatRowAsync("A2")).HoldId);
    }

    // The case the design note walks through. The confirmation starts while the hold is valid,
    // then queues behind another transaction's lock on the seat. By the time it gets the lock the
    // hold has run out. The decision uses the clock read after the lock, so it loses. With now()
    // (the transaction start time) it would have booked a seat whose hold had already expired.
    [Fact]
    public async Task A_confirmation_queued_behind_a_lock_is_judged_by_the_clock_after_the_lock()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        await house.Clock.FreezeAsync(TestHouse.T0);
        var hold = (await house.HoldAsync(ada, "A1")).ShouldBeHeld();
        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(89));

        await using var blocker = await LockSeatAsync(house, "A1");
        var confirm = house.ConfirmAsync(ada, hold.HoldId);
        await WaitForLockWaiterAsync(house);
        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(91));
        await blocker.DisposeAsync();

        var expired = (await confirm).ShouldFail<HoldExpired>(409, "hold_expired");
        Assert.Equal(TestHouse.T0.AddSeconds(89), expired.RequestedAt);
        Assert.Equal(TestHouse.T0.AddSeconds(91), expired.DecidedAt);
        Assert.Equal(2000, expired.LockWaitMs);
        Assert.Null((await house.SeatRowAsync("A1")).BookingId);
    }

    [Fact]
    public async Task A_lock_wait_that_ends_before_expiry_still_books()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        await house.Clock.FreezeAsync(TestHouse.T0);
        var hold = (await house.HoldAsync(ada, "A1")).ShouldBeHeld();

        await using var blocker = await LockSeatAsync(house, "A1");
        var confirm = house.ConfirmAsync(ada, hold.HoldId);
        await WaitForLockWaiterAsync(house);
        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(60));
        await blocker.DisposeAsync();

        var booked = (await confirm).ShouldBeBooked();
        Assert.Equal(TestHouse.T0.AddSeconds(60), booked.ConfirmedAt);
    }

    [Fact]
    public async Task Cleanup_clears_expired_holds_only()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var grace = await house.SessionAsync("Grace");
        var lin = await house.SessionAsync("Lin");
        await house.Clock.FreezeAsync(TestHouse.T0);
        (await house.HoldAsync(ada, "A1", "A2")).ShouldBeHeld();
        var confirmed = (await house.HoldAsync(lin, "B1")).ShouldBeHeld();
        (await house.ConfirmAsync(lin, confirmed.HoldId)).ShouldBeBooked();
        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(60));
        var fresh = (await house.HoldAsync(grace, "A5")).ShouldBeHeld();

        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(95));
        house.Notifier.Notifications.Clear();
        Assert.Equal(2, await house.Cleanup.SweepAsync());

        Assert.Null((await house.SeatRowAsync("A1")).HoldId);
        Assert.Null((await house.SeatRowAsync("A2")).HoldId);
        Assert.Equal(fresh.HoldId, (await house.SeatRowAsync("A5")).HoldId);
        Assert.NotNull((await house.SeatRowAsync("B1")).BookingId);
        Assert.Equal([TestHouse.PerformanceId], house.Notifier.Notifications);
        var swept = await house.CountAsync(db => db.AllocationEvents.SingleAsync(e => e.Kind == "cleanup"));
        Assert.Equal(ada, swept.SessionId);
        Assert.Equal(house.Seats("A1", "A2"), swept.SeatIds);
        Assert.Equal(0, await house.Cleanup.SweepAsync());
    }

    [Fact]
    public async Task Cleanup_skips_a_seat_that_a_decision_has_locked()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        await house.Clock.FreezeAsync(TestHouse.T0);
        (await house.HoldAsync(ada, "A1")).ShouldBeHeld();
        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(120));

        await using var blocker = await LockSeatAsync(house, "A1");

        // Returns at once instead of waiting: the locked seat belongs to another decision.
        var sweep = house.Cleanup.SweepAsync();
        Assert.Same(sweep, await Task.WhenAny(sweep, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Equal(0, await sweep);
        await blocker.DisposeAsync();
        Assert.Equal(1, await house.Cleanup.SweepAsync());
        Assert.Null((await house.SeatRowAsync("A1")).HoldId);
    }

    // Holds a row lock on one seat until disposed, like a slow transaction would.
    internal static async Task<SeatLock> LockSeatAsync(TestHouse house, string label)
    {
        var conn = await house.DataSource.OpenConnectionAsync();
        var tx = await conn.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("select id from seats where id = @id for update", conn, tx);
        command.Parameters.AddWithValue("id", house.Seat(label));
        await command.ExecuteScalarAsync();
        return new SeatLock(conn, tx);
    }

    internal sealed class SeatLock(NpgsqlConnection conn, NpgsqlTransaction tx) : IAsyncDisposable
    {
        bool released;

        public async ValueTask DisposeAsync()
        {
            if (released)
            {
                return;
            }
            released = true;
            await tx.RollbackAsync();
            await tx.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    internal static async Task WaitForLockWaiterAsync(TestHouse house)
    {
        var database = new NpgsqlConnectionStringBuilder(house.ConnectionString).Database!;
        for (var i = 0; i < 400; i++)
        {
            await using var conn = await house.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "select count(*) from pg_stat_activity where datname = @db and wait_event_type = 'Lock'", conn);
            command.Parameters.AddWithValue("db", database);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail("The confirmation never queued behind the lock.");
    }
}
