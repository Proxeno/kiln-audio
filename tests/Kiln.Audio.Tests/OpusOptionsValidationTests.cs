using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests;

public class OpusOptionsValidationTests
{
    [Fact]
    public void Encoder_defaults_are_valid_and_match_spec()
    {
        var o = new OpusEncoderOptions();
        o.Validate();
        o.SampleRate.Should().Be(48000);
        o.Channels.Should().Be(1);
        o.Application.Should().Be(OpusApplication.Voip);
        o.BitrateBps.Should().Be(24000);
        o.FrameDuration.Should().Be(OpusFrameDuration.Ms20);
        o.Complexity.Should().Be(5);
        o.UseInbandFec.Should().BeFalse();
        o.UseDtx.Should().BeFalse();
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(12000)]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(48000)]
    public void Legal_sample_rates_pass(int rate)
    {
        new OpusEncoderOptions { SampleRate = rate }.Validate();
        new OpusDecoderOptions { SampleRate = rate }.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(-8000)]
    public void Illegal_sample_rates_throw(int rate)
    {
        ((Action)(() => new OpusEncoderOptions { SampleRate = rate }.Validate())).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusDecoderOptions { SampleRate = rate }.Validate())).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Illegal_channels_throw(int channels)
    {
        ((Action)(() => new OpusEncoderOptions { Channels = channels }.Validate())).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusDecoderOptions { Channels = channels }.Validate())).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Illegal_complexity_throws(int complexity)
    {
        ((Action)(() => new OpusEncoderOptions { Complexity = complexity }.Validate())).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_bitrate_throws(int bitrate)
    {
        ((Action)(() => new OpusEncoderOptions { BitrateBps = bitrate }.Validate())).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Undefined_enum_values_throw()
    {
        ((Action)(() => new OpusEncoderOptions { Application = (OpusApplication)99 }.Validate())).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusEncoderOptions { FrameDuration = (OpusFrameDuration)99 }.Validate())).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructors_validate()
    {
        ((Action)(() => new OpusEncoder(new OpusEncoderOptions { Complexity = 11 }))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusDecoder(new OpusDecoderOptions { Channels = 5 }))).Should().Throw<ArgumentOutOfRangeException>();
    }
}
