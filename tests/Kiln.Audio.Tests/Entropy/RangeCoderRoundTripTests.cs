using Kiln.Audio.Entropy;

namespace Kiln.Audio.Tests;

public class RangeCoderRoundTripTests
{
    /// <summary>A buffer large enough that raw bits sit far from the range-coded bytes.</summary>
    private const int BufferSize = 1 << 16;

    [Fact]
    public void TwoThousand_random_streams_round_trip_with_matching_tell()
    {
        for (int seed = 0; seed < 2000; seed++)
        {
            var random = new Random(seed);
            List<RangeOp> ops = RangeCoderTestStreams.Generate(random);
            var buffer = new byte[BufferSize];

            var encoder = new RangeEncoder(buffer);
            var tells = new int[ops.Count];
            var fractions = new uint[ops.Count];
            for (int i = 0; i < ops.Count; i++)
            {
                RangeCoderTestStreams.ApplyEncode(ref encoder, ops[i]);
                tells[i] = encoder.Tell();
                fractions[i] = encoder.TellFrac();
            }

            uint encoderRng = encoder.Rng;
            if (encoder.Error)
            {
                Assert.Fail($"seed {seed}: the encoder reported an error while encoding.");
            }

            encoder.Done();
            if (encoder.Error)
            {
                Assert.Fail($"seed {seed}: the encoder reported an error while finalizing.");
            }

            var decoder = new RangeDecoder(buffer);
            for (int i = 0; i < ops.Count; i++)
            {
                bool ok = RangeCoderTestStreams.ApplyDecode(ref decoder, ops[i], out uint observed);
                if (!ok)
                {
                    Assert.Fail($"seed {seed} op {i} ({ops[i].Kind}): decoded {observed}.");
                }

                int decoderTell = decoder.Tell();
                if (decoderTell != tells[i])
                {
                    Assert.Equal(tells[i], decoderTell);
                }

                uint decoderFraction = decoder.TellFrac();
                if (decoderFraction != fractions[i])
                {
                    Assert.Equal(fractions[i], decoderFraction);
                }

                if (decoder.Error)
                {
                    Assert.Fail($"seed {seed} op {i} ({ops[i].Kind}): the decoder reported an error.");
                }
            }

            if (decoder.Rng != encoderRng)
            {
                Assert.Equal(encoderRng, decoder.Rng);
            }
        }
    }
}
