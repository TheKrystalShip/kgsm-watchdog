using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization.Metadata;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Client;

/// <summary>What asking the watchdog whether it is ready found.</summary>
/// <param name="Reachable">Whether anything answered on the socket. False is a measured absence, never "not ready".</param>
/// <param name="Ready">Whether the daemon is in its slice and able to spawn; false when it did not answer.</param>
/// <param name="Detail">The daemon's own words, or why it could not be reached.</param>
public sealed record WatchdogHealth(bool Reachable, bool Ready, string Detail);

/// <summary>The watchdog could not be asked: the socket is absent, refused, or the answer was not understood.</summary>
public sealed class WatchdogUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A typed client over the watchdog's control socket: HTTP/1.1 on a unix socket. Every answer is read
/// from the body, never only the status code. A request that cannot reach the daemon throws
/// <see cref="WatchdogUnavailableException"/>; an instance the daemon does not track is a null, not an error.
/// </summary>
public sealed class WatchdogClient : IWatchdogClient
{
    private static readonly Uri Base = new("http://watchdog/");

    private const string ConsoleStartHeader = "X-Console-Start";
    private const string ConsoleEndHeader = "X-Console-End";

    // Bounded requests carry the timeout. The console follow and download read a body that has no end the
    // client can predict, and HttpClient.Timeout bounds the whole request including that body, so they ride a
    // second client with no limit and end on the caller's cancellation.
    private readonly HttpClient _http;
    private readonly HttpClient _stream;

    /// <param name="socketPath">The control socket, <c>/run/kgsm-watchdog/control.sock</c> by default in the engine's settings.</param>
    /// <param name="timeout">How long one bounded request may take; a stop drains for the instance's whole stop timeout, so callers that stop pass a longer one.</param>
    public WatchdogClient(string socketPath, TimeSpan? timeout = null)
        : this(new WatchdogClientOptions { SocketPath = socketPath, RequestTimeout = timeout ?? TimeSpan.FromSeconds(5) })
    {
    }

    public WatchdogClient(WatchdogClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SocketPath);

        _http = new HttpClient(Handler(options.SocketPath), disposeHandler: true) { BaseAddress = Base, Timeout = options.RequestTimeout };
        _stream = new HttpClient(Handler(options.SocketPath), disposeHandler: true) { BaseAddress = Base, Timeout = Timeout.InfiniteTimeSpan };
    }

    private static SocketsHttpHandler Handler(string socketPath) => new()
    {
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <inheritdoc />
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
        (await HealthAsync(cancellationToken).ConfigureAwait(false)).Ready;

    /// <summary>The daemon's readiness. Never throws: an absent daemon is <see cref="WatchdogHealth.Reachable"/> false.</summary>
    public async Task<WatchdogHealth> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("health", cancellationToken).ConfigureAwait(false);
            ReadyState? body = await ReadBodyAsync(response, WatchdogClientJsonContext.Default.ReadyState, cancellationToken).ConfigureAwait(false);
            return body is null
                ? new WatchdogHealth(true, false, $"the watchdog answered {(int)response.StatusCode} with no readable body")
                : new WatchdogHealth(true, body.Ready, body.Detail);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return new WatchdogHealth(false, false, Describe(ex));
        }
    }

    /// <summary>One supervised instance's state, or null when the daemon does not track it.</summary>
    public async Task<InstanceState?> StatusAsync(string instance, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"status/{Escape(instance)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadRequiredAsync(response, WatchdogClientJsonContext.Default.InstanceState, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every supervised instance.</summary>
    public async Task<IReadOnlyList<InstanceState>> ListAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "list", cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(response, WatchdogClientJsonContext.Default.InstanceStateArray, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The run clock of every instance the daemon can date, supervised or only remembered.</summary>
    public async Task<IReadOnlyList<InstanceRunTimes>> RunTimesAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "runtimes", cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(response, WatchdogClientJsonContext.Default.InstanceRunTimesArray, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The names in the boot-autostart set.</summary>
    public async Task<IReadOnlyList<string>> EnabledAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "enabled", cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(response, WatchdogClientJsonContext.Default.StringArray, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The running daemon's build identity.</summary>
    public async Task<WatchdogVersionInfo> VersionAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "version", cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(response, WatchdogClientJsonContext.Default.WatchdogVersionInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts an instance. A capacity refusal is an answer: <see cref="ActionResult.Refusal"/> is <see cref="ActionRefusal.NoRoom"/>.</summary>
    /// <param name="force">A person's override of the memory admission check.</param>
    /// <param name="origin">The surface that asked; it reaches the daemon's own log.</param>
    public Task<ActionResult> StartAsync(string instance, bool force = false, string? origin = null, CancellationToken cancellationToken = default) =>
        ActAsync(HttpMethod.Post, $"start/{Escape(instance)}{Query(("force", force ? "true" : null), ("origin", origin))}", cancellationToken);

    /// <summary>Stops an instance gracefully; the daemon drains for the instance's stop timeout before it kills.</summary>
    public Task<ActionResult> StopAsync(string instance, string? origin = null, CancellationToken cancellationToken = default) =>
        ActAsync(HttpMethod.Post, $"stop/{Escape(instance)}{Query(("origin", origin))}", cancellationToken);

    /// <summary>Restarts an instance as one transition.</summary>
    public Task<ActionResult> RestartAsync(string instance, string? origin = null, CancellationToken cancellationToken = default) =>
        ActAsync(HttpMethod.Post, $"restart/{Escape(instance)}{Query(("origin", origin))}", cancellationToken);

    /// <summary>Adds an instance to the boot-autostart set.</summary>
    public Task<ActionResult> EnableAsync(string instance, CancellationToken cancellationToken = default) =>
        ActAsync(HttpMethod.Post, $"enable/{Escape(instance)}", cancellationToken);

    /// <summary>Removes an instance from the boot-autostart set.</summary>
    public Task<ActionResult> DisableAsync(string instance, CancellationToken cancellationToken = default) =>
        ActAsync(HttpMethod.Post, $"disable/{Escape(instance)}", cancellationToken);

    /// <summary>Drops an instance from supervision, stopping it first. Not Ok while the instance is still live.</summary>
    public Task<ActionResult> ForgetAsync(string instance, CancellationToken cancellationToken = default) =>
        ActAsync(HttpMethod.Delete, $"instance/{Escape(instance)}", cancellationToken);

    /// <inheritdoc />
    public Task<ActionResult> SetCpuPriorityAsync(string instance, string priority, CancellationToken cancellationToken = default) =>
        ActAsync(HttpMethod.Post, $"set-cpu-priority/{Escape(instance)}/{Escape(priority)}", cancellationToken);

    /// <inheritdoc />
    public Task<ActionResult> BeginMaintenanceAsync(string instance, string? origin = null, CancellationToken cancellationToken = default) =>
        ActAsync(HttpMethod.Post, $"maintenance/begin/{Escape(instance)}{Query(("origin", origin))}", cancellationToken);

    /// <inheritdoc />
    public Task<ActionResult> EndMaintenanceAsync(string instance, string? origin = null, CancellationToken cancellationToken = default) =>
        ActAsync(HttpMethod.Post, $"maintenance/end/{Escape(instance)}{Query(("origin", origin))}", cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConsoleRun>> ConsoleRunsAsync(string instance, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"console/{Escape(instance)}/runs", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        return await ReadRequiredAsync(response, WatchdogClientJsonContext.Default.ConsoleRunArray, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConsoleWindow> ConsoleWindowAsync(
        string instance, int lines, int run = 0, long endOffset = -1, CancellationToken cancellationToken = default)
    {
        string path = $"console/{Escape(instance)}?tail={lines}&run={run}" + (endOffset >= 0 ? $"&end={endOffset}" : string.Empty);
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return ConsoleWindow.Empty;
        EnsureSuccess(response);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        long start = ReadOffset(response, ConsoleStartHeader);
        long end = ReadOffset(response, ConsoleEndHeader);
        if (body.Length == 0) return new ConsoleWindow([], start, end);

        // The daemon joins the lines with a newline and ends the body with one.
        string[] split = body.Split('\n');
        int count = split[^1].Length == 0 ? split.Length - 1 : split.Length;
        return new ConsoleWindow(split[..count], start, end);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ConsoleTailAsync(string instance, int lines, int run = 0, CancellationToken cancellationToken = default) =>
        (await ConsoleWindowAsync(instance, lines, run, -1, cancellationToken).ConfigureAwait(false)).Lines;

    /// <inheritdoc />
    public async IAsyncEnumerable<string> FollowConsoleAsync(
        string instance, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendStreamAsync($"console/{Escape(instance)}/follow", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) yield break;
        EnsureSuccess(response);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line) yield return line;
    }

    /// <inheritdoc />
    public async Task<ConsoleDownload?> OpenConsoleDownloadAsync(string instance, int run = 0, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await SendStreamAsync($"console/{Escape(instance)}/download?run={run}", cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return null;
            }

            EnsureSuccess(response);
            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ConsoleDownload(content, response.Content.Headers.ContentLength ?? 0, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, InstancePresence>?> PlayersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "players", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await ReadBodyAsync(response, WatchdogClientJsonContext.Default.DictionaryStringInstancePresence, cancellationToken).ConfigureAwait(false);
        }
        catch (WatchdogUnavailableException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<UpnpListResult?> UpnpAsync(string instance, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Get, $"upnp/{Escape(instance)}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await ReadBodyAsync(response, WatchdogClientJsonContext.Default.UpnpListResult, cancellationToken).ConfigureAwait(false);
        }
        catch (WatchdogUnavailableException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<MemoryGateState?> MemoryGateAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "memory-gate", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await ReadBodyAsync(response, WatchdogClientJsonContext.Default.MemoryGateState, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _stream.Dispose();
    }

    private async Task<HttpResponseMessage> SendStreamAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            return await _stream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            throw new WatchdogUnavailableException($"the watchdog cannot be reached: {Describe(ex)}", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new WatchdogUnavailableException($"the watchdog answered {(int)response.StatusCode}");
    }

    private static long ReadOffset(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
        && long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed)
        && parsed >= 0
            ? parsed
            : 0;

    private async Task<ActionResult> ActAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(method, path, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(response, WatchdogClientJsonContext.Default.ActionResult, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            throw new WatchdogUnavailableException($"the watchdog cannot be reached: {Describe(ex)}", ex);
        }
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class =>
        await ReadBodyAsync(response, typeInfo, cancellationToken).ConfigureAwait(false)
        ?? throw new WatchdogUnavailableException($"the watchdog answered {(int)response.StatusCode} with a body that is not the expected shape");

    private static bool IsTransport(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && ex is HttpRequestException or SocketException or IOException or TaskCanceledException;

    private static string Describe(Exception ex) => ex is HttpRequestException { InnerException: { } inner } ? inner.Message : ex.Message;

    private static string Escape(string instance) => Uri.EscapeDataString(instance);

    private static string Query(params (string Key, string? Value)[] pairs)
    {
        string joined = string.Join('&', pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
        return joined.Length == 0 ? string.Empty : "?" + joined;
    }
}
