using FluentAssertions;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Amplitude regression: the decoded signal must keep the level of the input. A scale error in
/// the analysis, prefilter or quantizer chain shows up here long before it shows up in G2.
/// </summary>
/// <remarks>
/// Covers 20 ms and 60 ms packets (one and three SILK frames, so conditional gain coding) in
/// each SILK bandwidth, at complexity 0 (single-state quantizer) and 5.
/// </remarks>
public class SilkEncoderAmplitudeTests
{
    public static TheoryData<OpusBandwidth, int, OpusFrameDuration, int> Bands()
    {
        var data = new TheoryData<OpusBandwidth, int, OpusFrameDuration, int>();
        foreach (var duration in new[] { OpusFrameDuration.Ms20, OpusFrameDuration.Ms60 })
        {
            foreach (int complexity in new[] { 0, 5 })
            {
                data.Add(OpusBandwidth.Narrowband, 8000, duration, complexity);
                data.Add(OpusBandwidth.Mediumband, 12000, duration, complexity);
                data.Add(OpusBandwidth.Wideband, 16000, duration, complexity);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Bands))]
    public void Decoded_rms_is_within_1_db_of_the_input(
        OpusBandwidth bandwidth, int internalRate, OpusFrameDuration duration, int complexity)
    {
        const int sampleRate = 48000;
        int frameSamples = duration == OpusFrameDuration.Ms20 ? 960 : 2880;
        var pcm = SpeechLike(sampleRate, seconds: 2);

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = sampleRate,
            Channels = 1,
            Bandwidth = bandwidth,
            BitrateBps = 16000,
            FrameDuration = duration,
            Complexity = complexity,
        };

        using var encoder = new OpusEncoder(options);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = sampleRate, Channels = 1 });
        var packet = new byte[1276];
        var decoded = new short[pcm.Length];
        int offset = 0;
        for (; offset + frameSamples <= pcm.Length; offset += frameSamples)
        {
            int written = encoder.Encode(pcm.AsSpan(offset, frameSamples), packet);
            decoder.Decode(packet.AsSpan(0, written), decoded.AsSpan(offset, frameSamples));
        }

        // Compare the steady-state part, past the codec delay and the first frames.
        int start = 9600;
        double inputRms = Rms(pcm.AsSpan(start, offset - start));
        double outputRms = Rms(decoded.AsSpan(start, offset - start));
        double deltaDb = 20 * Math.Log10(outputRms / inputRms);

        deltaDb.Should().BeInRange(
            -1.0, 1.0,
            $"{bandwidth} (internal {internalRate} Hz), {duration}, complexity {complexity}: input RMS {inputRms:F0}, decoded RMS {outputRms:F0}");
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
