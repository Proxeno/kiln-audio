using Kiln.Audio.Celt;

namespace Kiln.Audio.Tests;

/// <summary>The RFC 6716 §4.3.7 low-overlap window.</summary>
public class MdctWindowTests
{
    [Fact]
    public void Default_overlap_is_120()
    {
        Assert.Equal(120, CeltWindow.DefaultOverlap);
    }

    [Fact]
    public void Rising_half_is_power_complementary_with_its_mirror()
    {
        const int Overlap = 120;
        float[] window = CeltWindow.Create(Overlap);

        for (int n = 0; n < Overlap; n++)
        {
            double sample = window[n];
            double mirror = window[Overlap - 1 - n];
            double sum = (sample * sample) + (mirror * mirror);
            Assert.True(
                Math.Abs(sum - 1.0) <= 1e-6,
                $"n={n}: w(n)^2 + w(overlap-1-n)^2 = {sum:R} is not 1.");
        }
    }

    [Fact]
    public void Values_match_the_closed_form()
    {
        const int Overlap = 120;
        float[] window = CeltWindow.Create(Overlap);

        for (int n = 0; n < Overlap; n++)
        {
            double inner = Math.Sin(Math.PI * (n + 0.5) / (2.0 * Overlap));
            double expected = Math.Sin(0.5 * Math.PI * inner * inner);
            Assert.Equal((float)expected, window[n]);
        }
    }

    [Fact]
    public void Window_rises_from_zero_to_one()
    {
        const int Overlap = 120;
        float[] window = CeltWindow.Create(Overlap);

        float previous = -1f;
        for (int n = 0; n < Overlap; n++)
        {
            Assert.True(window[n] > previous, $"n={n}: the window is not strictly rising.");
            Assert.InRange(window[n], 0f, 1f);
            previous = window[n];
        }
    }
}
