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
    /// <summary>
    /// Keys owned by Microsoft.Extensions.Logging rather than by the watchdog. The panel offers
    /// exactly one of them — <c>Logging__LogLevel__Default</c>, the overall level — while the rest of
    /// the namespace is per-category filtering, an open-ended set a category name can spell any way
    /// it likes. Exempted as a namespace because enumerating it in the descriptor is not possible.
    /// </summary>
    private static bool IsFrameworkKey(string key) =>
        key.StartsWith("Logging__", StringComparison.Ordinal);

    private static readonly string[] FieldTypes =
        ["string", "int", "bool", "enum", "secret", "path", "csv", "duration", "float"];

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

    /// <summary>
    /// Every environment variable that can configure the daemon, derived from
    /// <c>kgsm-watchdog.settings.json</c> — the source of truth. A variable overrides a key only if
    /// that key exists in the file, so the file's leaves ARE the settable surface: each
    /// <c>Section:Key</c> path is reachable as <c>Section__Key</c>.
    /// </summary>
    /// <remarks>
    /// This reads the settings file rather than scanning the source for string literals because with
    /// bound configuration there are none left to scan — the binder matches property names against
    /// the file. It is also the stronger check: the file is the same artifact the daemon loads, so it
    /// cannot describe a surface the daemon does not have.
    /// </remarks>
    private static HashSet<string> SettableEnvKeys()
    {
        string path = Path.Combine(RepoRoot(), "src", "Watchdog", "kgsm-watchdog.settings.json");
        Assert.True(File.Exists(path), $"the settings file is missing: {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var found = new HashSet<string>(StringComparer.Ordinal);

        static void Walk(JsonElement node, string prefix, HashSet<string> into)
        {
            foreach (JsonProperty prop in node.EnumerateObject())
            {
                string key = prefix.Length == 0 ? prop.Name : $"{prefix}__{prop.Name}";
                if (prop.Value.ValueKind == JsonValueKind.Object)
                    Walk(prop.Value, key, into);
                else
                    into.Add(key);
            }
        }

        Walk(doc.RootElement, string.Empty, found);

        Assert.NotEmpty(found);   // a scan that finds nothing would pass every check below vacuously
        return found;
    }


    /// <summary>
    /// The env file overrides the settings file one key at a time, so a variable naming a key the file
    /// does not declare binds to nothing — it looks like configuration and is inert. The template is the
    /// one copy of that file in version control, so it is the copy that can be checked.
    /// </summary>
    [Fact]
    public void The_env_example_sets_no_key_the_settings_file_does_not_declare()
    {
        string path = Path.Combine(RepoRoot(), "deploy", "kgsm-watchdog.env.example");
        Assert.True(File.Exists(path), $"the env template is missing: {path}");

        var declared = SettableEnvKeys();
        var unknown = new List<string>();

        foreach (string raw in File.ReadAllLines(path))
        {
            // Both live lines and the commented-out examples: a commented key is what someone uncomments.
            bool commented = raw.TrimStart().StartsWith('#');
            string line = raw.TrimStart().TrimStart('#').Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line[..eq];
            // Prose, not an assignment — a sentence that happens to contain '='.
            if (key.Any(char.IsWhiteSpace))
                continue;

            // A commented line counts only when it is spelled like an override (Section__Key). These
            // templates also quote systemd's own directives in their prose — "EnvironmentFile=...",
            // "Delegate=yes" — and those configure the unit, not the leaf.
            if (commented && !key.Contains("__", StringComparison.Ordinal))
                continue;

            // The runtime's own variables are settings by another name, reached without the settings file.
            if (key.StartsWith("DOTNET_", StringComparison.Ordinal)
                || key.StartsWith("ASPNETCORE_", StringComparison.Ordinal))
                continue;

            if (!declared.Contains(key))
                unknown.Add(key);
        }

        Assert.True(unknown.Count == 0,
            "deploy/kgsm-watchdog.env.example sets these, and kgsm-watchdog.settings.json declares no such key, so " +
            "they bind to nothing:\n  " + string.Join("\n  ", unknown.Distinct()));
    }

    // ── Coverage: the descriptor and the settings file agree, both ways ──────

    [Fact]
    public void Every_configurable_key_is_described()
    {
        var described = Fields().Select(f => Str(f, "env")).ToHashSet(StringComparer.Ordinal);
        var missing = SettableEnvKeys()
            .Where(k => !described.Contains(k) && !IsFrameworkKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "these keys are declared in kgsm-watchdog.settings.json but not described in " +
            "deploy/kgsm-watchdog.leaf.json, so the Control Panel cannot show or set them:\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_described_key_is_really_settable()
    {
        var settable = SettableEnvKeys();
        var fabricated = Fields()
            .Select(f => Str(f, "env"))
            .Where(e => !settable.Contains(e) && !IsFrameworkKey(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        Assert.True(fabricated.Count == 0,
            "these descriptor fields name keys that do not exist in kgsm-watchdog.settings.json, so nothing " +
            "binds them — an override written for one would be reported as applied while changing " +
            "nothing:\n  " + string.Join("\n  ", fabricated));
    }

    [Fact]
    public void Every_settings_key_binds_to_a_property()
    {
        // The settings file is only a source of truth if the daemon reads what it declares. A key
        // with no matching property is inert: the panel offers it, an operator sets it, nothing moves.
        var bound = WatchdogOptions.KnownEnvVars.ToHashSet(StringComparer.Ordinal);
        var orphaned = SettableEnvKeys()
            .Where(k => k.StartsWith($"{WatchdogSettings.Section}__", StringComparison.Ordinal))
            .Where(k => !bound.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphaned.Count == 0,
            "these keys are declared in kgsm-watchdog.settings.json but have no WatchdogSettings property " +
            "to bind to, so setting them changes nothing:\n  " + string.Join("\n  ", orphaned));
    }

    [Fact]
    public void Every_settings_property_is_declared_in_the_file()
    {
        // The other direction: a property missing from the file has an invisible default. The panel
        // shows no fallback tier for it and an operator cannot discover it exists.
        var declared = SettableEnvKeys();
        var undeclared = WatchdogOptions.KnownEnvVars
            .Where(k => !declared.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "these WatchdogSettings properties are not declared in kgsm-watchdog.settings.json, so their " +
            "defaults are invisible to anyone reading the file:\n  " + string.Join("\n  ", undeclared));
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

        // floorSources is lowest-precedence-first, and the settings file is the base every other
        // source overrides. Listed anywhere else, the Control Panel resolves a knob to the file's
        // default and reports it as the deployed value — showing a blank where the unit sets a real
        // path. That is wrong on a screen whose whole job is saying where a value came from.
        Assert.Equal("appsettings", Str(sources[0], "kind"));
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
            bool numeric = Str(f, "type") is "int" or "duration" or "float";

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
                Assert.True(double.TryParse(def, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value),
                    $"{key}: default '{def}' is not a number");
                if (f.TryGetProperty("min", out JsonElement min))
                    Assert.True(value >= min.GetDouble(), $"{key}: default {value} is below its own min");
                if (f.TryGetProperty("max", out JsonElement max))
                    Assert.True(value <= max.GetDouble(), $"{key}: default {value} is above its own max");
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
    /// <c>--help</c> renders and what flags a typo'd <c>Watchdog__*</c> var at startup. The
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
            .Where(e => e.StartsWith($"{WatchdogSettings.Section}__", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var undescribed = known.Except(described).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var unknown = described.Except(known).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(undescribed.Count == 0, "in --help but not the descriptor:\n  " + string.Join("\n  ", undescribed));
        Assert.True(unknown.Count == 0,
            "in the descriptor but not the daemon's known vars — startup would warn about it as a typo:\n  "
            + string.Join("\n  ", unknown));
    }
}
