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
}
