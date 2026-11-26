using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>The supported option surface and its explicit rejections.</summary>
public class EncoderOptionTests
{
    private static OpusEncoderOptions Supported() => new()
    {
        Application = OpusApplication.RestrictedLowDelay,
        BitrateBps = 32000,
        FrameDuration = OpusFrameDuration.Ms20,
        Complexity = 5,
    };

    [Fact]
    public void The_default_options_are_accepted()
    {
        using var encoder = new OpusEncoder(new OpusEncoderOptions());
        encoder.Options.Should().Be(new OpusEncoderOptions());
    }

    [Theory]
    [InlineData(OpusApplication.Voip)]
    [InlineData(OpusApplication.Audio)]
    [InlineData(OpusApplication.RestrictedLowDelay)]
    public void Every_application_is_accepted(OpusApplication application)
    {
        var options = Supported() with { Application = application };
        using var encoder = new OpusEncoder(options);
        encoder.Options.Application.Should().Be(application);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(12000)]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(48000)]
    public void Every_sample_rate_is_supported(int rate)
    {
        var options = Supported() with { SampleRate = rate };
        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        int samples = rate / 50;
        int written = encoder.Encode(new short[samples], packet);
        written.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Stereo_is_supported()
    {
        var options = Supported() with { Channels = 2, BitrateBps = 64000 };
        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        int written = encoder.Encode(new short[960 * 2], packet);
        written.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData(OpusFrameDuration.Ms40)]
    [InlineData(OpusFrameDuration.Ms60)]
    public void Silk_frame_durations_are_rejected(OpusFrameDuration duration)
    {
        var options = Supported() with { FrameDuration = duration };
        ((Action)(() => new OpusEncoder(options))).Should().Throw<NotSupportedException>();
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