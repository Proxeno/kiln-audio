using Kiln.Audio.Celt;

namespace Kiln.Audio.Tests.Celt;

/// <summary>
/// Unit tests for the post-filter and de-emphasis stages of <see cref="CeltSynthesizer"/>.
/// </summary>
public class CeltSynthesizerTests
{
    private const int Overlap = 120;

    [Fact]
    public void Comb_filter_matches_the_reference_formula()
    {
        var synthesizer = new CeltSynthesizer(2);
        const int HistorySize = 2048;
        const int FrameStart = 1024;
        const int Count = 480;

        var history = new float[HistorySize];
        Fill(history, 1234);
        var expected = (float[])history.Clone();

        const int Period0 = 40;
        const int Period1 = 57;
        const float Gain0 = 0.5f;
        const float Gain1 = 0.28125f;
        const int TapSet0 = 0;
        const int TapSet1 = 2;

        ApplyReferenceComb(expected, FrameStart, Count, Period0, Period1, Gain0, Gain1, TapSet0, TapSet1);
        synthesizer.CombFilter(history, FrameStart, Count, Period0, Period1, Gain0, Gain1, TapSet0, TapSet1);

        for (int i = 0; i < Count; i++)
        {
            int position = FrameStart + i;
            Assert.Equal(expected[position], history[position], 5);
        }
    }

    [Fact]
    public void Comb_filter_cross_fade_adds_no_seam_when_parameters_match()
    {
        var synthesizer = new CeltSynthesizer(2);
        const int HistorySize = 2048;
        const int FrameStart = 1024;
        const int Count = 480;
        const int Period = 64;
        const float Gain = 0.4638671875f;
        const int TapSet = 1;

        var crossfade = new float[HistorySize];
        var steady = new float[HistorySize];
        Fill(crossfade, 99);
        Array.Copy(crossfade, steady, HistorySize);

        // Identical old and new parameters: the squared-window cross-fade is a convex
        // combination of the same filter, so it must equal the steady filter everywhere.
        synthesizer.CombFilter(crossfade, FrameStart, Count, Period, Period, Gain, Gain, TapSet, TapSet);
        ApplyReferenceSteady(steady, FrameStart, Count, Period, Gain, TapSet);

        for (int i = 0; i < Count; i++)
        {
            int position = FrameStart + i;
            Assert.Equal(steady[position], crossfade[position], 4);
        }
    }

    [Fact]
    public void Comb_filter_cross_fade_of_a_constant_input_is_piecewise_smooth()
    {
        var synthesizer = new CeltSynthesizer(2);
        const int HistorySize = 2048;
        const int FrameStart = 1024;
        const int Count = 480;

        var history = new float[HistorySize];
        Array.Fill(history, 1f);

        // For a constant input every delayed tap reads 1, so the cross-fade only moves
        // between two constants; the result must stay bounded by those constants and
        // change by at most their difference.
        synthesizer.CombFilter(history, FrameStart, Count, 40, 57, 0.5f, 0.28125f, 0, 2);

        float low = float.MaxValue;
        float high = float.MinValue;
        for (int i = 0; i < Count; i++)
        {
            low = Math.Min(low, history[FrameStart + i]);
            high = Math.Max(high, history[FrameStart + i]);
        }

        for (int i = 1; i < Count; i++)
        {
            float delta = Math.Abs(history[FrameStart + i] - history[FrameStart + i - 1]);
            Assert.True(delta <= (high - low) + 1e-6f, $"sample {i} jumped by {delta}.");
        }
    }

    [Fact]
    public void Deemphasis_state_carries_across_frames()
    {
        const float Alpha = CeltTables.DeemphasisCoefficient;
        float memory = 0f;

        // First "frame": seed the filter with one full-scale sample.
        float first = CeltSynthesizer.DeemphasiseSample(32768f, ref memory);
        Assert.Equal(32768f, first);
        Assert.Equal(Alpha * 32768f, memory);

        // Second "frame": silence. The stored memory must be emitted and then decayed.
        float expectedMemory = memory;
        for (int j = 0; j < 8; j++)
        {
            float value = CeltSynthesizer.DeemphasiseSample(0f, ref memory);
            Assert.Equal(expectedMemory, value);
            expectedMemory = Alpha * expectedMemory;
            Assert.Equal(expectedMemory, memory);
        }
    }

    [Fact]
    public void Deemphasis_is_a_first_order_inverse_of_preemphasis()
    {
        // De-emphasis y[n] = x[n] + alpha*y[n-1]; pre-emphasis x[n] = y[n] - alpha*y[n-1].
        float memory = 0f;
        float previous = 0f;
        var random = new Random(7);
        for (int n = 0; n < 200; n++)
        {
            float input = (float)((random.NextDouble() * 2) - 1);
            float output = CeltSynthesizer.DeemphasiseSample(input, ref memory);
            float recovered = output - (CeltTables.DeemphasisCoefficient * previous);
            Assert.Equal(input, recovered, 4);
            previous = output;
        }
    }

    private static void ApplyReferenceComb(
        float[] buffer,
        int frameStart,
        int count,
        int period0,
        int period1,
        float gain0,
        float gain1,
        int tapSet0,
        int tapSet1)
    {
        float[] taps0 = CeltTables.PostFilterGains.Slice(tapSet0 * 3, 3).ToArray();
        float[] taps1 = CeltTables.PostFilterGains.Slice(tapSet1 * 3, 3).ToArray();
        float g00 = gain0 * taps0[0];
        float g01 = gain0 * taps0[1];
        float g02 = gain0 * taps0[2];
        float g10 = gain1 * taps1[0];
        float g11 = gain1 * taps1[1];
        float g12 = gain1 * taps1[2];
        float[] window = CeltWindow.Create(Overlap);

        for (int i = 0; i < count; i++)
        {
            int position = frameStart + i;
            float value = buffer[position];
            if (i < Overlap)
            {
                float fade = window[i] * window[i];
                value += (1f - fade) * (
                    (g00 * buffer[position - period0])
                    + (g01 * (buffer[position - period0 - 1] + buffer[position - period0 + 1]))
                    + (g02 * (buffer[position - period0 - 2] + buffer[position - period0 + 2])));
                value += fade * (
                    (g10 * buffer[position - period1])
                    + (g11 * (buffer[position - period1 - 1] + buffer[position - period1 + 1]))
                    + (g12 * (buffer[position - period1 - 2] + buffer[position - period1 + 2])));
            }
            else
            {
                value +=
                    (g10 * buffer[position - period1])
                    + (g11 * (buffer[position - period1 - 1] + buffer[position - period1 + 1]))
                    + (g12 * (buffer[position - period1 - 2] + buffer[position - period1 + 2]));
            }

            buffer[position] = value;
        }
    }

    private static void ApplyReferenceSteady(float[] buffer, int frameStart, int count, int period, float gain, int tapSet)
    {
        float[] taps = CeltTables.PostFilterGains.Slice(tapSet * 3, 3).ToArray();
        float g0 = gain * taps[0];
        float g1 = gain * taps[1];
        float g2 = gain * taps[2];
        for (int i = 0; i < count; i++)
        {
            int position = frameStart + i;
            buffer[position] +=
                (g0 * buffer[position - period])
                + (g1 * (buffer[position - period - 1] + buffer[position - period + 1]))
                + (g2 * (buffer[position - period - 2] + buffer[position - period + 2]));
        }
    }

    private static void Fill(float[] buffer, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (float)((random.NextDouble() * 2) - 1);
        }
    }
}
