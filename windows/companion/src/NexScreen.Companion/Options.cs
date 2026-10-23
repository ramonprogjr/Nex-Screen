using System.Globalization;
using NexScreen.Companion.Encoding;

namespace NexScreen.Companion;

public sealed class Options
{
    public bool List { get; private set; }
    public bool Help { get; private set; }
    public string Output { get; private set; } = "auto";
    public int Fps { get; private set; } = 30;
    public int Bitrate { get; private set; } = 8_000_000;
    public int? GopFrames { get; private set; }
    public int? Width { get; private set; }
    public int? Height { get; private set; }

    // The Tab A9+ hardware AVC decoders top out at 1920x1088 (8160 macroblocks), so larger captures are scaled down
    public int MaxWidth { get; private set; } = 1920;
    public int MaxHeight { get; private set; } = 1088;
    public H264Profile Profile { get; private set; } = H264Profile.High;
    public int? Level { get; private set; }
    public EncoderPreference Encoder { get; private set; } = EncoderPreference.Auto;
    public string? DumpPath { get; private set; }
    public int Seconds { get; private set; } = 10;
    public int Port { get; private set; } = Net.NexLink.DefaultPort;
    public bool LogFile { get; private set; } = true;
    public bool Verbose { get; private set; }

    /// <summary>Inject the tablet's touch input as mouse input on the captured output. --no-input turns it off.</summary>
    public bool InjectInput { get; private set; } = true;

    /// <summary>Draw the mouse pointer into the video (Desktop Duplication leaves it out). --no-cursor turns it off.</summary>
    public bool DrawCursor { get; private set; } = true;

    public int EffectiveGop => GopFrames ?? Fps * 10;

    public const string Usage = """
        NexScreen companion (M2): captures a monitor, encodes H.264 and serves NexLink v0-usb on 127.0.0.1.

        Usage: NexScreen.Companion [options]
          --list                 List outputs (name, resolution, refresh, adapter) and encoders, then exit.
          --output <sel>         auto (default: NexScreen monitor, else the VDD virtual monitor), nexscreen (EDID
                                 name "NexScreen" / manufacturer NXS), vdd (EDID manufacturer MTT), primary, or \\.\DISPLAYn
          --fps <n>              Maximum frame rate, 1-60 (default 30).
          --bitrate <v>          Target bitrate: bps, or with k/M suffix (default 8M).
          --gop <frames>         Keyframe interval in frames (default fps*10); IDRs are also sent on demand.
          --size <WxH>           Scale the capture to this size (default: output size). Aspect ratio is kept.
          --max-size <WxH>       Without --size, scale down captures larger than this (default 1920x1088, the
                                 Tab A9+ decoder limit; 1920x1200 becomes 1728x1080).
          --profile <main|high>  H.264 profile (default high, no B-frames).
          --level <n.n>          H.264 level to request, e.g. 4.2 (default: chosen by the encoder).
          --encoder <auto|hardware|software>  Encoder selection (default auto: hardware, then software).
          --dump <file.h264>     Write the Annex-B stream to a file instead of serving clients.
          --seconds <n>          Dump duration (default 10).
          --port <n>             TCP port (default 27183; always bound to 127.0.0.1).
          --no-cursor            Do not draw the mouse pointer into the video.
          --no-input             Do not turn touches on the tablet into mouse input on Windows.
          --no-log-file          Do not write %LOCALAPPDATA%\NexScreen\logs\companion-*.log.
          --verbose              Debug logging.
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{a} needs a value.");
                }

                return args[++i];
            }

            switch (a.ToLowerInvariant())
            {
                case "--list":
                    o.List = true;
                    break;
                case "-h" or "--help" or "/?":
                    o.Help = true;
                    break;
                case "--output":
                    o.Output = Next();
                    break;
                case "--fps":
                    o.Fps = ParseInt(a, Next(), 1, 60);
                    break;
                case "--bitrate":
                    o.Bitrate = ParseBitrate(Next());
                    break;
                case "--gop":
                    o.GopFrames = ParseInt(a, Next(), 1, 100_000);
                    break;
                case "--size":
                    (o.Width, o.Height) = ParseSize(Next());
                    break;
                case "--max-size":
                    (o.MaxWidth, o.MaxHeight) = ParseSize(Next());
                    break;
                case "--profile":
                    o.Profile = Next().ToLowerInvariant() switch
                    {
                        "main" => H264Profile.Main,
                        "high" => H264Profile.High,
                        var p => throw new ArgumentException($"Unknown profile '{p}'."),
                    };
                    break;
                case "--level":
                    o.Level = ParseLevel(Next());
                    break;
                case "--encoder":
                    o.Encoder = Next().ToLowerInvariant() switch
                    {
                        "auto" => EncoderPreference.Auto,
                        "hardware" or "hw" => EncoderPreference.Hardware,
                        "software" or "sw" => EncoderPreference.Software,
                        var e => throw new ArgumentException($"Unknown encoder '{e}'."),
                    };
                    break;
                case "--dump":
                    o.DumpPath = Next();
                    break;
                case "--seconds":
                    o.Seconds = ParseInt(a, Next(), 1, 86_400);
                    break;
                case "--port":
                    o.Port = ParseInt(a, Next(), 1, 65_535);
                    break;
                case "--no-cursor":
                    o.DrawCursor = false;
                    break;
                case "--no-input":
                    o.InjectInput = false;
                    break;
                case "--no-log-file":
                    o.LogFile = false;
                    break;
                case "--verbose":
                    o.Verbose = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{a}'.");
            }
        }

        return o;
    }

    private static int ParseInt(string name, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
        {
            throw new ArgumentException($"{name} must be an integer between {min} and {max}.");
        }

        return v;
    }

    internal static int ParseBitrate(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        double mult = 1;
        if (v.EndsWith('m'))
        {
            mult = 1_000_000;
            v = v[..^1];
        }
        else if (v.EndsWith('k'))
        {
            mult = 1_000;
            v = v[..^1];
        }

        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            throw new ArgumentException($"Invalid bitrate '{value}'.");
        }

        var bps = n * mult;
        if (bps < 100_000 || bps > 100_000_000)
        {
            throw new ArgumentException("--bitrate must be between 100k and 100M.");
        }

        return (int)bps;
    }

    internal static int ParseLevel(string value)
    {
        int[] valid = [30, 31, 32, 40, 41, 42, 50, 51, 52];
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ||
            !valid.Contains((int)Math.Round(d * 10)))
        {
            throw new ArgumentException("--level must be one of 3.0, 3.1, 3.2, 4.0, 4.1, 4.2, 5.0, 5.1, 5.2.");
        }

        return (int)Math.Round(d * 10);
    }

    public (int Width, int Height) EncodeSize(int captureWidth, int captureHeight) =>
        Streaming.EncodeSizing.Fit(Width, Height, MaxWidth, MaxHeight, captureWidth, captureHeight);

    private static (int, int) ParseSize(string value)
    {
        var parts = value.ToLowerInvariant().Split('x');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
            w < 64 || h < 64 || w > 4096 || h > 4096 || (w & 1) != 0 || (h & 1) != 0)
        {
            throw new ArgumentException("--size must be WxH with even values between 64 and 4096.");
        }

        return (w, h);
    }
}
