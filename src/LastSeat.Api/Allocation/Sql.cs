using Npgsql;

namespace LastSeat.Api.Allocation;

// A thin helper so the transactions in SeatAllocator read as SQL rather than ADO.NET plumbing.
internal sealed class Sql(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
{
    public async Task<int> ExecuteAsync(string text, params (string Name, object? Value)[] args)
    {
        await using var command = Command(text, args);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<T?> ScalarAsync<T>(string text, params (string Name, object? Value)[] args)
    {
        await using var command = Command(text, args);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)value;
    }

    public async Task<List<T>> ListAsync<T>(string text, Func<NpgsqlDataReader, T> map, params (string Name, object? Value)[] args)
    {
        await using var command = Command(text, args);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    // The database clock. lastseat_now() is clock_timestamp() unless the demo clock moves it.
    public async Task<DateTime> NowAsync() => (await ScalarAsync<DateTime?>("select lastseat_now()"))!.Value;

    NpgsqlCommand Command(string text, (string Name, object? Value)[] args)
    {
        var command = new NpgsqlCommand(text, connection, transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.Add(value switch
            {
                // An untyped null: PostgreSQL infers the type from the column it is compared with.
                null => new NpgsqlParameter(name, DBNull.Value),
                _ => new NpgsqlParameter(name, value),
            });
        }
        return command;
    }
}
