using System.Diagnostics;

namespace Kiln.Audio.Benchmarks;

/// <summary>Shared Stopwatch best-of-N timing loop for the PERF-3 measurements.</summary>
internal static class Perf3Timing
{
    /// <summary>Times <paramref name="action"/> and prints the fastest batch's per-call cost.</summary>
    /// <param name="name">The label to print.</param>
    /// <param name="action">The work to repeat.</param>
    /// <param name="iterations">The number of calls per batch.</param>
    /// <returns>The best per-call time in microseconds.</returns>
    public static double Measure(string name, Action action, int iterations)
    {
        // Warm up for at least ~250 ms so CPU frequency and the tiered JIT state settle before the
        // timed batches; the shared machine's load then only costs whole batches, not the best one.
        long warmupStart = Stopwatch.GetTimestamp();
        long warmupTicks = Stopwatch.Frequency / 4;
        while (true)
        {
            for (int i = 0; i < iterations; i++)
            {
                action();
            }

            if (Stopwatch.GetTimestamp() - warmupStart >= warmupTicks)
            {
                break;
            }
        }

        double best = double.MaxValue;
        long allocated = 0;
        for (int batch = 0; batch < 25; batch++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
            {
                action();
            }

            long end = Stopwatch.GetTimestamp();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            double microseconds = (end - start) * 1_000_000.0 / Stopwatch.Frequency / iterations;
            best = Math.Min(best, microseconds);
        }

        Console.WriteLine($"{name,-14} {best,8:F3} us   {allocated,6} B");
        return best;
    }
}
