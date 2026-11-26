using LastSeat.Api.Allocation;
using LastSeat.Api.Data;
using LastSeat.Api.Endpoints;
using LastSeat.Api.Queries;
using LastSeat.Api.Realtime;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Used by the container HEALTHCHECK: the runtime image has no curl.
if (args.Contains("--health-check"))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try
    {
        return (await http.GetAsync("http://localhost:8080/health")).IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("LastSeat")
    ?? throw new InvalidOperationException("ConnectionStrings:LastSeat is not set.");
builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddDbContext<LastSeatDb>((sp, options) => options
    .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
    .UseSnakeCaseNamingConvention());

builder.Services.AddSignalR();
builder.Services.AddSingleton<ISeatChangeNotifier, SignalRSeatChangeNotifier>();
builder.Services.AddSingleton<SeatAllocator>();
builder.Services.AddSingleton<HoldCleanup>();
builder.Services.AddSingleton<DemoClockControl>();
builder.Services.AddSingleton<SeatSnapshot>();
builder.Services.AddScoped<BoxOffice>();
if (builder.Configuration.GetValue("Cleanup:Enabled", true))
{
    builder.Services.AddHostedService<HoldCleanupService>();
}

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LastSeatDb>();
    await db.Database.MigrateAsync();
    var reset = args.Contains("reset");
    if (reset || (app.Configuration.GetValue("Demo:SeedOnStartup", true) && await DemoSeed.IsEmptyAsync(db)))
    {
        await DemoSeed.ResetAsync(db);
        app.Logger.LogInformation("Seeded the demo performance");
    }
    if (reset)
    {
        return 0;
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapBookingApi();
if (app.Configuration.GetValue("Demo:Enabled", true))
{
    app.MapDemoApi();
}
app.MapHub<SeatHub>("/hubs/seats");
app.MapFallbackToFile("index.html");

await app.RunAsync();
return 0;

public partial class Program;
