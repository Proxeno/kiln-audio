using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Celt;
using Kiln.Audio.Celt.Encoder;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>
/// State and band-range support added for HYBRID-ENC-1: <see cref="CeltEncoder.Reset"/> and the
/// internal per-call start/end band. Every case decodes with the matching band range and the
/// decoder's final range equals the encoder's.
/// </summary>
public class Enc2StateTests
{
    public static TheoryData<int, int, int> BandRanges()
    {
        var data = new TheoryData<int, int, int>();
        foreach (int channels in new[] { 1, 2 })
        {
            foreach ((int start, int end) in new[] { (0, 13), (0, 17), (0, 19), (0, 21), (17, 21) })
            {
                data.Add(channels, start, end);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BandRanges))]
    public void Band_ranges_decode_with_matching_range(int channels, int start, int end)
    {
        short[] pcm = BuildSignal(channels, frames: 5);
        var encoder = Create(channels);
        var payload = new byte[encoder.FrameBytes];
        int frameSamples = 0;
        uint finalRange = 0;
        for (int frame = 0; frame < 5; frame++)
        {
            int interleaved = 960 * channels;
            frameSamples = encoder.EncodeFrame(
                pcm.AsSpan(frame * interleaved, interleaved), payload, start, end);
            finalRange = encoder.FinalRange;
        }

        var decoder = new CeltDecoder(channels);
        var range = new RangeDecoder(payload.AsSpan(0, frameSamples));
        var result = new CeltFrameResult();
        decoder.DecodeFrame(ref range, frameSamples, 3, start, end, channels, result);
        range.Rng.Should().Be(finalRange);
    }

    [Theory]
    [InlineData(1, 13, 16)]
    [InlineData(2, 13, 16)]
    [InlineData(1, 17, 20)]
    [InlineData(2, 17, 20)]
    [InlineData(1, 19, 24)]
    [InlineData(2, 19, 24)]
    [InlineData(1, 21, 28)]
    [InlineData(2, 21, 28)]
    public void Band_limited_packets_decode_through_the_public_decoder(
        int channels, int end, int configBase)
    {
        short[] pcm = BuildSignal(channels, frames: 8);
        var encoder = Create(channels);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = channels });
        var packet = new byte[1276];
        var decoded = new short[960 * channels];

        for (int frame = 0; frame < 8; frame++)
        {
            int interleaved = 960 * channels;
            int payloadBytes = encoder.EncodeFrame(
                pcm.AsSpan(frame * interleaved, interleaved), packet.AsSpan(1), 0, end);
            // CELT-only config base + Ms20 (3), plus the stereo flag when needed.
            packet[0] = (byte)(((configBase + 3) << 3) | (channels == 2 ? 0x04 : 0));
            int written = payloadBytes + 1;

            int count = decoder.Decode(packet.AsSpan(0, written), decoded);
            count.Should().Be(960);
            decoder.FinalRange.Should().Be(encoder.FinalRange);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_restores_the_freshly_constructed_state(bool vbr)
    {
        short[] pcm = BuildSignal(channels: 1, frames: 3);
        var encoder = Create(1, vbr);
        var packet = new byte[encoder.MaxFrameBytes];
        var freshPacket = new byte[encoder.MaxFrameBytes];

        // The reference is a freshly constructed encoder.
        var fresh = Create(1, vbr);
        int freshLength = fresh.EncodeFrame(pcm.AsSpan(0, 960), freshPacket);
        uint freshRange = fresh.FinalRange;

        // Dirty the encoder with two different frames, then reset and repeat.
        encoder.EncodeFrame(pcm.AsSpan(960, 960), packet);
        encoder.EncodeFrame(pcm.AsSpan(1920, 960), packet);
        encoder.Reset();
        int resetLength = encoder.EncodeFrame(pcm.AsSpan(0, 960), packet);

        resetLength.Should().Be(freshLength);
        encoder.FinalRange.Should().Be(freshRange);
        packet.AsSpan(0, resetLength).SequenceEqual(freshPacket.AsSpan(0, freshLength)).Should().BeTrue();
    }

    [Fact]
    public void Vbr_silent_frames_are_two_bytes_and_followed_by_a_valid_frame()
    {
        var encoder = Create(1, vbr: true);
        var packet = new byte[encoder.MaxFrameBytes];

        int silentLength = encoder.EncodeFrame(new short[960], packet);
        silentLength.Should().Be(2);
        var range = new RangeDecoder(packet.AsSpan(0, silentLength));
        var result = new CeltFrameResult();
        new CeltDecoder(1).DecodeFrame(ref range, silentLength, 3, 0, 21, 1, result);
        result.IsSilent.Should().BeTrue();
        range.Rng.Should().Be(encoder.FinalRange);

        // The reservoir/drift update must leave the encoder usable for the next frame.
        short[] tone = BuildSignal(channels: 1, frames: 1);
        int toneLength = encoder.EncodeFrame(tone, packet);
        toneLength.Should().BeGreaterThan(2);
        var toneRange = new RangeDecoder(packet.AsSpan(0, toneLength));
        new CeltDecoder(1).DecodeFrame(ref toneRange, toneLength, 3, 0, 21, 1, new CeltFrameResult());
        toneRange.Rng.Should().Be(encoder.FinalRange);
    }

    private static CeltEncoder Create(int channels, bool vbr = false) => new(
        OpusFrameDuration.Ms20,
        channels == 2 ? 64000 : 32000,
        5,
        channels,
        InputResampler.InternalSampleRate,
        useVbr: vbr,
        constrainedVbr: true);

    private static short[] BuildSignal(int channels, int frames)
    {
        int perChannel = 960 * frames;
        var pcm = new short[perChannel * channels];
        for (int i = 0; i < perChannel; i++)
        {
            short left = (short)(9000 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
            if (channels == 1)
            {
                pcm[i] = left;
            }
            else
            {
                pcm[2 * i] = left;
                pcm[(2 * i) + 1] = (short)(7000 * Math.Sin(2 * Math.PI * 660 * i / 48000.0));
            }
        }

        return pcm;
    }
}