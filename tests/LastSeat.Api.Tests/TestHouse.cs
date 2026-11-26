using System.Collections.Concurrent;
using LastSeat.Api.Allocation;
using LastSeat.Api.Data;
using LastSeat.Api.Realtime;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LastSeat.Api.Tests;

// One performance in its own database, with the real allocator wired to it. Default house:
// rows A and B, ten seats each, an aisle after seat 5, everything free.
public sealed class TestHouse : IAsyncDisposable
{
    public const int PerformanceId = 7;

    public static readonly DateTime T0 = new(2026, 12, 1, 18, 0, 0, DateTimeKind.Utc);

    TestHouse(string connectionString, TheaterLayout layout)
    {
        ConnectionString = connectionString;
        Layout = layout;
        DataSource = NpgsqlDataSource.Create(connectionString);
        Notifier = new RecordingNotifier();
        Allocator = new SeatAllocator(DataSource, Notifier);
        Cleanup = new HoldCleanup(DataSource, Notifier);
        Clock = new DemoClockControl(DataSource);
    }

    public string ConnectionString { get; }
    public TheaterLayout Layout { get; }
    public NpgsqlDataSource DataSource { get; }
    public RecordingNotifier Notifier { get; }
    public SeatAllocator Allocator { get; }
    public HoldCleanup Cleanup { get; }
    public DemoClockControl Clock { get; }

    public static async Task<TestHouse> CreateAsync(PostgresServer server, TheaterLayout? layout = null)
    {
        layout ??= new TheaterLayout(PerformanceId, ["A", "B"], 10, [5]);
        var house = new TestHouse(await server.CreateDatabaseAsync(), layout);
        await using var db = house.Db();
        db.Performances.Add(new Performance
        {
            Id = layout.PerformanceId,
            Title = "Test performance",
            Venue = "Test hall",
            StartsAt = T0.AddDays(10),
            HoldSeconds = 90,
        });
        db.Seats.AddRange(layout.Seats());
        db.DemoClock.Add(new DemoClock());
        await db.SaveChangesAsync();
        return house;
    }

    public LastSeatDb Db() => new(new DbContextOptionsBuilder<LastSeatDb>()
        .UseNpgsql(ConnectionString)
        .UseSnakeCaseNamingConvention()
        .Options);

    public int Seat(string label)
    {
        var row = Layout.Rows.ToList().IndexOf(label[..1]);
        return Layout.SeatId(row, int.Parse(label[1..], System.Globalization.CultureInfo.InvariantCulture));
    }

    public int[] Seats(params string[] labels) => labels.Select(Seat).ToArray();

    public async Task<Guid> SessionAsync(string name)
    {
        await using var db = Db();
        var session = new Session { Id = Guid.CreateVersion7(), Name = name, CreatedAt = T0 };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    public Task<Outcome> HoldAsync(Guid session, params string[] labels) =>
        Allocator.HoldAsync(session, Guid.NewGuid().ToString(), Layout.PerformanceId, Seats(labels));

    public Task<Outcome> ConfirmAsync(Guid session, Guid holdId) =>
        Allocator.ConfirmAsync(session, Guid.NewGuid().ToString(), holdId);

    public async Task<Seat> SeatRowAsync(string label)
    {
        await using var db = Db();
        var id = Seat(label);
        return await db.Seats.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    public async Task<T> CountAsync<T>(Func<LastSeatDb, Task<T>> query)
    {
        await using var db = Db();
        return await query(db);
    }

    public async ValueTask DisposeAsync() => await DataSource.DisposeAsync();
}

public sealed class RecordingNotifier : ISeatChangeNotifier
{
    public ConcurrentQueue<int> Notifications { get; } = new();

    public Task SeatsChangedAsync(int performanceId, CancellationToken ct)
    {
        Notifications.Enqueue(performanceId);
        return Task.CompletedTask;
    }
}

public static class OutcomeAssertions
{
    public static HoldCreated ShouldBeHeld(this Outcome outcome)
    {
        Assert.True(outcome.Status == 201, $"expected 201, got {outcome.Status}: {outcome.Json}");
        return outcome.Read<HoldCreated>();
    }

    public static BookingConfirmed ShouldBeBooked(this Outcome outcome)
    {
        Assert.True(outcome.Status == 200, $"expected 200, got {outcome.Status}: {outcome.Json}");
        return outcome.Read<BookingConfirmed>();
    }

    public static T ShouldFail<T>(this Outcome outcome, int status, string code)
    {
        Assert.True(outcome.Status == status, $"expected {status}, got {outcome.Status}: {outcome.Json}");
        Assert.Equal(code, outcome.ReadCode());
        return outcome.Read<T>();
    }
}
