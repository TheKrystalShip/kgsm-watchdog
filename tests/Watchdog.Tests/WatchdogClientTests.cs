using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Watchdog.Client;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The typed client against a real HTTP server on a unix socket, answering in the daemon's wire shapes.
/// Each answer is read from the body; the status line only chooses which kind of answer it is.
/// </summary>
public sealed class WatchdogClientTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wc" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? _app;

    private string SocketPath => Path.Combine(_directory, "c.sock");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(SocketPath));
        builder.Logging.ClearProviders();
        _app = builder.Build();

        InstanceState running = new("factorio-a", "running", true, true, 4242, "/sys/fs/cgroup/x", "running", 0, "started");

        _app.MapGet("/health", () => Results.Json(new ReadyState(true, "in the slice"), (System.Text.Json.JsonSerializerOptions?)null));
        _app.MapGet("/status/{name}", (string name) =>
            name == "factorio-a" ? Results.Json(running, JsonCamel) : Results.StatusCode(404));
        _app.MapGet("/list", () => Results.Json(new[] { running }, JsonCamel));
        _app.MapGet("/enabled", () => Results.Json(new[] { "factorio-a" }, JsonCamel));
        _app.MapPost("/start/{name}", (string name, string? force, string? origin) =>
            force == "true"
                ? Results.Json(new ActionResult(name, true, $"started by {origin}"), JsonCamel)
                : Results.Json(new ActionResult(name, false, "no room", ActionRefusal.NoRoom), JsonCamel, statusCode: 507));
        _app.MapDelete("/instance/{name}", (string name) =>
            Results.Json(new ActionResult(name, false, "still running"), JsonCamel, statusCode: 409));

        _app.MapPost("/maintenance/begin/{name}", (string name, string? origin) =>
            Results.Json(new ActionResult(name, true, $"parked by {origin}"), JsonCamel));
        _app.MapPost("/set-cpu-priority/{name}/{priority}", (string name, string priority) =>
            Results.Json(new ActionResult(name, true, priority), JsonCamel));
        _app.MapGet("/console/{name}/runs", (string name) =>
            name == "factorio-a"
                ? Results.Json(new[] { new ConsoleRun(0, true, null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 12, "running", null) }, JsonCamel)
                : Results.StatusCode(404));
        _app.MapGet("/console/{name}", (string name, int? tail, HttpContext http) =>
        {
            if (name != "factorio-a") return Results.StatusCode(404);
            http.Response.Headers["X-Console-Start"] = "10";
            http.Response.Headers["X-Console-End"] = "30";
            return Results.Text("one\ntwo\n");
        });
        _app.MapGet("/console/{name}/follow", async (string name, HttpContext http) =>
        {
            http.Response.ContentType = "text/plain";
            await http.Response.WriteAsync("first\nsecond\n");
            await http.Response.Body.FlushAsync();
            await Task.Delay(Timeout.Infinite, http.RequestAborted).ContinueWith(_ => { });
        });
        _app.MapGet("/console/{name}/download", (string name) =>
            name == "factorio-a" ? Results.Text("whole run\n") : Results.StatusCode(404));
        _app.MapGet("/players", () => Results.Json(
            new Dictionary<string, InstancePresence>
            {
                ["factorio-a"] = new("log", [new PlayerSession("k", "1", "ann", null)]),
                ["terraria-b"] = new("none", []),
            }, JsonCamel));
        _app.MapGet("/upnp/{name}", (string name) => Results.Json(
            new UpnpListResult(name, "unavailable", []), JsonCamel));
        _app.MapGet("/memory-gate", () => Results.Json(new MemoryGateState(true, 1024), JsonCamel));

        await _app.StartAsync(CancellationToken.None);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonCamel = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public async Task Health_reads_the_body_of_a_ready_daemon()
    {
        using var client = new WatchdogClient(SocketPath);

        WatchdogHealth health = await client.HealthAsync(CancellationToken.None);

        Assert.Equal(new WatchdogHealth(true, true, "in the slice"), health);
    }

    [Fact]
    public async Task Health_of_an_absent_socket_is_unreachable_and_never_an_exception()
    {
        using var client = new WatchdogClient(Path.Combine(_directory, "absent.sock"));

        WatchdogHealth health = await client.HealthAsync(CancellationToken.None);

        Assert.False(health.Reachable);
        Assert.False(health.Ready);
    }

    [Fact]
    public async Task Status_returns_the_measured_state_and_null_for_an_untracked_instance()
    {
        using var client = new WatchdogClient(SocketPath);

        InstanceState? tracked = await client.StatusAsync("factorio-a", CancellationToken.None);
        InstanceState? untracked = await client.StatusAsync("nothing", CancellationToken.None);

        Assert.True(tracked!.Populated);
        Assert.Equal(4242, tracked.Pid);
        Assert.Null(untracked);
    }

    [Fact]
    public async Task List_and_enabled_read_their_arrays()
    {
        using var client = new WatchdogClient(SocketPath);

        Assert.Equal("factorio-a", Assert.Single(await client.ListAsync(CancellationToken.None)).Name);
        Assert.Equal(["factorio-a"], await client.EnabledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_capacity_refusal_is_an_answer_carried_in_the_body()
    {
        using var client = new WatchdogClient(SocketPath);

        ActionResult refused = await client.StartAsync("factorio-a", cancellationToken: CancellationToken.None);
        ActionResult forced = await client.StartAsync("factorio-a", force: true, origin: "cli", cancellationToken: CancellationToken.None);

        Assert.False(refused.Ok);
        Assert.Equal(ActionRefusal.NoRoom, refused.Refusal);
        Assert.True(forced.Ok);
        Assert.Equal("started by cli", forced.Message);
    }

    [Fact]
    public async Task A_conflict_is_an_answer_with_its_message()
    {
        using var client = new WatchdogClient(SocketPath);

        ActionResult forgotten = await client.ForgetAsync("factorio-a", CancellationToken.None);

        Assert.False(forgotten.Ok);
        Assert.Equal("still running", forgotten.Message);
    }

    [Fact]
    public async Task A_request_to_an_absent_daemon_throws_unavailable()
    {
        using var client = new WatchdogClient(Path.Combine(_directory, "absent.sock"));

        await Assert.ThrowsAsync<WatchdogUnavailableException>(() => client.StatusAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task Maintenance_and_cpu_priority_are_actions_with_their_answers()
    {
        using var client = new WatchdogClient(SocketPath);

        Assert.Equal("parked by scheduler", (await client.BeginMaintenanceAsync("factorio-a", "scheduler")).Message);
        Assert.Equal("low", (await client.SetCpuPriorityAsync("factorio-a", "low")).Message);
    }

    [Fact]
    public async Task A_console_window_carries_its_lines_and_its_byte_range()
    {
        using var client = new WatchdogClient(SocketPath);

        ConsoleWindow window = await client.ConsoleWindowAsync("factorio-a", 2);

        Assert.Equal(["one", "two"], window.Lines);
        Assert.Equal(10, window.Start);
        Assert.Equal(30, window.End);
        Assert.True(window.HasEarlier);
        Assert.Empty((await client.ConsoleWindowAsync("nothing", 2)).Lines);
    }

    [Fact]
    public async Task A_console_that_does_not_exist_is_empty_and_never_an_error()
    {
        using var client = new WatchdogClient(SocketPath);

        Assert.Equal(0, (await client.ConsoleRunsAsync("nothing")).Count);
        Assert.Null(await client.OpenConsoleDownloadAsync("nothing"));
        Assert.Empty(await client.ConsoleTailAsync("nothing", 5));
        Assert.Equal(12, Assert.Single(await client.ConsoleRunsAsync("factorio-a")).SizeBytes);
    }

    [Fact]
    public async Task A_follow_yields_lines_until_the_caller_cancels()
    {
        using var client = new WatchdogClient(SocketPath);
        using var cancel = new CancellationTokenSource();
        var seen = new List<string>();

        try
        {
            await foreach (string line in client.FollowConsoleAsync("factorio-a", cancel.Token))
            {
                seen.Add(line);
                if (seen.Count == 2) await cancel.CancelAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(["first", "second"], seen);
    }

    [Fact]
    public async Task A_download_is_a_stream_with_its_length()
    {
        using var client = new WatchdogClient(SocketPath);

        using ConsoleDownload download = (await client.OpenConsoleDownloadAsync("factorio-a"))!;
        using var reader = new StreamReader(download.Content);

        Assert.Equal("whole run\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Players_carry_how_each_instance_is_detected_and_upnp_its_honest_state()
    {
        using var client = new WatchdogClient(SocketPath);

        IReadOnlyDictionary<string, InstancePresence> players = (await client.PlayersAsync())!;
        Assert.True(players["factorio-a"].IsDetected);
        Assert.Equal("ann", Assert.Single(players["factorio-a"].Players).Name);
        Assert.False(players["terraria-b"].IsDetected);
        Assert.Equal("unavailable", (await client.UpnpAsync("factorio-a"))!.State);
        Assert.Equal(new MemoryGateState(true, 1024), await client.MemoryGateAsync());
    }

    [Fact]
    public async Task Players_and_upnp_of_an_absent_daemon_are_unknown_not_errors()
    {
        using var client = new WatchdogClient(Path.Combine(_directory, "absent.sock"));

        Assert.Null(await client.PlayersAsync());
        Assert.Null(await client.UpnpAsync("x"));
        Assert.False(await client.IsReadyAsync());
    }
}
