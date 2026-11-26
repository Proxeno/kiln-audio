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

}
