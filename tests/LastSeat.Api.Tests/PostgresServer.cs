using LastSeat.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(LastSeat.Api.Tests.PostgresServer))]

namespace LastSeat.Api.Tests;

// Real PostgreSQL for every test. By default a Testcontainers postgres:18 container; set
// LASTSEAT_TEST_DB to a server connection string (no database) to use a server you already run.
// Each test gets a fresh database cloned from a migrated template, so tests never share state.
public sealed class PostgresServer : IAsyncLifetime
{
    readonly string run = Guid.NewGuid().ToString("N")[..8];
    PostgreSqlContainer? container;
    int databases;

    public string ServerConnectionString { get; private set; } = "";

    string TemplateName => $"lastseat_tpl_{run}";

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("LASTSEAT_TEST_DB");
        if (!string.IsNullOrWhiteSpace(external))
        {
            ServerConnectionString = external;
        }
        else
        {
            container = new PostgreSqlBuilder("postgres:18-alpine")
                .WithCommand("-c", "max_connections=300")
                .Build();
            await container.StartAsync();
            ServerConnectionString = container.GetConnectionString();
        }

        await AdminAsync($"create database {TemplateName}");
        var options = new DbContextOptionsBuilder<LastSeatDb>()
            .UseNpgsql(For(TemplateName, pooling: false))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new LastSeatDb(options);
        await db.Database.MigrateAsync();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"lastseat_test_{run}_{Interlocked.Increment(ref databases)}";
        await AdminAsync($"create database {name} template {TemplateName}");
        return For(name, pooling: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
            return;
        }
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(For("postgres", pooling: false));
        await conn.OpenAsync();
        var names = new List<string>();
        await using (var list = new NpgsqlCommand($"select datname from pg_database where datname like 'lastseat_%{run}%'", conn))
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }
        foreach (var name in names)
        {
            await using var drop = new NpgsqlCommand($"drop database if exists {name} with (force)", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    string For(string database, bool pooling) => new NpgsqlConnectionStringBuilder(ServerConnectionString)
    {
        Database = database,
        Pooling = pooling,
        MaxPoolSize = 150,
        IncludeErrorDetail = true,
    }.ConnectionString;

    async Task AdminAsync(string text)
    {
        await using var conn = new NpgsqlConnection(For("postgres", pooling: false));
        await conn.OpenAsync();
        await using var command = new NpgsqlCommand(text, conn);
        await command.ExecuteNonQueryAsync();
    }
}
