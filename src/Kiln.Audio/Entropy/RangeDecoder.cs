using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln.Audio.Entropy;

/// <summary>
/// The Opus range decoder of RFC 6716 Section 4.1.
/// </summary>
/// <remarks>
/// The decoder owns no allocations: it reads the caller's frame span in place and keeps all
/// of its state in fields. Reading past the end of the range-coded bytes yields zero bits, as
/// required by RFC 6716 §4.1.2.1, so no bounds exception is raised on the hot path.
/// </remarks>
internal ref struct RangeDecoder
{
    /// <summary>Number of bits in a renormalization symbol (RFC 6716 §4.1.1, EC_SYM_BITS).</summary>
    public const int EcSymBits = 8;

    /// <summary>Number of bits in the code value (RFC 6716 §4.1.1, EC_CODE_BITS).</summary>
    public const int EcCodeBits = 32;

    /// <summary>Largest value of a renormalization symbol (RFC 6716 §4.1.1, EC_SYM_MAX).</summary>
    public const uint EcSymMax = 255;

    /// <summary>Top of the code value range (RFC 6716 §4.1.1, EC_CODE_TOP).</summary>
    public const uint EcCodeTop = 1u << 31;

    /// <summary>Bottom of the normalized range (RFC 6716 §4.1.1, EC_CODE_BOT = EC_CODE_TOP >> 8).</summary>
    public const uint EcCodeBot = EcCodeTop >> EcSymBits;

    /// <summary>Number of low bits carried between renormalization bytes (RFC 6716 §4.1.1, EC_CODE_EXTRA).</summary>
    public const int EcCodeExtra = 7;

    /// <summary>Number of high bits range-coded by <see cref="DecodeUInt"/> (RFC 6716 §4.1.5, EC_UINT_BITS).</summary>
    public const int EcUintBits = 8;

    /// <summary>Number of fractional bits reported by <see cref="TellFrac"/> (RFC 6716 §4.1.6.2, BITRES).</summary>
    public const int BitRes = 3;

    private readonly ReadOnlySpan<byte> _frame;
    private int _storageBytes;
    private int _offs;
    private int _endOffs;
    private uint _endWindow;
    private int _nendBits;
    private int _nbitsTotal;
    private uint _rng;
    private uint _val;
    private uint _ext;
    private int _rem;
    private bool _error;

    /// <summary>
    /// Initializes the decoder over a range-coded frame (RFC 6716 §4.1.1).
    /// </summary>
    /// <param name="frame">The range-coded portion of the packet.</param>
    public RangeDecoder(ReadOnlySpan<byte> frame)
    {
        _frame = frame;
        _storageBytes = frame.Length;
        _offs = 0;
        _endOffs = 0;
        _endWindow = 0;
        _nendBits = 0;
        // RFC 6716 §4.1.6: nine bits are buffered before the initial renormalization.
        _nbitsTotal = EcCodeBits + 1 - ((EcCodeBits - EcCodeExtra) / EcSymBits) * EcSymBits;
        _rng = 1u << EcCodeExtra;
        _val = 0;
        _ext = 0;
        _error = false;

        _rem = ReadByte();
        _val = _rng - 1u - (uint)(_rem >> (EcSymBits - EcCodeExtra));
        Normalize();
    }

    /// <summary>Gets a value indicating whether the stream was found to be corrupt (RFC 6716 §4.1.5).</summary>
    public readonly bool Error => _error;

    /// <summary>Gets the current range size (RFC 6716 §4.1.2, <c>rng</c>).</summary>
    public readonly uint Rng => _rng;

    /// <summary>Gets the number of bytes the coder may read from (RFC 6716 §4.1).</summary>
    public readonly int StorageBytes => _storageBytes;

    /// <summary>
    /// Hides the last <paramref name="bytes"/> bytes of the frame from both range-coded and raw
    /// bit reads, as the reference decoder does for a hybrid frame's trailing redundant CELT
    /// frame (RFC 6716 §4.5.1.3: <c>dec.storage -= redundancy_bytes</c>).
    /// </summary>
    /// <param name="bytes">Number of trailing bytes to hide.</param>
    public void TrimStorage(int bytes)
    {
        if (bytes < 0 || bytes > _storageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "The trim must fit inside the frame.");
        }

        _storageBytes -= bytes;
    }

    /// <summary>
    /// Returns the number of whole bits used so far (RFC 6716 §4.1.6.1).
    /// </summary>
    /// <returns><c>nbits_total - ilog(rng)</c>.</returns>
    public readonly int Tell() => _nbitsTotal - Ilog(_rng);

    /// <summary>
    /// Returns the number of bits used so far in units of 1/8 bit (RFC 6716 §4.1.6.2).
    /// </summary>
    /// <returns><c>nbits_total*8 - lg</c>, where <c>lg</c> has three fractional bits.</returns>
    public readonly uint TellFrac()
    {
        // PERF-7: the three fractional bits are a monotone function of the 16-bit normalized
        // range, so they are the number of precomputed thresholds the range has reached. This
        // replaces the three dependent multiply/shift rounds of the reference loop (identical
        // result; see TellFracReference and the exhaustive test against it).
        uint rng = _rng;
        if (rng > 0xFFFF && TellTable.Monotone)
        {
            int lg = Ilog(rng);
            uint rQ15 = rng >> (lg - 16);
            ref uint t = ref MemoryMarshal.GetArrayDataReference(TellTable.Thresholds);
            int frac = (rQ15 >= t ? 1 : 0)
                + (rQ15 >= Unsafe.Add(ref t, 1) ? 1 : 0)
                + (rQ15 >= Unsafe.Add(ref t, 2) ? 1 : 0)
                + (rQ15 >= Unsafe.Add(ref t, 3) ? 1 : 0)
                + (rQ15 >= Unsafe.Add(ref t, 4) ? 1 : 0)
                + (rQ15 >= Unsafe.Add(ref t, 5) ? 1 : 0)
                + (rQ15 >= Unsafe.Add(ref t, 6) ? 1 : 0);
            return (uint)((_nbitsTotal * 8) - ((lg << BitRes) + frac));
        }

        return TellFracReference();
    }

    /// <summary>Overrides the range, for exhaustive tests of the tell computation.</summary>
    internal void SetRngForTest(uint rng) => _rng = rng;

    /// <summary>The reference fractional-bit loop of RFC 6716 §4.1.6.2.</summary>
    internal readonly uint TellFracReference()
    {
        int lg = Ilog(_rng);
        // r_Q15 is a Q15 representation of the fractional part of rng, in [32768, 65536).
        uint rQ15 = _rng >> (lg - 16);
        for (int i = 0; i < BitRes; i++)
        {
            rQ15 = (rQ15 * rQ15) >> 15;
            uint bit = rQ15 >> 16;
            lg = (lg << 1) + (int)bit;
            if (bit != 0)
            {
                rQ15 >>= 1;
            }
        }

        return (uint)((_nbitsTotal * 8) - lg);
    }

    /// <summary>Returns the fractional bits the reference loop yields for a Q15 range.</summary>
    internal static int TellFraction(uint rQ15)
    {
        int lg = 0;
        for (int i = 0; i < BitRes; i++)
        {
            rQ15 = (rQ15 * rQ15) >> 15;
            uint bit = rQ15 >> 16;
            lg = (lg << 1) + (int)bit;
            if (bit != 0)
            {
                rQ15 >>= 1;
            }
        }

        return lg;
    }

    private static class TellTable
    {
        internal static readonly uint[] Thresholds = new uint[7];

        internal static readonly bool Monotone = Build();

        private static bool Build()
        {
            int previous = 0;
            int next = 1;
            for (uint r = 32768; r < 65536; r++)
            {
                int f = TellFraction(r);
                if (f < previous || f > 7)
                {
                    return false;
                }

                while (next <= f)
                {
                    Thresholds[next - 1] = r;
                    next++;
                }

                previous = f;
            }

            return next == 8;
        }
    }

    /// <summary>
    /// Computes the first step of symbol decoding: the value <c>fs</c> in [0, ft) (RFC 6716 §4.1.2).
    /// </summary>
    /// <remarks>
    /// The caller must find the symbol <c>k</c> with <c>fl[k] &lt;= fs &lt; fh[k]</c> and then call
    /// <see cref="Update"/> with that symbol's three-tuple and the same <paramref name="ft"/>.
    /// </remarks>
    /// <param name="ft">Total frequency, at most 65535.</param>
    /// <returns>The scaled value <c>fs</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Decode(uint ft)
    {
        if (ft == 0)
        {
            _error = true;
            return 0;
        }

        _ext = _rng / ft;
        if (_ext == 0)
        {
            // Invalid context: ft exceeds rng. Mark the stream and leave the state usable.
            _error = true;
            return 0;
        }

        uint scaled = _val / _ext;
        uint limited = scaled + 1;
        if (limited > ft)
        {
            limited = ft;
        }

        return ft - limited;
    }

    /// <summary>
    /// Computes <c>fs</c> for a context whose total frequency is <c>1 &lt;&lt; bits</c>
    /// (RFC 6716 §4.1.3.1).
    /// </summary>
    /// <param name="bits">The base-2 logarithm of the total frequency.</param>
    /// <returns>The scaled value <c>fs</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeBin(int bits)
    {
        _ext = _rng >> bits;
        if (_ext == 0)
        {
            _error = true;
            return 0;
        }

        uint ft = 1u << bits;
        uint scaled = _val / _ext;
        uint limited = scaled + 1;
        if (limited > ft)
        {
            limited = ft;
        }

        return ft - limited;
    }

    /// <summary>
    /// Applies the second step of symbol decoding and renormalizes (RFC 6716 §4.1.2 and §4.1.2.1).
    /// </summary>
    /// <param name="fl">Lower cumulative frequency of the symbol.</param>
    /// <param name="fh">Upper cumulative frequency of the symbol.</param>
    /// <param name="ft">Total frequency, equal to the value passed to the preceding <see cref="Decode"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update(uint fl, uint fh, uint ft)
    {
        if (_ext == 0)
        {
            _error = true;
            return;
        }

        uint delta = _ext * (ft - fh);
        _val -= delta;
        if (fl > 0)
        {
            _rng = _ext * (fh - fl);
        }
        else
        {
            _rng -= delta;
        }

        Normalize();
    }

    /// <summary>
    /// Decodes a single binary symbol whose probability of a "1" is <c>2^-logp</c>
    /// (RFC 6716 §4.1.3.2).
    /// </summary>
    /// <param name="logp">The absolute base-2 logarithm of the probability of a "1", 1 to 15.</param>
    /// <returns><see langword="true"/> if a "1" was decoded.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool DecodeBitLogp(int logp)
    {
        uint s = _rng >> logp;
        bool one = _val < s;
        if (!one)
        {
            _val -= s;
        }

        _rng = one ? s : _rng - s;
        Normalize();
        return one;
    }

    /// <summary>
    /// Decodes a symbol from an inverse cumulative distribution table (RFC 6716 §4.1.3.3).
    /// </summary>
    /// <remarks>
    /// <paramref name="icdf"/> is the table described in §4.1.3.3: entry <c>k</c> is
    /// <c>(1 &lt;&lt; ftb) - fh[k]</c>, the entries are strictly decreasing, and the table is
    /// terminated by zero.
    /// </remarks>
    /// <param name="icdf">The inverse CDF table.</param>
    /// <param name="ftb">The base-2 logarithm of the total frequency, at most 8.</param>
    /// <returns>The index of the decoded symbol.</returns>
    public int DecodeIcdf(ReadOnlySpan<byte> icdf, int ftb)
    {
        uint r = _rng >> ftb;
        uint d = _val;
        uint s = _rng;
        int ret = -1;
        uint previous = s;
        while (true)
        {
            ret++;
            if ((uint)ret >= (uint)icdf.Length)
            {
                // Malformed table with no terminating zero; stop rather than read out of bounds.
                _error = true;
                return ret;
            }

            previous = s;
            s = r * icdf[ret];
            if (d >= s)
            {
                break;
            }
        }

        _val = d - s;
        _rng = previous - s;
        Normalize();
        return ret;
    }

    /// <summary>
    /// Decodes a symbol from a forward probability table whose total frequency is 256
    /// (RFC 6716 §4.1.3.3), without the integer divide the generic path needs.
    /// </summary>
    /// <param name="pdf">
    /// The probability table: the entries sum to 256 and the symbol <c>k</c> occupies
    /// <c>[fl[k], fl[k]+pdf[k])</c>.
    /// </param>
    /// <returns>The decoded symbol, or the last index for a malformed table.</returns>
    /// <remarks>
    /// The generic <see cref="Decode"/> path computes <c>fs</c> with <c>val / (rng/256)</c>.
    /// Inverting the search condition lets the loop compare <c>val</c> against
    /// <c>(256 - fh[k]) * (rng/256)</c> instead, which trades one hardware divide for one
    /// multiply per candidate symbol. The arithmetic is identical, so the symbol and the
    /// state update are bit-for-bit those of the divide-based path.
    /// </remarks>
    public int DecodePdf(ReadOnlySpan<byte> pdf)
    {
        uint ext = _rng >> 8;
        if (ext == 0)
        {
            _error = true;
            _ext = 0;
            return pdf.Length - 1;
        }

        _ext = ext;
        uint d = _val;
        uint fl = 0;
        uint fh = 0;
        int symbol = -1;
        for (int k = 0; k < pdf.Length; k++)
        {
            fl = fh;
            fh += pdf[k];
            if (fh == 0)
            {
                // A zero-probability symbol can never be selected; the comparison below would
                // otherwise mis-handle the one code value that saturates the range.
                continue;
            }

            if (d >= (256u - fh) * ext)
            {
                symbol = k;
                break;
            }
        }

        if (symbol < 0)
        {
            // The table sums to less than 256: take the last symbol rather than stall, exactly
            // as the generic path does.
            Update(fh, 256, 256);
            return pdf.Length - 1;
        }

        Update(fl, fh, 256);
        return symbol;
    }

    /// <summary>
    /// Decodes raw bits packed LSB-first from the end of the frame (RFC 6716 §4.1.4).
    /// </summary>
    /// <param name="bits">The number of bits to read, 1 to 25.</param>
    /// <returns>The decoded value, with the first bit in the least significant position.</returns>
    public uint DecodeBits(int bits)
    {
        if (_nendBits < bits)
        {
            while (_nendBits < bits)
            {
                _endWindow |= (uint)ReadByteFromEnd() << _nendBits;
                _nendBits += EcSymBits;
            }
        }

        uint mask = (1u << bits) - 1u;
        uint ret = _endWindow & mask;
        _endWindow >>= bits;
        _nendBits -= bits;
        _nbitsTotal += bits;
        return ret;
    }

    /// <summary>
    /// Decodes one of <paramref name="ft"/> equiprobable integers (RFC 6716 §4.1.5).
    /// </summary>
    /// <remarks>
    /// The high eight bits are range-coded and the remainder are raw bits. If the result is
    /// outside [0, ft), <see cref="Error"/> is set and <c>ft - 1</c> is returned.
    /// </remarks>
    /// <param name="ft">The number of equiprobable values, at least 2.</param>
    /// <returns>The decoded integer.</returns>
    public uint DecodeUInt(uint ft)
    {
        uint f = ft - 1;
        int ftb = Ilog(f);
        if (ftb > EcUintBits)
        {
            int highBits = ftb - EcUintBits;
            uint fr = (f >> highBits) + 1;
            uint t = Decode(fr);
            Update(t, t + 1, fr);
            t = (t << highBits) | DecodeBits(highBits);
            if (t <= f)
            {
                return t;
            }

            _error = true;
            return f;
        }
        else
        {
            uint t = Decode(ft);
            Update(t, t + 1, ft);
            return t;
        }
    }

    /// <summary>
    /// Renormalizes the range decoder (RFC 6716 §4.1.2.1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize()
    {
        // Renormalization is required only when the range has fallen to or below BOT. Most
        // symbols leave it above BOT, so keep that common case branch-only.
        if (_rng > EcCodeBot)
        {
            return;
        }

        while (_rng <= EcCodeBot)
        {
            if (_rng == 0)
            {
                _error = true;
                return;
            }

            _nbitsTotal += EcSymBits;
            _rng <<= EcSymBits;

            // RFC 6716 §4.1.2.1: the leftover bit of the previous byte becomes the high bit of
            // sym; the top seven bits of the next byte supply the rest.
            int leftover = _rem & 1;
            int next = ReadByte();
            int sym = (leftover << 7) | (next >> 1);
            _rem = next;
            _val = ((_val << EcSymBits) + (EcSymMax - (uint)sym)) & (EcCodeTop - 1);
        }
    }

    /// <summary>Reads the next range-coded byte, or zero past the end of the frame (RFC 6716 §4.1.2.1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadByte()
    {
        int index = _offs;
        if (index < _storageBytes)
        {
            _offs = index + 1;

            // _storageBytes never exceeds the frame's length, so this read is inside the span.
            return Unsafe.Add(ref MemoryMarshal.GetReference(_frame), index);
        }

        return 0;
    }

    /// <summary>Reads the next raw bit byte from the end of the frame, or zero past the end (RFC 6716 §4.1.4).</summary>
    private byte ReadByteFromEnd()
    {
        if (_endOffs < _storageBytes)
        {
            _endOffs++;
            return _frame[_storageBytes - _endOffs];
        }

        return 0;
    }

    /// <summary>Returns <c>ilog(x)</c>: the number of bits needed to represent <c>x</c>, for <c>x &gt; 0</c>.</summary>
    private static int Ilog(uint x) => BitOperations.Log2(x) + 1;
}
