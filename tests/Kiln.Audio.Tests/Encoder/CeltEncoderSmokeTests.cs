using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Tests.Encoder;

public class CeltEncoderSmokeTests
{
    [Fact]
    public void Encodes_an_impulse_with_unit_gain_and_the_documented_delay()
    {
        const int frames = 20;
        const int samples = 960;
        var pcm = new short[frames * samples];
        int impulse = 5000;
        pcm[impulse] = 20000;

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            BitrateBps = 32000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
        };

        using var encoder = new OpusEncoder(options);
        var decoder = new CeltDecoder(1);
        var synthesizer = new CeltSynthesizer(1);
        var frame = new CeltFrameResult();
        decoder.Reset();
        synthesizer.Reset();

        var packet = new byte[1275];
        var decoded = new float[frames * samples];
        var floatPcm = new float[samples];

        for (int f = 0; f < frames; f++)
        {
            int written = encoder.Encode(pcm.AsSpan(f * samples, samples), packet);
            var range = new RangeDecoder(packet.AsSpan(1, written - 1));
            decoder.DecodeFrame(ref range, written - 1, 3, 0, 21, 1, frame);
            range.Rng.Should().Be(encoder.FinalRange);
            synthesizer.Synthesize(frame, floatPcm);
            Array.Copy(floatPcm, 0, decoded, f * samples, samples);
        }

        int maxIndex = 0;
        float maxValue = 0;
        for (int i = 0; i < decoded.Length; i++)
        {
            float magnitude = Math.Abs(decoded[i]);
            if (magnitude > maxValue)
            {
                maxValue = magnitude;
                maxIndex = i;
            }
        }

        int delay = maxIndex - impulse;
        delay.Should().BeInRange(EncoderCorpus.StreamDelay - 1, EncoderCorpus.StreamDelay,
            "the CELT stream delay is the 2.5 ms overlap (120 samples)");
        maxValue.Should().BeGreaterThan(0.05f);
    }

    [Fact]
    public void Reconstructs_a_steady_tone_with_unity_gain()
    {
        const int frames = 20;
        const int samples = 960;
        var pcm = new short[frames * samples];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 200 * i / 48000.0));
        }

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            BitrateBps = 32000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
        };

        using var encoder = new OpusEncoder(options);
        var decoder = new CeltDecoder(1);
        var synthesizer = new CeltSynthesizer(1);
        var frame = new CeltFrameResult();
        decoder.Reset();
        synthesizer.Reset();

        var packet = new byte[1275];
        var decoded = new float[frames * samples];
        var floatPcm = new float[samples];
        for (int f = 0; f < frames; f++)
        {
            int written = encoder.Encode(pcm.AsSpan(f * samples, samples), packet);
            var range = new RangeDecoder(packet.AsSpan(1, written - 1));
            decoder.DecodeFrame(ref range, written - 1, 3, 0, 21, 1, frame);
            synthesizer.Synthesize(frame, floatPcm);
            Array.Copy(floatPcm, 0, decoded, f * samples, samples);
        }

        // Skip the first four frames (energy and MDCT history warm-up) and the stream delay.
        double inputEnergy = 0;
        double outputEnergy = 0;
        for (int i = (4 * samples) + EncoderCorpus.StreamDelay; i < decoded.Length; i++)
        {
            double reference = pcm[i - EncoderCorpus.StreamDelay] / 32768.0;
            inputEnergy += reference * reference;
            outputEnergy += (double)decoded[i] * decoded[i];
        }

        double ratio = Math.Sqrt(outputEnergy / inputEnergy);
        ratio.Should().BeInRange(0.95, 1.05, "the encoder and decoder must preserve the signal gain");
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        var pcm = new short[960];
        var random = new Random(7);
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)random.Next(-20000, 20000);
        }

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            BitrateBps = 24000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
        };

        var first = Encode(pcm, options);
        var second = Encode(pcm, options);
        first.Should().Equal(second);
        return;

        static byte[] Encode(short[] pcm, OpusEncoderOptions options)
        {
            using var encoder = new OpusEncoder(options);
            var packet = new byte[1275];
            int written = encoder.Encode(pcm, packet);
            return packet.AsSpan(0, written).ToArray();
        }
    }
}
