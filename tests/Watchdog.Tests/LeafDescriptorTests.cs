using System.Text.Json;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The leaf config descriptor (<c>deploy/kgsm-watchdog.leaf.json</c>) is what the Control Panel
/// renders this daemon's configuration page from. These tests are the anti-drift guard: a knob
/// added to the watchdog without a descriptor entry fails the build here, and a descriptor entry
/// naming a variable the watchdog does not read fails here too.
///
/// The coverage check scans the <em>source</em> rather than a table of constants. A table only
/// proves the table and the descriptor agree; a knob read through a raw string literal would
/// bypass both. The contract is documented in tks/leaf-config-descriptor.md.
/// </summary>
public class LeafDescriptorTests
{
    private const string EnvPrefix = "KGSM_WATCHDOG_";

    /// <summary>
    /// Variables the watchdog genuinely reads that do NOT appear as literals in its source: the
    /// ecosystem logging convention resolves these through Microsoft.Extensions.Logging. Named
    /// explicitly rather than allowed by a pattern, so the exception cannot quietly widen.
    /// </summary>
    private static readonly HashSet<string> FrameworkKeys = new(StringComparer.Ordinal)
    {
        "Logging__LogLevel__Default",
    };

    /// <summary>
    /// Variables that carry the <c>KGSM_WATCHDOG_</c> prefix but are not operator configuration, so
    /// describing them would put a knob on the Control Panel that configures nothing. Only the hot-swap
    /// handoff qualifies: the outgoing image sets it on the incoming one across the re-exec, an internal
    /// channel with a lifetime of one syscall.
    /// </summary>
    private static readonly HashSet<string> NotConfiguration = new(StringComparer.Ordinal)
    {
        Model.HotSwapHandoff.EnvVarName,
    };

    private static readonly string[] FieldTypes =
        ["string", "int", "bool", "enum", "secret", "path", "csv", "duration"];

    private static readonly string[] RiskLevels = ["safe", "wiring", "destructive"];

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>The repo root, found by walking up from the test binary to the solution file.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "kgsm-watchdog.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not locate the repo root (no kgsm-watchdog.slnx above the test binary)");
        return dir!.FullName;
    }

    private static JsonElement Descriptor()
    {
        string path = Path.Combine(RepoRoot(), "deploy", "kgsm-watchdog.leaf.json");
        Assert.True(File.Exists(path), $"the leaf descriptor is missing: {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static List<JsonElement> Fields() => [.. Descriptor().GetProperty("fields").EnumerateArray()];

    private static string Str(JsonElement field, string name) => field.GetProperty(name).GetString()!;

    private static string? OptionalStr(JsonElement field, string name) =>
        field.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;

    /// <summary>Every KGSM_WATCHDOG_* variable named anywhere in the daemon's own source.</summary>
    private static HashSet<string> EnvKeysInSource()
    {
        string src = Path.Combine(RepoRoot(), "src", "Watchdog");
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            // The build's own generated sources are not configuration reads.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"KGSM_WATCHDOG_[A-Z0-9_]+"))
                found.Add(m.Value);
        }

        found.ExceptWith(NotConfiguration);
        Assert.NotEmpty(found);   // a scan that finds nothing would pass every check below vacuously
        return found;
    }

    // ── Coverage: the descriptor and the code agree, both ways ───────────────

    [Fact]
    public void Every_env_var_the_watchdog_reads_is_described()
    {
        var described = Fields().Select(f => Str(f, "env")).ToHashSet(StringComparer.Ordinal);
        var missing = EnvKeysInSource().Where(k => !described.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "these variables are read by the watchdog but not described in deploy/kgsm-watchdog.leaf.json, so " +
            "the Control Panel cannot show or set them:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_described_env_var_is_real()
    {
        var inSource = EnvKeysInSource();
        var fabricated = Fields()
            .Select(f => Str(f, "env"))
            .Where(e => !inSource.Contains(e) && !FrameworkKeys.Contains(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        Assert.True(fabricated.Count == 0,
            "these descriptor fields name variables the watchdog does not read — an override written for one " +
            "would be reported as applied while changing nothing:\n  " + string.Join("\n  ", fabricated));
    }

    // ── Structure ────────────────────────────────────────────────────────────

    [Fact]
    public void Descriptor_identity_matches_this_project()
    {
        JsonElement d = Descriptor();

        Assert.Equal(1, d.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("watchdog", d.GetProperty("id").GetString());
        Assert.Equal("kgsm-watchdog.service", d.GetProperty("unit").GetString());
        Assert.Equal("restart", d.GetProperty("applyMode").GetString());
        Assert.False(d.GetProperty("onDemand").GetBoolean());
        Assert.NotEmpty(d.GetProperty("displayName").GetString()!);
        Assert.NotEmpty(d.GetProperty("role").GetString()!);
    }

    [Fact]
    public void Floor_sources_are_declared_and_typed()
    {
        var kinds = new[] { "systemd-unit", "env-file", "appsettings" };

        var sources = Descriptor().GetProperty("floorSources").EnumerateArray().ToList();
        Assert.NotEmpty(sources);   // the watchdog's floor is its unit's Environment= lines + the optional env file

        foreach (JsonElement s in sources)
        {
            Assert.Contains(Str(s, "kind"), kinds);
            Assert.StartsWith("/", Str(s, "path"));
        }
    }

    [Fact]
    public void Field_keys_are_unique()
    {
        var keys = Fields().Select(f => Str(f, "key")).ToList();
        var dupes = keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(dupes.Count == 0, "duplicate field keys: " + string.Join(", ", dupes));
        Assert.Equal(keys.Count, Fields().Select(f => Str(f, "env")).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_field_is_completely_described()
    {
        foreach (JsonElement f in Fields())
        {
            string key = Str(f, "key");

            Assert.False(string.IsNullOrWhiteSpace(key), "a field has no key");
            Assert.False(string.IsNullOrWhiteSpace(OptionalStr(f, "label")), $"{key}: no label");
            Assert.False(string.IsNullOrWhiteSpace(OptionalStr(f, "description")), $"{key}: no description");

            string type = Str(f, "type");
            Assert.True(FieldTypes.Contains(type), $"{key}: unknown type '{type}'");

            string risk = OptionalStr(f, "risk") ?? "safe";
            Assert.True(RiskLevels.Contains(risk), $"{key}: unknown risk '{risk}'");

            // A default is always a string, so the descriptor renders one provenance tier uniformly.
            if (f.TryGetProperty("default", out JsonElement def))
                Assert.Equal(JsonValueKind.String, def.ValueKind);
        }
    }

    [Fact]
    public void Enum_fields_carry_their_values_and_a_valid_default()
    {
        foreach (JsonElement f in Fields().Where(f => Str(f, "type") == "enum"))
        {
            string key = Str(f, "key");
            var values = f.GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToList();
            Assert.NotEmpty(values);

            string? def = OptionalStr(f, "default");
            if (def is not null)
                Assert.True(values.Contains(def), $"{key}: default '{def}' is not one of its values");
        }
    }

    [Fact]
    public void Int_bounds_and_units_are_coherent()
    {
        foreach (JsonElement f in Fields())
        {
            string key = Str(f, "key");
            bool numeric = Str(f, "type") is "int" or "duration";

            if (!numeric)
            {
                Assert.False(f.TryGetProperty("min", out _), $"{key}: min on a non-numeric field");
                Assert.False(f.TryGetProperty("max", out _), $"{key}: max on a non-numeric field");
                continue;
            }

            // A numeric default must parse, and must satisfy the bounds the field declares —
            // otherwise the API rejects the leaf's own default the moment someone re-enters it.
            if (OptionalStr(f, "default") is { } def)
            {
                Assert.True(long.TryParse(def, out long value), $"{key}: default '{def}' is not an integer");
                if (f.TryGetProperty("min", out JsonElement min))
                    Assert.True(value >= min.GetInt64(), $"{key}: default {value} is below its own min");
                if (f.TryGetProperty("max", out JsonElement max))
                    Assert.True(value <= max.GetInt64(), $"{key}: default {value} is above its own max");
            }
        }
    }

    [Fact]
    public void Bool_defaults_are_the_wire_representation()
    {
        foreach (JsonElement f in Fields().Where(f => Str(f, "type") == "bool"))
        {
            string? def = OptionalStr(f, "default");
            if (def is not null)
                Assert.True(def is "true" or "false", $"{Str(f, "key")}: bool default must be 'true' or 'false', got '{def}'");
        }
    }

    [Fact]
    public void Group_and_dependency_references_resolve()
    {
        JsonElement d = Descriptor();

        var groups = d.TryGetProperty("groups", out JsonElement g)
            ? g.EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        var keys = Fields().Select(f => Str(f, "key")).ToHashSet(StringComparer.Ordinal);

        foreach (JsonElement f in Fields())
        {
            string key = Str(f, "key");

            if (OptionalStr(f, "group") is { } group)
                Assert.True(groups.Contains(group), $"{key}: references group '{group}', which is not defined");

            if (OptionalStr(f, "dependsOn") is { } dep)
            {
                Assert.True(keys.Contains(dep), $"{key}: dependsOn '{dep}', which is not a field here");
                Assert.NotEqual(key, dep);
            }
        }
    }

    [Fact]
    public void Wire_keys_already_in_use_by_the_api_are_preserved()
    {
        // Overrides stored by kgsm-api are keyed by these. Renaming one orphans a live override
        // and silently reverts the watchdog to its floor, so they are pinned here deliberately.
        var keys = Fields().Select(f => Str(f, "key")).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("logLevel", keys);
        Assert.Contains("pollIntervalMs", keys);
    }

    /// <summary>
    /// The daemon already keeps its own list of the variables it recognises — it is what
    /// <c>--help</c> renders and what flags a typo'd <c>KGSM_WATCHDOG_*</c> var at startup. The
    /// descriptor and that list describe the same surface to two different audiences, so they must
    /// agree exactly: a knob in one and not the other means the Control Panel and the daemon's own
    /// documentation disagree about what this leaf can be configured with.
    /// </summary>
    [Fact]
    public void Descriptor_and_the_daemons_own_known_vars_agree()
    {
        var known = WatchdogOptions.KnownEnvVars.ToHashSet(StringComparer.Ordinal);
        var described = Fields()
            .Select(f => Str(f, "env"))
            .Where(e => e.StartsWith(EnvPrefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var undescribed = known.Except(described).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var unknown = described.Except(known).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(undescribed.Count == 0, "in --help but not the descriptor:\n  " + string.Join("\n  ", undescribed));
        Assert.True(unknown.Count == 0,
            "in the descriptor but not the daemon's known vars — startup would warn about it as a typo:\n  "
            + string.Join("\n  ", unknown));
    }
}
