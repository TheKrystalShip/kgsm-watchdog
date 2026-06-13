using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The startup adopt/spawn/skip fork, the riskiest new logic in the persistence increment, tested in
/// isolation from the cgroup/fork I/O it normally drives (mirrors how <c>BackoffPolicy</c> is unit-
/// tested apart from the live reconcile loop). The two axes are: is the cgroup already live, and did
/// kgsm-lib return an in-scope spec.
/// </summary>
public sealed class RestorePlanTests
{
    private static Instance Native() => new()
    {
        Name = "7dtd",
        Runtime = InstanceRuntime.Native,
    };

    [Fact]
    public void Null_spec_is_skip_gone()
        => Assert.Equal(RestoreAction.SkipGone, RestorePlan.Classify(cgroupPopulated: false, instance: null));

    [Fact]
    public void Null_spec_is_skip_gone_even_if_a_cgroup_is_live()
        => Assert.Equal(RestoreAction.SkipGone, RestorePlan.Classify(cgroupPopulated: true, instance: null));

    [Fact]
    public void Container_runtime_is_out_of_scope()
    {
        var container = new Instance { Name = "valheim", Runtime = InstanceRuntime.Container };
        Assert.Equal(RestoreAction.SkipOutOfScope, RestorePlan.Classify(false, container));
    }

    [Fact]
    public void Native_with_a_live_cgroup_is_adopt()
        => Assert.Equal(RestoreAction.Adopt, RestorePlan.Classify(cgroupPopulated: true, instance: Native()));

    [Fact]
    public void Native_with_an_empty_cgroup_is_spawn()
        => Assert.Equal(RestoreAction.Spawn, RestorePlan.Classify(cgroupPopulated: false, instance: Native()));
}
