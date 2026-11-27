using FluentAssertions;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>G3: encoding a whole SILK stream allocates nothing after construction.</summary>
public class SilkEncoderAllocationTests
{
    public static TheoryData<OpusBandwidth, OpusFrameDuration> Modes() => new()
    {
        { OpusBandwidth.Narrowband, OpusFrameDuration.Ms20 },
        { OpusBandwidth.Mediumband, OpusFrameDuration.Ms20 },
        { OpusBandwidth.Wideband, OpusFrameDuration.Ms20 },
        { OpusBandwidth.Wideband, OpusFrameDuration.Ms60 },
        { OpusBandwidth.Narrowband, OpusFrameDuration.Ms10 },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Encoding_a_whole_stream_allocates_nothing(OpusBandwidth bandwidth, OpusFrameDuration duration)
    {
        int ms = duration switch
        {
            OpusFrameDuration.Ms10 => 10,
            OpusFrameDuration.Ms20 => 20,
            OpusFrameDuration.Ms40 => 40,
            OpusFrameDuration.Ms60 => 60,
            _ => throw new ArgumentOutOfRangeException(nameof(duration)),
        };
        int frameSamples = 48000 * ms / 1000;
        var pcm = new short[frameSamples * 20];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(5000 * Math.Sin(2 * Math.PI * 180 * i / 48000.0));
        }

        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 1,
            Bandwidth = bandwidth,
            BitrateBps = 16000,
            FrameDuration = duration,
            Complexity = 10,
        };

        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];

        Run(encoder, pcm, packet, frameSamples);
        Run(encoder, pcm, packet, frameSamples);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(encoder, pcm, packet, frameSamples);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0, "Encode must not allocate after construction");
    }

    private static void Run(OpusEncoder encoder, short[] pcm, byte[] packet, int frameSamples)
    {
        for (int offset = 0; offset + frameSamples <= pcm.Length; offset += frameSamples)
        {
            encoder.Encode(pcm.AsSpan(offset, frameSamples), packet);
        }
    }
}
