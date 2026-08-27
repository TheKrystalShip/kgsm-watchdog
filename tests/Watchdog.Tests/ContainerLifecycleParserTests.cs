using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the pure NDJSON line → lifecycle-type translation (mirrors <see cref="PlayerPresenceParserTests"/>):
/// valid <c>instance_started</c>/<c>instance_stopping</c> lines resolve, and malformed JSON / an
/// unknown or missing <c>type</c> all DROP rather than act on junk.
/// </summary>
public sealed class ContainerLifecycleParserTests
{
    [Fact]
    public void Instance_started_resolves()
    {
        var r = ContainerLifecycleParser.Parse("""{"type":"instance_started","ts":"2026-07-05T12:00:00Z"}""");

        Assert.True(r.Emit);
        Assert.Equal(ContainerLifecycleParser.TypeStarted, r.Type);
        Assert.Equal("instance_started", r.Type); // the literal wire form
    }

    [Fact]
    public void Instance_stopping_resolves()
    {
        var r = ContainerLifecycleParser.Parse("""{"type":"instance_stopping","ts":"2026-07-05T12:05:00Z"}""");

        Assert.True(r.Emit);
        Assert.Equal(ContainerLifecycleParser.TypeStopping, r.Type);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"type":"instance_started" """)] // truncated / unterminated
    [InlineData("{ }{ }")]                            // trailing garbage after the object
    public void Malformed_json_drops(string line)
    {
        var r = ContainerLifecycleParser.Parse(line);

        Assert.False(r.Emit);
        Assert.NotNull(r.DropReason);
    }

    [Theory]
    [InlineData("""{"type":"server.crashed","ts":"t"}""")] // not a recognised lifecycle type
    [InlineData("""{"type":"","ts":"t"}""")]
    [InlineData("""{"ts":"t"}""")]                            // no type at all
    public void Unknown_or_missing_type_drops(string line)
    {
        var r = ContainerLifecycleParser.Parse(line);

        Assert.False(r.Emit);
        Assert.Contains("type", r.DropReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Blank_lines_drop_quietly(string line)
    {
        var r = ContainerLifecycleParser.Parse(line);

        Assert.False(r.Emit);
        Assert.Equal("blank line", r.DropReason);
    }
}
