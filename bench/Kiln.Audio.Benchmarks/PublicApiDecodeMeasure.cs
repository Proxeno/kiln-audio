using System.Diagnostics;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// PUB-PERF-1: times the shipped entry point, <c>OpusDecoder.Decode(packet, short[])</c>, over a
/// whole RFC vector with one outer timer per pass and reports the best of N passes.
/// </summary>
/// <remarks>
/// The libopus side is <c>libopus_bench decode</c> (<c>opus_decode</c>, same rate and channel
/// count, same N, same statistic). Run <c>micro pubapi [passes]</c>; each line is
/// <c>pubapi vector=NN channels=C packets=P best_s=S</c>.
/// </remarks>
internal static class PublicApiDecodeMeasure
{
    private const int DefaultPasses = 15;
    private const int WarmupPasses = 20;

    internal static void Run(string[] args)
    {
        int passes = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : DefaultPasses;
        Console.WriteLine(FormattableString.Invariant($"== PUB-PERF-1 public API decode: best of {passes} passes, 48 kHz =="));
        foreach (int channels in new[] { 2, 1 })
        {
            for (int vector = 1; vector <= 12; vector++)
            {
                byte[][] packets = TestVectorData.ReadPackets(vector);
                short[] pcm = new short[48000 * 120 / 1000 * channels];
                double best = MeasureVector(packets, pcm, channels, passes);
                Console.WriteLine(FormattableString.Invariant(
                    $"pubapi vector={vector:D2} channels={channels} packets={packets.Length} best_s={best:F9}"));
            }
        }
    }

    private static double MeasureVector(byte[][] packets, short[] pcm, int channels, int passes)
    {
        var options = new OpusDecoderOptions { SampleRate = 48000, Channels = channels };

        // Tier-1 warm-up on a throwaway decoder so the JIT has re-compiled the hot paths.
        using (var warm = new OpusDecoder(options))
        {
            for (int i = 0; i < WarmupPasses; i++)
            {
                DecodeAll(warm, packets, pcm);
            }
        }

        double best = double.MaxValue;
        for (int pass = 0; pass < passes; pass++)
        {
            using var decoder = new OpusDecoder(options);
            long start = Stopwatch.GetTimestamp();
            DecodeAll(decoder, packets, pcm);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (elapsed < best)
            {
                best = elapsed;
            }
        }

        return best;
    }

    private static void DecodeAll(OpusDecoder decoder, byte[][] packets, short[] pcm)
    {
        foreach (byte[] packet in packets)
        {
            decoder.Decode(packet, pcm);
        }
    }
}
