using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.PortForwarding;

/// <summary>
/// The description URL the router last answered UPnP discovery from, kept so a call can reach the router
/// directly when discovery gets no answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Discovery and control are two services on the router.</b> Discovery is a multicast query the
/// router answers on port 1900; control is plain HTTP on whatever port the router's UPnP daemon chose. A
/// router can lose the first while keeping the second, and then every call that starts by discovering
/// fails although the router would have done what it was asked. <c>upnpc -u</c> skips discovery for a URL
/// already known, which is what this holds.
/// </para>
/// <para>
/// <b>It can go stale, and that costs one extra call.</b> A router that restarts its UPnP daemon usually
/// picks a new port, so the remembered URL stops answering too — the direct attempt fails the same way
/// discovery did and the outcome is unchanged. The next discovery that succeeds replaces it.
/// </para>
/// <para>
/// Persisted beside the other state files as a single line, because the case it exists for is an outage
/// long enough to span a deploy, and an address that only lived in memory would be gone exactly then. A
/// failed read or write degrades to remembering nothing and never throws.
/// </para>
/// </remarks>
internal sealed class UpnpGatewayMemory
{
    private readonly string _path;
    private readonly ILogger<UpnpGatewayMemory> _logger;
    private readonly Lock _gate = new();

    private bool _loaded;
    private string? _url;

    public UpnpGatewayMemory(StatePathResolver paths, ILogger<UpnpGatewayMemory> logger)
        : this(paths.PathFor(StatePathResolver.UpnpGatewayFile), logger) { }

    /// <summary>The file named outright, so a test can keep it in a directory of its own.</summary>
    internal UpnpGatewayMemory(string path, ILogger<UpnpGatewayMemory> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>The URL the router last answered from, or null when none has been seen.</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _url = Load();
                    _loaded = true;
                }

                return _url;
            }
        }
    }

    /// <summary>Records the URL the router just answered from. The same URL again writes nothing.</summary>
    public void Remember(string url)
    {
        if (string.Equals(Current, url, StringComparison.Ordinal))
            return;

        lock (_gate)
        {
            _url = url;
        }

        try
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, url + "\n");
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still remembered for the life of this process, which covers every outage that does not
            // outlast a deploy.
            _logger.LogWarning(ex, "could not persist the UPnP gateway address to {Path}", _path);
        }
    }

    private string? Load()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            string url = File.ReadAllText(_path).Trim();
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && parsed.Scheme is "http" or "https"
                ? url
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "could not read the UPnP gateway address from {Path}; discovering afresh", _path);
            return null;
        }
    }
}
