using System.Diagnostics;
using System.Globalization;
using System.IO;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// SIMD-3 encode harness: <c>micro enc dump &lt;dir&gt;</c> writes the PCM corpora, and
/// <c>micro enc run &lt;pcm&gt; &lt;rate&gt; &lt;ch&gt; &lt;app&gt; &lt;bitrate&gt; &lt;bw|-&gt; &lt;cx&gt; &lt;passes&gt; [profile]</c>
/// reports best-of-N microseconds per 20 ms frame, the packet-mode histogram and, with a
/// KILN_ENC_PROFILE build, the per-stage split.
/// </summary>
internal static class EncMeasure
{
    internal static void Run(string[] args)
    {
        if (args[2] == "dump")
        {
            Directory.CreateDirectory(args[3]);
            short[] mono = CorpusData.LoadMono();
            WriteShorts(Path.Combine(args[3], "mono48.pcm"), mono);
            var stereo = new List<short>();
            for (int v = 1; v <= 12; v++)
            {
                stereo.AddRange(CorpusData.ReadStereo(v));
            }

            WriteShorts(Path.Combine(args[3], "stereo48.pcm"), [.. stereo]);
            return;
        }

        string[] a = args[3..];
        string path = a[0];
        int rate = int.Parse(a[1], CultureInfo.InvariantCulture);
        int channels = int.Parse(a[2], CultureInfo.InvariantCulture);
        OpusApplication app = a[3] switch
        {
            "voip" => OpusApplication.Voip,
            "audio" => OpusApplication.Audio,
            _ => OpusApplication.RestrictedLowDelay,
        };
        int bitrate = int.Parse(a[4], CultureInfo.InvariantCulture);
        OpusBandwidth? bandwidth = a[5] switch
        {
            "NB" => OpusBandwidth.Narrowband,
            "MB" => OpusBandwidth.Mediumband,
            "WB" => OpusBandwidth.Wideband,
            "SWB" => OpusBandwidth.SuperWideband,
            "FB" => OpusBandwidth.Fullband,
            _ => null,
        };
        int complexity = int.Parse(a[6], CultureInfo.InvariantCulture);
        int passes = int.Parse(a[7], CultureInfo.InvariantCulture);
        bool profile = args.Length > 11 && a[8] == "profile";

        byte[] bytes = File.ReadAllBytes(path);
        var pcm = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
        int frameSamples = rate / 50 * channels;
        int frames = pcm.Length / frameSamples;
        var packet = new byte[1276];
        double best = double.MaxValue;
        long[] bestTicks = new long[EncProf.Slots];
        long[] modes = new long[3];
        ulong hash = 14695981039346656037UL;

        for (int pass = 0; pass < passes; pass++)
        {
            var encoder = new OpusEncoder(new OpusEncoderOptions
            {
                Application = app,
                SampleRate = rate,
                Channels = channels,
                BitrateBps = bitrate,
                FrameDuration = OpusFrameDuration.Ms20,
                Complexity = complexity,
                Bandwidth = bandwidth,
                UseCbr = true,
                PerformanceMode = string.Equals(Environment.GetEnvironmentVariable("KILN_BENCH_MODE"), "relaxed", StringComparison.OrdinalIgnoreCase)
                    ? OpusPerformanceMode.Relaxed : OpusPerformanceMode.Precise,
            });
            long[] pm = new long[3];
            ulong passHash = 14695981039346656037UL;
            EncProf.Reset();
            long start = Stopwatch.GetTimestamp();
            for (int f = 0; f < frames; f++)
            {
                int n = encoder.Encode(pcm.AsSpan(f * frameSamples, frameSamples), packet);
                for (int bi = 0; bi < n; bi++)
                {
                    passHash = (passHash ^ packet[bi]) * 1099511628211UL;
                }

                int cfg = packet[0] >> 3;
                pm[cfg < 12 ? 0 : cfg < 16 ? 1 : 2]++;
            }

            double us = (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency / frames;
            encoder.Dispose();
            hash = passHash;
            if (us < best)
            {
                best = us;
                Array.Copy(EncProf.Ticks, bestTicks, bestTicks.Length);
                modes = pm;
            }
        }

        Console.WriteLine(FormattableString.Invariant(
            $"kiln-encode file={Path.GetFileName(path)} rate={rate} ch={channels} app={a[3]} bitrate={bitrate} bw={a[5]} cx={complexity} frames={frames} us_per_frame={best:F3} modes: silk={modes[0]} hybrid={modes[1]} celt={modes[2]} hash={hash:x16}"));
        if (Environment.GetEnvironmentVariable("KILN_BENCH_SNR") == "1")
        {
            var qenc = new OpusEncoder(new OpusEncoderOptions
            {
                Application = app,
                SampleRate = rate,
                Channels = channels,
                BitrateBps = bitrate,
                FrameDuration = OpusFrameDuration.Ms20,
                Complexity = complexity,
                Bandwidth = bandwidth,
                UseCbr = true,
                PerformanceMode = string.Equals(Environment.GetEnvironmentVariable("KILN_BENCH_MODE"), "relaxed", StringComparison.OrdinalIgnoreCase)
                    ? OpusPerformanceMode.Relaxed : OpusPerformanceMode.Precise,
            });
            var qdec = new OpusDecoder(new OpusDecoderOptions { SampleRate = rate, Channels = channels });
            var decoded = new short[frames * frameSamples];
            var dframe = new short[frameSamples * 6];
            long totalBytes = 0;
            int maxBytes = 0;
            for (int f = 0; f < frames; f++)
            {
                int n = qenc.Encode(pcm.AsSpan(f * frameSamples, frameSamples), packet);
                totalBytes += n;
                maxBytes = Math.Max(maxBytes, n);
                int got = qdec.Decode(packet.AsSpan(0, n), dframe);
                Array.Copy(dframe, 0, decoded, f * frameSamples, Math.Min(got * channels, frameSamples));
            }

            Console.WriteLine(FormattableString.Invariant($"snr_db={SegSnr(pcm, decoded, channels, rate):F4} avg_bytes={(double)totalBytes / frames:F2} max_bytes={maxBytes}"));
        }

        if (profile)
        {
            double total = 0;
            for (int i = 0; i < EncProf.Slots; i++)
            {
                total += bestTicks[i];
            }

            for (int i = 0; i < EncProf.Slots; i++)
            {
                if (EncProf.Names[i] is { } name && bestTicks[i] > 0)
                {
                    double us2 = bestTicks[i] * 1e6 / Stopwatch.Frequency / frames;
                    Console.WriteLine(FormattableString.Invariant($"  stage {i,2} {name,-44} {us2,8:F3} us  {100 * bestTicks[i] / total,5:F1}%"));
                }
            }
        }
    }

    private static double SegSnr(short[] reference, short[] test, int channels, int rate)
    {
        int seg = rate / 50;
        int perChannel = Math.Min(reference.Length, test.Length) / channels;
        int bestDelay = 0;
        double bestVal = double.MinValue;
        int probe = Math.Min(perChannel - 4000, rate * 4);
        for (int delay = 0; delay < 2000; delay++)
        {
            double sig = 0;
            double noise = 0;
            for (int i = 4000; i < probe; i += 3)
            {
                double r = reference[i * channels];
                double d = test[(i + delay) * channels] - r;
                sig += r * r;
                noise += d * d;
            }

            double v = -noise / Math.Max(sig, 1);
            if (v > bestVal)
            {
                bestVal = v;
                bestDelay = delay;
            }
        }

        double total = 0;
        long count = 0;
        for (int start = 0; start + seg <= perChannel - bestDelay; start += seg)
        {
            double signal = 0;
            double noise = 0;
            for (int i = 0; i < seg; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    double r = reference[((start + i) * channels) + c];
                    double d = test[((start + i + bestDelay) * channels) + c] - r;
                    signal += r * r;
                    noise += d * d;
                }
            }

            double snr = signal <= 0 ? (noise <= 0 ? 60 : -10) : 10 * Math.Log10(signal / Math.Max(noise, 1e-9));
            total += Math.Clamp(snr, -10, 60);
            count++;
        }

        return total / count;
    }

    private static void WriteShorts(string path, short[] data)
    {
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(path, bytes);
    }
}
