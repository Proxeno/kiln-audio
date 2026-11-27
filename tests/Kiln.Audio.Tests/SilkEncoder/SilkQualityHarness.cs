using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Shared G2 measurement code: the corpus, the independent reference signal, segmental SNR, and
/// the three encoders under comparison (ours, libopus 1.5.2, and the RFC 6716 Appendix A encoder).
/// </summary>
internal static class SilkQualityHarness
{
    private static readonly Lazy<IReadOnlyList<(int Vector, short[] Mono)>> CorpusLazy = new(LoadCorpus);
    private static readonly ConcurrentDictionary<(int Vector, int Rate), short[]> References = new();

    /// <summary>The corpus: RFC 8251 test vectors 2, 3, 4 and 6, decoded and mixed to mono, 48 kHz.</summary>
    internal static IReadOnlyList<(int Vector, short[] Mono)> Corpus => CorpusLazy.Value;

    /// <summary>Internal SILK rate for a bandwidth.</summary>
    internal static int InternalRate(OpusBandwidth bandwidth) => bandwidth switch
    {
        OpusBandwidth.Narrowband => 8000,
        OpusBandwidth.Mediumband => 12000,
        _ => 16000,
    };

    /// <summary>The opus_demo name of a bandwidth.</summary>
    internal static string BandwidthName(OpusBandwidth bandwidth) => bandwidth switch
    {
        OpusBandwidth.Narrowband => "NB",
        OpusBandwidth.Mediumband => "MB",
        _ => "WB",
    };

    /// <summary>Frame duration in milliseconds.</summary>
    internal static int Milliseconds(OpusFrameDuration duration) => duration == OpusFrameDuration.Ms20 ? 20 : 60;

    /// <summary>The independent reference: <paramref name="vector"/> through <see cref="IdealResampler"/>.</summary>
    internal static short[] Reference(int vector, short[] mono, int internalRate) =>
        References.GetOrAdd((vector, internalRate), _ => IdealResampler.Resample(mono, 48000, internalRate));

    /// <summary>Measures one encoder over the whole corpus; returns the segment-weighted mean segSNR.</summary>
    internal static double Aggregate(int internalRate, Func<short[], short[], double> measure, out string detail)
    {
        double total = 0;
        long segments = 0;
        var lines = new List<string>();
        foreach ((int vector, short[] mono) in Corpus)
        {
            short[] reference = Reference(vector, mono, internalRate);
            double snr = measure(mono, reference);
            long count = reference.Length / (internalRate / 50);
            total += snr * count;
            segments += count;
            lines.Add(FormattableString.Invariant($"v{vector} {snr:F2}"));
        }

        detail = string.Join(", ", lines);
        return total / segments;
    }

    /// <summary>Encodes with our encoder, decodes with libopus and scores against <paramref name="reference"/>.</summary>
    internal static double MeasureOurs(
        short[] mono, OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate, int complexity,
        int internalRate, short[] reference)
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 1,
            Bandwidth = bandwidth,
            BitrateBps = bitrate,
            FrameDuration = duration,
            Complexity = complexity,
        };

        int frameSamples = 48000 * Milliseconds(duration) / 1000;
        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        var bit = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        for (int offset = 0; offset + frameSamples <= mono.Length; offset += frameSamples)
        {
            int written = encoder.Encode(mono.AsSpan(offset, frameSamples), packet);
            BinaryPrimitives.WriteInt32BigEndian(header[..4], written);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], encoder.FinalRange);
            bit.Write(header);
            bit.Write(packet.AsSpan(0, written));
        }

        return Score(reference, DecodeBitstream(bit.ToArray(), internalRate), internalRate);
    }

    /// <summary>Encodes with libopus 1.5.2 (the oracle), decodes and scores.</summary>
    internal static double MeasureLibopus(
        short[] mono, OpusBandwidth bandwidth, int ms, int bitrate, int complexity, int internalRate, short[] reference)
    {
        byte[] encoded = LibOpusOracle.Encode(mono, 48000, 1, bitrate, EncoderArguments(bandwidth, ms, complexity));
        return Score(reference, DecodeBitstream(encoded, internalRate), internalRate);
    }

    /// <summary>Encodes with an external opus_demo (the RFC 6716 Appendix A build), decodes with libopus and scores.</summary>
    internal static double MeasureExternal(
        string opusDemo, short[] mono, OpusBandwidth bandwidth, int ms, int bitrate, int complexity,
        int internalRate, short[] reference)
    {
        string stem = Guid.NewGuid().ToString("N");
        string input = Path.Combine(Path.GetTempPath(), $"kiln-rfc-in-{stem}.pcm");
        string output = Path.Combine(Path.GetTempPath(), $"kiln-rfc-out-{stem}.bit");
        try
        {
            Pcm16.Write(input, mono);
            var start = new ProcessStartInfo(opusDemo)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (string a in new[] { "-e", "voip", "48000", "1", bitrate.ToString(CultureInfo.InvariantCulture) })
            {
                start.ArgumentList.Add(a);
            }

            foreach (string a in EncoderArguments(bandwidth, ms, complexity))
            {
                start.ArgumentList.Add(a);
            }

            start.ArgumentList.Add(input);
            start.ArgumentList.Add(output);
            using Process process = Process.Start(start)!;

            // Drain both pipes concurrently (opus_demo writes a lot to stderr; reading them one
            // after the other can deadlock) and bound the run, killing the whole tree on timeout.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10 * 60 * 1000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{opusDemo} did not finish within 10 minutes.");
            }

            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{opusDemo} exited with {process.ExitCode}.");
            }

            return Score(reference, DecodeBitstream(File.ReadAllBytes(output), internalRate), internalRate);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static string[] EncoderArguments(OpusBandwidth bandwidth, int ms, int complexity) =>
    [
        "-bandwidth", BandwidthName(bandwidth),
        "-framesize", ms.ToString(CultureInfo.InvariantCulture),
        "-complexity", complexity.ToString(CultureInfo.InvariantCulture),
    ];

    internal static short[] DecodeBitstream(byte[] bit, int internalRate)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kiln-silk-g2-{Guid.NewGuid():N}.bit");
        try
        {
            File.WriteAllBytes(path, bit);
            return LibOpusOracle.Decode(path, internalRate, 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Segmental SNR in dB: 20 ms segments, each clamped to [-10, 60] dB, averaged. The decoder's
    /// delay is found by cross-correlation and removed.
    /// </summary>
    internal static double Score(short[] reference, short[] decoded, int internalRate)
    {
        int segment = internalRate / 50;
        int delay = FindDelay(reference, decoded);
        double total = 0;
        long count = 0;
        int first = Math.Max(0, -delay);
        int limit = Math.Min(reference.Length, decoded.Length - delay);
        for (int start = first; start + segment <= limit; start += segment)
        {
            double signal = 0;
            double noise = 0;
            for (int i = 0; i < segment; i++)
            {
                double s = reference[start + i];
                double d = decoded[start + i + delay] - s;
                signal += s * s;
                noise += d * d;
            }

            double snr = signal <= 0
                ? (noise <= 0 ? 60 : -10)
                : (noise <= 0 ? 60 : 10 * Math.Log10(signal / noise));
            total += Math.Clamp(snr, -10, 60);
            count++;
        }

        return count == 0 ? -10 : total / count;
    }

    // Coarse search in steps of 4 samples, then a one-sample refinement around the best.
    private static int FindDelay(short[] reference, short[] decoded)
    {
        const int Maximum = 400;
        int best = 0;
        double bestCorrelation = double.NegativeInfinity;
        for (int delay = -Maximum; delay <= Maximum; delay += 4)
        {
            double c = Correlate(reference, decoded, delay, Maximum, 4);
            if (c > bestCorrelation)
            {
                bestCorrelation = c;
                best = delay;
            }
        }

        int coarse = best;
        bestCorrelation = double.NegativeInfinity;
        for (int delay = coarse - 3; delay <= coarse + 3; delay++)
        {
            double c = Correlate(reference, decoded, delay, Maximum, 1);
            if (c > bestCorrelation)
            {
                bestCorrelation = c;
                best = delay;
            }
        }

        return best;
    }

    private static double Correlate(short[] reference, short[] decoded, int delay, int margin, int step)
    {
        double correlation = 0;
        int limit = Math.Min(reference.Length, decoded.Length);
        for (int i = margin; i + margin < limit; i += step)
        {
            int j = i + delay;
            if (j >= 0 && j < decoded.Length)
            {
                correlation += (double)reference[i] * decoded[j];
            }
        }

        return correlation;
    }

    private static IReadOnlyList<(int Vector, short[] Mono)> LoadCorpus()
    {
        var corpus = new List<(int, short[])>();
        foreach (int vector in new[] { 2, 3, 4, 6 })
        {
            short[] stereo = Pcm16.Read(TestVectors.DecPath(vector));
            var mono = new short[stereo.Length / 2];
            for (int i = 0; i < mono.Length; i++)
            {
                mono[i] = (short)(((int)stereo[2 * i] + stereo[2 * i + 1]) / 2);
            }

            corpus.Add((vector, mono));
        }

        return corpus;
    }
}
