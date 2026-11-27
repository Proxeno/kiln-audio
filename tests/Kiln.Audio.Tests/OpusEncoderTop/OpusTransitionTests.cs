using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Decoder;
using Kiln.Audio.Encoder;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// The encoder side of RFC 6716 §4.5.1: redundancy signalling, the 5 ms redundant CELT frame and
/// the final range's XOR, driven by a stub SILK encoder because the SILK encoder lane has not
/// landed yet.
/// </summary>
public class OpusTransitionTests
{
    private const int FrameSamples = 960;
    private const int MaxFrameBytes = 100;

    [Fact]
    public void Celt_to_silk_redundancy_decodes_and_matches_the_final_range()
    {
        var pcm = EncoderTestSignals.Tone(FrameSamples);
        var frame = new byte[MaxFrameBytes];

        int frameBytes = OpusTransitionEncoder.EncodeSilkOnlyFrame(
            new MockInactiveSilkEncoder(),
            new MockSilentRedundantCeltEncoder(),
            pcm,
            FrameSamples,
            bitrateBps: 24000,
            MaxFrameBytes,
            OpusBandwidth.Wideband,
            redundancy: true,
            celtToSilk: true,
            frame,
            out uint finalRange);

        frameBytes.Should().BeGreaterThan(0, "the frame carries the SILK layer and a redundant frame");
        DecodeAndCheck(frame.AsSpan(0, frameBytes), finalRange);
    }

    [Fact]
    public void Silk_to_celt_redundancy_decodes_and_matches_the_final_range()
    {
        var pcm = EncoderTestSignals.Tone(FrameSamples);
        var frame = new byte[MaxFrameBytes];

        int frameBytes = OpusTransitionEncoder.EncodeSilkOnlyFrame(
            new MockInactiveSilkEncoder(),
            new MockSilentRedundantCeltEncoder(),
            pcm,
            FrameSamples,
            bitrateBps: 24000,
            MaxFrameBytes,
            OpusBandwidth.Wideband,
            redundancy: true,
            celtToSilk: false,
            frame,
            out uint finalRange);

        DecodeAndCheck(frame.AsSpan(0, frameBytes), finalRange);
    }

    [Fact]
    public void A_frame_without_redundancy_still_decodes_to_the_encoded_range()
    {
        var frame = new byte[MaxFrameBytes];

        int frameBytes = OpusTransitionEncoder.EncodeSilkOnlyFrame(
            new MockInactiveSilkEncoder(),
            new MockSilentRedundantCeltEncoder(),
            EncoderTestSignals.Tone(FrameSamples),
            FrameSamples,
            bitrateBps: 24000,
            MaxFrameBytes,
            OpusBandwidth.Wideband,
            redundancy: false,
            celtToSilk: false,
            frame,
            out uint finalRange);

        DecodeAndCheck(frame.AsSpan(0, frameBytes), finalRange);
    }

    [Fact]
    public void The_signalling_round_trips_through_the_decoder()
    {
        var buffer = new byte[MaxFrameBytes];
        var encoder = new Kiln.Audio.Entropy.RangeEncoder(buffer);

        // A few symbols stand in for the SILK layer so the range coder is not at its initial state.
        encoder.EncodeBitLogp(1, 1);
        encoder.EncodeBitLogp(0, 1);
        encoder.EncodeBitLogp(1, 1);

        OpusTransitionEncoder.TryEncodeSignalling(
            ref encoder,
            OpusMode.Silk,
            redundancy: true,
            celtToSilk: true,
            nbComprBytes: 0,
            MaxFrameBytes,
            bitrateBps: 24000,
            out int redundancyBytes).Should().BeTrue();

        int mainBytes = (encoder.Tell() + 7) >> 3;
        encoder.Done();
        redundancyBytes.Should().Be(15); // 24000 / 1600

        var frame = new byte[mainBytes + redundancyBytes];
        buffer.AsSpan(0, mainBytes).CopyTo(frame);

        var decoder = new Kiln.Audio.Entropy.RangeDecoder(frame);
        decoder.DecodeBitLogp(1);
        decoder.DecodeBitLogp(1);
        decoder.DecodeBitLogp(1);
        Kiln.Audio.Silk.OpusRedundancyInfo info =
            Kiln.Audio.Silk.SilkDecoder.DecodeRedundancy(ref decoder, frame.Length);

        info.Present.Should().BeTrue();
        info.CeltToSilk.Should().BeTrue();
        info.Bytes.Should().Be(redundancyBytes);
    }

    [Fact]
    public void A_real_five_millisecond_celt_frame_matches_its_decoded_range()
    {
        var encoder = new CeltRedundantEncoder(bitrateBps: 24000, complexity: 5);
        encoder.FrameBytes.Should().Be(15, "the 5 ms CBR size is bitrate/1600");

        var frame = new byte[encoder.FrameBytes];
        encoder.Encode(EncoderTestSignals.Tone(240), endBand: 21, frame);

        var output = new CeltOutput(48000, 1);
        var pcm = new float[240];
        uint decodedRange = output.DecodeRedundant(frame, endBand: 21, streamChannels: 1, pcm);

        decodedRange.Should().Be(encoder.FinalRange);
    }

    [Fact]
    public void The_transition_encoder_allocates_nothing()
    {
        var silk = new MockInactiveSilkEncoder();
        var celt = new MockSilentRedundantCeltEncoder();
        var pcm = EncoderTestSignals.Tone(FrameSamples);
        var frame = new byte[MaxFrameBytes];

        Run(silk, celt, pcm, frame, out _); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(silk, celt, pcm, frame, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0);
    }

    [Fact]
    public void End_bands_follow_the_packet_bandwidth()
    {
        OpusTransitionEncoder.EndBandOf(OpusBandwidth.Narrowband).Should().Be(13);
        OpusTransitionEncoder.EndBandOf(OpusBandwidth.Mediumband).Should().Be(17);
        OpusTransitionEncoder.EndBandOf(OpusBandwidth.Wideband).Should().Be(17);
        OpusTransitionEncoder.EndBandOf(OpusBandwidth.SuperWideband).Should().Be(19);
        OpusTransitionEncoder.EndBandOf(OpusBandwidth.Fullband).Should().Be(21);
    }

    [Fact]
    public void Bad_spans_are_rejected()
    {
        var silk = new MockInactiveSilkEncoder();
        var celt = new MockSilentRedundantCeltEncoder();
        var pcm = EncoderTestSignals.Tone(FrameSamples);
        var frame = new byte[MaxFrameBytes];

        ((Action)(() => OpusTransitionEncoder.EncodeSilkOnlyFrame(
            silk, celt, pcm[..10], FrameSamples, 24000, MaxFrameBytes, OpusBandwidth.Wideband,
            false, false, frame, out _))).Should().Throw<ArgumentException>();
        ((Action)(() => OpusTransitionEncoder.EncodeSilkOnlyFrame(
            silk, celt, pcm, FrameSamples, 24000, MaxFrameBytes, OpusBandwidth.Wideband,
            false, false, new byte[2], out _))).Should().Throw<ArgumentException>();
        ((Action)(() => OpusTransitionEncoder.EncodeSilkOnlyFrame(
            silk, celt, pcm, FrameSamples, 24000, MaxFrameBytes, OpusBandwidth.Fullband,
            false, false, frame, out _))).Should().Throw<ArgumentOutOfRangeException>();
    }

    private static void Run(
        MockInactiveSilkEncoder silk,
        MockSilentRedundantCeltEncoder celt,
        short[] pcm,
        byte[] frame,
        out uint finalRange)
    {
        OpusTransitionEncoder.EncodeSilkOnlyFrame(
            silk,
            celt,
            pcm,
            FrameSamples,
            bitrateBps: 24000,
            MaxFrameBytes,
            OpusBandwidth.Wideband,
            redundancy: true,
            celtToSilk: true,
            frame,
            out finalRange);
    }

    private static void DecodeAndCheck(ReadOnlySpan<byte> frame, uint expectedRange)
    {
        byte toc = OpusTocBuilder.Build(
            OpusMode.Silk, OpusBandwidth.Wideband, OpusFrameDuration.Ms20, stereo: false);
        var packet = new byte[frame.Length + 8];
        int written = OpusPacketWriter.Write(packet, toc, [frame.Length], frame);

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[FrameSamples];
        decoder.Decode(packet.AsSpan(0, written), pcm).Should().Be(FrameSamples);
        decoder.FinalRange.Should().Be(expectedRange);
    }
}
