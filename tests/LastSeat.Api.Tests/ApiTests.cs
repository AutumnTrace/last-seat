using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LastSeat.Api.Allocation;
using LastSeat.Api.Data;
using LastSeat.Api.Endpoints;
using LastSeat.Api.Queries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace LastSeat.Api.Tests;

// The HTTP surface and the SignalR hint, against the seeded demo performance.
public sealed class ApiTests(PostgresServer server)
{
    sealed class Api(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:LastSeat", connectionString);
            builder.UseSetting("Cleanup:Enabled", "false");
            builder.UseSetting("Demo:SeedOnStartup", "true");
        }
    }

    static int F(int number) => DemoSeed.Layout.SeatId(5, number);

    static async Task<Guid> SessionAsync(HttpClient http, string name)
    {
        var response = await http.PostAsJsonAsync("/api/sessions", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessionBody>(JsonSerializerOptions.Web))!.Id;
    }

    static HttpRequestMessage Hold(Guid session, string key, params int[] seats)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/performances/1/holds")
        {
            Content = JsonContent.Create(new { seatIds = seats }),
        };
        request.Headers.Add(BookingApi.SessionHeader, session.ToString());
        request.Headers.Add(BookingApi.RequestKeyHeader, key);
        return request;
    }

    static async Task<SeatSnapshotBody> SnapshotAsync(HttpClient http, Guid? session = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/performances/1/seats");
        if (session is not null)
        {
            request.Headers.Add(BookingApi.SessionHeader, session.ToString());
        }
        var response = await http.SendAsync(request);
        return (await response.Content.ReadFromJsonAsync<SeatSnapshotBody>(JsonSerializerOptions.Web))!;
    }

    [Fact]
    public async Task The_seed_leaves_the_last_two_seats_together_in_row_F()
    {
        await using var api = new Api(await server.CreateDatabaseAsync());
        var http = api.CreateClient();
        var snapshot = await SnapshotAsync(http);

        Assert.Equal(["B2", "D12", "F7", "F8", "H4", "K14"],
            snapshot.Seats.Where(s => s.State == "available").Select(s => s.Label));
        Assert.Equal(140, snapshot.Seats.Count);
    }

    [Fact]
    public async Task The_flagship_race_over_http()
    {
        await using var api = new Api(await server.CreateDatabaseAsync());
        var http = api.CreateClient();
        var ada = await SessionAsync(http, "Ada");
        var grace = await SessionAsync(http, "Grace");

        var responses = await Task.WhenAll(
            http.SendAsync(Hold(ada, "a", F(7), F(8))),
            http.SendAsync(Hold(grace, "g", F(7), F(8))));

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var body = (await loser.Content.ReadFromJsonAsync<SeatsUnavailable>(JsonSerializerOptions.Web))!;
        Assert.Equal(["F7", "F8"], body.Conflicts.Select(c => c.Label));
        Assert.Empty(body.StillAvailable);
        // No other pair is left, so the suggestion is the two closest single seats.
        var apart = Assert.Single(body.Alternatives);
        Assert.Equal("apart", apart.Kind);
    }

    [Fact]
    public async Task Requests_without_a_session_or_key_are_refused()
    {
        await using var api = new Api(await server.CreateDatabaseAsync());
        var http = api.CreateClient();
        var ada = await SessionAsync(http, "Ada");

        var noSession = Hold(ada, "k", F(7));
        noSession.Headers.Remove(BookingApi.SessionHeader);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(noSession)).StatusCode);

        var noKey = Hold(ada, "k", F(7));
        noKey.Headers.Remove(BookingApi.RequestKeyHeader);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(noKey)).StatusCode);

        var retry1 = await http.SendAsync(Hold(ada, "k", F(7)));
        var retry2 = await http.SendAsync(Hold(ada, "k", F(7)));
        Assert.Equal(HttpStatusCode.Created, retry2.StatusCode);
        Assert.False(retry1.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", retry2.Headers.GetValues("Idempotent-Replayed").Single());
    }

    [Fact]
    public async Task The_snapshot_shows_an_expired_hold_as_available_before_any_cleanup()
    {
        await using var api = new Api(await server.CreateDatabaseAsync());
        var http = api.CreateClient();
        var ada = await SessionAsync(http, "Ada");
        Assert.Equal(HttpStatusCode.Created, (await http.SendAsync(Hold(ada, "k", F(7)))).StatusCode);

        var during = await SnapshotAsync(http, ada);
        Assert.Equal("held", during.Seats.Single(s => s.Id == F(7)).State);
        Assert.True(during.Seats.Single(s => s.Id == F(7)).Yours);
        Assert.NotNull(during.Hold);

        var advance = await http.PostAsJsonAsync("/api/demo/clock/advance", new { seconds = 91 });
        Assert.Equal(HttpStatusCode.NoContent, advance.StatusCode);

        var after = await SnapshotAsync(http, ada);
        Assert.Equal("available", after.Seats.Single(s => s.Id == F(7)).State);
        Assert.Null(after.Hold);
    }

    // The client refreshes when it hears "seatsChanged". The message is sent after commit, so
    // the snapshot fetched in response must already show the change.
    [Fact]
    public async Task A_refresh_hint_arrives_after_the_change_is_visible()
    {
        await using var api = new Api(await server.CreateDatabaseAsync());
        var http = api.CreateClient();
        var ada = await SessionAsync(http, "Ada");

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/seats"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
            })
            .Build();
        var seen = new TaskCompletionSource<SeatSnapshotBody>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<int>("seatsChanged", async performanceId =>
        {
            if (performanceId == 1)
            {
                seen.TrySetResult(await SnapshotAsync(http));
            }
        });
        await hub.StartAsync();
        await hub.InvokeAsync("Watch", 1);

        Assert.Equal(HttpStatusCode.Created, (await http.SendAsync(Hold(ada, "k", F(7), F(8)))).StatusCode);

        var snapshot = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("held", snapshot.Seats.Single(s => s.Id == F(7)).State);
        Assert.Equal("held", snapshot.Seats.Single(s => s.Id == F(8)).State);
    }

    [Fact]
    public async Task Demo_reset_restores_the_seed()
    {
        await using var api = new Api(await server.CreateDatabaseAsync());
        var http = api.CreateClient();
        var ada = await SessionAsync(http, "Ada");
        await http.SendAsync(Hold(ada, "k", F(7), F(8)));

        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync("/api/demo/reset", null)).StatusCode);

        var snapshot = await SnapshotAsync(http);
        Assert.Equal(6, snapshot.Seats.Count(s => s.State == "available"));
        var box = (await http.GetFromJsonAsync<BoxOfficeBody>("/api/box-office/1", JsonSerializerOptions.Web))!;
        Assert.Empty(box.Holds);
        Assert.Empty(box.Events);
    }
}
