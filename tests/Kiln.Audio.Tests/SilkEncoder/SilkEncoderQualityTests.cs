using System.Globalization;
using FluentAssertions;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// G2: aggregate segmental SNR of our SILK encoder against libopus, at complexity 0, 5 and 10.
/// The gate is ours &gt;= libopus at complexity 0; the other two are reported.
/// </summary>
public class SilkEncoderQualityTests
{
    private static readonly Lazy<IReadOnlyList<(int Vector, short[] Mono)>> Corpus = new(LoadCorpus);

    public static TheoryData<OpusBandwidth, OpusFrameDuration, int, int> Cases()
    {
        var data = new TheoryData<OpusBandwidth, OpusFrameDuration, int, int>();
        foreach (OpusBandwidth bandwidth in new[]
                 {
                     OpusBandwidth.Narrowband, OpusBandwidth.Mediumband, OpusBandwidth.Wideband,
                 })
        {
            foreach (OpusFrameDuration duration in new[] { OpusFrameDuration.Ms20, OpusFrameDuration.Ms60 })
            {
                foreach (int bitrate in new[] { 8000, 12000, 20000 })
                {
                    if (bandwidth == OpusBandwidth.Narrowband && bitrate == 20000)
                    {
                        continue;
                    }

                    foreach (int complexity in new[] { 0, 5, 10 })
                    {
                        data.Add(bandwidth, duration, bitrate, complexity);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Quality_against_libopus(OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate, int complexity)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        int ms = duration == OpusFrameDuration.Ms20 ? 20 : 60;
        int internalRate = bandwidth switch
        {
            OpusBandwidth.Narrowband => 8000,
            OpusBandwidth.Mediumband => 12000,
            _ => 16000,
        };

        double oursTotal = 0;
        double libopusTotal = 0;
        long segments = 0;
        var lines = new List<string>();

        foreach ((int vector, short[] mono) in Corpus.Value)
        {
            short[] reference = ReferenceFor(vector, mono, internalRate);
            double ours = MeasureOurs(mono, bandwidth, duration, bitrate, complexity, internalRate, reference, out double ourQuality);
            double theirs = MeasureLibopus(mono, bandwidth, ms, bitrate, complexity, internalRate, reference, out double theirQuality);

            long count = reference.Length / (internalRate / 50);
            oursTotal += ours * count;
            libopusTotal += theirs * count;
            segments += count;
            lines.Add($"v{vector}: ours {ours:F2} libopus {theirs:F2} (q {FormatQuality(ourQuality)} / {FormatQuality(theirQuality)})");
        }

        double oursMean = oursTotal / segments;
        double libopusMean = libopusTotal / segments;
        Report(bandwidth, ms, bitrate, complexity, oursMean, libopusMean, string.Join("; ", lines));

        if (complexity == 0)
        {
            oursMean.Should().BeGreaterThanOrEqualTo(
                libopusMean,
                $"G2 requires ours >= libopus at complexity 0 ({bandwidth}, {ms} ms, {bitrate} bps): " +
                $"ours {oursMean:F2} dB, libopus {libopusMean:F2} dB");
        }
    }

    private static double MeasureOurs(
        short[] mono, OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate, int complexity,
        int internalRate, short[] reference, out double quality)
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

        int frameSamples = 48000 * (duration == OpusFrameDuration.Ms20 ? 20 : 60) / 1000;
        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        var bit = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        for (int offset = 0; offset + frameSamples <= mono.Length; offset += frameSamples)
        {
            int written = encoder.Encode(mono.AsSpan(offset, frameSamples), packet);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[..4], written);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[4..], encoder.FinalRange);
            bit.Write(header);
            bit.Write(packet.AsSpan(0, written));
        }

        short[] decoded = DecodeBitstream(bit.ToArray(), internalRate);
        return Score(reference, decoded, internalRate, out quality);
    }

    private static double MeasureLibopus(
        short[] mono, OpusBandwidth bandwidth, int ms, int bitrate, int complexity,
        int internalRate, short[] reference, out double quality)
    {
        string name = bandwidth switch
        {
            OpusBandwidth.Narrowband => "NB",
            OpusBandwidth.Mediumband => "MB",
            _ => "WB",
        };

        byte[] encoded = LibOpusOracle.Encode(
            mono, 48000, 1, bitrate,
            ["-bandwidth", name, "-framesize", ms.ToString(CultureInfo.InvariantCulture),
             "-complexity", complexity.ToString(CultureInfo.InvariantCulture)]);

        short[] decoded = DecodeBitstream(encoded, internalRate);
        return Score(reference, decoded, internalRate, out quality);
    }

    private static short[] DecodeBitstream(byte[] bit, int internalRate)
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

    private static double Score(short[] reference, short[] decoded, int internalRate, out double quality)
    {
        int segment = internalRate / 50;
        int delay = FindDelay(reference, decoded, internalRate);
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

        quality = CompareQuality(reference, decoded, delay, internalRate);
        return count == 0 ? -10 : total / count;
    }

    /// <summary>
    /// Attempts to run <c>opus_compare</c> on the aligned signals, capped at ten seconds. The
    /// result is reported, not gated. <c>opus_compare</c> only scores 48 kHz, and comparing a
    /// band-limited SILK decode to the full-band original is not its intended use, so the metric
    /// is reported as "n/a" unless the tool returns a usable score.
    /// </summary>
    private static double CompareQuality(short[] reference, short[] decoded, int delay, int internalRate)
    {
        try
        {
            int start = Math.Max(0, -delay);
            int available = Math.Min(reference.Length - start, decoded.Length - start - delay);
            int length = Math.Min(available, 10 * internalRate);
            if (length <= internalRate / 5)
            {
                return double.NaN;
            }

            // opus_compare always reads its reference file as stereo, so the mono reference is
            // duplicated to two identical channels.
            var referenceStereo = new short[length * 2];
            for (int i = 0; i < length; i++)
            {
                short value = reference[start + i];
                referenceStereo[2 * i] = value;
                referenceStereo[(2 * i) + 1] = value;
            }

            short[] test = decoded.AsSpan(start + delay, length).ToArray();
            return LibOpusOracle.Compare(referenceStereo, test, internalRate, stereo: false).Quality;
        }
        catch (Exception)
        {
            return double.NaN;
        }
    }

    private static string FormatQuality(double quality) =>
        double.IsNaN(quality) ? "n/a" : FormattableString.Invariant($"{quality:F1}%");

    private static int FindDelay(short[] reference, short[] decoded, int internalRate)
    {
        // Align on the band above the encoder high-pass (60-100 Hz), where both codecs encode
        // the same content. Searching the full band locks onto the low-frequency content that
        // the codec removes, and the previous fixed four-sample step misaligned 12 kHz output by
        // up to two samples; both cost several dB and made the bands incomparable.
        short[] referenceHighPassed = HighPass(reference, 150, internalRate);
        short[] decodedHighPassed = HighPass(decoded, 150, internalRate);
        const int maximum = 500;
        int best = 0;
        double bestCorrelation = double.NegativeInfinity;
        for (int delay = -maximum; delay <= maximum; delay += 8)
        {
            double correlation = NormalizedCorrelation(referenceHighPassed, decodedHighPassed, delay);
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                best = delay;
            }
        }

        int coarse = best;
        for (int delay = coarse - 7; delay <= coarse + 7; delay++)
        {
            double correlation = NormalizedCorrelation(referenceHighPassed, decodedHighPassed, delay);
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                best = delay;
            }
        }

        return best;
    }

    private static double NormalizedCorrelation(short[] reference, short[] decoded, int delay)
    {
        double numerator = 0;
        double referencePower = 0;
        double decodedPower = 0;
        int limit = Math.Min(reference.Length, decoded.Length);
        for (int i = 400; i + 400 < limit; i += 2)
        {
            int j = i + delay;
            if (j < 0 || j >= decoded.Length)
            {
                continue;
            }

            double r = reference[i];
            double d = decoded[j];
            numerator += r * d;
            referencePower += r * r;
            decodedPower += d * d;
        }

        return referencePower <= 0 || decodedPower <= 0
            ? double.NegativeInfinity
            : numerator / Math.Sqrt(referencePower * decodedPower);
    }

    /// <summary>An RBJ second-order high-pass, used only to align the two decodes.</summary>
    private static short[] HighPass(short[] input, double cutoffHz, int sampleRate)
    {
        double w = 2 * Math.PI * cutoffHz / sampleRate;
        double alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
        double cos = Math.Cos(w);
        double a0 = 1 + alpha;
        double b0 = (1 + cos) / 2 / a0;
        double b1 = -(1 + cos) / a0;
        double b2 = b0;
        double a1 = -2 * cos / a0;
        double a2 = (1 - alpha) / a0;
        var output = new short[input.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < input.Length; i++)
        {
            double x = input[i];
            double y = (b0 * x) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2);
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;
            output[i] = (short)Math.Clamp((int)Math.Round(y), short.MinValue, short.MaxValue);
        }

        return output;
    }

    private static readonly Dictionary<(int Vector, int Rate), short[]> ReferenceCache = new();

    private static short[] ReferenceFor(int vector, short[] mono, int internalRate)
    {
        // The brief asks for an independent reference: an ideal windowed-sinc band-limited
        // version of the original, not the encoder's own resampler. The ideal resampler is
        // slow, so each (vector, rate) pair is computed once.
        lock (ReferenceCache)
        {
            if (!ReferenceCache.TryGetValue((vector, internalRate), out short[]? reference))
            {
                reference = IdealResampler.ToPcm(IdealResampler.Resample(mono, 48000, internalRate));
                ReferenceCache[(vector, internalRate)] = reference;
            }

            return reference;
        }
    }

    private static void Report(OpusBandwidth bandwidth, int ms, int bitrate, int complexity, double ours, double libopus, string detail)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "kiln-silk-encoder-quality.txt");
        double gap = ours - libopus;
        File.AppendAllText(
            path,
            $"SILK {bandwidth} {ms}ms {bitrate} c{complexity}: ours {ours:F2} dB, libopus {libopus:F2} dB, "
            + $"gap {gap:F2} dB | {detail}\n");
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
