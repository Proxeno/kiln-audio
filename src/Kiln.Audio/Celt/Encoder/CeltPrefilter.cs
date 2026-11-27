// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System.Runtime.Intrinsics;
using Kiln.Audio.Entropy;
using Kiln.Audio.Simd;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>
/// The encoder-side pitch pre-filter of RFC 6716 §4.3.7.1 (RFC 6716 Appendix A,
/// <c>celt/pitch.c</c> and the prefilter block of <c>celt/celt.c</c>).
/// </summary>
/// <remarks>
/// <para>
/// The pre-filter predicts the current frame from a delayed copy of the signal and subtracts the
/// prediction before the MDCT. The pitch is found by the reference's decimated correlation search:
/// the signal is low-pass filtered, decimated by two and then by four for a coarse search, and the
/// best lag is refined by two and by one. The resulting period, gain and tap set are signalled in
/// the bitstream so the decoder can apply the matching post-filter.
/// </para>
/// <para>
/// The search only runs at complexity 5 and above; below that the filter is signalled off and the
/// frame is coded as usual. All buffers are allocated once; processing allocates nothing.
/// </para>
/// </remarks>
internal sealed class CeltPrefilter
{
    private const int MaxPeriod = CeltTables.CombFilterMaxPeriod;
    private const int MinPeriod = CeltTables.CombFilterMinPeriod;
    private const int MaxFrameSamples = CeltTables.MaxFrameSamples;
    private const int Overlap = CeltTables.ShortBlockSamples;

    private static ReadOnlySpan<int> SecondCheck => [0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2];

    private readonly int _channels;
    private readonly float[] _prefilterMemory = new float[2 * MaxPeriod];
    private readonly float[] _inMemory = new float[2 * Overlap];
    private readonly float[] _pre = new float[2 * (MaxPeriod + MaxFrameSamples)];
    private readonly float[] _pitchBuffer = new float[(MaxPeriod + MaxFrameSamples) >> 1];
    private readonly float[] _xcorr = new float[MaxPeriod >> 1];
    private readonly float[] _xLp4 = new float[MaxFrameSamples >> 2];
    private readonly float[] _yLp4 = new float[(MaxFrameSamples + MaxPeriod) >> 2];
    private readonly float[] _autocorr = new float[5];
    private readonly float[] _lpc = new float[4];
    private readonly int[] _bestPitch = new int[2];

    private int _period = MinPeriod;
    private float _gain;
    private int _tapset;

    /// <summary>Creates the pre-filter state for a fixed number of stream channels.</summary>
    internal CeltPrefilter(int channels)
    {
        _channels = channels;
        Reset();
    }

    /// <summary>Restores the freshly constructed state (RFC 6716 Appendix A, <c>OPUS_RESET_STATE</c>).</summary>
    internal void Reset()
    {
        Array.Clear(_prefilterMemory);
        Array.Clear(_inMemory);
        _period = MinPeriod;
        _gain = 0f;
        _tapset = 0;
    }

    /// <summary>
    /// Runs the pitch search, signals the pre-filter and applies it to the frame in place.
    /// </summary>
    /// <param name="enc">The range encoder.</param>
    /// <param name="input">Channel-major pre-emphasized frame, including the overlap history.</param>
    /// <param name="channels">The number of stream channels.</param>
    /// <param name="frameSamples">The number of 48 kHz samples per channel.</param>
    /// <param name="availableBytes">The bytes available to the frame.</param>
    /// <param name="totalBits">The frame budget in bits.</param>
    /// <param name="complexity">The encoder complexity, 0 to 10.</param>
    /// <param name="tapsetDecision">The tap-set decision left by the previous spreading analysis.</param>
    /// <param name="allowPitch">
    /// False for a hybrid frame, whose CELT layer starts at band 17: the reference skips the
    /// pitch search and signals nothing, but still applies the (zero-gain) comb filter so the
    /// overlap history stays consistent.
    /// </param>
    /// <param name="window">The rising CELT window, <see cref="Overlap"/> samples.</param>
    /// <returns>True when the pre-filter was enabled.</returns>
    internal bool Process<TK>(
        ref RangeEncoder enc,
        Span<float> input,
        int channels,
        int frameSamples,
        int availableBytes,
        int totalBits,
        int complexity,
        int tapsetDecision,
        bool allowPitch,
        ReadOnlySpan<float> window)
        where TK : struct, IEncoderKernels
    {
        int stride = frameSamples + Overlap;
        BuildPreAndHistory(input, channels, frameSamples, stride);

        int pitchIndex = MinPeriod;
        int tapset = 0;
        float gain1 = 0f;

        if (allowPitch && availableBytes > 12 * channels && complexity >= 5)
        {
            PitchDownsample<TK>(channels, MaxPeriod + frameSamples, stride);
            PitchSearch<TK>(
                _pitchBuffer.AsSpan(MaxPeriod >> 1),
                _pitchBuffer,
                frameSamples,
                MaxPeriod - MinPeriod,
                out pitchIndex);
            pitchIndex = MaxPeriod - pitchIndex;

            gain1 = RemoveDoubling<TK>(
                _pitchBuffer,
                MaxPeriod,
                MinPeriod,
                frameSamples,
                ref pitchIndex,
                _period,
                _gain);
            if (pitchIndex > MaxPeriod - 2)
            {
                pitchIndex = MaxPeriod - 2;
            }

            gain1 *= 0.7f;
            tapset = tapsetDecision;
        }

        float threshold = 0.2f;
        if (Math.Abs(pitchIndex - _period) * 10 > pitchIndex)
        {
            threshold += 0.2f;
        }

        if (availableBytes < 25)
        {
            threshold += 0.1f;
        }

        if (availableBytes < 35)
        {
            threshold += 0.1f;
        }

        if (_gain > 0.4f)
        {
            threshold -= 0.1f;
        }

        if (_gain > 0.55f)
        {
            threshold -= 0.1f;
        }

        if (threshold < 0.2f)
        {
            threshold = 0.2f;
        }

        bool on;
        if (gain1 < threshold)
        {
            if (allowPitch && enc.Tell() + 16 <= totalBits)
            {
                enc.EncodeBitLogp(0, 1);
            }

            gain1 = 0f;
            on = false;
        }
        else
        {
            if (Math.Abs(gain1 - _gain) < 0.1f)
            {
                gain1 = _gain;
            }

            int qg = (int)MathF.Floor(0.5f + (gain1 * 32f / 3f)) - 1;
            qg = Math.Clamp(qg, 0, 7);
            enc.EncodeBitLogp(1, 1);

            pitchIndex += 1;
            int octave = CeltTables.Ilog(pitchIndex) - 5;
            enc.EncodeUInt((uint)octave, 6);
            enc.EncodeBits((uint)(pitchIndex - (16 << octave)), 4 + octave);
            pitchIndex -= 1;
            enc.EncodeBits((uint)qg, 3);
            if (enc.Tell() + 2 <= totalBits)
            {
                enc.EncodeIcdf(CeltTables.TapsetIcdf, 2, tapset);
            }
            else
            {
                tapset = 0;
            }

            gain1 = 0.09375f * (qg + 1);
            on = true;
        }

        ApplyCombFilter<TK>(input, channels, frameSamples, stride, pitchIndex, gain1, tapset, window);

        _period = Math.Max(pitchIndex, MinPeriod);
        _gain = gain1;
        _tapset = tapset;
        return on;
    }

    private void BuildPreAndHistory(Span<float> input, int channels, int frameSamples, int stride)
    {
        for (int c = 0; c < channels; c++)
        {
            Span<float> pre = _pre.AsSpan(c * (MaxPeriod + MaxFrameSamples), MaxPeriod + frameSamples);
            Span<float> memory = _prefilterMemory.AsSpan(c * MaxPeriod, MaxPeriod);
            memory.CopyTo(pre);
            input.Slice((c * stride) + Overlap, frameSamples).CopyTo(pre.Slice(MaxPeriod, frameSamples));
        }
    }

    /// <summary>Copies the previous tail into the frame and applies the comb filter per channel.</summary>
    private void ApplyCombFilter<TK>(
        Span<float> input,
        int channels,
        int frameSamples,
        int stride,
        int pitchIndex,
        float gain,
        int tapset,
        ReadOnlySpan<float> window)
        where TK : struct, IEncoderKernels
    {
        _period = Math.Max(_period, MinPeriod);
        for (int c = 0; c < channels; c++)
        {
            int inputBase = c * stride;
            int preBase = c * (MaxPeriod + MaxFrameSamples);
            _inMemory.AsSpan(c * Overlap, Overlap).CopyTo(input.Slice(inputBase, Overlap));
            ReadOnlySpan<float> gainTable = CeltTables.PostFilterGains;
            TK.CombFilter(
                input.Slice(inputBase + Overlap, frameSamples),
                _pre.AsSpan(preBase, MaxPeriod + frameSamples),
                MaxPeriod,
                _period,
                pitchIndex,
                frameSamples,
                -_gain,
                -gain,
                gainTable.Slice(_tapset * 3, 3),
                gainTable.Slice(tapset * 3, 3),
                window,
                Overlap);
            input.Slice(inputBase + frameSamples, Overlap).CopyTo(_inMemory.AsSpan(c * Overlap, Overlap));

            Span<float> memory = _prefilterMemory.AsSpan(c * MaxPeriod, MaxPeriod);
            Span<float> pre = _pre.AsSpan(preBase, MaxPeriod + frameSamples);
            if (frameSamples > MaxPeriod)
            {
                pre.Slice(frameSamples, MaxPeriod).CopyTo(memory);
            }
            else
            {
                memory.Slice(frameSamples, MaxPeriod - frameSamples).CopyTo(memory);
                pre.Slice(MaxPeriod, frameSamples).CopyTo(memory.Slice(MaxPeriod - frameSamples, frameSamples));
            }
        }
    }

    /// <summary>The three-tap comb filter of RFC 6716 §4.3.7.1 (RFC 6716 Appendix A, celt/celt.c: comb_filter).</summary>
    /// <param name="y">Destination, which does not overlap <paramref name="x"/>.</param>
    /// <param name="x">The pre-emphasized signal, including <see cref="MaxPeriod"/> history samples before <paramref name="xStart"/>.</param>
    /// <param name="xStart">The index in <paramref name="x"/> of the first current sample.</param>
    /// <param name="t0">The previous pitch period.</param>
    /// <param name="t1">The current pitch period.</param>
    /// <param name="n">The number of samples to filter.</param>
    /// <param name="g0">The previous gain.</param>
    /// <param name="g1">The current gain.</param>
    /// <param name="tapset0">The previous tap set.</param>
    /// <param name="tapset1">The current tap set.</param>
    /// <param name="window">The rising window; only its first <paramref name="overlap"/> entries are used.</param>
    /// <param name="overlap">The cross-fade length.</param>
    internal static void CombFilter(
        Span<float> y,
        ReadOnlySpan<float> x,
        int xStart,
        int t0,
        int t1,
        int n,
        float g0,
        float g1,
        int tapset0,
        int tapset1,
        ReadOnlySpan<float> window,
        int overlap)
    {
        ReadOnlySpan<float> gains = CeltTables.PostFilterGains;
        CombFilterReference(
            y, x, xStart, t0, t1, n, g0, g1, gains.Slice(tapset0 * 3, 3), gains.Slice(tapset1 * 3, 3), window, overlap);
    }

    /// <summary>The scalar comb filter; see <see cref="CombFilter"/>.</summary>
    internal static void CombFilterReference(
        Span<float> y,
        ReadOnlySpan<float> x,
        int xStart,
        int t0,
        int t1,
        int n,
        float g0,
        float g1,
        ReadOnlySpan<float> gains0,
        ReadOnlySpan<float> gains1,
        ReadOnlySpan<float> window,
        int overlap)
    {
        float g00 = g0 * gains0[0];
        float g01 = g0 * gains0[1];
        float g02 = g0 * gains0[2];
        float g10 = g1 * gains1[0];
        float g11 = g1 * gains1[1];
        float g12 = g1 * gains1[2];

        int fade = Math.Min(overlap, n);
        for (int i = 0; i < fade; i++)
        {
            float f = window[i] * window[i];
            int p = xStart + i;
            float previous = (1f - f) * g00 * x[p - t0];
            previous += (1f - f) * g01 * x[p - t0 - 1];
            previous += (1f - f) * g01 * x[p - t0 + 1];
            previous += (1f - f) * g02 * x[p - t0 - 2];
            previous += (1f - f) * g02 * x[p - t0 + 2];
            float current = f * g10 * x[p - t1];
            current += f * g11 * x[p - t1 - 1];
            current += f * g11 * x[p - t1 + 1];
            current += f * g12 * x[p - t1 - 2];
            current += f * g12 * x[p - t1 + 2];
            y[i] = x[p] + previous + current;
        }

        for (int i = fade; i < n; i++)
        {
            int p = xStart + i;
            y[i] = x[p]
                + (g10 * x[p - t1])
                + (g11 * x[p - t1 - 1])
                + (g11 * x[p - t1 + 1])
                + (g12 * x[p - t1 - 2])
                + (g12 * x[p - t1 + 2]);
        }
    }

    /// <summary>Low-pass filters and decimates the signal by two before the pitch search.</summary>
    private void PitchDownsample<TK>(int channels, int len, int stride)
        where TK : struct, IEncoderKernels
    {
        int count = len >> 1;
        Span<float> low = _pitchBuffer.AsSpan(0, count);
        TK.DecimateLowpass(_pre.AsSpan(0, len), low, count, accumulate: false);
        for (int c = 1; c < channels; c++)
        {
            TK.DecimateLowpass(_pre.AsSpan(c * stride, len), low, count, accumulate: true);
        }

        _ = _channels;
        TK.Autocorr5(low, _autocorr);
        _autocorr[0] += 10f;
        _autocorr[0] *= 1.0001f;
        for (int i = 1; i <= 4; i++)
        {
            _autocorr[i] -= _autocorr[i] * (0.008f * i) * (0.008f * i);
        }

        Lpc(_lpc, _autocorr, 4);
        float tmp = 1f;
        for (int i = 0; i < 4; i++)
        {
            tmp *= 0.9f;
            _lpc[i] *= tmp;
        }

        TK.Fir4(low, _lpc[0], _lpc[1], _lpc[2], _lpc[3]);
        TK.Fir1(low, 0.8f);
    }

    private static void Lpc(Span<float> lpc, ReadOnlySpan<float> ac, int order)
    {
        for (int i = 0; i < order; i++)
        {
            lpc[i] = 0f;
        }

        float error = ac[0];
        if (ac[0] == 0f)
        {
            return;
        }

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

    /// <summary>The decimated correlation pitch search (RFC 6716 Appendix A, celt/pitch.c: pitch_search).</summary>
    private void PitchSearch<TK>(
        ReadOnlySpan<float> xLow, ReadOnlySpan<float> y, int len, int maxPitch, out int pitch)
        where TK : struct, IEncoderKernels
    {
        int lag = len + maxPitch;
        Span<float> xLow4 = _xLp4.AsSpan(0, Math.Max(1, len >> 2));
        Span<float> yLow4 = _yLp4.AsSpan(0, Math.Max(1, lag >> 2));
        int coarseCount = Math.Max(1, maxPitch >> 2);

        for (int j = 0; j < len >> 2; j++)
        {
            xLow4[j] = xLow[2 * j];
        }

        for (int j = 0; j < lag >> 2; j++)
        {
            yLow4[j] = y[2 * j];
        }

        int coarseLength = len >> 2;
        TK.XcorrLags(xLow4[..coarseLength], yLow4, coarseCount, coarseLength, _xcorr);
        for (int i = 0; i < coarseCount; i++)
        {
            _xcorr[i] = Math.Max(-1f, _xcorr[i]);
        }

        _bestPitch[0] = 0;
        _bestPitch[1] = 0;
        FindBestPitch(_xcorr.AsSpan(0, coarseCount), yLow4, len >> 2, coarseCount, _bestPitch);

        int fineCount = Math.Max(1, maxPitch >> 1);
        Span<int> candidates = stackalloc int[16];
        int candidateCount = 0;
        for (int i = 0; i < fineCount; i++)
        {
            _xcorr[i] = 0f;
            if (Math.Abs(i - (2 * _bestPitch[0])) > 2 && Math.Abs(i - (2 * _bestPitch[1])) > 2)
            {
                continue;
            }

            candidates[candidateCount++] = i;
        }

        // ENC-CELT-1: the refinement lags (at most ten) are correlated four at a time.
        int count = len >> 1;
        ReadOnlySpan<float> fineTarget = xLow[..count];
        for (int g = 0; g < candidateCount; g += 4)
        {
            int c0 = candidates[g];
            int c1 = candidates[Math.Min(g + 1, candidateCount - 1)];
            int c2 = candidates[Math.Min(g + 2, candidateCount - 1)];
            int c3 = candidates[Math.Min(g + 3, candidateCount - 1)];
            TK.Dot4(
                count,
                fineTarget, y.Slice(c0, count),
                fineTarget, y.Slice(c1, count),
                fineTarget, y.Slice(c2, count),
                fineTarget, y.Slice(c3, count),
                out float d0, out float d1, out float d2, out float d3);
            _xcorr[c0] = Math.Max(-1f, d0);
            if (g + 1 < candidateCount)
            {
                _xcorr[c1] = Math.Max(-1f, d1);
            }

            if (g + 2 < candidateCount)
            {
                _xcorr[c2] = Math.Max(-1f, d2);
            }

            if (g + 3 < candidateCount)
            {
                _xcorr[c3] = Math.Max(-1f, d3);
            }
        }

        FindBestPitch(_xcorr.AsSpan(0, fineCount), y, len >> 1, fineCount, _bestPitch);

        int offset = 0;
        int best = _bestPitch[0];
        if (best > 0 && best < fineCount - 1)
        {
            float a = _xcorr[best - 1];
            float b = _xcorr[best];
            float c = _xcorr[best + 1];
            if (c - a > 0.7f * (b - a))
            {
                offset = 1;
            }
            else if (a - c > 0.7f * (b - c))
            {
                offset = -1;
            }
        }

        pitch = (2 * best) - offset;
    }

    /// <summary>Ranks the two best lags by normalized correlation (RFC 6716 Appendix A, celt/pitch.c: find_best_pitch).</summary>
    private static void FindBestPitch(
        Span<float> xcorr, ReadOnlySpan<float> y, int len, int maxPitch, Span<int> bestPitch)
    {
        float syy = 1f;
        for (int j = 0; j < len; j++)
        {
            syy += y[j] * y[j];
        }

        Span<float> bestNum = stackalloc float[2];
        Span<float> bestDen = stackalloc float[2];
        bestNum[0] = -1f;
        bestNum[1] = -1f;
        bestDen[0] = 0f;
        bestDen[1] = 0f;
        bestPitch[0] = 0;
        bestPitch[1] = 1;

        for (int i = 0; i < maxPitch; i++)
        {
            if (xcorr[i] > 0f)
            {
                float num = xcorr[i] * xcorr[i];
                if (num * bestDen[1] > bestNum[1] * syy)
                {
                    if (num * bestDen[0] > bestNum[0] * syy)
                    {
                        bestNum[1] = bestNum[0];
                        bestDen[1] = bestDen[0];
                        bestPitch[1] = bestPitch[0];
                        bestNum[0] = num;
                        bestDen[0] = syy;
                        bestPitch[0] = i;
                    }
                    else
                    {
                        bestNum[1] = num;
                        bestDen[1] = syy;
                        bestPitch[1] = i;
                    }
                }
            }

            syy += (y[i + len] * y[i + len]) - (y[i] * y[i]);
            if (syy < 1f)
            {
                syy = 1f;
            }
        }
    }

    /// <summary>
    /// Removes pitch doubling and refines the lag (RFC 6716 Appendix A, celt/pitch.c:
    /// remove_doubling).
    /// </summary>
    internal static float RemoveDoubling<TK>(
        Span<float> x,
        int maxPeriod,
        int minPeriod,
        int frameSamples,
        ref int t0,
        int previousPeriod,
        float previousGain)
        where TK : struct, IEncoderKernels
    {
        int minPeriod0 = minPeriod;
        maxPeriod /= 2;
        minPeriod /= 2;
        t0 /= 2;
        previousPeriod /= 2;
        int n = frameSamples / 2;
        if (t0 >= maxPeriod)
        {
            t0 = maxPeriod - 1;
        }

        int period = t0;
        ReadOnlySpan<float> current = x.Slice(maxPeriod, n);
        ReadOnlySpan<float> delayed = x.Slice(maxPeriod - period, n);
        TK.Dot4(
            n,
            current, delayed,
            current, current,
            delayed, delayed,
            delayed, delayed,
            out float xy, out float xx, out float yy, out _);

        float bestXy = xy;
        float bestYy = yy;
        float g = xy / MathF.Sqrt(1f + (xx * yy));
        float g0 = g;

        for (int k = 2; k <= 15; k++)
        {
            int t1 = ((2 * period) + k) / (2 * k);
            if (t1 < minPeriod)
            {
                break;
            }

            int t1b;
            if (k == 2)
            {
                t1b = t1 + period > maxPeriod ? period : period + t1;
            }
            else
            {
                t1b = ((2 * SecondCheck[k] * period) + k) / (2 * k);
            }

            ReadOnlySpan<float> d1 = x.Slice(maxPeriod - t1, n);
            ReadOnlySpan<float> d2 = x.Slice(maxPeriod - t1b, n);
            TK.Dot4(n, current, d1, current, d2, d1, d1, d2, d2, out float xy1, out float xy2, out float yy1, out float yy2);
            xy = xy1 + xy2;
            yy = yy1 + yy2;

            float g1 = xy / MathF.Sqrt(1f + (2f * xx * yy));
            float cont;
            if (Math.Abs(t1 - previousPeriod) <= 1)
            {
                cont = previousGain;
            }
            else if (Math.Abs(t1 - previousPeriod) <= 2 && (5 * k * k) < period)
            {
                cont = 0.5f * previousGain;
            }
            else
            {
                cont = 0f;
            }

            if (g1 > 0.3f + (0.4f * g0) - cont)
            {
                bestXy = xy;
                bestYy = yy;
                period = t1;
                g = g1;
            }
        }

        float pg;
        if (bestYy <= bestXy)
        {
            pg = 1f;
        }
        else
        {
            pg = bestXy / (bestYy + 1f);
        }

        Span<float> xcorr = stackalloc float[3];
        TK.Dot4(
            n,
            current, x.Slice(maxPeriod - (period - 1), n),
            current, x.Slice(maxPeriod - period, n),
            current, x.Slice(maxPeriod - (period + 1), n),
            current, current,
            out xcorr[0], out xcorr[1], out xcorr[2], out _);

        int offset;
        if (xcorr[2] - xcorr[0] > 0.7f * (xcorr[1] - xcorr[0]))
        {
            offset = 1;
        }
        else if (xcorr[0] - xcorr[2] > 0.7f * (xcorr[1] - xcorr[2]))
        {
            offset = -1;
        }
        else
        {
            offset = 0;
        }

        if (pg > g)
        {
            pg = g;
        }

        t0 = (2 * period) + offset;
        if (t0 < minPeriod0)
        {
            t0 = minPeriod0;
        }

        return pg;
    }

    /// <summary>Scalar dot product used by the pitch correlations.</summary>
    internal static float DotScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Vectorized dot product with a scalar fallback; exposed for the parity test.</summary>
    internal static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b, bool useVector)
    {
        // The kernel lives in the Vector128 tier: the encoder takes no 256-bit or Vector<T> path.
        // The caller's flag stays authoritative so KILN_AUDIO_DISABLE_SIMD gives a scalar run.
        if (!useVector || !SimdConfig.UseSimd || a.Length < 4 || !System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            return DotScalar(a, b);
        }

        return Kiln.Audio.Simd.Kernels.Vector128Kernels.Dot(a, b);
    }
}