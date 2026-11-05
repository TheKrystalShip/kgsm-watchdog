using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Firewall.Contracts;
using Microsoft.Extensions.Logging;
namespace TheKrystalShip.KGSM.Core.Models;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;


/// <summary>
/// Newline-delimited-JSON-over-unix-socket implementation of <c>IFirewallService</c>. Speaks the
/// kgsm-firewall authority's wire protocol directly (the daemon is not HTTP, unlike the watchdog): connect,
/// write one JSON line, half-close the send side, read one JSON line. Serialization runs entirely through
/// the source-generated <c>WireJsonContext</c> from the shared
/// <c>TheKrystalShip.KGSM.Firewall.Contracts</c> package (no reflection), so the client is
/// Native-AOT/trim-safe for an AOT consumer. Maps <c>PortMapping</c>↔the wire <c>PortDto</c> at the
/// boundary so the rest of the ecosystem keeps its single canonical port type.
/// </summary>
public sealed class FirewallService : IFirewallService
{
    private readonly string _socketPath;
    private readonly TimeSpan _timeout;
    private readonly ILogger<FirewallService> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes the client against the kgsm-firewall control socket.
    /// </summary>
    /// <param name="options">Client options (socket path, request timeout).</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="options"/> or <paramref name="logger"/> is null.</exception>
    /// <exception cref="ArgumentException">When the socket path is null, empty, or whitespace.</exception>
    public FirewallService(FirewallClientOptions options, ILogger<FirewallService> logger)
    {
        ArgumentNullException.ThrowIfNull(options, nameof(options));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));

        if (string.IsNullOrWhiteSpace(options.SocketPath))
            throw new ArgumentException("Firewall socket path cannot be null, empty, or whitespace.", nameof(options));

        _socketPath = options.SocketPath;
        _timeout = options.RequestTimeout;
        _logger = logger;

        _logger.LogDebug("FirewallService initialized for control socket {SocketPath}", _socketPath);
    }

    /// <inheritdoc/>
    public async Task<FirewallActionResult> EnsureOpenAsync(
        string instanceName, IReadOnlyList<PortMapping> ports, string? actor = null,
        string? origin = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName, nameof(instanceName));
        ArgumentNullException.ThrowIfNull(ports, nameof(ports));

        var dtos = new PortDto[ports.Count];
        for (int i = 0; i < ports.Count; i++)
        {
            PortMapping p = ports[i];
            dtos[i] = new PortDto(p.Start, p.End, p.Protocol);
        }

        var request = new FirewallRequest(FirewallOps.EnsureOpen, instanceName, dtos, actor, origin);
        FirewallResponse response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return ToActionResult(response);
    }

    /// <inheritdoc/>
    public async Task<FirewallActionResult> RemoveAsync(
        string instanceName, string? actor = null, string? origin = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName, nameof(instanceName));

        var request = new FirewallRequest(FirewallOps.Remove, instanceName, null, actor, origin);
        FirewallResponse response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return ToActionResult(response);
    }

    /// <inheritdoc/>
    public async Task<FirewallListResult> ListOwnedAsync(string? instanceName = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var request = new FirewallRequest(FirewallOps.List, instanceName);
        FirewallResponse response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return ToListResult(response);
    }

    /// <inheritdoc/>
    public async Task<FirewallBackendInfo> BackendAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var request = new FirewallRequest(FirewallOps.Backend);
        FirewallResponse response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        CapabilitiesDto? caps = response.Capabilities;
        return new FirewallBackendInfo
        {
            Backend = response.Backend,
            CanApply = caps?.CanApply ?? false,
            CanRemove = caps?.CanRemove ?? false,
            CanList = caps?.CanList ?? false,
        };
    }

    // ---- transport --------------------------------------------------------------------------------

    private async Task<FirewallResponse> SendAsync(FirewallRequest request, CancellationToken cancellationToken)
    {
        // Layer the per-request timeout onto the caller's token; a fired timeout (caller's token NOT
        // requested) surfaces as an unreachable FirewallException, not a bare cancellation.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_timeout);

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), linked.Token).ConfigureAwait(false);

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, WireJsonContext.Default.FirewallRequest);
            await LineProtocol.WriteLineAsync(socket, payload, linked.Token).ConfigureAwait(false);
            socket.Shutdown(SocketShutdown.Send); // signal "request complete" to the daemon's EOF-aware reader

            string? line = await LineProtocol.ReadLineAsync(socket, LineProtocol.DefaultMaxBytes, linked.Token)
                .ConfigureAwait(false);
            if (line is null)
                throw new FirewallException("The firewall authority sent no reply.", _socketPath);

            FirewallResponse? response = JsonSerializer.Deserialize(line, WireJsonContext.Default.FirewallResponse);
            return response ?? throw new FirewallException("The firewall authority sent an empty reply.", _socketPath);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked token fired from CancelAfter (a timeout), not the caller — that's unreachable.
            _logger.LogDebug("Firewall request timed out after {Timeout}", _timeout);
            throw new FirewallException(
                $"Timed out reaching the firewall authority at {_socketPath} after {_timeout.TotalSeconds:0.#}s.",
                _socketPath);
        }
        catch (Exception ex) when (
            ex is System.Net.Sockets.SocketException or IOException or InvalidDataException or JsonException)
        {
            _logger.LogDebug(ex, "Firewall request to {SocketPath} failed at the transport", _socketPath);
            throw new FirewallException(
                $"Cannot reach the firewall authority at {_socketPath}: {ex.Message}", _socketPath, ex);
        }
    }

    // ---- mapping (wire → kgsm-lib result types) ---------------------------------------------------

    private static FirewallActionResult ToActionResult(FirewallResponse r) => new()
    {
        Ok = r.Ok,
        Outcome = ToOutcome(r.Outcome),
        Backend = r.Backend,
        Detail = r.Detail,
    };

    private static FirewallListResult ToListResult(FirewallResponse r)
    {
        FirewallListStatus status = r.Outcome switch
        {
            Outcomes.Ok => FirewallListStatus.Ok,
            Outcomes.Unknown => FirewallListStatus.Unknown,
            Outcomes.Unsupported => FirewallListStatus.Unsupported,
            // An unrecognised token is honest "we don't know" (Unknown), never a definitive "can't"
            // (Unsupported) — consistent with the never-fabricate ethos.
            _ => FirewallListStatus.Unknown,
        };

        OwnedRuleDto[] wire = r.Rules ?? [];
        var rules = new List<FirewallOwnedRule>(wire.Length);
        foreach (OwnedRuleDto rule in wire)
        {
            var ports = new List<PortMapping>(rule.Ports.Length);
            foreach (PortDto p in rule.Ports)
                ports.Add(new PortMapping { Start = p.Start, End = p.End, Protocol = p.Protocol });
            rules.Add(new FirewallOwnedRule(rule.Instance, ports));
        }

        return new FirewallListResult { Status = status, Rules = rules, Enforcement = ToEnforcement(r.Enforcement) };
    }

    private static FirewallOutcome ToOutcome(string token) => token switch
    {
        Outcomes.Applied => FirewallOutcome.Applied,
        Outcomes.AppliedInactive => FirewallOutcome.AppliedInactive,
        Outcomes.Removed => FirewallOutcome.Removed,
        Outcomes.NoOp => FirewallOutcome.NoOp,
        Outcomes.Ok => FirewallOutcome.Ok,
        Outcomes.Unknown => FirewallOutcome.Unknown,
        Outcomes.Unsupported => FirewallOutcome.Unsupported,
        _ => FirewallOutcome.Failed, // includes Outcomes.Failed and any unrecognised token (fail-closed)
    };

    // Map the wire enforcement token (Firewall.Contracts 1.1.0). Null = a pre-1.1.0 authority that does not
    // report it, or any op that doesn't carry it → honest Unknown (the consumer falls back to prior behaviour).
    private static FirewallEnforcement ToEnforcement(string? token) => token switch
    {
        Enforcements.Enforcing => FirewallEnforcement.Enforcing,
        Enforcements.Inactive => FirewallEnforcement.Inactive,
        _ => FirewallEnforcement.Unknown,
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc/>
    public void Dispose()
    {
        // No persistent connection (a socket is opened per request and disposed), so disposal is just the
        // disposed-guard flip — kept for symmetry with IWatchdogClient and to honor IDisposable on the seam.
        _disposed = true;
    }
}
