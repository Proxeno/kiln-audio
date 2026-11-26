using Kiln.Audio.Silk;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Benchmarks;

/// <summary>PERF-3 SILK fixed-point kernel timings (Stopwatch best of N).</summary>
internal static class Perf3SilkMeasure
{
    /// <summary>Measures the NLSF to LPC conversion and the LPC synthesis loops.</summary>
    public static void Run()
    {
        Console.WriteLine("== PERF-3 SILK (best of 15 batches) ==");
        MeasureNlsf(10);
        MeasureNlsf(16);
        MeasureLpc(10);
        MeasureLpc(16);
    }

    private static void MeasureNlsf(int order)
    {
        var nlsf = new short[order];
        for (int i = 0; i < order; i++)
        {
            nlsf[i] = (short)(((i + 1) * 32768) / (order + 1));
        }

        var aQ12 = new short[order];
        Perf3Timing.Measure(
            $"NlsfToLpc{order}",
            () => SilkNlsf.ConvertToLpc(aQ12, nlsf, order),
            iterations: 20000);
    }

    private static void MeasureLpc(int order)
    {
        const int Subframe = 80;
        var lpc = new int[SilkLpcKernel.MaxLpcOrder + Subframe];
        var coefficients = new short[order];
        var residual = new int[Subframe];
        var output = new short[Subframe];
        for (int i = 0; i < coefficients.Length; i++)
        {
            coefficients[i] = (short)(i % 3 == 0 ? -120 : 80);
        }

        for (int i = 0; i < residual.Length; i++)
        {
            residual[i] = (i % 7) - 3;
        }

        Perf3Timing.Measure(
            $"LpcSynth{order}",
            () => SilkLpcKernel.Synthesize(lpc, coefficients, residual, 512, order, Subframe, output),
            iterations: 20000);
    }
}
