namespace NexScreen.Companion.Net;

/// <summary>
/// Server-side counterpart of the Android DisplayTransport (PROJETO.md section 8): hides whether the
/// client arrives over USB (adb reverse + TCP, NexLink v0-usb) or, later, Wi-Fi (TLS control + UDP video).
/// </summary>
public interface IDisplayTransport : IAsyncDisposable
{
    /// <summary>Raised after a client completed the handshake (valid HELLO).</summary>
    event Action<IDisplaySession>? SessionStarted;

    event Action<IDisplaySession>? SessionEnded;

    Task StartAsync(CancellationToken cancellationToken);
}

public interface IDisplaySession
{
    int Id { get; }

    string RemoteDescription { get; }

    ClientHello Hello { get; }

    bool IsOpen { get; }

    event Action? KeyframeRequested;

    event Action<ClientStats>? StatsReceived;

    /// <summary>Queues a control message (HELLO_ACK, CODEC_CONFIG). Never dropped while open.</summary>
    bool SendControl(MessageType type, ReadOnlyMemory<byte> payload);

    /// <summary>Queues a video frame. Returns false when it was dropped because the client is not keeping up.</summary>
    bool SendVideo(ReadOnlyMemory<byte> accessUnit, bool keyframe, ulong captureTimestampUs);

    void Close(string reason);
}
