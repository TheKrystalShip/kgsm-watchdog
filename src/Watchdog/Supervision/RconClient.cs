using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Minimal Source RCON protocol client. Connects via TCP, authenticates with a
/// password, executes commands, and returns raw text responses. AOT-safe — no
/// reflection, no external dependencies. Intended for periodic polling (connect,
/// execute, disconnect) rather than persistent connections.
/// </summary>
/// <remarks>
/// The Source RCON protocol (Valve) uses length-prefixed binary packets over TCP:
/// <list type="bullet">
///   <item>Packet = [Size:4][Id:4][Type:4][Body:N][0x00 0x00]</item>
///   <item>Type 3 = <c>SERVERDATA_AUTH</c> (client→server)</item>
///   <item>Type 2 = <c>SERVERDATA_EXECCOMMAND</c> (client→server)</item>
///   <item>Type 2 = <c>SERVERDATA_AUTH_RESPONSE</c> (server→client) — id echoes the request on
///         success, and is -1 when the password is rejected</item>
///   <item>Type 0 = <c>SERVERDATA_RESPONSE_VALUE</c> (server→client, and the client's end-of-response
///         sentinel)</item>
/// </list>
/// Type 2 carries two meanings, separated by direction; a packet's type alone does not identify it.
/// See https://developer.valvesoftware.com/wiki/Source_RCON_Protocol
/// </remarks>
internal sealed class RconClient : IAsyncDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _packetId;
    private bool _authenticated;
    private bool _disposed;

    private const int TypeResponseValue = 0;
    private const int TypeExecCommand = 2;
    private const int TypeAuthResponse = 2;
    private const int TypeAuth = 3;
    private const int AuthFailureId = -1;
    private const int ConnectTimeoutMs = 5000;
    private const int ReadTimeoutMs = 5000;

    /// <summary>Connects and authenticates; a rejected password or a refused connection is an <see cref="RconException"/>.</summary>
    public async Task ConnectAsync(string host, int port, string password, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Disconnect();

        _tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ConnectTimeoutMs);

        try
        {
            await _tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _tcp.Dispose();
            _tcp = null;
            throw new RconException($"Failed to connect to RCON at {host}:{port}", ex);
        }

        _stream = _tcp.GetStream();
        _stream.ReadTimeout = ReadTimeoutMs;

        // Authenticate
        _packetId = 0;
        var authPacket = BuildPacket(_packetId, TypeAuth, password);
        await SendPacketAsync(authPacket, cts.Token).ConfigureAwait(false);

        // The verdict is the SERVERDATA_AUTH_RESPONSE, and it is not necessarily the first thing on
        // the wire: servers may precede it with an empty SERVERDATA_RESPONSE_VALUE carrying the same
        // id. Judging whichever packet arrives first reads that filler as the verdict and leaves the
        // real one queued, so every later read is off by a packet — match on the type instead.
        while (true)
        {
            var response = await ReadPacketAsync(cts.Token).ConfigureAwait(false);

            if (response.Type != TypeAuthResponse)
                continue;

            if (response.Id == AuthFailureId)
            {
                Disconnect();
                throw new RconException("RCON authentication failed (server rejected password)");
            }

            break;
        }

        _authenticated = true;
    }

    /// <summary>Runs one command and returns the server's text answer.</summary>
    public async Task<string> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_authenticated || _stream is null)
            throw new RconException("Not connected. Call ConnectAsync first.");

        int id = ++_packetId;
        int sentinelId = ++_packetId;

        await SendPacketAsync(BuildPacket(id, TypeExecCommand, command), cancellationToken).ConfigureAwait(false);

        // A response is split across packets once it exceeds the packet limit, and nothing in a
        // response announces how many parts follow. The protocol's marker is this empty packet: the
        // server answers requests in order, so its echo cannot arrive before the last part of the
        // response ahead of it. Waiting instead for an empty body ends the read on the first game
        // whose reply is a single packet with content and no trailer — the read then blocks until
        // the timeout and the poll returns nothing, having had the answer in hand the whole time.
        await SendPacketAsync(BuildPacket(sentinelId, TypeResponseValue, string.Empty), cancellationToken).ConfigureAwait(false);

        // NetworkStream.ReadTimeout governs only synchronous reads, so an async read has no deadline
        // of its own — a server that never echoes the sentinel would hang this call for as long as
        // the caller's token allows, which for a polling loop is until shutdown.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ReadTimeoutMs);

        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var response = await ReadPacketAsync(cts.Token).ConfigureAwait(false);

                if (response.Id == sentinelId)
                    break;

                if (response.Id != id)
                    continue; // stale or unrelated packet

                // Parts of a split response are byte continuations, not lines: anything inserted
                // between them lands in the middle of whatever token straddled the split.
                sb.Append(response.Body);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RconException($"Timed out awaiting the response to '{command}'.");
        }

        return sb.ToString();
    }

    /// <summary>Closes the connection; safe to call when it is already closed.</summary>
    public Task DisconnectAsync()
    {
        Disconnect();
        return Task.CompletedTask;
    }

    /// <summary>Closes the connection and releases the client.</summary>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Disconnect();
        }
        return ValueTask.CompletedTask;
    }

    private void Disconnect()
    {
        _authenticated = false;
        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;
    }

    private async Task SendPacketAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_stream is null)
            throw new RconException("Not connected.");

        await _stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int Id, int Type, string Body)> ReadPacketAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
            throw new RconException("Not connected.");

        // Read 4-byte size prefix
        byte[] sizeBuf = new byte[4];
        await ReadExactAsync(_stream, sizeBuf, cancellationToken).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(sizeBuf);

        if (size < 10 || size > 4096)
            throw new RconException($"Invalid RCON packet size: {size}");

        // Read remaining payload (size bytes = id + type + body + 2 null terminators)
        byte[] payload = new byte[size];
        await ReadExactAsync(_stream, payload, cancellationToken).ConfigureAwait(false);

        int id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        int type = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));

        // Body is everything between the type field and the two null terminators at the end:
        // size = 4(id) + 4(type) + body + 2(null), so an empty body is size 10 and the body runs
        // for size - 10 bytes from offset 8.
        string body = size > 10
            ? Encoding.UTF8.GetString(payload, 8, size - 10)
            : string.Empty;

        return (id, type, body);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new RconException("Connection closed by server.");
            totalRead += read;
        }
    }

    private static byte[] BuildPacket(int id, int type, string body)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        // Packet: [Size:4][Id:4][Type:4][Body:N][0x00 0x00]
        int size = 4 + 4 + bodyBytes.Length + 2; // id + type + body + null terminators
        byte[] packet = new byte[4 + size]; // size prefix + payload

        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0), size);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        Buffer.BlockCopy(bodyBytes, 0, packet, 12, bodyBytes.Length);
        packet[^2] = 0x00; // first null terminator
        packet[^1] = 0x00; // second null terminator

        return packet;
    }
}
