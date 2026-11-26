using System.Globalization;
using System.Text;
using Kiln.Audio.Entropy;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests;

/// <summary>
/// Differential tests that hold the managed range coder to libopus's entropy coder, byte for
/// byte and state for state. The reference side is the test-only <c>ec_probe</c> binary.
/// </summary>
public class RangeCoderOracleTests
{
    private const int SeedCount = 500;
    private const int GarbageSeedCount = 200;
    private const int ShrinkSeedCount = 50;
    private const int GenerousBuffer = 1 << 16;

    [Fact]
    public void Encoder_matches_libopus_byte_for_byte_on_tight_buffers()
    {
        if (!OracleTestSupport.RangeProbeAvailable())
        {
            return;
        }

        for (int seed = 0; seed < SeedCount; seed++)
        {
            List<RangeOp> ops = RangeCoderTestStreams.Generate(new Random(seed));
            int minimum = MeasureMinimumStorage(ops);
            for (int extra = 0; extra <= 2; extra++)
            {
                int storage = minimum + extra;
                var ours = new byte[storage];
                var encoder = new RangeEncoder(ours);
                var ourSteps = new List<LibOpusOracle.RangeProbeStep>(ops.Count + 1);
                for (int i = 0; i < ops.Count; i++)
                {
                    RangeCoderTestStreams.ApplyEncode(ref encoder, ops[i]);
                    ourSteps.Add(EncoderState(encoder));
                }

                encoder.Done();
                ourSteps.Add(EncoderState(encoder));

                string context = $"seed {seed}, storage {storage}";
                if (encoder.Error)
                {
                    Assert.Fail($"{context}: our encoder reported an error.");
                }

                LibOpusOracle.RangeProbeResult probe =
                    LibOpusOracle.RangeProbe(EncoderScript(ops, storage));
                if (probe.Bytes.Length != storage)
                {
                    Assert.Fail($"{context}: probe produced {probe.Bytes.Length} bytes, expected {storage}.");
                }

                if (!probe.Bytes.AsSpan().SequenceEqual(ours))
                {
                    Assert.Fail(
                        $"{context}: encoder bytes differ.\nprobe {Convert.ToHexString(probe.Bytes)}\nours  {Convert.ToHexString(ours)}");
                }

                if (probe.Steps.Count != ourSteps.Count)
                {
                    Assert.Fail($"{context}: probe reported {probe.Steps.Count} steps, expected {ourSteps.Count}.");
                }

                for (int i = 0; i < ourSteps.Count; i++)
                {
                    string stepContext = $"{context}, step {i}";
                    if (probe.Steps[i].Value != 0)
                    {
                        Assert.Fail($"{stepContext}: probe reported a decoded value during encoding.");
                    }

                    AssertState(probe.Steps[i], ourSteps[i], stepContext);
                }
            }
        }
    }

    [Fact]
    public void Decoder_on_libopus_bytes_matches_libopus_state()
    {
        if (!OracleTestSupport.RangeProbeAvailable())
        {
            return;
        }

        for (int seed = 0; seed < SeedCount; seed++)
        {
            List<RangeOp> ops = RangeCoderTestStreams.Generate(new Random(seed));
            int minimum = MeasureMinimumStorage(ops);
            byte[] frame = LibOpusOracle.RangeProbe(EncoderScript(ops, minimum)).Bytes;

            var decoder = new RangeDecoder(frame);
            var ourSteps = new List<LibOpusOracle.RangeProbeStep>(ops.Count);
            for (int i = 0; i < ops.Count; i++)
            {
                bool ok = RangeCoderTestStreams.ApplyDecode(ref decoder, ops[i], out uint observed);
                string context = $"seed {seed}, step {i} ({ops[i].Kind})";
                if (!ok)
                {
                    Assert.Fail($"{context}: our decoder read {observed} from the libopus frame.");
                }

                ourSteps.Add(new LibOpusOracle.RangeProbeStep(
                    observed, decoder.Tell(), decoder.TellFrac(), decoder.Rng, decoder.Error));
            }

            LibOpusOracle.RangeProbeResult probe =
                LibOpusOracle.RangeProbe(DecoderScript(frame, ops));
            if (probe.Steps.Count != ourSteps.Count)
            {
                Assert.Fail(
                    $"seed {seed}: probe reported {probe.Steps.Count} values, expected {ourSteps.Count}.");
            }

            for (int i = 0; i < ourSteps.Count; i++)
            {
                string context = $"seed {seed}, step {i} ({ops[i].Kind})";
                if (probe.Steps[i].Value != ourSteps[i].Value)
                {
                    Assert.Fail(
                        $"{context}: decoded {ourSteps[i].Value}, libopus decoded {probe.Steps[i].Value}.");
                }

                AssertState(probe.Steps[i], ourSteps[i], context);
            }
        }
    }

    [Fact]
    public void Decoder_on_garbage_matches_libopus_including_uint_saturation()
    {
        if (!OracleTestSupport.RangeProbeAvailable())
        {
            return;
        }

        for (int seed = 0; seed < GarbageSeedCount; seed++)
        {
            var random = new Random(seed + 700_000);
            int length = random.Next(0, 65);
            var frame = new byte[length];
            random.NextBytes(frame);
            List<RangeOp> ops = RangeCoderTestStreams.Generate(new Random(seed + 800_000));
            AssertDecoderMatchesProbe(frame, ops, $"garbage seed {seed} (length {length})");
        }

        // A frame of all ones drives DecodeUInt past its range, which must set the error flag
        // and saturate at ft - 1 in both decoders.
        var saturated = new byte[8];
        Array.Fill(saturated, (byte)0xFF);
        List<RangeOp> saturationOps =
        [
            new RangeOp(RangeOpKind.UInt, Ft: 1001),
            new RangeOp(RangeOpKind.UInt, Ft: 1u << 20),
            new RangeOp(RangeOpKind.Bits, Bits: 8),
            new RangeOp(RangeOpKind.Encode, Fl: 0, Fh: 1, Ft: 4),
        ];

        var saturationDecoder = new RangeDecoder(saturated);
        uint saturatedValue = saturationDecoder.DecodeUInt(1001);
        if (!saturationDecoder.Error || saturatedValue != 1000)
        {
            Assert.Fail(
                $"all-ones frame: DecodeUInt(1001) returned {saturatedValue}, error {saturationDecoder.Error}.");
        }

        AssertDecoderMatchesProbe(saturated, saturationOps, "all-ones saturation");
    }

    [Fact]
    public void Shrink_matches_libopus_and_keeps_raw_bits_at_the_end()
    {
        if (!OracleTestSupport.RangeProbeAvailable())
        {
            return;
        }

        const int initialStorage = 4096;
        for (int seed = 0; seed < ShrinkSeedCount; seed++)
        {
            List<RangeOp> ops = RangeCoderTestStreams.Generate(new Random(seed + 900_000));
            int finalSize = MeasureMinimumStorage(ops);
            if (finalSize >= initialStorage)
            {
                Assert.Fail($"seed {seed}: the stream is larger than the shrink test buffer.");
            }

            int first = ops.Count / 3;
            int second = (2 * ops.Count) / 3;
            int firstSize = Math.Min(initialStorage, finalSize + 16);

            var ours = new byte[initialStorage];
            var encoder = new RangeEncoder(ours);
            var ourSteps = new List<LibOpusOracle.RangeProbeStep>();
            for (int i = 0; i < first; i++)
            {
                RangeCoderTestStreams.ApplyEncode(ref encoder, ops[i]);
                ourSteps.Add(EncoderState(encoder));
            }

            encoder.Shrink(firstSize);
            ourSteps.Add(EncoderState(encoder));
            for (int i = first; i < second; i++)
            {
                RangeCoderTestStreams.ApplyEncode(ref encoder, ops[i]);
                ourSteps.Add(EncoderState(encoder));
            }

            encoder.Shrink(finalSize);
            ourSteps.Add(EncoderState(encoder));
            for (int i = second; i < ops.Count; i++)
            {
                RangeCoderTestStreams.ApplyEncode(ref encoder, ops[i]);
                ourSteps.Add(EncoderState(encoder));
            }

            encoder.Done();
            ourSteps.Add(EncoderState(encoder));

            string context = $"shrink seed {seed}";
            if (encoder.Error)
            {
                Assert.Fail($"{context}: our encoder reported an error.");
            }

            var script = new StringBuilder();
            script.Append(CultureInfo.InvariantCulture, $"enc {initialStorage}\n");
            for (int i = 0; i < first; i++)
            {
                AppendEncode(script, ops[i]);
            }

            script.Append(CultureInfo.InvariantCulture, $"s {firstSize}\n");
            for (int i = first; i < second; i++)
            {
                AppendEncode(script, ops[i]);
            }

            script.Append(CultureInfo.InvariantCulture, $"s {finalSize}\n");
            for (int i = second; i < ops.Count; i++)
            {
                AppendEncode(script, ops[i]);
            }

            script.Append("done\n");

            LibOpusOracle.RangeProbeResult probe = LibOpusOracle.RangeProbe(script.ToString());
            if (probe.Bytes.Length != finalSize)
            {
                Assert.Fail($"{context}: probe produced {probe.Bytes.Length} bytes, expected {finalSize}.");
            }

            if (!probe.Bytes.AsSpan().SequenceEqual(ours.AsSpan(0, finalSize)))
            {
                Assert.Fail(
                    $"{context}: bytes after shrink differ.\nprobe {Convert.ToHexString(probe.Bytes)}\nours  {Convert.ToHexString(ours.AsSpan(0, finalSize))}");
            }

            if (probe.Steps.Count != ourSteps.Count)
            {
                Assert.Fail($"{context}: probe reported {probe.Steps.Count} steps, expected {ourSteps.Count}.");
            }

            for (int i = 0; i < ourSteps.Count; i++)
            {
                AssertState(probe.Steps[i], ourSteps[i], $"{context}, step {i}");
            }
        }
    }

    [Fact]
    public void Hand_vectors_match_the_libopus_probe()
    {
        if (!OracleTestSupport.RangeProbeAvailable())
        {
            return;
        }

        var cases = new (string Name, string Script, byte[] Expected)[]
        {
            (
                "single symbol",
                "enc 4\ne 2 3 4\ndone\n",
                [0x80, 0x00, 0x00, 0x00]),
            (
                "carry propagation",
                "enc 8\ne 0 1 4\ne 3 4 8\ne 0 7 8\ne 3 4 8\ndone\n",
                [0x1B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
            (
                "raw bits in the gap",
                "enc 2\ne 2 3 4\nr 5 3\ndone\n",
                [0x80, 0x05]),
            (
                "raw bits only",
                "enc 3\nr 11 5\ndone\n",
                [0x00, 0x00, 0x0B]),
        };

        foreach ((string name, string script, byte[] expected) in cases)
        {
            LibOpusOracle.RangeProbeResult probe = LibOpusOracle.RangeProbe(script);
            if (!probe.Bytes.AsSpan().SequenceEqual(expected))
            {
                Assert.Fail(
                    $"{name}: libopus produced {Convert.ToHexString(probe.Bytes)}, the vector says {Convert.ToHexString(expected)}.");
            }

            foreach (LibOpusOracle.RangeProbeStep step in probe.Steps)
            {
                if (step.Error)
                {
                    Assert.Fail($"{name}: libopus reported an error.");
                }
            }
        }
    }

    private static void AssertDecoderMatchesProbe(
        byte[] frame, IReadOnlyList<RangeOp> ops, string label)
    {
        var decoder = new RangeDecoder(frame);
        var ourSteps = new List<LibOpusOracle.RangeProbeStep>(ops.Count);
        for (int i = 0; i < ops.Count; i++)
        {
            RangeCoderTestStreams.ApplyDecode(ref decoder, ops[i], out uint observed);
            ourSteps.Add(new LibOpusOracle.RangeProbeStep(
                observed, decoder.Tell(), decoder.TellFrac(), decoder.Rng, decoder.Error));
        }

        LibOpusOracle.RangeProbeResult probe = LibOpusOracle.RangeProbe(DecoderScript(frame, ops));
        if (probe.Steps.Count != ourSteps.Count)
        {
            Assert.Fail($"{label}: probe reported {probe.Steps.Count} values, expected {ourSteps.Count}.");
        }

        for (int i = 0; i < ourSteps.Count; i++)
        {
            string context = $"{label}, step {i} ({ops[i].Kind})";
            if (probe.Steps[i].Value != ourSteps[i].Value)
            {
                Assert.Fail(
                    $"{context}: decoded {ourSteps[i].Value}, libopus decoded {probe.Steps[i].Value}.");
            }

            AssertState(probe.Steps[i], ourSteps[i], context);
        }
    }

    /// <summary>
    /// Returns the smallest storage the libopus probe accepts for the stream.
    /// </summary>
    /// <remarks>
    /// With room to spare, the encoder gives the partial raw-bit byte a byte of its own, so its
    /// reported frame size is one more than the tightest possible frame. When the unused low bits
    /// of the last range byte can absorb those raw bits, one byte less is enough. The probe is the
    /// authority on whether that tighter size is accepted, so the tests do not merely ask the
    /// implementation under test how small a frame it can make.
    /// </remarks>
    private static int MeasureMinimumStorage(List<RangeOp> ops)
    {
        var buffer = new byte[GenerousBuffer];
        var encoder = new RangeEncoder(buffer);
        foreach (RangeOp op in ops)
        {
            RangeCoderTestStreams.ApplyEncode(ref encoder, op);
        }

        encoder.Done();
        Assert.False(encoder.Error);
        int frame = encoder.RangeBytes;
        int candidate = Math.Max(0, frame - 1);
        if (candidate == 0)
        {
            return frame;
        }

        LibOpusOracle.RangeProbeResult probe = LibOpusOracle.RangeProbe(EncoderScript(ops, candidate));
        foreach (LibOpusOracle.RangeProbeStep step in probe.Steps)
        {
            if (step.Error)
            {
                return frame;
            }
        }

        return candidate;
    }

    private static LibOpusOracle.RangeProbeStep EncoderState(RangeEncoder encoder) =>
        new(0, encoder.Tell(), encoder.TellFrac(), encoder.Rng, encoder.Error);

    private static void AssertState(
        LibOpusOracle.RangeProbeStep expected, LibOpusOracle.RangeProbeStep actual, string context)
    {
        if (actual.Tell != expected.Tell)
        {
            Assert.Fail($"{context}: tell {actual.Tell}, libopus {expected.Tell}.");
        }

        if (actual.TellFrac != expected.TellFrac)
        {
            Assert.Fail($"{context}: tellfrac {actual.TellFrac}, libopus {expected.TellFrac}.");
        }

        if (actual.Rng != expected.Rng)
        {
            Assert.Fail($"{context}: rng {actual.Rng}, libopus {expected.Rng}.");
        }

        if (actual.Error != expected.Error)
        {
            Assert.Fail($"{context}: error {actual.Error}, libopus {expected.Error}.");
        }
    }

    private static string EncoderScript(IReadOnlyList<RangeOp> ops, int storage)
    {
        var script = new StringBuilder();
        script.Append(CultureInfo.InvariantCulture, $"enc {storage}\n");
        foreach (RangeOp op in ops)
        {
            AppendEncode(script, op);
        }

        script.Append("done\n");
        return script.ToString();
    }

    private static string DecoderScript(byte[] frame, IReadOnlyList<RangeOp> ops)
    {
        var script = new StringBuilder();
        script.Append(CultureInfo.InvariantCulture, $"dec {Convert.ToHexString(frame)}\n");
        foreach (RangeOp op in ops)
        {
            AppendDecode(script, op);
        }

        script.Append("done\n");
        return script.ToString();
    }

    private static void AppendEncode(StringBuilder script, RangeOp op)
    {
        switch (op.Kind)
        {
            case RangeOpKind.Encode:
                script.Append(CultureInfo.InvariantCulture, $"e {op.Fl} {op.Fh} {op.Ft}\n");
                break;
            case RangeOpKind.EncodeBin:
                script.Append(CultureInfo.InvariantCulture, $"b {op.Fl} {op.Fh} {op.Bits}\n");
                break;
            case RangeOpKind.BitLogp:
                script.Append(CultureInfo.InvariantCulture, $"l {op.Value} {op.Logp}\n");
                break;
            case RangeOpKind.Icdf:
                script.Append(CultureInfo.InvariantCulture, $"i {op.Symbol} {op.Bits}");
                AppendIcdf(script, op);
                break;
            case RangeOpKind.Bits:
                script.Append(CultureInfo.InvariantCulture, $"r {op.Value} {op.Bits}\n");
                break;
            case RangeOpKind.UInt:
                script.Append(CultureInfo.InvariantCulture, $"u {op.Value} {op.Ft}\n");
                break;
            default:
                throw new InvalidOperationException($"Unknown operation kind {op.Kind}.");
        }
    }

    private static void AppendDecode(StringBuilder script, RangeOp op)
    {
        switch (op.Kind)
        {
            case RangeOpKind.Encode:
                script.Append(CultureInfo.InvariantCulture, $"e {op.Fl} {op.Fh} {op.Ft}\n");
                break;
            case RangeOpKind.EncodeBin:
                script.Append(CultureInfo.InvariantCulture, $"b {op.Fl} {op.Fh} {op.Bits}\n");
                break;
            case RangeOpKind.BitLogp:
                script.Append(CultureInfo.InvariantCulture, $"l {op.Logp}\n");
                break;
            case RangeOpKind.Icdf:
                script.Append(CultureInfo.InvariantCulture, $"i {op.Bits}");
                AppendIcdf(script, op);
                break;
            case RangeOpKind.Bits:
                script.Append(CultureInfo.InvariantCulture, $"r {op.Bits}\n");
                break;
            case RangeOpKind.UInt:
                script.Append(CultureInfo.InvariantCulture, $"u {op.Ft}\n");
                break;
            default:
                throw new InvalidOperationException($"Unknown operation kind {op.Kind}.");
        }
    }

    private static void AppendIcdf(StringBuilder script, RangeOp op)
    {
        byte[] table = op.Icdf ?? throw new InvalidOperationException("An icdf operation has no table.");
        foreach (byte entry in table)
        {
            script.Append(' ').Append(entry.ToString(CultureInfo.InvariantCulture));
        }

        script.Append('\n');
    }
}
