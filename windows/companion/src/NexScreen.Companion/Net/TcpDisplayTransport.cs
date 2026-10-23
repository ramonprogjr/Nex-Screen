using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NexScreen.Companion.Util;

namespace NexScreen.Companion.Net;

/// <summary>
/// NexLink v0-usb server: TCP on 127.0.0.1 only (reached from the tablet through adb reverse),
/// one session at a time, a new connection replaces the current one.
/// </summary>
public sealed class TcpDisplayTransport(int port = NexLink.DefaultPort) : IDisplayTransport
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private TcpDisplaySession? _current;
    private int _nextId;

    public event Action<IDisplaySession>? SessionStarted;
    public event Action<IDisplaySession>? SessionEnded;

    public IPEndPoint? LocalEndPoint => _listener?.LocalEndpoint as IPEndPoint;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        _listener.Start(backlog: 4);
        Log.Info($"NexLink v0-usb listening on {_listener.LocalEndpoint} (loopback only)");
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener!.AcceptSocketAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Warn($"Accept failed: {ex.SocketErrorCode}");
                continue;
            }

            socket.NoDelay = true;
            var id = Interlocked.Increment(ref _nextId);
            _ = Task.Run(() => RunSessionAsync(socket, id, ct), ct);
        }
    }

    private async Task RunSessionAsync(Socket socket, int id, CancellationToken ct)
    {
        var remote = socket.RemoteEndPoint?.ToString() ?? "?";
        Log.Info($"Client #{id} connected from {remote}");
        var stream = new NetworkStream(socket, ownsSocket: true);
        var reader = new NexLinkReader(stream);
        TcpDisplaySession? session = null;
        var reason = "closed by client";
        try
        {
            NexLinkMessage? first;
            using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                helloCts.CancelAfter(HelloTimeout);
                first = await reader.ReadAsync(helloCts.Token).ConfigureAwait(false);
            }

            if (first is null)
            {
                return;
            }

            if (first.Header.Type != MessageType.Hello)
            {
                throw new ProtocolException($"Expected HELLO, got {first.Header.Type}.");
            }

            var hello = NexLink.ParseHello(first.Payload);
            session = new TcpDisplaySession(id, remote, hello, socket, stream);
            Log.Info($"Client #{id} HELLO: screen {hello.ScreenWidth}x{hello.ScreenHeight}, {hello.DensityDpi} dpi, rotation {hello.Rotation}");

            TcpDisplaySession? previous;
            lock (_gate)
            {
                previous = _current;
                _current = session;
            }

            previous?.Close($"replaced by client #{id}");
            session.StartWriter();
            SessionStarted?.Invoke(session);

            while (!ct.IsCancellationRequested && session.IsOpen)
            {
                var msg = await reader.ReadAsync(session.Token).ConfigureAwait(false);
                if (msg is null)
                {
                    break;
                }

                switch (msg.Header.Type)
                {
                    case MessageType.RequestKeyframe:
                        session.RaiseKeyframeRequested();
                        break;
                    case MessageType.Stats:
                        session.RaiseStats(NexLink.ParseStats(msg.Payload));
                        break;
                    default:
                        throw new ProtocolException($"Unexpected {msg.Header.Type} after the handshake.");
                }
            }
        }
        catch (ProtocolException ex)
        {
            reason = $"protocol error: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            reason = session?.CloseReason ?? (ct.IsCancellationRequested ? "server stopping" : "HELLO timeout");
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            reason = session?.CloseReason ?? $"connection error: {ex.Message}";
        }
        finally
        {
            if (session is not null)
            {
                session.Close(reason);
                await session.WaitWriterAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (_current == session)
                    {
                        _current = null;
                    }
                }

                SessionEnded?.Invoke(session);
            }
            else
            {
                stream.Dispose();
            }

            Log.Info($"Client #{id} disconnected ({session?.CloseReason ?? reason})");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        TcpDisplaySession? current;
        lock (_gate)
        {
            current = _current;
        }

        current?.Close("server stopping");
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        _cts?.Dispose();
    }
}

internal sealed class TcpDisplaySession : IDisplaySession
{
    // Video backpressure: drop non-key frames when this much is already queued.
    private const int MaxQueuedVideoFrames = 6;
    private const int MaxQueuedVideoFramesForKeyframe = 12;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly Channel<(byte[] Header, ReadOnlyMemory<byte> Payload, bool IsVideo)> _queue =
        Channel.CreateUnbounded<(byte[], ReadOnlyMemory<byte>, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private Task _writer = Task.CompletedTask;
    private int _queuedVideo;
    private int _closed;

    public TcpDisplaySession(int id, string remote, ClientHello hello, Socket socket, NetworkStream stream)
    {
        Id = id;
        RemoteDescription = remote;
        Hello = hello;
        _socket = socket;
        _stream = stream;
    }

    public int Id { get; }
    public string RemoteDescription { get; }
    public ClientHello Hello { get; }
    public bool IsOpen => Volatile.Read(ref _closed) == 0;
    public string? CloseReason { get; private set; }
    public CancellationToken Token => _cts.Token;

    public event Action? KeyframeRequested;
    public event Action<ClientStats>? StatsReceived;

    internal void RaiseKeyframeRequested() => KeyframeRequested?.Invoke();

    internal void RaiseStats(ClientStats stats) => StatsReceived?.Invoke(stats);

    internal void StartWriter() => _writer = Task.Run(WriteLoopAsync);

    internal Task WaitWriterAsync() => _writer;

    public bool SendControl(MessageType type, ReadOnlyMemory<byte> payload)
    {
        if (!IsOpen)
        {
            return false;
        }

        var header = NexLink.BuildHeader(type, 0, (uint)payload.Length, MonoClock.NowMicroseconds);
        return _queue.Writer.TryWrite((header, payload, false));
    }

    public bool SendVideo(ReadOnlyMemory<byte> accessUnit, bool keyframe, ulong captureTimestampUs)
    {
        if (!IsOpen)
        {
            return false;
        }

        var queued = Volatile.Read(ref _queuedVideo);
        if (queued >= (keyframe ? MaxQueuedVideoFramesForKeyframe : MaxQueuedVideoFrames))
        {
            return false;
        }

        var header = NexLink.BuildHeader(MessageType.Frame, keyframe ? NexLink.FrameFlagKeyframe : (ushort)0,
            (uint)accessUnit.Length, captureTimestampUs);
        Interlocked.Increment(ref _queuedVideo);
        if (!_queue.Writer.TryWrite((header, accessUnit, true)))
        {
            Interlocked.Decrement(ref _queuedVideo);
            return false;
        }

        return true;
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var (header, payload, isVideo) in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _stream.WriteAsync(header, _cts.Token).ConfigureAwait(false);
                if (!payload.IsEmpty)
                {
                    await _stream.WriteAsync(payload, _cts.Token).ConfigureAwait(false);
                }

                if (isVideo)
                {
                    Interlocked.Decrement(ref _queuedVideo);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Close($"send failed: {ex.Message}");
        }
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        CloseReason = reason;
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        _stream.Dispose();
    }
}
