// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

namespace Kiln.Audio.Celt;

/// <summary>
/// Turns a decoded CELT frame into 48 kHz PCM for the RFC 6716 §4.3.6–§4.3.7 synthesis chain.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="CeltDecoder"/> stops at the normalized spectrum. This class performs the
/// remaining stages in bitstream order: denormalization by band energy (§4.3.6), the inverse
/// MDCT with overlap-add (§4.3.7), the pitch post-filter with its parameter cross-fade
/// (§4.3.7.1) and de-emphasis (§4.3.7.2). Anti-collapse (§4.3.5) has already been applied to
/// the spectrum by the decoder, so it is not repeated here.
/// </para>
/// <para>
/// <see cref="Synthesize"/> was chosen over an output span on <c>DecodeFrame</c> because the
/// synthesis chain owns a large amount of cross-frame state (MDCT overlap, post-filter history,
/// de-emphasis memory, and a rolling 2048-sample per-channel history), and keeping it separate
/// lets the bitstream decoder stay allocation-free and independently testable. The
/// synthesized PCM is interleaved and normalized to [-1, 1]; use <see cref="CeltPcm.ToInt16"/>
/// for the saturating float-to-int16 step.
/// </para>
/// <para>
/// RFC 8251 errata applied here: §8 caps the denormalization exponent at 32, and §10's
/// 180-degree intensity-stereo phase inversion is left enabled (the default, matching the
/// <c>.dec</c> reference decodes rather than the <c>m.dec</c> variants).
/// </para>
/// <para>
/// The reference keeps post-filter history in a rolling <c>DECODE_BUFFER_SIZE</c> (2048)
/// buffer that is shifted left by the frame length each frame; this class does the same, which
/// makes the in-place comb filter read the already-filtered current frame exactly as
/// <c>comb_filter()</c> does. Instances are not thread-safe and allocate nothing after
/// construction.
/// </para>
/// </remarks>
internal sealed class CeltSynthesizer
{
    private const int Overlap = CeltWindow.DefaultOverlap;
    private const int HistorySize = 2048;
    private const int MaxFrameSamples = CeltTables.MaxFrameSamples;

    private readonly int _outputChannels;
    private readonly Mdct _shortMdct;
    private readonly Mdct[] _mdct = new Mdct[4];
    private readonly float[] _freq = new float[2 * MaxFrameSamples];
    private readonly float[] _block = new float[MaxFrameSamples + Overlap];
    private readonly float[] _imdct = new float[MaxFrameSamples + Overlap];
    private readonly float[] _rawOutput = new float[MaxFrameSamples];
    private readonly float[][] _rawOverlap = new float[2][];
    private readonly float[][] _history = new float[2][];
    private readonly float[] _deemphasis = new float[2];
    private readonly float[] _windowSquare = new float[Overlap];

    /// <summary>Initializes a synthesizer for the given output channel count.</summary>
    /// <param name="outputChannels">Output channels, 1 or 2.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outputChannels"/> is not 1 or 2.</exception>
    internal CeltSynthesizer(int outputChannels)
    {
        if (outputChannels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputChannels), outputChannels, "Output channels must be 1 or 2.");
        }

        _outputChannels = outputChannels;
        _shortMdct = new Mdct(CeltTables.ShortBlockSamples);
        for (int lm = 0; lm < _mdct.Length; lm++)
        {
            _mdct[lm] = new Mdct(CeltTables.ShortBlockSamples << lm);
        }

        float[] rising = CeltWindow.Create(Overlap);
        for (int i = 0; i < Overlap; i++)
        {
            _windowSquare[i] = rising[i] * rising[i];
        }

        for (int channel = 0; channel < 2; channel++)
        {
            _rawOverlap[channel] = new float[Overlap];
            _history[channel] = new float[HistorySize];
        }
    }

    /// <summary>Gets the output channel count this synthesizer produces.</summary>
    internal int OutputChannels => _outputChannels;

    /// <summary>Clears every cross-frame synthesis state, as after a mode switch or reset.</summary>
    internal void Reset()
    {
        for (int channel = 0; channel < 2; channel++)
        {
            Array.Clear(_rawOverlap[channel]);
            Array.Clear(_history[channel]);
        }

        Array.Clear(_deemphasis);
        Array.Clear(_freq);
        Array.Clear(_block);
        Array.Clear(_imdct);
        Array.Clear(_rawOutput);
    }

    /// <summary>Produces silence for a lost frame until packet loss concealment is implemented.</summary>
    /// <param name="lm">Base-2 logarithm of the number of short MDCT blocks.</param>
    /// <param name="pcmOut">Destination for interleaved normalized PCM.</param>
    /// <remarks>
    /// This is the hook required by RFC 6716 §4.4. Packet loss concealment (the pitch-based
    /// waveform repetition of <c>celt_decode_lost()</c>) is a later lane; until then a lost
    /// frame contributes silence of the correct duration.
    /// </remarks>
    internal void DecodeLost(int lm, Span<float> pcmOut)
    {
        if (lm is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(lm), lm, "LM must be 0 to 3.");
        }

        int count = (CeltTables.ShortBlockSamples << lm) * _outputChannels;
        if (pcmOut.Length < count)
        {
            throw new ArgumentException("The PCM destination is too short for the frame.", nameof(pcmOut));
        }

        pcmOut[..count].Clear();
    }

    /// <summary>
    /// Synthesizes one decoded CELT frame into interleaved normalized 48 kHz PCM.
    /// </summary>
    /// <param name="frame">The frame produced by <see cref="CeltDecoder.DecodeFrame"/>.</param>
    /// <param name="pcmOut">
    /// Destination for <c>SampleCount * OutputChannels</c> interleaved samples in [-1, 1].
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pcmOut"/> is too short.</exception>
    internal void Synthesize(CeltFrameResult frame, Span<float> pcmOut)
    {
        ArgumentNullException.ThrowIfNull(frame);

        int samples = frame.SampleCount;
        int streamChannels = frame.StreamChannels;
        int channels = _outputChannels;
        if (samples is < CeltTables.ShortBlockSamples or > MaxFrameSamples)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"invalid frame sample count {samples} (lm {frame.Lm}, start {frame.StartBand}, end {frame.EndBand})"));
        }
        if (pcmOut.Length < samples * channels)
        {
            throw new ArgumentException(
                $"The PCM destination must hold {samples * channels} samples.", nameof(pcmOut));
        }

        Denormalise(frame, samples, streamChannels, channels);

        for (int channel = 0; channel < channels; channel++)
        {
            BuildTimeSignal(frame, samples, channel);
            ApplyPostFilter(frame, samples, channel);
            Deemphasise(samples, channel, pcmOut, channels);
        }
    }

    /// <summary>Denormalizes the normalized bands and maps the stream channels to the output channels.</summary>
    private void Denormalise(CeltFrameResult frame, int samples, int streamChannels, int channels)
    {
        // The coded stream may have more channels than the output (a stereo stream decoded to
        // mono): the working span covers the larger of the two so both coded channels fit before
        // the downmix.
        Span<float> freq = _freq.AsSpan(0, Math.Max(channels, streamChannels) * samples);
        freq.Clear();

        ReadOnlySpan<short> eBands = CeltTables.EBands;
        ReadOnlySpan<float> eMeans = CeltTables.EMeans;
        int m = 1 << frame.Lm;
        for (int channel = 0; channel < streamChannels; channel++)
        {
            for (int i = frame.StartBand; i < frame.EndBand; i++)
            {
                float logEnergy = frame.BandEnergies[(channel * CeltTables.BandCount) + i] + eMeans[i];
                if (logEnergy > 32f)
                {
                    // RFC 8251 §8: cap the log-domain energy before exponentiation.
                    logEnergy = 32f;
                }

                float gain = (float)Math.Exp(0.6931471805599453094 * logEnergy);
                int start = m * eBands[i];
                int end = m * eBands[i + 1];
                for (int j = start; j < end; j++)
                {
                    freq[(channel * samples) + j] = frame.Spectrum[(channel * samples) + j] * gain;
                }
            }
        }

        if (channels == 2 && streamChannels == 1)
        {
            freq.Slice(0, samples).CopyTo(freq.Slice(samples, samples));
        }
        else if (channels == 1 && streamChannels == 2)
        {
            for (int j = 0; j < samples; j++)
            {
                freq[j] = 0.5f * (freq[j] + freq[samples + j]);
            }
        }
    }

    /// <summary>Runs the inverse MDCT for one channel and does the overlap-add with the previous frame.</summary>
    private void BuildTimeSignal(CeltFrameResult frame, int samples, int channel)
    {
        if (samples + Overlap > _block.Length)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"samples {samples} overlap {Overlap} block {_block.Length}"));
        }

        Span<float> block = _block.AsSpan(0, samples + Overlap);
        block.Clear();

        ReadOnlySpan<float> spectrum = _freq.AsSpan(channel * samples, samples);
        if (frame.ShortBlocks != 0)
        {
            int blocks = frame.ShortBlocks;
            float scale = _shortMdct.CoefficientCount * 0.5f;
            for (int b = 0; b < blocks; b++)
            {
                _shortMdct.Inverse(spectrum[b..], _imdct, stride: blocks);
                int offset = b * CeltTables.ShortBlockSamples;
                for (int k = 0; k < _shortMdct.Length; k++)
                {
                    block[offset + k] += scale * _imdct[k];
                }
            }
        }
        else
        {
            Mdct mdct = _mdct[frame.Lm];
            mdct.Inverse(spectrum, _imdct);
            float scale = mdct.CoefficientCount * 0.5f;
            for (int k = 0; k < mdct.Length; k++)
            {
                block[k] += scale * _imdct[k];
            }
        }

        float[] overlap = _rawOverlap[channel];
        Span<float> raw = _rawOutput.AsSpan(0, samples);
        for (int j = 0; j < Overlap; j++)
        {
            raw[j] = block[j] + overlap[j];
            overlap[j] = block[samples + j];
        }

        for (int j = Overlap; j < samples; j++)
        {
            raw[j] = block[j];
        }

        float[] history = _history[channel];
        Array.Copy(history, samples, history, 0, HistorySize - samples);
        raw.CopyTo(history.AsSpan(HistorySize - samples, samples));
    }

    /// <summary>Applies the §4.3.7.1 post-filter, cross-fading the parameter sets in place.</summary>
    private void ApplyPostFilter(CeltFrameResult frame, int samples, int channel)
    {
        float[] history = _history[channel];
        int baseIndex = HistorySize - samples;

        // The reference clamps the two historical periods before use; the newly decoded pitch
        // is used as-is (it is at least the minimum whenever its gain is non-zero).
        int oldPeriod = Math.Max(frame.PostFilterPitchOld, CeltTables.CombFilterMinPeriod);
        int previousPeriod = Math.Max(frame.PostFilterPitchPrevious, CeltTables.CombFilterMinPeriod);
        int currentPeriod = frame.PostFilterPitch;

        // First overlap: cross-fade the older pair into the previous frame's parameters.
        CombFilter(
            history,
            baseIndex,
            Overlap,
            oldPeriod,
            previousPeriod,
            frame.PostFilterGainOld,
            frame.PostFilterGainPrevious,
            frame.PostFilterTapSetOld,
            frame.PostFilterTapSetPrevious);

        if (frame.Lm != 0)
        {
            // Rest of the frame: cross-fade the previous parameters into this frame's.
            CombFilter(
                history,
                baseIndex + Overlap,
                samples - Overlap,
                previousPeriod,
                currentPeriod,
                frame.PostFilterGainPrevious,
                frame.PostFilterGain,
                frame.PostFilterTapSetPrevious,
                frame.PostFilterTapSet);
        }
    }

    /// <summary>Applies one in-place comb-filter pass with a squared-window parameter cross-fade.</summary>
    internal void CombFilter(
        float[] history,
        int frameStart,
        int count,
        int period0,
        int period1,
        float gain0,
        float gain1,
        int tapSet0,
        int tapSet1)
    {
        ReadOnlySpan<float> taps0 = CeltTables.PostFilterGains.Slice(tapSet0 * 3, 3);
        ReadOnlySpan<float> taps1 = CeltTables.PostFilterGains.Slice(tapSet1 * 3, 3);

        float g00 = gain0 * taps0[0];
        float g01 = gain0 * taps0[1];
        float g02 = gain0 * taps0[2];
        float g10 = gain1 * taps1[0];
        float g11 = gain1 * taps1[1];
        float g12 = gain1 * taps1[2];
        bool use0 = g00 != 0f || g01 != 0f || g02 != 0f;
        bool use1 = g10 != 0f || g11 != 0f || g12 != 0f;

        for (int i = 0; i < count; i++)
        {
            int position = frameStart + i;
            float value = history[position];
            if (i < Overlap)
            {
                float fade = _windowSquare[i];
                if (use0)
                {
                    float weight = 1f - fade;
                    value += weight * (
                        (g00 * history[position - period0])
                        + (g01 * (history[position - period0 - 1] + history[position - period0 + 1]))
                        + (g02 * (history[position - period0 - 2] + history[position - period0 + 2])));
                }

                if (use1)
                {
                    value += fade * (
                        (g10 * history[position - period1])
                        + (g11 * (history[position - period1 - 1] + history[position - period1 + 1]))
                        + (g12 * (history[position - period1 - 2] + history[position - period1 + 2])));
                }
            }
            else if (use1)
            {
                value +=
                    (g10 * history[position - period1])
                    + (g11 * (history[position - period1 - 1] + history[position - period1 + 1]))
                    + (g12 * (history[position - period1 - 2] + history[position - period1 + 2]));
            }

            history[position] = value;
        }
    }

    /// <summary>Applies the §4.3.7.2 de-emphasis filter and writes the interleaved output.</summary>
    private void Deemphasise(int samples, int channel, Span<float> pcmOut, int channels)
    {
        float[] history = _history[channel];
        int baseIndex = HistorySize - samples;
        float memory = _deemphasis[channel];
        for (int j = 0; j < samples; j++)
        {
            float value = DeemphasiseSample(history[baseIndex + j], ref memory);
            pcmOut[(j * channels) + channel] = value * (1f / CeltTables.SignalScale);
        }

        _deemphasis[channel] = memory;
    }

    /// <summary>
    /// Applies one de-emphasis step (RFC 6716 §4.3.7.2) and updates the filter memory.
    /// </summary>
    /// <param name="input">The post-filtered sample.</param>
    /// <param name="memory">The filter memory: its current value on entry, the updated value on exit.</param>
    /// <returns>The de-emphasized sample, still in the reference's internal scale.</returns>
    internal static float DeemphasiseSample(float input, ref float memory)
    {
        float value = input + memory;
        memory = CeltTables.DeemphasisCoefficient * value;
        return value;
    }
}
