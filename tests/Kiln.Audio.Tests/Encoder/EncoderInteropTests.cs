using FluentAssertions;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>G1: every corpus file, frame size and bitrate decodes without a range-coder mismatch.</summary>
public class EncoderInteropTests
{
    private static readonly Lazy<IReadOnlyList<(int Vector, short[] Mono)>> Corpus = new(EncoderCorpus.Load);

    public static TheoryData<OpusFrameDuration, int> Combinations()
    {
        var data = new TheoryData<OpusFrameDuration, int>();
        foreach (OpusFrameDuration duration in new[]
                 {
                     OpusFrameDuration.Ms2_5, OpusFrameDuration.Ms5,
                     OpusFrameDuration.Ms10, OpusFrameDuration.Ms20,
                 })
        {
            foreach (int bitrate in new[] { 24000, 32000 })
            {
                data.Add(duration, bitrate);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void OpusDemo_decodes_our_stream_without_a_range_mismatch(OpusFrameDuration duration, int bitrate)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        int lm = duration switch
        {
            OpusFrameDuration.Ms2_5 => 0,
            OpusFrameDuration.Ms5 => 1,
            OpusFrameDuration.Ms10 => 2,
            OpusFrameDuration.Ms20 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(duration)),
        };
        int frameSamples = 120 << lm;

        foreach ((int vector, short[] mono) in Corpus.Value)
        {
            var packets = EncoderCorpus.EncodeWithLibrary(mono, bitrate, duration, complexity: 5);
            packets.Should().NotBeEmpty();

            var decoder = new CeltDecoder(1);
            var frame = new CeltFrameResult();
            decoder.Reset();
            foreach ((byte[] packet, uint expectedRange) in packets)
            {
                var range = new RangeDecoder(packet.AsSpan(1));
                decoder.DecodeFrame(ref range, packet.Length - 1, lm, 0, 21, 1, frame);
                range.Error.Should().BeFalse($"vector {vector}, {duration}, {bitrate}: our decoder reported corruption");
                range.Rng.Should().Be(
                    expectedRange,
                    $"vector {vector}, {duration}, {bitrate}: our decoder's final range must match the encoder's");
            }

            string bitPath = Path.Combine(Path.GetTempPath(), $"kiln-g1-{Guid.NewGuid():N}.bit");
            try
            {
                EncoderCorpus.WriteBitFile(bitPath, packets);
                (short[] decoded, string diagnostics) = EncoderOracle.Decode(bitPath);
                EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse(
                    $"vector {vector}, {duration}, {bitrate}: opus_demo reported a range mismatch:\n{diagnostics}");
                decoded.Length.Should().Be(
                    packets.Count * frameSamples,
                    $"vector {vector}, {duration}, {bitrate}: opus_demo decoded an unexpected sample count");
            }
            finally
            {
                if (File.Exists(bitPath))
                {
                    File.Delete(bitPath);
                }
            }
        }
    }
}
