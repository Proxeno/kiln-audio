using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests;

public class OpusCodecStubTests
{
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
