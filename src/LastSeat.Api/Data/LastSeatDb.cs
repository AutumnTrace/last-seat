using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LastSeat.Api.Data;

// EF Core owns the schema, the migrations, seeding and the read models for the box office.
// The allocation transactions themselves are plain SQL in Allocation/SeatAllocator.cs, because
// the locking order and the moment the clock is read are the whole point and should be visible.
public sealed class LastSeatDb(DbContextOptions<LastSeatDb> options) : DbContext(options)
{
    public DbSet<Performance> Performances => Set<Performance>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Hold> Holds => Set<Hold>();
    public DbSet<HoldSeat> HoldSeats => Set<HoldSeat>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<RequestKey> RequestKeys => Set<RequestKey>();
    public DbSet<AllocationEvent> AllocationEvents => Set<AllocationEvent>();
    public DbSet<DemoClock> DemoClock => Set<DemoClock>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Performance>(e =>
        {
            e.Property(p => p.Id).ValueGeneratedNever();
            e.Property(p => p.Title).HasMaxLength(120);
            e.Property(p => p.Venue).HasMaxLength(120);
        });

        model.Entity<Seat>(e =>
        {
            // Seat ids are assigned by the seed in row-major order. They are also the lock order.
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.Section).HasMaxLength(40);
            e.Property(s => s.Row).HasColumnName("row_label").HasMaxLength(4);
            e.HasIndex(s => new { s.PerformanceId, s.Row, s.Number }).IsUnique();
            e.HasIndex(s => s.HoldId);
            e.HasOne<Performance>().WithMany().HasForeignKey(s => s.PerformanceId);
            e.HasOne<Hold>().WithMany().HasForeignKey(s => s.HoldId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Booking>().WithMany().HasForeignKey(s => s.BookingId).OnDelete(DeleteBehavior.SetNull);
            e.ToTable(t => t.HasCheckConstraint("ck_seats_hold_columns",
                "(hold_id is null) = (hold_expires_at is null)"));
            e.ToTable(t => t.HasCheckConstraint("ck_seats_booked_or_held",
                "booking_id is null or hold_id is null"));
        });

        model.Entity<Session>(e => e.Property(s => s.Name).HasMaxLength(24));

        model.Entity<Hold>(e =>
        {
            e.Property(h => h.Status).HasMaxLength(16);
            e.HasOne<Session>().WithMany().HasForeignKey(h => h.SessionId);
            e.HasOne<Performance>().WithMany().HasForeignKey(h => h.PerformanceId);
            // A session has at most one open hold per performance; a new hold replaces it.
            e.HasIndex(h => new { h.PerformanceId, h.SessionId })
                .IsUnique()
                .HasFilter("status = 'active'");
            e.ToTable(t => t.HasCheckConstraint("ck_holds_status",
                "status in ('active', 'confirmed', 'released', 'replaced')"));
        });

        model.Entity<HoldSeat>(e =>
        {
            e.HasKey(hs => new { hs.HoldId, hs.SeatId });
            e.HasOne<Hold>().WithMany().HasForeignKey(hs => hs.HoldId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Seat>().WithMany().HasForeignKey(hs => hs.SeatId);
        });

        model.Entity<Booking>(e =>
        {
            e.Property(b => b.Reference).HasMaxLength(16);
            e.Property(b => b.Source).HasMaxLength(16);
            e.HasIndex(b => b.Reference).IsUnique();
            e.HasIndex(b => b.HoldId).IsUnique();
            e.HasOne<Performance>().WithMany().HasForeignKey(b => b.PerformanceId);
        });

        model.Entity<RequestKey>(e =>
        {
            // Keys are scoped to the session that sent them: another session reusing the same
            // string gets its own, unrelated key.
            e.HasKey(k => new { k.SessionId, k.Key });
            e.Property(k => k.Key).HasMaxLength(64);
            e.Property(k => k.Operation).HasMaxLength(64);
            e.Property(k => k.PayloadHash).HasMaxLength(64);
            // json, not jsonb: a replay returns the stored bytes exactly as first sent.
            e.Property(k => k.ResponseJson).HasColumnType("json");
            e.HasOne<Session>().WithMany().HasForeignKey(k => k.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<AllocationEvent>(e =>
        {
            e.Property(a => a.Kind).HasMaxLength(16);
            e.Property(a => a.Outcome).HasMaxLength(24);
            e.HasIndex(a => new { a.PerformanceId, a.Id });
        });

        model.Entity<DemoClock>(e =>
        {
            e.Property(c => c.Id).ValueGeneratedNever();
            e.ToTable(t => t.HasCheckConstraint("ck_demo_clock_single_row", "id = 1"));
        });
    }
}

public sealed class Performance
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string Venue { get; set; }
    public DateTime StartsAt { get; set; }
    public int HoldSeconds { get; set; } = 90;
}

public sealed class Seat
{
    public int Id { get; set; }
    public int PerformanceId { get; set; }
    public required string Section { get; set; }
    public required string Row { get; set; }
    public int Number { get; set; }

    // Seats in the same row and block with consecutive numbers are adjacent. Aisles split blocks.
    public int Block { get; set; }
    public float X { get; set; }
    public float Y { get; set; }

    // Current allocation. Only SeatAllocator and HoldCleanup write these columns.
    public Guid? HoldId { get; set; }
    public DateTime? HoldExpiresAt { get; set; }
    public Guid? BookingId { get; set; }
}

public sealed class Session
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Hold
{
    public Guid Id { get; set; }
    public int PerformanceId { get; set; }
    public Guid SessionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    // 'expired' is not stored: an active hold past expires_at is expired, whether or not
    // the cleanup sweep has cleared its seats yet.
    public required string Status { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public sealed class HoldSeat
{
    public Guid HoldId { get; set; }
    public int SeatId { get; set; }
}

public sealed class Booking
{
    public Guid Id { get; set; }
    public int PerformanceId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? HoldId { get; set; }
    public required string Reference { get; set; }
    public required string Source { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class RequestKey
{
    public Guid SessionId { get; set; }
    public required string Key { get; set; }
    public required string Operation { get; set; }
    public required string PayloadHash { get; set; }
    public int StatusCode { get; set; }
    public required string ResponseJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AllocationEvent
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public int? PerformanceId { get; set; }
    public Guid? SessionId { get; set; }
    public required string Kind { get; set; }
    public required string Outcome { get; set; }
    public int[] SeatIds { get; set; } = [];
    public double? LockWaitMs { get; set; }
    public string? Detail { get; set; }
}

// One row. lastseat_now() reads it: frozen_at pins the clock (tests), offset_ms moves it
// forward (the demo's "skip ahead" button). With the row at its defaults the database clock
// is plain clock_timestamp().
public sealed class DemoClock
{
    public int Id { get; set; } = 1;
    public long OffsetMs { get; set; }
    public DateTime? FrozenAt { get; set; }
    public bool CleanupPaused { get; set; }
}

internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<LastSeatDb>
{
    public LastSeatDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LastSeatDb>()
            .UseNpgsql("Host=localhost;Database=lastseat_design")
            .UseSnakeCaseNamingConvention()
            .Options);
}
