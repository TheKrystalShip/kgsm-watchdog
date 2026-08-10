using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The one predicate that says whether an instance's players can be seen at all.
/// </summary>
/// <remarks>
/// This is the rule <c>GET /players</c> reports and the RCON poller gates on, and downstream every
/// surface reads it rather than deriving its own. What it gets wrong, they all get wrong — in the
/// direction that matters most, because a false "detectable" turns an empty roster into a confident
/// "nobody is online" about a game that never says.
/// </remarks>
public sealed class PlayerDetectionTests
{
    private const string ValidJoin = @"\[JOIN\] (?<name>\S+) joined";
    private const string ValidLeft = @"\[LEAVE\] (?<name>\S+) left";
    private const string ValidRosterLine = @"^-\s*(?<name>\S.*?)\s*$";

    private static Instance Rcon(
        string? command = "players",
        string? regex = ValidRosterLine,
        int? port = 27015,
        string password = "secret",
        InstanceRuntime runtime = InstanceRuntime.Native) =>
        new()
        {
            Name = "instance",
            Runtime = runtime,
            RconPort = port,
            RconPassword = password,
            RconPlayersCommand = command ?? string.Empty,
            RconPlayersRegex = regex ?? string.Empty,
        };

    // ── log detection ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_join_pattern_alone_is_detection()
    {
        // Half a pair still observes something real. A game that logs connects but not disconnects
        // is the case RCON polling exists to complete, not a reason to call it unobservable.
        var instance = new Instance { Name = "i", PlayerJoinedRegex = ValidJoin };

        Assert.True(PlayerDetection.FromLog(instance));
        Assert.Equal(PlayerDetectionMechanism.Log, PlayerDetection.For(instance));
    }

    [Fact]
    public void No_patterns_is_not_detection()
    {
        var instance = new Instance { Name = "i" };

        Assert.False(PlayerDetection.FromLog(instance));
        Assert.Equal(PlayerDetectionMechanism.None, PlayerDetection.For(instance));
    }

    /// <summary>
    /// A pattern that does not compile is not detection, and this is the half a consumer deriving
    /// the answer from the same config would get wrong — the string is there, so it looks configured.
    /// </summary>
    [Fact]
    public void A_pattern_that_does_not_compile_is_not_detection()
    {
        var instance = new Instance { Name = "i", PlayerJoinedRegex = "(?<name>[unterminated" };

        Assert.False(PlayerDetection.FromLog(instance));
        Assert.Equal(PlayerDetectionMechanism.None, PlayerDetection.For(instance));
    }

    /// <summary>
    /// Runtime is deliberately absent from the log check: a container matches the same blueprint
    /// patterns in-image via the shim. Same patterns, same verdict, different place it runs.
    /// </summary>
    [Fact]
    public void Log_detection_does_not_depend_on_runtime()
    {
        var container = new Instance
        {
            Name = "i",
            Runtime = InstanceRuntime.Container,
            PlayerJoinedRegex = ValidJoin,
            PlayerLeftRegex = ValidLeft,
        };

        Assert.True(PlayerDetection.FromLog(container));
    }

    // ── RCON detection ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fully_wired_rcon_is_detection()
    {
        Instance instance = Rcon();

        Assert.True(PlayerDetection.FromRcon(instance));
        Assert.Equal(PlayerDetectionMechanism.Rcon, PlayerDetection.For(instance));
    }

    /// <summary>
    /// The four ways RCON is wired but useless. Each leaves the poll returning an empty roster for
    /// want of asking or want of parsing — indistinguishable from nobody being connected, which is
    /// exactly the claim this predicate exists to refuse.
    /// </summary>
    [Theory]
    [InlineData(null, ValidRosterLine, 27015, "secret")]           // nothing to ask
    [InlineData("players", null, 27015, "secret")]                  // reply cannot be read
    [InlineData("players", "(?<name>[unterminated", 27015, "secret")] // pattern does not compile
    [InlineData("players", ValidRosterLine, null, "secret")]        // no port to reach
    [InlineData("players", ValidRosterLine, 27015, "")]             // no password to authenticate
    public void Rcon_missing_any_piece_is_not_detection(
        string? command, string? regex, int? port, string password)
    {
        Instance instance = Rcon(command, regex, port, password);

        Assert.False(PlayerDetection.FromRcon(instance));
        Assert.Equal(PlayerDetectionMechanism.None, PlayerDetection.For(instance));
    }

    /// <summary>A container's RCON port is not this daemon's to reach.</summary>
    [Fact]
    public void Rcon_is_native_only()
    {
        Assert.False(PlayerDetection.FromRcon(Rcon(runtime: InstanceRuntime.Container)));
    }

    // ── precedence ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Both wired is normal, and the log takes the label: it carries real transitions where a poll
    /// reports a snapshot and infers them by diffing, which cannot see churn between two polls.
    /// </summary>
    [Fact]
    public void The_log_wins_the_label_when_both_are_wired()
    {
        Instance instance = Rcon();
        instance.PlayerJoinedRegex = ValidJoin;
        instance.PlayerLeftRegex = ValidLeft;

        Assert.True(PlayerDetection.FromLog(instance));
        Assert.True(PlayerDetection.FromRcon(instance));
        Assert.Equal(PlayerDetectionMechanism.Log, PlayerDetection.For(instance));
    }

    /// <summary>
    /// The wire spellings, pinned. These strings are the contract three repos read; renaming one
    /// silently turns every consumer's "is this detectable" check into a no.
    /// </summary>
    [Theory]
    [InlineData(PlayerDetectionMechanism.None, "none")]
    [InlineData(PlayerDetectionMechanism.Log, "log")]
    [InlineData(PlayerDetectionMechanism.Rcon, "rcon")]
    public void The_wire_spelling_is_the_lowercased_name(PlayerDetectionMechanism mechanism, string expected)
    {
        Assert.Equal(expected, mechanism.ToString().ToLowerInvariant());
    }
}
