using LastSeat.Api.Allocation;
using LastSeat.Api.Data;

namespace LastSeat.Api.Tests;

public sealed class AlternativesTests
{
    static readonly TheaterLayout Layout = new(1, ["A", "B", "C"], 10, [5]);

    static readonly IReadOnlyList<Alternatives.SeatPosition> House = Layout.Seats()
        .Select(s => new Alternatives.SeatPosition(s.Id, s.Row, s.Number, s.Block, s.X, s.Y))
        .ToList();

    static int Id(string label) => House.Single(s => s.Label == label).Id;

    static HashSet<int> Free(params string[] labels) => labels.Select(Id).ToHashSet();

    [Fact]
    public void Offers_the_nearest_seats_together_and_never_the_request_itself()
    {
        var free = Free("A3", "A4", "B3", "B4", "C8", "C9");
        var suggestions = Alternatives.Suggest(House, free, [Id("A3"), Id("A4")]);

        Assert.Equal(["B3–4", "C8–9"], suggestions.Select(s => s.Label));
        Assert.All(suggestions, s => Assert.Equal("together", s.Kind));
    }

    [Fact]
    public void An_aisle_breaks_a_run_of_seats()
    {
        // A5 and A6 have consecutive numbers but the aisle runs between them.
        var free = Free("A5", "A6", "C1", "C2");
        var suggestions = Alternatives.Suggest(House, free, [Id("B5"), Id("B6")]);

        Assert.Equal(["C1–2"], suggestions.Select(s => s.Label));
    }

    [Fact]
    public void Falls_back_to_the_closest_seats_apart_when_no_run_is_left()
    {
        var free = Free("A1", "B5", "B6", "C10");
        var suggestions = Alternatives.Suggest(House, free, [Id("A5"), Id("A6")]);

        var apart = Assert.Single(suggestions);
        Assert.Equal("apart", apart.Kind);
        Assert.Equal("B5, B6", apart.Label);
    }

    [Fact]
    public void Keeps_still_available_seats_in_play()
    {
        // B7 was lost; B6 and B8 are still free and form runs with their other neighbours.
        var free = Free("B5", "B6", "B8", "B9", "B10");
        var suggestions = Alternatives.Suggest(House, free, [Id("B6"), Id("B7"), Id("B8")]);

        Assert.Equal("B8–10", suggestions[0].Label);
    }

    [Fact]
    public void A_single_seat_gets_the_nearest_free_seats()
    {
        var free = Free("A1", "B4", "C9");
        var suggestions = Alternatives.Suggest(House, free, [Id("B3")]);

        Assert.Equal(["B4", "A1", "C9"], suggestions.Select(s => s.Label));
    }

    [Fact]
    public void Nothing_free_means_no_suggestions()
    {
        Assert.Empty(Alternatives.Suggest(House, Free(), [Id("A1"), Id("A2")]));
    }
}
