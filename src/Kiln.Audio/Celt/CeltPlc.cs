// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Celt;

/// <summary>
/// Packet-loss concealment for the CELT layer (RFC 6716 §4.4; Appendix A,
/// <c>celt_decode_lost()</c> in <c>celt/celt.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is the second half of <see cref="CeltSynthesizer"/>. For the first four consecutive
/// losses it repeats the most recent pitch period: a pitch search over the last 2048 synthesis
/// samples picks the period, the last 1024 samples are whitened with an LPC filter estimated
/// from them, the resulting excitation is repeated with a decay, and the whitened copy is fed
/// back through the LPC synthesis filter. The sample at the end of the previous frame is used as
/// the overlap so the time-domain alias cancellation with the next good frame still holds, and
/// the post-filter is applied to the overlap region exactly as the reference does.
/// </para>
/// <para>
/// From the fifth consecutive loss, and for a hybrid frame (whose low band is concealed by SILK),
/// the decoder switches to shaped noise with an energy decay. Both paths run the de-emphasis
/// filter, so the next good frame continues from the concealed history.
/// </para>
/// <para>
/// All scratch state is allocated once in the constructor, so concealment allocates nothing.
/// The pitch search and LPC helpers are the float forms of the reference routines at the same
/// scale (<c>SCALEOUT</c> only happens in de-emphasis).
/// </para>
/// </remarks>
internal sealed partial class CeltSynthesizer
{
    private const int LpcOrder = 24;
    private const int MaxPeriod = 1024;
    private const int DecodeHistorySize = 2048;
    private const int PitchDownsampleOffset = 720;
    private const int PitchMaxSearchPeriod = 620;

    private readonly float[] _plcWindow = CeltWindow.Create(Overlap);
    private readonly float[] _plcOutMem = new float[MaxPeriod + Overlap + 2];
    private const int OutMemOffset = 2;
    private readonly float[] _plcPitchBuf = new float[DecodeHistorySize >> 1];
    private readonly float[] _plcPitchXlp4 = new float[DecodeHistorySize >> 2];
    private readonly float[] _plcPitchYlp4 = new float[(DecodeHistorySize + DecodeHistorySize) >> 2];
    private readonly float[] _plcPitchXcorr = new float[PitchMaxSearchPeriod >> 1];
    private readonly float[] _plcExcitation = new float[MaxPeriod];
    private readonly float[] _plcCopy = new float[MaxPeriod + (2 * Overlap)];
    private readonly float[] _plcAutocorrScratch = new float[DecodeHistorySize];
    private readonly float[] _plcLpc = new float[2 * LpcOrder];
    private readonly float[] _plcAc = new float[LpcOrder + 1];
    private readonly float[] _plcMem = new float[LpcOrder];
    private readonly float[] _plcOldBandE = new float[2 * CeltTables.BandCount];
    private readonly CeltFrameResult _plcFrame = new();

    private int _plcLossCount;
    private int _plcLastPitchIndex;
    private int _plcStartBand;
    private int _plcEndBand;
    private int _plcLm;
    private int _plcStreamChannels;
    private int _plcPostFilterPeriod;
    private float _plcPostFilterGain;
    private int _plcPostFilterTapSet;
    private uint _plcSeed;

    /// <summary>Clears the concealment state, as after a decoder reset (RFC 6716 §4.5.2).</summary>
    private void ResetPlcState()
    {
        _plcLossCount = 0;
        _plcLastPitchIndex = 0;
        _plcStartBand = 0;
        _plcEndBand = 21;
        _plcLm = 0;
        _plcStreamChannels = _outputChannels;
        _plcPostFilterPeriod = 0;
        _plcPostFilterGain = 0f;
        _plcPostFilterTapSet = 0;
        _plcSeed = 0;
        System.Array.Clear(_plcOldBandE);
        System.Array.Clear(_plcLpc);
        _plcFrame.BandEnergies.AsSpan().Clear();
    }

    /// <summary>Records the state a later concealment needs from a good frame.</summary>
    /// <param name="frame">The freshly synthesised frame.</param>
    private void CapturePlcState(CeltFrameResult frame)
    {
        _plcLossCount = 0;
        _plcStartBand = frame.StartBand;
        _plcEndBand = frame.EndBand;
        _plcLm = frame.Lm;
        _plcStreamChannels = frame.StreamChannels;
        _plcPostFilterPeriod = frame.PostFilterPitch;
        _plcPostFilterGain = frame.PostFilterGain;
        _plcPostFilterTapSet = frame.PostFilterTapSet;
        System.Array.Copy(frame.BandEnergies, _plcOldBandE, _plcOldBandE.Length);
    }

    /// <summary>Conceals one lost CELT frame (Appendix A, <c>celt_decode_lost</c>).</summary>
    /// <param name="lm">Base-2 logarithm of the number of short MDCT blocks.</param>
    /// <param name="startBand">First band to generate: 0 for CELT-only, 17 for hybrid.</param>
    /// <param name="pcmOut">Destination for interleaved normalized 48 kHz PCM.</param>
    private void PlcDecodeLost(int lm, int startBand, Span<float> pcmOut)
    {
        int samples = CeltTables.ShortBlockSamples << lm;
        int channels = _outputChannels;
        int count = samples * channels;
        if (pcmOut.Length < count)
        {
            throw new System.ArgumentException("The PCM destination is too short for the frame.", nameof(pcmOut));
        }

        if (_plcLossCount >= 5 || startBand != 0)
        {
            PlcNoise(lm, startBand, pcmOut);
        }
        else
        {
            PlcPitch(lm, startBand, pcmOut);
        }

        _plcLossCount++;
    }

    /// <summary>Pitch-periodic concealment for the first losses (Appendix A, pitch branch).</summary>
    private void PlcPitch(int lm, int startBand, Span<float> pcmOut)
    {
        int channels = _outputChannels;
        int n = CeltTables.ShortBlockSamples << lm;
        int overlap = Overlap;
        int len = n + overlap;

        int pitchIndex;
        float fade = 1f;

        // The pitch index is only valid after this branch has run since the last good frame.
        // A hybrid loss conceals its higher bands as noise (start band 17) and bumps the loss
        // count without searching for a pitch, so a following CELT-only loss must search here;
        // reusing the zeroed index would make the excitation offset stop advancing.
        if (_plcLossCount == 0 || _plcLastPitchIndex == 0)
        {
            PitchDownsample(channels);
            PitchSearch(
                _plcPitchBuf.AsSpan(PitchDownsampleOffset >> 1),
                _plcPitchBuf,
                DecodeHistorySize - PitchDownsampleOffset,
                PitchDownsampleOffset - 100,
                out pitchIndex);
            pitchIndex = PitchDownsampleOffset - pitchIndex;
            _plcLastPitchIndex = pitchIndex;
        }
        else
        {
            pitchIndex = _plcLastPitchIndex;
            fade = 0.8f;
        }

        for (int channel = 0; channel < channels; channel++)
        {
            float[] history = _history[channel];
            Span<float> exc = _plcExcitation.AsSpan(0, MaxPeriod);
            Span<float> lpc = _plcLpc.AsSpan(channel * LpcOrder, LpcOrder);
            Span<float> ac = _plcAc;
            float s1 = 0f;
            float decay = 1f;

            int offset = MaxPeriod - pitchIndex;
            for (int i = 0; i < MaxPeriod; i++)
            {
                exc[i] = history[MaxPeriod + i];
            }

            if (_plcLossCount == 0)
            {
                Autocorr(exc, ac, _plcWindow, overlap, LpcOrder, MaxPeriod);
                ac[0] *= 1.0001f;
                for (int i = 1; i <= LpcOrder; i++)
                {
                    ac[i] -= ac[i] * (0.008f * i) * (0.008f * i);
                }

                CeltLpc(lpc, ac, LpcOrder);
            }

            for (int i = 0; i < LpcOrder; i++)
            {
                _plcMem[i] = history[MaxPeriod - 1 - i];
            }

            Fir(exc, lpc, exc, MaxPeriod, LpcOrder, _plcMem);

            int period = pitchIndex <= MaxPeriod / 2 ? pitchIndex : MaxPeriod / 2;
            float e1 = 1f;
            float e2 = 1f;
            for (int i = 0; i < period; i++)
            {
                e1 += exc[MaxPeriod - period + i] * exc[MaxPeriod - period + i];
                e2 += exc[MaxPeriod - (2 * period) + i] * exc[MaxPeriod - (2 * period) + i];
            }

            if (e1 > e2)
            {
                e1 = e2;
            }

            decay = (float)System.Math.Sqrt(e1 / e2);

            Span<float> e = _plcCopy.AsSpan(0, len + overlap);
            for (int i = 0; i < len + overlap; i++)
            {
                if (offset + i >= MaxPeriod)
                {
                    offset -= pitchIndex;
                    decay *= decay;
                }

                e[i] = decay * exc[offset + i];
                float tmp = history[MaxPeriod + offset + i];
                s1 += tmp * tmp;
            }

            for (int i = 0; i < LpcOrder; i++)
            {
                _plcMem[i] = history[MaxPeriod - 1 - i];
            }

            for (int i = 0; i < len + overlap; i++)
            {
                e[i] *= fade;
            }

            Iir(e, lpc, e, len + overlap, LpcOrder, _plcMem);

            float s2 = 0f;
            for (int i = 0; i < len + overlap; i++)
            {
                s2 += e[i] * e[i];
            }

            if (!(s1 > 0.2f * s2))
            {
                e.Clear();
            }
            else if (s1 < s2)
            {
                float ratio = (float)System.Math.Sqrt((s1 + 1f) / (s2 + 1f));
                for (int i = 0; i < len + overlap; i++)
                {
                    e[i] *= ratio;
                }
            }

            // outMem[k] is decode_mem[1022 + k]: the live window is the last 1024 samples plus
            // the overlap, shifted by a two-sample prefix so the comb filter's negative taps
            // (period up to 1024) stay in range.
            Span<float> outMem = _plcOutMem;
            history.AsSpan(MaxPeriod, MaxPeriod).CopyTo(outMem[OutMemOffset..]);
            _rawOverlap[channel]
                .AsSpan(0, overlap)
                .CopyTo(outMem[(OutMemOffset + MaxPeriod)..]);

            int overlapBase = OutMemOffset + MaxPeriod;
            CombFilterNoFade(
                outMem, overlapBase, overlap, _plcPostFilterPeriod, _plcPostFilterGain,
                _plcPostFilterTapSet);

            for (int i = 0; i < MaxPeriod + overlap - n; i++)
            {
                outMem[OutMemOffset + i] = outMem[OutMemOffset + n + i];
            }

            for (int i = 0; i < overlap / 2; i++)
            {
                float tmp = (_plcWindow[i] * e[n + overlap - 1 - i])
                    + (_plcWindow[overlap - i - 1] * e[n + i]);
                outMem[overlapBase + i] = _plcWindow[overlap - i - 1] * tmp;
                outMem[overlapBase + overlap - i - 1] = _plcWindow[i] * tmp;
            }

            for (int i = 0; i < n; i++)
            {
                outMem[overlapBase - n + i] = e[i];
            }

            // Pre-filter the overlap for the next frame's post-filter.
            CombFilterNoFade(
                outMem, overlapBase, overlap, _plcPostFilterPeriod, -_plcPostFilterGain,
                _plcPostFilterTapSet, e[..overlap]);

            for (int i = 0; i < overlap; i++)
            {
                outMem[overlapBase + i] = e[i];
            }

            outMem.Slice(OutMemOffset, MaxPeriod).CopyTo(history.AsSpan(MaxPeriod, MaxPeriod));
            outMem.Slice(overlapBase, overlap).CopyTo(_rawOverlap[channel]);

            Deemphasise(n, channel, pcmOut, channels);
        }

    }

    /// <summary>Noise-based concealment from the fifth loss or for a hybrid frame.</summary>
    private void PlcNoise(int lm, int startBand, Span<float> pcmOut)
    {
        int channels = _outputChannels;
        int streamChannels = _plcStreamChannels is 1 or 2 ? _plcStreamChannels : channels;
        int n = CeltTables.ShortBlockSamples << lm;
        int endBand = _plcEndBand;

        float decay = _plcLossCount == 0 ? 1.5f : 0.5f;
        for (int i = 0; i < _plcOldBandE.Length; i++)
        {
            _plcOldBandE[i] = System.Math.Max(-28f, _plcOldBandE[i] - decay);
        }

        CeltFrameResult frame = _plcFrame;
        frame.SampleCount = n;
        frame.Lm = lm;
        frame.StartBand = startBand;
        frame.EndBand = endBand;
        frame.StreamChannels = streamChannels;
        frame.ShortBlocks = 0;
        frame.IsSilent = false;
        System.Array.Copy(_plcOldBandE, frame.BandEnergies, _plcOldBandE.Length);

        uint seed = _plcSeed;
        for (int channel = 0; channel < streamChannels; channel++)
        {
            int channelBase = channel * n;
            frame.Spectrum.AsSpan(channelBase, n).Clear();
            for (int band = startBand; band < endBand; band++)
            {
                int start = CeltTables.EBands[band] << lm;
                int end = CeltTables.EBands[band + 1] << lm;
                float energy = 1e-15f;
                for (int j = start; j < end; j++)
                {
                    seed = (1664525u * seed) + 1013904223u;
                    float value = (int)seed >> 20;
                    frame.Spectrum[channelBase + j] = value;
                    energy += value * value;
                }

                float scale = 1f / (float)System.Math.Sqrt(energy);
                for (int j = start; j < end; j++)
                {
                    frame.Spectrum[channelBase + j] *= scale;
                }
            }
        }

        _plcSeed = seed;

        Denormalise<ScalarKernels>(frame, n, streamChannels, channels, duplicate: false);
        for (int channel = 0; channel < channels; channel++)
        {
            BuildTimeSignal<ScalarKernels>(frame, n, channel);
            Deemphasise(n, channel, pcmOut, channels);
        }
    }

    /// <summary>Downsamples the last 2048 synthesis samples for the pitch search.</summary>
    private void PitchDownsample(int channels)
    {
        float[] x0 = _history[0];
        int half = DecodeHistorySize >> 1;
        for (int i = 1; i < half; i++)
        {
            _plcPitchBuf[i] = 0.5f
                * ((0.5f * (x0[(2 * i) - 1] + x0[(2 * i) + 1])) + x0[2 * i]);
        }

        _plcPitchBuf[0] = 0.5f * ((0.5f * x0[1]) + x0[0]);

        if (channels == 2)
        {
            float[] x1 = _history[1];
            for (int i = 1; i < half; i++)
            {
                _plcPitchBuf[i] += 0.5f
                    * ((0.5f * (x1[(2 * i) - 1] + x1[(2 * i) + 1])) + x1[2 * i]);
            }

            _plcPitchBuf[0] += 0.5f * ((0.5f * x1[1]) + x1[0]);
        }

        float[] ac = _plcAc;
        Autocorr(_plcPitchBuf.AsSpan(0, half), ac, default, 0, 4, half);
        ac[0] *= 1.0001f;
        for (int i = 1; i <= 4; i++)
        {
            ac[i] -= ac[i] * (0.008f * i) * (0.008f * i);
        }

        Span<float> lpc = _plcLpc.AsSpan(0, 4);
        CeltLpc(lpc, ac, 4);
        float tmp = 1f;
        for (int i = 0; i < 4; i++)
        {
            tmp *= 0.9f;
            lpc[i] *= tmp;
        }

        System.Array.Clear(_plcMem);
        Fir(_plcPitchBuf.AsSpan(0, half), lpc, _plcPitchBuf.AsSpan(0, half), half, 4, _plcMem);

        _plcMem[0] = 0f;
        _plcMem[1] = 0.8f;
        Fir(_plcPitchBuf.AsSpan(0, half), _plcMem.AsSpan(1), _plcPitchBuf.AsSpan(0, half), half, 1, _plcMem);
    }

    /// <summary>Finds the pitch period (Appendix A, celt/pitch.c: <c>pitch_search</c>).</summary>
    private void PitchSearch(
        ReadOnlySpan<float> xLp, ReadOnlySpan<float> y, int length, int maxPitch, out int pitch)
    {
        int lag = length + maxPitch;
        Span<float> xLp4 = _plcPitchXlp4.AsSpan(0, length >> 2);
        Span<float> yLp4 = _plcPitchYlp4.AsSpan(0, lag >> 2);
        Span<float> xcorr = _plcPitchXcorr.AsSpan(0, maxPitch >> 1);

        for (int j = 0; j < length >> 2; j++)
        {
            xLp4[j] = xLp[2 * j];
        }

        for (int j = 0; j < lag >> 2; j++)
        {
            yLp4[j] = y[2 * j];
        }

        for (int i = 0; i < maxPitch >> 2; i++)
        {
            float sum = 0f;
            for (int j = 0; j < length >> 2; j++)
            {
                sum += xLp4[j] * yLp4[i + j];
            }

            xcorr[i] = System.Math.Max(-1f, sum);
        }

        FindBestPitch(xcorr, yLp4, length >> 2, maxPitch >> 2, out int best0, out int best1);

        for (int i = 0; i < maxPitch >> 1; i++)
        {
            float sum = 0f;
            xcorr[i] = 0f;
            if (System.Math.Abs(i - (2 * best0)) > 2 && System.Math.Abs(i - (2 * best1)) > 2)
            {
                continue;
            }

            for (int j = 0; j < length >> 1; j++)
            {
                sum += xLp[j] * y[i + j];
            }

            xcorr[i] = System.Math.Max(-1f, sum);
        }

        FindBestPitch(xcorr, y, length >> 1, maxPitch >> 1, out best0, out best1);

        int offset = 0;
        if (best0 > 0 && best0 < (maxPitch >> 1) - 1)
        {
            float a = xcorr[best0 - 1];
            float b = xcorr[best0];
            float c = xcorr[best0 + 1];
            if ((c - a) > (0.7f * (b - a)))
            {
                offset = 1;
            }
            else if ((a - c) > (0.7f * (b - c)))
            {
                offset = -1;
            }
        }

        pitch = (2 * best0) - offset;
    }

    /// <summary>Chooses the two best pitch candidates (Appendix A, <c>find_best_pitch</c>).</summary>
    private static void FindBestPitch(
        Span<float> xcorr, ReadOnlySpan<float> y, int length, int maxPitch, out int best0, out int best1)
    {
        float syy = 1f;
        float bestNum0 = -1f;
        float bestNum1 = -1f;
        float bestDen0 = 0f;
        float bestDen1 = 0f;
        best0 = 0;
        best1 = 1;

        for (int j = 0; j < length; j++)
        {
            syy += y[j] * y[j];
        }

        for (int i = 0; i < maxPitch; i++)
        {
            if (xcorr[i] > 0f)
            {
                float num = xcorr[i] * xcorr[i];
                if (num * bestDen1 > bestNum1 * syy)
                {
                    if (num * bestDen0 > bestNum0 * syy)
                    {
                        bestNum1 = bestNum0;
                        bestDen1 = bestDen0;
                        best1 = best0;
                        bestNum0 = num;
                        bestDen0 = syy;
                        best0 = i;
                    }
                    else
                    {
                        bestNum1 = num;
                        bestDen1 = syy;
                        best1 = i;
                    }
                }
            }

            syy += (y[i + length] * y[i + length]) - (y[i] * y[i]);
            syy = System.Math.Max(1f, syy);
        }
    }

    /// <summary>Computes autocorrelations with an optional window (Appendix A, <c>_celt_autocorr</c>).</summary>
    private void Autocorr(
        ReadOnlySpan<float> x, Span<float> ac, ReadOnlySpan<float> window, int overlap, int lag, int n)
    {
        Span<float> scratch = _plcAutocorrScratch.AsSpan(0, n);
        x.CopyTo(scratch);
        if (overlap > 0)
        {
            for (int i = 0; i < overlap; i++)
            {
                scratch[i] = x[i] * window[i];
                scratch[n - i - 1] = x[n - i - 1] * window[i];
            }
        }

        for (int l = lag; l >= 0; l--)
        {
            float d = 0f;
            for (int i = l; i < n; i++)
            {
                d += scratch[i] * scratch[i - l];
            }

            ac[l] = d;
        }

        ac[0] += 10f;
    }

    /// <summary>Levinson-Durbin recursion in float (Appendix A, celt/celt_lpc.c: <c>_celt_lpc</c>).</summary>
    private static void CeltLpc(Span<float> lpc, ReadOnlySpan<float> ac, int order)
    {
        lpc[..order].Clear();
        if (ac[0] == 0f)
        {
            return;
        }

        float error = ac[0];
        for (int i = 0; i < order; i++)
        {
            float rr = 0f;
            for (int j = 0; j < i; j++)
            {
                rr += lpc[j] * ac[i - j];
            }

            rr += ac[i + 1];
            float r = -rr / error;
            lpc[i] = r;
            for (int j = 0; j < (i + 1) >> 1; j++)
            {
                float tmp1 = lpc[j];
                float tmp2 = lpc[i - 1 - j];
                lpc[j] = tmp1 + (r * tmp2);
                lpc[i - 1 - j] = tmp2 + (r * tmp1);
            }

            error -= r * r * error;
            if (error < 0.001f * ac[0])
            {
                break;
            }
        }
    }

    /// <summary>FIR filter (Appendix A, celt/celt_lpc.c: <c>celt_fir</c>).</summary>
    private static void Fir(
        ReadOnlySpan<float> x, ReadOnlySpan<float> num, Span<float> y, int n, int order, Span<float> mem)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = x[i];
            for (int j = 0; j < order; j++)
            {
                sum += num[j] * mem[j];
            }

            for (int j = order - 1; j >= 1; j--)
            {
                mem[j] = mem[j - 1];
            }

            mem[0] = x[i];
            y[i] = sum;
        }
    }

    /// <summary>IIR filter (Appendix A, celt/celt_lpc.c: <c>celt_iir</c>).</summary>
    private static void Iir(
        ReadOnlySpan<float> x, ReadOnlySpan<float> den, Span<float> y, int n, int order, Span<float> mem)
    {
        for (int i = 0; i < n; i++)
        {
            float sum = x[i];
            for (int j = 0; j < order; j++)
            {
                sum -= den[j] * mem[j];
            }

            for (int j = order - 1; j >= 1; j--)
            {
                mem[j] = mem[j - 1];
            }

            mem[0] = sum;
            y[i] = sum;
        }
    }

    /// <summary>
    /// One comb-filter pass without a parameter cross-fade, as the PLC uses (Appendix A,
    /// <c>comb_filter</c> with an overlap of zero).
    /// </summary>
    /// <param name="buffer">The out-memory window: 1024 history samples then the overlap.</param>
    /// <param name="frameStart">Index of the first sample to filter.</param>
    /// <param name="count">Number of samples to filter.</param>
    /// <param name="period">Pitch period.</param>
    /// <param name="gain">Post-filter gain.</param>
    /// <param name="tapSet">Tap set, 0 to 2.</param>
    /// <param name="output">Optional destination; when empty, the filter runs in place.</param>
    private void CombFilterNoFade(
        Span<float> buffer, int frameStart, int count, int period, float gain, int tapSet, Span<float> output = default)
    {
        // A disabled post-filter leaves the period at zero; clamp it so the tap indices stay in
        // range. With a zero gain the taps contribute nothing either way.
        period = Math.Max(period, CeltTables.CombFilterMinPeriod);
        ReadOnlySpan<float> taps = CeltTables.PostFilterGains.Slice(tapSet * 3, 3);
        float g0 = gain * taps[0];
        float g1 = gain * taps[1];
        float g2 = gain * taps[2];
        bool inPlace = output.IsEmpty;
        for (int i = 0; i < count; i++)
        {
            int position = frameStart + i;
            float value = buffer[position]
                + (g0 * buffer[position - period])
                + (g1 * (buffer[position - period - 1] + buffer[position - period + 1]))
                + (g2 * (buffer[position - period - 2] + buffer[position - period + 2]));
            if (inPlace)
            {
                buffer[position] = value;
            }
            else
            {
                output[i] = value;
            }
        }
    }
}
