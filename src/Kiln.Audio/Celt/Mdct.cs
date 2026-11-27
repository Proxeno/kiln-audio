// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.
using Kiln.Audio.Simd;
using Kiln.Audio.Simd.Kernels;

namespace Kiln.Audio.Celt;

/// <summary>
/// The low-overlap MDCT/IMDCT used by the CELT layer (RFC 6716 §4.3.7).
/// </summary>
/// <remarks>
/// <para>
/// Let <c>N</c> be the number of MDCT coefficients and <c>O</c> the overlap (120 for the 48 kHz
/// modes). One frame consumes <c>N + O</c> time samples - the current frame plus <c>O</c> lookahead
/// samples - and produces <c>N</c> coefficients. The analysis transform is
/// </para>
/// <code>
/// X(k) = sum_{n=0}^{N+O-1} w(n) x(n) cos(pi/N * (n + 1/2 + N - O/2) * (k + 1/2)), 0 &lt;= k &lt; N
/// </code>
/// <para>
/// where <c>w</c> is unity over <c>[O, N)</c>, rises over <c>[0, O)</c> with
/// <see cref="CeltWindow"/> and falls over <c>[N, N+O)</c> as the mirror of the rising half. The
/// phase <c>N - O/2</c> is the low-overlap generalisation of the textbook MDCT phase <c>N/2</c>
/// (which is recovered for <c>O = N</c>); it is what makes the window power-complementary after
/// overlap-add. The inverse is the transpose, followed by multiplication by <c>w(n)</c>, and
/// overlap-add of consecutive frames (hop <c>N</c>) reconstructs the input with unity gain.
/// </para>
/// <para>
/// <b>PERF-3 structure (real-input MDCT with one complex FFT).</b> The transform is the direct
/// <c>2N</c>-point MDCT evaluated with a single size-<c>N/4</c> complex FFT instead of the two
/// size-<c>N/2</c> DCT-IV FFTs of the PERF-1 structure. The windowed input is folded around the
/// block centre into the structure-of-arrays complex sequence the FFT consumes (with the window
/// ramps applied inside the fold), each half-sample phase rotation is folded into a single
/// precomputed complex twiddle, and the post-rotation writes coefficient <c>2p</c> from the real
/// part and <c>N-1-2p</c> from the imaginary part. The inverse runs the mirrored path: a
/// pre-rotation, the same complex FFT with the conjugate twiddles, a post-rotation, the
/// de-shuffle and the windowed TDAC mirror. The structure follows the RFC 6716 Appendix A
/// reference (<c>celt/mdct.c</c>); the two forward/post rotations are combined into exact
/// single-precision twiddles rather than the reference's small-angle <c>sine</c> shortcut, so the
/// result matches the double-precision definition to float rounding.
/// </para>
/// <para>
/// Both directions use the shared mixed-radix <see cref="Fft"/>, whose combine stage has a scalar
/// reference path and a <c>Vector128</c> path. The pre/post rotations are elementwise
/// structure-of-arrays complex multiplies vectorised in <see cref="MdctKernels"/>.
/// </para>
/// <para>
/// Instances hold mutable scratch buffers and are therefore not thread-safe. Allocate one per
/// concurrent decoder. Execute methods allocate no managed memory.
/// </para>
/// </remarks>
internal sealed class Mdct
{
    private readonly int _coefficientCount;
    private readonly int _overlap;
    private readonly int _length;
    private readonly int _half;      // N/4: the complex FFT size.
    private readonly float[] _window;
    private readonly float[] _windowReversed;
    private readonly Fft _fft;

    // Combined pre/post rotations. theta = 2*pi*(p + 1/8)/N with N = 2*coefficientCount.
    private readonly float[] _rotCos;
    private readonly float[] _rotSin;

    // Scratch: the fold, the FFT output, and the de-shuffled inverse sequence.
    private readonly float[] _foldReal;
    private readonly float[] _foldImaginary;
    private readonly float[] _spectrumReal;
    private readonly float[] _spectrumImaginary;
    private readonly float[] _deshuffle;

    /// <summary>
    /// Initializes a transform for one CELT block size.
    /// </summary>
    /// <param name="coefficientCount">
    /// The number of MDCT coefficients: 120, 240, 480 or 960 at 48 kHz.
    /// </param>
    /// <param name="overlap">The overlap length; the CELT 48 kHz modes use 120 samples.</param>
    public Mdct(int coefficientCount, int overlap = CeltWindow.DefaultOverlap)
    {
        if (coefficientCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coefficientCount), "The coefficient count must be positive.");
        }

        if (overlap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "The overlap must be positive.");
        }

        if (coefficientCount < overlap)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlap), "The overlap cannot exceed the coefficient count.");
        }

        if ((coefficientCount & 3) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coefficientCount), "The coefficient count must be a multiple of four.");
        }

        if ((overlap & 3) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlap), "The overlap must be a multiple of four.");
        }

        _coefficientCount = coefficientCount;
        _overlap = overlap;
        _length = coefficientCount + overlap;
        _half = coefficientCount / 2;
        _fft = Fft.Get(_half);
        _window = CeltWindow.Create(overlap);
        _windowReversed = new float[overlap];
        for (int i = 0; i < overlap; i++)
        {
            _windowReversed[i] = _window[overlap - 1 - i];
        }

        int count = coefficientCount;
        int quarter = count / 2;
        double modulus = 2.0 * count;
        double sine = 2.0 * Math.PI * 0.125 / modulus;
        _rotCos = new float[quarter];
        _rotSin = new float[quarter];
        for (int p = 0; p < quarter; p++)
        {
            double theta = (2.0 * Math.PI * p / modulus) + sine;
            _rotCos[p] = (float)Math.Cos(theta);
            _rotSin[p] = (float)Math.Sin(theta);
        }

        _foldReal = new float[quarter];
        _foldImaginary = new float[quarter];
        _spectrumReal = new float[quarter];
        _spectrumImaginary = new float[quarter];
        _deshuffle = new float[count];
    }

    /// <summary>Gets the number of MDCT coefficients produced per block.</summary>
    public int CoefficientCount => _coefficientCount;

    /// <summary>Gets the overlap length in samples.</summary>
    public int Overlap => _overlap;

    /// <summary>Gets the number of time samples consumed by <see cref="Forward"/> and produced by <see cref="Inverse"/>.</summary>
    public int Length => _length;

    /// <summary>Computes the windowed low-overlap MDCT of one block.</summary>
    /// <param name="input">
    /// The current frame followed by <see cref="Overlap"/> lookahead samples; at least
    /// <see cref="Length"/> samples.
    /// </param>
    /// <param name="coefficients">
    /// Receives the coefficients. Coefficient <c>k</c> is written to index <c>k * stride</c>.
    /// </param>
    /// <param name="stride">
    /// Distance in samples between consecutive coefficients. Use 1 for a contiguous block, or the
    /// number of short blocks <c>B</c> to write the interleaved transient layout
    /// <c>X[k * B + b]</c> (advance the output span by the block index).
    /// </param>
    public void Forward(ReadOnlySpan<float> input, Span<float> coefficients, int stride = 1) =>
        ForwardCore(input, coefficients, stride, SimdConfig.UseSimd);

    /// <summary>Forward transform on the scalar path, used by the parity tests.</summary>
    /// <param name="input">The windowed input.</param>
    /// <param name="coefficients">Receives the coefficients.</param>
    /// <param name="stride">Distance in samples between consecutive coefficients.</param>
    internal void ForwardScalar(ReadOnlySpan<float> input, Span<float> coefficients, int stride = 1) =>
        ForwardCore(input, coefficients, stride, useVector: false);

    private void ForwardCore(ReadOnlySpan<float> input, Span<float> coefficients, int stride, bool useVector)
    {
        ValidateStride(stride);
        ValidateInput(input);
        long required = (((long)_coefficientCount - 1) * stride) + 1;
        if (coefficients.Length < required)
        {
            throw new ArgumentException(
                "The coefficient buffer is too short for the requested stride.", nameof(coefficients));
        }

        int count = _coefficientCount;
        int half = _half;
        int preHalf = _overlap / 2;
        int preQuarter = _overlap / 4;
        float[] window = _window;
        float[] foldReal = _foldReal;
        float[] foldImaginary = _foldImaginary;

        // Fold the windowed input around the block centre into N/4 complex values. The first and
        // last overlap/4 pairs carry the window ramps; the middle block is the un-windowed fold.
        int source1 = preHalf;
        int source2 = count - 1 + preHalf;
        int window1 = preHalf;
        int window2 = preHalf - 1;
        for (int i = 0; i < preQuarter; i++)
        {
            foldReal[i] =
                (window[window2] * input[source1 + count]) + (window[window1] * input[source2]);
            foldImaginary[i] =
                (window[window1] * input[source1]) - (window[window2] * input[source2 - count]);
            source1 += 2;
            source2 -= 2;
            window1 += 2;
            window2 -= 2;
        }

        window1 = 0;
        window2 = _overlap - 1;
        for (int i = preQuarter; i < half - preQuarter; i++)
        {
            foldReal[i] = input[source2];
            foldImaginary[i] = input[source1];
            source1 += 2;
            source2 -= 2;
        }

        for (int i = half - preQuarter; i < half; i++)
        {
            foldReal[i] =
                (-window[window1] * input[source1 - count]) + (window[window2] * input[source2]);
            foldImaginary[i] =
                (window[window2] * input[source1]) + (window[window1] * input[source2 + count]);
            source1 += 2;
            source2 -= 2;
            window1 += 2;
            window2 -= 2;
        }

        // Pre-rotation by -exp(-i*theta).
        MdctKernels.RotateForwardPre(foldReal, foldImaginary, _rotCos, _rotSin, half, useVector);

        _fft.Transform(
            foldReal, foldImaginary, _spectrumReal, _spectrumImaginary, inverse: false, useVector);

        // Post-rotation: coefficient 2p comes from the real part of conj(Z) exp(+i*theta), and
        // coefficient N-1-2p from the imaginary part.
        MdctKernels.WriteForwardPost(
            coefficients,
            _spectrumReal,
            _spectrumImaginary,
            _rotCos,
            _rotSin,
            count,
            stride,
            useVector);
    }

    /// <summary>Computes the windowed inverse MDCT of one block.</summary>
    /// <param name="coefficients">
    /// The coefficients of one block. Coefficient <c>k</c> is read from index <c>k * stride</c>.
    /// </param>
    /// <param name="output">
    /// Receives <see cref="Length"/> windowed samples. The first <see cref="Overlap"/> samples
    /// belong to the overlap with the previous frame; after adding the previous frame's saved tail
    /// the first <see cref="CoefficientCount"/> samples are the reconstructed output and the last
    /// <see cref="Overlap"/> samples are the tail saved for the next frame.
    /// </param>
    /// <param name="stride">
    /// Distance in samples between consecutive coefficients; see <see cref="Forward"/>.
    /// </param>
    public void Inverse(ReadOnlySpan<float> coefficients, Span<float> output, int stride = 1) =>
        InverseCore(coefficients, output, stride, SimdConfig.UseSimd);

    /// <summary>Inverse transform with the output left unscaled (the <c>2/N</c> factor omitted).</summary>
    /// <remarks>The CELT synthesizer multiplies by <c>N/2</c> straight after the inverse, so the two cancel.</remarks>
    internal void InverseUnscaled(ReadOnlySpan<float> coefficients, Span<float> output, int stride = 1) =>
        InverseCore(coefficients, output, stride, SimdConfig.UseSimd, scale: 1f);

    /// <summary>Inverse transform on the scalar path, used by the parity tests.</summary>
    /// <param name="coefficients">The coefficients of one block.</param>
    /// <param name="output">Receives the windowed samples.</param>
    /// <param name="stride">Distance in samples between consecutive coefficients.</param>
    internal void InverseScalar(ReadOnlySpan<float> coefficients, Span<float> output, int stride = 1) =>
        InverseCore(coefficients, output, stride, useVector: false);

    private void InverseCore(ReadOnlySpan<float> coefficients, Span<float> output, int stride, bool useVector, float scale = float.NaN)
    {
        ValidateStride(stride);
        long required = (((long)_coefficientCount - 1) * stride) + 1;
        if (coefficients.Length < required)
        {
            throw new ArgumentException(
                "The coefficient buffer is too short for the requested stride.", nameof(coefficients));
        }

        if (output.Length < _length)
        {
            throw new ArgumentException(
                $"The output must hold {_length} samples.", nameof(output));
        }

        int count = _coefficientCount;
        int half = _half;
        float[] foldReal = _foldReal;
        float[] foldImaginary = _foldImaginary;
        if (float.IsNaN(scale))
        {
            scale = 2f / count;
        }

        // Pre-rotation: pair coefficient 2p with coefficient N-1-2p. The two exact rotations of the
        // reference (phi and the half-sample correction) collapse into one linear map with the
        // same combined twiddles as the forward post-rotation.
        MdctKernels.RotateInversePre(coefficients, stride, foldReal, foldImaginary, _rotCos, _rotSin, count, half, useVector);

        _fft.Transform(
            foldReal, foldImaginary, _spectrumReal, _spectrumImaginary, inverse: true, useVector);

        // Post-rotation by exp(+i*theta), then the de-shuffle that interleaves the direct and
        // reversed spectra for the TDAC mirror.
        MdctKernels.RotateInversePost(_spectrumReal, _spectrumImaginary, _rotCos, _rotSin, half, useVector);

        float[] deshuffle = _deshuffle;
        MdctKernels.Deshuffle(_spectrumReal, _spectrumImaginary, deshuffle, half, useVector);

        MdctKernels.MirrorInverse(output, deshuffle, _window, scale, count, _overlap, useVector);
    }

    /// <summary>
    /// The unscaled inverse transform with the given kernel tier: pre-rotation, the FFT, the fused
    /// post-rotation and de-shuffle, and the TDAC mirror (block copy plus vectorized window ramps).
    /// </summary>
    /// <param name="coefficients">The spectrum, read with the given stride.</param>
    /// <param name="output">Receives <c>count + overlap</c> windowed samples.</param>
    /// <param name="stride">The coefficient stride (the interleave factor of short blocks).</param>
    internal void InverseUnscaled<TK>(ReadOnlySpan<float> coefficients, Span<float> output, int stride = 1)
        where TK : struct, ICeltKernels
    {
        ValidateStride(stride);
        long required = (((long)_coefficientCount - 1) * stride) + 1;
        if (coefficients.Length < required)
        {
            throw new ArgumentException(
                "The coefficient buffer is too short for the requested stride.", nameof(coefficients));
        }

        if (output.Length < _length)
        {
            throw new ArgumentException($"The output must hold {_length} samples.", nameof(output));
        }

        TK.ImdctPre(coefficients, stride, _foldReal, _foldImaginary, _rotCos, _rotSin, _coefficientCount, _half);
        _fft.Transform<TK>(_foldReal, _foldImaginary, _spectrumReal, _spectrumImaginary, inverse: true);
        TK.ImdctPostDeshuffle(_spectrumReal, _spectrumImaginary, _rotCos, _rotSin, _deshuffle, _half);
        TK.ImdctMirror(_deshuffle, _window, _windowReversed, output, _coefficientCount, _overlap);
    }

    private void ValidateStride(int stride)
    {
        if (stride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "The stride must be positive.");
        }
    }

    private void ValidateInput(ReadOnlySpan<float> input)
    {
        if (input.Length < _length)
        {
            throw new ArgumentException(
                $"The input must contain {_length} samples.", nameof(input));
        }
    }
}
