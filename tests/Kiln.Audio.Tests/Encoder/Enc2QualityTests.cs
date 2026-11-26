using System.Globalization;
using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>
/// G2 for the extended encoder: aggregate segmental SNR against libopus at CBR 20 ms. The hard
/// gate is asserted at complexity 0; complexity 5 and 10 runs are recorded so the gap to the
/// reference can be reported.
/// </summary>
public class Enc2QualityTests
{
    private static readonly Lazy<IReadOnlyList<short[]>> StereoCorpus = new(LoadStereoCorpus);

    public static TheoryData<int, int, int> Cases()
    {
        var data = new TheoryData<int, int, int>();
        foreach ((int channels, int bitrate) in new[]
                 {
                     (1, 24000), (1, 32000), (2, 32000), (2, 64000),
                 })
        {
            foreach (int complexity in new[] { 0, 5, 10 })
            {
                data.Add(channels, bitrate, complexity);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Corpus_quality(int channels, int bitrate, int complexity)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        double oursTotal = 0;
        double libopusTotal = 0;
        long segments = 0;
        int vectors = 0;
        foreach (short[] stereo in StereoCorpus.Value)
        {
            short[] reference = channels == 2 ? stereo : Downmix(stereo);
            (double ours, long count) = MeasureOurs(reference, channels, bitrate, complexity);
            (double theirs, _) = MeasureLibopus(reference, channels, bitrate, complexity);
            oursTotal += ours * count;
            libopusTotal += theirs * count;
            segments += count;
            vectors++;
        }

        double oursMean = oursTotal / segments;
        double libopusMean = libopusTotal / segments;
        SaveReport(channels, bitrate, complexity, oursMean, libopusMean, vectors);

        if (complexity == 0)
        {
            oursMean.Should().BeGreaterThanOrEqualTo(
                libopusMean,
                $"G2 requires ours >= libopus at complexity 0 for {channels}ch at {bitrate} bps " +
                $"(ours {oursMean:F3} dB, libopus {libopusMean:F3} dB)");
        }
    }

    private static (double Mean, long Segments) MeasureOurs(
        short[] reference, int channels, int bitrate, int complexity)
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            SampleRate = 48000,
            Channels = channels,
            BitrateBps = bitrate,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = complexity,
        };

        int frameSamples = 960 * channels;
        using var encoder = new OpusEncoder(options);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = channels });
        var packet = new byte[1276];
        var decoded = new short[reference.Length];
        var frame = new short[960 * channels];
        int offset = 0;
        while (offset + frameSamples <= reference.Length)
        {
            int written = encoder.Encode(reference.AsSpan(offset, frameSamples), packet);
            int count = decoder.Decode(packet.AsSpan(0, written), frame);
            Array.Copy(frame, 0, decoded, offset, count * channels);
            offset += frameSamples;
        }

        return SegmentalSnr(reference, decoded, channels);
    }

    private static (double Mean, long Segments) MeasureLibopus(
        short[] reference, int channels, int bitrate, int complexity)
    {
        byte[] bit = EncoderOracle.EncodeRestrictedLowDelay(reference, channels, bitrate, 20, complexity);
        (short[] decoded, string diagnostics) = EncoderOracle.Decode(bit, channels);
        EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(diagnostics);
        short[] comparable = decoded.Length > reference.Length
            ? decoded.AsSpan(0, reference.Length).ToArray()
            : decoded;
        return SegmentalSnr(reference, comparable, channels);
    }

    /// <summary>Mean segmental SNR over both channels, in 20 ms segments clamped to [-10, 60] dB.</summary>
    private static (double Mean, long Segments) SegmentalSnr(short[] reference, short[] test, int channels)
    {
        const int segment = 960;
        int delay = EncoderCorpus.StreamDelay;
        double total = 0;
        long segments = 0;
        int perChannel = Math.Min(reference.Length, test.Length) / channels;
        for (int start = 0; start + segment <= perChannel - delay; start += segment)
        {
            double signal = 0;
            double noise = 0;
            for (int i = 0; i < segment; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    double s = reference[((start + i) * channels) + c];
                    double d = test[(((start + i) + delay) * channels) + c] - s;
                    signal += s * s;
                    noise += d * d;
                }
            }

            double snr;
            if (signal <= 0)
            {
                snr = noise <= 0 ? 60 : -10;
            }
            else if (noise <= 0)
            {
                snr = 60;
            }
            else
            {
                snr = 10 * Math.Log10(signal / noise);
            }

            total += Math.Clamp(snr, -10, 60);
            segments++;
        }

        return (total / segments, segments);
    }

    private static IReadOnlyList<short[]> LoadStereoCorpus()
    {
        var corpus = new List<short[]>(TestVectors.Count);
        for (int vector = 1; vector <= TestVectors.Count; vector++)
        {
            corpus.Add(Pcm16.Read(TestVectors.DecPath(vector)));
        }

        string? limit = Environment.GetEnvironmentVariable("KILN_ENCODER_CORPUS_LIMIT");
        if (limit is not null
            && int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            && count > 0)
        {
            return corpus.Take(Math.Min(count, corpus.Count)).ToArray();
        }

        return corpus;
    }

    private static short[] Downmix(short[] stereo)
    {
        var mono = new short[stereo.Length / 2];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)(((int)stereo[2 * i] + stereo[(2 * i) + 1]) / 2);
        }

        return mono;
    }

    private static void SaveReport(
        int channels, int bitrate, int complexity, double ours, double libopus, int vectors)
    {
        string path = Path.Combine(Path.GetTempPath(), "kiln-encoder-quality.txt");
        string line =
            $"ENC2 {channels}ch {bitrate} c{complexity}: ours {ours:F3} dB, libopus {libopus:F3} dB, " +
            $"gap {ours - libopus:+0.000;-0.000} dB, vectors {vectors}\n";
        File.AppendAllText(path, line);
    }
}