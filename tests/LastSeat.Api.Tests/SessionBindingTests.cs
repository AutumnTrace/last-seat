using LastSeat.Api.Allocation;
using Microsoft.EntityFrameworkCore;

namespace LastSeat.Api.Tests;

public sealed class SessionBindingTests(PostgresServer server)
{
    // Ada's tab still shows "Confirm" for a hold that ran out and was taken by Grace.
    [Fact]
    public async Task A_stale_browser_cannot_confirm_the_new_owners_hold()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var grace = await house.SessionAsync("Grace");
        await house.Clock.FreezeAsync(TestHouse.T0);
        var adas = (await house.HoldAsync(ada, "A1", "A2")).ShouldBeHeld();
        await house.Clock.FreezeAsync(TestHouse.T0.AddSeconds(95));
        var graces = (await house.HoldAsync(grace, "A1", "A2")).ShouldBeHeld();

        // Her own hold id: refused, and told who has the seats now.
        var stale = (await house.ConfirmAsync(ada, adas.HoldId)).ShouldFail<HoldExpired>(409, "hold_expired");
        Assert.All(stale.Seats, s => Assert.Equal("held", s.State));
        Assert.All(stale.Seats, s => Assert.False(s.Yours));
        Assert.False(stale.CanHoldAgain);

        // Grace's hold id, if Ada's tab somehow had it: indistinguishable from a missing hold.
        (await house.ConfirmAsync(ada, graces.HoldId)).ShouldFail<ProblemBody>(404, "hold_not_found");
        (await house.Allocator.ReleaseAsync(ada, graces.HoldId)).ShouldFail<ProblemBody>(404, "hold_not_found");

        var booking = (await house.ConfirmAsync(grace, graces.HoldId)).ShouldBeBooked();
        Assert.Equal(booking.BookingId, (await house.SeatRowAsync("A1")).BookingId);
        Assert.Equal(1, await house.CountAsync(db => db.Bookings.CountAsync()));
    }

    [Fact]
    public async Task A_second_tab_of_the_same_session_replaces_the_first_tabs_hold()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var firstTab = (await house.HoldAsync(ada, "A1")).ShouldBeHeld();
        var secondTab = (await house.HoldAsync(ada, "A1", "A2")).ShouldBeHeld();
        Assert.Equal(firstTab.HoldId, secondTab.ReplacedHoldId);

        (await house.ConfirmAsync(ada, firstTab.HoldId)).ShouldFail<ProblemBody>(409, "hold_closed");
        (await house.ConfirmAsync(ada, secondTab.HoldId)).ShouldBeBooked();
        Assert.Equal(1, await house.CountAsync(db => db.Holds.CountAsync(h => h.Status == "confirmed")));
        Assert.Equal(1, await house.CountAsync(db => db.Holds.CountAsync(h => h.Status == "replaced")));
    }

    [Fact]
    public async Task Replacing_a_hold_frees_the_seats_the_new_one_does_not_keep()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        (await house.HoldAsync(ada, "A1", "A2", "A3")).ShouldBeHeld();
        var narrower = (await house.HoldAsync(ada, "A3", "A4")).ShouldBeHeld();

        Assert.Null((await house.SeatRowAsync("A1")).HoldId);
        Assert.Null((await house.SeatRowAsync("A2")).HoldId);
        Assert.Equal(narrower.HoldId, (await house.SeatRowAsync("A3")).HoldId);
        Assert.Equal(narrower.HoldId, (await house.SeatRowAsync("A4")).HoldId);
    }

    [Fact]
    public async Task An_unknown_session_cannot_hold_or_confirm()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var stranger = Guid.NewGuid();
        (await house.HoldAsync(stranger, "A1")).ShouldFail<ProblemBody>(401, "unknown_session");
        (await house.ConfirmAsync(stranger, Guid.NewGuid())).ShouldFail<ProblemBody>(401, "unknown_session");
        Assert.Null((await house.SeatRowAsync("A1")).HoldId);
    }

    [Fact]
    public async Task Releasing_a_hold_frees_its_seats_once()
    {
        await using var house = await TestHouse.CreateAsync(server);
        var ada = await house.SessionAsync("Ada");
        var hold = (await house.HoldAsync(ada, "B2", "B3")).ShouldBeHeld();

        var released = (await house.Allocator.ReleaseAsync(ada, hold.HoldId)).Read<HoldReleased>();
        Assert.Equal(["B2", "B3"], released.Seats.Select(s => s.Label));
        Assert.Null((await house.SeatRowAsync("B2")).HoldId);

        var again = (await house.Allocator.ReleaseAsync(ada, hold.HoldId)).Read<HoldReleased>();
        Assert.Equal("released", again.Status);
        Assert.Empty(again.Seats);
        (await house.ConfirmAsync(ada, hold.HoldId)).ShouldFail<ProblemBody>(409, "hold_closed");
    }
}
