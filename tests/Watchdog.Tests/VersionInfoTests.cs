using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The pure informational-version → (version, commit) split that backs <c>GET /version</c> and the
/// hot-swap deploy's post-swap build check (Inc 7 Phase 0). Unit-tested apart from the reflection that
/// reads the assembly attribute — exactly like <c>BackoffPolicy</c>/<c>RestorePlan</c> are tested apart
/// from the I/O they normally drive.
/// </summary>
public sealed class VersionInfoTests
{
    [Fact]
    public void Splits_version_and_commit_on_the_plus()
    {
        var info = WatchdogVersionInfo.FromInformational("1.0.0+abc123");
        Assert.Equal("1.0.0", info.Version);
        Assert.Equal("abc123", info.Commit);
    }

    [Fact]
    public void No_plus_is_the_whole_version_with_empty_commit()
    {
        var info = WatchdogVersionInfo.FromInformational("1.2.3");
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal("", info.Commit);
    }

    [Fact]
    public void Long_sourcelink_hash_is_preserved_verbatim()
    {
        var info = WatchdogVersionInfo.FromInformational("2.5.1+0affcf2deadbeef0123456789abcdef01234567");
        Assert.Equal("2.5.1", info.Version);
        Assert.Equal("0affcf2deadbeef0123456789abcdef01234567", info.Commit);
    }

    [Fact]
    public void Empty_commit_segment_after_a_trailing_plus_is_empty()
    {
        var info = WatchdogVersionInfo.FromInformational("1.0.0+");
        Assert.Equal("1.0.0", info.Version);
        Assert.Equal("", info.Commit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_yields_a_safe_non_null_placeholder(string? informational)
    {
        var info = WatchdogVersionInfo.FromInformational(informational);
        Assert.Equal("0.0.0", info.Version);
        Assert.Equal("", info.Commit);
    }
}
