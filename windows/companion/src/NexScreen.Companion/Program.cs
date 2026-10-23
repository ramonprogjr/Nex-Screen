using NexScreen.Companion;
using NexScreen.Companion.Adb;
using NexScreen.Companion.Displays;
using NexScreen.Companion.Encoding;
using NexScreen.Companion.Input;
using NexScreen.Companion.Net;
using NexScreen.Companion.Streaming;
using NexScreen.Companion.Tray;
using NexScreen.Companion.Util;
using Vortice.MediaFoundation;

var trayRequested = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

// GUI-subsystem exe: console modes attach to the launching console (or open one); --tray has none.
if (!trayRequested)
{
    ConsoleHost.Setup(allocateIfNone: true);
}

Log.ConsoleEnabled = ConsoleHost.HasOutput;

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    if (trayRequested)
    {
        System.Windows.Forms.MessageBox.Show(ex.Message, "NexScreen", System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Error);
        return 2;
    }

    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

if (options.Help)
{
    Console.WriteLine(Options.Usage);
    return 0;
}

using var instance = options.Tray ? SingleInstance.TryAcquire() : null;
if (options.Tray && instance is null)
{
    // Already running in this session; the other instance shows a notification.
    return 0;
}

Log.MinLevel = options.Verbose ? LogLevel.Debug : LogLevel.Info;
if (options.LogFile && !options.List)
{
    Log.EnableFile(Log.DefaultDirectory);
}

MediaFactory.MFStartup(false).CheckError();
try
{
    if (options.List)
    {
        return ListOutputs();
    }

    using var cts = new CancellationTokenSource();
    if (ConsoleHost.HasOutput)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
    }

    Log.Info($"NexScreen companion starting (output={options.Output}, fps={options.Fps}, bitrate={options.Bitrate / 1000} kbps" +
             (options.Tray ? ", tray" : string.Empty) + ")" +
             (Log.FilePath is null ? string.Empty : $", log file {Log.FilePath}"));
    var pipelineOptions = new PipelineOptions(options.Output, options.Fps, options.Bitrate, options.EffectiveGop,
        options.Width, options.Height, options.Profile, options.Level, options.Encoder, options.MaxWidth, options.MaxHeight,
        DrawCursor: options.DrawCursor);

    return options.DumpPath is not null
        ? RunDump(pipelineOptions, options.DumpPath, options.Seconds, cts)
        : await RunServerAsync(pipelineOptions, options, instance, cts);
}
finally
{
    MediaFactory.MFShutdown();
    Log.Close();
}

static int ListOutputs()
{
    var outputs = DisplayEnumerator.Enumerate();
    Console.WriteLine($"{outputs.Count} output(s):");
    foreach (var o in outputs)
    {
        var tags = new List<string>();
        if (o.IsPrimary)
        {
            tags.Add("primary");
        }

        if (o.IsNexScreen)
        {
            tags.Add("NexScreen");
        }

        var name = string.IsNullOrEmpty(o.FriendlyName) ? "(no EDID name)" : o.FriendlyName;
        Console.WriteLine($"  {o.GdiName,-14} \"{name}\" [{o.EdidManufacturer ?? "?"}] {o.Width}x{o.Height} @ {o.RefreshHz:0.##} Hz " +
                          $"at ({o.Left},{o.Top}), rotation {o.Rotation}, adapter #{o.AdapterIndex} \"{o.AdapterName}\"" +
                          (tags.Count > 0 ? $"  <{string.Join(", ", tags)}>" : string.Empty));
    }

    if (!outputs.Any(o => o.IsNexScreen))
    {
        Console.WriteLine("  (no NexScreen virtual monitor found; use --output primary for local tests)");
    }

    foreach (var luid in outputs.Select(o => (o.AdapterLuid, o.AdapterName)).Distinct())
    {
        Console.WriteLine($"H.264 encoders for \"{luid.AdapterName}\":");
        foreach (var e in MftH264Encoder.Describe(luid.AdapterLuid))
        {
            Console.WriteLine($"  {e}");
        }
    }

    return 0;
}

static int RunDump(PipelineOptions pipelineOptions, string path, int seconds, CancellationTokenSource cts)
{
    var full = Path.GetFullPath(path);
    using var sink = new AnnexBFileSink(full);
    var pipeline = new StreamPipeline(pipelineOptions, sink);
    var thread = new Thread(() => pipeline.Run(cts.Token)) { Name = "Capture", IsBackground = false };
    Log.Info($"Dumping to {full} for {seconds} s");
    thread.Start();

    // The duration counts from the first encoded frame, so encoder start-up does not shorten the dump.
    if (WaitHandle.WaitAny([pipeline.FirstFrame, cts.Token.WaitHandle], TimeSpan.FromSeconds(30)) == 0)
    {
        cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
    }
    else if (!cts.IsCancellationRequested)
    {
        Log.Error("No encoded frame within 30 s.");
    }

    cts.Cancel();
    thread.Join();

    Log.Info($"Dump finished: {sink.Frames} frames ({sink.Keyframes} keyframes), {sink.Bytes / 1024.0:0} KiB, " +
             $"encoder \"{pipeline.EncoderName ?? "none"}\" ({(pipeline.EncoderName is null ? "n/a" : pipeline.EncoderIsHardware ? "hardware" : "software")}), " +
             $"average {pipeline.AverageFps:0.0} fps, submitted {pipeline.FramesSubmitted}");
    return sink.Frames > 0 ? 0 : 1;
}

static async Task<int> RunServerAsync(PipelineOptions pipelineOptions, Options options, SingleInstance? instance, CancellationTokenSource cts)
{
    var port = options.Port;
    var status = new CompanionStatus(options.AutoAdb);
    var extender = new AutoExtender(pipelineOptions.OutputSelector, () => status.Snapshot.TabletPresent,
        () => status.Snapshot.OutputAvailable) { Enabled = options.AutoExtend };
    using var tray = options.Tray ? TrayApp.Start(status, extender, cts.Cancel, instance) : null;

    await using var transport = new TcpDisplayTransport(port);
    transport.SessionStarted += status.SessionStarted;
    transport.SessionEnded += status.SessionEnded;
    var sink = new NetworkStreamSink(transport);
    if (options.InjectInput)
    {
        // Touch on the tablet -> mouse on the captured output (protocol/nexlink-v0-usb.md, POINTER/SCROLL)
        var injector = new InputInjector(new Win32MouseBackend(), new CapturedOutputLocator(pipelineOptions.OutputSelector).GetCurrent);
        _ = new SessionInputBridge(transport, injector, sink.HasAnnouncedFormat);
        Log.Info("Touch input from the tablet is injected as mouse input (--no-input to disable).");
    }
    else
    {
        Log.Info("Touch input from the tablet is ignored (--no-input).");
    }

    // Another companion may own the port (e.g. a console instance); keep retrying instead of exiting.
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            await transport.StartAsync(cts.Token);
            break;
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            if (attempt == 0)
            {
                Log.Warn($"Port {port} is in use (another NexScreen companion running?); retrying every 5 s.");
            }

            if (cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                return 1;
            }
        }
    }

    var pipeline = new StreamPipeline(pipelineOptions, sink);
    pipeline.OutputAvailabilityChanged += status.SetOutputAvailable;
    var thread = new Thread(() => pipeline.Run(cts.Token)) { Name = "Capture", IsBackground = false };
    thread.Start();

    extender.Start(cts.Token);
    if (options.AutoAdb)
    {
        var linker = new AdbLinker(options.AdbPath, port);
        linker.StateChanged += status.SetAdb;
        linker.Start(cts.Token);
        Log.Info($"Tablet link: an authorized tablet on USB or wireless debugging gets adb reverse tcp:{port} and the app is opened (--no-adb to disable).");
    }
    else
    {
        Log.Info($"Waiting for a client (adb reverse tcp:{port} tcp:{port}).");
    }

    Log.Info(options.Tray ? "Running in the notification area." : "Ctrl+C to stop.");
    cts.Token.WaitHandle.WaitOne();
    thread.Join();
    Log.Info("Stopped.");
    return 0;
}
