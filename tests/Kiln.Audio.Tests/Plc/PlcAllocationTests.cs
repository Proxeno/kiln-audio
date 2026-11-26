using Kiln.Audio;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Plc;

/// <summary>Loss concealment and in-band FEC decode must not allocate managed memory.</summary>
public class PlcAllocationTests
{
    private static readonly int[] LossVectors = [2, 6, 11];

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(11)]
    public void DecodeLoss_allocates_nothing(int vector)
    {
        byte[] packet = FirstPacket(vector);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[48000 * 120 / 1000];
        decoder.Decode(packet, pcm);

        for (int warmup = 0; warmup < 8; warmup++)
        {
            decoder.DecodeLoss(pcm);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++)
        {
            decoder.DecodeLoss(pcm);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Fec_decode_allocates_nothing()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        byte[] packet = FindPacketWithLbrr();
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = 1 });
        var pcm = new short[48000 * 120 / 1000];

        for (int warmup = 0; warmup < 5; warmup++)
        {
            decoder.Decode(packet, pcm, decodeFec: true);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        decoder.Decode(packet, pcm, decodeFec: true);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    private static byte[] FirstPacket(int vector)
    {
        foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
        {
            return packet;
        }

        throw new InvalidOperationException(FormattableString.Invariant($"vector {vector} is empty"));
    }

    private static byte[] FindPacketWithLbrr()
    {
        short[] stereo = Pcm16.Read(TestVectors.DecPath(4));
        int samples = Math.Min(stereo.Length / 2, 48000);
        var mono = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            mono[i] = (short)((stereo[2 * i] + stereo[(2 * i) + 1]) / 2);
        }

        byte[] bitFile = LibOpusOracle.Encode(
            mono,
            48000,
            1,
            24000,
            ["-bandwidth", "WB", "-framesize", "20", "-cbr", "-inbandfec", "-loss", "10"]);

        string path = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-plc-alloc-{Guid.NewGuid():N}.bit"));
        try
        {
            File.WriteAllBytes(path, bitFile);
            var decoder = new SilkDecoder();
            var result = new SilkFrameResult();
            int index = 0;
            foreach ((byte[] packet, _) in OpusBitFile.Read(path))
            {
                if (index++ < 2)
                {
                    continue;
                }

                var dec = new RangeDecoder(packet);
                decoder.DecodeFrame(ref dec, 16000, 1, 20, result, lbrrOnly: true);
                if (result.IsLbrrCoded(0, 0))
                {
                    return packet;
                }
            }
        }
        finally
        {
            File.Delete(path);
        }

        throw new InvalidOperationException("no packet with LBRR data was produced");
    }
}
