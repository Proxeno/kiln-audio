using Kiln.Audio.Entropy;

namespace Kiln.Audio.Celt;

/// <summary>
/// Caller-owned storage receiving the result of one CELT frame decode (RFC 6716 §4.3).
/// </summary>
/// <remarks>
/// The instance is reusable across frames and packets: <see cref="CeltDecoder.DecodeFrame"/>
/// overwrites the entries that are valid for the frame. Buffers are sized for the largest
/// possible frame (20 ms, two stream channels) so decoding never allocates.
/// </remarks>
internal sealed class CeltFrameResult
{
    /// <summary>Normalized spectrum, channel-major, in the layout of RFC 6716 §4.3.4.</summary>
    /// <remarks>
    /// Channel <c>c</c> occupies <c>[c*N, c*N + N)</c>, where <c>N</c> is
    /// <see cref="SampleCount"/>.
    /// </remarks>
    internal float[] Spectrum { get; } = new float[2 * CeltTables.MaxFrameSamples];

    /// <summary>Decoded band log2 energies, channel-major, 21 bands per channel.</summary>
    internal float[] BandEnergies { get; } = new float[2 * CeltTables.BandCount];

    /// <summary>Per-band time/frequency resolution change selected by the frame (RFC 6716 §4.3.1).</summary>
    internal int[] TfResolution { get; } = new int[CeltTables.BandCount];

    /// <summary>Per-channel collapse masks produced by the PVQ decoder (RFC 6716 §4.3.4).</summary>
    internal byte[] CollapseMasks { get; } = new byte[2 * CeltTables.BandCount];

    /// <summary>Pulse count allocated to each band (RFC 6716 §4.3.3).</summary>
    internal int[] Pulses { get; } = new int[CeltTables.BandCount];

    /// <summary>Number of stream channels decoded in this frame.</summary>
    internal int StreamChannels { get; set; }

    /// <summary>Number of samples per channel in this frame.</summary>
    internal int SampleCount { get; set; }

    /// <summary>Base-2 logarithm of the number of short MDCT blocks (0 to 3).</summary>
    internal int Lm { get; set; }

    /// <summary>Number of bands that received any allocation.</summary>
    internal int CodedBands { get; set; }

    /// <summary>Spread decision: 0 none, 1 light, 2 normal, 3 aggressive (RFC 6716 §4.3.4.3).</summary>
    internal int Spread { get; set; }

    /// <summary>First band coded by intensity stereo, or 0 when disabled (RFC 6716 §4.3.3).</summary>
    internal int Intensity { get; set; }

    /// <summary>Non-zero when the two channels were coded independently (RFC 6716 §4.3.3).</summary>
    internal int DualStereo { get; set; }

    /// <summary>True when the frame declared itself silent (RFC 6716 §4.3.1).</summary>
    internal bool IsSilent { get; set; }

    /// <summary>Transient flag (RFC 6716 §4.3.1).</summary>
    internal bool IsTransient { get; set; }

    /// <summary>Number of short blocks, or 0 for long blocks (RFC 6716 §4.3.1).</summary>
    internal int ShortBlocks { get; set; }

    /// <summary>Anti-collapse flag for the frame (RFC 6716 §4.3.5).</summary>
    internal bool AntiCollapse { get; set; }

    /// <summary>Post-filter pitch period, 0 when disabled (RFC 6716 §4.3.7.1).</summary>
    internal int PostFilterPitch { get; set; }

    /// <summary>Post-filter tap set, 0 to 2 (RFC 6716 §4.3.7.1).</summary>
    internal int PostFilterTapSet { get; set; }

    /// <summary>Post-filter gain, 0 when disabled (RFC 6716 §4.3.7.1).</summary>
    internal float PostFilterGain { get; set; }
}

/// <summary>
/// CELT bitstream decoder: every symbol and the normalized band shapes of RFC 6716 §4.3.
/// </summary>
/// <remarks>
/// <para>
/// This class stops before synthesis (denormalization, inverse MDCT, post-filter and
/// de-emphasis), which is the CELT-DEC-2 lane. It keeps exactly the cross-frame state the
/// bitstream needs: predicted band energies, the anti-collapse energy history, the last
/// post-filter parameters, and the folding seed derived from the previous final range.
/// </para>
/// <para>
/// The symbol order is Table 56 of RFC 6716. Procedures that Section 4.3 specifies only by
/// naming the reference implementation are taken from the complete source embedded in
/// Appendix A of RFC 6716. RFC 8251 Section 9 (hybrid folding) is applied; its other changes
/// concern SILK, packet parsing or synthesis, not this layer.
/// </para>
/// <para>
/// The hot path is allocation-free. All working buffers are owned by the decoder instance or
/// by the caller's <see cref="CeltFrameResult"/>.
/// </para>
/// </remarks>
internal sealed class CeltDecoder
{
    private const int BandCount = CeltTables.BandCount;

    // Spread decisions (RFC 6716 §4.3.4.3).
    private const int SpreadNone = 0;
    private const int SpreadNormal = 2;
    private const int SpreadAggressive = 3;

    // Fine-allocation offsets (RFC 6716 §4.3.3; Appendix A, celt/rate.h).
    private const int FineOffset = 21;
    private const int QThetaOffset = 4;
    private const int QThetaOffsetTwoPhase = 16;

    private readonly float[] _oldBandE = new float[2 * BandCount];
    private readonly float[] _oldLogE = new float[2 * BandCount];
    private readonly float[] _oldLogE2 = new float[2 * BandCount];
    private readonly float[] _backgroundLogE = new float[2 * BandCount];

    private readonly float[] _spectrum = new float[2 * CeltTables.MaxFrameSamples];
    private readonly float[] _norm = new float[2 * (8 * 100)];
    private readonly float[] _lowbandScratch = new float[CeltTables.MaxFrameSamples];
    private readonly float[] _reorderScratch = new float[CeltTables.MaxFrameSamples];
    private readonly float[] _coarsePrevious = new float[2];
    private readonly int[] _iy = new int[CeltTables.MaxFrameSamples];
    private readonly byte[] _collapseMasks = new byte[2 * BandCount];

    private readonly int[] _tfResolution = new int[BandCount];
    private readonly int[] _cap = new int[BandCount];
    private readonly int[] _offsets = new int[BandCount];
    private readonly int[] _pulses = new int[BandCount];
    private readonly int[] _fineQuant = new int[BandCount];
    private readonly int[] _finePriority = new int[BandCount];
    private readonly int[] _bits1 = new int[BandCount];
    private readonly int[] _bits2 = new int[BandCount];
    private readonly int[] _threshold = new int[BandCount];
    private readonly int[] _trimOffset = new int[BandCount];

    private BandContext _ctx;
    private uint _seed;
    private int _postFilterPeriod;
    private int _postFilterPeriodOld;
    private float _postFilterGainValue;
    private float _postFilterGainOld;
    private int _postFilterTapSet;
    private int _postFilterTapSetOld;

    /// <summary>Initializes a CELT decoder for the given number of output channels.</summary>
    /// <param name="channels">Output channel count, 1 or 2.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channels"/> is not 1 or 2.</exception>
    internal CeltDecoder(int channels)
    {
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1 or 2.");
        }

        Channels = channels;
    }

    /// <summary>Gets the output channel count this decoder was created for.</summary>
    internal int Channels { get; }

    /// <summary>
    /// Clears all cross-frame state, as RFC 6716 requires after a mode switch or a reset.
    /// </summary>
    internal void Reset()
    {
        Array.Clear(_oldBandE);
        Array.Clear(_oldLogE);
        Array.Clear(_oldLogE2);
        Array.Clear(_backgroundLogE);
        Array.Clear(_spectrum);
        Array.Clear(_norm);
        _seed = 0;
        _postFilterPeriod = 0;
        _postFilterPeriodOld = 0;
        _postFilterGainValue = 0;
        _postFilterGainOld = 0;
        _postFilterTapSet = 0;
        _postFilterTapSetOld = 0;
    }

    /// <summary>
    /// Decodes one CELT frame from <paramref name="dec"/> in the symbol order of RFC 6716
    /// §4.3, producing normalized band shapes and the decoded energies.
    /// </summary>
    /// <param name="dec">The range decoder positioned at the start of this frame's CELT data.</param>
    /// <param name="frameBytes">Number of bytes in the frame.</param>
    /// <param name="lm">Base-2 logarithm of the number of short blocks, 0 to 3.</param>
    /// <param name="startBand">First coded band: 0 for CELT-only, 17 for a hybrid frame.</param>
    /// <param name="endBand">One past the last coded band, from the packet bandwidth.</param>
    /// <param name="streamChannels">Number of coded channels, 1 or 2.</param>
    /// <param name="result">Destination for the decoded frame.</param>
    internal void DecodeFrame(
        ref RangeDecoder dec,
        int frameBytes,
        int lm,
        int startBand,
        int endBand,
        int streamChannels,
        CeltFrameResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (lm is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(lm), lm, "LM must be 0 to 3.");
        }

        if (streamChannels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(streamChannels), streamChannels, "Channels must be 1 or 2.");
        }

        if (startBand is < 0 or >= BandCount)
        {
            throw new ArgumentOutOfRangeException(nameof(startBand), startBand, "Start band must be 0 to 20.");
        }

        if (endBand is < 1 or > BandCount || endBand <= startBand)
        {
            throw new ArgumentOutOfRangeException(nameof(endBand), endBand, "End band must be after the start band and at most 21.");
        }

        // Symbols are decoded in the order of RFC 6716 Table 56.
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int m = 1 << lm;
        int n = m * CeltTables.ShortBlockSamples;
        int c = streamChannels;
        int totalBits = frameBytes * 8;

        result.StreamChannels = c;
        result.SampleCount = n;
        result.Lm = lm;

        Array.Clear(_tfResolution);
        Array.Clear(_pulses);
        Array.Clear(_fineQuant);
        Array.Clear(_finePriority);
        Array.Clear(_collapseMasks);
        Array.Clear(_offsets);

        // The decoder folds a mono presentation of the two channel histories (RFC 6716 §4.3.2.1).
        if (c == 1)
        {
            for (int i = 0; i < BandCount; i++)
            {
                _oldBandE[i] = Math.Max(_oldBandE[i], _oldBandE[BandCount + i]);
            }
        }

        int tell = dec.Tell();
        bool silence;
        if (tell >= totalBits)
        {
            silence = true;
        }
        else if (tell == 1)
        {
            silence = dec.DecodeBitLogp(15);
        }
        else
        {
            silence = false;
        }

        if (silence)
        {
            DecodeSilent(ref dec, lm, startBand, endBand, c, result);
            return;
        }

        // Post-filter parameters (RFC 6716 §4.3.7.1); never present in a hybrid frame.
        int postFilterPitch = 0;
        int postFilterTapSet = 0;
        float postFilterGain = 0;
        if (startBand == 0 && tell + 16 <= totalBits)
        {
            if (dec.DecodeBitLogp(1))
            {
                int octave = (int)dec.DecodeUInt(6);
                postFilterPitch = (16 << octave) + (int)dec.DecodeBits(4 + octave) - 1;
                int qg = (int)dec.DecodeBits(3);
                if (dec.Tell() + 2 <= totalBits)
                {
                    postFilterTapSet = dec.DecodeIcdf(CeltTables.TapsetIcdf, 2);
                }

                postFilterGain = 0.09375f * (qg + 1);
            }

            tell = dec.Tell();
        }

        // Transient flag (RFC 6716 §4.3.1).
        bool isTransient = false;
        if (lm > 0 && tell + 3 <= totalBits)
        {
            isTransient = dec.DecodeBitLogp(3);
            tell = dec.Tell();
        }

        int shortBlocks = isTransient ? m : 0;

        // Intra-frame energy flag (RFC 6716 §4.3.2.1).
        bool intraEnergy = tell + 3 <= totalBits && dec.DecodeBitLogp(3);

        UnquantCoarseEnergy(ref dec, frameBytes, startBand, endBand, c, lm, intraEnergy);

        // Time/frequency resolution (RFC 6716 §4.3.1).
        TfDecode(ref dec, frameBytes, lm, startBand, endBand, isTransient);

        tell = dec.Tell();

        // Spreading decision (RFC 6716 §4.3.4.3).
        int spread = SpreadNormal;
        if (tell + 4 <= totalBits)
        {
            spread = dec.DecodeIcdf(CeltTables.SpreadIcdf, 5);
        }

        InitCaps(lm, c);

        // Dynamic allocation boosts (RFC 6716 §4.3.3).
        int dynAllocLogp = 6;
        int totalBitsFrac = totalBits << CeltTables.BitRes;
        int tellFrac = (int)dec.TellFrac();
        for (int i = startBand; i < endBand; i++)
        {
            int width = c * (eBands[i + 1] - eBands[i]) << lm;
            int quanta = Math.Min(width << CeltTables.BitRes, Math.Max(6 << CeltTables.BitRes, width));
            int loopLogp = dynAllocLogp;
            int boost = 0;
            while (tellFrac + (loopLogp << CeltTables.BitRes) < totalBitsFrac && boost < _cap[i])
            {
                bool flag = dec.DecodeBitLogp(loopLogp);
                tellFrac = (int)dec.TellFrac();
                if (!flag)
                {
                    break;
                }

                boost += quanta;
                totalBitsFrac -= quanta;
                loopLogp = 1;
            }

            _offsets[i] = boost;
            if (boost > 0)
            {
                dynAllocLogp = Math.Max(2, dynAllocLogp - 1);
            }
        }

        int allocTrim = tellFrac + (6 << CeltTables.BitRes) <= totalBitsFrac
            ? dec.DecodeIcdf(CeltTables.TrimIcdf, 7)
            : 5;

        int bits = (totalBits << CeltTables.BitRes) - (int)dec.TellFrac() - 1;
        int antiCollapseReserve = isTransient && lm >= 2 && bits >= (lm + 2) << CeltTables.BitRes
            ? 1 << CeltTables.BitRes
            : 0;
        bits -= antiCollapseReserve;

        int codedBands = ComputeAllocation(
            ref dec,
            startBand,
            endBand,
            lm,
            c,
            bits,
            allocTrim,
            out int intensity,
            out int dualStereo,
            out int balance);

        UnquantFineEnergy(ref dec, startBand, endBand, c);

        // PVQ band shapes (RFC 6716 §4.3.4).
        QuantAllBands(
            ref dec,
            lm,
            startBand,
            endBand,
            c,
            n,
            shortBlocks != 0,
            spread,
            dualStereo,
            intensity,
            codedBands,
            (totalBits << CeltTables.BitRes) - antiCollapseReserve,
            balance);

        bool antiCollapse = false;
        if (antiCollapseReserve > 0)
        {
            antiCollapse = dec.DecodeBits(1) != 0;
        }

        // Leftover energy bits (RFC 6716 §4.3.2.2).
        UnquantEnergyFinalise(ref dec, startBand, endBand, c, totalBits - dec.Tell());

        if (antiCollapse)
        {
            ApplyAntiCollapse(lm, startBand, endBand, c, n);
        }

        // Publish the frame.
        result.CodedBands = codedBands;
        result.Spread = spread;
        result.Intensity = intensity;
        result.DualStereo = dualStereo;
        result.IsSilent = false;
        result.IsTransient = isTransient;
        result.ShortBlocks = shortBlocks;
        result.AntiCollapse = antiCollapse;
        result.PostFilterPitch = postFilterPitch;
        result.PostFilterTapSet = postFilterTapSet;
        result.PostFilterGain = postFilterGain;
        Array.Copy(_tfResolution, result.TfResolution, BandCount);
        Array.Copy(_pulses, result.Pulses, BandCount);
        Array.Copy(_collapseMasks, result.CollapseMasks, 2 * BandCount);
        Array.Copy(_spectrum, result.Spectrum, c * n);

        // Advance the cross-frame state exactly as RFC 6716 §4.3.6 requires before the next frame.
        if (c == 1)
        {
            Array.Copy(_oldBandE, 0, _oldBandE, BandCount, BandCount);
        }

        if (!isTransient)
        {
            Array.Copy(_oldLogE, _oldLogE2, 2 * BandCount);
            Array.Copy(_oldBandE, _oldLogE, 2 * BandCount);
            float backgroundIncrease = m * 0.001f;
            for (int i = 0; i < 2 * BandCount; i++)
            {
                _backgroundLogE[i] = Math.Min(_backgroundLogE[i] + backgroundIncrease, _oldBandE[i]);
            }
        }
        else
        {
            for (int i = 0; i < 2 * BandCount; i++)
            {
                _oldLogE[i] = Math.Min(_oldLogE[i], _oldBandE[i]);
            }
        }

        for (int ch = 0; ch < 2; ch++)
        {
            for (int i = 0; i < startBand; i++)
            {
                _oldBandE[(ch * BandCount) + i] = 0;
                _oldLogE[(ch * BandCount) + i] = -28;
                _oldLogE2[(ch * BandCount) + i] = -28;
            }

            for (int i = endBand; i < BandCount; i++)
            {
                _oldBandE[(ch * BandCount) + i] = 0;
                _oldLogE[(ch * BandCount) + i] = -28;
                _oldLogE2[(ch * BandCount) + i] = -28;
            }
        }

        // Publish the band energies after the state clamp so out-of-band entries are defined.
        Array.Copy(_oldBandE, result.BandEnergies, 2 * BandCount);

        _postFilterPeriodOld = _postFilterPeriod;
        _postFilterGainOld = _postFilterGainValue;
        _postFilterTapSetOld = _postFilterTapSet;
        _postFilterPeriod = postFilterPitch;
        _postFilterGainValue = postFilterGain;
        _postFilterTapSet = postFilterTapSet;
        if (lm != 0)
        {
            _postFilterPeriodOld = _postFilterPeriod;
            _postFilterGainOld = _postFilterGainValue;
            _postFilterTapSetOld = _postFilterTapSet;
        }

        _seed = dec.Rng;
    }

    /// <summary>Decodes the finite-difference coarse energies (RFC 6716 §4.3.2.1).</summary>
    private void UnquantCoarseEnergy(ref RangeDecoder dec, int frameBytes, int start, int end, int c, int lm, bool intra)
    {
        ReadOnlySpan<byte> model = CeltTables.EnergyProbabilityModel.Slice(lm * 2 * 42 + (intra ? 42 : 0), 42);
        float coef = intra ? 0f : CeltTables.PredictionCoefficient[lm] / 32768f;
        float beta = intra ? CeltTables.BetaIntra / 32768f : CeltTables.BetaCoefficient[lm] / 32768f;
        int budget = frameBytes * 8;
        _coarsePrevious[0] = 0;
        _coarsePrevious[1] = 0;

        for (int i = start; i < end; i++)
        {
            for (int ch = 0; ch < c; ch++)
            {
                int tell = dec.Tell();
                int qi;
                if (budget - tell >= 15)
                {
                    int pi = 2 * Math.Min(i, 20);
                    qi = LaplaceDecode(ref dec, (uint)model[pi] << 7, model[pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = dec.DecodeIcdf(CeltTables.SmallEnergyIcdf, 2);
                    qi = (qi >> 1) ^ -(qi & 1);
                }
                else if (budget - tell >= 1)
                {
                    qi = dec.DecodeBitLogp(1) ? -1 : 0;
                }
                else
                {
                    qi = -1;
                }

                float previous = _coarsePrevious[ch];
                float oldEnergy = Math.Max(-9f, _oldBandE[(ch * BandCount) + i]);
                _oldBandE[(ch * BandCount) + i] = (coef * oldEnergy) + previous + qi;
                _coarsePrevious[ch] = previous + qi - (beta * qi);
            }
        }
    }

    /// <summary>Decodes a Laplace-distributed energy residual (RFC 6716 §4.3.2.1).</summary>
    internal static int LaplaceDecode(ref RangeDecoder dec, uint fs, int decay)
    {
        const uint MinProbability = 1;

        uint fm = dec.DecodeBin(15);
        uint fl = 0;
        int value = 0;
        if (fm >= fs)
        {
            value++;
            fl = fs;
            fs = LaplaceFrequency(fs, decay) + MinProbability;

            while (fs > MinProbability && fm >= fl + (2 * fs))
            {
                fs *= 2;
                fl += fs;
                fs = (uint)(((int)fs - (2 * (int)MinProbability)) * decay >> 15);
                fs += MinProbability;
                value++;
            }

            if (fs <= MinProbability)
            {
                int extra = (int)((fm - fl) >> (0 + 1));
                value += extra;
                fl += (uint)(2 * extra * MinProbability);
            }

            if (fm < fl + fs)
            {
                value = -value;
            }
            else
            {
                fl += fs;
            }
        }

        uint high = Math.Min(fl + fs, 32768);
        dec.Update(fl, high, 32768);
        return value;
    }

    /// <summary>Returns the first-step probability of the decaying Laplace tail (RFC 6716 §4.3.2.1).</summary>
    private static uint LaplaceFrequency(uint fs0, int decay)
    {
        const uint MinProbability = 1;
        const int MinValues = 16;
        uint total = 32768 - (MinProbability * (2 * MinValues)) - fs0;
        return (uint)((ulong)total * (uint)(16384 - decay) >> 15);
    }

    /// <summary>Decodes the per-band time/frequency resolution changes (RFC 6716 §4.3.1).</summary>
    private void TfDecode(ref RangeDecoder dec, int frameBytes, int lm, int start, int end, bool isTransient)
    {
        int budget = frameBytes * 8;
        int tell = dec.Tell();
        int logp = isTransient ? 2 : 4;
        bool tfSelectReserve = lm > 0 && tell + logp + 1 <= budget;
        if (tfSelectReserve)
        {
            budget--;
        }

        int current = 0;
        bool changed = false;
        for (int i = start; i < end; i++)
        {
            if (tell + logp <= budget)
            {
                current ^= dec.DecodeBitLogp(logp) ? 1 : 0;
                tell = dec.Tell();
                changed |= current != 0;
            }

            _tfResolution[i] = current;
            logp = isTransient ? 4 : 5;
        }

        int tfSelect = 0;
        int transientIndex = isTransient ? 4 : 0;
        int changedIndex = changed ? 1 : 0;
        ReadOnlySpan<sbyte> table = CeltTables.TfSelectTable;
        if (tfSelectReserve
            && table[(lm * 8) + transientIndex + changedIndex]
                != table[(lm * 8) + transientIndex + 2 + changedIndex])
        {
            tfSelect = dec.DecodeBitLogp(1) ? 1 : 0;
        }

        for (int i = start; i < end; i++)
        {
            _tfResolution[i] = table[(lm * 8) + transientIndex + (2 * tfSelect) + _tfResolution[i]];
        }
    }

    /// <summary>Fills the per-band bit caps from the pulse cache (RFC 6716 §4.3.3).</summary>
    private void InitCaps(int lm, int c)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        for (int i = 0; i < BandCount; i++)
        {
            int bandSamples = (eBands[i + 1] - eBands[i]) << lm;
            _cap[i] = (CeltTables.CacheCaps[(BandCount * ((2 * lm) + c - 1)) + i] + 64) * c * bandSamples >> 2;
        }
    }

    /// <summary>Computes the bit allocation (RFC 6716 §4.3.3).</summary>
    private int ComputeAllocation(
        ref RangeDecoder dec,
        int start,
        int end,
        int lm,
        int c,
        int total,
        int allocTrim,
        out int intensity,
        out int dualStereo,
        out int balance)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        ReadOnlySpan<byte> allocVectors = CeltTables.BandAllocation;

        total = Math.Max(total, 0);
        int skipStart = start;
        int skipReserve = total >= 1 << CeltTables.BitRes ? 1 << CeltTables.BitRes : 0;
        total -= skipReserve;

        int intensityReserve = 0;
        int dualStereoReserve = 0;
        if (c == 2)
        {
            intensityReserve = CeltTables.Log2FracTable[end - start];
            if (intensityReserve > total)
            {
                intensityReserve = 0;
            }
            else
            {
                total -= intensityReserve;
                dualStereoReserve = total >= 1 << CeltTables.BitRes ? 1 << CeltTables.BitRes : 0;
                total -= dualStereoReserve;
            }
        }

        for (int j = start; j < end; j++)
        {
            int width = eBands[j + 1] - eBands[j];
            _threshold[j] = Math.Max(c << CeltTables.BitRes, (3 * width << lm << CeltTables.BitRes) >> 4);
            _trimOffset[j] = c * width * (allocTrim - 5 - lm) * (end - j - 1) * (1 << (lm + CeltTables.BitRes)) >> 6;
            if (width << lm == 1)
            {
                _trimOffset[j] -= c << CeltTables.BitRes;
            }
        }

        int lo = 1;
        int hi = CeltTables.AllocVectorCount - 1;
        do
        {
            bool done = false;
            int psum = 0;
            int mid = (lo + hi) >> 1;
            for (int j = end; j-- > start;)
            {
                int bitsj = c * (eBands[j + 1] - eBands[j]) * allocVectors[(mid * BandCount) + j] << lm >> 2;
                if (bitsj > 0)
                {
                    bitsj = Math.Max(0, bitsj + _trimOffset[j]);
                }

                bitsj += _offsets[j];
                if (bitsj >= _threshold[j] || done)
                {
                    done = true;
                    psum += Math.Min(bitsj, _cap[j]);
                }
                else if (bitsj >= c << CeltTables.BitRes)
                {
                    psum += c << CeltTables.BitRes;
                }
            }

            if (psum > total)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        while (lo <= hi);

        hi = lo--;
        for (int j = start; j < end; j++)
        {
            int width = eBands[j + 1] - eBands[j];
            int bits1 = c * width * allocVectors[(lo * BandCount) + j] << lm >> 2;
            int bits2 = hi >= CeltTables.AllocVectorCount
                ? _cap[j]
                : c * width * allocVectors[(hi * BandCount) + j] << lm >> 2;
            if (bits1 > 0)
            {
                bits1 = Math.Max(0, bits1 + _trimOffset[j]);
            }

            if (bits2 > 0)
            {
                bits2 = Math.Max(0, bits2 + _trimOffset[j]);
            }

            if (lo > 0)
            {
                bits1 += _offsets[j];
            }

            bits2 += _offsets[j];
            if (_offsets[j] > 0)
            {
                skipStart = j;
            }

            bits2 = Math.Max(0, bits2 - bits1);
            _bits1[j] = bits1;
            _bits2[j] = bits2;
        }

        return InterpolateAllocation(
            ref dec,
            start,
            end,
            lm,
            c,
            skipStart,
            total,
            skipReserve,
            intensityReserve,
            dualStereoReserve,
            out intensity,
            out dualStereo,
            out balance);
    }

    /// <summary>Interpolates between allocation vectors and resolves the skip decisions (RFC 6716 §4.3.3).</summary>
    private int InterpolateAllocation(
        ref RangeDecoder dec,
        int start,
        int end,
        int lm,
        int c,
        int skipStart,
        int total,
        int skipReserve,
        int intensityReserve,
        int dualStereoReserve,
        out int intensity,
        out int dualStereo,
        out int balance)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int allocFloor = c << CeltTables.BitRes;
        int stereo = c > 1 ? 1 : 0;
        int logM = lm << CeltTables.BitRes;
        int codedBands;

        int lo = 0;
        int hi = 1 << CeltTables.AllocSteps;
        for (int step = 0; step < CeltTables.AllocSteps; step++)
        {
            int mid = (lo + hi) >> 1;
            int psum = 0;
            bool done = false;
            for (int j = end; j-- > start;)
            {
                int tmp = _bits1[j] + ((mid * _bits2[j]) >> CeltTables.AllocSteps);
                if (tmp >= _threshold[j] || done)
                {
                    done = true;
                    psum += Math.Min(tmp, _cap[j]);
                }
                else if (tmp >= allocFloor)
                {
                    psum += allocFloor;
                }
            }

            if (psum > total)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        int psumFinal = 0;
        bool doneFinal = false;
        for (int j = end; j-- > start;)
        {
            int tmp = _bits1[j] + ((lo * _bits2[j]) >> CeltTables.AllocSteps);
            if (tmp < _threshold[j] && !doneFinal)
            {
                tmp = tmp >= allocFloor ? allocFloor : 0;
            }
            else
            {
                doneFinal = true;
            }

            tmp = Math.Min(tmp, _cap[j]);
            _pulses[j] = tmp;
            psumFinal += tmp;
        }

        for (codedBands = end; ; codedBands--)
        {
            int j = codedBands - 1;
            if (j <= skipStart)
            {
                total += skipReserve;
                break;
            }

            int left = total - psumFinal;
            int bandStart = eBands[start];
            int perCoefficient = left / (eBands[codedBands] - bandStart);
            left -= (eBands[codedBands] - bandStart) * perCoefficient;
            int remainder = Math.Max(left - (eBands[j] - bandStart), 0);
            int bandWidth = eBands[codedBands] - eBands[j];
            int bandBits = _pulses[j] + (perCoefficient * bandWidth) + remainder;
            if (bandBits >= Math.Max(_threshold[j], allocFloor + (1 << CeltTables.BitRes)))
            {
                if (dec.DecodeBitLogp(1))
                {
                    break;
                }

                psumFinal += 1 << CeltTables.BitRes;
                bandBits -= 1 << CeltTables.BitRes;
            }

            psumFinal -= _pulses[j] + intensityReserve;
            if (intensityReserve > 0)
            {
                intensityReserve = CeltTables.Log2FracTable[j - start];
            }

            psumFinal += intensityReserve;
            if (bandBits >= allocFloor)
            {
                psumFinal += allocFloor;
                _pulses[j] = allocFloor;
            }
            else
            {
                _pulses[j] = 0;
            }
        }

        if (intensityReserve > 0)
        {
            intensity = start + (int)dec.DecodeUInt((uint)(codedBands + 1 - start));
        }
        else
        {
            intensity = 0;
        }

        if (intensity <= start)
        {
            total += dualStereoReserve;
            dualStereoReserve = 0;
        }

        dualStereo = dualStereoReserve > 0 && dec.DecodeBitLogp(1) ? 1 : 0;

        int left2 = total - psumFinal;
        int perCoefficient2 = left2 / (eBands[codedBands] - eBands[start]);
        left2 -= (eBands[codedBands] - eBands[start]) * perCoefficient2;
        for (int j = start; j < codedBands; j++)
        {
            _pulses[j] += perCoefficient2 * (eBands[j + 1] - eBands[j]);
        }

        for (int j = start; j < codedBands; j++)
        {
            int extra = Math.Min(left2, eBands[j + 1] - eBands[j]);
            _pulses[j] += extra;
            left2 -= extra;
        }

        int excessBalance = 0;
        int jj = start;
        for (; jj < codedBands; jj++)
        {
            int width = eBands[jj + 1] - eBands[jj];
            int samples = width << lm;
            int allocated = _pulses[jj] + excessBalance;
            int excess;
            if (samples > 1)
            {
                excess = Math.Max(allocated - _cap[jj], 0);
                _pulses[jj] = allocated - excess;
                int den = c * samples
                    + ((c == 2 && samples > 2 && dualStereo == 0 && jj < intensity) ? 1 : 0);
                int ncLogN = den * (CeltTables.LogN[jj] + logM);
                int offset = (ncLogN >> 1) - (den * FineOffset);
                if (samples == 2)
                {
                    offset += (den << CeltTables.BitRes) >> 2;
                }

                if (_pulses[jj] + offset < (den * 2) << CeltTables.BitRes)
                {
                    offset += ncLogN >> 2;
                }
                else if (_pulses[jj] + offset < (den * 3) << CeltTables.BitRes)
                {
                    offset += ncLogN >> 3;
                }

                int ebits = Math.Max(
                    0,
                    (_pulses[jj] + offset + (den << (CeltTables.BitRes - 1))) / (den << CeltTables.BitRes));
                if (c * ebits > _pulses[jj] >> CeltTables.BitRes)
                {
                    ebits = _pulses[jj] >> stereo >> CeltTables.BitRes;
                }

                ebits = Math.Min(ebits, CeltTables.MaxFineBits);
                _finePriority[jj] = ebits * (den << CeltTables.BitRes) >= _pulses[jj] + offset ? 1 : 0;
                _pulses[jj] -= c * ebits << CeltTables.BitRes;
                _fineQuant[jj] = ebits;
            }
            else
            {
                excess = Math.Max(0, allocated - (c << CeltTables.BitRes));
                _pulses[jj] = allocated - excess;
                _fineQuant[jj] = 0;
                _finePriority[jj] = 1;
            }

            if (excess > 0)
            {
                int extraFine = Math.Min(excess >> (stereo + CeltTables.BitRes), CeltTables.MaxFineBits - _fineQuant[jj]);
                _fineQuant[jj] += extraFine;
                int extraBits = extraFine * c << CeltTables.BitRes;
                _finePriority[jj] = extraBits >= excess - excessBalance ? 1 : 0;
                excess -= extraBits;
            }

            excessBalance = excess;
        }

        balance = excessBalance;

        for (; jj < end; jj++)
        {
            _fineQuant[jj] = _pulses[jj] >> stereo >> CeltTables.BitRes;
            _pulses[jj] = 0;
            _finePriority[jj] = _fineQuant[jj] < 1 ? 1 : 0;
        }

        return codedBands;
    }

    /// <summary>Decodes the fine energy bits (RFC 6716 §4.3.2.2).</summary>
    private void UnquantFineEnergy(ref RangeDecoder dec, int start, int end, int c)
    {
        for (int i = start; i < end; i++)
        {
            int quant = _fineQuant[i];
            if (quant <= 0)
            {
                continue;
            }

            for (int ch = 0; ch < c; ch++)
            {
                int q2 = (int)dec.DecodeBits(quant);
                float offset = ((q2 + 0.5f) * (1 << (14 - quant)) * (1f / 16384)) - 0.5f;
                _oldBandE[(ch * BandCount) + i] += offset;
            }
        }
    }

    /// <summary>Spends the leftover bits on additional fine energy (RFC 6716 §4.3.2.2).</summary>
    private void UnquantEnergyFinalise(ref RangeDecoder dec, int start, int end, int c, int bitsLeft)
    {
        for (int priority = 0; priority < 2; priority++)
        {
            for (int i = start; i < end && bitsLeft >= c; i++)
            {
                if (_fineQuant[i] >= CeltTables.MaxFineBits || _finePriority[i] != priority)
                {
                    continue;
                }

                for (int ch = 0; ch < c; ch++)
                {
                    int q2 = (int)dec.DecodeBits(1);
                    float offset = (q2 - 0.5f) * (1 << (14 - _fineQuant[i] - 1)) * (1f / 16384);
                    _oldBandE[(ch * BandCount) + i] += offset;
                    bitsLeft--;
                }
            }
        }
    }

    /// <summary>Decodes every band's normalized shape (RFC 6716 §4.3.4).</summary>
    private void QuantAllBands(
        ref RangeDecoder dec,
        int lm,
        int start,
        int end,
        int c,
        int n,
        bool shortBlocks,
        int spread,
        int dualStereo,
        int intensity,
        int codedBands,
        int totalBits,
        int balance)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int m = 1 << lm;
        int blocks = shortBlocks ? m : 1;
        int normOffset = m * eBands[start];
        int normLength = (m * eBands[BandCount]) - normOffset;

        Span<float> norm = _norm.AsSpan(0, normLength);
        Span<float> norm2 = _norm.AsSpan(normLength, normLength);
        Span<float> spectrum = _spectrum.AsSpan(0, c * n);
        Span<float> scratch = _lowbandScratch.AsSpan(0, n);

        // RFC 6716 §4.3.4 (reference: celt.c zeroes X before decoding): only
        // [eBands[start], eBands[end]) is coded.
        int firstCoded = m * eBands[start];
        int lastCoded = m * eBands[end];
        for (int ch = 0; ch < c; ch++)
        {
            spectrum.Slice(ch * n, firstCoded).Clear();
            spectrum.Slice((ch * n) + lastCoded, n - lastCoded).Clear();
        }

        _ctx.Band = 0;
        _ctx.Intensity = intensity;
        _ctx.Spread = spread;
        _ctx.TfChange = 0;
        _ctx.RemainingBits = 0;
        _ctx.Seed = _seed;

        int lowbandOffset = 0;
        bool updateLowband = true;

        for (int i = start; i < end; i++)
        {
            _ctx.Band = i;
            int bandSamples = m * (eBands[i + 1] - eBands[i]);
            int tell = (int)dec.TellFrac();

            if (i != start)
            {
                balance -= tell;
            }

            int remainingBits = totalBits - tell - 1;
            _ctx.RemainingBits = remainingBits;
            int b;
            if (i <= codedBands - 1)
            {
                int currentBalance = IntDiv(balance, Math.Min(3, codedBands - i));
                b = Math.Max(0, Math.Min(16383, Math.Min(remainingBits + 1, _pulses[i] + currentBalance)));
            }
            else
            {
                b = 0;
            }

            if (m * eBands[i] - bandSamples >= m * eBands[start] || i == start + 1)
            {
                if (updateLowband || lowbandOffset == 0)
                {
                    lowbandOffset = i;
                }
            }

            if (i == start + 1)
            {
                SpecialHybridFolding(norm, norm2, start, m, dualStereo != 0);
            }

            _ctx.TfChange = _tfResolution[i];

            Span<float> x = spectrum.Slice(m * eBands[i], bandSamples);
            Span<float> y = c == 2
                ? spectrum.Slice(n + (m * eBands[i]), bandSamples)
                : default;

            int xMask;
            int yMask;
            if (lowbandOffset != 0 && (spread != SpreadAggressive || blocks > 1 || _ctx.TfChange < 0))
            {
                int effectiveLowband = Math.Max(0, (m * eBands[lowbandOffset]) - normOffset - bandSamples);
                int foldStart = lowbandOffset;
                while (m * eBands[--foldStart] > effectiveLowband + normOffset)
                {
                }

                int foldEnd = lowbandOffset - 1;
                while (++foldEnd < i && m * eBands[foldEnd] < effectiveLowband + normOffset + bandSamples)
                {
                }

                xMask = 0;
                yMask = 0;
                for (int foldBand = foldStart; foldBand < foldEnd; foldBand++)
                {
                    xMask |= _collapseMasks[(foldBand * c) + 0];
                    yMask |= _collapseMasks[(foldBand * c) + c - 1];
                }
            }
            else
            {
                xMask = (1 << blocks) - 1;
                yMask = xMask;
            }

            if (dualStereo != 0 && i == intensity)
            {
                dualStereo = 0;
                for (int j = 0; j < (m * eBands[i]) - normOffset; j++)
                {
                    norm[j] = 0.5f * (norm[j] + norm2[j]);
                }
            }

            int effective = lowbandOffset != 0
                ? Math.Max(0, (m * eBands[lowbandOffset]) - normOffset - bandSamples)
                : -1;

            int xCollapse;
            int yCollapse;
            if (dualStereo != 0)
            {
                xCollapse = QuantBand(
                    ref dec,
                    x,
                    bandSamples,
                    b / 2,
                    blocks,
                    effective >= 0 ? norm.Slice(effective, bandSamples) : default,
                    lm,
                    norm.Slice((m * eBands[i]) - normOffset, bandSamples),
                    1f,
                    scratch,
                    xMask);
                yCollapse = QuantBand(
                    ref dec,
                    y,
                    bandSamples,
                    b / 2,
                    blocks,
                    effective >= 0 ? norm2.Slice(effective, bandSamples) : default,
                    lm,
                    norm2.Slice((m * eBands[i]) - normOffset, bandSamples),
                    1f,
                    scratch,
                    yMask);
            }
            else
            {
                Span<float> lowband = effective >= 0 ? norm.Slice(effective, bandSamples) : default;
                Span<float> lowbandOut = norm.Slice((m * eBands[i]) - normOffset, bandSamples);
                if (c == 2)
                {
                    xCollapse = QuantBandStereo(
                        ref dec,
                        x,
                        y,
                        bandSamples,
                        b,
                        blocks,
                        lowband,
                        lm,
                        lowbandOut,
                        scratch,
                        xMask | yMask);
                }
                else
                {
                    xCollapse = QuantBand(ref dec, x, bandSamples, b, blocks, lowband, lm, lowbandOut, 1f, scratch, xMask | yMask);
                }

                yCollapse = xCollapse;
            }

            _collapseMasks[(i * c) + 0] = (byte)xCollapse;
            _collapseMasks[(i * c) + c - 1] = (byte)yCollapse;
            balance += _pulses[i] + tell;
            updateLowband = b > (bandSamples << CeltTables.BitRes);
        }

        _seed = _ctx.Seed;
    }

    /// <summary>Decodes one band with the mono partitioner (RFC 6716 §4.3.4).</summary>
    private int QuantBand(
        ref RangeDecoder dec,
        Span<float> x,
        int n,
        int b,
        int blocks,
        Span<float> lowband,
        int lm,
        Span<float> lowbandOut,
        float gain,
        Span<float> lowbandScratch,
        int fill)
    {
        int n0 = n;
        int nB = n;
        bool longBlocks = blocks == 1;
        int b0 = blocks;
        int timeDivide = 0;
        int recombine = 0;
        int tfChange = _ctx.TfChange;

        nB /= blocks;
        if (n == 1)
        {
            return QuantBandN1(ref dec, x, default, lowbandOut);
        }

        if (tfChange > 0)
        {
            recombine = tfChange;
        }

        if (!lowbandScratch.IsEmpty && !lowband.IsEmpty
            && (recombine != 0 || ((nB & 1) == 0 && tfChange < 0) || blocks > 1))
        {
            lowband.CopyTo(lowbandScratch);
            lowband = lowbandScratch.Slice(0, n);
        }

        for (int k = 0; k < recombine; k++)
        {
            if (!lowband.IsEmpty)
            {
                Haar1(lowband, n >> k, 1 << k);
            }

            fill = CeltTables.BitInterleaveTable[fill & 0xF] | (CeltTables.BitInterleaveTable[fill >> 4] << 2);
        }

        blocks >>= recombine;
        nB <<= recombine;

        while ((nB & 1) == 0 && tfChange < 0)
        {
            if (!lowband.IsEmpty)
            {
                Haar1(lowband, nB, blocks);
            }

            fill |= fill << blocks;
            blocks <<= 1;
            nB >>= 1;
            timeDivide++;
            tfChange++;
        }

        b0 = blocks;
        int nB0 = nB;

        if (b0 > 1 && !lowband.IsEmpty)
        {
            DeinterleaveHadamard(lowband, nB >> recombine, b0 << recombine, longBlocks);
        }

        int collapseMask = QuantPartition(ref dec, x, n, b, blocks, lowband, lm, gain, fill);

        if (b0 > 1)
        {
            InterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks);
        }

        nB = nB0;
        blocks = b0;
        for (int k = 0; k < timeDivide; k++)
        {
            blocks >>= 1;
            nB <<= 1;
            collapseMask |= collapseMask >> blocks;
            Haar1(x, nB, blocks);
        }

        for (int k = 0; k < recombine; k++)
        {
            collapseMask = CeltTables.BitDeinterleaveTable[collapseMask];
            Haar1(x, n0 >> k, 1 << k);
        }

        blocks <<= recombine;

        if (!lowbandOut.IsEmpty)
        {
            float scale = (float)Math.Sqrt(n0);
            for (int j = 0; j < n0; j++)
            {
                lowbandOut[j] = scale * x[j];
            }
        }

        return collapseMask & ((1 << blocks) - 1);
    }

    /// <summary>Decodes a one-coefficient band: up to one sign per channel (RFC 6716 §4.3.4).</summary>
    private int QuantBandN1(ref RangeDecoder dec, Span<float> x, Span<float> y, Span<float> lowbandOut)
    {
        bool hasY = !y.IsEmpty;
        for (int c = 0; c < (hasY ? 2 : 1); c++)
        {
            Span<float> channel = c == 0 ? x : y;
            int sign = 0;
            if (_ctx.RemainingBits >= 1 << CeltTables.BitRes)
            {
                sign = (int)dec.DecodeBits(1);
                _ctx.RemainingBits -= 1 << CeltTables.BitRes;
            }

            channel[0] = sign != 0 ? -1f : 1f;
        }

        if (!lowbandOut.IsEmpty)
        {
            lowbandOut[0] = x[0];
        }

        return 1;
    }

    /// <summary>Recursively splits and decodes a mono partition (RFC 6716 §4.3.4).</summary>
    private int QuantPartition(
        ref RangeDecoder dec,
        Span<float> x,
        int n,
        int b,
        int blocks,
        Span<float> lowband,
        int lm,
        float gain,
        int fill)
    {
        int b0 = blocks;
        int collapseMask = 0;

        int cacheOffset = CeltTables.CacheIndex[((lm + 1) * BandCount) + _ctx.Band];
        if (lm != -1 && cacheOffset >= 0)
        {
            int maxPulses = CeltTables.CacheBits[cacheOffset];
            if (b > CeltTables.CacheBits[cacheOffset + maxPulses] + 12 && n > 2)
            {
                n >>= 1;
                lm--;
                if (blocks == 1)
                {
                    fill = (fill & 1) | (fill << 1);
                }

                blocks = (blocks + 1) >> 1;

                ComputeTheta(
                    ref dec,
                    x.Slice(0, n),
                    x.Slice(n, n),
                    n,
                    ref b,
                    blocks,
                    b0,
                    lm,
                    stereo: false,
                    ref fill,
                    out int inv,
                    out int imid,
                    out int iside,
                    out int delta,
                    out int itheta,
                    out int qalloc);
                _ = inv;

                float mid = imid * (1f / 32768);
                float side = iside * (1f / 32768);

                if (b0 > 1 && (itheta & 0x3fff) != 0)
                {
                    if (itheta > 8192)
                    {
                        delta -= delta >> (4 - lm);
                    }
                    else
                    {
                        delta = Math.Min(0, delta + (n << CeltTables.BitRes >> (5 - lm)));
                    }
                }

                int mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
                int sbits = b - mbits;
                _ctx.RemainingBits -= qalloc;

                Span<float> nextLowband = lowband.IsEmpty ? default : lowband.Slice(n, n);

                int rebalance = _ctx.RemainingBits;
                if (mbits >= sbits)
                {
                    collapseMask = QuantPartition(
                        ref dec,
                        x.Slice(0, n),
                        n,
                        mbits,
                        blocks,
                        lowband.IsEmpty ? default : lowband.Slice(0, n),
                        lm,
                        gain * mid,
                        fill);
                    rebalance = mbits - (rebalance - _ctx.RemainingBits);
                    if (rebalance > 3 << CeltTables.BitRes && itheta != 0)
                    {
                        sbits += rebalance - (3 << CeltTables.BitRes);
                    }

                    collapseMask |= QuantPartition(
                        ref dec,
                        x.Slice(n, n),
                        n,
                        sbits,
                        blocks,
                        nextLowband,
                        lm,
                        gain * side,
                        fill >> blocks) << (b0 >> 1);
                }
                else
                {
                    collapseMask = QuantPartition(
                        ref dec,
                        x.Slice(n, n),
                        n,
                        sbits,
                        blocks,
                        nextLowband,
                        lm,
                        gain * side,
                        fill >> blocks) << (b0 >> 1);
                    rebalance = sbits - (rebalance - _ctx.RemainingBits);
                    if (rebalance > 3 << CeltTables.BitRes && itheta != 16384)
                    {
                        mbits += rebalance - (3 << CeltTables.BitRes);
                    }

                    collapseMask |= QuantPartition(
                        ref dec,
                        x.Slice(0, n),
                        n,
                        mbits,
                        blocks,
                        lowband.IsEmpty ? default : lowband.Slice(0, n),
                        lm,
                        gain * mid,
                        fill);
                }

                return collapseMask;
            }
        }

        int pulses = Bits2Pulses(lm, b);
        int currentBits = Pulses2Bits(lm, pulses);
        _ctx.RemainingBits -= currentBits;
        while (_ctx.RemainingBits < 0 && pulses > 0)
        {
            _ctx.RemainingBits += currentBits;
            pulses--;
            currentBits = Pulses2Bits(lm, pulses);
            _ctx.RemainingBits -= currentBits;
        }

        if (pulses != 0)
        {
            int k = CeltTables.GetPulses(pulses);
            collapseMask = AlgUnquant(ref dec, x, n, k, blocks, gain);
        }
        else
        {
            int mask = (1 << blocks) - 1;
            fill &= mask;
            if (fill == 0)
            {
                x.Clear();
            }
            else
            {
                if (lowband.IsEmpty)
                {
                    for (int j = 0; j < n; j++)
                    {
                        _ctx.Seed = Lcg(_ctx.Seed);
                        x[j] = (int)_ctx.Seed >> 20;
                    }

                    collapseMask = mask;
                }
                else
                {
                    for (int j = 0; j < n; j++)
                    {
                        _ctx.Seed = Lcg(_ctx.Seed);
                        float noise = 1f / 256;
                        noise = (_ctx.Seed & 0x8000) != 0 ? noise : -noise;
                        x[j] = lowband[j] + noise;
                    }

                    collapseMask = fill;
                }

                RenormaliseVector(x, gain);
            }
        }

        return collapseMask;
    }

    /// <summary>Decodes a stereo band, including the in-band angle (RFC 6716 §4.3.4.2).</summary>
    private int QuantBandStereo(
        ref RangeDecoder dec,
        Span<float> x,
        Span<float> y,
        int n,
        int b,
        int blocks,
        Span<float> lowband,
        int lm,
        Span<float> lowbandOut,
        Span<float> lowbandScratch,
        int fill)
    {
        if (n == 1)
        {
            return QuantBandN1(ref dec, x, y, lowbandOut);
        }

        int originalFill = fill;
        ComputeTheta(
            ref dec,
            x,
            y,
            n,
            ref b,
            blocks,
            blocks,
            lm,
            stereo: true,
            ref fill,
            out int inv,
            out int imid,
            out int iside,
            out int delta,
            out int itheta,
            out int qalloc);

        float mid = imid * (1f / 32768);
        float side = iside * (1f / 32768);

        int collapseMask;
        if (n == 2)
        {
            int mbits = b;
            int sbits = itheta != 0 && itheta != 16384 ? 1 << CeltTables.BitRes : 0;
            mbits -= sbits;
            bool useY = itheta > 8192;
            _ctx.RemainingBits -= qalloc + sbits;

            Span<float> x2 = useY ? y : x;
            Span<float> y2 = useY ? x : y;
            int sign = 0;
            if (sbits != 0)
            {
                sign = (int)dec.DecodeBits(1);
            }

            sign = 1 - (2 * sign);
            collapseMask = QuantBand(
                ref dec,
                x2,
                n,
                mbits,
                blocks,
                lowband,
                lm,
                lowbandOut,
                1f,
                lowbandScratch,
                originalFill);
            y2[0] = -sign * x2[1];
            y2[1] = sign * x2[0];

            float tmp = x[0] * mid;
            x[0] = tmp;
            x[1] *= mid;
            y[0] *= side;
            y[1] *= side;
            tmp = x[0];
            x[0] = tmp - y[0];
            y[0] = tmp + y[0];
            tmp = x[1];
            x[1] = tmp - y[1];
            y[1] = tmp + y[1];
        }
        else
        {
            int mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
            int sbits = b - mbits;
            _ctx.RemainingBits -= qalloc;

            int rebalance = _ctx.RemainingBits;
            if (mbits >= sbits)
            {
                collapseMask = QuantBand(
                    ref dec,
                    x,
                    n,
                    mbits,
                    blocks,
                    lowband,
                    lm,
                    lowbandOut,
                    1f,
                    lowbandScratch,
                    fill);
                rebalance = mbits - (rebalance - _ctx.RemainingBits);
                if (rebalance > 3 << CeltTables.BitRes && itheta != 0)
                {
                    sbits += rebalance - (3 << CeltTables.BitRes);
                }

                collapseMask |= QuantBand(
                    ref dec,
                    y,
                    n,
                    sbits,
                    blocks,
                    default,
                    lm,
                    default,
                    side,
                    default,
                    fill >> blocks);
            }
            else
            {
                collapseMask = QuantBand(
                    ref dec,
                    y,
                    n,
                    sbits,
                    blocks,
                    default,
                    lm,
                    default,
                    side,
                    default,
                    fill >> blocks);
                rebalance = sbits - (rebalance - _ctx.RemainingBits);
                if (rebalance > 3 << CeltTables.BitRes && itheta != 16384)
                {
                    mbits += rebalance - (3 << CeltTables.BitRes);
                }

                collapseMask |= QuantBand(
                    ref dec,
                    x,
                    n,
                    mbits,
                    blocks,
                    lowband,
                    lm,
                    lowbandOut,
                    1f,
                    lowbandScratch,
                    fill);
            }

            StereoMerge(x, y, mid, n);
        }

        if (inv != 0)
        {
            for (int j = 0; j < n; j++)
            {
                y[j] = -y[j];
            }
        }

        return collapseMask;
    }

    /// <summary>Decodes the mid/side angle and its resolution (RFC 6716 §4.3.4.2).</summary>
    private void ComputeTheta(
        ref RangeDecoder dec,
        Span<float> x,
        Span<float> y,
        int n,
        ref int b,
        int blocks,
        int b0,
        int lm,
        bool stereo,
        ref int fill,
        out int inv,
        out int imid,
        out int iside,
        out int delta,
        out int itheta,
        out int qalloc)
    {
        int pulseCap = CeltTables.LogN[_ctx.Band] + (lm << CeltTables.BitRes);
        int offset = (pulseCap >> 1) - (stereo && n == 2 ? QThetaOffsetTwoPhase : QThetaOffset);
        int qn = ComputeQn(n, b, offset, pulseCap, stereo);
        if (stereo && _ctx.Band >= _ctx.Intensity)
        {
            qn = 1;
        }

        int localTheta = 0;
        int localInv = 0;
        int tell = (int)dec.TellFrac();
        if (qn != 1)
        {
            if (stereo && n > 2)
            {
                int p0 = 3;
                int x0 = qn / 2;
                int ft = (p0 * (x0 + 1)) + x0;
                uint fs = dec.Decode((uint)ft);
                int symbol;
                if (fs < (uint)((x0 + 1) * p0))
                {
                    symbol = (int)fs / p0;
                }
                else
                {
                    symbol = x0 + 1 + (int)(fs - (uint)((x0 + 1) * p0));
                }

                int fl = symbol <= x0 ? p0 * symbol : (symbol - 1 - x0) + ((x0 + 1) * p0);
                int fh = symbol <= x0 ? p0 * (symbol + 1) : (symbol - x0) + ((x0 + 1) * p0);
                dec.Update((uint)fl, (uint)fh, (uint)ft);
                localTheta = symbol;
            }
            else if (b0 > 1 || stereo)
            {
                localTheta = (int)dec.DecodeUInt((uint)(qn + 1));
            }
            else
            {
                int ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                uint fm = dec.Decode((uint)ft);
                int fl;
                int fs;
                if (fm < (uint)((qn >> 1) * ((qn >> 1) + 1) >> 1))
                {
                    localTheta = (Isqrt32((uint)((8 * fm) + 1)) - 1) >> 1;
                    fs = localTheta + 1;
                    fl = localTheta * (localTheta + 1) >> 1;
                }
                else
                {
                    localTheta = (2 * (qn + 1) - Isqrt32((uint)(8 * (((uint)ft - fm) - 1)) + 1)) >> 1;
                    fs = qn + 1 - localTheta;
                    fl = ft - ((qn + 1 - localTheta) * (qn + 2 - localTheta) >> 1);
                }

                dec.Update((uint)fl, (uint)(fl + fs), (uint)ft);
            }

            localTheta = localTheta * 16384 / qn;
        }
        else if (stereo)
        {
            if (b > 2 << CeltTables.BitRes && _ctx.RemainingBits > 2 << CeltTables.BitRes)
            {
                localInv = dec.DecodeBitLogp(2) ? 1 : 0;
            }

            localTheta = 0;
        }

        qalloc = (int)dec.TellFrac() - tell;
        b -= qalloc;

        int localImid;
        int localIside;
        int localDelta;
        if (localTheta == 0)
        {
            localImid = 32767;
            localIside = 0;
            fill &= (1 << blocks) - 1;
            localDelta = -16384;
        }
        else if (localTheta == 16384)
        {
            localImid = 0;
            localIside = 32767;
            fill &= ((1 << blocks) - 1) << blocks;
            localDelta = 16384;
        }
        else
        {
            localImid = BitexactCos(localTheta);
            localIside = BitexactCos(16384 - localTheta);
            localDelta = FracMul16((n - 1) << 7, BitexactLog2Tan(localIside, localImid));
        }

        inv = localInv;
        imid = localImid;
        iside = localIside;
        delta = localDelta;
        itheta = localTheta;
    }

    /// <summary>Chooses the angle quantizer resolution (RFC 6716 §4.3.4.2).</summary>
    private static int ComputeQn(int n, int b, int offset, int pulseCap, bool stereo)
    {
        int n2 = (2 * n) - 1;
        if (stereo && n == 2)
        {
            n2--;
        }

        if (n2 <= 0)
        {
            return 1;
        }

        int qb = IntDiv(b + (n2 * offset), n2);
        qb = Math.Min(b - pulseCap - (4 << CeltTables.BitRes), qb);
        qb = Math.Min(8 << CeltTables.BitRes, qb);
        if (qb < 1 << CeltTables.BitRes >> 1)
        {
            return 1;
        }

        int qn = CeltTables.Exp2Table8[qb & 0x7] >> (14 - (qb >> CeltTables.BitRes));
        qn = (qn + 1) >> 1 << 1;
        return qn;
    }

    /// <summary>Decodes a PVQ vector and produces the normalized shape (RFC 6716 §4.3.4.2).</summary>
    private int AlgUnquant(ref RangeDecoder dec, Span<float> x, int n, int k, int blocks, float gain)
    {
        // RFC 6716 Appendix A, celt/vq.c: decode_pulses(), then Ryy = sum(iy[i]**2).
        Span<int> iy = _iy.AsSpan(0, n);
        DecodePulses(ref dec, n, k, iy);
        float energy = 0;
        for (int i = 0; i < n; i++)
        {
            energy += iy[i] * iy[i];
        }

        float g = (1f / (float)Math.Sqrt(energy)) * gain;
        for (int i = 0; i < n; i++)
        {
            x[i] = g * iy[i];
        }

        ExpRotation(x, n, -1, blocks, k, _ctx.Spread);
        return ExtractCollapseMask(iy, n, blocks);
    }

    /// <summary>Decodes a PVQ codeword index into its integer vector (RFC 6716 §4.3.4.2).</summary>
    internal static void DecodePulses(ref RangeDecoder dec, int n, int k, Span<int> y)
    {
        uint index = dec.DecodeUInt(PvqV(n, k));
        Cwrsi(n, k, index, y);
    }

    /// <summary>
    /// Returns the number of PVQ codewords <c>V(N,K)</c> (RFC 6716 §4.3.4.2: "V(N,K) =
    /// V(N-1,K) + V(N,K-1) + V(N-1,K-1), with V(N,0) = 1 and V(0,K) = 0, K != 0").
    /// </summary>
    internal static uint PvqV(int n, int k) => PvqTable[n, k];

    /// <summary>
    /// Recovers the integer pulse vector from its codeword index using the five steps of
    /// RFC 6716 §4.3.4.2.
    /// </summary>
    private static void Cwrsi(int n, int k, uint index, Span<int> y)
    {
        uint i = index;
        for (int j = 0; j < n; j++)
        {
            int remaining = n - j;

            // 1. p = (V(N-j-1,k) + V(N-j,k)) / 2.
            uint p = (PvqV(remaining - 1, k) + PvqV(remaining, k)) / 2;

            // 2. If i < p, sgn = 1; otherwise sgn = -1 and i = i - p.
            int sgn;
            if (i < p)
            {
                sgn = 1;
            }
            else
            {
                sgn = -1;
                i -= p;
            }

            // 3. k0 = k and p = p - V(N-j-1,k).
            int k0 = k;
            p -= PvqV(remaining - 1, k);

            // 4. While p > i, set k = k - 1 and p = p - V(N-j-1,k).
            while (p > i && k > 0)
            {
                k--;
                p -= PvqV(remaining - 1, k);
            }

            // 5. X[j] = sgn*(k0 - k) and i = i - p.
            y[j] = sgn * (k0 - k);
            i -= p;
        }
    }

    /// <summary>Decodes the second half of the band splitter's angle (RFC 6716 §4.3.4.2).</summary>
    private static void ExpRotation(Span<float> x, int length, int direction, int stride, int k, int spread)
    {
        if (2 * k >= length || spread == SpreadNone)
        {
            return;
        }

        int factor = CeltTables.SpreadFactor[spread - 1];
        float gain = (float)length / (float)(length + (factor * k));
        float theta = 0.5f * (gain * gain);
        float c = (float)Math.Cos(0.5f * Math.PI * theta);
        float s = (float)Math.Cos(0.5f * Math.PI * (1f - theta));
        int stride2 = 0;
        if (length >= 8 * stride)
        {
            stride2 = 1;
            while ((((stride2 * stride2) + stride2) * stride) + (stride >> 2) < length)
            {
                stride2++;
            }
        }

        int len = length / stride;
        for (int i = 0; i < stride; i++)
        {
            if (direction < 0)
            {
                if (stride2 != 0)
                {
                    ExpRotation1(x.Slice(i * len, len), len, stride2, s, c);
                }

                ExpRotation1(x.Slice(i * len, len), len, 1, c, s);
            }
            else
            {
                ExpRotation1(x.Slice(i * len, len), len, 1, c, -s);
                if (stride2 != 0)
                {
                    ExpRotation1(x.Slice(i * len, len), len, stride2, s, -c);
                }
            }
        }
    }

    /// <summary>Applies one pass of the spread rotation (RFC 6716 §4.3.4.3).</summary>
    private static void ExpRotation1(Span<float> x, int length, int stride, float c, float s)
    {
        int i;
        for (i = 0; i < length - stride; i++)
        {
            float x1 = x[i];
            float x2 = x[i + stride];
            x[i + stride] = (c * x2) + (s * x1);
            x[i] = (c * x1) - (s * x2);
        }

        for (i = length - (2 * stride) - 1; i >= 0; i--)
        {
            float x1 = x[i];
            float x2 = x[i + stride];
            x[i + stride] = (c * x2) + (s * x1);
            x[i] = (c * x1) - (s * x2);
        }
    }

    /// <summary>Normalizes a vector to the requested gain (RFC 6716 §4.3.4.1).</summary>
    private static void RenormaliseVector(Span<float> x, float gain)
    {
        float energy = 1e-15f;
        for (int i = 0; i < x.Length; i++)
        {
            energy += x[i] * x[i];
        }

        float g = (1f / (float)Math.Sqrt(energy)) * gain;
        for (int i = 0; i < x.Length; i++)
        {
            x[i] *= g;
        }
    }

    /// <summary>
    /// Builds the collapse mask from the decoded pulse vector (RFC 6716 Appendix A,
    /// celt/vq.c: extract_collapse_mask).
    /// </summary>
    private static int ExtractCollapseMask(ReadOnlySpan<int> iy, int n, int blocks)
    {
        if (blocks <= 1)
        {
            return 1;
        }

        int perBlock = n / blocks;
        int mask = 0;
        for (int i = 0; i < blocks; i++)
        {
            for (int j = 0; j < perBlock; j++)
            {
                mask |= (iy[(i * perBlock) + j] != 0 ? 1 : 0) << i;
            }
        }

        return mask;
    }

    /// <summary>Reconstructs the two channels from a mid/side band (RFC 6716 §4.3.4.2).</summary>
    private static void StereoMerge(Span<float> x, Span<float> y, float mid, int n)
    {
        float xp = 0;
        float side = 0;
        for (int j = 0; j < n; j++)
        {
            xp += y[j] * x[j];
            side += y[j] * y[j];
        }

        xp = mid * xp;
        float mid2 = mid;
        float el = (mid2 * mid2) + side - (2 * xp);
        float er = (mid2 * mid2) + side + (2 * xp);
        if (er < 6e-4f || el < 6e-4f)
        {
            y.CopyTo(x);
            return;
        }

        float lgain = 1f / (float)Math.Sqrt(el);
        float rgain = 1f / (float)Math.Sqrt(er);
        for (int j = 0; j < n; j++)
        {
            float l = mid * x[j];
            float r = y[j];
            x[j] = lgain * (l - r);
            y[j] = rgain * (l + r);
        }
    }

    /// <summary>Applies the anti-collapse noise fill (RFC 6716 §4.3.5).</summary>
    private void ApplyAntiCollapse(int lm, int start, int end, int c, int frameSamples)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int m = 1 << lm;
        uint seed = _seed;
        for (int i = start; i < end; i++)
        {
            int n0 = eBands[i + 1] - eBands[i];
            int depth = (1 + _pulses[i]) / (n0 << lm);
            float threshold = 0.5f * (float)Math.Exp(0.6931471805599453094 * (-0.125f * depth));
            float sqrt1 = 1f / (float)Math.Sqrt(n0 << lm);
            for (int ch = 0; ch < c; ch++)
            {
                float prev1 = _oldLogE[(ch * BandCount) + i];
                float prev2 = _oldLogE2[(ch * BandCount) + i];
                if (c == 1)
                {
                    prev1 = Math.Max(prev1, _oldLogE[BandCount + i]);
                    prev2 = Math.Max(prev2, _oldLogE2[BandCount + i]);
                }

                float difference = Math.Max(0, _oldBandE[(ch * BandCount) + i] - Math.Min(prev1, prev2));
                float r = 2f * (float)Math.Exp(0.6931471805599453094 * -difference);
                if (lm == 3)
                {
                    r *= 1.41421356f;
                }

                r = Math.Min(threshold, r) * sqrt1;
                int baseOffset = (ch * frameSamples) + (eBands[i] << lm);
                bool renormalise = false;
                for (int k = 0; k < m; k++)
                {
                    if ((_collapseMasks[(i * c) + ch] & (1 << k)) == 0)
                    {
                        for (int j = 0; j < n0; j++)
                        {
                            seed = Lcg(seed);
                            _spectrum[baseOffset + (j << lm) + k] = (seed & 0x8000) != 0 ? r : -r;
                        }

                        renormalise = true;
                    }
                }

                if (renormalise)
                {
                    RenormaliseVector(_spectrum.AsSpan(baseOffset, n0 << lm), 1f);
                }
            }
        }
    }

    /// <summary>Decodes a silent frame: every symbol is the RFC's "all bits used" degenerate case.</summary>
    private void DecodeSilent(ref RangeDecoder dec, int lm, int start, int end, int c, CeltFrameResult result)
    {
        int m = 1 << lm;
        for (int ch = 0; ch < c; ch++)
        {
            for (int i = 0; i < BandCount; i++)
            {
                _oldBandE[(ch * BandCount) + i] = -28;
            }
        }

        Array.Clear(_spectrum);
        Array.Clear(_collapseMasks);
        Array.Clear(_pulses);
        Array.Clear(_tfResolution);
        for (int i = start; i < end; i++)
        {
            _tfResolution[i] = CeltTables.TfSelectTable[lm * 8];
        }

        result.StreamChannels = c;
        result.SampleCount = m * CeltTables.ShortBlockSamples;
        result.Lm = lm;
        result.CodedBands = start;
        result.Spread = SpreadNormal;
        result.Intensity = 0;
        result.DualStereo = 0;
        result.IsSilent = true;
        result.IsTransient = false;
        result.ShortBlocks = 0;
        result.AntiCollapse = false;
        result.PostFilterPitch = 0;
        result.PostFilterTapSet = 0;
        result.PostFilterGain = 0;
        Array.Clear(result.TfResolution);
        Array.Clear(result.Pulses);
        Array.Clear(result.CollapseMasks);
        Array.Copy(_tfResolution, result.TfResolution, BandCount);
        Array.Copy(_oldBandE, result.BandEnergies, 2 * BandCount);
        Array.Clear(result.Spectrum, 0, c * result.SampleCount);

        if (c == 1)
        {
            Array.Copy(_oldBandE, 0, _oldBandE, BandCount, BandCount);
        }

        Array.Copy(_oldLogE, _oldLogE2, 2 * BandCount);
        Array.Copy(_oldBandE, _oldLogE, 2 * BandCount);

        float backgroundIncrease = m * 0.001f;
        for (int i = 0; i < 2 * BandCount; i++)
        {
            _backgroundLogE[i] = Math.Min(_backgroundLogE[i] + backgroundIncrease, _oldBandE[i]);
        }

        for (int ch = 0; ch < 2; ch++)
        {
            for (int i = 0; i < start; i++)
            {
                _oldBandE[(ch * BandCount) + i] = 0;
                _oldLogE[(ch * BandCount) + i] = -28;
                _oldLogE2[(ch * BandCount) + i] = -28;
            }

            for (int i = end; i < BandCount; i++)
            {
                _oldBandE[(ch * BandCount) + i] = 0;
                _oldLogE[(ch * BandCount) + i] = -28;
                _oldLogE2[(ch * BandCount) + i] = -28;
            }
        }

        _postFilterPeriodOld = _postFilterPeriod;
        _postFilterGainOld = _postFilterGainValue;
        _postFilterTapSetOld = _postFilterTapSet;
        _postFilterPeriod = 0;
        _postFilterGainValue = 0;
        _postFilterTapSet = 0;
        if (lm != 0)
        {
            _postFilterPeriodOld = _postFilterPeriod;
            _postFilterGainOld = _postFilterGainValue;
            _postFilterTapSetOld = _postFilterTapSet;
        }

        _seed = dec.Rng;
    }

    /// <summary>Returns the pulse cache row offset for a band at a frame size (RFC 6716 §4.3.3).</summary>
    private static int CacheRow(int lm, int band) => CeltTables.CacheIndex[((lm + 1) * BandCount) + band];

    /// <summary>Chooses the number of pulses that fits a bit budget (RFC 6716 §4.3.3).</summary>
    private int Bits2Pulses(int lm, int bits)
    {
        ReadOnlySpan<byte> cache = CeltTables.CacheBits.Slice(CacheRow(lm, _ctx.Band));
        int lo = 0;
        int hi = cache[0];
        bits--;
        for (int i = 0; i < CeltTables.LogMaxPseudo; i++)
        {
            int mid = (lo + hi + 1) >> 1;
            if (cache[mid] >= bits)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return bits - (lo == 0 ? -1 : cache[lo]) <= cache[hi] - bits ? lo : hi;
    }

    /// <summary>Returns the exact bit cost of a pulse count for the current band (RFC 6716 §4.3.3).</summary>
    private int Pulses2Bits(int lm, int pulses)
    {
        if (pulses == 0)
        {
            return 0;
        }

        return CeltTables.CacheBits[CacheRow(lm, _ctx.Band) + pulses] + 1;
    }

    /// <summary>Copies the first hybrid band's folding data into the second (RFC 6716 §4.3.4.4).</summary>
    private static void SpecialHybridFolding(Span<float> norm, Span<float> norm2, int start, int m, bool dualStereo)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int n1 = m * (eBands[start + 1] - eBands[start]);
        int n2 = m * (eBands[start + 2] - eBands[start + 1]);
        if (n2 <= n1)
        {
            return;
        }

        norm.Slice((2 * n1) - n2, n2 - n1).CopyTo(norm.Slice(n1, n2 - n1));
        if (dualStereo)
        {
            norm2.Slice((2 * n1) - n2, n2 - n1).CopyTo(norm2.Slice(n1, n2 - n1));
        }
    }

    /// <summary>Applies the Haar transform used by the time/frequency reshaping (RFC 6716 §4.3.4.4).</summary>
    private static void Haar1(Span<float> x, int n0, int stride)
    {
        n0 >>= 1;
        for (int i = 0; i < stride; i++)
        {
            for (int j = 0; j < n0; j++)
            {
                float tmp1 = 0.70710678f * x[(stride * (2 * j)) + i];
                float tmp2 = 0.70710678f * x[(stride * ((2 * j) + 1)) + i];
                x[(stride * (2 * j)) + i] = tmp1 + tmp2;
                x[(stride * ((2 * j) + 1)) + i] = tmp1 - tmp2;
            }
        }
    }

    /// <summary>Reorders a Hadamard-transformed band into the frequency layout (RFC 6716 §4.3.4.4).</summary>
    private void DeinterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int count = n0 * stride;
        Span<float> tmp = _reorderScratch.AsSpan(0, count);
        if (hadamard)
        {
            ReadOnlySpan<int> order = CeltTables.OrderyTable.Slice(stride - 2);
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(order[i] * n0) + j] = x[(j * stride) + i];
                }
            }
        }
        else
        {
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(i * n0) + j] = x[(j * stride) + i];
                }
            }
        }

        tmp.CopyTo(x);
    }

    /// <summary>Reorders a Hadamard-transformed band back into time order (RFC 6716 §4.3.4.4).</summary>
    private void InterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        int count = n0 * stride;
        Span<float> tmp = _reorderScratch.AsSpan(0, count);
        if (hadamard)
        {
            ReadOnlySpan<int> order = CeltTables.OrderyTable.Slice(stride - 2);
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(j * stride) + i] = x[(order[i] * n0) + j];
                }
            }
        }
        else
        {
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < n0; j++)
                {
                    tmp[(j * stride) + i] = x[(i * n0) + j];
                }
            }
        }

        tmp.CopyTo(x);
    }

    /// <summary>Bounds- and overflow-safe version of the RFC's Q15 cosine (RFC 6716 §4.3.4.2).</summary>
    private static int BitexactCos(int x)
    {
        int tmp = (4096 + (x * x)) >> 13;
        short x2 = (short)tmp;
        x2 = (short)((32767 - x2) + FracMul16(x2, -7651 + FracMul16(x2, 8277 + FracMul16(-626, x2))));
        return 1 + x2;
    }

    /// <summary>Bit-exact <c>log2(tan)</c> used by the split angle (RFC 6716 §4.3.4.2).</summary>
    private static int BitexactLog2Tan(int isin, int icos)
    {
        int lc = CeltTables.Ilog(icos);
        int ls = CeltTables.Ilog(isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return ((ls - lc) * (1 << 11))
            + FracMul16(isin, FracMul16(isin, -2597) + 7932)
            - FracMul16(icos, FracMul16(icos, -2597) + 7932);
    }

    /// <summary>Q15 fractional multiply of two 16-bit values (RFC 6716 §4.3.4.2).</summary>
    private static int FracMul16(int a, int b) => (16384 + ((short)a * (short)b)) >> 15;

    /// <summary>
    /// Integer square root, exact for the full 32-bit range (RFC 6716 Appendix A,
    /// celt/mathops.c: isqrt32).
    /// </summary>
    private static int Isqrt32(uint value)
    {
        uint g = 0;
        int bShift = (CeltTables.Ilog((int)value) - 1) >> 1;
        uint b = 1u << bShift;
        do
        {
            uint t = (((uint)g << 1) + b) << bShift;
            if (t <= value)
            {
                g += b;
                value -= t;
            }

            b >>= 1;
            bShift--;
        }
        while (bShift >= 0);

        return (int)g;
    }

    /// <summary>The linear congruential generator used for spectral folding (RFC 6716 §4.3.4).</summary>
    private static uint Lcg(uint seed) => (1664525 * seed) + 1013904223;

    /// <summary>Truncating division, as the RFC reference's integer semantics specify.</summary>
    private static int IntDiv(int n, int d) => n / d;

    /// <summary>
    /// <c>V(N,K)</c> from the recurrence in RFC 6716 §4.3.4.2, tabulated for the largest
    /// band (176 bins at 20 ms) and the largest pulse count (128).
    /// </summary>
    private static readonly uint[,] PvqTable = BuildPvqTable();

    private static uint[,] BuildPvqTable()
    {
        const int MaxN = 177;
        const int MaxK = 129;
        uint[,] table = new uint[MaxN, MaxK];
        for (int n = 0; n < MaxN; n++)
        {
            table[n, 0] = 1;
        }

        // V(0,K) = 0 for K != 0. Values above 32 bits cannot occur in a valid codeword and are
        // saturated, which keeps the recurrence inside 32 bits; the saturation cannot affect
        // any V(N,K) that fits the 32-bit index space.
        for (int n = 1; n < MaxN; n++)
        {
            for (int k = 1; k < MaxK; k++)
            {
                ulong value = (ulong)table[n - 1, k] + table[n, k - 1] + table[n - 1, k - 1];
                table[n, k] = value > uint.MaxValue ? uint.MaxValue : (uint)value;
            }
        }

        return table;
    }

    private struct BandContext
    {
        public int Band;
        public int Intensity;
        public int Spread;
        public int TfChange;
        public int RemainingBits;
        public uint Seed;
    }
}
