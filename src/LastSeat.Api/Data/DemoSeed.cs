using Microsoft.EntityFrameworkCore;

namespace LastSeat.Api.Data;

// A rectangular house: rows of numbered seats split into blocks by aisles.
public sealed record TheaterLayout(int PerformanceId, IReadOnlyList<string> Rows, int SeatsPerRow, IReadOnlyList<int> AisleAfter)
{
    const float SeatPitch = 30f;
    const float AisleWidth = 26f;
    const float RowPitch = 36f;
    const float Curve = 0.00022f;

    public int SeatId(int rowIndex, int number) => PerformanceId * 1000 + rowIndex * SeatsPerRow + number;

    public IEnumerable<Seat> Seats()
    {
        var aisles = AisleAfter.Count;
        var width = (SeatsPerRow - 1) * SeatPitch + aisles * AisleWidth;
        for (var r = 0; r < Rows.Count; r++)
        {
            for (var n = 1; n <= SeatsPerRow; n++)
            {
                var block = AisleAfter.Count(a => n > a);
                var x = (n - 1) * SeatPitch + block * AisleWidth - width / 2;
                // Rows curve round towards the stage at the sides, like a stalls plan.
                var y = r * RowPitch - Curve * x * x;
                yield return new Seat
                {
                    Id = SeatId(r, n),
                    PerformanceId = PerformanceId,
                    Section = "Stalls",
                    Row = Rows[r],
                    Number = n,
                    Block = block,
                    X = MathF.Round(x, 1),
                    Y = MathF.Round(y, 1),
                };
            }
        }
    }
}

public static class DemoSeed
{
    public const int PerformanceId = 1;

    // The seeded house: 10 rows of 14, aisles after seats 3 and 11. Everything is sold except
    // F7 and F8 (the last two seats together) and four single seats with sold neighbours.
    public static readonly TheaterLayout Layout =
        new(PerformanceId, ["A", "B", "C", "D", "E", "F", "G", "H", "J", "K"], 14, [3, 11]);

    public static readonly string[] UnsoldSeats = ["F7", "F8", "B2", "D12", "H4", "K14"];

    public static async Task ResetAsync(LastSeatDb db, CancellationToken ct = default)
    {
        await TruncateAsync(db, ct);

        db.Performances.Add(new Performance
        {
            Id = PerformanceId,
            Title = "The Understudy",
            Venue = "Harbour Street Playhouse (fictional)",
            StartsAt = new DateTime(2026, 12, 12, 19, 30, 0, DateTimeKind.Utc),
            HoldSeconds = 90,
        });

        var seats = Layout.Seats().ToList();
        var earlierSales = new Booking
        {
            Id = Guid.CreateVersion7(),
            PerformanceId = PerformanceId,
            Reference = "EARLIER",
            Source = "seed",
            CreatedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
        };
        db.Bookings.Add(earlierSales);
        foreach (var seat in seats)
        {
            if (!UnsoldSeats.Contains($"{seat.Row}{seat.Number}"))
            {
                seat.BookingId = earlierSales.Id;
            }
        }
        db.Seats.AddRange(seats);
        db.DemoClock.Add(new DemoClock());
        await db.SaveChangesAsync(ct);
    }

    public static async Task TruncateAsync(LastSeatDb db, CancellationToken ct = default) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            truncate allocation_events, request_keys, hold_seats, seats, bookings, holds,
                     sessions, performances, demo_clock restart identity cascade
            """, ct);

    public static async Task<bool> IsEmptyAsync(LastSeatDb db, CancellationToken ct = default) =>
        !await db.Performances.AnyAsync(ct);
}
