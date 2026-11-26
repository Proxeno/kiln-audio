using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Silk;

/// <summary>
/// Checks that synthesizing a frame allocates no managed memory, which the real-time path requires.
/// </summary>
public class SilkSynthesisAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SilkSynthesisAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Synthesize_allocates_nothing()
    {
        var frames = new Range[OpusPacket.MaxFrames];
        var decoder = new SilkDecoder();
        var synthesis = new SilkSynthesis();
        var result = new SilkFrameResult();
        short[] pcm = new short[6 * 960];

        // Find a SILK-only packet and decode its parameters once.
        (byte[] packet, OpusToc toc, int frameCount, int durationMs) = FindSilkPacket(frames);
        int rate = toc.Bandwidth switch
        {
            OpusBandwidth.Narrowband => 8000,
            OpusBandwidth.Mediumband => 12000,
            _ => 16000,
        };
        int channels = toc.IsStereo ? 2 : 1;

        Range frameRange = frames[0];
        var rangeDecoder = new RangeDecoder(packet.AsSpan(frameRange));
        decoder.DecodeFrame(ref rangeDecoder, rate, channels, durationMs, result);

        // Warm up: JIT the synthesis path and grow any lazily initialized state.
        synthesis.Synthesize(result, pcm);

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int Iterations = 100;
        for (int i = 0; i < Iterations; i++)
        {
            synthesis.Synthesize(result, pcm);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine(
            "Synthesized {0} frames ({1} Hz, {2} ch), allocated {3} bytes.",
            Iterations, rate, channels, allocated);
        allocated.Should().Be(0);
    }

    private static (byte[] Packet, OpusToc Toc, int FrameCount, int DurationMs) FindSilkPacket(
        Range[] frames)
    {
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(2)))
        {
            OpusToc toc = OpusToc.Parse(packet[0]);
            if (toc.Mode != OpusMode.Silk || toc.FrameCountCode != 0)
            {
                continue;
            }

            OpusStatus status = OpusPacket.TryParse(packet, frames, out int frameCount, out _, out _);
            status.Should().Be(OpusStatus.Ok);
            int durationMs = toc.FrameDuration switch
            {
                OpusFrameDuration.Ms10 => 10,
                OpusFrameDuration.Ms20 => 20,
                OpusFrameDuration.Ms40 => 40,
                _ => 60,
            };
            return (packet, toc, frameCount, durationMs);
        }

        throw new InvalidOperationException("No SILK-only packet in testvector02.bit.");
    }
}
