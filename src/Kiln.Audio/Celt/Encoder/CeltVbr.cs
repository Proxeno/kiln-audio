// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Entropy;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>
/// Variable-bitrate frame sizing for the CELT encoder (RFC 6716 Appendix A, celt/celt.c: the
/// VBR block of <c>celt_encode_with_ec</c>).
/// </summary>
/// <remarks>
/// <para>
/// The target is the configured bitrate converted to eighth-bits per frame, adjusted for the frame
/// size, transient content and time/frequency resolution. The encoder tracks the difference between
/// the target and the rate actually produced in a drift term with a decaying rate, and in
/// constrained mode with a bit reservoir. The reference's constants are reproduced exactly so the
/// rate control behaves the same as the reference.
/// </para>
/// <para>
/// The instance holds only integers and allocates nothing.
/// </para>
/// </remarks>
internal sealed class CeltVbr
{
    private const int BitRes = CeltTables.BitRes;
    private const int MaxLm = 3;

    private int _reservoir;
    private int _drift;
    private int _offset;
    private int _count;

    /// <summary>
    /// Returns the per-frame target in eighth-bits (RFC 6716 Appendix A: <c>vbr_rate</c>).
    /// </summary>
    /// <param name="bitrateBps">The configured bitrate.</param>
    /// <param name="frameSamples">The number of 48 kHz samples per frame.</param>
    /// <returns>The target in eighth-bits.</returns>
    internal static int RateFor(int bitrateBps, int frameSamples)
    {
        long den = InputResampler.InternalSampleRate >> BitRes;
        return (int)(((long)bitrateBps * frameSamples + (den >> 1)) / den);
    }

    /// <summary>Returns the byte target implied by <paramref name="vbrRate"/>.</summary>
    /// <param name="vbrRate">The per-frame target in eighth-bits.</param>
    /// <returns>The effective byte count.</returns>
    internal static int EffectiveBytes(int vbrRate) => vbrRate >> (3 + BitRes);

    /// <summary>
    /// Applies the constrained-VBR preamble that caps the frame before any symbol is written
    /// (RFC 6716 Appendix A, celt/celt.c: the <c>constrained_vbr</c> block).
    /// </summary>
    /// <param name="enc">The range encoder, which is shrunk when the cap binds.</param>
    /// <param name="vbrRate">The per-frame target in eighth-bits.</param>
    /// <param name="tell">The encoder position in whole bits at entry.</param>
    /// <param name="filledBytes">Bytes already written before this frame (zero for CELT-only).</param>
    /// <param name="nbCompressedBytes">The initial frame size; updated in place.</param>
    /// <param name="nbAvailableBytes">The available bytes; updated in place.</param>
    internal void ConstrainPreamble(
        ref RangeEncoder enc,
        int vbrRate,
        int tell,
        int filledBytes,
        ref int nbCompressedBytes,
        ref int nbAvailableBytes)
    {
        int bound = vbrRate;
        int maxAllowed = Math.Min(
            Math.Max(tell == 1 ? 2 : 0, (vbrRate + bound - _reservoir) >> (BitRes + 3)),
            nbAvailableBytes);
        if (maxAllowed < nbAvailableBytes)
        {
            nbCompressedBytes = filledBytes + maxAllowed;
            nbAvailableBytes = maxAllowed;
            enc.Shrink(nbCompressedBytes);
        }
    }

    /// <summary>
    /// Chooses the final frame size from the target and the bits used so far, updating the drift
    /// and reservoir (RFC 6716 Appendix A, celt/celt.c: the VBR block).
    /// </summary>
    /// <param name="vbrRate">The per-frame target in eighth-bits.</param>
    /// <param name="constrained">True for constrained VBR.</param>
    /// <param name="channels">The number of stream channels.</param>
    /// <param name="lm">The frame's <c>LM</c>.</param>
    /// <param name="shortBlocks">Non-zero when the frame is transient.</param>
    /// <param name="tfSum">The time/frequency metric sum from the analysis.</param>
    /// <param name="start">The first coded band.</param>
    /// <param name="end">One past the last coded band.</param>
    /// <param name="m">The number of MDCT blocks, <c>1 &lt;&lt; lm</c>.</param>
    /// <param name="tellFrac">The encoder position in eighth-bits.</param>
    /// <param name="totalBoost">The dynamic-allocation boost in eighth-bits.</param>
    /// <param name="nbCompressedBytes">The current frame size.</param>
    /// <param name="filledBytes">Bytes written before this frame (zero for CELT-only).</param>
    /// <param name="nbAvailableBytes">The available bytes at entry; updated in place.</param>
    /// <returns>The final frame size in bytes.</returns>
    internal int Update(
        int vbrRate,
        bool constrained,
        int channels,
        int lm,
        int shortBlocks,
        int tfSum,
        int start,
        int end,
        int m,
        int tellFrac,
        int totalBoost,
        int nbCompressedBytes,
        int filledBytes,
        ref int nbAvailableBytes)
    {
        int lmDiff = MaxLm - lm;
        int target = vbrRate + (_offset >> lmDiff) - ((40 * channels + 20) << BitRes);

        if (shortBlocks != 0 || tfSum < -2 * (end - start))
        {
            target = 7 * target / 4;
        }
        else if (tfSum < -(end - start))
        {
            target = 3 * target / 2;
        }
        else if (m > 1)
        {
            target -= (target + 14) / 28;
        }

        target += tellFrac;

        int minAllowed = ((tellFrac + totalBoost + (1 << (BitRes + 3)) - 1) >> (BitRes + 3))
            + 2 - filledBytes;
        nbAvailableBytes = (target + (1 << (BitRes + 2))) >> (BitRes + 3);
        nbAvailableBytes = Math.Max(minAllowed, nbAvailableBytes);
        nbAvailableBytes = Math.Min(nbCompressedBytes, nbAvailableBytes + filledBytes) - filledBytes;

        int delta = target - vbrRate;
        target = nbAvailableBytes << (BitRes + 3);

        float alpha;
        if (_count < 970)
        {
            _count++;
            alpha = 1f / (_count + 20);
        }
        else
        {
            alpha = 0.001f;
        }

        if (constrained)
        {
            _reservoir += target - vbrRate;
        }

        _drift += (int)(alpha * ((delta * (1 << lmDiff)) - _offset - _drift));
        _offset = -_drift;

        if (constrained && _reservoir < 0)
        {
            int adjust = (-_reservoir) / (8 << BitRes);
            nbAvailableBytes += adjust;
            _reservoir = 0;
        }

        return Math.Min(nbCompressedBytes, nbAvailableBytes + filledBytes);
    }
}