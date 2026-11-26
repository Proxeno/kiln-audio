using System.IO;
using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Decoder;

/// <summary>
/// Behaviour of the frozen public <see cref="OpusDecoder"/> surface: packet validation, the two
/// PCM overloads, final range, disposal, and the minimal packet-loss concealment contract.
/// </summary>
/// <remarks>
/// These tests use the committed <c>.bit</c> packets only, so they need no oracle build.
/// </remarks>
public class OpusDecoderApiTests
{
    [Fact]
    public void Malformed_packet_throws_InvalidDataException()
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[4800];

        // A code 3 packet with no frame-count byte violates RFC 6716 §3.2.1.
        Action decode = () => decoder.Decode([0x03], pcm);
        decode.Should().Throw<InvalidDataException>();

        // The decoder remains usable after a rejected packet.
        byte[] packet = FirstPacket(12);
        int samples = decoder.Decode(packet, pcm);
        samples.Should().Be(960);
    }

    [Fact]
    public void Too_short_destination_throws_before_decoding()
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        byte[] packet = FirstPacket(12);

        Action decode = () => decoder.Decode(packet, new short[16]);
        decode.Should().Throw<ArgumentException>();

        // No state was consumed, so the packet still decodes normally.
        int samples = decoder.Decode(packet, new short[960]);
        samples.Should().Be(960);
    }

    [Fact]
    public void In_band_FEC_decode_returns_the_frame_duration()
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        byte[] packet = FirstPacket(12);

        var pcm = new short[960];
        int samples = decoder.Decode(packet, pcm, decodeFec: true);

        samples.Should().Be(960, "the output has the frame size of the packet that carries the FEC");
    }

    [Fact]
    public void Final_range_matches_the_encoder_for_each_packet()
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[5760];
        foreach ((byte[] packet, uint expectedRange) in OpusBitFile.Read(TestVectors.BitPath(12)))
        {
            decoder.Decode(packet, pcm);
            decoder.FinalRange.Should().Be(expectedRange);
        }
    }

    [Fact]
    public void Float_overload_matches_the_16_bit_output()
    {
        byte[] packet = FirstPacket(12);
        var options = new OpusDecoderOptions { SampleRate = 48000, Channels = 1 };
        using var shortDecoder = new OpusDecoder(options);
        using var floatDecoder = new OpusDecoder(options);

        var pcm16 = new short[960];
        var pcmFloat = new float[960];
        int shortSamples = shortDecoder.Decode(packet, pcm16);
        int floatSamples = floatDecoder.Decode(packet, pcmFloat);

        floatSamples.Should().Be(shortSamples);
        for (int i = 0; i < shortSamples; i++)
        {
            float scaled = Math.Max(-32768f, Math.Min(32767f, pcmFloat[i] * 32768f));
            short expected = (short)(int)Math.Floor(0.5f + scaled);
            pcm16[i].Should().Be(expected);
        }
    }

    [Fact]
    public void DecodeLoss_before_any_packet_returns_silence_of_2_5_ms()
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 2 });
        var pcm = new short[240];

        int samples = decoder.DecodeLoss(pcm);
        samples.Should().Be(120);
        pcm.Should().OnlyContain(sample => sample == 0);
        decoder.FinalRange.Should().Be(0u);
    }

    [Fact]
    public void DecodeLoss_after_a_packet_uses_the_last_frame_duration()
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[960];
        decoder.Decode(FirstPacket(12), pcm);

        pcm.AsSpan().Clear();
        int samples = decoder.DecodeLoss(pcm);
        samples.Should().Be(960, "the last packet was a 20 ms SILK frame");
        decoder.FinalRange.Should().Be(0u);
        pcm.Should().Contain(sample => sample != 0, "SILK concealment repeats the last frame with a decay");
    }

    [Fact]
    public void Empty_packet_is_concealed()
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[960];
        decoder.Decode(FirstPacket(12), pcm);

        int samples = decoder.Decode([], pcm);
        samples.Should().Be(960);
        decoder.FinalRange.Should().Be(0u);
    }

    [Fact]
    public void Disposed_decoder_throws()
    {
        var decoder = new OpusDecoder(new OpusDecoderOptions());
        decoder.Dispose();

        ((Action)(() => decoder.Decode([0x00], new short[960]))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => decoder.DecodeLoss(new short[960]))).Should().Throw<ObjectDisposedException>();
    }

    private static byte[] FirstPacket(int vector)
    {
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            return packet;
        }

        throw new InvalidOperationException(FormattableString.Invariant($"Vector {vector} is empty."));
    }
}
