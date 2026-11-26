using LastSeat.Api.Allocation;
using Microsoft.EntityFrameworkCore;

namespace LastSeat.Api.Tests;

public sealed class RequestKeyTests(PostgresServer server)
{
    [Fact]
    public async Task A_retried_hold_returns_the_original_response()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var seats = house.Seats("A1", "A2");

        var first = await house.Allocator.HoldAsync(ada, "hold-1", TestHouse.PerformanceId, seats);
        var retry = await house.Allocator.HoldAsync(ada, "hold-1", TestHouse.PerformanceId, seats.Reverse().ToArray());

        Assert.False(first.Replayed);
        Assert.True(retry.Replayed);
        Assert.Equal(first.Json, retry.Json);
        Assert.Equal(1, await house.CountAsync(db => db.Holds.CountAsync()));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_payload_is_refused()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var hold = (await house.Allocator.HoldAsync(ada, "k", TestHouse.PerformanceId, house.Seats("A1"))).ShouldBeHeld();

        (await house.Allocator.HoldAsync(ada, "k", TestHouse.PerformanceId, house.Seats("A2")))
            .ShouldFail<ProblemBody>(422, "request_key_reused");
        (await house.Allocator.ConfirmAsync(ada, "k", hold.HoldId))
            .ShouldFail<ProblemBody>(422, "request_key_reused");

        Assert.Equal(hold.HoldId, (await house.SeatRowAsync("A1")).HoldId);
        Assert.Null((await house.SeatRowAsync("A2")).HoldId);
        Assert.Null((await house.SeatRowAsync("A1")).BookingId);
    }

    [Fact]
    public async Task Concurrent_retries_with_one_key_create_one_hold()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var seats = house.Seats("B4", "B5");

        var outcomes = await AllocationRaceTests.RaceAsync(Enumerable.Range(0, 20).Select(_ =>
            (Func<Task<Outcome>>)(() => house.Allocator.HoldAsync(ada, "double-click", TestHouse.PerformanceId, seats))));

        Assert.All(outcomes, o => Assert.Equal(201, o.Status));
        Assert.Single(outcomes.Select(o => o.Read<HoldCreated>().HoldId).Distinct());
        Assert.Equal(19, outcomes.Count(o => o.Replayed));
        Assert.Equal(1, await house.CountAsync(db => db.Holds.CountAsync()));
    }

    [Fact]
    public async Task Confirmation_is_idempotent_with_or_without_the_same_key()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var hold = (await house.HoldAsync(ada, "A7", "A8")).ShouldBeHeld();

        var first = (await house.Allocator.ConfirmAsync(ada, "confirm-1", hold.HoldId)).ShouldBeBooked();
        var sameKey = await house.Allocator.ConfirmAsync(ada, "confirm-1", hold.HoldId);
        var newKey = (await house.Allocator.ConfirmAsync(ada, "confirm-2", hold.HoldId)).ShouldBeBooked();

        Assert.True(sameKey.Replayed);
        Assert.Equal(first.Reference, sameKey.Read<BookingConfirmed>().Reference);
        Assert.Equal(first.Reference, newKey.Reference);
        Assert.False(first.AlreadyConfirmed);
        Assert.True(newKey.AlreadyConfirmed);
        Assert.Equal(1, await house.CountAsync(db => db.Bookings.CountAsync()));
    }

    [Fact]
    public async Task Concurrent_confirmations_book_once()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var hold = (await house.HoldAsync(ada, "B9", "B10")).ShouldBeHeld();

        var outcomes = await AllocationRaceTests.RaceAsync(Enumerable.Range(0, 10).Select(i =>
            (Func<Task<Outcome>>)(() => house.Allocator.ConfirmAsync(ada, $"tab-{i}", hold.HoldId))));

        Assert.All(outcomes, o => Assert.Equal(200, o.Status));
        Assert.Single(outcomes.Select(o => o.Read<BookingConfirmed>().Reference).Distinct());
        Assert.Single(outcomes, o => !o.Read<BookingConfirmed>().AlreadyConfirmed);
        Assert.Equal(1, await house.CountAsync(db => db.Bookings.CountAsync()));
    }

    [Fact]
    public async Task A_lost_race_is_replayed_as_lost_even_after_the_seat_frees_up()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var grace = await house.SessionAsync("Grace");
        var adas = (await house.HoldAsync(ada, "A1")).ShouldBeHeld();
        var seat = house.Seats("A1");

        (await house.Allocator.HoldAsync(grace, "g", TestHouse.PerformanceId, seat)).ShouldFail<SeatsUnavailable>(409, "seats_unavailable");
        await house.Allocator.ReleaseAsync(ada, adas.HoldId);

        // The key names that attempt, and that attempt lost. A new attempt needs a new key.
        var replay = await house.Allocator.HoldAsync(grace, "g", TestHouse.PerformanceId, seat);
        Assert.True(replay.Replayed);
        Assert.Equal(409, replay.Status);
        (await house.Allocator.HoldAsync(grace, "g2", TestHouse.PerformanceId, seat)).ShouldBeHeld();
    }

    [Fact]
    public async Task Keys_belong_to_the_session_that_sent_them()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var grace = await house.SessionAsync("Grace");

        var adas = (await house.Allocator.HoldAsync(ada, "same", TestHouse.PerformanceId, house.Seats("A1"))).ShouldBeHeld();
        var graces = await house.Allocator.HoldAsync(grace, "same", TestHouse.PerformanceId, house.Seats("A2"));

        Assert.False(graces.Replayed);
        Assert.NotEqual(adas.HoldId, graces.ShouldBeHeld().HoldId);
    }
}
