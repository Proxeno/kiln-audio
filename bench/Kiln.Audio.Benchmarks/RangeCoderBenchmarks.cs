using BenchmarkDotNet.Attributes;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Benchmarks;

/// <summary>Decodes and encodes a fixed 1000-symbol mixed stream (RFC 6716 §4.1 and §5.1).</summary>
[MemoryDiagnoser]
public class RangeCoderBenchmarks
{
    private const int SymbolCount = 1000;
    private const int BufferSize = 1 << 14;

    private static readonly byte[] IcdfTable = [200, 150, 90, 40, 0];
    private static readonly BenchOp[] Stream = BuildStream();
    private static readonly byte[] Frame = BuildFrame();

    private readonly byte[] _encodeBuffer = new byte[BufferSize];

    /// <summary>Encodes the fixed stream.</summary>
    /// <returns>The number of bytes written.</returns>
    [Benchmark]
    public int Encode()
    {
        var encoder = new RangeEncoder(_encodeBuffer);
        foreach (BenchOp op in Stream)
        {
            switch (op.Kind)
            {
                case BenchKind.Encode:
                    encoder.Encode(op.Fl, op.Fh, op.Ft);
                    break;
                case BenchKind.EncodeBin:
                    encoder.EncodeBin(op.Fl, op.Fh, op.Bits);
                    break;
                case BenchKind.BitLogp:
                    encoder.EncodeBitLogp((int)op.Value, op.Logp);
                    break;
                case BenchKind.Icdf:
                    encoder.EncodeIcdf(IcdfTable, op.Bits, op.Symbol);
                    break;
                case BenchKind.Bits:
                    encoder.EncodeBits(op.Value, op.Bits);
                    break;
                default:
                    encoder.EncodeUInt(op.Value, op.Ft);
                    break;
            }
        }

        encoder.Done();
        return encoder.RangeBytes;
    }

    /// <summary>Decodes the fixed stream.</summary>
    /// <returns>The final decoder bit position.</returns>
    [Benchmark]
    public int Decode()
    {
        var decoder = new RangeDecoder(Frame);
        uint accumulator = 0;
        foreach (BenchOp op in Stream)
        {
            switch (op.Kind)
            {
                case BenchKind.Encode:
                    accumulator += decoder.Decode(op.Ft);
                    decoder.Update(op.Fl, op.Fh, op.Ft);
                    break;
                case BenchKind.EncodeBin:
                    accumulator += decoder.DecodeBin(op.Bits);
                    decoder.Update(op.Fl, op.Fh, 1u << op.Bits);
                    break;
                case BenchKind.BitLogp:
                    accumulator += decoder.DecodeBitLogp(op.Logp) ? 1u : 0u;
                    break;
                case BenchKind.Icdf:
                    accumulator += (uint)decoder.DecodeIcdf(IcdfTable, op.Bits);
                    break;
                case BenchKind.Bits:
                    accumulator += decoder.DecodeBits(op.Bits);
                    break;
                default:
                    accumulator += decoder.DecodeUInt(op.Ft);
                    break;
            }
        }

        return decoder.Tell() + (int)accumulator;
    }

    private static BenchOp[] BuildStream()
    {
        var ops = new BenchOp[SymbolCount];
        for (int i = 0; i < SymbolCount; i++)
        {
            int phase = i % 6;
            ops[i] = phase switch
            {
                0 => new BenchOp(BenchKind.Encode, Fl: (uint)(i % 4), Fh: (uint)(i % 4 + 1), Ft: 8),
                1 => new BenchOp(BenchKind.EncodeBin, Fl: 1, Fh: 2, Bits: 3),
                2 => new BenchOp(BenchKind.BitLogp, Value: (uint)(i & 1), Logp: 3),
                3 => new BenchOp(BenchKind.Icdf, Bits: 8, Symbol: i % 4),
                4 => new BenchOp(BenchKind.Bits, Value: (uint)(i * 7 & 0xFF), Bits: 8),
                _ => new BenchOp(BenchKind.UInt, Ft: 1000, Value: (uint)(i * 13 % 1000)),
            };
        }

        return ops;
    }

    private static byte[] BuildFrame()
    {
        var buffer = new byte[BufferSize];
        var encoder = new RangeEncoder(buffer);
        foreach (BenchOp op in Stream)
        {
            switch (op.Kind)
            {
                case BenchKind.Encode:
                    encoder.Encode(op.Fl, op.Fh, op.Ft);
                    break;
                case BenchKind.EncodeBin:
                    encoder.EncodeBin(op.Fl, op.Fh, op.Bits);
                    break;
                case BenchKind.BitLogp:
                    encoder.EncodeBitLogp((int)op.Value, op.Logp);
                    break;
                case BenchKind.Icdf:
                    encoder.EncodeIcdf(IcdfTable, op.Bits, op.Symbol);
                    break;
                case BenchKind.Bits:
                    encoder.EncodeBits(op.Value, op.Bits);
                    break;
                default:
                    encoder.EncodeUInt(op.Value, op.Ft);
                    break;
            }
        }

        encoder.Done();
        return buffer;
    }

    private enum BenchKind
    {
        Encode,
        EncodeBin,
        BitLogp,
        Icdf,
        Bits,
        UInt,
    }

    private readonly record struct BenchOp(
        BenchKind Kind,
        uint Fl = 0,
        uint Fh = 0,
        uint Ft = 0,
        uint Value = 0,
        int Bits = 0,
        int Logp = 0,
        int Symbol = 0);
}
