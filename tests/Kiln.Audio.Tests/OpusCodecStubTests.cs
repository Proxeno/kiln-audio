using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests;

public class OpusCodecStubTests
{
    [Fact]
    public void Encode_throws_NotImplemented()
    {
        using var enc = new OpusEncoder(new OpusEncoderOptions());
        var pcm = new short[960];
        var packet = new byte[1275];
        try
        {
            enc.Encode(pcm, packet);
            Assert.Fail("expected NotImplementedException");
        }
        catch (NotImplementedException ex)
        {
            ex.Message.Should().Contain("EWI-2743");
        }
    }

    [Fact]
    public void Decode_and_DecodeLoss_throw_NotImplemented()
    {
        using var dec = new OpusDecoder(new OpusDecoderOptions());
        var pcm = new short[960];
        try
        {
            dec.Decode([0xF8, 0xFF, 0xFE], pcm);
            Assert.Fail("expected NotImplementedException");
        }
        catch (NotImplementedException ex)
        {
            ex.Message.Should().Contain("EWI-2742");
        }

        try
        {
            dec.DecodeLoss(pcm);
            Assert.Fail("expected NotImplementedException");
        }
        catch (NotImplementedException)
        {
        }
    }
}
