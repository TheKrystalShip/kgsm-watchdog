using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Firewall.Contracts;
using Microsoft.Extensions.Logging;
namespace TheKrystalShip.KGSM.Core.Models;

// Result types for the kgsm-firewall control client (IFirewallService). Unlike the watchdog DTOs these
// are NOT deserialized from the wire — FirewallService maps them in code from the
// TheKrystalShip.KGSM.Firewall.Contracts wire DTOs at its boundary — so they carry no [JsonPropertyName]
// and need no KgsmJsonContext registration. They deliberately preserve the authority's precise outcome
// (the C# analog of kgsm-firewall's exit-code contract: applied/removed/noop vs unsupported/op-failed vs
// honest-unknown), never collapsing it to a bare success bool — kgsm-api's future honest "is this port
// open" verdict depends on telling "the backend cannot answer" apart from "nothing is open".

/// <summary>
/// The precise outcome of an ensure-open / remove operation, mapped from the authority's wire token.
/// Distinct from the coarse <c>FirewallActionResult.Ok</c> bit so a caller can tell an applied
/// change from a no-op, and an unsupported backend from an outright failure.
/// </summary>
public enum FirewallOutcome
{
    /// <summary>The rule was applied and the backend is enforcing (the ports are now open for the instance).</summary>
    Applied,

    /// <summary>The rule was written/staged, but the backend is NOT enforcing (e.g. ufw inactive): it
    /// persists and takes effect on the operator's next <c>ufw enable</c>, and meanwhile the port is open
    /// anyway (nothing is filtering). A success — distinct from <c>Applied</c> so a caller can say
    /// "staged, not yet enforced" rather than imply an enforced open (Firewall.Contracts 1.1.0).</summary>
    AppliedInactive,

    /// <summary>The instance's rules were removed.</summary>
    Removed,

    /// <summary>Nothing to do — the desired state already held (e.g. ensure-open with no ports).</summary>
    NoOp,

    /// <summary>A read/query succeeded (used on the backend op; not an apply/remove result).</summary>
    Ok,

    /// <summary>The backend genuinely cannot answer — honest unknown, never a guessed empty/false.</summary>
    Unknown,

    /// <summary>The active backend does not support the operation (e.g. no firewall, or a read-only driver).</summary>
    Unsupported,

    /// <summary>The operation reached the backend but failed (e.g. ufw rejected the rule).</summary>
    Failed,
}

/// <summary>
/// Whether the authority could enumerate the rules it owns. <c>Unknown</c> is the honest "the
/// backend cannot tell" — it must never be read as "nothing is open" (that is the difference between a
/// measured empty set and an unanswerable query).
/// </summary>
public enum FirewallListStatus
{
    /// <summary>The backend enumerated its owned rules (the set may legitimately be empty).</summary>
    Ok,

    /// <summary>The backend genuinely cannot enumerate rules — honest unknown, not an empty set.</summary>
    Unknown,

    /// <summary>The active backend does not support listing.</summary>
    Unsupported,
}

/// <summary>
/// Result of an ensure-open / remove operation against the firewall authority. <c>Ok</c> is the
/// coarse success bit; <c>Outcome</c> carries the precise status. A transport failure (the
/// authority is unreachable) does not produce a result — it throws
/// <c>Exceptions.FirewallException</c>, mirroring the unreachable-vs-rejected split in the
/// authority's exit-code contract.
/// </summary>
public sealed record FirewallActionResult
{
    /// <summary>Coarse success bit (true for applied/removed/noop).</summary>
    public bool Ok { get; init; }

    /// <summary>The precise outcome token, mapped from the authority's reply.</summary>
    public FirewallOutcome Outcome { get; init; }

    /// <summary>The active backend (e.g. <c>"ufw"</c>, <c>"none"</c>).</summary>
    public string Backend { get; init; } = string.Empty;

    /// <summary>Human-readable detail from the authority, when present.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The backend's runtime <b>enforcement</b> state (Firewall.Contracts 1.1.0), orthogonal to whether any
/// rules exist. A backend can be installed yet <c>Inactive</c> (e.g. ufw disabled), in which case
/// it filters NOTHING — so every port is reachable regardless of rules. A consumer computing an honest
/// "is this port open" verdict needs this: <c>Enforcing</c> → open iff a rule allows it;
/// <c>Inactive</c> → open (all, unfiltered); <c>Unknown</c> → unknown. NEVER read an
/// inactive backend's empty rule set as "closed".
/// </summary>
public enum FirewallEnforcement
{
    /// <summary>The backend is active and filtering — owned rules determine open/closed.</summary>
    Enforcing,

    /// <summary>Installed but not enforcing (e.g. <c>ufw</c> inactive) — nothing filtered, all ports open.</summary>
    Inactive,

    /// <summary>Cannot determine (non-root / backend absent / unparsable), OR a pre-1.1.0 authority that
    /// does not report enforcement — honest unknown; a consumer should fall back to its prior behaviour.</summary>
    Unknown,
}

/// <summary>The ports the authority owns for one instance — the range-preserving
/// <c>PortMapping</c> form, mapped from the wire.</summary>
public sealed record FirewallOwnedRule(string Instance, IReadOnlyList<PortMapping> Ports);

/// <summary>
/// Result of a list-owned query. <c>Status</c> distinguishes a measured result (<c>Rules</c>
/// is authoritative, possibly empty) from an honest <c>FirewallListStatus.Unknown</c>
/// (<c>Rules</c> is empty because the backend could not answer — never a fabricated "nothing open").
/// </summary>
public sealed record FirewallListResult
{
    /// <summary>Whether the rules could be enumerated (<c>FirewallListStatus.Unknown</c> ≠ empty).</summary>
    public FirewallListStatus Status { get; init; }

    /// <summary>The owned rules. Authoritative only when <c>Status</c> is
    /// <c>FirewallListStatus.Ok</c>; empty otherwise.</summary>
    public IReadOnlyList<FirewallOwnedRule> Rules { get; init; } = [];

    /// <summary>The backend's enforcement state (Firewall.Contracts 1.1.0). Load-bearing for an honest
    /// open verdict: when <c>FirewallEnforcement.Inactive</c>, the empty/partial rule set must NOT
    /// be read as "closed" — nothing is filtering, so every port is open. A pre-1.1.0 authority (no
    /// enforcement on the wire) maps to <c>FirewallEnforcement.Unknown</c>.</summary>
    public FirewallEnforcement Enforcement { get; init; } = FirewallEnforcement.Unknown;
}

/// <summary>
/// The active backend and what it can honestly do — the capabilities the authority reports for the
/// detected backend (e.g. a present-but-read-only driver reports <c>CanApply == false</c>).
/// </summary>
public sealed record FirewallBackendInfo
{
    /// <summary>The detected backend token (e.g. <c>"ufw"</c>, <c>"nftables"</c>, <c>"none"</c>).</summary>
    public string Backend { get; init; } = string.Empty;

    /// <summary>Whether the backend can apply (open) rules.</summary>
    public bool CanApply { get; init; }

    /// <summary>Whether the backend can remove rules.</summary>
    public bool CanRemove { get; init; }

    /// <summary>Whether the backend can enumerate the rules it owns.</summary>
    public bool CanList { get; init; }
}
