using System.IO;
using FluentAssertions;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Robustness;

/// <summary>
/// Asserts the documented exception contract at every API sample rate and channel count: a
/// framing violation is <see cref="InvalidDataException"/>, a short destination is
/// <see cref="ArgumentException"/>, and neither consumes decoder state.
/// </summary>
public class DecoderExceptionContractTests
{
    public static TheoryData<int, int> Configurations()
    {
        var data = new TheoryData<int, int>();
        foreach (int rate in (int[])[8000, 12000, 16000, 24000, 48000])
        {
            data.Add(rate, 1);
            data.Add(rate, 2);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void Malformed_packets_throw_only_InvalidDataException(int sampleRate, int channels)
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = sampleRate, Channels = channels });
        var pcm = new short[sampleRate * 120 / 1000 * channels];
        byte[] valid = FirstPacket();

        foreach (byte[] malformed in MalformedPackets())
        {
            Action decode = () => decoder.Decode(malformed, pcm);
            decode.Should().Throw<InvalidDataException>();

            // A rejected packet must not consume state: the next valid packet still decodes.
            decoder.Decode(valid, pcm).Should().Be(960 * sampleRate / 48000);
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void A_short_destination_throws_ArgumentException_before_decoding(int sampleRate, int channels)
    {
        byte[] packet = FirstPacket();
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = sampleRate, Channels = channels });

        Span<Range> frames = stackalloc Range[OpusPacket.MaxFrames];
        OpusPacket.TryParse(packet, frames, out int frameCount, out OpusToc toc, out _).Should().Be(OpusStatus.Ok);
        int required = frameCount * OpusPacket.GetSamplesPerFrame(toc, sampleRate) * channels;
        var tooShort = new short[required - 1];

        Action decode = () => decoder.Decode(packet, tooShort);
        decode.Should().Throw<ArgumentException>();

        // Nothing was consumed, so a destination of exactly the documented size works.
        decoder.Decode(packet, new short[required]).Should().Be(required / channels);
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void DecodeLoss_before_any_packet_returns_2_5_ms_of_silence(int sampleRate, int channels)
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = sampleRate, Channels = channels });
        var pcm = new short[sampleRate * 60 / 1000 * channels];

        int samples = decoder.DecodeLoss(pcm);
        samples.Should().Be(sampleRate / 400);
        decoder.FinalRange.Should().Be(0u);
    }

    private static byte[] FirstPacket()
    {
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(12)))
        {
            return packet;
        }

        throw new InvalidOperationException("Vector 12 is empty.");
    }

    private static byte[][] MalformedPackets()
    {
        byte[] oversized = new byte[1 + OpusPacket.MaxFrameBytes + 1];
        return
        [
            [0x03],             // code 3 without a frame-count byte
            [0x03, 0x00],       // zero frames
            [0x03, 0x31],       // 49 frames of 2.5 ms exceed 120 ms
            [0x01, 0xAA],       // code 1 with an odd payload
            [0x02],             // code 2 without its length byte
            [0x03, 0x82, 0xFC], // code 3 VBR with a truncated two-byte length
            oversized,          // a 1276-byte frame
        ];
    }
}
