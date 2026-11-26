namespace LastSeat.Api.Allocation;

// Suggestions offered after a conflict. Pure functions over a snapshot of the house; the
// suggested seats are not reserved, so taking one is a new hold request that can lose too.
public static class Alternatives
{
    public sealed record SeatPosition(int Id, string Row, int Number, int Block, float X, float Y)
    {
        public string Label => $"{Row}{Number}";
    }

    public static IReadOnlyList<Alternative> Suggest(
        IReadOnlyList<SeatPosition> house, IReadOnlySet<int> available, IReadOnlyCollection<int> requested, int max = 3)
    {
        var byId = house.ToDictionary(s => s.Id);
        var wanted = requested.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var size = wanted.Count;
        var cx = wanted.Average(s => s.X);
        var cy = wanted.Average(s => s.Y);
        var requestedSet = requested.ToHashSet();

        if (size == 1)
        {
            return house.Where(s => available.Contains(s.Id) && !requestedSet.Contains(s.Id))
                .OrderBy(s => Distance([s], cx, cy)).ThenBy(s => s.Id)
                .Take(max)
                .Select(s => new Alternative("single", s.Label, [s.Id]))
                .ToList();
        }

        // Runs of `size` free seats next to each other: same row, same block, consecutive numbers.
        var together = house
            .GroupBy(s => (s.Row, s.Block))
            .SelectMany(g => Windows(g.OrderBy(s => s.Number).ToList(), size, available))
            .Where(w => !w.All(s => requestedSet.Contains(s.Id)))
            .OrderBy(w => Distance(w, cx, cy))
            .ThenBy(w => w[0].Id)
            .Take(max)
            .Select(w => new Alternative("together", RangeLabel(w), w.Select(s => s.Id).ToList()))
            .ToList();
        if (together.Count > 0)
        {
            return together;
        }

        // No block has `size` free seats in a row. Offer the closest free seats, apart.
        var nearest = house.Where(s => available.Contains(s.Id))
            .OrderBy(s => Distance([s], cx, cy)).ThenBy(s => s.Id)
            .Take(size)
            .OrderBy(s => s.Id)
            .ToList();
        return nearest.Count == size
            ? [new Alternative("apart", string.Join(", ", nearest.Select(s => s.Label)), nearest.Select(s => s.Id).ToList())]
            : [];
    }

    public static string RangeLabel(IReadOnlyList<SeatPosition> run) =>
        run.Count == 1 ? run[0].Label : $"{run[0].Label}–{run[^1].Number}";

    static IEnumerable<List<SeatPosition>> Windows(List<SeatPosition> row, int size, IReadOnlySet<int> available)
    {
        for (var start = 0; start + size <= row.Count; start++)
        {
            var window = row.GetRange(start, size);
            var consecutive = window[^1].Number - window[0].Number == size - 1;
            if (consecutive && window.All(s => available.Contains(s.Id)))
            {
                yield return window;
            }
        }
    }

    static double Distance(IReadOnlyList<SeatPosition> seats, double cx, double cy)
    {
        var x = seats.Average(s => s.X) - cx;
        var y = seats.Average(s => s.Y) - cy;
        return Math.Sqrt(x * x + y * y);
    }
}
