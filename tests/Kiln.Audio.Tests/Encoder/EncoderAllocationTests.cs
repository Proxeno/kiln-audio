using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>G3: <see cref="OpusEncoder.Encode(ReadOnlySpan{short}, Span{byte})"/> allocates nothing.</summary>
public class EncoderAllocationTests
{
    public static TheoryData<OpusFrameDuration> Durations => new()
    {
        OpusFrameDuration.Ms2_5,
        OpusFrameDuration.Ms5,
        OpusFrameDuration.Ms10,
        OpusFrameDuration.Ms20,
    };

    [Theory]
    [MemberData(nameof(Durations))]
    public void Encoding_a_whole_file_allocates_nothing(OpusFrameDuration duration)
    {
        if (!TestVectors.Available)
        {
            return;
        }

        int frameSamples = duration switch
        {
            OpusFrameDuration.Ms2_5 => 120,
            OpusFrameDuration.Ms5 => 240,
            OpusFrameDuration.Ms10 => 480,
            _ => 960,
        };

        short[] stereo = Pcm16.Read(TestVectors.DecPath(1));
        var mono = new short[stereo.Length / 2];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)(((int)stereo[2 * i] + stereo[2 * i + 1]) / 2);
        }

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            BitrateBps = 32000,
            FrameDuration = duration,
            Complexity = 10,
        };

        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];

        // Warm up the JIT and every branch before measuring.
        Run(encoder, mono, packet, frameSamples);
        Run(encoder, mono, packet, frameSamples);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(encoder, mono, packet, frameSamples);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0, "Encode must not allocate after construction");
    }

    [Fact]
    public void The_float_overload_allocates_nothing()
    {
        if (!TestVectors.Available)
        {
            return;
        }

        const int frameSamples = 960;
        short[] stereo = Pcm16.Read(TestVectors.DecPath(1));
        int frames = (stereo.Length / 2) / frameSamples;
        var mono = new float[frames * frameSamples];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (((stereo[2 * i] + stereo[(2 * i) + 1]) * 0.5f) / 32768f);
        }

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            BitrateBps = 24000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = 5,
        };

        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];

        RunFloat(encoder, mono, packet, frameSamples);
        RunFloat(encoder, mono, packet, frameSamples);

        long before = GC.GetAllocatedBytesForCurrentThread();
        RunFloat(encoder, mono, packet, frameSamples);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0, "the float overload must not allocate after construction");
    }

    private static void Run(OpusEncoder encoder, short[] mono, byte[] packet, int frameSamples)
    {
        for (int offset = 0; offset + frameSamples <= mono.Length; offset += frameSamples)
        {
            encoder.Encode(mono.AsSpan(offset, frameSamples), packet);
        }
    }

    private static void RunFloat(OpusEncoder encoder, float[] mono, byte[] packet, int frameSamples)
    {
        for (int offset = 0; offset + frameSamples <= mono.Length; offset += frameSamples)
        {
            encoder.Encode(mono.AsSpan(offset, frameSamples), packet);
        }
    }
}
