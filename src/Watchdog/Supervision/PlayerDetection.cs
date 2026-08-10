using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Whether an instance's player presence can be observed at all, and by what.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one place the question is answered.</b> Two ingesters decide independently whether
/// to watch an instance — the log matcher and the RCON poller — and a consumer asking "is anybody
/// online" needs to know whether an empty answer means nobody or means nobody can tell. Every
/// surface re-deriving that from the instance's config is how they come to disagree, and the
/// derivation is not something a consumer can get right: it includes whether a regex <i>compiles</i>.
/// </para>
/// <para>
/// So the predicate lives here, the ingesters gate on it, and <c>GET /players</c> reports it. A
/// consumer is told what this daemon actually does rather than guessing from the same inputs.
/// </para>
/// </remarks>
internal static class PlayerDetection
{
    /// <summary>
    /// Whether presence is read out of the game's own log output.
    /// </summary>
    /// <remarks>
    /// Native instances are matched here by <see cref="NativeLogMatcher"/>; container instances are
    /// matched in-image by the shim, handed the same blueprint patterns. Same patterns, same verdict,
    /// different place the matching runs — which is why runtime does not appear in this check.
    /// <para>
    /// A pattern that does not compile is not detection. The matcher reports it as disabled and warns,
    /// and this agrees with it by asking the matcher rather than by re-testing the strings.
    /// </para>
    /// </remarks>
    public static bool FromLog(Instance instance) =>
        new NativeLogMatcher(instance.PlayerJoinedRegex ?? "", instance.PlayerLeftRegex ?? "").Enabled;

    /// <summary>
    /// Whether presence is polled over RCON — the fallback for a game that logs connects but not
    /// disconnects.
    /// </summary>
    /// <remarks>
    /// Five things have to hold, and the last two are the ones a consumer would miss: without a
    /// command there is nothing to ask, and without a <i>compiling</i> response pattern the reply
    /// cannot be read — either way the poll would return an empty roster for want of parsing, which
    /// is indistinguishable from nobody being connected. RCON is native-only because a container's
    /// port is not this daemon's to reach.
    /// </remarks>
    public static bool FromRcon(Instance instance) =>
        instance.Runtime == InstanceRuntime.Native
        && instance.RconPort is not null
        && !string.IsNullOrEmpty(instance.RconPassword)
        && !string.IsNullOrWhiteSpace(instance.RconPlayersCommand)
        && RconPlayerResponseParser.IsValidPattern(instance.RconPlayersRegex);

    /// <summary>
    /// How this instance's presence is detected, or <see cref="PlayerDetectionMechanism.None"/>.
    /// </summary>
    /// <remarks>
    /// <b>The log wins the label when both are wired</b>, and both being wired is normal. The log
    /// carries real transitions — a join is a join — where a poll reports a snapshot and infers the
    /// transitions by diffing it, which cannot see churn between two polls. So the label names the
    /// mechanism whose answers are exact, not the only one running.
    /// </remarks>
    public static PlayerDetectionMechanism For(Instance instance) =>
        FromLog(instance) ? PlayerDetectionMechanism.Log
        : FromRcon(instance) ? PlayerDetectionMechanism.Rcon
        : PlayerDetectionMechanism.None;
}

/// <summary>How an instance's player presence is observed.</summary>
public enum PlayerDetectionMechanism
{
    /// <summary>
    /// Not observable. An empty player list for this instance means <b>nobody can tell</b>, and a
    /// consumer that renders it as "0 online" is stating something this host does not know.
    /// </summary>
    None = 0,

    /// <summary>Matched out of the game's log output. Real transitions.</summary>
    Log = 1,

    /// <summary>Polled over RCON and diffed. Cannot see churn between two polls.</summary>
    Rcon = 2,
}
