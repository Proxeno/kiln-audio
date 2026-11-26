using FluentAssertions;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Checks the LBRR flag and frame layout against the existing decoder's LBRR parsing
/// (RFC 6716 §4.2.3, §4.2.4, §4.2.5) and the selection rules of Appendix A.
/// </summary>
public class Enc2LbrrEncoderTests
{
    /// <summary>The VAD bits, LBRR flags and per-frame masks round-trip through the decoder.</summary>
    [Theory]
    [InlineData(1, 20, 0b1, 0b0, 0b1, 0b0)]
    [InlineData(1, 20, 0b0, 0b0, 0b0, 0b0)]
    [InlineData(2, 20, 0b1, 0b1, 0b1, 0b1)]
    [InlineData(2, 20, 0b0, 0b1, 0b0, 0b0)]
    [InlineData(1, 40, 0b10, 0b0, 0b10, 0b0)]
    [InlineData(2, 40, 0b01, 0b10, 0b11, 0b01)]
    [InlineData(2, 60, 0b101, 0b010, 0b111, 0b000)]
    [InlineData(2, 60, 0b000, 0b000, 0b000, 0b000)]
    [InlineData(1, 60, 0b111, 0b000, 0b101, 0b000)]
    public void Lbrr_header_round_trips_through_the_decoder(
        int channels, int frameDurationMs, int midVadMask, int sideVadMask, int midLbrrMask, int sideLbrrMask)
    {
        int silkFrames = frameDurationMs == 20 ? 1 : frameDurationMs / 20;
        var buffer = new byte[256];
        var encoder = new RangeEncoder(buffer);
        SilkLbrrEncoder.EncodeHeader(
            ref encoder, channels, silkFrames, midVadMask, sideVadMask, midLbrrMask, sideLbrrMask);
        encoder.Done();
        encoder.Error.Should().BeFalse();

        var decoder = new RangeDecoder(buffer.AsSpan(0, encoder.RangeBytes));
        var silk = new SilkDecoder();
        var result = new SilkFrameResult();
        silk.DecodeFrame(ref decoder, 8000, channels, frameDurationMs, result, lbrrOnly: true);

        result.HasLbrr(0).Should().Be(midLbrrMask != 0);
        if (channels == 2)
        {
            result.HasLbrr(1).Should().Be(sideLbrrMask != 0);
        }

        for (int f = 0; f < silkFrames; f++)
        {
            result.VadFlag(f, 0).Should().Be(((midVadMask >> f) & 1) != 0);
            result.IsLbrrCoded(f, 0).Should().Be(((midLbrrMask >> f) & 1) != 0);
            if (channels == 2)
            {
                result.VadFlag(f, 1).Should().Be(((sideVadMask >> f) & 1) != 0);
                result.IsLbrrCoded(f, 1).Should().Be(((sideLbrrMask >> f) & 1) != 0);
            }
        }
    }

    /// <summary>LBRR frames are interleaved mid/side per interval and use conditional coding.</summary>
    [Fact]
    public void Lbrr_frames_are_interleaved_and_conditionally_coded()
    {
        var buffer = new byte[256];
        var encoder = new RangeEncoder(buffer);
        var writer = new RecordingWriter();
        byte[] predictionIndices = new byte[2 * 6];
        bool[] midOnly = [false, false];

        SilkLbrrEncoder.EncodeFrames(
            ref encoder, channels: 2, silkFrames: 2, midLbrrMask: 0b11, sideLbrrMask: 0b01,
            predictionIndices, midOnly, writer);

        writer.Frames.Should().Equal(
            (0, 0, false),
            (0, 1, false),
            (1, 0, true));
    }

    /// <summary>A side frame that is not redundantly coded leaves the mid-only flag in the stream.</summary>
    [Fact]
    public void Mid_only_flag_is_written_when_the_side_lbrr_frame_is_absent()
    {
        var buffer = new byte[256];
        var encoder = new RangeEncoder(buffer);
        var writer = new RecordingWriter();
        byte[] predictionIndices = new byte[6];

        SilkLbrrEncoder.EncodeFrames(
            ref encoder, channels: 2, silkFrames: 1, midLbrrMask: 0b1, sideLbrrMask: 0b0,
            predictionIndices, [true], writer);

        writer.Frames.Should().Equal((0, 0, false));
        encoder.Tell().Should().BeGreaterThan(0);
    }

    /// <summary>The enable threshold and gain increase follow Appendix A (silk_setup_LBRR).</summary>
    [Theory]
    [InlineData(8, 20000, 10, true, 4)]
    [InlineData(8, 13000, 10, false, 0)]
    [InlineData(12, 20000, 10, true, 4)]
    [InlineData(16, 16000, 25, true, 2)]
    [InlineData(16, 30000, 1, true, 7)]
    public void Setup_follows_the_rate_and_loss_rules(
        int fsKHz, int targetRateBps, int lossPercent, bool expectedEnabled, int expectedGainIncreases)
    {
        SilkLbrrEncoder.Setup(useInBandFec: true, lossPercent, fsKHz, targetRateBps,
            out bool enabled, out int gainIncreases);
        enabled.Should().Be(expectedEnabled);
        if (expectedEnabled)
        {
            gainIncreases.Should().Be(expectedGainIncreases);
        }
    }

    /// <summary>Without FEC or without loss, LBRR stays off.</summary>
    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 0)]
    public void Setup_is_disabled_without_fec_or_loss(bool useInBandFec, int lossPercent)
    {
        SilkLbrrEncoder.Setup(useInBandFec, lossPercent, 16, 64000, out bool enabled, out int gainIncreases);
        enabled.Should().BeFalse();
        gainIncreases.Should().Be(0);
    }

    /// <summary>The LBRR first-subframe gain is raised and clamped.</summary>
    [Theory]
    [InlineData(10, 4, 14)]
    [InlineData(62, 4, 63)]
    [InlineData(0, 2, 2)]
    public void First_gain_index_is_raised_and_clamped(int regular, int increases, int expected)
    {
        SilkLbrrEncoder.RaiseFirstGainIndex(regular, increases).Should().Be(expected);
    }

    /// <summary>Only active frames are redundantly coded (Appendix A threshold 0.3).</summary>
    [Theory]
    [InlineData(77, false)]
    [InlineData(78, true)]
    [InlineData(255, true)]
    public void Only_active_frames_are_coded(int speechActivityQ8, bool expected)
    {
        SilkLbrrEncoder.ShouldCodeFrame(enabled: true, speechActivityQ8).Should().Be(expected);
        SilkLbrrEncoder.ShouldCodeFrame(enabled: false, speechActivityQ8).Should().BeFalse();
    }

    private sealed class RecordingWriter : ISilkLbrrFrameWriter
    {
        public List<(int Frame, int Channel, bool Conditional)> Frames { get; } = [];

        public void WriteLbrrFrame(ref RangeEncoder enc, int frameIndex, int channel, bool conditionalCoding)
        {
            Frames.Add((frameIndex, channel, conditionalCoding));
            enc.EncodeBits(0, 4);
        }
    }
}
