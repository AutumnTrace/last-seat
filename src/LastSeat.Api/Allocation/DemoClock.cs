using Npgsql;

namespace LastSeat.Api.Allocation;

// Moves the clock that lastseat_now() reads. The demo uses Advance so a reviewer does not have to
// wait 90 seconds for a hold to run out; the tests use Freeze to put expiry on an exact instant.
public sealed class DemoClockControl(NpgsqlDataSource dataSource)
{
    public Task AdvanceAsync(TimeSpan by, CancellationToken ct = default) => ExecuteAsync(
        "update demo_clock set offset_ms = offset_ms + @ms, frozen_at = frozen_at + @ms * interval '1 millisecond' where id = 1",
        ct, ("ms", (long)by.TotalMilliseconds));

    public Task FreezeAsync(DateTime at, CancellationToken ct = default) => ExecuteAsync(
        "update demo_clock set frozen_at = @at where id = 1", ct, ("at", at));

    public Task UnfreezeAsync(CancellationToken ct = default) => ExecuteAsync(
        "update demo_clock set frozen_at = null where id = 1", ct);

    public Task PauseCleanupAsync(bool paused, CancellationToken ct = default) => ExecuteAsync(
        "update demo_clock set cleanup_paused = @paused where id = 1", ct, ("paused", paused));

    async Task ExecuteAsync(string text, CancellationToken ct, params (string, object?)[] args)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await new Sql(conn, null, ct).ExecuteAsync(text, args);
    }
}
