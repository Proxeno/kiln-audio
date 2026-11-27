using Kiln.Audio.Silk;

namespace Kiln.Audio.Simd.Kernels;

/// <summary>The scalar reference implementations of the SILK kernels (the Scalar tier and the vector tiers' tails).</summary>
internal static class SilkDecScalar
{
    /// <summary>The LPC analysis filter for outputs <paramref name="start"/> to <paramref name="end"/>.</summary>
    internal static void LpcAnalysisRange(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficientsQ12, int start, int end, int order)
    {
        for (int ix = start; ix < end; ix++)
        {
            int inputIndex = ix - 1;
            int prediction = SilkFixedPoint.Smulbb(input[inputIndex], coefficientsQ12[0]);
            for (int j = 1; j < order; j++)
            {
                prediction = unchecked(prediction + SilkFixedPoint.Smulbb(input[inputIndex - j], coefficientsQ12[j]));
            }

            int residual = unchecked(SilkFixedPoint.Lshift(input[inputIndex + 1], 12) - prediction);
            output[ix] = (short)SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(residual, 12));
        }
    }

    internal static void LpcAnalysis(
        Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> coefficientsQ12, int length, int order)
    {
        LpcAnalysisRange(output, input, coefficientsQ12, order, length, order);
        output[..order].Clear();
    }

    internal static void ScaleToQ15(Span<int> destination, ReadOnlySpan<short> source, int gain)
    {
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = SilkFixedPoint.Smulwb(gain, source[i]);
        }
    }

    internal static void ScaleWideInPlace(Span<int> values, int gain)
    {
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = SilkFixedPoint.Smulww(gain, values[i]);
        }
    }

    /// <summary>Mid/side to left/right for samples <paramref name="start"/> to <paramref name="end"/>.</summary>
    internal static void StereoUnmix(
        ReadOnlySpan<short> mid,
        ReadOnlySpan<short> side,
        int start,
        int end,
        int interpLength,
        int pred0PrevQ13,
        int pred1PrevQ13,
        int delta0Q13,
        int delta1Q13,
        int pred0Q13,
        int pred1Q13,
        Span<short> output)
    {
        for (int n = start; n < end; n++)
        {
            int p0 = n < interpLength ? unchecked(pred0PrevQ13 + ((n + 1) * delta0Q13)) : pred0Q13;
            int p1 = n < interpLength ? unchecked(pred1PrevQ13 + ((n + 1) * delta1Q13)) : pred1Q13;
            int sum = SilkFixedPoint.Lshift(
                SilkFixedPoint.Add(mid[n] + mid[n + 2], SilkFixedPoint.Lshift(mid[n + 1], 1)), 9);
            sum = SilkFixedPoint.Smlawb(SilkFixedPoint.Lshift(side[n + 1], 8), sum, p0);
            sum = SilkFixedPoint.Smlawb(sum, SilkFixedPoint.Lshift(mid[n + 1], 11), p1);
            int predicted = SilkFixedPoint.Sat16(SilkFixedPoint.RshiftRound(sum, 8));
            output[2 * n] = (short)SilkFixedPoint.Sat16(mid[n + 1] + predicted);
            output[(2 * n) + 1] = (short)SilkFixedPoint.Sat16(mid[n + 1] - predicted);
        }
    }

    internal static void Deinterleave(ReadOnlySpan<short> interleaved, Span<short> first, Span<short> second, int count)
    {
        for (int i = 0; i < count; i++)
        {
            first[i] = interleaved[2 * i];
            second[i] = interleaved[(2 * i) + 1];
        }
    }

    internal static void AverageShorts(ReadOnlySpan<short> a, ReadOnlySpan<short> b, Span<short> destination, int count)
    {
        for (int i = 0; i < count; i++)
        {
            destination[i] = (short)((a[i] + b[i]) >> 1);
        }
    }

    internal static void ShortsToFloat(ReadOnlySpan<short> source, Span<float> destination, int count, float scale)
    {
        for (int i = 0; i < count; i++)
        {
            destination[i] = source[i] * scale;
        }
    }

    internal static void InterleaveToFloat(
        ReadOnlySpan<short> left, ReadOnlySpan<short> right, Span<float> destination, int count, float scale)
    {
        for (int i = 0; i < count; i++)
        {
            destination[2 * i] = left[i] * scale;
            destination[(2 * i) + 1] = right[i] * scale;
        }
    }

    /// <summary>Validates the geometry of a polyphase block so the kernels may read without bounds checks.</summary>
    internal static void ValidateResample(
        ReadOnlySpan<float> history,
        ReadOnlySpan<float> bank,
        ReadOnlySpan<int> bankOffset,
        ReadOnlySpan<int> bankLength,
        int newest,
        int phase,
        int stepSamples,
        int stepPhase,
        int up,
        int count,
        Span<short> output)
    {
        if (count < 0 || output.Length < count || up <= 0 || phase < 0 || phase >= up
            || bankOffset.Length < up || bankLength.Length < up || stepSamples < 0 || stepPhase < 0 || stepPhase >= up)
        {
            throw new ArgumentException("The resampler block geometry is invalid.");
        }

        int longest = 0;
        for (int p = 0; p < up; p++)
        {
            if (bankOffset[p] < 0 || bankLength[p] < 0 || bankOffset[p] + bankLength[p] > bank.Length)
            {
                throw new ArgumentException("The resampler bank geometry is invalid.");
            }

            longest = Math.Max(longest, bankLength[p]);
        }

        if (count > 0)
        {
            long lastNewest = newest + ((long)(count - 1) * stepSamples) + (((long)phase + ((long)(count - 1) * stepPhase)) / up);
            if (newest - longest + 1 < 0 || lastNewest >= history.Length)
            {
                throw new ArgumentException("The resampler history is too short for the block.");
            }
        }
    }

    internal static void Resample(
        ReadOnlySpan<float> history,
        ReadOnlySpan<float> bank,
        ReadOnlySpan<int> bankOffset,
        ReadOnlySpan<int> bankLength,
        int newest,
        int phase,
        int stepSamples,
        int stepPhase,
        int up,
        int count,
        Span<short> output)
    {
        ValidateResample(history, bank, bankOffset, bankLength, newest, phase, stepSamples, stepPhase, up, count, output);
        for (int n = 0; n < count; n++)
        {
            int length = bankLength[phase];
            int first = newest - length + 1;
            int offset = bankOffset[phase];
            float sum = 0f;
            for (int t = 0; t < length; t++)
            {
                sum += history[first + t] * bank[offset + t];
            }

            float rounded = MathF.Round(sum);
            output[n] = rounded > short.MaxValue ? short.MaxValue : rounded < short.MinValue ? short.MinValue : (short)rounded;
            newest += stepSamples;
            phase += stepPhase;
            if (phase >= up)
            {
                phase -= up;
                newest++;
            }
        }
    }

    /// <summary>The phase-padded width of the transposed coefficient layout for an upsampling factor.</summary>
    internal static int UpPad(int up) => up == 6 ? 8 : 4;

    internal static void ValidateResampleUp(
        ReadOnlySpan<float> history, ReadOnlySpan<float> bankT, int up, int length, int newest, int inputs, Span<short> output)
    {
        if (up is not (3 or 4 or 6) || length <= 0 || (length & 3) != 0 || inputs < 0
            || bankT.Length < length * UpPad(up) || output.Length < inputs * up
            || (inputs > 0 && (newest - length + 1 < 0 || newest + inputs > history.Length)))
        {
            throw new ArgumentException("The upsampler block geometry is invalid.");
        }
    }

    /// <summary>The scalar reference of <see cref="ISilkKernels.ResampleUp"/>.</summary>
    internal static int ResampleUp(
        ReadOnlySpan<float> history, ReadOnlySpan<float> bankT, int up, int length, int newest, int inputs, Span<short> output)
    {
        ValidateResampleUp(history, bankT, up, length, newest, inputs, output);
        int pad = UpPad(up);
        int done = inputs & ~3;
        for (int j = 0; j < done; j++)
        {
            for (int p = 0; p < up; p++)
            {
                float sum = 0f;
                for (int k = 0; k < length; k++)
                {
                    sum += history[newest + j - length + 1 + k] * bankT[(k * pad) + p];
                }

                float rounded = MathF.Round(sum);
                output[(j * up) + p] = rounded > short.MaxValue ? short.MaxValue : rounded < short.MinValue ? short.MinValue : (short)rounded;
            }
        }

        return done;
    }
}
