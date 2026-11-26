// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Entropy;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>
/// The CELT-only encoder of RFC 6716 §4.3: mono or stereo, 8 to 48 kHz API rate, 2.5 to 20 ms
/// frames, constant or variable bitrate, with the pitch pre-filter of §4.3.7.1.
/// </summary>
/// <remarks>
/// <para>
/// The frame pipeline follows RFC 6716 Appendix A (<c>celt_encode_with_ec</c>) symbol for symbol:
/// the zero-stuffing upsampler, pre-emphasis, pitch pre-filter, transient detection, MDCT, band
/// energy, coarse/fine energy quantization, TF, spreading, dynamic allocation, stereo decisions,
/// PVQ and the final range flush. SILK, hybrid, FEC and DTX are absent by design and rejected by
/// the public options type.
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
    private const int SampleRate = InputResampler.InternalSampleRate;
    private const int BitRes = CeltTables.BitRes;
    private const int MaxLm = 3;

    private readonly int _lm;
    private readonly int _internalFrameSamples;
    private readonly int _apiFrameSamples;
    private readonly int _channels;
    private readonly int _upsample;
    private readonly int _end;
    private readonly int _complexity;
    private readonly int _bitrateBps;
    private readonly bool _useVbr;
    private readonly bool _constrainedVbr;

    private readonly Mdct[] _mdct = new Mdct[4];
    private readonly Mdct _shortMdct;
    private readonly CeltEncoderBands _bands = new();
    private readonly CeltPrefilter _prefilter;
    private readonly CeltVbr _vbr = new();
    private readonly float[] _window;

    private readonly float[] _in = new float[2 * (MaxFrameSamples + Overlap)];
    private readonly float[] _frequency = new float[2 * MaxFrameSamples];
    private readonly float[] _spectrum = new float[2 * MaxFrameSamples];
    private readonly float[] _bandEnergy = new float[2 * BandCount];
    private readonly float[] _bandLogE = new float[2 * BandCount];
    private readonly float[] _oldBandE = new float[2 * BandCount];
    private readonly float[] _error = new float[2 * BandCount];
    private readonly float[] _intraOldBandE = new float[2 * BandCount];
    private readonly float[] _intraError = new float[2 * BandCount];
    private readonly float[] _preEmphasisMemory = new float[2];

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
    /// <param name="bitrateBps">The target bitrate.</param>
    /// <param name="complexity">The encoder complexity, 0 to 10.</param>
    /// <param name="channels">The number of interleaved input channels, 1 or 2.</param>
    /// <param name="sampleRate">The API sample rate (8000, 12000, 16000, 24000 or 48000).</param>
    /// <param name="useVbr">Whether to use variable bitrate.</param>
    /// <param name="constrainedVbr">Whether VBR is constrained by the bit reservoir.</param>
    internal CeltEncoder(
        OpusFrameDuration frameDuration,
        int bitrateBps,
        int complexity,
        int channels,
        int sampleRate,
        bool useVbr,
        bool constrainedVbr)
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

        _internalFrameSamples = 120 << _lm;
        _upsample = InputResampler.UpsampleFactor(sampleRate);
        _apiFrameSamples = _internalFrameSamples / _upsample;
        _end = InputResampler.EndBand(sampleRate);
        _channels = channels;
        _bitrateBps = bitrateBps;
        _complexity = complexity;
        _useVbr = useVbr;
        _constrainedVbr = constrainedVbr;
        _prefilter = new CeltPrefilter(channels);
        _window = CeltWindow.Create(Overlap);
        _shortMdct = new Mdct(CeltTables.ShortBlockSamples);
        for (int lm = 0; lm < 4; lm++)
        {
            _mdct[lm] = new Mdct(CeltTables.ShortBlockSamples << lm);
        }

        Array.Fill(_oldBandE, 0f);
    }

    /// <summary>Gets the number of API-rate samples in one input frame per channel.</summary>
    internal int FrameSamples => _apiFrameSamples;

    /// <summary>Gets the number of interleaved input channels.</summary>
    internal int Channels => _channels;

    /// <summary>Gets the encoder final range after the most recent <see cref="EncodeFrame"/>.</summary>
    internal uint FinalRange => _finalRange;

    /// <summary>Gets the largest frame this encoder can emit, in bytes.</summary>
    internal int MaxFrameBytes => MaxPacketBytes;

    /// <summary>Returns the constant-bitrate frame size in bytes for the configured bitrate and frame size.</summary>
    internal int FrameBytes
    {
        get
        {
            long payload = (long)_bitrateBps * _internalFrameSamples;
            int size = (int)((payload + (4L * SampleRate)) / (8L * SampleRate));
            return Math.Max(2, Math.Min(MaxPacketBytes, size));
        }
    }

    /// <summary>
    /// Encodes one frame of interleaved 16-bit PCM into a CELT frame payload (without the TOC
    /// byte) and returns the number of bytes written.
    /// </summary>
    /// <param name="pcm">Exactly <see cref="FrameSamples"/> samples per channel, interleaved.</param>
    /// <param name="destination">
    /// The destination. CBR needs at least <see cref="FrameBytes"/> bytes; VBR may use as many as
    /// the destination holds (up to 1275).
    /// </param>
    /// <returns>The payload size in bytes.</returns>
    internal int EncodeFrame(ReadOnlySpan<short> pcm, Span<byte> destination)
    {
        if (pcm.Length != _apiFrameSamples * _channels)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The frame must contain {_apiFrameSamples * _channels} interleaved samples."),
                nameof(pcm));
        }

        int channels = _channels;
        int n = _internalFrameSamples;
        int lm = _lm;
        int m = 1 << lm;

        int nbCompressedBytes;
        int nbAvailableBytes;
        int effectiveBytes;
        int vbrRate = 0;
        if (_useVbr)
        {
            int maxBytes = Math.Min(MaxPacketBytes, destination.Length);
            if (maxBytes < 2)
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"The destination must hold at least 2 bytes; got {destination.Length}."),
                    nameof(destination));
            }

            nbCompressedBytes = maxBytes;
            nbAvailableBytes = maxBytes;
            vbrRate = CeltVbr.RateFor(_bitrateBps, n);
            effectiveBytes = CeltVbr.EffectiveBytes(vbrRate);
        }
        else
        {
            int frameBytes = FrameBytes;
            if (destination.Length < frameBytes)
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"The destination must hold {frameBytes} bytes."),
                    nameof(destination));
            }

            nbCompressedBytes = frameBytes;
            nbAvailableBytes = frameBytes;
            effectiveBytes = frameBytes;
        }

        var enc = new RangeEncoder(_frameBuffer.AsSpan(0, nbCompressedBytes));
        if (vbrRate > 0 && _constrainedVbr)
        {
            _vbr.ConstrainPreamble(ref enc, vbrRate, enc.Tell(), 0, ref nbCompressedBytes, ref nbAvailableBytes);
        }

        int totalBits = nbCompressedBytes * 8;

        // Pre-emphasis (RFC 6716 Appendix A, celt/celt.c: the prefilter block).
        Span<float> input = _in.AsSpan(0, channels * (n + Overlap));
        CeltEncoderKernels.PreEmphasis(
            pcm, channels, _upsample, n, n + Overlap, Overlap, input, _preEmphasisMemory, out bool silent);

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
            Array.Fill(_oldBandE, -28f);
            _delayedIntra = 200f;
            _spread = 2;
            _consecutiveTransient = 0;
            _finalRange = enc.Rng;
            if (vbrRate > 0)
            {
                int size = Math.Min(nbCompressedBytes, 2);
                enc.Shrink(size);
                nbCompressedBytes = size;
            }

            enc.Done();
            _frameBuffer.AsSpan(0, nbCompressedBytes).CopyTo(destination);
            return nbCompressedBytes;
        }

        bool pfOn = _prefilter.Process(
            ref enc, input, channels, n, nbAvailableBytes, totalBits, _complexity, _tapsetDecision, _window);

        bool isTransient = false;
        int shortBlocks = 0;
        if (lm > 0 && enc.Tell() + 3 <= totalBits)
        {
            if (_complexity > 1)
            {
                isTransient = CeltEncoderKernels.AnalyzeTransient(
                    input, channels, n, Overlap, _transientScratch, _transientBins);
                if (isTransient)
                {
                    shortBlocks = m;
                }
            }

            enc.EncodeBitLogp(isTransient ? 1 : 0, 3);
        }

        ComputeMdcts(shortBlocks, n, input, channels);
        if (_upsample != 1)
        {
            for (int c = 0; c < channels; c++)
            {
                int bound = n / _upsample;
                Span<float> channel = _frequency.AsSpan(c * n, n);
                for (int i = 0; i < bound; i++)
                {
                    channel[i] *= _upsample;
                }

                channel.Slice(bound).Clear();
            }
        }

        ComputeBandEnergies(_end, n, channels);
        CeltEncoderEnergy.Amp2Log2(_bandEnergy, channels, _end, _end, _bandLogE);
        NormaliseBands(_end, n, channels);

        int tfSelect = CeltEncoderAnalysis.TfAnalysis(
            CeltTables.EBands, _end, channels, n, isTransient, _tfResolution, effectiveBytes,
            _spectrum, lm, out int tfSum, _metric, _path0, _path1, _tfScratch);

        CeltEncoderEnergy.QuantCoarseEnergy(
            ref enc, lm, 0, _end, _end, channels, _bandLogE, _oldBandE, _error,
            _intraOldBandE, _intraError, _frameBuffer.AsSpan(0, nbCompressedBytes), _snapshot,
            nbAvailableBytes, totalBits, twoPass: _complexity >= 4, ref _delayedIntra);

        CeltEncoderAnalysis.TfEncode(
            ref enc, 0, _end, isTransient, _tfResolution, lm, tfSelect, nbCompressedBytes);

        _spread = 2;
        if (enc.Tell() + 4 <= totalBits)
        {
            if (shortBlocks != 0 || _complexity < 3 || nbAvailableBytes < 10 * channels)
            {
                if (_complexity == 0)
                {
                    _spread = 0;
                }
            }
            else
            {
                _spread = CeltEncoderAnalysis.SpreadingDecision(
                    CeltTables.EBands, _spectrum, channels, n, ref _tonalAverage, _spread,
                    ref _hfAverage, ref _tapsetDecision, updateHf: pfOn && shortBlocks == 0, _end, m);
            }

            enc.EncodeIcdf(CeltTables.SpreadIcdf, 5, _spread);
        }

        CeltEncoderAnalysis.InitCaps(CeltTables.EBands, lm, channels, _cap);
        _offsets.AsSpan().Clear();
        if (effectiveBytes > 50 && lm >= 1)
        {
            int t1 = lm <= 1 ? 3 : 2;
            int t2 = lm <= 1 ? 5 : 4;
            for (int i = 1; i < _end - 1; i++)
            {
                float d2 = (2f * _bandLogE[i]) - _bandLogE[i - 1] - _bandLogE[i + 1];
                if (channels == 2)
                {
                    d2 = 0.5f
                        * (d2 + (2f * _bandLogE[BandCount + i])
                            - _bandLogE[BandCount + i - 1] - _bandLogE[BandCount + i + 1]);
                }

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

        EncodeDynamicAllocation(ref enc, _end, lm, totalBits, channels);

        int totalBitsFrac = totalBits << BitRes;
        int totalBoost = SumBoosts();
        int allocTrim = 5;
        if ((int)enc.TellFrac() + (6 << BitRes) <= totalBitsFrac - totalBoost)
        {
            allocTrim = CeltEncoderAnalysis.AllocTrimAnalysis(
                _bandLogE, _spectrum, channels, n, _end, lm, n);
            enc.EncodeIcdf(CeltTables.TrimIcdf, 7, allocTrim);
        }

        if (vbrRate > 0)
        {
            int finalSize = _vbr.Update(
                vbrRate, _constrainedVbr, channels, lm, shortBlocks != 0 ? 1 : 0, tfSum,
                0, _end, m, (int)enc.TellFrac(), totalBoost, nbCompressedBytes, 0, ref nbAvailableBytes);
            enc.Shrink(finalSize);
            nbCompressedBytes = finalSize;
        }

        int intensity = 0;
        int dualStereo = 0;
        if (channels == 2)
        {
            if (lm != 0)
            {
                dualStereo = StereoAnalysis(_spectrum, lm, n);
            }

            int effectiveRate = (8 * effectiveBytes - 80) >> lm;
            effectiveRate = 2 * effectiveRate / 5;
            intensity = effectiveRate switch
            {
                < 35 => 8,
                < 50 => 12,
                < 68 => 16,
                < 84 => 18,
                < 102 => 19,
                < 130 => 20,
                _ => 100,
            };
            intensity = Math.Min(_end, Math.Max(0, intensity));
        }

        int bits = ((nbCompressedBytes * 8) << BitRes) - (int)enc.TellFrac() - 1;
        int antiCollapseReserve = isTransient && lm >= 2 && bits >= (lm + 2) << BitRes ? 1 << BitRes : 0;
        bits -= antiCollapseReserve;

        int codedBands = CeltEncoderAllocation.ComputeAllocation(
            ref enc, _bits1, _bits2, _threshold, _trimOffset, 0, _end, lm, channels, bits, allocTrim,
            ref intensity, ref dualStereo, _offsets, _cap, _pulses, _fineQuant, _finePriority,
            out int balance, _lastCodedBands);
        _lastCodedBands = codedBands;

        CeltEncoderEnergy.QuantFineEnergy(ref enc, 0, _end, channels, _oldBandE, _error, _fineQuant);

        _bands.QuantAllBands(
            ref enc, CeltTables.EBands, 0, _end, _spectrum, channels, n, lm, shortBlocks != 0, _spread,
            codedBands, (nbCompressedBytes << (3 + BitRes)) - antiCollapseReserve, balance, _pulses,
            _tfResolution, intensity, dualStereo, _bandEnergy);

        if (antiCollapseReserve > 0)
        {
            int antiCollapseOn = _consecutiveTransient < 2 ? 1 : 0;
            enc.EncodeBits((uint)antiCollapseOn, 1);
        }

        CeltEncoderEnergy.QuantEnergyFinalise(
            ref enc, 0, _end, channels, _oldBandE, _error, _fineQuant, _finePriority,
            (nbCompressedBytes * 8) - enc.Tell());

        _consecutiveTransient = isTransient ? _consecutiveTransient + 1 : 0;
        _finalRange = enc.Rng;
        enc.Done();
        _frameBuffer.AsSpan(0, nbCompressedBytes).CopyTo(destination);
        return nbCompressedBytes;
    }

    private void EncodeDynamicAllocation(ref RangeEncoder enc, int bandCount, int lm, int totalBits, int channels)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int dynAllocLogp = 6;
        int totalBitsFrac = totalBits << BitRes;
        int totalBoost = 0;
        int tellFrac = (int)enc.TellFrac();
        for (int i = 0; i < bandCount; i++)
        {
            int width = channels * (eBands[i + 1] - eBands[i]) << lm;
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

    private void ComputeMdcts(int shortBlocks, int n, ReadOnlySpan<float> input, int channels)
    {
        int stride = n + Overlap;
        if (shortBlocks == 0)
        {
            float scale = 2f / n;
            for (int c = 0; c < channels; c++)
            {
                _mdct[_lm].Forward(
                    input.Slice(c * stride, stride), _frequency.AsSpan(c * n, n));
                Span<float> channel = _frequency.AsSpan(c * n, n);
                for (int k = 0; k < n; k++)
                {
                    channel[k] *= scale;
                }
            }

            return;
        }

        int blocks = shortBlocks;
        int shortSamples = CeltTables.ShortBlockSamples;
        for (int c = 0; c < channels; c++)
        {
            for (int b = 0; b < blocks; b++)
            {
                _shortMdct.Forward(
                    input.Slice((c * stride) + (b * shortSamples), shortSamples + Overlap),
                    _frequency.AsSpan((c * n) + b),
                    blocks);
            }

            float shortScale = 2f / shortSamples;
            Span<float> channel = _frequency.AsSpan(c * n, n);
            for (int k = 0; k < n; k++)
            {
                channel[k] *= shortScale;
            }
        }
    }

    private void ComputeBandEnergies(int end, int n, int channels)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int m = 1 << _lm;
        for (int c = 0; c < channels; c++)
        {
            int channelOffset = c * n;
            int energyOffset = c * BandCount;
            for (int i = 0; i < end; i++)
            {
                int start = channelOffset + (m * eBands[i]);
                int count = m * (eBands[i + 1] - eBands[i]);
                _bandEnergy[energyOffset + i] =
                    MathF.Sqrt(CeltEncoderKernels.BandEnergy(_frequency.AsSpan(start, count)));
            }
        }
    }

    private void NormaliseBands(int end, int n, int channels)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        int m = 1 << _lm;
        for (int c = 0; c < channels; c++)
        {
            int channelOffset = c * n;
            int energyOffset = c * BandCount;
            for (int i = 0; i < end; i++)
            {
                int start = channelOffset + (m * eBands[i]);
                int count = m * (eBands[i + 1] - eBands[i]);
                _frequency.AsSpan(start, count).CopyTo(_spectrum.AsSpan(start, count));
                CeltEncoderKernels.NormaliseBand(_spectrum.AsSpan(start, count), _bandEnergy[energyOffset + i]);
            }
        }
    }

    /// <summary>
    /// Decides between independent (dual) and mid/side stereo coding (RFC 6716 Appendix A,
    /// celt/celt.c: <c>stereo_analysis</c>).
    /// </summary>
    private static int StereoAnalysis(ReadOnlySpan<float> spectrum, int lm, int n)
    {
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        float sumLr = 1e-15f;
        float sumMs = 1e-15f;
        int m = 1 << lm;
        for (int i = 0; i < 13; i++)
        {
            int start = eBands[i] << lm;
            int stop = eBands[i + 1] << lm;
            for (int j = start; j < stop; j++)
            {
                float left = spectrum[j];
                float right = spectrum[n + j];
                float mid = left + right;
                float side = left - right;
                sumLr += MathF.Abs(left) + MathF.Abs(right);
                sumMs += MathF.Abs(mid) + MathF.Abs(side);
            }
        }

        sumMs *= 0.707107f;
        int thetas = 13;
        if (lm <= 1)
        {
            thetas -= 8;
        }

        return ((eBands[13] << (lm + 1)) + thetas) * sumMs > (eBands[13] << (lm + 1)) * sumLr ? 1 : 0;
    }
}