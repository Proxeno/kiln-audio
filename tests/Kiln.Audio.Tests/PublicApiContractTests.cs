using System.IO;
using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests;

/// <summary>
/// The documented public contract of <see cref="OpusEncoder"/>, <see cref="OpusDecoder"/>, the
/// options records and <see cref="OpusPacket.HasLbrr"/>, exercised through the public API only.
/// </summary>
/// <remarks>
/// The signals are generated in code, so these tests need neither the libopus oracle nor the
/// committed test vectors.
/// </remarks>
public class PublicApiContractTests
{
    private static readonly int[] SampleRates = [8000, 12000, 16000, 24000, 48000];

    public static TheoryData<int, int, OpusApplication> RoundTripConfigurations()
    {
        var data = new TheoryData<int, int, OpusApplication>();
        foreach (int rate in SampleRates)
        {
            foreach (int channels in (int[])[1, 2])
            {
                data.Add(rate, channels, OpusApplication.Voip);
                data.Add(rate, channels, OpusApplication.RestrictedLowDelay);
            }
        }

        return data;
    }

    // ---------------------------------------------------------------- constructors

    [Fact]
    public void Null_options_throw_ArgumentNullException()
    {
        ((Action)(() => new OpusEncoder(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new OpusDecoder(null!))).Should().Throw<ArgumentNullException>();
    }

    // ---------------------------------------------------------------- options validation

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Packet_loss_outside_0_to_100_throws(int percent)
    {
        var options = new OpusEncoderOptions { PacketLossPercent = percent };

        ((Action)(() => options.Validate())).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusEncoder(options))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Packet_loss_at_the_limits_passes(int percent)
    {
        var options = new OpusEncoderOptions { PacketLossPercent = percent };

        ((Action)(() => options.Validate())).Should().NotThrow();
    }

    [Fact]
    public void An_undefined_bandwidth_throws_from_Validate_and_the_constructor()
    {
        var options = new OpusEncoderOptions { Bandwidth = (OpusBandwidth)99 };

        ((Action)(() => options.Validate())).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new OpusEncoder(options))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Every_defined_bandwidth_passes_Validate()
    {
        foreach (OpusBandwidth bandwidth in Enum.GetValues<OpusBandwidth>())
        {
            var options = new OpusEncoderOptions { Bandwidth = bandwidth };
            ((Action)(() => options.Validate())).Should().NotThrow();
        }

        ((Action)(() => new OpusEncoderOptions { Bandwidth = null }.Validate())).Should().NotThrow();
    }

    [Fact]
    public void Validation_errors_name_the_offending_property()
    {
        var cases = new (string Property, OpusEncoderOptions Options)[]
        {
            (nameof(OpusEncoderOptions.SampleRate), new OpusEncoderOptions { SampleRate = 44100 }),
            (nameof(OpusEncoderOptions.Channels), new OpusEncoderOptions { Channels = 3 }),
            (nameof(OpusEncoderOptions.BitrateBps), new OpusEncoderOptions { BitrateBps = 0 }),
            (nameof(OpusEncoderOptions.Complexity), new OpusEncoderOptions { Complexity = 11 }),
            (nameof(OpusEncoderOptions.PacketLossPercent), new OpusEncoderOptions { PacketLossPercent = 101 }),
            (nameof(OpusEncoderOptions.Bandwidth), new OpusEncoderOptions { Bandwidth = (OpusBandwidth)99 }),
            (nameof(OpusEncoderOptions.Application), new OpusEncoderOptions { Application = (OpusApplication)99 }),
            (nameof(OpusEncoderOptions.FrameDuration), new OpusEncoderOptions { FrameDuration = (OpusFrameDuration)99 }),
        };

        foreach ((string property, OpusEncoderOptions options) in cases)
        {
            Exception? fromValidate = Record.Exception(() => options.Validate());
            fromValidate.Should().BeOfType<ArgumentOutOfRangeException>(property);
            ((ArgumentOutOfRangeException)fromValidate!).ParamName.Should().Be(property);

            Exception? fromConstructor = Record.Exception(() => new OpusEncoder(options));
            fromConstructor.Should().BeOfType<ArgumentOutOfRangeException>(property);
            ((ArgumentOutOfRangeException)fromConstructor!).ParamName.Should().Be(property);
        }

        var decoderCases = new (string Property, OpusDecoderOptions Options)[]
        {
            (nameof(OpusDecoderOptions.SampleRate), new OpusDecoderOptions { SampleRate = 44100 }),
            (nameof(OpusDecoderOptions.Channels), new OpusDecoderOptions { Channels = 3 }),
        };

        foreach ((string property, OpusDecoderOptions options) in decoderCases)
        {
            Exception? fromConstructor = Record.Exception(() => new OpusDecoder(options));
            fromConstructor.Should().BeOfType<ArgumentOutOfRangeException>(property);
            ((ArgumentOutOfRangeException)fromConstructor!).ParamName.Should().Be(property);
        }
    }

    // ---------------------------------------------------------------- options records

    [Fact]
    public void Encoder_options_have_value_equality_and_with_expressions()
    {
        var first = new OpusEncoderOptions();
        var second = new OpusEncoderOptions();

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());

        OpusEncoderOptions changed = first with { BitrateBps = 32000 };
        changed.Should().NotBe(first);
        changed.BitrateBps.Should().Be(32000);
        first.BitrateBps.Should().Be(24000, "a with expression copies and leaves the original unchanged");
        (changed with { BitrateBps = 24000 }).Should().Be(first);
    }

    [Fact]
    public void Decoder_options_have_value_equality_and_with_expressions()
    {
        var first = new OpusDecoderOptions();
        var second = new OpusDecoderOptions();

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());

        OpusDecoderOptions changed = first with { Channels = 2 };
        changed.Should().NotBe(first);
        changed.Channels.Should().Be(2);
        first.Channels.Should().Be(1);
    }

    [Fact]
    public void The_Options_properties_equal_what_was_passed()
    {
        var encoderOptions = new OpusEncoderOptions
        {
            SampleRate = 16000,
            Channels = 2,
            BitrateBps = 40000,
            Complexity = 7,
            UseInbandFec = true,
            PacketLossPercent = 5,
        };
        var decoderOptions = new OpusDecoderOptions { SampleRate = 24000, Channels = 2 };

        using var encoder = new OpusEncoder(encoderOptions);
        using var decoder = new OpusDecoder(decoderOptions);

        encoder.Options.Should().Be(encoderOptions);
        decoder.Options.Should().Be(decoderOptions);
    }

    // ---------------------------------------------------------------- supported and unsupported combinations

    [Fact]
    public void Fec_and_dtx_on_the_celt_only_path_throw_NotSupportedException()
    {
        var restricted = new OpusEncoderOptions { Application = OpusApplication.RestrictedLowDelay };

        ((Action)(() => new OpusEncoder(restricted with { UseInbandFec = true }))).Should().Throw<NotSupportedException>();
        ((Action)(() => new OpusEncoder(restricted with { UseDtx = true }))).Should().Throw<NotSupportedException>();

        var audioStereo = new OpusEncoderOptions
        {
            Application = OpusApplication.Audio,
            Channels = 2,
            UseInbandFec = true,
        };
        ((Action)(() => new OpusEncoder(audioStereo))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Fec_and_dtx_on_the_silk_and_automatic_paths_construct()
    {
        var voipStereo16k = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 16000,
            Channels = 2,
            UseInbandFec = true,
            PacketLossPercent = 10,
        };
        using (var encoder = new OpusEncoder(voipStereo16k))
        {
            encoder.Options.Should().Be(voipStereo16k);
        }

        var voipDtx = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 1,
            UseDtx = true,
        };
        using (var encoder = new OpusEncoder(voipDtx))
        {
            encoder.Options.Should().Be(voipDtx);
        }

        var audioMonoFec = new OpusEncoderOptions
        {
            Application = OpusApplication.Audio,
            SampleRate = 48000,
            Channels = 1,
            FrameDuration = OpusFrameDuration.Ms20,
            UseInbandFec = true,
            PacketLossPercent = 10,
        };
        using (var encoder = new OpusEncoder(audioMonoFec))
        {
            encoder.Options.Should().Be(audioMonoFec);
        }
    }

    // ---------------------------------------------------------------- disposal

    [Fact]
    public void Encoder_Dispose_is_idempotent_and_Encode_then_throws()
    {
        var options = new OpusEncoderOptions();
        var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        int written = encoder.Encode(Frame(48000, 1, 0), packet);
        written.Should().BeGreaterThan(0);
        uint rangeBefore = encoder.FinalRange;

        encoder.Dispose();
        ((Action)(() => encoder.Dispose())).Should().NotThrow();

        ((Action)(() => encoder.Encode(Frame(48000, 1, 1), packet))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => encoder.Encode(new float[960], packet))).Should().Throw<ObjectDisposedException>();

        // The disposed check comes before the frame-length check on both overloads.
        ((Action)(() => encoder.Encode(new float[3], packet))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => encoder.Encode(new short[3], packet))).Should().Throw<ObjectDisposedException>();

        encoder.Options.Should().Be(options);
        encoder.FinalRange.Should().Be(rangeBefore);
    }

    [Fact]
    public void Decoder_Dispose_is_idempotent_and_Decode_then_throws()
    {
        var options = new OpusDecoderOptions();
        var decoder = new OpusDecoder(options);
        var pcm = new short[960];
        using var encoder = new OpusEncoder(new OpusEncoderOptions());
        var packet = new byte[1276];
        int written = encoder.Encode(Frame(48000, 1, 0), packet);
        decoder.Decode(packet.AsSpan(0, written), pcm).Should().Be(960);
        uint rangeBefore = decoder.FinalRange;

        decoder.Dispose();
        ((Action)(() => decoder.Dispose())).Should().NotThrow();

        var floats = new float[960];
        ((Action)(() => decoder.Decode(packet.AsSpan(0, written), floats))).Should().Throw<ObjectDisposedException>();

        decoder.Options.Should().Be(options);
        decoder.FinalRange.Should().Be(rangeBefore);
        decoder.LastFecOutcome.Should().Be(OpusFecOutcome.NotRequested);
    }

    // ---------------------------------------------------------------- argument errors

    [Fact]
    public void A_wrong_length_frame_throws_and_the_encoder_stays_usable()
    {
        var configurations = new (int Rate, int Channels, OpusApplication Application)[]
        {
            (48000, 1, OpusApplication.Voip),
            (16000, 2, OpusApplication.Voip),
            (48000, 1, OpusApplication.RestrictedLowDelay),
            (24000, 2, OpusApplication.RestrictedLowDelay),
        };

        foreach ((int rate, int channels, OpusApplication application) in configurations)
        {
            using var encoder = new OpusEncoder(Options(rate, channels, application));
            var packet = new byte[1276];
            int frameSamples = rate / 50 * channels;

            ((Action)(() => encoder.Encode(new short[frameSamples - 1], packet))).Should()
                .Throw<ArgumentException>().WithParameterName("pcm");
            ((Action)(() => encoder.Encode(new short[frameSamples + 1], packet))).Should()
                .Throw<ArgumentException>().WithParameterName("pcm");
            ((Action)(() => encoder.Encode(new float[frameSamples - 1], packet))).Should()
                .Throw<ArgumentException>().WithParameterName("pcm");
            ((Action)(() => encoder.Encode(new float[frameSamples + 1], packet))).Should()
                .Throw<ArgumentException>().WithParameterName("pcm");

            int written = encoder.Encode(Frame(rate, channels, 0), packet);
            written.Should().BeInRange(1, 1276);

            var floatFrame = new float[frameSamples];
            written = encoder.Encode(floatFrame, packet);
            written.Should().BeInRange(1, 1276);
        }
    }

    [Fact]
    public void A_too_short_destination_throws_ArgumentException_for_the_float_decode()
    {
        using var encoder = new OpusEncoder(new OpusEncoderOptions());
        using var decoder = new OpusDecoder(new OpusDecoderOptions());
        var packet = new byte[1276];
        int written = encoder.Encode(Frame(48000, 1, 0), packet);

        ((Action)(() => decoder.Decode(packet.AsSpan(0, written), new float[16]))).Should().Throw<ArgumentException>();

        // A destination of the documented size works afterwards.
        decoder.Decode(packet.AsSpan(0, written), new float[960]).Should().Be(960);
    }

    [Fact]
    public void A_too_short_destination_throws_ArgumentException_for_DecodeLoss()
    {
        using var encoder = new OpusEncoder(new OpusEncoderOptions());
        using var decoder = new OpusDecoder(new OpusDecoderOptions());
        var packet = new byte[1276];
        int written = encoder.Encode(Frame(48000, 1, 0), packet);
        decoder.Decode(packet.AsSpan(0, written), new short[960]).Should().Be(960);

        ((Action)(() => decoder.DecodeLoss(new short[16]))).Should().Throw<ArgumentException>();

        decoder.DecodeLoss(new short[960]).Should().Be(960);
    }

    // ---------------------------------------------------------------- OpusPacket.HasLbrr

    [Fact]
    public void HasLbrr_throws_InvalidDataException_for_an_empty_packet()
    {
        ((Action)(() => OpusPacket.HasLbrr([]))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void HasLbrr_throws_InvalidDataException_for_a_malformed_packet()
    {
        // A code 3 packet with no frame-count byte violates RFC 6716 §3.2.1.
        ((Action)(() => OpusPacket.HasLbrr([0x03]))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void HasLbrr_is_false_for_a_celt_only_packet()
    {
        using var encoder = new OpusEncoder(Options(48000, 1, OpusApplication.RestrictedLowDelay));
        var packet = new byte[1276];
        int written = encoder.Encode(Frame(48000, 1, 0), packet);

        OpusPacket.HasLbrr(packet.AsSpan(0, written)).Should().BeFalse();
    }

    // ---------------------------------------------------------------- round trip

    [Theory]
    [MemberData(nameof(RoundTripConfigurations))]
    public void Every_rate_and_channel_count_round_trips(int rate, int channels, OpusApplication application)
    {
        using var encoder = new OpusEncoder(Options(rate, channels, application));
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = rate, Channels = channels });
        var packet = new byte[1276];
        var decoded = new short[rate / 50 * channels];

        for (int frame = 0; frame < 10; frame++)
        {
            int written = encoder.Encode(Frame(rate, channels, frame), packet);
            written.Should().BeInRange(1, 1276);

            int samples = decoder.Decode(packet.AsSpan(0, written), decoded);
            samples.Should().Be(rate / 50);
        }

        decoded.Should().Contain(sample => sample != 0, "a 440 Hz tone must not decode to silence");
    }

    // ---------------------------------------------------------------- channel mapping

    [Theory]
    [InlineData(OpusApplication.Voip)]
    [InlineData(OpusApplication.RestrictedLowDelay)]
    public void A_mono_stream_decoded_as_stereo_duplicates_the_channel(OpusApplication application)
    {
        using var encoder = new OpusEncoder(Options(48000, 1, application));
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 2 });
        var packet = new byte[1276];
        var decoded = new short[960 * 2];

        for (int frame = 0; frame < 5; frame++)
        {
            int written = encoder.Encode(Frame(48000, 1, frame), packet);
            decoder.Decode(packet.AsSpan(0, written), decoded).Should().Be(960);

            for (int i = 0; i < 960; i++)
            {
                decoded[2 * i].Should().Be(decoded[(2 * i) + 1], $"frame {frame}, sample {i}");
            }
        }

        decoded.Should().Contain(sample => sample != 0);
    }

    [Theory]
    [InlineData(OpusApplication.Voip)]
    [InlineData(OpusApplication.RestrictedLowDelay)]
    public void A_stereo_stream_decoded_as_mono_returns_one_channel_of_samples(OpusApplication application)
    {
        using var encoder = new OpusEncoder(Options(48000, 2, application));
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var packet = new byte[1276];
        var decoded = new short[960];

        for (int frame = 0; frame < 5; frame++)
        {
            int written = encoder.Encode(Frame(48000, 2, frame), packet);
            decoder.Decode(packet.AsSpan(0, written), decoded).Should().Be(960);
        }

        decoded.Should().Contain(sample => sample != 0);
    }

    // ---------------------------------------------------------------- determinism

    [Theory]
    [InlineData(OpusApplication.Voip)]
    [InlineData(OpusApplication.RestrictedLowDelay)]
    public void Identical_encoders_produce_identical_packets_and_matching_final_ranges(OpusApplication application)
    {
        OpusEncoderOptions options = Options(48000, 1, application);
        using var first = new OpusEncoder(options);
        using var second = new OpusEncoder(options);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var packetA = new byte[1276];
        var packetB = new byte[1276];
        var decoded = new short[960];

        for (int frame = 0; frame < 20; frame++)
        {
            short[] input = Frame(48000, 1, frame);
            int writtenA = first.Encode(input, packetA);
            int writtenB = second.Encode(input, packetB);

            writtenB.Should().Be(writtenA, $"frame {frame}");
            packetA.AsSpan(0, writtenA).SequenceEqual(packetB.AsSpan(0, writtenB)).Should().BeTrue($"frame {frame}");
            second.FinalRange.Should().Be(first.FinalRange, $"frame {frame}");

            decoder.Decode(packetA.AsSpan(0, writtenA), decoded).Should().Be(960);
            decoder.FinalRange.Should().Be(first.FinalRange, $"frame {frame}: the decoder must reproduce the encoder's range");
        }
    }

    // ---------------------------------------------------------------- instance independence

    [Fact]
    public void Separate_default_voip_instances_are_independent_across_threads()
    {
        AssertThreadsMatchSingleThread(new OpusEncoderOptions());
    }

    [Fact]
    public void Separate_audio_stereo_instances_are_independent_across_threads()
    {
        AssertThreadsMatchSingleThread(new OpusEncoderOptions
        {
            Application = OpusApplication.Audio,
            SampleRate = 48000,
            Channels = 2,
            BitrateBps = 64000,
        });
    }

    private static void AssertThreadsMatchSingleThread(OpusEncoderOptions options)
    {
        const int Frames = 50;
        const int Threads = 4;

        StreamResult reference = RunStream(options, Frames);
        var results = new StreamResult[Threads];

        Parallel.For(0, Threads, index => results[index] = RunStream(options, Frames));

        for (int i = 0; i < Threads; i++)
        {
            results[i].Packets.AsSpan().SequenceEqual(reference.Packets).Should().BeTrue($"thread {i}: packets");
            results[i].Pcm.AsSpan().SequenceEqual(reference.Pcm).Should().BeTrue($"thread {i}: decoded PCM");
            results[i].EncoderRanges.Should().Equal(reference.EncoderRanges, $"thread {i}: encoder ranges");
        }
    }

    private static StreamResult RunStream(OpusEncoderOptions options, int frames)
    {
        using var encoder = new OpusEncoder(options);
        using var decoder = new OpusDecoder(
            new OpusDecoderOptions { SampleRate = options.SampleRate, Channels = options.Channels });
        int perChannel = options.SampleRate / 50;
        var buffer = new byte[1276];
        var decoded = new short[perChannel * options.Channels];
        var allPcm = new short[frames * decoded.Length];
        var ranges = new uint[frames];
        using var packets = new MemoryStream();

        for (int frame = 0; frame < frames; frame++)
        {
            int written = encoder.Encode(Frame(options.SampleRate, options.Channels, frame), buffer);
            ranges[frame] = encoder.FinalRange;
            packets.WriteByte((byte)(written & 0xFF));
            packets.WriteByte((byte)(written >> 8));
            packets.Write(buffer, 0, written);

            int samples = decoder.Decode(buffer.AsSpan(0, written), decoded);
            if (samples != perChannel)
            {
                throw new InvalidOperationException("Unexpected decoded frame size.");
            }

            decoded.CopyTo(allPcm, frame * decoded.Length);
        }

        return new StreamResult(packets.ToArray(), allPcm, ranges);
    }

    // ---------------------------------------------------------------- allocation

    [Fact]
    public void Default_voip_encode_and_decode_allocate_nothing()
    {
        const int WarmUpFrames = 5;
        const int MeasuredFrames = 20;
        const int FrameSamples = 960;

        using var encoder = new OpusEncoder(new OpusEncoderOptions());
        using var decoder = new OpusDecoder(new OpusDecoderOptions());
        var input = new short[(WarmUpFrames + MeasuredFrames) * FrameSamples];
        for (int frame = 0; frame < WarmUpFrames + MeasuredFrames; frame++)
        {
            Frame(48000, 1, frame).CopyTo(input, frame * FrameSamples);
        }

        var packet = new byte[1276];
        var decoded = new short[FrameSamples];

        for (int frame = 0; frame < WarmUpFrames; frame++)
        {
            int written = encoder.Encode(input.AsSpan(frame * FrameSamples, FrameSamples), packet);
            decoder.Decode(packet.AsSpan(0, written), decoded);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = WarmUpFrames; frame < WarmUpFrames + MeasuredFrames; frame++)
        {
            int written = encoder.Encode(input.AsSpan(frame * FrameSamples, FrameSamples), packet);
            decoder.Decode(packet.AsSpan(0, written), decoded);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    // ---------------------------------------------------------------- helpers

    private static OpusEncoderOptions Options(int rate, int channels, OpusApplication application) => new()
    {
        SampleRate = rate,
        Channels = channels,
        Application = application,
        BitrateBps = channels == 1 ? 32000 : 64000,
        FrameDuration = OpusFrameDuration.Ms20,
    };

    /// <summary>One 20 ms frame of interleaved tones: 440 Hz left, 660 Hz right, amplitude 8000.</summary>
    private static short[] Frame(int rate, int channels, int frameIndex)
    {
        int perChannel = rate / 50;
        var pcm = new short[perChannel * channels];
        for (int i = 0; i < perChannel; i++)
        {
            long n = ((long)frameIndex * perChannel) + i;
            for (int channel = 0; channel < channels; channel++)
            {
                double hz = channel == 0 ? 440.0 : 660.0;
                pcm[(i * channels) + channel] = (short)Math.Round(8000.0 * Math.Sin(2.0 * Math.PI * hz * n / rate));
            }
        }

        return pcm;
    }

    private sealed record StreamResult(byte[] Packets, short[] Pcm, uint[] EncoderRanges);
}
