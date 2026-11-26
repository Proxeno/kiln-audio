namespace Kiln.Audio.Celt;

/// <summary>
/// The low-overlap sine window of RFC 6716 §4.3.7.
/// </summary>
/// <remarks>
/// <para>
/// The rising half of the window is
/// <c>w(n) = sin(pi/2 * sin^2(pi*(n+0.5)/(2*overlap)))</c> for <c>0 &lt;= n &lt; overlap</c>.
/// It is power-complementary with its mirror: <c>w(n)^2 + w(overlap-1-n)^2 = 1</c>. The falling
/// half of a frame is the mirror of the rising half.
/// </para>
/// <para>
/// Values are computed in double precision and stored as <see cref="float"/>, because the codec
/// arithmetic in this scalar path is single precision.
/// </para>
/// </remarks>
internal static class CeltWindow
{
    /// <summary>The overlap used by the 48 kHz CELT modes (2.5 ms).</summary>
    public const int DefaultOverlap = 120;

    /// <summary>Computes the rising half of the window.</summary>
    /// <param name="overlap">The overlap length in samples.</param>
    /// <returns>An array of <paramref name="overlap"/> samples, <c>w(0) .. w(overlap-1)</c>.</returns>
    public static float[] Create(int overlap)
    {
        if (overlap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "The overlap must be positive.");
        }

        var window = new float[overlap];
        for (int n = 0; n < overlap; n++)
        {
            double argument = Math.PI * (n + 0.5) / (2.0 * overlap);
            double sine = Math.Sin(argument);
            window[n] = (float)Math.Sin(0.5 * Math.PI * sine * sine);
        }

        return window;
    }
}
