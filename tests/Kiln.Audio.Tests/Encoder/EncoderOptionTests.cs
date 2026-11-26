using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>The supported option surface and its explicit rejections.</summary>
public class EncoderOptionTests
{
    private static OpusEncoderOptions Supported(int frameSamples = 960) => new()
    {
        Application = OpusApplication.RestrictedLowDelay,
        BitrateBps = 32000,
        FrameDuration = OpusFrameDuration.Ms20,
        Complexity = 5,
    };

    [Fact]
    public void The_default_options_select_the_silk_voip_encoder()
    {
        using var encoder = new OpusEncoder(new OpusEncoderOptions());
        var packet = new byte[1276];
        int written = encoder.Encode(new short[960], packet);

        written.Should().BeGreaterThan(1);
        // RFC 6716 §3.1, Table 2: configs 0..11 are SILK-only.
        (packet[0] >> 3).Should().BeLessThan(12);
    }

    [Fact]
    public void The_audio_application_is_rejected()
    {
        var options = Supported() with { Application = OpusApplication.Audio };
        ((Action)(() => new OpusEncoder(options))).Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    [InlineData(24000)]
    public void Other_sample_rates_are_rejected(int rate)
    {
        var options = Supported() with { SampleRate = rate };
        ((Action)(() => new OpusEncoder(options))).Should()
            .Throw<NotSupportedException>()
            .WithMessage("*48 kHz*");
    }

    [Fact]
    public void Stereo_is_rejected()
    {
        var options = Supported() with { Channels = 2 };
        ((Action)(() => new OpusEncoder(options))).Should()
            .Throw<NotSupportedException>()
            .WithMessage("*mono*");
    }

    [Theory]
    [InlineData(OpusFrameDuration.Ms40, 2, 1)]
    [InlineData(OpusFrameDuration.Ms60, 3, 3)]
    public void Long_celt_frames_are_split_into_20_ms_frames(OpusFrameDuration duration, int frameCount, int code)
    {
        using var encoder = new OpusEncoder(Supported() with { FrameDuration = duration });
        var packet = new byte[1276];
        int written = encoder.Encode(new short[frameCount * 960], packet);

        var frames = new Range[OpusPacket.MaxFrames];
        OpusPacket.TryParse(packet.AsSpan(0, written), frames, out int count, out OpusToc toc, out _)
            .Should().Be(OpusStatus.Ok);
        count.Should().Be(frameCount);
        (packet[0] & 0x03).Should().Be(code);
        toc.FrameDuration.Should().Be(OpusFrameDuration.Ms20);
    }

    [Fact]
    public void Fec_and_dtx_are_rejected()
    {
        ((Action)(() => new OpusEncoder(Supported() with { UseInbandFec = true }))).Should()
            .Throw<NotSupportedException>()
            .WithMessage("*forward error correction*");
        ((Action)(() => new OpusEncoder(Supported() with { UseDtx = true }))).Should()
            .Throw<NotSupportedException>()
            .WithMessage("*Discontinuous transmission*");
    }

    [Fact]
    public void Encode_validates_the_frame_and_packet_sizes()
    {
        using var encoder = new OpusEncoder(Supported());
        var packet = new byte[1275];
        var pcm = new short[960];

        ((Action)(() => encoder.Encode(new short[959], packet))).Should().Throw<ArgumentException>();
        ((Action)(() => encoder.Encode(pcm, new byte[10]))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_float_overload_produces_the_same_packet()
    {
        var pcm = new short[960];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        }

        var floats = new float[pcm.Length];
        for (int i = 0; i < floats.Length; i++)
        {
            floats[i] = pcm[i] / 32768f;
        }

        var shortPacket = new byte[1276];
        var floatPacket = new byte[1276];
        int shortCount;
        int floatCount;
        using (var shortEncoder = new OpusEncoder(Supported()))
        {
            shortCount = shortEncoder.Encode(pcm, shortPacket);
        }

        using (var floatEncoder = new OpusEncoder(Supported()))
        {
            floatCount = floatEncoder.Encode(floats, floatPacket);
            floatEncoder.FinalRange.Should().NotBe(0);
        }

        floatCount.Should().Be(shortCount);
        floatPacket.AsSpan(0, floatCount).SequenceEqual(shortPacket.AsSpan(0, shortCount)).Should().BeTrue();
    }

    [Fact]
    public void Every_supported_frame_duration_encodes()
    {
        foreach (OpusFrameDuration duration in new[]
                 {
                     OpusFrameDuration.Ms2_5, OpusFrameDuration.Ms5,
                     OpusFrameDuration.Ms10, OpusFrameDuration.Ms20,
                 })
        {
            using var encoder = new OpusEncoder(Supported() with { FrameDuration = duration });
            int samples = duration switch
            {
                OpusFrameDuration.Ms2_5 => 120,
                OpusFrameDuration.Ms5 => 240,
                OpusFrameDuration.Ms10 => 480,
                _ => 960,
            };
            var packet = new byte[1276];
            int written = encoder.Encode(new short[samples], packet);
            written.Should().BeGreaterThan(1);
        }
    }
}
