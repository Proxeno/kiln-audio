// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Celt.Encoder;

/// <summary>Frame analysis of the CELT encoder: time/frequency resolution, spreading and trim.</summary>
/// <remarks>RFC 6716 Appendix A, celt/celt.c: <c>tf_analysis</c>, <c>tf_encode</c>, <c>spreading_decision</c>, <c>alloc_trim_analysis</c>, <c>init_caps</c>.</remarks>
internal static class CeltEncoderAnalysis
{
    private const int BandCount = CeltTables.BandCount;
    private const int BitRes = CeltTables.BitRes;

    private static ReadOnlySpan<float> SqrtM1 => [1f, 0.70710678f, 0.5f, 0.35355339f];

    /// <summary>
    /// Chooses the per-band time/frequency resolution and writes nothing
    /// (RFC 6716 Appendix A, celt/celt.c: <c>tf_analysis</c>).
    /// </summary>
    internal static int TfAnalysis(
        ReadOnlySpan<short> eBands,
        int len,
        bool isTransient,
        Span<int> tfResolution,
        int nbCompressedBytes,
        ReadOnlySpan<float> spectrum,
        int lm,
        out int tfSum,
        Span<int> metric,
        Span<int> path0,
        Span<int> path1,
        Span<float> tmp)
    {
        if (nbCompressedBytes < 15)
        {
            tfSum = 0;
            for (int i = 0; i < len; i++)
            {
                tfResolution[i] = isTransient ? 1 : 0;
            }

            return 0;
        }

        int lambda = nbCompressedBytes switch
        {
            < 40 => 12,
            < 60 => 6,
            < 100 => 4,
            _ => 3,
        };

        tfSum = 0;
        for (int i = 0; i < len; i++)
        {
            int bandStart = eBands[i] << lm;
            int samples = (eBands[i + 1] - eBands[i]) << lm;
            spectrum.Slice(bandStart, samples).CopyTo(tmp);

            float best = L1Metric(tmp, samples, isTransient ? lm : 0, samples >> lm);
            int bestLevel = 0;
            for (int k = 0; k < lm; k++)
            {
                int blocks = isTransient ? lm - k - 1 : k + 1;
                if (isTransient)
                {
                    Haar1(tmp, samples >> (lm - k), 1 << (lm - k));
                }
                else
                {
                    Haar1(tmp, samples >> k, 1 << k);
                }

                float l1 = L1Metric(tmp, samples, blocks, samples >> lm);
                if (l1 < best)
                {
                    best = l1;
                    bestLevel = k + 1;
                }
            }

            metric[i] = isTransient ? bestLevel : -bestLevel;
            tfSum += metric[i];
        }

        const int tfSelect = 0;
        int cost0 = 0;
        int cost1 = isTransient ? 0 : lambda;
        ReadOnlySpan<sbyte> table = CeltTables.TfSelectTable;
        int transientIndex = isTransient ? 4 : 0;
        for (int i = 1; i < len; i++)
        {
            int curr0;
            int curr1;
            int from0 = cost0;
            int from1 = cost1 + lambda;
            if (from0 < from1)
            {
                curr0 = from0;
                path0[i] = 0;
            }
            else
            {
                curr0 = from1;
                path0[i] = 1;
            }

            from0 = cost0 + lambda;
            from1 = cost1;
            if (from0 < from1)
            {
                curr1 = from0;
                path1[i] = 0;
            }
            else
            {
                curr1 = from1;
                path1[i] = 1;
            }

            cost0 = curr0 + Math.Abs(metric[i] - table[(lm * 8) + transientIndex + 0]);
            cost1 = curr1 + Math.Abs(metric[i] - table[(lm * 8) + transientIndex + 1]);
        }

        tfResolution[len - 1] = cost0 < cost1 ? 0 : 1;
        for (int i = len - 2; i >= 0; i--)
        {
            tfResolution[i] = tfResolution[i + 1] == 1 ? path1[i + 1] : path0[i + 1];
        }

        return tfSelect;
    }

    private static float L1Metric(ReadOnlySpan<float> tmp, int n, int lm, int width)
    {
        ref float values = ref MemoryMarshal.GetReference(tmp);
        int groups = 1 << lm;
        int perGroup = n >> lm;
        float l1 = 0f;
        for (int i = 0; i < groups; i++)
        {
            float l2 = 0f;
            for (int j = 0; j < perGroup; j++)
            {
                float value = Unsafe.Add(ref values, (j << lm) + i);
                l2 += value * value;
            }

            l1 += MathF.Sqrt(l2);
        }

        l1 *= SqrtM1[lm];
        float bias = width switch
        {
            1 => 0.12f * lm,
            2 => 0.05f * lm,
            _ => 0.02f * lm,
        };
        return l1 + (bias * l1);
    }

    /// <summary>Writes the time/frequency resolution symbols (RFC 6716 Appendix A, celt/celt.c: <c>tf_encode</c>).</summary>
    internal static void TfEncode(
        ref RangeEncoder enc,
        int start,
        int end,
        bool isTransient,
        Span<int> tfResolution,
        int lm,
        int tfSelect,
        int frameBytes)
    {
        int budget = frameBytes * 8;
        int tell = enc.Tell();
        int logp = isTransient ? 2 : 4;
        bool reserve = lm > 0 && tell + logp + 1 <= budget;
        if (reserve)
        {
            budget--;
        }

        int current = 0;
        bool changed = false;
        for (int i = start; i < end; i++)
        {
            if (tell + logp <= budget)
            {
                enc.EncodeBitLogp(tfResolution[i] ^ current, logp);
                tell = enc.Tell();
                current = tfResolution[i];
                changed |= current != 0;
            }
            else
            {
                tfResolution[i] = current;
            }

            logp = isTransient ? 4 : 5;
        }

        ReadOnlySpan<sbyte> table = CeltTables.TfSelectTable;
        int transientIndex = isTransient ? 4 : 0;
        int changedIndex = changed ? 1 : 0;
        if (reserve
            && table[(lm * 8) + transientIndex + changedIndex]
                != table[(lm * 8) + transientIndex + 2 + changedIndex])
        {
            enc.EncodeBitLogp(tfSelect, 1);
        }
        else
        {
            tfSelect = 0;
        }

        for (int i = start; i < end; i++)
        {
            tfResolution[i] = table[(lm * 8) + transientIndex + (2 * tfSelect) + tfResolution[i]];
        }
    }

    /// <summary>Decides whether to spread the pulses (RFC 6716 Appendix A, celt/celt.c: spreading_decision).</summary>
    internal static int SpreadingDecision(
        ReadOnlySpan<short> eBands,
        ReadOnlySpan<float> spectrum,
        ref int average,
        int lastDecision,
        ref int hfAverage,
        ref int tapsetDecision,
        bool updateHf,
        int end,
        int m)
    {
        if (m * (eBands[end] - eBands[end - 1]) <= 8)
        {
            return 0;
        }

        int sum = 0;
        int bandCount = 0;
        int hfSum = 0;
        for (int i = 0; i < end; i++)
        {
            int bandSamples = m * (eBands[i + 1] - eBands[i]);
            if (bandSamples <= 8)
            {
                continue;
            }

            int t0 = 0;
            int t1 = 0;
            int t2 = 0;
            int offset = m * eBands[i];
            for (int j = 0; j < bandSamples; j++)
            {
                float value = spectrum[offset + j];
                float x2N = value * value * bandSamples;
                if (x2N < 0.25f)
                {
                    t0++;
                }

                if (x2N < 0.0625f)
                {
                    t1++;
                }

                if (x2N < 0.015625f)
                {
                    t2++;
                }
            }

            if (i > BandCount - 4)
            {
                hfSum += 32 * (t1 + t0) / bandSamples;
            }

            int tmp = ((2 * t2 >= bandSamples ? 1 : 0) + (2 * t1 >= bandSamples ? 1 : 0) + (2 * t0 >= bandSamples ? 1 : 0));
            sum += tmp * 256;
            bandCount++;
        }

        if (updateHf)
        {
            if (hfSum != 0)
            {
                hfSum /= 4 - BandCount + end;
            }

            hfAverage = (hfAverage + hfSum) >> 1;
            hfSum = hfAverage;
            if (tapsetDecision == 2)
            {
                hfSum += 4;
            }
            else if (tapsetDecision == 0)
            {
                hfSum -= 4;
            }

            tapsetDecision = hfSum > 22 ? 2 : (hfSum > 18 ? 1 : 0);
        }

        sum /= bandCount;
        sum = (sum + average) >> 1;
        average = sum;
        sum = ((3 * sum) + (((3 - lastDecision) << 7) + 64) + 2) >> 2;
        if (sum < 80)
        {
            return 3;
        }

        if (sum < 256)
        {
            return 2;
        }

        if (sum < 384)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>Chooses the allocation trim (RFC 6716 Appendix A, celt/celt.c: alloc_trim_analysis).</summary>
    internal static int AllocTrimAnalysis(ReadOnlySpan<float> bandLogE, int end)
    {
        float diff = 0f;
        for (int i = 0; i < end - 1; i++)
        {
            diff += bandLogE[i] * (2 + (2 * i) - BandCount);
        }

        diff /= 2 * (end - 1);

        int trim = 5;
        if (diff > 2f)
        {
            trim--;
        }

        if (diff > 8f)
        {
            trim--;
        }

        if (diff < -4f)
        {
            trim++;
        }

        if (diff < -10f)
        {
            trim++;
        }

        return Math.Clamp(trim, 0, 10);
    }

    /// <summary>Fills the per-band bit caps (RFC 6716 Appendix A, celt/celt.c: init_caps).</summary>
    internal static void InitCaps(ReadOnlySpan<short> eBands, int lm, int channels, Span<int> cap)
    {
        for (int i = 0; i < BandCount; i++)
        {
            int bandSamples = (eBands[i + 1] - eBands[i]) << lm;
            cap[i] = (CeltTables.CacheCaps[(BandCount * ((2 * lm) + channels - 1)) + i] + 64) * channels * bandSamples >> 2;
        }
    }

    /// <summary>The Haar transform used by the time/frequency analysis (RFC 6716 Appendix A, celt/bands.c: haar1).</summary>
    internal static void Haar1(Span<float> x, int n0, int stride)
    {
        n0 >>= 1;
        ref float values = ref MemoryMarshal.GetReference(x);
        for (int i = 0; i < stride; i++)
        {
            int even = i;
            int odd = stride + i;
            int advance = 2 * stride;
            for (int j = 0; j < n0; j++)
            {
                float first = 0.70710678f * Unsafe.Add(ref values, even);
                float second = 0.70710678f * Unsafe.Add(ref values, odd);
                Unsafe.Add(ref values, even) = first + second;
                Unsafe.Add(ref values, odd) = first - second;
                even += advance;
                odd += advance;
            }
        }
    }
}
