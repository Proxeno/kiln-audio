using System.Globalization;
using FluentAssertions;
using Kiln.Audio.Silk.Encoder;
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
            short[] reference = Downsample(mono, internalRate);
            double ours = MeasureOurs(mono, bandwidth, duration, bitrate, complexity, internalRate, reference, out double ourQuality);
            double theirs = MeasureLibopus(mono, bandwidth, ms, bitrate, complexity, internalRate, reference, out double theirQuality);

            long count = reference.Length / (internalRate / 50);
            oursTotal += ours * count;
            libopusTotal += theirs * count;
            segments += count;
            lines.Add($"v{vector}: ours {ours:F2} libopus {theirs:F2} (q {ourQuality:F1}% / {theirQuality:F1}%)");
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
        int delay = FindDelay(reference, decoded, segment);
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

        quality = 0;
        return count == 0 ? -10 : total / count;
    }

    private static int FindDelay(short[] reference, short[] decoded, int segment)
    {
        const int maximum = 400;
        int best = 0;
        double bestCorrelation = double.NegativeInfinity;
        for (int delay = -maximum; delay <= maximum; delay += 4)
        {
            double correlation = 0;
            int limit = Math.Min(reference.Length, decoded.Length);
            for (int i = maximum; i + maximum < limit; i += 4)
            {
                int j = i + delay;
                if (j < 0 || j >= decoded.Length)
                {
                    continue;
                }

                correlation += (double)reference[i] * decoded[j];
            }

            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                best = delay;
            }
        }

        return best;
    }

    private static short[] Downsample(short[] mono, int internalRate)
    {
        var resampler = new SilkInputResampler(48000, internalRate);
        var output = new short[internalRate / 50];
        var result = new List<short>();
        for (int offset = 0; offset + 960 <= mono.Length; offset += 960)
        {
            resampler.Process(mono.AsSpan(offset, 960), output);
            result.AddRange(output);
        }

        return result.ToArray();
    }

    private static void Report(OpusBandwidth bandwidth, int ms, int bitrate, int complexity, double ours, double libopus, string detail)
    {
        string path = Path.Combine(Path.GetTempPath(), "kiln-silk-encoder-quality.txt");
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
