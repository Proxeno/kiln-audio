using Kiln.Audio.Celt;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// A Stopwatch best-of-N microbenchmark for the PERF-3 kernels.
/// </summary>
/// <remarks>
/// BenchmarkDotNet 0.15 does not recognize the .NET 11 runtime this machine provides, so the
/// PERF-3 measurements use a plain in-process loop instead: warm up, then take the fastest of
/// several batches of a fixed iteration count. The fastest batch is the statistic the shared
/// machine's noise leaves intact.
/// </remarks>
internal static class Perf3Measure
{
    private const int Overlap = 120;
    private const int ShortBlocks = 8;

    /// <summary>Runs the requested measurement set.</summary>
    /// <param name="args">The command line; <c>micro</c> followed by an optional group name.</param>
    public static void Run(string[] args)
    {
        string group = args.Length > 1 ? args[1] : "all";
        if (group is "all" or "mdct")
        {
            MeasureMdct();
        }

        if (group is "all" or "silk")
        {
            Perf3SilkMeasure.Run();
        }

        if (group is "decode")
        {
            MeasureDecodeStages();
        }
    }

    private static void MeasureDecodeStages()
    {
        Console.WriteLine("== PERF-3 whole-vector stages (tier-1 warmed, average of 3 passes) ==");

        // The tiered JIT keeps a method in the quick (unoptimized) tier for its first ~30 calls,
        // so warm a throwaway instance until the shared code has been re-jitted before measuring.
        foreach (int vector in new[] { 1, 7, 11 })
        {
            var warmup = new CeltDecodeStageBenchmarks { Vector = vector };
            warmup.Setup();
            for (int i = 0; i < 40; i++)
            {
                warmup.Decode();
            }

            var benchmark = new CeltDecodeStageBenchmarks { Vector = vector };
            benchmark.Setup();
            for (int i = 0; i < 3; i++)
            {
                benchmark.Decode();
            }

            benchmark.Report();
        }

        foreach (int vector in new[] { 2, 3, 4 })
        {
            var warmup = new SilkDecodeStageBenchmarks { Vector = vector };
            warmup.Setup();
            for (int i = 0; i < 40; i++)
            {
                warmup.Decode();
            }

            var benchmark = new SilkDecodeStageBenchmarks { Vector = vector };
            benchmark.Setup();
            for (int i = 0; i < 3; i++)
            {
                benchmark.Decode();
            }

            benchmark.Report();
        }
    }

    private static void MeasureMdct()
    {
        Console.WriteLine("== PERF-3 MDCT (warmed; best of 25 batches; 0 B allocated) ==");
        var sizes = new[] { 120, 240, 480, 960 };
        foreach (int size in sizes)
        {
            var mdct = new Mdct(size);
            var input = new float[size + Overlap];
            var coefficients = new float[size];
            var output = new float[size + Overlap];
            for (int i = 0; i < input.Length; i++)
            {
                input[i] = (float)Math.Sin(0.017 * i);
            }

            int iterations = Math.Max(1000, 500000 / size);
            Perf3Timing.Measure($"Forward{size}", () => mdct.Forward(input, coefficients), iterations);
            Perf3Timing.Measure($"Inverse{size}", () => mdct.Inverse(coefficients, output), iterations);
        }

        var shortMdct = new Mdct(120);
        var shortInput = new float[ShortBlocks * (120 + Overlap)];
        var shortCoefficients = new float[ShortBlocks * 120];
        var shortOutput = new float[120 + Overlap];
        for (int i = 0; i < shortInput.Length; i++)
        {
            shortInput[i] = (float)Math.Sin(0.29 * i);
        }

        Perf3Timing.Measure(
            "Forward8x120",
            () =>
            {
                for (int block = 0; block < ShortBlocks; block++)
                {
                    shortMdct.Forward(
                        shortInput.AsSpan(block * (120 + Overlap), 120 + Overlap),
                        shortCoefficients.AsSpan(block),
                        stride: ShortBlocks);
                }
            },
            iterations: 2000);
        Perf3Timing.Measure(
            "Inverse8x120",
            () =>
            {
                for (int block = 0; block < ShortBlocks; block++)
                {
                    shortMdct.Inverse(shortCoefficients.AsSpan(block), shortOutput, stride: ShortBlocks);
                }
            },
            iterations: 2000);

        // The complex FFT the 960-coefficient MDCT runs, to show its share of the total.
        var fft = Fft.Get(480);
        var fftReal = new float[480];
        var fftImaginary = new float[480];
        var fftOutReal = new float[480];
        var fftOutImaginary = new float[480];
        for (int i = 0; i < fftReal.Length; i++)
        {
            fftReal[i] = (float)Math.Sin(0.031 * i);
            fftImaginary[i] = (float)Math.Cos(0.017 * i);
        }

        Perf3Timing.Measure(
            "Fft480Fwd",
            () => fft.Transform(fftReal, fftImaginary, fftOutReal, fftOutImaginary, inverse: false, useVector: true),
            iterations: 2000);
        Perf3Timing.Measure(
            "Fft480Inv",
            () => fft.Transform(fftReal, fftImaginary, fftOutReal, fftOutImaginary, inverse: true, useVector: true),
            iterations: 2000);
    }
}
