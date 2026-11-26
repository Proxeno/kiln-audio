using FluentAssertions;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Checks the SILK stereo mixer and prediction quantizer (RFC 6716 §5.2.2) against the existing
/// decoder's dequantization (RFC 6716 §4.2.7.1 and §4.2.7.2).
/// </summary>
public class Enc2StereoEncoderTests
{
    /// <summary>Every quantizer level decodes back to exactly the weight the quantizer chose.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-13732, 13732)]
    [InlineData(5000, -5000)]
    [InlineData(-6500, 820)]
    [InlineData(13107, 13107)]
    [InlineData(-16384, 16384)]
    [InlineData(1, -1)]
    [InlineData(12345, 6789)]
    public void Quantized_weights_decode_back_through_the_decoder(int pred0Q13, int pred1Q13)
    {
        Span<byte> indices = stackalloc byte[6];
        SilkStereoEncoder.QuantizePredictors(ref pred0Q13, ref pred1Q13, indices);

        (int decoded0, int decoded1, bool decodedMidOnly) = DecodeWeights(indices, midOnly: false);

        decoded0.Should().Be(pred0Q13);
        decoded1.Should().Be(pred1Q13);
        decodedMidOnly.Should().BeFalse();
    }

    /// <summary>The quantizer searches the whole table, not just the first interval.</summary>
    [Fact]
    public void Every_quantizer_interval_survives_the_round_trip()
    {
        Span<byte> indices = stackalloc byte[6];
        for (int step = -16384; step <= 16384; step += 97)
        {
            int pred0 = step;
            int pred1 = -step;
            SilkStereoEncoder.QuantizePredictors(ref pred0, ref pred1, indices);

            (int decoded0, int decoded1, _) = DecodeWeights(indices, midOnly: false);
            decoded0.Should().Be(pred0);
            decoded1.Should().Be(pred1);
        }
    }

    /// <summary>The mid-only flag decodes correctly (RFC 6716 §4.2.7.2, Table 8).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mid_only_flag_round_trips(bool midOnly)
    {
        Span<byte> indices = stackalloc byte[6];
        int pred0 = 4000;
        int pred1 = -2000;
        SilkStereoEncoder.QuantizePredictors(ref pred0, ref pred1, indices);

        (_, _, bool decoded) = DecodeWeights(indices, midOnly);
        decoded.Should().Be(midOnly);
    }

    /// <summary>
    /// A stereo frame that is almost amplitude-panned ends up coded as mid only once the shape
    /// look-ahead has been transmitted (RFC 6716 §5.2.2).
    /// </summary>
    [Fact]
    public void Near_panned_signal_switches_to_mid_only_after_the_taper()
    {
        const int FsKHz = 8;
        const int FrameLength = FsKHz * 20;
        var encoder = new SilkStereoEncoder();
        short[] left = new short[FrameLength];
        short[] right = new short[FrameLength];
        short[] mid = new short[FrameLength];
        short[] side = new short[FrameLength];
        byte[] indices = new byte[6];
        var random = new Random(1234);
        for (int i = 0; i < FrameLength; i++)
        {
            short sample = (short)((random.NextDouble() - 0.5) * 8000);
            left[i] = sample;
            right[i] = sample;
        }

        bool sawMidOnly = false;
        for (int frame = 0; frame < 40; frame++)
        {
            encoder.Analyze(
                left, right, FsKHz, 20000, 200, toMono: false,
                mid, side, indices, out bool midOnly, out _, out int sideRateBps);
            sawMidOnly |= midOnly;
            if (midOnly)
            {
                sideRateBps.Should().Be(0);
                break;
            }
        }

        sawMidOnly.Should().BeTrue("an amplitude-panned input should collapse to mid-only coding");
    }

    /// <summary>The mixer produces mid/side signals that track the input and its history.</summary>
    [Fact]
    public void Mixer_produces_the_reference_mid_and_one_sample_delayed_side()
    {
        const int FsKHz = 8;
        const int FrameLength = FsKHz * 20;
        var encoder = new SilkStereoEncoder();
        short[] left = new short[FrameLength];
        short[] right = new short[FrameLength];
        short[] mid = new short[FrameLength];
        short[] side = new short[FrameLength];
        byte[] indices = new byte[6];
        for (int i = 0; i < FrameLength; i++)
        {
            left[i] = (short)((i * 37) % 2001 - 1000);
            right[i] = (short)((i * 19) % 1501 - 750);
        }

        encoder.Analyze(
            left, right, FsKHz, 24000, 256, toMono: false,
            mid, side, indices, out _, out int midRateBps, out int sideRateBps);

        midRateBps.Should().BePositive();
        sideRateBps.Should().BePositive();
        for (int i = 0; i < FrameLength; i++)
        {
            mid[i].Should().Be((short)((left[i] + right[i] + 1) >> 1));
        }

        // The reference writes the predicted side one sample late and leaves the last slot as
        // the right input sample.
        side[FrameLength - 1].Should().Be(right[FrameLength - 1]);
    }

    /// <summary>The mixer and quantizer allocate nothing after construction.</summary>
    [Fact]
    public void Analyze_allocates_nothing()
    {
        const int FsKHz = 16;
        const int FrameLength = FsKHz * 20;
        var encoder = new SilkStereoEncoder();
        short[] left = new short[FrameLength];
        short[] right = new short[FrameLength];
        short[] mid = new short[FrameLength];
        short[] side = new short[FrameLength];
        byte[] indices = new byte[6];
        var random = new Random(42);
        for (int i = 0; i < FrameLength; i++)
        {
            left[i] = (short)random.Next(-8000, 8000);
            right[i] = (short)random.Next(-8000, 8000);
        }

        for (int i = 0; i < 10; i++)
        {
            encoder.Analyze(left, right, FsKHz, 32000, 200, false, mid, side, indices, out _, out _, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            encoder.Analyze(left, right, FsKHz, 32000, 200, false, mid, side, indices, out _, out _, out _);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    private static (int Weight0, int Weight1, bool MidOnly) DecodeWeights(ReadOnlySpan<byte> indices, bool midOnly)
    {
        var buffer = new byte[256];
        var encoder = new RangeEncoder(buffer);

        // 10 ms stereo header: mid VAD, mid LBRR flag, side VAD, side LBRR flag.
        SilkLbrrEncoder.EncodeHeader(
            ref encoder, channels: 2, silkFrames: 1, midVadMask: 0, sideVadMask: 0,
            midLbrrMask: 0, sideLbrrMask: 0);
        SilkStereoEncoder.EncodePrediction(ref encoder, indices);
        SilkStereoEncoder.EncodeMidOnly(ref encoder, midOnly);
        encoder.Done();
        encoder.Error.Should().BeFalse();

        var decoder = new RangeDecoder(buffer.AsSpan(0, encoder.RangeBytes));
        var silk = new SilkDecoder();
        var result = new SilkFrameResult();
        silk.DecodeFrame(ref decoder, 8000, 2, 10, result);

        SilkChannelFrame midFrame = result.Regular(0, 0);
        return (midFrame.StereoWeight0Q13, midFrame.StereoWeight1Q13, midFrame.IsMidOnly);
    }
}
