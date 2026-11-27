using FluentAssertions;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>Internal-consistency smoke tests for the SILK encoder (no libopus oracle needed).</summary>
public class SilkEncoderSmokeTests
{
    public static TheoryData<OpusBandwidth, OpusFrameDuration> Modes() => new()
    {
        { OpusBandwidth.Narrowband, OpusFrameDuration.Ms10 },
        { OpusBandwidth.Narrowband, OpusFrameDuration.Ms20 },
        { OpusBandwidth.Narrowband, OpusFrameDuration.Ms40 },
        { OpusBandwidth.Narrowband, OpusFrameDuration.Ms60 },
        { OpusBandwidth.Mediumband, OpusFrameDuration.Ms20 },
        { OpusBandwidth.Wideband, OpusFrameDuration.Ms10 },
        { OpusBandwidth.Wideband, OpusFrameDuration.Ms20 },
        { OpusBandwidth.Wideband, OpusFrameDuration.Ms60 },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Our_decoder_reproduces_the_encoder_final_range(OpusBandwidth bandwidth, OpusFrameDuration duration)
    {
        const int sampleRate = 48000;
        int ms = DurationMilliseconds(duration);
        int frameSamples = sampleRate * ms / 1000;

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = sampleRate,
            Channels = 1,
            Bandwidth = bandwidth,
            BitrateBps = 12000,
            FrameDuration = duration,
            Complexity = 5,
        };

        var pcm = new short[frameSamples];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(7000 * Math.Sin(2 * Math.PI * 220 * i / sampleRate));
        }

        using var encoder = new OpusEncoder(options);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = sampleRate, Channels = 1 });
        var packet = new byte[1276];
        var decoded = new short[frameSamples];

        for (int frame = 0; frame < 20; frame++)
        {
            int written = encoder.Encode(pcm, packet);
            written.Should().BeGreaterThan(1);
            int config = packet[0] >> 3;
            config.Should().BeInRange(0, 11, "the TOC must be a SILK-only configuration");

            int samples = decoder.Decode(packet.AsSpan(0, written), decoded);
            samples.Should().Be(frameSamples);
            decoder.FinalRange.Should().Be(encoder.FinalRange, "enc and dec range state must agree");
        }
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        var pcm = new short[960];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(4000 * Math.Sin(2 * Math.PI * 330 * i / 48000.0));
        }

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            BitrateBps = 16000,
            FrameDuration = OpusFrameDuration.Ms20,
            Bandwidth = OpusBandwidth.Wideband,
        };

        var first = new byte[1276];
        var second = new byte[1276];
        int firstCount;
        int secondCount;
        using (var encoder = new OpusEncoder(options))
        {
            firstCount = encoder.Encode(pcm, first);
        }

        using (var encoder = new OpusEncoder(options))
        {
            secondCount = encoder.Encode(pcm, second);
        }

        secondCount.Should().Be(firstCount);
        second.AsSpan(0, firstCount).SequenceEqual(first.AsSpan(0, firstCount)).Should().BeTrue();
    }

    private static int DurationMilliseconds(OpusFrameDuration duration) => duration switch
    {
        OpusFrameDuration.Ms10 => 10,
        OpusFrameDuration.Ms20 => 20,
        OpusFrameDuration.Ms40 => 40,
        OpusFrameDuration.Ms60 => 60,
        _ => throw new ArgumentOutOfRangeException(nameof(duration)),
    };
}
