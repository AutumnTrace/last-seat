using LastSeat.Api.Allocation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LastSeat.Api.Tests;

public sealed class AllocationRaceTests(PostgresServer server)
{
    [Fact]
    public async Task Fifty_sessions_racing_for_one_seat_produce_exactly_one_hold()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var sessions = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => house.SessionAsync($"client {i}")));

        // Five rounds, one seat each, so the result is not one lucky interleaving.
        foreach (var label in new[] { "A3", "A4", "B1", "B7", "B10" })
        {
            var outcomes = await RaceAsync(sessions.Select(s => (Func<Task<Outcome>>)(() => house.HoldAsync(s, label))));

            var winners = outcomes.Where(o => o.Status == 201).Select(o => o.Read<HoldCreated>()).ToList();
            var losers = outcomes.Where(o => o.Status == 409).Select(o => o.Read<SeatsUnavailable>()).ToList();
            var winner = Assert.Single(winners);
            Assert.Equal(49, losers.Count);

            var seat = await house.SeatRowAsync(label);
            Assert.Equal(winner.HoldId, seat.HoldId);
            // Every loser was decided after the winner, under the same lock, and was told so.
            Assert.All(losers, l =>
            {
                var conflict = Assert.Single(l.Conflicts);
                Assert.Equal("held", conflict.State);
                Assert.Equal(winner.DecidedAt, conflict.Since);
                Assert.True(l.DecidedAt >= winner.DecidedAt);
            });
        }

        Assert.Equal(5, await house.CountAsync(db => db.Holds.CountAsync()));
        Assert.Equal(5, await house.CountAsync(db => db.HoldSeats.CountAsync()));
    }

    [Fact]
    public async Task An_overlapping_group_request_leaves_no_partial_allocation()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var grace = await house.SessionAsync("Grace");
        (await house.HoldAsync(ada, "A3")).ShouldBeHeld();

        var lost = (await house.HoldAsync(grace, "A2", "A3", "A4")).ShouldFail<SeatsUnavailable>(409, "seats_unavailable");

        Assert.Equal(["A3"], lost.Conflicts.Select(c => c.Label));
        Assert.Equal(["A2", "A4"], lost.StillAvailable.Select(s => s.Label));
        Assert.Null((await house.SeatRowAsync("A2")).HoldId);
        Assert.Null((await house.SeatRowAsync("A4")).HoldId);
        Assert.Equal(0, await house.CountAsync(db => db.Holds.CountAsync(h => h.SessionId == grace)));
    }

    [Fact]
    public async Task Racing_overlapping_groups_get_all_their_seats_or_none()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var random = new Random(20261126);
        var sessions = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => house.SessionAsync($"group {i}")));

        for (var round = 0; round < 8; round++)
        {
            // Each session asks for 2 to 4 seats in a random order. Many requests overlap.
            var requests = sessions.Select(s =>
            {
                var size = random.Next(2, 5);
                var seats = Enumerable.Range(0, 20).OrderBy(_ => random.Next()).Take(size)
                    .Select(i => house.Layout.SeatId(i / 10, i % 10 + 1)).ToArray();
                return (Session: s, Seats: seats);
            }).ToList();

            var outcomes = await RaceAsync(requests.Select(r => (Func<Task<Outcome>>)(() =>
                house.Allocator.HoldAsync(r.Session, Guid.NewGuid().ToString(), TestHouse.PerformanceId, r.Seats))));

            Assert.All(outcomes, o => Assert.Contains(o.Status, new[] { 201, 409 }));
            await using var db = house.Db();
            var seats = await db.Seats.AsNoTracking().ToListAsync();
            var holds = await db.Holds.AsNoTracking().Where(h => h.Status == "active").ToListAsync();
            var holdSeats = await db.HoldSeats.AsNoTracking().ToListAsync();
            foreach (var hold in holds)
            {
                // Every open hold owns every seat it asked for.
                var asked = holdSeats.Where(hs => hs.HoldId == hold.Id).Select(hs => hs.SeatId).ToHashSet();
                var owned = seats.Where(s => s.HoldId == hold.Id).Select(s => s.Id).ToHashSet();
                Assert.Equal(asked, owned);
            }
            // And no seat points at a hold that is not open.
            Assert.All(seats.Where(s => s.HoldId is not null), s => Assert.Contains(holds, h => h.Id == s.HoldId));
            Assert.Equal(outcomes.Count(o => o.Status == 201), holds.Count);

            // Clear the house for the next round.
            await db.Database.ExecuteSqlRawAsync(
                "update seats set hold_id = null, hold_expires_at = null; update holds set status = 'released'");
        }
    }

    [Fact]
    public async Task Requests_naming_seats_in_opposite_orders_do_not_deadlock()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var sessions = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => house.SessionAsync($"order {i}")));
        var forward = house.Seats("A1", "A2", "A3", "A4", "A5", "A6");
        var backward = forward.Reverse().ToArray();

        // Without a stable lock order these would deadlock; PostgreSQL would abort one with 40P01.
        var outcomes = await RaceAsync(sessions.Select((s, i) => (Func<Task<Outcome>>)(() =>
            house.Allocator.HoldAsync(s, "k", TestHouse.PerformanceId, i % 2 == 0 ? forward : backward))));

        Assert.Single(outcomes, o => o.Status == 201);
        Assert.Equal(39, outcomes.Count(o => o.Status == 409));
    }

    // Release every call at once, each on its own pooled connection.
    internal static async Task<Outcome[]> RaceAsync(IEnumerable<Func<Task<Outcome>>> calls)
    {
        ThreadPool.SetMinThreads(128, 128);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(call => Task.Run(async () =>
        {
            await gate.Task;
            return await call();
        })).ToArray();
        gate.SetResult();
        try
        {
            return await Task.WhenAll(tasks);
        }
        catch (PostgresException e)
        {
            Assert.Fail($"PostgreSQL error {e.SqlState}: {e.MessageText}");
            throw;
        }
    }
}
