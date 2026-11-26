using FluentAssertions;

namespace Kiln.Audio.Tests.Robustness;

/// <summary>
/// Regression coverage for SILK concealment when the lost frame is shorter than the last good
/// frame. The frame geometry must follow the lost duration (RFC 6716 Appendix A,
/// <c>silk/dec_API.c: silk_Decode()</c>), not the previous frame's.
/// </summary>
public class SilkPlcDurationTests
{
    [Fact]
    public void A_5ms_loss_after_a_20ms_SILK_frame_conceals_the_lost_duration()
    {
        // 24 kHz mono output. The first packet is a 20 ms stereo SILK narrowband frame (TOC 0x0C);
        // the second is a 5 ms CELT TOC whose frame is empty, so it is concealed in SILK mode.
        byte[] longFrame = Convert.FromHexString("0c08dbc37377d2478d4ca488b484007e4951f30b");
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 24000, Channels = 1 });
        var pcm = new short[5760];

        decoder.Decode(longFrame, pcm).Should().Be(480, "a 20 ms frame at 24 kHz is 480 samples");
        int samples = decoder.Decode([0xCC], pcm);

        samples.Should().Be(120, "the concealed output has the 5 ms frame size of the packet that was lost");
    }
}
