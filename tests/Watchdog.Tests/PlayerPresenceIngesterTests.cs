using TheKrystalShip.KGSM.Watchdog.Events;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the ingester's discovery (two-level walk, including <b>through the instance symlink</b> the
/// real kgsm layout uses), instance-name derivation, instances-dir resolution, and the emit shape it
/// hands kgsm-lib — event name (dash form), provenance (<c>actor=null / origin="system"</c>), and the
/// positional param order (<c>instance, id, name</c>, with null id/name as an empty positional arg).
/// </summary>
[Collection(EnvironmentCollection.Name)] // ResolveInstancesDir tests mutate HOME/XDG_DATA_HOME — serialize with the other env-touching classes
public sealed class PlayerPresenceIngesterTests : IDisposable
{
    private readonly string _root;

    public PlayerPresenceIngesterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kgsm-wd-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- instance-name derivation ------------------------------------------------------------

    [Theory]
    [InlineData("/data/kgsm/instances/factorio/factorio-test/events/events.ndjson", "factorio-test")]
    [InlineData("/data/kgsm/instances/minecraft/mc-07/events/events.ndjson", "mc-07")]
    [InlineData("/x/y/terraria/terraria-hardmode/events/events.ndjson", "terraria-hardmode")]
    public void Derives_instance_name_from_the_dir_two_levels_above_the_file(string path, string expected)
    {
        Assert.Equal(expected, PlayerPresenceIngester.DeriveInstanceName(path));
    }

    // ---- discovery ----------------------------------------------------------------------------

    [Fact]
    public void Discovers_two_level_channels_and_ignores_instances_without_one()
    {
        // <root>/<blueprint>/<instance>/events/events.ndjson
        string has = MakeChannel("factorio", "factorio-test", "{}\n");
        MakeInstanceDir("factorio", "factorio-other"); // no events/ → must be ignored
        string has2 = MakeChannel("terraria", "terraria-hardmode", "{}\n");

        var found = PlayerPresenceIngester.DiscoverChannels(_root).ToHashSet();

        Assert.Contains(has, found);
        Assert.Contains(has2, found);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Discovers_a_channel_reached_through_an_instance_symlink()
    {
        // Mirror the REAL kgsm layout: <root>/<blueprint>/<instance> is a symlink to the working dir,
        // and events/events.ndjson lives under the symlink target. Discovery must traverse it.
        string blueprintDir = Path.Combine(_root, "factorio");
        Directory.CreateDirectory(blueprintDir);

        string workingDir = Path.Combine(_root, "_real", "factorio", "factorio-test");
        Directory.CreateDirectory(Path.Combine(workingDir, "events"));
        File.WriteAllText(Path.Combine(workingDir, "events", "events.ndjson"), "{}\n");

        string symlink = Path.Combine(blueprintDir, "factorio-test");
        Directory.CreateSymbolicLink(symlink, workingDir);

        var found = PlayerPresenceIngester.DiscoverChannels(_root).ToList();

        string expected = Path.Combine(symlink, "events", "events.ndjson");
        Assert.Contains(expected, found);
        Assert.Equal("factorio-test", PlayerPresenceIngester.DeriveInstanceName(expected)); // name off the symlink path
    }

    [Fact]
    public void Missing_root_discovers_nothing_without_throwing()
    {
        Assert.Empty(PlayerPresenceIngester.DiscoverChannels(Path.Combine(_root, "nope")));
    }

    // ---- instances-dir resolution -------------------------------------------------------------

    [Fact]
    public void Explicit_instances_dir_option_wins()
    {
        var ingester = NewIngester(new WatchdogOptions { InstancesDir = "/custom/instances" }, new RecordingJournal());
        Assert.Equal("/custom/instances", ingester.ResolveInstancesDir());
    }

    [Fact]
    public void Default_instances_dir_derives_under_HOME_when_XDG_unset()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", "/home/kgsm");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);

            var ingester = NewIngester(new WatchdogOptions(), new RecordingJournal());
            Assert.Equal("/home/kgsm/.local/share/kgsm/instances", ingester.ResolveInstancesDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
        }
    }

    [Fact]
    public void Default_instances_dir_prefers_XDG_DATA_HOME()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HOME", "/home/kgsm");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/xdg/data");

            var ingester = NewIngester(new WatchdogOptions(), new RecordingJournal());
            Assert.Equal("/xdg/data/kgsm/instances", ingester.ResolveInstancesDir());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
        }
    }

    // ---- the emit shape (end-to-end through one ingest pass via reflection-free internals) -----

    [Fact]
    public void Ingest_pass_emits_join_and_left_with_correct_name_provenance_and_param_order()
    {
        MakeChannel("factorio", "factorio-test",
            """{"type":"player_joined","id":"steam-1","name":"Alice","ts":"2026-06-19T12:00:00Z"}""" + "\n" +
            """{"type":"player_left","id":null,"name":"Bob","ts":"2026-06-19T12:01:00Z"}""" + "\n");

        var rec = new RecordingJournal();
        var ingester = NewIngester(new WatchdogOptions { InstancesDir = _root }, rec);

        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);

        var join = rec.Calls[0];
        Assert.Equal("player.joined", join.Type);
        Assert.Equal("system:watchdog", join.Actor);   // autonomous: actor=system:watchdog, NOT null (null → OS-user fallback on the wire)
        Assert.Equal("system", join.Origin);
        Assert.Equal("factorio-test", join.String("InstanceName"));
        Assert.Equal("steam-1", join.String("PlayerId"));
        Assert.Equal("Alice", join.String("PlayerName"));
        // The container shim reports neither an address nor a session key, and neither is invented.
        Assert.True(join.IsNull("PlayerAddr"));
        Assert.Equal(string.Empty, join.String("SessionKey"));

        var left = rec.Calls[1];
        Assert.Equal("player.left", left.Type);
        Assert.Equal("system:watchdog", left.Actor);
        Assert.Equal("system", left.Origin);
        // null id → empty positional arg, name preserved, instance first.
        Assert.Equal("factorio-test", left.String("InstanceName"));
        Assert.True(left.IsNull("PlayerId"));
        Assert.Equal("Bob", left.String("PlayerName"));
    }

    [Fact]
    public void Both_null_line_is_dropped_not_emitted()
    {
        MakeChannel("factorio", "factorio-test",
            """{"type":"player_joined","id":null,"name":null,"ts":"2026-06-19T12:00:00Z"}""" + "\n");

        var rec = new RecordingJournal();
        var ingester = NewIngester(new WatchdogOptions { InstancesDir = _root }, rec);

        ingester.IngestOnce(_root);

        Assert.Empty(rec.Calls); // the defensive guard kept the junk event off the wire
    }

    [Fact]
    public void Appended_lines_are_not_redelivered_on_a_second_pass()
    {
        string channel = MakeChannel("factorio", "factorio-test",
            """{"type":"player_joined","id":"a","name":"A","ts":"t"}""" + "\n");

        var rec = new RecordingJournal();
        var ingester = NewIngester(new WatchdogOptions { InstancesDir = _root }, rec);

        ingester.IngestOnce(_root);
        Assert.Single(rec.Calls); // the same ingester instance keeps its per-channel tail cursor

        File.AppendAllText(channel, """{"type":"player_left","id":"a","name":"A","ts":"t"}""" + "\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count); // only the new line emitted on pass 2 (no redelivery)
        Assert.Equal("player.left", rec.Calls[1].Type);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private PlayerPresenceIngester NewIngester(WatchdogOptions options, WatchdogJournal journal)
        => new(options, journal, NullLogger<PlayerPresenceIngester>.Instance);

    private string MakeInstanceDir(string blueprint, string instance)
    {
        string dir = Path.Combine(_root, blueprint, instance);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string MakeChannel(string blueprint, string instance, string contents)
    {
        string instanceDir = MakeInstanceDir(blueprint, instance);
        string eventsDir = Path.Combine(instanceDir, "events");
        Directory.CreateDirectory(eventsDir);
        string channel = Path.Combine(eventsDir, "events.ndjson");
        File.WriteAllText(channel, contents);
        return channel;
    }

    /// <summary>A fake event service that records every <see cref="IEventManagementService.EmitWithProvenance"/> call.</summary>
}
