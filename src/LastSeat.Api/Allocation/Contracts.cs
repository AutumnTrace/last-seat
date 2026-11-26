using System.Text.Json;

namespace LastSeat.Api.Allocation;

// What a decision returns: an HTTP status and the JSON body, exactly as stored against the
// request key. A retry with the same key gets the same bytes back with Replayed = true.
public sealed record Outcome(int Status, string Json, bool Replayed = false, bool ChangedSeats = false)
{
    public static Outcome Of(int status, object body, bool changedSeats = false) =>
        new(status, JsonSerializer.Serialize(body, body.GetType(), JsonSerializerOptions.Web), ChangedSeats: changedSeats);

    public static Outcome Problem(int status, string code, string message) =>
        Of(status, new ProblemBody(code, message));

    public T Read<T>() => JsonSerializer.Deserialize<T>(Json, JsonSerializerOptions.Web)!;

    public string ReadCode() => Read<ProblemBody>().Code;
}

public sealed record ProblemBody(string Code, string Message);

public sealed record SeatRef(int Id, string Label);

public sealed record HoldCreated(
    Guid HoldId,
    int PerformanceId,
    IReadOnlyList<SeatRef> Seats,
    DateTime RequestedAt,
    DateTime DecidedAt,
    double LockWaitMs,
    DateTime ExpiresAt,
    Guid? ReplacedHoldId);

// The losing side of a race. Everything in it is read from the database after the seat locks
// were granted, so it describes the state the decision was actually made against.
public sealed record SeatsUnavailable(
    string Code,
    string Message,
    DateTime RequestedAt,
    DateTime DecidedAt,
    double LockWaitMs,
    IReadOnlyList<SeatConflict> Conflicts,
    IReadOnlyList<SeatRef> StillAvailable,
    IReadOnlyList<Alternative> Alternatives);

public sealed record SeatConflict(int SeatId, string Label, string State, DateTime? Since, bool Yours);

public sealed record Alternative(string Kind, string Label, IReadOnlyList<int> SeatIds);

public sealed record BookingConfirmed(
    Guid BookingId,
    string Reference,
    Guid HoldId,
    IReadOnlyList<SeatRef> Seats,
    DateTime ConfirmedAt,
    bool AlreadyConfirmed);

public sealed record HoldExpired(
    string Code,
    string Message,
    Guid HoldId,
    DateTime ExpiredAt,
    DateTime RequestedAt,
    DateTime DecidedAt,
    double LockWaitMs,
    IReadOnlyList<ExpiredSeat> Seats,
    bool CanHoldAgain);

// State: "free" (nobody took it), "held" (another hold has it), "booked".
public sealed record ExpiredSeat(int SeatId, string Label, string State, DateTime? Since, bool Yours);

public sealed record HoldReleased(Guid HoldId, string Status, IReadOnlyList<SeatRef> Seats);
