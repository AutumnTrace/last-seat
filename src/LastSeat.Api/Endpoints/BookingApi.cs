using LastSeat.Api.Allocation;
using LastSeat.Api.Data;
using LastSeat.Api.Queries;
using LastSeat.Api.Realtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LastSeat.Api.Endpoints;

public sealed record CreateSessionRequest(string Name);

public sealed record SessionBody(Guid Id, string Name);

public sealed record HoldRequest(int[] SeatIds);

public static class BookingApi
{
    public const string SessionHeader = "X-Session-Id";
    public const string RequestKeyHeader = "Idempotency-Key";

    public static void MapBookingApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/sessions", async (CreateSessionRequest request, LastSeatDb db, CancellationToken ct) =>
        {
            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > 24)
            {
                return Send(Outcome.Problem(400, "bad_name", "A session name is 1 to 24 characters."));
            }
            var session = new Session
            {
                Id = Guid.CreateVersion7(),
                Name = name,
                CreatedAt = await db.Database.SqlQuery<DateTime>($"select lastseat_now() as \"Value\"").SingleAsync(ct),
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync(ct);
            return Results.Json(new SessionBody(session.Id, session.Name), statusCode: 201);
        });

        api.MapGet("/performances/{performanceId:int}/seats", async (int performanceId, HttpRequest http,
            SeatSnapshot snapshot, CancellationToken ct) =>
        {
            var body = await snapshot.ReadAsync(performanceId, SessionFrom(http), ct);
            return body is null ? Send(Outcome.Problem(404, "performance_not_found", "No such performance.")) : Results.Json(body);
        });

        api.MapPost("/performances/{performanceId:int}/holds", async (int performanceId, HoldRequest request,
            HttpRequest http, SeatAllocator allocator, CancellationToken ct) =>
        {
            if (Headers(http) is { } problem)
            {
                return problem;
            }
            var outcome = await allocator.HoldAsync(SessionFrom(http)!.Value, http.Headers[RequestKeyHeader]!,
                performanceId, request.SeatIds ?? [], ct);
            return Send(outcome);
        });

        api.MapPost("/holds/{holdId:guid}/confirm", async (Guid holdId, HttpRequest http, SeatAllocator allocator,
            CancellationToken ct) =>
        {
            if (Headers(http) is { } problem)
            {
                return problem;
            }
            return Send(await allocator.ConfirmAsync(SessionFrom(http)!.Value, http.Headers[RequestKeyHeader]!, holdId, ct));
        });

        api.MapPost("/holds/{holdId:guid}/release", async (Guid holdId, HttpRequest http, SeatAllocator allocator,
            CancellationToken ct) =>
        {
            if (SessionFrom(http) is not Guid session)
            {
                return Send(Outcome.Problem(401, "unknown_session", $"Send the {SessionHeader} header."));
            }
            return Send(await allocator.ReleaseAsync(session, holdId, ct));
        });

        api.MapGet("/box-office/{performanceId:int}", async (int performanceId, BoxOffice boxOffice, CancellationToken ct) =>
            await boxOffice.ReadAsync(performanceId, ct) is { } body
                ? Results.Json(body)
                : Send(Outcome.Problem(404, "performance_not_found", "No such performance.")));
    }

    public static Guid? SessionFrom(HttpRequest http) =>
        Guid.TryParse(http.Headers[SessionHeader], out var id) ? id : null;

    static IResult? Headers(HttpRequest http)
    {
        if (SessionFrom(http) is null)
        {
            return Send(Outcome.Problem(401, "unknown_session", $"Send the {SessionHeader} header."));
        }
        var key = http.Headers[RequestKeyHeader].ToString();
        return key.Length is 0 or > 64
            ? Send(Outcome.Problem(400, "missing_request_key", $"Send an {RequestKeyHeader} header of 1 to 64 characters."))
            : null;
    }

    static IResult Send(Outcome outcome) => new OutcomeResult(outcome);

    sealed class OutcomeResult(Outcome outcome) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = outcome.Status;
            http.Response.ContentType = "application/json; charset=utf-8";
            if (outcome.Replayed)
            {
                http.Response.Headers["Idempotent-Replayed"] = "true";
            }
            await http.Response.WriteAsync(outcome.Json);
        }
    }
}

public sealed record AdvanceClockRequest(double Seconds);

public sealed record CleanupRequest(bool Paused);

// Only mapped when Demo:Enabled is true. These exist so a reviewer can watch an expiry without
// waiting 90 seconds, and pause the sweep to see that allocation does not depend on it.
public static class DemoApi
{
    public static void MapDemoApi(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/api/demo");

        demo.MapPost("/clock/advance", async ([FromBody] AdvanceClockRequest request, DemoClockControl clock,
            ISeatChangeNotifier notify, CancellationToken ct) =>
        {
            if (request.Seconds is <= 0 or > 3600)
            {
                return Results.BadRequest();
            }
            await clock.AdvanceAsync(TimeSpan.FromSeconds(request.Seconds), ct);
            await notify.SeatsChangedAsync(DemoSeed.PerformanceId, ct);
            return Results.NoContent();
        });

        demo.MapPost("/cleanup", async ([FromBody] CleanupRequest request, DemoClockControl clock, CancellationToken ct) =>
        {
            await clock.PauseCleanupAsync(request.Paused, ct);
            return Results.NoContent();
        });

        demo.MapPost("/reset", async (LastSeatDb db, ISeatChangeNotifier notify, CancellationToken ct) =>
        {
            await DemoSeed.ResetAsync(db, ct);
            await notify.SeatsChangedAsync(DemoSeed.PerformanceId, ct);
            return Results.NoContent();
        });
    }
}
