using BenchmarkDotNet.Running;

namespace Kiln.Audio.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        // PERF-3: a Stopwatch best-of-N harness that does not depend on BenchmarkDotNet's runtime
        // validation (this machine only has the .NET 11 runtime, which BDN 0.15 does not know).
        if (args.Length > 1 && args[0] == "micro" && args[1] == "perf4")
        {
            Perf4ProfileMeasure.Run();
            return;
        }

        if (args.Length > 0 && args[0] == "micro")
        {
            Perf3Measure.Run(args);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
