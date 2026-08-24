using System.Runtime.CompilerServices;

namespace TheKrystalShip.Testing;

/// <summary>
/// Gives the test run a temp directory of its own and takes it back when the run ends.
/// </summary>
/// <remarks>
/// <para>
/// Fixtures write into the system temp directory constantly — databases, state files, sockets,
/// whole directory trees — and every one of those paths comes from <see cref="Path.GetTempPath"/>.
/// Pointing <c>TMPDIR</c> at a directory belonging to this run puts all of it under one root, so
/// the run takes back everything it wrote without any fixture having to remember what it made.
/// </para>
/// <para>
/// <see cref="Path.GetTempPath"/> reads <c>TMPDIR</c> on every call, so this redirects the whole
/// process, the code under test included. The module initializer runs before the first test class
/// is loaded, which is what makes it cover paths built in field initializers too.
/// </para>
/// <para>
/// Set <c>KGSM_TEST_KEEP_TEMP=1</c> to leave the root behind and read what a failing test wrote.
/// </para>
/// </remarks>
internal static class TestTempRoot
{
    /// <summary>How long an abandoned root is left alone before a later run sweeps it.</summary>
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(12);

    /// <summary>
    /// Distinct from the bash suite's <c>kgsm-test-sandbox-*</c>, which lives in the same directory
    /// and whose sandboxes a sweep here must never take.
    /// </summary>
    private const string Prefix = "kgsm-nettest-";

    private static string? _root;

    [ModuleInitializer]
    internal static void Redirect()
    {
        string systemTemp = Path.GetTempPath();

        // Short on purpose. Everything a test builds hangs off this, and the kernel caps a unix
        // socket path at 108 bytes — a verbose root would spend that budget here.
        string stamp = Guid.NewGuid().ToString("N")[..8];
        string root = Path.Combine(systemTemp, $"{Prefix}{Environment.ProcessId}-{stamp}");

        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TMPDIR", root + Path.DirectorySeparatorChar);
        _root = root;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove();

        SweepAbandonedRoots(systemTemp);
    }

    private static void Remove()
    {
        if (_root is null || Environment.GetEnvironmentVariable("KGSM_TEST_KEEP_TEMP") == "1")
        {
            return;
        }

        Delete(_root);
        _root = null;
    }

    /// <summary>
    /// Deletes the roots of runs that never reached their own cleanup — a killed test host, a
    /// crashed runner. Age is the only thing distinguishing those from a run happening right now,
    /// and the window is wide enough that no run reaches it.
    /// </summary>
    private static void SweepAbandonedRoots(string systemTemp)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - AbandonedAfter;

            foreach (string candidate in Directory.EnumerateDirectories(systemTemp, $"{Prefix}*"))
            {
                if (Directory.GetLastWriteTimeUtc(candidate) <= cutoff)
                {
                    Delete(candidate);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Best effort. A directory another run is already removing, or one holding something still
    /// open, is left where it is for the next sweep rather than failing a run that has passed.
    /// </summary>
    private static void Delete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
