using FluentAssertions;
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
    public void OpusDemo_and_our_decoder_agree_on_every_packet(OpusFrameDuration duration, int bitrate)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        int frameSamples = duration switch
        {
            OpusFrameDuration.Ms2_5 => 120,
            OpusFrameDuration.Ms5 => 240,
            OpusFrameDuration.Ms10 => 480,
            OpusFrameDuration.Ms20 => 960,
            _ => throw new ArgumentOutOfRangeException(nameof(duration)),
        };

        foreach ((int vector, short[] mono) in Corpus.Value)
        {
            var packets = EncoderCorpus.EncodeWithLibrary(mono, bitrate, duration, complexity: 5);
            packets.Should().NotBeEmpty();

            using var decoder = new OpusDecoder(new OpusDecoderOptions
            {
                SampleRate = 48000,
                Channels = 1,
            });
            var pcm = new short[frameSamples];
            foreach ((byte[] packet, uint expectedRange) in packets)
            {
                int decoded = decoder.Decode(packet, pcm);
                decoded.Should().Be(
                    frameSamples,
                    $"vector {vector}, {duration}, {bitrate}: the public decoder must return one frame");
                decoder.FinalRange.Should().Be(
                    expectedRange,
                    $"vector {vector}, {duration}, {bitrate}: the decoder final range must match the encoder's");
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
