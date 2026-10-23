using System.Diagnostics;

namespace NexScreen.Companion.Util;

/// <summary>Monotonic clock shared by capture timestamps and protocol headers.</summary>
public static class MonoClock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    public static long NowTicks => Stopwatch.GetTimestamp() - Origin;

    public static ulong NowMicroseconds => (ulong)(NowTicks * 1_000_000.0 / Stopwatch.Frequency);

    /// <summary>Time in 100 ns units, as used by Media Foundation sample times.</summary>
    public static long Now100ns => (long)(NowTicks * 10_000_000.0 / Stopwatch.Frequency);

    public static double NowSeconds => NowTicks / (double)Stopwatch.Frequency;
}
