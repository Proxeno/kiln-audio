// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Entropy;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>
/// The CELT-only encoder of RFC 6716 §4.3: a single-channel, 48 kHz encoder for
/// <see cref="OpusApplication.RestrictedLowDelay"/>.
/// </summary>
/// <remarks>
/// <para>
/// The frame pipeline follows RFC 6716 Appendix A (<c>celt_encode_with_ec</c>) symbol for symbol,
/// restricted to the paths that a mono, full-band, CBR CELT frame takes: pre-emphasis, transient
/// detection, MDCT, band energy, coarse/fine energy quantization, TF, spreading, dynamic
/// allocation, PVQ and the final range flush. Stereo, hybrid, FEC and DTX are absent by design and
/// rejected by the public options type.
/// </para>
/// <para>
/// The encoder allocates nothing after construction. All buffers are fixed-size fields and every
/// kernel writes into them.
/// </para>
/// </remarks>
internal sealed class CeltEncoder
{
    private const int BandCount = CeltTables.BandCount;
    private const int Overlap = CeltWindow.DefaultOverlap;
    private const int MaxFrameSamples = CeltTables.MaxFrameSamples;
    private const int MaxPacketBytes = 1275;
    private const int SampleRate = 48000;
    private const int BitRes = CeltTables.BitRes;

    private readonly int _lm;
    private readonly int _frameSamples;
    private readonly int _complexity;
    private readonly int _bitrateBps;

    private readonly Mdct[] _mdct = new Mdct[4];
    private readonly Mdct _shortMdct;
    private readonly CeltEncoderBands _bands = new();

    private readonly float[] _in = new float[MaxFrameSamples + Overlap];
    private readonly float[] _frequency = new float[MaxFrameSamples];
    private readonly float[] _spectrum = new float[MaxFrameSamples];
    private readonly float[] _bandEnergy = new float[BandCount];
    private readonly float[] _bandLogE = new float[BandCount];
    private readonly float[] _oldBandE = new float[BandCount];
    private readonly float[] _error = new float[BandCount];
    private readonly float[] _intraOldBandE = new float[BandCount];
    private readonly float[] _intraError = new float[BandCount];
    private readonly float[] _inMemory = new float[Overlap];

    private readonly float[] _transientScratch = new float[MaxFrameSamples + Overlap];
    private readonly float[] _transientBins = new float[(MaxFrameSamples + Overlap) / (Overlap / 2)];
    private readonly float[] _tfScratch = new float[512];
    private readonly int[] _metric = new int[BandCount];
    private readonly int[] _path0 = new int[BandCount];
    private readonly int[] _path1 = new int[BandCount];
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

    private readonly byte[] _frameBuffer = new byte[MaxPacketBytes];
    private readonly byte[] _snapshot = new byte[MaxPacketBytes];

    private float _preEmphasisMemory;
    private float _delayedIntra = 1f;
    private int _tonalAverage = 256;
    private int _hfAverage;
    private int _tapsetDecision;
    private int _spread = 2;
    private int _consecutiveTransient;
    private int _lastCodedBands;
    private uint _finalRange;

    /// <summary>Creates an encoder for one CELT configuration.</summary>
    /// <param name="frameDuration">The frame duration; must be 2.5, 5, 10 or 20 ms.</param>
    /// <param name="bitrateBps">The CBR target bitrate.</param>
    /// <param name="complexity">The encoder complexity, 0 to 10.</param>
    internal CeltEncoder(OpusFrameDuration frameDuration, int bitrateBps, int complexity)
    {
        _lm = frameDuration switch
        {
            OpusFrameDuration.Ms2_5 => 0,
            OpusFrameDuration.Ms5 => 1,
            OpusFrameDuration.Ms10 => 2,
            OpusFrameDuration.Ms20 => 3,
            _ => throw new NotSupportedException(
                FormattableString.Invariant(
                    $"CELT supports frame durations of 2.5, 5, 10 and 20 ms; got {frameDuration}.")),
        };

        _frameSamples = 120 << _lm;
        _bitrateBps = bitrateBps;
        _complexity = complexity;
        _shortMdct = new Mdct(CeltTables.ShortBlockSamples);
        for (int lm = 0; lm < 4; lm++)
        {
            _mdct[lm] = new Mdct(CeltTables.ShortBlockSamples << lm);
        }

        Array.Fill(_oldBandE, 0f);
    }

    /// <summary>Gets the number of samples in one input frame.</summary>
    internal int FrameSamples => _frameSamples;

    /// <summary>Gets the encoder final range after the most recent <see cref="EncodeFrame"/>.</summary>
    internal uint FinalRange => _finalRange;

    /// <summary>Returns the CBR frame size in bytes for the configured bitrate and frame size.</summary>
    internal int FrameBytes
    {
        get
        {
            long payload = (long)_bitrateBps * _frameSamples;
            int size = (int)((payload + (4L * SampleRate)) / (8L * SampleRate));
            return Math.Max(2, Math.Min(MaxPacketBytes, size));
        }
    }

    /// <summary>
    /// Encodes one frame of pre-emphasized-domain PCM into a CELT frame payload (without the TOC
    /// byte) and returns the number of bytes written.
    /// </summary>
    /// <param name="pcm">Exactly <see cref="FrameSamples"/> samples.</param>
    /// <param name="destination">At least <see cref="FrameBytes"/> bytes.</param>
    /// <returns>The payload size in bytes.</returns>
    internal int EncodeFrame(ReadOnlySpan<short> pcm, Span<byte> destination)
    {
        if (pcm.Length != _frameSamples)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The frame must contain {_frameSamples} samples."), nameof(pcm));
        }

        int frameBytes = FrameBytes;
        if (destination.Length < frameBytes)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The destination must hold {frameBytes} bytes."), nameof(destination));
        }

        int lm = _lm;
        int m = 1 << lm;
        int n = _frameSamples;

        var enc = new RangeEncoder(_frameBuffer.AsSpan(0, frameBytes));
        int totalBits = frameBytes * 8;

        // Pre-emphasis (RFC 6716 Appendix A, celt/celt.c: the prefilter block).
        Span<float> input = _in.AsSpan(0, n + Overlap);
        _inMemory.AsSpan().CopyTo(input);
        CeltEncoderKernels.PreEmphasis(pcm, input.Slice(Overlap, n), ref _preEmphasisMemory, out bool silent);
        input.Slice(n, Overlap).CopyTo(_inMemory);

        // Silence is reported once, on the first symbol of a fresh frame.
        if (enc.Tell() == 1)
        {
            enc.EncodeBitLogp(silent ? 1 : 0, 15);
        }
        else
        {
            silent = false;
        }

        if (silent)
        {
            _oldBandE.AsSpan().Fill(-28f);
            _delayedIntra = 200f;
            _spread = 2;
            _consecutiveTransient = 0;
            _finalRange = enc.Rng;
            enc.Done();
            _frameBuffer.AsSpan(0, frameBytes).CopyTo(destination);
            return frameBytes;
        }

        // Pitch pre-filter is not used (see docs/performance.md): signal it off when there is
        // room, exactly as the reference does when the gain is below its threshold.
        if (enc.Tell() + 16 <= totalBits)
        {
            enc.EncodeBitLogp(0, 1);
        }

        bool isTransient = false;
        int shortBlocks = 0;
        if (lm > 0 && enc.Tell() + 3 <= totalBits)
        {
            if (_complexity > 1)
            {
                isTransient = CeltEncoderKernels.AnalyzeTransient(input, _transientScratch, _transientBins, Overlap);
                if (isTransient)
                {
                    shortBlocks = m;
                }
            }

            enc.EncodeBitLogp(isTransient ? 1 : 0, 3);
        }

        ComputeMdcts(shortBlocks, n, input);
        ComputeBandEnergies(end: BandCount);
        CeltEncoderEnergy.Amp2Log2(_bandEnergy, BandCount, BandCount, _bandLogE);
        NormaliseBands(BandCount);

        int tfSelect = CeltEncoderAnalysis.TfAnalysis(
            CeltTables.EBands, BandCount, isTransient, _tfResolution, frameBytes, _spectrum, lm, out _,
            _metric, _path0, _path1, _tfScratch);

        CeltEncoderEnergy.QuantCoarseEnergy(
            ref enc, lm, 0, BandCount, BandCount, _bandLogE, _oldBandE, _error,
            _intraOldBandE, _intraError, _frameBuffer.AsSpan(0, frameBytes), _snapshot,
            frameBytes, totalBits, twoPass: _complexity >= 4, ref _delayedIntra);

        CeltEncoderAnalysis.TfEncode(
            ref enc, 0, BandCount, isTransient, _tfResolution, lm, tfSelect, frameBytes);

        _spread = 2;
        if (enc.Tell() + 4 <= totalBits)
        {
            if (shortBlocks != 0 || _complexity < 3 || frameBytes < 10)
            {
                if (_complexity == 0)
                {
                    _spread = 0;
                }
            }
            else
            {
                _spread = CeltEncoderAnalysis.SpreadingDecision(
                    CeltTables.EBands, _spectrum, ref _tonalAverage, _spread, ref _hfAverage,
                    ref _tapsetDecision, updateHf: false, BandCount, m);
            }

            enc.EncodeIcdf(CeltTables.SpreadIcdf, 5, _spread);
        }

        CeltEncoderAnalysis.InitCaps(CeltTables.EBands, lm, 1, _cap);
        _offsets.AsSpan().Clear();
        if (frameBytes > 50 && lm >= 1)
        {
            int t1 = lm <= 1 ? 3 : 2;
            int t2 = lm <= 1 ? 5 : 4;
            for (int i = 1; i < BandCount - 1; i++)
            {
                float d2 = (2f * _bandLogE[i]) - _bandLogE[i - 1] - _bandLogE[i + 1];
                if (d2 > t1)
                {
                    _offsets[i]++;
                }

                if (d2 > t2)
                {
                    _offsets[i]++;
                }
            }
        }

        EncodeDynamicAllocation(ref enc, bandCount: BandCount, lm, totalBits);

        int totalBitsFrac = totalBits << BitRes;
        int totalBoost = SumBoosts();
        int allocTrim = 5;
        if ((int)enc.TellFrac() + (6 << BitRes) <= totalBitsFrac - totalBoost)
        {
            allocTrim = CeltEncoderAnalysis.AllocTrimAnalysis(_bandLogE, BandCount);
            enc.EncodeIcdf(CeltTables.TrimIcdf, 7, allocTrim);
        }

        int bits = (totalBits << BitRes) - (int)enc.TellFrac() - 1;
        int antiCollapseReserve = isTransient && lm >= 2 && bits >= (lm + 2) << BitRes ? 1 << BitRes : 0;
        bits -= antiCollapseReserve;

        int intensity = 0;
        int dualStereo = 0;
        int codedBands = CeltEncoderAllocation.ComputeAllocation(
            ref enc, _bits1, _bits2, _threshold, _trimOffset, 0, BandCount, lm, 1, bits, allocTrim,
            ref intensity, ref dualStereo, _offsets, _cap, _pulses, _fineQuant, _finePriority,
            out int balance, _lastCodedBands);
        _lastCodedBands = codedBands;

        CeltEncoderEnergy.QuantFineEnergy(ref enc, 0, BandCount, _oldBandE, _error, _fineQuant);

        _bands.QuantAllBands(
            ref enc, CeltTables.EBands, 0, BandCount, _spectrum, lm, shortBlocks != 0, _spread,
            codedBands, (frameBytes << (3 + BitRes)) - antiCollapseReserve, balance, _pulses, _tfResolution);

        int antiCollapseOn = 0;
        if (antiCollapseReserve > 0)
        {
            antiCollapseOn = _consecutiveTransient < 2 ? 1 : 0;
            enc.EncodeBits((uint)antiCollapseOn, 1);
        }

        CeltEncoderEnergy.QuantEnergyFinalise(
            ref enc, 0, BandCount, _oldBandE, _error, _fineQuant, _finePriority,
            (frameBytes * 8) - enc.Tell());

        _consecutiveTransient = isTransient ? _consecutiveTransient + 1 : 0;
        _finalRange = enc.Rng;
        enc.Done();
        _frameBuffer.AsSpan(0, frameBytes).CopyTo(destination);
        return frameBytes;
    }

    private void EncodeDynamicAllocation(ref RangeEncoder enc, int bandCount, int lm, int totalBits)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int dynAllocLogp = 6;
        int totalBitsFrac = totalBits << BitRes;
        int totalBoost = 0;
        int tellFrac = (int)enc.TellFrac();
        for (int i = 0; i < bandCount; i++)
        {
            int width = (eBands[i + 1] - eBands[i]) << lm;
            int quanta = Math.Min(width << BitRes, Math.Max(6 << BitRes, width));
            int loopLogp = dynAllocLogp;
            int boost = 0;
            int j = 0;
            for (; tellFrac + (loopLogp << BitRes) < totalBitsFrac - totalBoost && boost < _cap[i]; j++)
            {
                int flag = j < _offsets[i] ? 1 : 0;
                enc.EncodeBitLogp(flag, loopLogp);
                tellFrac = (int)enc.TellFrac();
                if (flag == 0)
                {
                    break;
                }

                boost += quanta;
                totalBoost += quanta;
                loopLogp = 1;
            }

            if (j != 0)
            {
                dynAllocLogp = Math.Max(2, dynAllocLogp - 1);
            }

            _offsets[i] = boost;
        }
    }

    private int SumBoosts()
    {
        int total = 0;
        for (int i = 0; i < _offsets.Length; i++)
        {
            total += _offsets[i];
        }

        return total;
    }

    private void ComputeMdcts(int shortBlocks, int n, ReadOnlySpan<float> input)
    {
        if (shortBlocks == 0)
        {
            _mdct[_lm].Forward(input.Slice(0, n + Overlap), _frequency.AsSpan(0, n));
            float scale = 2f / n;
            for (int k = 0; k < n; k++)
            {
                _frequency[k] *= scale;
            }

            return;
        }

        int blocks = shortBlocks;
        int shortSamples = CeltTables.ShortBlockSamples;
        for (int b = 0; b < blocks; b++)
        {
            _shortMdct.Forward(input.Slice(b * shortSamples, shortSamples + Overlap), _frequency.AsSpan(b), blocks);
        }

        float shortScale = 2f / shortSamples;
        for (int k = 0; k < n; k++)
        {
            _frequency[k] *= shortScale;
        }
    }

    private void ComputeBandEnergies(int end)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int m = 1 << _lm;
        for (int i = 0; i < end; i++)
        {
            int start = m * eBands[i];
            int count = m * (eBands[i + 1] - eBands[i]);
            _bandEnergy[i] = MathF.Sqrt(CeltEncoderKernels.BandEnergy(_frequency.AsSpan(start, count)));
        }
    }

    private void NormaliseBands(int end)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int m = 1 << _lm;
        for (int i = 0; i < end; i++)
        {
            int start = m * eBands[i];
            int count = m * (eBands[i + 1] - eBands[i]);
            _frequency.AsSpan(start, count).CopyTo(_spectrum.AsSpan(start, count));
            CeltEncoderKernels.NormaliseBand(_spectrum.AsSpan(start, count), _bandEnergy[i]);
        }
    }
}
