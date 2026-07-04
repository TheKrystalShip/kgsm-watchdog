using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the pure readiness matcher in isolation (mirrors <see cref="NativeLogMatcherTests"/>'s style):
/// empty pattern → immediate fallback (not an error), invalid pattern → disabled + warning (never
/// throws), per-line matching, and the one-shot whole-file scan used for the late-attach gotcha.
/// </summary>
public sealed class NativeReadinessMatcherTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kgsm-wd-readiness-" + Guid.NewGuid().ToString("N"));

    public NativeReadinessMatcherTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Empty_pattern_is_immediate_not_enabled_no_warning()
    {
        var m = new NativeReadinessMatcher("");

        Assert.True(m.IsImmediate);
        Assert.False(m.Enabled);
        Assert.Null(m.Warning);
        Assert.False(m.IsMatch("Hosting game at IP ADDR:34197"));
    }

    [Fact]
    public void Whitespace_only_pattern_is_also_immediate()
    {
        var m = new NativeReadinessMatcher("   ");
        Assert.True(m.IsImmediate);
        Assert.False(m.Enabled);
    }

    [Fact]
    public void Valid_pattern_enables_and_matches()
    {
        var m = new NativeReadinessMatcher(@"Hosting game at IP ADDR:\d+");

        Assert.False(m.IsImmediate);
        Assert.True(m.Enabled);
        Assert.Null(m.Warning);
        Assert.True(m.IsMatch("1234.567 Hosting game at IP ADDR:34197"));
        Assert.False(m.IsMatch("1234.567 Loading map"));
    }

    [Fact]
    public void Invalid_pattern_disables_and_warns_never_throws()
    {
        var m = new NativeReadinessMatcher("(?<unterminated");

        Assert.False(m.IsImmediate); // it WAS a real (if broken) pattern, not "no pattern configured"
        Assert.False(m.Enabled);
        Assert.NotNull(m.Warning);
        Assert.Contains("not a valid .NET regex", m.Warning);
        Assert.False(m.IsMatch("anything")); // never throws, never matches
    }

    [Fact]
    public void MatchesExistingContent_finds_the_ready_line_anywhere_in_the_file()
    {
        string log = Path.Combine(_tmp, "server.log");
        File.WriteAllText(log,
            "1000.000 Loading map\n1000.500 Hosting game at IP ADDR:34197\n1001.000 Player list updated\n");

        var m = new NativeReadinessMatcher(@"Hosting game at IP ADDR:\d+");

        Assert.True(m.MatchesExistingContent(log));
    }

    [Fact]
    public void MatchesExistingContent_false_when_the_line_is_not_yet_present()
    {
        string log = Path.Combine(_tmp, "server.log");
        File.WriteAllText(log, "1000.000 Loading map\n");

        var m = new NativeReadinessMatcher(@"Hosting game at IP ADDR:\d+");

        Assert.False(m.MatchesExistingContent(log));
    }

    [Fact]
    public void MatchesExistingContent_is_honest_false_for_a_missing_file_never_throws()
    {
        var m = new NativeReadinessMatcher(@"Hosting game at IP ADDR:\d+");
        Assert.False(m.MatchesExistingContent(Path.Combine(_tmp, "does-not-exist.log")));
    }

    [Fact]
    public void MatchesExistingContent_returns_false_when_pattern_is_immediate_or_disabled()
    {
        string log = Path.Combine(_tmp, "server.log");
        File.WriteAllText(log, "Hosting game at IP ADDR:34197\n");

        var immediate = new NativeReadinessMatcher("");
        Assert.False(immediate.MatchesExistingContent(log));

        var invalid = new NativeReadinessMatcher("(?<unterminated");
        Assert.False(invalid.MatchesExistingContent(log));
    }
}
