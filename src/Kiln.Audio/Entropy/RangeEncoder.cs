using System.Numerics;

namespace Kiln.Audio.Entropy;

/// <summary>
/// The Opus range encoder of RFC 6716 Section 5.1.
/// </summary>
/// <remarks>
/// The encoder writes range-coded bytes from the front of the caller's buffer and raw bits from
/// the back, exactly as shown in Figure 12 of RFC 6716. If the two regions do not fit, the write
/// that would overflow sets <see cref="Error"/> and is dropped; no exception is thrown.
/// </remarks>
internal ref struct RangeEncoder
{
    // RFC 6716 §5.1.1 constants, mirrored from the decoder so both halves share one definition.
    private const int SymBits = RangeDecoder.EcSymBits;
    private const uint SymMax = RangeDecoder.EcSymMax;
    private const uint CodeTop = RangeDecoder.EcCodeTop;
    private const uint CodeBot = RangeDecoder.EcCodeBot;
    private const int UintBits = RangeDecoder.EcUintBits;
    private const int BitRes = RangeDecoder.BitRes;

    private readonly Span<byte> _buffer;
    private int _storage;
    private int _offs;
    private int _endOffs;
    private ulong _endWindow;
    private int _nendBits;
    private int _nbitsTotal;
    private uint _val;
    private uint _rng;
    private int _rem;
    private int _ext;
    private bool _error;
    private bool _done;
    private int _frameBytes;

    /// <summary>
    /// Initializes the encoder over a caller-owned output buffer (RFC 6716 §5.1).
    /// </summary>
    /// <param name="buffer">The frame buffer; range-coded bytes fill it from the front and raw bits from the back.</param>
    public RangeEncoder(Span<byte> buffer)
    {
        _buffer = buffer;
        _storage = buffer.Length;
        _offs = 0;
        _endOffs = 0;
        _endWindow = 0;
        _nendBits = 0;
        // RFC 6716 §5.1: the initial state is (val, rng, rem, ext) = (0, 2**31, -1, 0).
        _nbitsTotal = RangeDecoder.EcCodeBits + 1;
        _val = 0;
        _rng = CodeTop;
        _rem = -1;
        _ext = 0;
        _error = false;
        _done = false;
        _frameBytes = 0;
    }

    /// <summary>Gets a value indicating whether the output buffer overflowed (RFC 6716 §5.1.5).</summary>
    public readonly bool Error => _error;

    /// <summary>Gets the current range size (RFC 6716 §5.1.1, <c>rng</c>).</summary>
    public readonly uint Rng => _rng;

    /// <summary>
    /// Gets the number of bytes the frame occupies (range bytes plus raw bytes).
    /// </summary>
    /// <remarks>
    /// Before <see cref="Done"/> this counts the bytes handed to the buffer so far. Once the
    /// stream is finalized it counts the partial raw-bit byte as well.
    /// </remarks>
    public readonly int RangeBytes => _done ? _frameBytes : _offs + _endOffs;

    /// <summary>
    /// Returns the number of whole bits used so far (RFC 6716 §5.1.6).
    /// </summary>
    /// <returns><c>nbits_total - ilog(rng)</c>.</returns>
    public readonly int Tell() => _nbitsTotal - Ilog(_rng);

    /// <summary>
    /// Returns the number of bits used so far in units of 1/8 bit (RFC 6716 §5.1.6).
    /// </summary>
    /// <returns><c>nbits_total*8 - lg</c>, where <c>lg</c> has three fractional bits.</returns>
    public readonly uint TellFrac()
    {
        int lg = Ilog(_rng);
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

    /// <summary>
    /// Encodes symbol <c>k</c> described by the three-tuple (fl, fh, ft) (RFC 6716 §5.1.1).
    /// </summary>
    /// <param name="fl">Lower cumulative frequency of the symbol.</param>
    /// <param name="fh">Upper cumulative frequency of the symbol.</param>
    /// <param name="ft">Total frequency.</param>
    public void Encode(uint fl, uint fh, uint ft)
    {
        if (ft == 0)
        {
            _error = true;
            return;
        }

        uint r = _rng / ft;
        if (r == 0)
        {
            _error = true;
            return;
        }

        if (fl > 0)
        {
            _val += _rng - (r * (ft - fl));
            _rng = r * (fh - fl);
        }
        else
        {
            _rng -= r * (ft - fh);
        }

        Normalize();
    }

    /// <summary>
    /// Encodes a symbol in a context whose total frequency is <c>1 &lt;&lt; bits</c>
    /// (RFC 6716 §5.1.2.1).
    /// </summary>
    /// <param name="fl">Lower cumulative frequency of the symbol.</param>
    /// <param name="fh">Upper cumulative frequency of the symbol.</param>
    /// <param name="bits">The base-2 logarithm of the total frequency.</param>
    public void EncodeBin(uint fl, uint fh, int bits)
    {
        uint r = _rng >> bits;
        if (r == 0)
        {
            _error = true;
            return;
        }

        uint ft = 1u << bits;
        if (fl > 0)
        {
            _val += _rng - (r * (ft - fl));
            _rng = r * (fh - fl);
        }
        else
        {
            _rng -= r * (ft - fh);
        }

        Normalize();
    }

    /// <summary>
    /// Encodes a single binary symbol whose probability of a "1" is <c>2^-logp</c>
    /// (RFC 6716 §5.1.2.2).
    /// </summary>
    /// <param name="bit">The bit to encode; nonzero encodes a "1".</param>
    /// <param name="logp">The absolute base-2 logarithm of the probability of a "1", 1 to 15.</param>
    public void EncodeBitLogp(int bit, int logp)
    {
        uint r = _rng;
        uint s = r >> logp;
        r -= s;
        if (bit != 0)
        {
            _val += r;
        }

        _rng = bit != 0 ? s : r;
        Normalize();
    }

    /// <summary>
    /// Encodes a symbol using an inverse cumulative distribution table (RFC 6716 §5.1.2.3).
    /// </summary>
    /// <remarks>
    /// <paramref name="icdf"/> has the same form as in <see cref="RangeDecoder.DecodeIcdf"/>.
    /// </remarks>
    /// <param name="icdf">The inverse CDF table.</param>
    /// <param name="ftb">The base-2 logarithm of the total frequency, at most 8.</param>
    /// <param name="symbol">The index of the symbol to encode.</param>
    public void EncodeIcdf(ReadOnlySpan<byte> icdf, int ftb, int symbol)
    {
        uint r = _rng >> ftb;
        if (symbol > 0)
        {
            uint upper = icdf[symbol - 1];
            uint lower = icdf[symbol];
            _val += _rng - (r * upper);
            _rng = r * (upper - lower);
        }
        else
        {
            _rng -= r * icdf[0];
        }

        Normalize();
    }

    /// <summary>
    /// Encodes raw bits packed LSB-first at the end of the buffer (RFC 6716 §5.1.3).
    /// </summary>
    /// <param name="value">The value to encode; only the low <paramref name="bits"/> bits are used.</param>
    /// <param name="bits">The number of bits to write, 1 to 25.</param>
    public void EncodeBits(uint value, int bits)
    {
        uint mask = (1u << bits) - 1u;
        _endWindow |= (ulong)(value & mask) << _nendBits;
        _nendBits += bits;
        _nbitsTotal += bits;
        while (_nendBits >= SymBits)
        {
            WriteByteAtEnd((byte)(_endWindow & SymMax));
            _endWindow >>= SymBits;
            _nendBits -= SymBits;
        }
    }

    /// <summary>
    /// Encodes one of <paramref name="ft"/> equiprobable integers (RFC 6716 §5.1.4).
    /// </summary>
    /// <param name="value">The value to encode, less than <paramref name="ft"/>.</param>
    /// <param name="ft">The number of equiprobable values, at least 2.</param>
    public void EncodeUInt(uint value, uint ft)
    {
        uint f = ft - 1;
        int ftb = Ilog(f);
        if (ftb > UintBits)
        {
            int highBits = ftb - UintBits;
            uint top = value >> highBits;
            Encode(top, top + 1, (f >> highBits) + 1);
            EncodeBits(value, highBits);
        }
        else
        {
            Encode(value, value + 1, ft);
        }
    }

    /// <summary>
    /// Finalizes the stream by emitting a value inside the current range and flushing the buffers
    /// (RFC 6716 §5.1.5).
    /// </summary>
    /// <remarks>
    /// The encoder emits the shortest aligned value inside [val, val + rng) that decodes correctly
    /// for any following bits. It then flushes the buffered range byte and the raw-bit bytes. When
    /// the two regions meet, the remaining partial raw-bit byte is ORed into the last byte of the
    /// frame (which may be the last range-coded byte), and the gap between the regions is zeroed.
    /// </remarks>
    public void Done()
    {
        if (_done)
        {
            return;
        }

        _done = true;

        // RFC 6716 §5.1.5: start with the shortest value ending at a power-of-two boundary, then
        // fall back one bit if that value is not strictly below val + rng.
        int l = RangeDecoder.EcCodeBits - Ilog(_rng);
        uint msk = (CodeTop - 1) >> l;
        uint end = (_val + msk) & ~msk;
        if ((end | msk) >= _val + _rng)
        {
            l++;
            msk >>= 1;
            end = (_val + msk) & ~msk;
        }

        // Emit the chosen value, one renormalization symbol at a time, until the bit count is spent.
        while (l > 0)
        {
            CarryOut(end >> 23);
            end = (end << SymBits) & (CodeTop - 1);
            l -= SymBits;
        }

        // A buffered byte exists whenever rem >= 0; ext > 0 means a byte may still receive a carry.
        if (_rem >= 0 || _ext > 0)
        {
            CarryOut(0);
        }

        // Flush any whole raw-bit bytes still held in the window.
        uint window = (uint)_endWindow;
        int used = _nendBits;
        while (used >= SymBits)
        {
            WriteByteAtEnd((byte)(window & SymMax));
            window >>= SymBits;
            used -= SymBits;
        }

        if (!_error)
        {
            // Zero the gap between the two regions before placing the partial raw-bit byte.
            int gapEnd = _storage - _endOffs;
            if (gapEnd > _offs)
            {
                _buffer.Slice(_offs, gapEnd - _offs).Clear();
            }

            if (used > 0)
            {
                if (_endOffs >= _storage)
                {
                    // No room for raw bits at all.
                    _error = true;
                }
                else
                {
                    // l <= 0 counts the unused low bits of the last emitted range byte; raw bits
                    // may share those bits. If the frame is already full and they do not fit,
                    // keep the range data intact and report the overflow.
                    int freeBits = -l;
                    if (_offs + _endOffs >= _storage && freeBits < used)
                    {
                        window &= (1u << freeBits) - 1u;
                        _error = true;
                    }

                    _buffer[_storage - _endOffs - 1] |= (byte)window;
                }
            }
        }

        _frameBytes = _offs + _endOffs;
        if (!_error && used > 0 && _offs + _endOffs < _storage)
        {
            _frameBytes++;
        }
    }

    /// <summary>
    /// Shrinks the usable frame to <paramref name="size"/> bytes, moving the raw bits at the end of
    /// the buffer so that they stay at the end (RFC 6716 §5.1.5).
    /// </summary>
    /// <remarks>
    /// The CELT VBR encoder uses this when it learns the final packet size after some symbols have
    /// already been written. The caller must guarantee that everything written so far fits.
    /// </remarks>
    /// <param name="size">The new frame size in bytes, at least the number of bytes already written.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="size"/> is smaller than the bytes already written or larger than the current storage.
    /// </exception>
    public void Shrink(int size)
    {
        if (size < _offs + _endOffs || size > _storage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size), size, "The new size must hold the bytes already written and not exceed the current storage.");
        }

        if (size == _storage)
        {
            return;
        }

        // Moving to a lower offset; Span.CopyTo behaves like memmove for overlapping ranges.
        _buffer.Slice(_storage - _endOffs, _endOffs).CopyTo(_buffer.Slice(size - _endOffs, _endOffs));
        _storage = size;
    }

    /// <summary>Renormalizes the range encoder (RFC 6716 §5.1.1.1).</summary>
    private void Normalize()
    {
        while (_rng <= CodeBot)
        {
            if (_rng == 0)
            {
                _error = true;
                return;
            }

            // RFC 6716 §5.1.1.1: the top nine bits of val go to the carry buffer.
            CarryOut(_val >> 23);
            _val = (_val << SymBits) & (CodeTop - 1);
            _rng <<= SymBits;
            _nbitsTotal += SymBits;
        }
    }

    /// <summary>Carry propagation and output buffering (RFC 6716 §5.1.1.2).</summary>
    private void CarryOut(uint c)
    {
        if (c == SymMax)
        {
            // A pending 0xFF byte: it may still change to 0x00 if a carry arrives.
            _ext++;
            return;
        }

        uint carry = c >> SymBits;
        if (_rem >= 0)
        {
            WriteByte((byte)(_rem + (int)carry));
        }

        _rem = (int)(c & SymMax);
        while (_ext > 0)
        {
            WriteByte((byte)(SymMax + carry));
            _ext--;
        }
    }

    /// <summary>Writes one range-coded byte at the front of the buffer (RFC 6716 §5.1.1.2).</summary>
    private void WriteByte(byte value)
    {
        if (_offs + _endOffs >= _storage)
        {
            _error = true;
            return;
        }

        _buffer[_offs] = value;
        _offs++;
    }

    /// <summary>Writes one raw-bit byte at the back of the buffer (RFC 6716 §5.1.3).</summary>
    private void WriteByteAtEnd(byte value)
    {
        if (_offs + _endOffs >= _storage)
        {
            _error = true;
            return;
        }

        _endOffs++;
        _buffer[_storage - _endOffs] = value;
    }

    /// <summary>Returns <c>ilog(x)</c>: the number of bits needed to represent <c>x</c>, for <c>x &gt; 0</c>.</summary>
    private static int Ilog(uint x) => BitOperations.Log2(x) + 1;
}
