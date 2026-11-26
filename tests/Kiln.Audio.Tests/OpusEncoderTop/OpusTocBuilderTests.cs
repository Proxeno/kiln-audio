using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Encoder;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>The encoder TOC builder is the exact inverse of the parser for every Table 2 row.</summary>
public class OpusTocBuilderTests
{
    [Fact]
    public void Every_configuration_round_trips()
    {
        for (int config = 0; config < 32; config++)
        {
            byte toc = (byte)(config << 3);
            OpusToc parsed = OpusToc.Parse(toc);

            OpusTocBuilder.Build(parsed.Mode, parsed.Bandwidth, parsed.FrameDuration, parsed.IsStereo)
                .Should().Be(toc, $"config {config} must build back to its own TOC byte");
            OpusTocBuilder.ConfigOf(parsed.Mode, parsed.Bandwidth, parsed.FrameDuration).Should().Be(config);
        }
    }

    [Fact]
    public void The_stereo_flag_sets_bit_two()
    {
        byte mono = OpusTocBuilder.Build(OpusMode.Celt, OpusBandwidth.Fullband, OpusFrameDuration.Ms20, stereo: false);
        byte stereo = OpusTocBuilder.Build(OpusMode.Celt, OpusBandwidth.Fullband, OpusFrameDuration.Ms20, stereo: true);

        mono.Should().Be(31 << 3);
        stereo.Should().Be((31 << 3) | 0x04);
    }

    [Fact]
    public void The_frame_count_code_stays_zero()
    {
        byte toc = OpusTocBuilder.Build(
            OpusMode.Silk, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, stereo: false);

        (toc & 0x03).Should().Be(0);
    }

    [Theory]
    [InlineData((int)OpusMode.Silk, (int)OpusBandwidth.Narrowband, (int)OpusFrameDuration.Ms2_5)]
    [InlineData((int)OpusMode.Silk, (int)OpusBandwidth.Fullband, (int)OpusFrameDuration.Ms20)]
    [InlineData((int)OpusMode.Hybrid, (int)OpusBandwidth.Wideband, (int)OpusFrameDuration.Ms20)]
    [InlineData((int)OpusMode.Hybrid, (int)OpusBandwidth.Fullband, (int)OpusFrameDuration.Ms40)]
    [InlineData((int)OpusMode.Celt, (int)OpusBandwidth.Fullband, (int)OpusFrameDuration.Ms40)]
    public void Illegal_mode_bandwidth_duration_combinations_are_rejected(
        int mode, int bandwidth, int duration)
    {
        Action act = () => OpusTocBuilder.ConfigOf(
            (OpusMode)mode, (OpusBandwidth)bandwidth, (OpusFrameDuration)duration);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
