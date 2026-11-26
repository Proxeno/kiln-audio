namespace Kiln.Audio.Simd;

/// <summary>
/// The DCT-IV pre- and post-rotation stages of the CELT MDCT.
/// </summary>
/// <remarks>
/// <para>
/// The DCT-IV packing is <c>z[p] = (x[2p] + i*x[N-1-2p]) e^(-i*pi*(4p+1)/(4N))</c> for
/// <c>p &lt; N/2</c>, the size-<c>N/2</c> FFT, and the post-rotation <c>t = Z[k] e^(-i*pi*k/N)</c>
/// with <c>D[2k] = Re(t)</c> and <c>D[N-1-2k] = -Im(t)</c>. These stages work on separate real
/// and imaginary arrays (structure of arrays) so the FFT needs no interleave copy; both rotations
/// touch their operands at stride two, which is why they stay scalar here while the butterflies and
/// the window combine are vectorized.
/// </para>
/// <para>
/// The scalar and vector paths of the MDCT as a whole are bit-exact; the parity tests check it.
/// </para>
/// </remarks>
internal static class MdctTwiddleKernel
{
    /// <summary>Packs the DCT-IV pre-twiddled FFT input.</summary>
    /// <param name="input">The real input sequence, <c>count</c> long.</param>
    /// <param name="count">The DCT-IV length.</param>
    /// <param name="preCos">The pre-rotation cosines, <c>count/2</c> long.</param>
    /// <param name="preSin">The pre-rotation sines.</param>
    /// <param name="packReal">Receives the packed real parts.</param>
    /// <param name="packImaginary">Receives the packed imaginary parts.</param>
    public static void PreTwiddle(
        ReadOnlySpan<float> input,
        int count,
        ReadOnlySpan<float> preCos,
        ReadOnlySpan<float> preSin,
        Span<float> packReal,
        Span<float> packImaginary)
    {
        int quarter = count / 2;
        for (int p = 0; p < quarter; p++)
        {
            float a = input[2 * p];
            float b = input[count - 1 - (2 * p)];
            float cos = preCos[p];
            float sin = preSin[p];
            packReal[p] = (a * cos) + (b * sin);
            packImaginary[p] = (b * cos) - (a * sin);
        }
    }

    /// <summary>Post-rotates the FFT output and writes the DCT-IV result (and its reverse).</summary>
    /// <param name="spectrumReal">The FFT output real parts, <c>count/2</c> long.</param>
    /// <param name="spectrumImaginary">The FFT output imaginary parts.</param>
    /// <param name="count">The DCT-IV length.</param>
    /// <param name="postCos">The post-rotation cosines.</param>
    /// <param name="postSin">The post-rotation sines.</param>
    /// <param name="output">Receives <c>count</c> DCT-IV values.</param>
    /// <param name="outputReverse">If present, receives the DCT-IV values in reverse order.</param>
    public static void PostTwiddle(
        ReadOnlySpan<float> spectrumReal,
        ReadOnlySpan<float> spectrumImaginary,
        int count,
        ReadOnlySpan<float> postCos,
        ReadOnlySpan<float> postSin,
        Span<float> output,
        float[]? outputReverse)
    {
        int quarter = count / 2;
        int last = count - 1;
        for (int k = 0; k < quarter; k++)
        {
            float sr = spectrumReal[k];
            float si = spectrumImaginary[k];
            float cos = postCos[k];
            float sin = postSin[k];
            float real = (sr * cos) + (si * sin);
            float imaginary = (si * cos) - (sr * sin);
            output[2 * k] = real;
            output[last - (2 * k)] = -imaginary;
            if (outputReverse is not null)
            {
                outputReverse[last - (2 * k)] = real;
                outputReverse[2 * k] = -imaginary;
            }
        }
    }
}