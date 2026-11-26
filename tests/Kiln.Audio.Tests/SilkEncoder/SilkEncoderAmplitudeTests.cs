using FluentAssertions;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Amplitude regression: the decoded signal must keep the level of the input. A scale error in
/// the analysis, prefilter or quantizer chain shows up here long before it shows up in G2.
/// </summary>
/// <remarks>
/// This test currently fails: the encoder produces a bitstream that libopus decodes about 2x
/// (6 dB) louder than the input, so the G2 quality gate is not met. It is kept red on purpose
/// so the defect cannot regress silently.
/// </remarks>
public class SilkEncoderAmplitudeTests
{
    public static TheoryData<OpusBandwidth, int> Bands() => new()
    {
        { OpusBandwidth.Narrowband, 8000 },
        { OpusBandwidth.Mediumband, 12000 },
        { OpusBandwidth.Wideband, 16000 },
    };

    [Theory]
    [MemberData(nameof(Bands))]
    public void Decoded_rms_is_within_1_db_of_the_input(OpusBandwidth bandwidth, int internalRate)
    {
        const int sampleRate = 48000;
        var pcm = SpeechLike(sampleRate, seconds: 2);

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = sampleRate,
            Channels = 1,
            Bandwidth = bandwidth,
            BitrateBps = 16000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
        };

        using var encoder = new OpusEncoder(options);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = sampleRate, Channels = 1 });
        var packet = new byte[1276];
        var decoded = new short[pcm.Length];
        int offset = 0;
        for (; offset + 960 <= pcm.Length; offset += 960)
        {
            int written = encoder.Encode(pcm.AsSpan(offset, 960), packet);
            decoder.Decode(packet.AsSpan(0, written), decoded.AsSpan(offset, 960));
        }

        // Compare the steady-state part, past the codec delay and the first frames.
        int start = 9600;
        double inputRms = Rms(pcm.AsSpan(start, offset - start));
        double outputRms = Rms(decoded.AsSpan(start, offset - start));
        double deltaDb = 20 * Math.Log10(outputRms / inputRms);

        deltaDb.Should().BeInRange(
            -1.0, 1.0,
            $"{bandwidth} (internal {internalRate} Hz): input RMS {inputRms:F0}, decoded RMS {outputRms:F0}");
    }

    private static short[] SpeechLike(int sampleRate, int seconds)
    {
        var pcm = new short[sampleRate * seconds];
        var random = new Random(1234);
        double phase = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            double f0 = 140 + (30 * Math.Sin(2 * Math.PI * i / (sampleRate * 0.7)));
            phase += 2 * Math.PI * f0 / sampleRate;
            double value = 6000 * Math.Sin(phase)
                + 2500 * Math.Sin(2 * phase)
                + 1200 * Math.Sin(3 * phase)
                + 400 * (random.NextDouble() - 0.5);
            double envelope = 0.6 + (0.4 * Math.Sin(2 * Math.PI * i / (sampleRate * 0.35)));
            pcm[i] = (short)Math.Clamp(value * envelope, short.MinValue, short.MaxValue);
        }

        return pcm;
    }

    private static double Rms(ReadOnlySpan<short> values)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += (double)values[i] * values[i];
        }

        return Math.Sqrt(sum / values.Length);
    }
}
