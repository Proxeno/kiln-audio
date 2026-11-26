using FluentAssertions;
using Kiln.Audio;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>G3 for the extended encoder: zero allocations per frame and determinism.</summary>
public class Enc2AllocationTests
{
    public static TheoryData<int, int, bool, int> Combinations()
    {
        var data = new TheoryData<int, int, bool, int>();
        foreach ((int channels, int rate) in new[]
                 {
                     (1, 48000), (2, 48000), (1, 24000), (2, 16000), (1, 12000), (2, 8000),
                 })
        {
            foreach (bool vbr in new[] { false, true })
            {
                data.Add(channels, rate, vbr, 10);
                data.Add(channels, rate, vbr, 5);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Encoding_a_whole_file_allocates_nothing(int channels, int rate, bool vbr, int complexity)
    {
        const int frameMilliseconds = 20;
        int frameSamples = rate / 50;
        short[] pcm = BuildSignal(channels, rate, frames: 40);

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            SampleRate = rate,
            Channels = channels,
            BitrateBps = channels == 2 ? 64000 : 32000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = complexity,
            UseVbr = vbr,
        };

        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        int interleaved = frameSamples * channels;

        Run(encoder, pcm, packet, interleaved);
        Run(encoder, pcm, packet, interleaved);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(encoder, pcm, packet, interleaved);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0, $"Encode must not allocate after construction ({channels}ch, {rate} Hz, vbr={vbr}, c{complexity})");
        _ = frameMilliseconds;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Encoding_is_deterministic(int channels, int rate, bool vbr, int complexity)
    {
        short[] pcm = BuildSignal(channels, rate, frames: 20);
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            SampleRate = rate,
            Channels = channels,
            BitrateBps = channels == 2 ? 64000 : 32000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = complexity,
            UseVbr = vbr,
        };

        Encode(pcm, channels, rate, options).Should().Equal(Encode(pcm, channels, rate, options));
    }

    private static void Run(OpusEncoder encoder, short[] pcm, byte[] packet, int interleaved)
    {
        for (int offset = 0; offset + interleaved <= pcm.Length; offset += interleaved)
        {
            encoder.Encode(pcm.AsSpan(offset, interleaved), packet);
        }
    }

    private static byte[] Encode(short[] pcm, int channels, int rate, OpusEncoderOptions options)
    {
        int interleaved = (rate / 50) * channels;
        using var encoder = new OpusEncoder(options);
        var output = new MemoryStream();
        var packet = new byte[1276];
        for (int offset = 0; offset + interleaved <= pcm.Length; offset += interleaved)
        {
            int written = encoder.Encode(pcm.AsSpan(offset, interleaved), packet);
            output.Write(packet, 0, written);
        }

        return output.ToArray();
    }

    private static short[] BuildSignal(int channels, int rate, int frames)
    {
        int perChannel = (rate / 50) * frames;
        var pcm = new short[perChannel * channels];
        var random = new Random(channels * 1000 + rate);
        for (int i = 0; i < perChannel; i++)
        {
            short left = (short)(8000 * Math.Sin(2 * Math.PI * 330 * i / rate));
            if (channels == 1)
            {
                pcm[i] = left;
            }
            else
            {
                pcm[2 * i] = left;
                pcm[(2 * i) + 1] = (short)(random.Next(-4000, 4000) + (6000 * Math.Sin(2 * Math.PI * 550 * i / rate)));
            }
        }

        return pcm;
    }
}