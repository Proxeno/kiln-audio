using Kiln.Audio.Entropy;

namespace Kiln.Audio.Tests;

/// <summary>The kind of range-coder operation in a generated test stream.</summary>
internal enum RangeOpKind
{
    /// <summary><see cref="RangeEncoder.Encode"/> with an explicit three-tuple.</summary>
    Encode,

    /// <summary><see cref="RangeEncoder.EncodeBin"/>.</summary>
    EncodeBin,

    /// <summary><see cref="RangeEncoder.EncodeBitLogp"/>.</summary>
    BitLogp,

    /// <summary><see cref="RangeEncoder.EncodeIcdf"/>.</summary>
    Icdf,

    /// <summary><see cref="RangeEncoder.EncodeBits"/>.</summary>
    Bits,

    /// <summary><see cref="RangeEncoder.EncodeUInt"/>.</summary>
    UInt,
}

/// <summary>One symbol in a generated test stream.</summary>
/// <param name="Kind">Which coder operation to apply.</param>
/// <param name="Fl">Lower cumulative frequency for <see cref="RangeOpKind.Encode"/> and <see cref="RangeOpKind.EncodeBin"/>.</param>
/// <param name="Fh">Upper cumulative frequency for <see cref="RangeOpKind.Encode"/> and <see cref="RangeOpKind.EncodeBin"/>.</param>
/// <param name="Ft">Total frequency for <see cref="RangeOpKind.Encode"/> and <see cref="RangeOpKind.UInt"/>.</param>
/// <param name="Value">Encoded bit, raw value or integer value, depending on <paramref name="Kind"/>.</param>
/// <param name="Bits">Bit count or <c>ftb</c>, depending on <paramref name="Kind"/>.</param>
/// <param name="Logp">The <c>logp</c> parameter of <see cref="RangeOpKind.BitLogp"/>.</param>
/// <param name="Symbol">The symbol index for <see cref="RangeOpKind.Icdf"/>.</param>
/// <param name="Icdf">The ICDF table for <see cref="RangeOpKind.Icdf"/>.</param>
internal sealed record RangeOp(
    RangeOpKind Kind,
    uint Fl = 0,
    uint Fh = 0,
    uint Ft = 0,
    uint Value = 0,
    int Bits = 0,
    int Logp = 0,
    int Symbol = 0,
    byte[]? Icdf = null);

/// <summary>Generates and applies mixed range-coder test streams.</summary>
internal static class RangeCoderTestStreams
{
    /// <summary>Total frequencies exercised by <see cref="RangeOpKind.Encode"/>.</summary>
    private static readonly uint[] EncodeTotals = [2, 3, 4, 5, 7, 8, 16, 17, 64, 255, 256, 257, 300];

    /// <summary>Total frequencies exercised by <see cref="RangeOpKind.UInt"/>.</summary>
    private static readonly uint[] UIntTotals =
    [
        2, 3, 4, 255, 256, 257, 258, 1000, 65535, 65536, 65537, 1u << 20, (1u << 31) + 1, uint.MaxValue,
    ];

    /// <summary>Builds a deterministic stream of 1 to 400 mixed operations.</summary>
    /// <param name="random">The seeded random source.</param>
    /// <returns>The generated operations.</returns>
    internal static List<RangeOp> Generate(Random random)
    {
        int count = random.Next(1, 401);
        var ops = new List<RangeOp>(count);
        for (int i = 0; i < count; i++)
        {
            ops.Add(GenerateOp(random));
        }

        return ops;
    }

    /// <summary>Applies one operation to the encoder.</summary>
    /// <param name="encoder">The encoder under test.</param>
    /// <param name="op">The operation to apply.</param>
    internal static void ApplyEncode(ref RangeEncoder encoder, RangeOp op)
    {
        switch (op.Kind)
        {
            case RangeOpKind.Encode:
                encoder.Encode(op.Fl, op.Fh, op.Ft);
                break;
            case RangeOpKind.EncodeBin:
                encoder.EncodeBin(op.Fl, op.Fh, op.Bits);
                break;
            case RangeOpKind.BitLogp:
                encoder.EncodeBitLogp((int)op.Value, op.Logp);
                break;
            case RangeOpKind.Icdf:
                encoder.EncodeIcdf(op.Icdf, op.Bits, op.Symbol);
                break;
            case RangeOpKind.Bits:
                encoder.EncodeBits(op.Value, op.Bits);
                break;
            case RangeOpKind.UInt:
                encoder.EncodeUInt(op.Value, op.Ft);
                break;
            default:
                throw new InvalidOperationException($"Unknown operation kind {op.Kind}.");
        }
    }

    /// <summary>Applies one operation to the decoder and reports whether the decoded value matches.</summary>
    /// <param name="decoder">The decoder under test.</param>
    /// <param name="op">The operation to replay.</param>
    /// <param name="observed">The value the decoder produced.</param>
    /// <returns><see langword="true"/> if the decoder recovered the encoded value.</returns>
    internal static bool ApplyDecode(ref RangeDecoder decoder, RangeOp op, out uint observed)
    {
        switch (op.Kind)
        {
            case RangeOpKind.Encode:
            {
                observed = decoder.Decode(op.Ft);
                bool ok = observed >= op.Fl && observed < op.Fh;
                decoder.Update(op.Fl, op.Fh, op.Ft);
                return ok;
            }

            case RangeOpKind.EncodeBin:
            {
                observed = decoder.DecodeBin(op.Bits);
                bool ok = observed >= op.Fl && observed < op.Fh;
                decoder.Update(op.Fl, op.Fh, 1u << op.Bits);
                return ok;
            }

            case RangeOpKind.BitLogp:
                observed = decoder.DecodeBitLogp(op.Logp) ? 1u : 0u;
                return observed == op.Value;

            case RangeOpKind.Icdf:
                observed = (uint)decoder.DecodeIcdf(op.Icdf, op.Bits);
                return observed == (uint)op.Symbol;

            case RangeOpKind.Bits:
                observed = decoder.DecodeBits(op.Bits);
                return observed == op.Value;

            case RangeOpKind.UInt:
                observed = decoder.DecodeUInt(op.Ft);
                return observed == op.Value;

            default:
                throw new InvalidOperationException($"Unknown operation kind {op.Kind}.");
        }
    }

    private static RangeOp GenerateOp(Random random)
    {
        switch (random.Next(6))
        {
            case 0:
            {
                uint ft = EncodeTotals[random.Next(EncodeTotals.Length)];
                uint fl = (uint)random.Next((int)ft);
                uint fh = fl + 1 + (uint)random.Next((int)(ft - fl - 1) + 1);
                return new RangeOp(RangeOpKind.Encode, Fl: fl, Fh: fh, Ft: ft);
            }

            case 1:
            {
                int bits = random.Next(1, 9);
                uint ft = 1u << bits;
                uint fl = (uint)random.Next((int)ft);
                uint fh = fl + 1 + (uint)random.Next((int)(ft - fl - 1) + 1);
                return new RangeOp(RangeOpKind.EncodeBin, Fl: fl, Fh: fh, Bits: bits);
            }

            case 2:
                return new RangeOp(RangeOpKind.BitLogp, Value: (uint)random.Next(2), Logp: random.Next(1, 16));

            case 3:
            {
                int ftb = random.Next(1, 9);
                byte[] icdf = BuildIcdf(random, ftb);
                int symbol = random.Next(icdf.Length - 1);
                return new RangeOp(RangeOpKind.Icdf, Bits: ftb, Symbol: symbol, Icdf: icdf);
            }

            case 4:
            {
                int bits = random.Next(1, 26);
                uint value = (uint)(random.NextInt64(0, 1L << bits));
                return new RangeOp(RangeOpKind.Bits, Value: value, Bits: bits);
            }

            default:
            {
                uint ft = UIntTotals[random.Next(UIntTotals.Length)];
                uint value = (uint)random.NextInt64(0, ft);
                return new RangeOp(RangeOpKind.UInt, Ft: ft, Value: value);
            }
        }
    }

    /// <summary>
    /// Builds a valid inverse CDF table: strictly decreasing entries
    /// <c>(1 &lt;&lt; ftb) - fh[k]</c>, terminated by zero (RFC 6716 §4.1.3.3).
    /// </summary>
    private static byte[] BuildIcdf(Random random, int ftb)
    {
        int ft = 1 << ftb;
        int symbols = random.Next(2, Math.Min(6, ft) + 1);
        var frequencies = new int[symbols];
        for (int i = 0; i < symbols; i++)
        {
            frequencies[i] = 1;
        }

        for (int i = symbols; i < ft; i++)
        {
            frequencies[random.Next(symbols)]++;
        }

        var table = new byte[symbols];
        int cumulative = 0;
        for (int i = 0; i < symbols; i++)
        {
            cumulative += frequencies[i];
            table[i] = (byte)(ft - cumulative);
        }

        return table;
    }
}
