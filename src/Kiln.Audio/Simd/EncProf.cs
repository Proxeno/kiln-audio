using System.Diagnostics;

namespace Kiln.Audio.Simd;

/// <summary>Per-stage encode profiler; every call site compiles away unless KILN_ENC_PROFILE is defined.</summary>
internal static class EncProf
{
    internal const int Slots = 40;
    internal static readonly long[] Ticks = new long[Slots];
    internal static readonly string[] Names = new string[Slots];
    private static long s_last;

    [Conditional("KILN_ENC_PROFILE")]
    internal static void Reset()
    {
        Array.Clear(Ticks);
        s_last = Stopwatch.GetTimestamp();
    }

    [Conditional("KILN_ENC_PROFILE")]
    internal static void Restart() => s_last = Stopwatch.GetTimestamp();

    [Conditional("KILN_ENC_PROFILE")]
    internal static void Mark(int slot, string name)
    {
        long now = Stopwatch.GetTimestamp();
        Ticks[slot] += now - s_last;
        Names[slot] = name;
        s_last = Stopwatch.GetTimestamp();
    }
}
